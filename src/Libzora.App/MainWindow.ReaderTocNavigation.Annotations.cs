using System.Text.Json;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Pending annotations and saved annotation application.
/// </summary>
public partial class MainWindow
{
    private async Task ScrollToPendingReaderAnnotationAsync(
        IReaderHost host,
        ReaderAnnotation annotation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is IReaderLocationHost nativeReader)
        {
            var startOffset = nativeReader.TryGetAnnotationRange(
                annotation,
                out var relocatedStart,
                out _)
                ? relocatedStart
                : Math.Max(0, annotation.StartOffset);
            nativeReader.ScrollToOffset(startOffset);
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private async Task ApplySavedAnnotationsAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (host is IPdfAnnotationsHost) { await ApplySavedReaderPdfAnnotationsAsync(cancellationToken); return; }
        if (_readerBookFile is null || _readerDocument is null) return;
        var chapterPath = GetReaderChapterPath(host);
        if (chapterPath is null) return;

        if (host is IReaderAnnotationHost nativeReader)
        {
            // Resolve saved quote/context at paint and navigation time. The
            // native loader normally reproduces the same stream, but edited or
            // reparsed EPUB content can move a previously valid offset.
            var nativeAnnotations = await _readerData.GetAnnotationsAsync(
                _readerBookFile.Id,
                cancellationToken,
                MaxReaderAnnotations);
            nativeReader.SetAnnotations(nativeAnnotations
                .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
                .ToList());
            return;
        }

        var annotations = await _readerData.GetAnnotationsAsync(
            _readerBookFile.Id,
            cancellationToken,
            MaxReaderAnnotations);
        var marks = annotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.SelectedText))
            .Select(item => new
            {
                Id = item.Id.ToString("N"),
                StartOffset = item.StartOffset,
                EndOffset = item.EndOffset,
                Quote = item.SelectedText.Trim(),
                Prefix = item.Prefix,
                Suffix = item.Suffix,
                Note = item.Note,
                Color = NormalizeReaderAnnotationColor(item.Color),
                Style = NormalizeReaderAnnotationStyle(item.UnderlineStyle)
            })
            .OrderByDescending(item => item.StartOffset)
            .ToArray();

        var serialized = JsonSerializer.Serialize(marks);
        var script = $$"""
            (() => {
              const annotations = {{serialized}};
              const commonSuffixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[left.length - 1 - length] === right[right.length - 1 - length]) length++;
                return length;
              };
              const commonPrefixLength = (left, right) => {
                let length = 0;
                const max = Math.min(left.length, right.length);
                while (length < max && left[length] === right[length]) length++;
                return length;
              };
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                parent.normalize?.();
              };
              for (const oldMark of Array.from(document.querySelectorAll('.libzora-saved-annotation'))) {
                unwrap(oldMark);
              }
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#libzora-selection-bar, .libzora-wave-sweep');
              };
              const collectNodes = () => {
                const nodes = [];
                const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                  if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
                }
                return nodes;
              };
              const segmentsFor = (nodes, start, end) => {
                const segments = [];
                let cursor = 0;
                for (const node of nodes) {
                  const length = (node.data || '').length;
                  const nodeStart = cursor;
                  const nodeEnd = cursor + length;
                  const from = Math.max(start, nodeStart);
                  const to = Math.min(end, nodeEnd);
                  if (to > from) {
                    segments.push({
                      node,
                      start: from - nodeStart,
                      end: to - nodeStart
                    });
                  }
                  cursor = nodeEnd;
                  if (cursor >= end) break;
                }
                return segments;
              };
              const styleMark = (mark, annotation) => {
                mark.className = 'libzora-saved-annotation';
                if (annotation.Id) mark.setAttribute('data-libzora-annotation', annotation.Id);
                const color = /^#[0-9a-f]{6}$/i.test(annotation.Color || '') ? annotation.Color : '#000000';
                const style = ['solid', 'double', 'dashed', 'dotted', 'wavy', 'marker'].includes(annotation.Style)
                  ? annotation.Style
                  : 'solid';
                const marker = style === 'marker';
                const invertedMarker = marker && color === '#000000';
                mark.style.setProperty('background-color', marker ? (invertedMarker ? color : color + '50') : 'transparent', 'important');
                if (marker) {
                  mark.style.setProperty('color', invertedMarker ? '#FFFFFF' : 'inherit', 'important');
                  mark.style.setProperty('text-decoration-line', 'none', 'important');
                } else {
                  mark.style.setProperty('text-decoration-line', 'underline', 'important');
                  mark.style.setProperty('text-decoration-color', color, 'important');
                  mark.style.setProperty('text-decoration-style', style, 'important');
                  mark.style.setProperty('text-decoration-thickness', '2px', 'important');
                  mark.style.setProperty('text-underline-offset', '3px', 'important');
                  mark.style.setProperty('text-decoration-skip-ink', 'none', 'important');
                }
                mark.style.setProperty('display', 'inline', 'important');
                mark.style.cursor = 'pointer';
                if (annotation.Note) mark.title = annotation.Note;
              };
              const wrapSegments = (segments, annotation) => {
                for (let index = segments.length - 1; index >= 0; index--) {
                  const segment = segments[index];
                  if (!segment.node.parentNode) return false;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('span');
                  styleMark(mark, annotation);
                  try {
                    range.surroundContents(mark);
                  } catch (_) {
                    return false;
                  }
                }
                return true;
              };
              for (const annotation of annotations) {
                const quote = annotation.Quote || '';
                if (!quote) continue;
                // Search the logical text stream, not individual text nodes.
                // EPUB markup commonly splits one visible sentence across
                // spans, emphasis tags, and inline links.
                const nodes = collectNodes();
                const text = nodes.map(node => node.data || '').join('');
                const foldedText = text.toLocaleLowerCase();
                const foldedQuote = quote.toLocaleLowerCase();
                const prefix = (annotation.Prefix || '').slice(-72).toLocaleLowerCase();
                const suffix = (annotation.Suffix || '').slice(0, 72).toLocaleLowerCase();
                let bestAt = -1;
                let bestScore = -1;
                const context = prefix + foldedQuote + suffix;
                const contextAt = context.length > foldedQuote.length
                  ? foldedText.indexOf(context)
                  : -1;
                if (contextAt >= 0) bestAt = contextAt + prefix.length;
                let at = bestAt >= 0 ? -1 : foldedText.indexOf(foldedQuote);
                while (at >= 0) {
                  const before = text.slice(Math.max(0, at - 72), at).toLocaleLowerCase();
                  const after = text.slice(at + quote.length, at + quote.length + 72).toLocaleLowerCase();
                  const score = commonSuffixLength(before, prefix) + commonPrefixLength(after, suffix);
                  if (score > bestScore) {
                    bestScore = score;
                    bestAt = at;
                  }
                  at = foldedText.indexOf(foldedQuote, at + Math.max(1, foldedQuote.length));
                }
                if (bestAt < 0) {
                  const storedStart = Math.max(0, Number(annotation.StartOffset) || 0);
                  const storedEnd = Math.max(storedStart, Number(annotation.EndOffset) || storedStart);
                  if (storedEnd > storedStart && storedEnd <= text.length) {
                    bestAt = storedStart;
                  }
                }
                if (bestAt < 0) continue;
                const length = quote.length > 0
                  ? quote.length
                  : Math.max(0, (Number(annotation.EndOffset) || 0) - (Number(annotation.StartOffset) || 0));
                wrapSegments(segmentsFor(nodes, bestAt, bestAt + length), annotation);
              }
              return true;
            })();
            """;
        await host.InvokeScriptAsync(script);
    }
}
