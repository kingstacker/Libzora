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
/// Cache and page records, UTF-8 writer.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private sealed record TranslationCacheHeader(
        string Kind,
        int Version,
        string SourcePath,
        string SourceHash,
        BookTranslationSettings Settings,
        int TotalSegments,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSegmentLine(
        string Kind,
        int Index,
        string EntryName,
        string OriginalText,
        string RequestText,
        string TranslatedText,
        BookTranslationSegmentStatus Status,
        string ProcessFlow,
        string? ErrorMessage,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCachePartLine(
        string Kind,
        string SourceText,
        string TranslatedText,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSnapshot(
        TranslationCacheHeader Header,
        IReadOnlyDictionary<int, TranslationCacheSegmentLine> Segments,
        IReadOnlyDictionary<string, string> Parts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> SplitPlans,
        DateTimeOffset UpdatedAt);

    private sealed record TranslationCacheSplitLine(
        string Kind,
        string SourceText,
        IReadOnlyList<string> Parts,
        DateTimeOffset UpdatedAt);

    private sealed class TranslationCacheWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private bool _disposed;

        public TranslationCacheWriter(
            string path,
            TranslationCacheHeader header,
            bool append)
        {
            var stream = new FileStream(
                path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (append && stream.Length > 0)
                _writer.WriteLine();
            WriteHeader(header);
        }

        public void Append(BookTranslationSegmentProgress segment, string requestText, string translatedText)
        {
            if (_disposed) return;
            var line = new TranslationCacheSegmentLine(
                "segment",
                segment.Index,
                segment.EntryName,
                segment.OriginalText,
                requestText,
                translatedText,
                segment.Status,
                segment.ProcessFlow,
                segment.ErrorMessage,
                DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void AppendPart(string sourceText, string translatedText)
        {
            if (_disposed) return;
            var line = new TranslationCachePartLine("part", sourceText, translatedText, DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void AppendSplitPlan(string sourceText, IReadOnlyList<string> parts)
        {
            if (_disposed) return;
            var line = new TranslationCacheSplitLine("split", sourceText, parts, DateTimeOffset.UtcNow);
            _writer.WriteLine(JsonSerializer.Serialize(line, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        private void WriteHeader(TranslationCacheHeader header)
        {
            _writer.WriteLine(JsonSerializer.Serialize(header, TranslationCacheJsonOptions));
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed record TranslationPage(
        string EntryName,
        string OriginalMarkup,
        IReadOnlyList<TranslationSegment> Segments,
        bool IsNavigation,
        bool IsNcx);

    private sealed record TranslationSegment(
        string Key,
        string Text,
        XElement Element,
        EpubTranslationMarkup Markup,
        int Index = -1);

    private sealed class TranslationRequestContext(
        BookTranslationSettings settings,
        IReadOnlyDictionary<string, string>? parts,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? splitPlans,
        TranslationCacheWriter cacheWriter)
    {
        public BookTranslationSettings Settings { get; } = settings;
        public AiConnectionSettings? AiSettings { get; set; }
        public Dictionary<string, string> Parts { get; } = parts is null
            ? new(StringComparer.Ordinal)
            : new(parts, StringComparer.Ordinal);
        public TranslationCacheWriter CacheWriter { get; } = cacheWriter;
        public Dictionary<string, IReadOnlyList<string>> SplitPlans { get; } = splitPlans is null
            ? new(StringComparer.Ordinal)
            : new(splitPlans, StringComparer.Ordinal);
    }



    private sealed class Utf8StringWriter(
        StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
