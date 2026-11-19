using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Annotation bounds, ranges and signatures.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations.Clear();
        _annotations.AddRange(annotations);
        _annotationsVersion++;
        _annotationRanges.Clear();
        _annotationRangesContent = _content;
        _annotationRangesVersion = _annotationsVersion;
        _hoveredAnnotationId = null;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Returns the visible body bounds of an annotation in control coordinates.
    /// The reader uses the same overlay geometry for painting and hit testing,
    /// so the editor popup can stay anchored to the mark in every presentation
    /// mode (single page, spread, and continuous scroll).
    /// </summary>
    public IReadOnlyList<Rect> GetAnnotationBounds(ReaderAnnotation annotation)
    {
        if (_layout is null
            || !TryGetAnnotationRange(annotation, out var start, out var end)
            || end <= start)
            return [];

        var bounds = new List<Rect>();
        foreach (var pageIndex in VisiblePageIndexes())
        {
            var bands = _layout.GetOverlayRects(
                pageIndex,
                start,
                end - start);
            var origin = PageOrigin(pageIndex);
            foreach (var band in bands)
            {
                bounds.Add(new Rect(
                    origin.X + band.Left,
                    origin.Y + band.Top,
                    band.Width,
                    band.Height));
            }
        }

        return bounds;
    }

    /// <summary>
    /// Resolves an annotation against the current chapter text. EPUB
    /// annotations persist both an offset and a quote with surrounding
    /// context; when the content has been reparsed or edited, the stored
    /// offset may no longer identify the same passage. Prefer the exact
    /// stored range, then a trimmed quote, then the quoted context, and
    /// finally the nearest occurrence scored by the saved prefix/suffix.
    /// </summary>
    public bool TryGetAnnotationRange(
        ReaderAnnotation annotation,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        var content = _content;
        if (content is null
            || string.IsNullOrEmpty(content.BodyText)
            || annotation is null)
        {
            return false;
        }

        if (!ReferenceEquals(_annotationRangesContent, content)
            || _annotationRangesVersion != _annotationsVersion)
        {
            _annotationRanges.Clear();
            _annotationRangesContent = content;
            _annotationRangesVersion = _annotationsVersion;
        }

        var signature = AnnotationSignature(annotation);
        if (_annotationRanges.TryGetValue(annotation.Id, out var cached)
            && string.Equals(cached.Signature, signature, StringComparison.Ordinal))
        {
            start = cached.Start;
            end = cached.End;
            return end > start;
        }

        if (!TryResolveAnnotationRange(
                content.BodyText,
                annotation,
                out start,
                out end)
            || end <= start)
        {
            return false;
        }

        _annotationRanges[annotation.Id] = (start, end, signature);
        return true;
    }

    private static bool TryResolveAnnotationRange(
        string body,
        ReaderAnnotation annotation,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        if (body.Length == 0) return false;

        var originalStart = Math.Clamp(annotation.StartOffset, 0, body.Length);
        var originalEnd = Math.Clamp(annotation.EndOffset, originalStart, body.Length);
        var quote = annotation.SelectedText ?? string.Empty;
        var leadingWhitespace = 0;
        while (leadingWhitespace < quote.Length
            && char.IsWhiteSpace(quote[leadingWhitespace]))
        {
            leadingWhitespace++;
        }

        var quoteEnd = quote.Length;
        while (quoteEnd > leadingWhitespace
            && char.IsWhiteSpace(quote[quoteEnd - 1]))
        {
            quoteEnd--;
        }

        var trimmedQuote = quote[leadingWhitespace..quoteEnd];

        if (originalEnd > originalStart)
        {
            var originalText = body[originalStart..originalEnd];
            if (string.Equals(
                    originalText,
                    quote,
                    StringComparison.OrdinalIgnoreCase))
            {
                start = originalStart;
                end = originalEnd;
                return true;
            }

            if (trimmedQuote.Length > 0)
            {
                var originalLeading = 0;
                while (originalLeading < originalText.Length
                    && char.IsWhiteSpace(originalText[originalLeading]))
                {
                    originalLeading++;
                }

                var originalTrailing = originalText.Length;
                while (originalTrailing > originalLeading
                    && char.IsWhiteSpace(originalText[originalTrailing - 1]))
                {
                    originalTrailing--;
                }

                if (string.Equals(
                        originalText[originalLeading..originalTrailing],
                        trimmedQuote,
                        StringComparison.OrdinalIgnoreCase))
                {
                    start = originalStart + originalLeading;
                    end = originalStart + originalTrailing;
                    return true;
                }
            }
        }

        if (trimmedQuote.Length == 0)
        {
            if (originalEnd > originalStart)
            {
                start = originalStart;
                end = originalEnd;
                return true;
            }

            return false;
        }

        var prefix = annotation.Prefix ?? string.Empty;
        if (prefix.Length > 72) prefix = prefix[^72..];
        var suffix = annotation.Suffix ?? string.Empty;
        if (suffix.Length > 72) suffix = suffix[..72];

        var context = prefix + trimmedQuote + suffix;
        var contextAt = body.IndexOf(context, StringComparison.OrdinalIgnoreCase);
        if (contextAt >= 0)
        {
            var candidateStart = contextAt + prefix.Length;
            if (MatchesAt(body, candidateStart, trimmedQuote))
            {
                start = candidateStart;
                end = candidateStart + trimmedQuote.Length;
                return true;
            }
        }

        var bestAt = -1;
        var bestScore = int.MinValue;
        var bestDistance = int.MaxValue;
        var searchAt = 0;
        while (searchAt <= body.Length - trimmedQuote.Length)
        {
            var at = body.IndexOf(
                trimmedQuote,
                searchAt,
                StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;

            var beforeStart = Math.Max(0, at - 72);
            var before = body[beforeStart..at];
            var afterEnd = Math.Min(body.Length, at + trimmedQuote.Length + 72);
            var after = body[(at + trimmedQuote.Length)..afterEnd];
            var score = CommonSuffixLength(before, prefix)
                + CommonPrefixLength(after, suffix);
            var distance = Math.Abs(at - originalStart);
            if (score > bestScore
                || score == bestScore && distance < bestDistance)
            {
                bestAt = at;
                bestScore = score;
                bestDistance = distance;
            }

            searchAt = at + Math.Max(1, trimmedQuote.Length);
        }

        if (bestAt >= 0)
        {
            start = bestAt;
            end = bestAt + trimmedQuote.Length;
            return true;
        }

        if (originalEnd > originalStart)
        {
            start = originalStart;
            end = originalEnd;
            return true;
        }

        return false;
    }

    private static string AnnotationSignature(ReaderAnnotation annotation) =>
        string.Join(
            "\u001f",
            annotation.StartOffset.ToString(CultureInfo.InvariantCulture),
            annotation.EndOffset.ToString(CultureInfo.InvariantCulture),
            annotation.SelectedText ?? string.Empty,
            annotation.Prefix ?? string.Empty,
            annotation.Suffix ?? string.Empty);

    private static bool MatchesAt(string body, int at, string value) =>
        at >= 0
        && at + value.Length <= body.Length
        && body.AsSpan(at, value.Length).Equals(
            value.AsSpan(),
            StringComparison.OrdinalIgnoreCase);

    private static int CommonPrefixLength(string left, string right)
    {
        var count = 0;
        while (count < left.Length
            && count < right.Length
            && string.Compare(
                left,
                count,
                right,
                count,
                1,
                StringComparison.OrdinalIgnoreCase) == 0)
        {
            count++;
        }

        return count;
    }
}
