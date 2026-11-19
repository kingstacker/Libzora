using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Translates EPUB XHTML while keeping the archive's images, stylesheets and
/// navigation files intact. Translation uses only the explicitly configured model API.
/// </summary>

public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private const int AiRequestCharacterLimit = 5_500;
    private const int MaxArchiveEntryBytes = 64 * 1024 * 1024;
    private const int MaxRetryCount = 2;
    private const int TranslationCacheVersion = 2;
    private const string TranslationCacheFileName = ".libzora-translation-cache.jsonl";
    private static readonly JsonSerializerOptions TranslationCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SegmentMarkerPattern = new(
        @"__LIBZORA_SEG_(?<id>\d{1,6})__",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> TranslatableBlockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "caption", "dd", "div", "dl", "dt",
        "figcaption", "figure", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "li", "main", "nav", "ol", "p", "pre", "section", "table", "tbody", "td", "tfoot",
        "th", "thead", "tr", "ul", "body"
    };

    private readonly AppPaths _paths;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly bool _ownsAiChatClient;
    private readonly AiRequestsPerMinuteLimiter _aiRequestsPerMinuteLimiter = new();
    private bool _disposed;

    public EpubTranslationService(
        AppPaths paths,
        ISecretProtector secretProtector,
        AiChatClient? aiChatClient = null,
        HttpMessageHandler? httpHandler = null)
    {
        _paths = paths;
        _aiSettingsStore = new AiSettingsStore(paths, secretProtector);
        _aiChatClient = aiChatClient ?? new AiChatClient(httpHandler);
        _ownsAiChatClient = aiChatClient is null;
        if (aiChatClient is not null) httpHandler?.Dispose();
    }

    public Task<BookTranslationResumeInfo?> FindResumeAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        CancellationToken cancellationToken = default) => Task.Run(
            () => FindResumeCoreAsync(epubPath, outputDirectory, settings, cancellationToken),
            cancellationToken);

    private async Task<BookTranslationResumeInfo?> FindResumeCoreAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourcePath = Path.GetFullPath(epubPath);
        if (!File.Exists(sourcePath)) return null;
        var normalizedSettings = BookTranslationSettings.Normalize(settings);
        if (!normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            && !normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
            return null;

        var cachePath = GetTranslationCachePath(outputDirectory);
        if (!File.Exists(cachePath)) return null;

        try
        {
            var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
            var snapshot = await TryLoadTranslationCacheAsync(
                cachePath,
                sourcePath,
                sourceHash,
                normalizedSettings,
                cancellationToken);
            if (snapshot is null) return null;

            var totalSegments = GetCachedTotalSegments(snapshot);
            var completedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == BookTranslationSegmentStatus.Completed
                && !string.IsNullOrWhiteSpace(item.TranslatedText));
            var failedSegments = snapshot.Segments.Values.Count(item =>
                item.Index >= 0
                && item.Index < totalSegments
                && item.Status == BookTranslationSegmentStatus.Failed);
            return new BookTranslationResumeInfo(
                snapshot.Header.Settings,
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
            // A truncated or old cache should never prevent a fresh
            // translation. Keep it on disk so the next fresh run can replace
            // it atomically when it starts.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to inspect translation cache: {exception.Message}");
            return null;
        }
    }

    public Task<BookTranslationResult> TranslateAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        BookTranslationResumeMode resumeMode = BookTranslationResumeMode.Restart) => Task.Run(
            () => TranslateCoreAsync(epubPath, outputDirectory, settings, progress, cancellationToken, resumeMode),
            cancellationToken);

    private async Task<BookTranslationResult> TranslateCoreAsync(
        string epubPath,
        string outputDirectory,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress,
        CancellationToken cancellationToken,
        BookTranslationResumeMode resumeMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sourcePath = Path.GetFullPath(epubPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("待翻译的 EPUB 文件不存在。", sourcePath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("书籍翻译目前只支持 EPUB 文件。 ");

        var normalizedSettings = BookTranslationSettings.Normalize(settings);
        var targetDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(targetDirectory);
        var outputPaths = new List<string>();

        Report(progress, new BookTranslationProgress(
            "正在准备",
            Path.GetFileName(sourcePath),
            0,
            0));

        if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Original))
        {
            var originalPath = CreateOutputPath(targetDirectory, sourcePath, "原文");
            File.Copy(sourcePath, originalPath, overwrite: true);
            outputPaths.Add(originalPath);
        }

        var needsTranslatedArchive = normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated)
            || normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual);
        if (!needsTranslatedArchive)
        {
            Report(progress, new BookTranslationProgress(
                "完成",
                "原文件已复制",
                0,
                0));
            return new BookTranslationResult(sourcePath, targetDirectory, outputPaths, 0, 0);
        }

        var sourceHash = await Hashing.Sha256Async(sourcePath, cancellationToken);
        var cachePath = GetTranslationCachePath(targetDirectory);
        var resumeSnapshot = resumeMode == BookTranslationResumeMode.Resume
            ? await TryLoadTranslationCacheAsync(
                cachePath,
                sourcePath,
                sourceHash,
                normalizedSettings,
                cancellationToken)
            : null;
        TranslationCacheWriter? cacheWriter = null;
        var translationCompleted = false;
        try
        {
            var pages = await ReadTranslationPagesAsync(sourcePath, progress, cancellationToken);
            var segments = pages
                .SelectMany(page => page.Segments)
                .Select((segment, index) => segment with { Index = index })
                .ToArray();
            var totalCharacters = segments.Sum(segment => (long)segment.Text.Length);
            cacheWriter = new TranslationCacheWriter(
                cachePath,
                new TranslationCacheHeader(
                    "header",
                    TranslationCacheVersion,
                    sourcePath,
                    sourceHash,
                    normalizedSettings,
                    segments.Length,
                    DateTimeOffset.UtcNow),
                append: resumeSnapshot is not null);
            Report(progress, new BookTranslationProgress(
                "正在翻译",
                $"共发现 {segments.Length} 个文本段",
                0,
                segments.Length,
                0,
                totalCharacters));

            var translations = await TranslateSegmentsAsync(
                segments,
                normalizedSettings,
                progress,
                totalCharacters,
                cancellationToken,
                resumeSnapshot?.Segments,
                cacheWriter,
                new TranslationRequestContext(normalizedSettings, resumeSnapshot?.Parts, resumeSnapshot?.SplitPlans, cacheWriter));

            var renderedPages = new Dictionary<BookTranslationOutputMode, IReadOnlyDictionary<string, byte[]>>();
            if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Translated))
            {
                renderedPages[BookTranslationOutputMode.Translated] = RenderPages(
                    pages,
                    translations,
                    BookTranslationOutputMode.Translated,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
            }

            if (normalizedSettings.OutputMode.HasFlag(BookTranslationOutputMode.Bilingual))
            {
                renderedPages[BookTranslationOutputMode.Bilingual] = RenderPages(
                    pages,
                    translations,
                    BookTranslationOutputMode.Bilingual,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
            }

            var modesToWrite = new[]
            {
                BookTranslationOutputMode.Translated,
                BookTranslationOutputMode.Bilingual
            };
            foreach (var mode in modesToWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!renderedPages.TryGetValue(mode, out var pageBytes)) continue;

                Report(progress, new BookTranslationProgress(
                    "正在生成 EPUB",
                    GetOutputModeLabel(mode),
                    segments.Length,
                    segments.Length,
                    totalCharacters,
                    totalCharacters));

                var suffix = mode == BookTranslationOutputMode.Bilingual ? "双语版" : "单译版";
                var destination = CreateOutputPath(targetDirectory, sourcePath, suffix, '_');
                var packageEntries = await RenderPackageEntriesAsync(
                    sourcePath,
                    mode,
                    normalizedSettings.TargetLanguage,
                    cancellationToken);
                await WriteArchiveAsync(
                    sourcePath,
                    destination,
                    pageBytes,
                    packageEntries,
                    cancellationToken);
                outputPaths.Add(destination);
            }

            Report(progress, new BookTranslationProgress(
                "完成",
                $"已生成 {outputPaths.Count} 个文件",
                segments.Length,
                segments.Length,
                totalCharacters,
                totalCharacters));
            translationCompleted = true;
            return new BookTranslationResult(
                sourcePath,
                targetDirectory,
                outputPaths,
                segments.Length,
                totalCharacters);
        }
        finally
        {
            cacheWriter?.Dispose();
            if (translationCompleted)
                TryDeleteTranslationCache(cachePath);
        }
    }
}
