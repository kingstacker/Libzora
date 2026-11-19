using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Text.RegularExpressions;

namespace Libzora;

/// <summary>
/// Bookmark creation, indicators and navigation.
/// </summary>
public partial class MainWindow
{
    private async void ReaderBookmarkList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReaderBookmark bookmark)
        {
            try
            {
                await NavigateToReaderBookmarkAsync(bookmark);
            }
            catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReaderStatusText.Text = T("书签定位失败：{0}", UiText.Localize(exception.Message));
            }
            finally
            {
                // ListBox does not raise SelectionChanged when the user
                // chooses the same item twice. Clear the transient selection
                // after every jump so a bookmark can be opened repeatedly.
                ReaderBookmarkList.SelectedIndex = -1;
            }
        }
    }

    private async void ReaderBookmarkDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReaderBookmark bookmark } || _readerBookFile is null) return;
        try
        {
            await _readerData.DeleteBookmarkAsync(bookmark.Id, ReaderToken);
            await RefreshReaderBookmarksAsync(ReaderToken);
            await UpdateReaderBookmarkIndicatorAsync();
            ShowReaderTransientStatus(T("书签已删除"));
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("删除书签失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async Task ToggleReaderBookmarkAsync()
    {
        if (_readerBookCard is null || _readerBookFile is null) return;
        var location = await CaptureCurrentReaderBookmarkLocationAsync();
        if (CurrentReaderHost is IReaderPositionHost && location is null) return;
        var currentPath = location?.ChapterPath ?? (_readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath());
        if (string.IsNullOrWhiteSpace(currentPath)) return;

        var fragment = _readerIsPdf
            ? null
            : location?.Fragment ?? _readerCurrentFragment;
        var quote = _readerIsPdf
            ? T("PDF 第 {0} 页", _readerPdfPage)
            : await CaptureCurrentPageQuoteAsync();
        var currentPosition = location?.ScrollPosition;
        var currentFlowMode = location?.FlowMode ?? _readerLayout.FlowMode;
        var existing = ReaderBookmarks.FirstOrDefault(bookmark =>
            AreReaderBookmarkChapterPathsEqual(bookmark.ChapterPath, currentPath)
            && (ReaderContentPosition.Validate(bookmark.ContentPosition) is { } savedContent
                && CurrentReaderHost is IReaderPositionHost native
                ? native.IsContentPositionVisible(savedContent)
                : string.Equals(bookmark.Fragment, fragment, StringComparison.OrdinalIgnoreCase)
                && (bookmark.ScrollPosition is int savedPosition
                && currentPosition is int position
                ? Math.Abs(savedPosition - position) <= 4
                : string.IsNullOrWhiteSpace(bookmark.Quote)
                  || string.IsNullOrWhiteSpace(quote)
                  || string.Equals(bookmark.Quote, quote, StringComparison.OrdinalIgnoreCase))));

        try
        {
            if (existing is not null)
            {
                await _readerData.DeleteBookmarkAsync(existing.Id, ReaderToken);
                ShowReaderTransientStatus(T("已取消书签"));
                ShowReaderBookmarkFeedback(T("已取消书签"));
            }
            else
            {
                await _readerData.SaveBookmarkAsync(new ReaderBookmark
                {
                    BookId = _readerBookCard.Book.Id,
                    BookFileId = _readerBookFile.Id,
                    ChapterPath = currentPath,
                    Fragment = fragment,
                    ChapterIndex = _readerChapterIndex,
                    ScrollPosition = currentPosition,
                    FlowMode = currentFlowMode,
                    ContentPosition = location?.ContentPosition,
                    Title = GetReaderChapterLabel(),
                    Quote = quote ?? string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow
                }, ReaderToken);
                ShowReaderTransientStatus(T("已添加书签"));
                ShowReaderBookmarkFeedback(T("已添加书签"));
            }
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReaderStatusText.Text = T("书签保存失败：{0}", UiText.Localize(exception.Message));
        }
        await RefreshReaderBookmarksAsync(ReaderToken);
        await UpdateReaderBookmarkIndicatorAsync();
    }

    private async Task<string?> CaptureCurrentPageQuoteAsync()
    {
        if (CurrentReaderHost is not { } host) return null;
        if (host is IReaderPositionHost nativeReader)
            return nativeReader.GetCurrentPageQuote();

        try
        {
            var result = await host.InvokeScriptAsync(
                """
                (() => {
                  const body = document.body;
                  if (!body) return '';
                  const viewportWidth = window.visualViewport?.width
                    || window.innerWidth
                    || document.documentElement.clientWidth;
                  const viewportHeight = window.visualViewport?.height
                    || window.innerHeight
                    || document.documentElement.clientHeight;
                  const vertical = (getComputedStyle(body).writingMode || '').startsWith('vertical');
                  const twoPage = window.__libzoraReaderFlowMode === 1
                    && window.__libzoraReaderTwoPage === true;
                  const ignoredSelector = [
                    'script', 'style', 'noscript',
                    '#libzora-selection-bar', '#libzora-bookmark-corner',
                    '#lz-slide', '#lz-wave', '.libzora-wave-sweep'
                  ].join(',');
                  const intersectsViewport = rect => rect
                    && rect.width > 0
                    && rect.height > 0
                    && (rect.left + rect.right) / 2 >= 0
                    && (rect.left + rect.right) / 2 <= viewportWidth
                    && (rect.top + rect.bottom) / 2 >= 0
                    && (rect.top + rect.bottom) / 2 <= viewportHeight;
                  const caretAt = (x, y) => {
                    if (typeof document.caretRangeFromPoint === 'function') {
                      const range = document.caretRangeFromPoint(x, y);
                      if (range) return { node: range.startContainer, offset: range.startOffset };
                    }
                    if (typeof document.caretPositionFromPoint === 'function') {
                      const position = document.caretPositionFromPoint(x, y);
                      if (position) return { node: position.offsetNode, offset: position.offset };
                    }
                    return null;
                  };
                  const visibleGlyphRect = (node, offset, rect) => {
                    if (!intersectsViewport(rect)) return false;
                    const caret = caretAt(
                      (rect.left + rect.right) / 2,
                      (rect.top + rect.bottom) / 2);
                    return caret?.node === node && Math.abs(caret.offset - offset) <= 1;
                  };
                  const glyphs = [];
                  let domOrder = 0;
                  const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
                  while (walker.nextNode()) {
                    const node = walker.currentNode;
                    const parent = node.parentElement;
                    const value = node.data || '';
                    if (!parent || !value.trim() || parent.closest?.(ignoredSelector)) continue;

                    const nodeRange = document.createRange();
                    nodeRange.selectNodeContents(node);
                    if (!Array.from(nodeRange.getClientRects()).some(intersectsViewport)) continue;

                    const characterRange = document.createRange();
                    for (let offset = 0; offset < value.length; offset++) {
                      characterRange.setStart(node, offset);
                      characterRange.setEnd(node, offset + 1);
                      const rect = Array.from(characterRange.getClientRects())
                        .find(candidate => visibleGlyphRect(node, offset, candidate));
                      if (!rect) continue;
                      glyphs.push({
                        value: value[offset],
                        left: rect.left,
                        right: rect.right,
                        top: rect.top,
                        bottom: rect.bottom,
                        domOrder: domOrder++
                      });
                    }
                  }

                  if (!glyphs.length) return '';
                  const pages = new Map();
                  for (const glyph of glyphs) {
                    const center = (glyph.left + glyph.right) / 2;
                    const page = twoPage ? (center < viewportWidth / 2 ? 0 : 1) : 0;
                    if (!pages.has(page)) pages.set(page, []);
                    pages.get(page).push(glyph);
                  }

                  const pageNumbers = Array.from(pages.keys()).sort((a, b) => a - b);
                  const visualLines = [];
                  for (const pageNumber of pageNumbers) {
                    const pageGlyphs = pages.get(pageNumber);
                    pageGlyphs.sort(vertical
                      ? (a, b) => b.right - a.right || a.top - b.top || a.domOrder - b.domOrder
                      : (a, b) => a.top - b.top || a.left - b.left || a.domOrder - b.domOrder);
                    const lines = [];
                    for (const glyph of pageGlyphs) {
                      const start = vertical ? glyph.left : glyph.top;
                      const end = vertical ? glyph.right : glyph.bottom;
                      const line = lines.find(candidate =>
                        Math.min(candidate.end, end) >= Math.max(candidate.start, start) - 1);
                      if (line) {
                        line.glyphs.push(glyph);
                        line.start = Math.min(line.start, start);
                        line.end = Math.max(line.end, end);
                      } else {
                        lines.push({ start, end, glyphs: [glyph] });
                      }
                    }
                    lines.sort(vertical
                      ? (a, b) => b.end - a.end
                      : (a, b) => a.start - b.start);
                    for (const line of lines) {
                      line.glyphs.sort(vertical
                        ? (a, b) => a.top - b.top || a.domOrder - b.domOrder
                        : (a, b) => a.left - b.left || a.domOrder - b.domOrder);
                      const text = line.glyphs.map(glyph => glyph.value).join('').trim();
                      if (text) visualLines.push(text);
                    }
                  }
                  return visualLines.slice(0, 3).join('\n');
                })();
                """);
            var normalized = NormalizeReaderBookmarkQuote(DecodeReaderScriptString(result));
            return normalized.Length <= 72 ? normalized : normalized[..72];
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeReaderBookmarkQuote(string? text) =>
        string.Join(
            " ",
            (text ?? string.Empty).Split(
                [' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries));

    private async Task<ReaderBookmarkLocation?> CaptureCurrentReaderBookmarkLocationAsync()
    {
        if (_readerBookFile is null) return null;
        var chapterPath = _readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return null;

        var flowMode = _readerIsPdf ? 0 : _readerLayout.FlowMode;
        if (_readerIsPdf)
        {
            return new ReaderBookmarkLocation(
                chapterPath,
                0,
                flowMode,
                null);
        }

        if (CurrentReaderHost is not { } host) return null;
        if (host is IReaderPositionHost nativeReader)
        {
            var contentPosition = nativeReader.CaptureContentPosition();
            if (contentPosition is null) return null;
            var nativeState = nativeReader.GetScrollState();
            var nativePosition = Math.Max(0, (int)Math.Round(nativeState.Position));
            return new ReaderBookmarkLocation(
                chapterPath,
                nativePosition,
                nativeReader.IsPaginated ? 1 : 0,
                _readerCurrentFragment)
            {
                ContentPosition = contentPosition
            };
        }

        try
        {
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return null; return JSON.stringify({ left: el.scrollLeft || 0, top: el.scrollTop || 0, fragment: window.__libzoraReaderLogicalHash || location.hash || '' }); })();");
            var raw = DecodeReaderScriptString(result);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
            var position = horizontal
                ? ReadDouble(root, "left")
                : ReadDouble(root, "top");
            if (_readerLayout.VerticalWriting)
                position = Math.Abs(position);
            var fragment = ReadString(root, "fragment").TrimStart('#');
            try { fragment = Uri.UnescapeDataString(fragment); } catch { }
            return new ReaderBookmarkLocation(
                chapterPath,
                Math.Max(0, (int)Math.Round(position)),
                flowMode,
                string.IsNullOrWhiteSpace(fragment) ? _readerCurrentFragment : fragment);
        }
        catch
        {
            // The native host can be between documents while a chapter is
            // swapping. The indicator will hide until the next scroll report;
            // saving a bookmark still has the in-memory position as a safe
            // fallback at the call site.
            return null;
        }
    }

    private static string? DecodeReaderScriptString(string? result)
    {
        var raw = result?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(raw);
            }
            catch (JsonException)
            {
                return raw[1..^1];
            }
        }
        return raw;
    }

    private static bool AreReaderBookmarkChapterPathsEqual(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return TryGetReaderPdfPage(left, out var leftPage)
            && TryGetReaderPdfPage(right, out var rightPage)
            && leftPage == rightPage;
    }

    private static bool TryGetReaderPdfPage(string? chapterPath, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(chapterPath)
            || !chapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
            return false;
        var value = chapterPath[4..];
        if (value.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        return int.TryParse(value, out page) && page > 0;
    }

    // Shows a transient ToolTip near the bookmark button so the user gets
    // immediate feedback without clobbering the header status text (which is
    // also updated). Mirrors the WinUI ReaderBookmarkFeedbackToolTip.
    private void ShowReaderBookmarkFeedback(string message)
    {
        if (ReaderBookmarkButton is null) return;
        ToolTip.SetTip(ReaderBookmarkButton, message);
        ToolTip.SetIsOpen(ReaderBookmarkButton, true);
        _ = Task.Delay(1600).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (ReaderBookmarkButton is not null)
                    ToolTip.SetIsOpen(ReaderBookmarkButton, false);
            }),
            TaskScheduler.Default);
    }

    private void ClearReaderBookmarkPendingLocation()
    {
        _readerPendingBookmarkQuote = null;
        _readerPendingBookmarkPosition = null;
        _readerPendingBookmarkFlowMode = 0;
        _readerPendingBookmarkContentPosition = null;
    }

    private async Task UpdateReaderBookmarkIndicatorAsync()
    {
        var sequence = ++_readerBookmarkIndicatorSequence;
        if (ReaderBookmarkCornerMarker is null) return;
        var location = await CaptureCurrentReaderBookmarkLocationAsync();
        if (sequence != _readerBookmarkIndicatorSequence) return;
        ApplyReaderBookmarkIndicator(location);
    }

    private void UpdateReaderBookmarkIndicatorFromTrackedLocation()
    {
        _readerBookmarkIndicatorSequence++;
        var chapterPath = _readerIsPdf
            ? $"pdf:{_readerPdfPage}"
            : GetReaderChapterPath();
        var location = string.IsNullOrWhiteSpace(chapterPath)
            ? null
            : new ReaderBookmarkLocation(
                chapterPath,
                Math.Max(0, (int)Math.Round(_readerScrollPosition)),
                _readerIsPdf ? 0 : _readerLayout.FlowMode,
                _readerIsPdf ? null : _readerCurrentFragment)
            {
                ContentPosition = (CurrentReaderHost as IReaderPositionHost)?.CaptureContentPosition()
            };
        ApplyReaderBookmarkIndicator(location);
    }

    private void ApplyReaderBookmarkIndicator(ReaderBookmarkLocation? location)
    {
        if (ReaderBookmarkCornerMarker is null) return;
        if (location is null)
        {
            ReaderBookmarkCornerMarker.IsVisible = false;
            if (!_readerIsPdf)
                _ = ObserveReaderTaskAsync(SetReaderDocumentBookmarkIndicatorAsync(false));
            return;
        }

        var tolerance = location.FlowMode == 1 ? 8 : 4;
        var isBookmarked = ReaderBookmarks.Any(bookmark =>
            ReaderContentPosition.Validate(bookmark.ContentPosition) is { } savedContent
                && CurrentReaderHost is IReaderPositionHost native
            ? AreReaderBookmarkChapterPathsEqual(bookmark.ChapterPath, location.ChapterPath)
                && native.IsContentPositionVisible(savedContent)
            : ReaderBookmarkPolicy.MatchesVisiblePosition(
                bookmark.ChapterPath,
                bookmark.FlowMode,
                bookmark.ScrollPosition,
                location.ChapterPath,
                location.FlowMode,
                location.ScrollPosition,
                tolerance));
        // The corner ribbon shows on every surface. EPUB used to delegate the
        // triangle to the injected WebView page; the self-drawn engine has no
        // DOM, so the Avalonia marker renders for it as well.
        ReaderBookmarkCornerMarker.IsVisible = isBookmarked;
        if (!_readerIsPdf)
            _ = ObserveReaderTaskAsync(SetReaderDocumentBookmarkIndicatorAsync(isBookmarked));
    }

    private async Task SetReaderDocumentBookmarkIndicatorAsync(bool isBookmarked)
    {
        if (CurrentReaderHost is not { } host) return;
        try
        {
            await host.InvokeScriptAsync(
                $"window.__libzoraSetBookmarkMarked?.({(isBookmarked ? "true" : "false")});");
        }
        catch
        {
            // The outgoing chapter may be disposed while its indicator update
            // is still queued. The incoming chapter refreshes its own state.
        }
    }

    private async Task NavigateToReaderBookmarkAsync(ReaderBookmark bookmark)
    {
        if (_readerIsPdf)
        {
            var page = bookmark.ChapterIndex + 1;
            if (TryGetReaderPdfPage(bookmark.ChapterPath, out var savedPage))
                page = savedPage;
            await NavigatePdfPageAsync(page, ReaderToken);
            await UpdateReaderBookmarkIndicatorAsync();
            return;
        }
        if (_readerDocument is null) return;
        var path = Path.GetFullPath(Path.Combine(
            _readerDocument.RootPath,
            bookmark.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerDocument.RootPath, path) || !File.Exists(path)) return;

        var chapterIndex = _readerDocument.Chapters
            .Select((chapter, index) => (chapter, index))
            .Where(item => string.Equals(
                Path.GetFullPath(item.chapter),
                path,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (chapterIndex < 0) return;

        // Do not move _readerChapterIndex here: NavigateToReaderItemAsync
        // records the previous chapter for its own rollback, and a failed
        // bookmark load must leave both the page and the index on the old
        // chapter.
        _readerPendingBookmarkQuote = bookmark.Quote;
        _readerPendingBookmarkPosition = bookmark.ScrollPosition;
        _readerPendingBookmarkFlowMode = bookmark.FlowMode;
        _readerPendingBookmarkContentPosition = bookmark.ContentPosition;
        var target = new Uri(path);
        var fragment = DecodeReaderFragment(bookmark.Fragment);
        if (!string.IsNullOrWhiteSpace(fragment))
            target = new Uri(target.AbsoluteUri + "#" + Uri.EscapeDataString(fragment));

        try
        {
            var navigated = await NavigateToReaderItemAsync(
                new EpubReaderNavigationItem(bookmark.Title, target.AbsoluteUri, chapterIndex),
                ReaderToken,
                ReaderNavigationIntent.Bookmark);
            if (navigated)
                await UpdateReaderBookmarkIndicatorAsync();
        }
        finally
        {
            ClearReaderBookmarkPendingLocation();
        }
    }
}
