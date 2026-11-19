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
/// Annotation, review and cache records.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private sealed record LocalPinyinDecision(
        string Pinyin,
        IReadOnlyList<string> Candidates,
        bool FromPhraseDictionary);

    private sealed record PinyinReviewTarget(
        int SegmentIndex,
        int CharacterIndex,
        XElement Ruby,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context,
        int ContextCharacterIndex)
    {
        public bool Reviewed { get; set; }
    }

    private sealed record PinyinReviewRequest(
        int Id,
        char Character,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        string Context,
        int ContextCharacterIndex,
        IReadOnlyList<PinyinReviewTarget> Targets);

    private sealed record PinyinReviewContext(string Text, int CharacterIndex);

    private sealed class PinyinTextContext(string text)
    {
        public string Text { get; } = text;
        public PinyinContextAnalysis? Analysis { get; set; }
    }

    private sealed record PinyinContextAnalysis(string[] Pinyins, LocalPinyinDecision?[] Decisions);

    private sealed record PinyinAnnotationResult(
        IReadOnlyList<XNode> Nodes,
        int AnnotatedCharacters,
        IReadOnlyList<PinyinReviewTarget> Targets,
        string AnnotatedText);

    private sealed record PinyinPage(
        string EntryName,
        XDocument Document,
        IReadOnlyList<PinyinSegment> Segments);

    private sealed record PinyinSegment(
        int Index,
        string EntryName,
        XText TextNode,
        string OriginalText,
        PinyinTextContext Context,
        int ContextOffset);

    private sealed record PinyinWorkState(
        PinyinSegment Segment,
        int PageIndex,
        PinyinAnnotationResult Annotation)
    {
        public PinyinBookSegmentStatus Status { get; set; }
    }

    private sealed record PinyinCacheReviewTarget(
        int CharacterIndex,
        string CurrentPinyin,
        IReadOnlyList<string> Candidates,
        bool Reviewed);

    private sealed record PinyinCacheHeader(
        string Kind,
        int Version,
        string SourcePath,
        string SourceHash,
        PinyinBookOptions Options,
        int TotalSegments,
        DateTimeOffset UpdatedAt);

    private sealed record PinyinCacheSegmentLine(
        string Kind,
        int Index,
        string EntryName,
        string OriginalText,
        string AnnotatedText,
        string AnnotatedMarkup,
        int AnnotatedCharacterCount,
        int ReviewCandidateCount,
        int ReviewedCandidateCount,
        PinyinBookSegmentStatus Status,
        string ProcessFlow,
        string? ErrorMessage,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<PinyinCacheReviewTarget>? ReviewTargets = null);

    private sealed record PinyinCacheSnapshot(
        PinyinCacheHeader Header,
        IReadOnlyDictionary<int, PinyinCacheSegmentLine> Segments,
        DateTimeOffset UpdatedAt);

    private sealed class PinyinCacheWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private bool _disposed;

        public PinyinCacheWriter(
            string path,
            PinyinCacheHeader header,
            bool append)
        {
            var stream = new FileStream(
                path,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (append && stream.Length > 0)
                _writer.WriteLine();
            WriteHeader(header);
        }

        public void Append(
            PinyinBookSegmentProgress segment,
            string annotatedMarkup,
            int annotatedCharacterCount,
            int reviewCandidateCount,
            int reviewedCandidateCount,
            IReadOnlyList<PinyinReviewTarget> targets)
        {
            if (_disposed) return;
            var line = new PinyinCacheSegmentLine(
                "segment",
                segment.Index,
                segment.EntryName,
                segment.OriginalText,
                segment.AnnotatedText,
                annotatedMarkup,
                annotatedCharacterCount,
                reviewCandidateCount,
                reviewedCandidateCount,
                segment.Status,
                segment.ProcessFlow,
                segment.ErrorMessage,
                DateTimeOffset.UtcNow,
                targets.Select(target => new PinyinCacheReviewTarget(
                    target.CharacterIndex,
                    target.CurrentPinyin,
                    target.Candidates,
                    target.Reviewed)).ToArray());
            _writer.WriteLine(JsonSerializer.Serialize(line, PinyinCacheJsonOptions));
            _writer.Flush();
        }

        private void WriteHeader(PinyinCacheHeader header)
        {
            _writer.WriteLine(JsonSerializer.Serialize(header, PinyinCacheJsonOptions));
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
}
