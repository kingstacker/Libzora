using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Creates a new EPUB whose Chinese text is annotated with HTML ruby markup.
/// The source archive is copied entry-for-entry, so images, stylesheets,
/// navigation and metadata remain part of the generated book.
/// </summary>

public sealed partial class PinyinBookService : IPinyinBookService
{
    private const long MaxArchiveEntryBytes = 128L * 1024 * 1024;
    private const int MaxAiReviewContextCharacters = 120;
    private const int PinyinCacheVersion = 2;
    private const string PinyinCacheFileSuffix = ".libzora-pinyin-cache.jsonl";

    private static readonly JsonSerializerOptions PinyinCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<char, (char Base, int Tone)> ToneMarks =
        new Dictionary<char, (char Base, int Tone)>
        {
            ['ā'] = ('a', 1), ['á'] = ('a', 2), ['ǎ'] = ('a', 3), ['à'] = ('a', 4),
            ['ē'] = ('e', 1), ['é'] = ('e', 2), ['ě'] = ('e', 3), ['è'] = ('e', 4),
            ['ī'] = ('i', 1), ['í'] = ('i', 2), ['ǐ'] = ('i', 3), ['ì'] = ('i', 4),
            ['ō'] = ('o', 1), ['ó'] = ('o', 2), ['ǒ'] = ('o', 3), ['ò'] = ('o', 4),
            ['ū'] = ('u', 1), ['ú'] = ('u', 2), ['ǔ'] = ('u', 3), ['ù'] = ('u', 4),
            ['ǖ'] = ('ü', 1), ['ǘ'] = ('ü', 2), ['ǚ'] = ('ü', 3), ['ǜ'] = ('ü', 4)
        };

    private static readonly IReadOnlyDictionary<(char Base, int Tone), char> ToneMarkByBaseAndTone =
        ToneMarks.ToDictionary(pair => pair.Value, pair => pair.Key);

    private static readonly HashSet<string> SkippedElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "script", "style", "noscript", "svg", "math", "title",
        "pre", "code", "ruby", "rt", "rp"
    };

    private static readonly HashSet<string> BlockElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "li", "main", "nav", "ol", "p", "section", "table", "tbody", "td",
        "tfoot", "th", "thead", "tr", "ul"
    };

    private readonly IBookFormatConverter? _formatConverter;
    private readonly PinyinPhraseDictionary _phraseDictionary;
    private readonly IPinyinEngine _pinyinEngine;
    private readonly AiChatClient? _aiChatClient;
    private readonly AiConnectionSettings? _aiSettings;

    public PinyinBookService(
        IBookFormatConverter? formatConverter = null,
        AiChatClient? aiChatClient = null,
        AiConnectionSettings? aiSettings = null,
        IPinyinEngine? pinyinEngine = null)
    {
        _formatConverter = formatConverter;
        _phraseDictionary = PinyinPhraseDictionary.LoadEmbedded();
        _pinyinEngine = pinyinEngine ?? new DotNetG2PPinyinEngine();
        _aiChatClient = aiChatClient;
        _aiSettings = aiSettings?.Clone();
    }

    public async Task<PinyinBookResumeInfo?> FindResumeAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source)
            || !Path.GetExtension(destination).Equals(".epub", StringComparison.OrdinalIgnoreCase))
            return null;

        var cachePath = GetPinyinCachePath(destination);
        if (!File.Exists(cachePath)) return null;

        try
        {
            var sourceHash = await Hashing.Sha256Async(source, cancellationToken);
            var snapshot = await TryLoadPinyinCacheAsync(
                cachePath,
                source,
                sourceHash,
                PinyinBookOptions.Normalize(options),
                cancellationToken);
            if (snapshot is null) return null;

            var totalSegments = GetCachedTotalSegments(snapshot);
            if (totalSegments <= 0) return null;
            var completedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == PinyinBookSegmentStatus.Completed
                && !string.IsNullOrWhiteSpace(item.AnnotatedMarkup)
                && item.ReviewTargets is not null
                && (!options.EnableAiReview || item.ReviewTargets.All(target => target.Reviewed)));
            var failedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == PinyinBookSegmentStatus.Failed);
            return new PinyinBookResumeInfo(
                snapshot.Header.Options,
                completedSegments,
                failedSegments,
                totalSegments,
                snapshot.UpdatedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to inspect pinyin cache: {exception.Message}");
            return null;
        }
    }

    public async Task<PinyinBookResult> GenerateAsync(
        string sourcePath,
        string destinationPath,
        PinyinBookOptions? options = null,
        IProgress<PinyinBookProgress>? progress = null,
        CancellationToken cancellationToken = default,
        PinyinBookResumeMode resumeMode = PinyinBookResumeMode.Restart)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("待处理的书籍文件不存在。", source);
        if (!string.Equals(Path.GetExtension(destination), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("拼音版目前输出为 EPUB 格式。 ");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拼音版必须输出为新文件，不能覆盖原书。 ");

        var normalizedOptions = PinyinBookOptions.Normalize(options);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var cachePath = GetPinyinCachePath(destination);
        if (File.Exists(destination))
        {
            if (!File.Exists(cachePath))
                throw new IOException("拼音版输出文件已经存在，请换一个文件名。 ");

            // The output is managed together with the resume cache. A previous
            // run may have written the archive before AI review failed; rebuild
            // that managed file from the source when the user retries.
            File.Delete(destination);
        }

        var sourceHash = await Hashing.Sha256Async(source, cancellationToken);

        string? temporaryDirectory = null;
        var epubSource = source;
        try
        {
            if (!string.Equals(Path.GetExtension(source), ".epub", StringComparison.OrdinalIgnoreCase))
            {
                if (_formatConverter is null)
                    throw new NotSupportedException("处理非 EPUB 书籍需要先配置 Calibre。 ");

                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "Libzora",
                    "pinyin",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                epubSource = Path.Combine(temporaryDirectory, "source.epub");
                Report(progress, new PinyinBookProgress(
                    "正在准备",
                    Path.GetFileName(source),
                    0,
                    0,
                    0));

                var conversionProgress = progress is null
                    ? null
                    : new Progress<FormatConversionProgress>(value => Report(
                        progress,
                        new PinyinBookProgress(
                            "正在准备",
                            value.Message,
                            0,
                            0,
                            0)));
                await _formatConverter.ConvertAsync(
                    source,
                    epubSource,
                    conversionProgress,
                    cancellationToken);
            }

            return await Task.Run(() => GenerateFromEpubAsync(
                epubSource,
                destination,
                normalizedOptions,
                progress,
                cancellationToken,
                source,
                sourceHash,
                resumeMode), cancellationToken);
        }
        finally
        {
            if (temporaryDirectory is not null)
                TryDeleteDirectory(temporaryDirectory);
        }
    }
}
