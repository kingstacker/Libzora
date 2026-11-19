using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// In-page search and search navigation.
/// </summary>
public partial class MainWindow
{
    private async Task ApplyReaderSearchAsync(string query, int sequence, bool navigate = true)
    {
        if (_readerDocument is null || CurrentReaderHost is not { } host) return;
        if (sequence != _readerSearchSequence) return;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
            if (sequence != _readerSearchSequence) return;
            if (CurrentReaderHost is IReaderTextSearchHost nativeReader)
            {
                // Case-folded search over the same body-text stream the
                // WebKit walker saw; highlights paint per page.
                var nativeQuery = query.Trim();
                var nativeHits = nativeReader
                    .FindSearchMatches(nativeQuery)
                    .Select(match => (match.Start, match.Length))
                    .ToList();

                nativeReader.SetSearchHighlights(nativeHits, null);
                _readerSearchCount = nativeHits.Count;
                _readerSearchIndex = _readerSearchCount > 0
                    ? navigate
                        ? 0
                        : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
                    : -1;
                if (navigate && _readerSearchCount > 0)
                    await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
                else
                    UpdateReaderSearchCount();
                return;
            }
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.libzora-page-find-hit'));
              const normalizedParents = new Set();
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                normalizedParents.add(parent);
              };
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                unwrap(oldMarks[index]);
              }
              for (const parent of normalizedParents) parent.normalize?.();
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const folded = query.toLocaleLowerCase();
              const ignored = node => {
                const parent = node?.parentElement;
                return !parent
                  || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                  || !!parent.closest?.('#libzora-selection-bar, .libzora-wave-sweep, mark.libzora-page-find-hit');
              };
              const nodes = [];
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              while (walker.nextNode()) {
                if (!ignored(walker.currentNode)) nodes.push(walker.currentNode);
              }
              const nodeOffsets = [];
              let totalLength = 0;
              for (const node of nodes) {
                nodeOffsets.push(totalLength);
                totalLength += (node.data || '').length;
              }
              const text = nodes.map(node => node.data || '').join('');
              const foldedText = text.toLocaleLowerCase();
              const matches = [];
              const markedMatches = new Set();
              let start = foldedText.indexOf(folded);
              while (start >= 0) {
                matches.push({ start, length: query.length });
                start = foldedText.indexOf(folded, start + Math.max(1, folded.length));
              }
              const segmentsFor = (start, end) => {
                const segments = [];
                let low = 0, high = nodes.length;
                while (low < high) {
                  const mid = (low + high) >>> 1;
                  if ((nodeOffsets[mid + 1] ?? totalLength) <= start) low = mid + 1;
                  else high = mid;
                }
                let cursor = nodeOffsets[low] || 0;
                for (let nodeIndex = low; nodeIndex < nodes.length; nodeIndex++) {
                  const node = nodes[nodeIndex];
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
              for (let index = matches.length - 1; index >= 0; index--) {
                const match = matches[index];
                const segments = segmentsFor(match.start, match.start + match.length);
                let didMark = false;
                for (let segmentIndex = segments.length - 1; segmentIndex >= 0; segmentIndex--) {
                  const segment = segments[segmentIndex];
                  if (!segment.node.parentNode) continue;
                  const range = document.createRange();
                  range.setStart(segment.node, Math.min(segment.start, segment.node.data.length));
                  range.setEnd(segment.node, Math.min(segment.end, segment.node.data.length));
                  const mark = document.createElement('mark');
                  mark.className = 'libzora-page-find-hit';
                  mark.setAttribute('data-libzora-page-hit', String(index));
                  mark.style.setProperty('background', '#000000', 'important');
                  mark.style.setProperty('color', '#FFFFFF', 'important');
                  mark.style.setProperty('text-decoration', 'none', 'important');
                  try {
                    range.surroundContents(mark);
                    didMark = true;
                  } catch (_) {
                    // A malformed EPUB node should not abort the remaining hits.
                  }
                }
                if (didMark) markedMatches.add(index);
              }
              return markedMatches.size;
            })();
            """;
        string? result;
        try
        {
            result = await host.InvokeScriptAsync(script);
        }
        catch
        {
            if (sequence != _readerSearchSequence) return;
            _readerSearchCount = 0;
            _readerSearchIndex = -1;
            UpdateReaderSearchCount();
            return;
        }
        if (sequence != _readerSearchSequence) return;
        _readerSearchCount = ParseScriptInt(result);
        _readerSearchIndex = _readerSearchCount > 0
            ? navigate
                ? 0
                : Math.Clamp(_readerSearchIndex, 0, _readerSearchCount - 1)
            : -1;
        if (navigate)
            await NavigateReaderSearchAsync(_readerSearchIndex, sequence);
        else
            UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }

    private async Task NavigateReaderSearchAsync(int index, int? sequence = null)
    {
        if (_readerSearchCount <= 0 || CurrentReaderHost is not { } host)
        {
            UpdateReaderSearchCount();
            return;
        }
        if (sequence is not null && sequence.Value != _readerSearchSequence) return;
        _readerSearchIndex = (index % _readerSearchCount + _readerSearchCount) % _readerSearchCount;
        if (host is IPdfSearchHost pdf)
        {
            pdf.ScrollToSearchHit(_readerSearchIndex);
            UpdateReaderSearchCount();
            return;
        }
        if (host is IReaderTextSearchHost nativeReader)
        {
            nativeReader.ScrollToSearchHit(_readerSearchIndex);
            UpdateReaderSearchCount();
        }
    }

    private async Task ClearReaderSearchAsync()
    {
        _readerSearchSequence++;
        _readerPdfSearchSequence++;
        _readerSearchCount = 0;
        _readerSearchIndex = -1;
        await _readerSearchMutationGate.WaitAsync(ReaderToken);
        try
        {
        if (CurrentReaderHost is { } host)
        {
            try
            {
                if (host is IPdfTextHost pdf) pdf.ClearSearch();
                await host.InvokeScriptAsync("""
                    (() => {
                      const unwrap = mark => {
                        const parent = mark.parentNode;
                        if (!parent) return;
                        while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                        parent.removeChild(mark);
                        parent.normalize?.();
                      };
                      for (const mark of Array.from(document.querySelectorAll('mark.libzora-page-find-hit, mark.libzora-search-hit'))) {
                        unwrap(mark);
                      }
                    })();
                    """);
            }
            catch
            {
                // PDF's built-in viewer and a just-swapped WebView do not
                // expose a scriptable DOM; clearing search is still complete.
            }
        }
        UpdateReaderSearchCount();
        }
        finally
        {
            _readerSearchMutationGate.Release();
        }
    }
}
