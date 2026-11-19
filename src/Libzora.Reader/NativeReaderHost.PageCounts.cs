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
/// Chapter page-count estimation and position restore.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private static readonly object PageCountCacheLock = new();
    private static readonly Dictionary<long, (int[] Counts, long LastUse)> PageCountCache = [];
    private static long _pageCountCacheTick;

    private static long BuildPageCountCacheKey(
        IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings,
        double width,
        double height,
        (string ChapterPath, int PageCount)? knownChapter)
    {
        var hash = new HashCode();
        hash.Add(width);
        hash.Add(height);
        hash.Add(settings.VerticalWriting);
        hash.Add(settings.ParagraphIndent);
        hash.Add(settings.ParagraphSpacing);
        hash.Add(settings.ParagraphSpacingMatchesLineHeight);
        hash.Add(settings.OverridePublisherLineHeight);
        hash.Add(settings.OverridePublisherParagraphSpacing);
        hash.Add(settings.NaturalLatinTypography);
        hash.Add(settings.HyphenateEnglish);
        hash.Add(settings.AvoidWidowsAndOrphans);
        hash.Add(settings.AlignLeft);
        hash.Add(settings.CjkLatinSpacing);
        hash.Add(settings.FontScale);
        hash.Add(settings.LineHeight);
        hash.Add(settings.MaxWidth);
        hash.Add(settings.BodyPadding);
        hash.Add(settings.BodyBottomPadding);
        hash.Add(settings.FontFamily, StringComparer.Ordinal);
        hash.Add(settings.LatinFontFamily, StringComparer.Ordinal);
        hash.Add(knownChapter?.ChapterPath, StringComparer.Ordinal);
        hash.Add(knownChapter?.PageCount ?? -1);
        foreach (var path in chapterPaths)
        {
            var info = new FileInfo(path);
            hash.Add(path, StringComparer.Ordinal);
            hash.Add(info.Exists ? info.Length : -1);
            hash.Add(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        }
        return hash.ToHashCode();
    }

    private static int[] EstimatePageCountsCore(
        IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings,
        double width,
        double height,
        CancellationToken cancellationToken,
        TypesetFontLibrary? sharedFonts,
        (string ChapterPath, int PageCount)? knownChapter)
    {
        // Reuse the live reader's font library when available: opening a book
        // must not decode every candidate CJK face a second time just to
        // estimate page counts.
        var fonts = sharedFonts ?? CreateFontLibrary();
        try
        {
        using var engine = new TypesetEngine(fonts, ownsFonts: sharedFonts is null);
        SelectReaderMainFont(engine, settings.FontFamily);
        SelectReaderLatinFont(engine, settings.LatinFontFamily);
        var options = BuildOptions(settings, width, height);
        var counts = new int[chapterPaths.Count];
        for (var index = 0; index < chapterPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (knownChapter is { } known
                && string.Equals(known.ChapterPath, chapterPaths[index], StringComparison.Ordinal))
            {
                counts[index] = Math.Max(1, known.PageCount);
                continue;
            }
            try
            {
                var content = new XhtmlChapterLoader(settings.ParagraphIndent).Load(chapterPaths[index], cancellationToken);
                counts[index] = Math.Max(1, engine.Compose(content, options, cancellationToken).Pages.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException) when (sharedFonts is not null)
            {
                // The reader host closed while we were reusing its library.
                throw;
            }
            catch
            {
                // A malformed spine item should not make the progress label
                // disappear for the rest of a valid book.
                counts[index] = 1;
            }
        }

        return counts;
        }
        catch (ObjectDisposedException) when (sharedFonts is not null)
        {
            // Fall back to a private library so closing a book cannot turn a
            // background page-count estimate into an unobserved exception.
            return EstimatePageCounts(
                chapterPaths, settings, width, height, cancellationToken, null, knownChapter);
        }
    }

    private void ApplyPendingPosition()
    {
        if (_composePending || _layout is null || _layout.Pages.Count == 0)
        {
            return;
        }

        var page = _pendingSeekToEnd ? _layout.Pages.Count - 1
            : _pendingFragment is { } fragment ? _layout.GetPageIndexOfFragment(NormalizeFragment(fragment)) : -1;
        if (page < 0)
        {
            if (_pendingScrollOffset is { } scroll) _scrollOffset = ClampScrollOffset(scroll);
            else if (_pendingRestoreOffset is { } offset)
                page = _layout.GetPageIndexOfOffset(Math.Clamp(offset, 0, Math.Max(0, _layout.BodyTextLength - 1)));
            else if (_pendingRestorePage is { } restorePage)
                page = Math.Clamp(restorePage, 0, _layout.Pages.Count - 1);
            else if (_pendingRestoreRatio is { } ratio)
            {
                if (IsScroll) _scrollOffset = ClampScrollOffset(ratio * MaxScrollOffset);
                else page = (int)Math.Round(ratio * (_layout.Pages.Count - 1));
            }
            else if (_pendingReadingAnchor is { } anchor)
            {
                RestoreViewportReadingAnchor(anchor);
                _viewportReadingAnchor = anchor;
                RememberViewportReadingPosition();
            }
        }
        if (page >= 0)
        {
            if (IsScroll) _scrollOffset = ClampScrollOffset(page * Math.Max(1, Bounds.Height));
            else _pageIndex = IsSpread ? page - page % 2 : page;
        }

        // These are alternative destinations for one request. Once a fragment
        // wins, its fallback offset must not fire on the next resize/reflow.
        _pendingSeekToEnd = false;
        _pendingFragment = null;
        _pendingScrollOffset = null;
        _pendingRestoreOffset = null;
        _pendingRestorePage = null;
        _pendingRestoreRatio = null;
        _pendingReadingAnchor = null;
    }

    private static string NormalizeFragment(string fragment)
    {
        fragment = fragment.Trim().TrimStart('#');
        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }

    /// <summary>
    /// Re-runs the chapter load for a paragraph-indent toggle. The indent is
    /// applied by the loader when blocks are built, so Recompose alone never
    /// changes it. Keep the semantic position until the new content is ready.
    /// </summary>
    private Task<bool> ReloadChapterForIndentChange(ViewportReadingAnchor? readingAnchor, bool anchorPageStart)
    {
        if (Source is null || _disposed)
        {
            return Task.FromResult(false);
        }

        var uri = Source;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        return NavigateCore(path, _loadedFootnoteFragment, readingAnchor, anchorPageStart);
    }

    // ---- rendering --------------------------------------------------------

    private sealed record ViewportReadingAnchor(int TextOffset, int PageIndex, double RunY, double SliceFraction,
        int? ImageIndex = null);

    private static ViewportReadingAnchor ToViewportReadingAnchor(ReaderContentPosition position) =>
        new(position.TextOffset, position.PageIndex, position.RunY, position.SliceFraction, position.ImageIndex);
    private sealed record ViewportReadingState(
        ChapterContent Content, ChapterLayout Layout, ReaderLayoutSettings Settings, int PageIndex, double ScrollOffset);

    /// <summary>
    /// Pins the visible text while temporary reader chrome changes the viewport.
    /// Reuse the anchor until navigation changes the position, so repeated
    /// expand/collapse cycles cannot walk backwards through page boundaries.
    /// </summary>
    public void PreservePositionForViewportChange()
    {
        if (_disposed || _content is null || _layout is null || _layout.Pages.Count == 0) return;
        if (!ViewportReadingPositionMatches())
            _viewportReadingAnchor = CaptureViewportReadingAnchor();
        RememberViewportReadingPosition();
        _preserveViewportPosition = true;
    }

    private bool ViewportReadingPositionMatches() => _viewportReadingState is { } state
        && ReferenceEquals(state.Content, _content) && ReferenceEquals(state.Layout, _layout)
        && state.Settings == _settings && state.PageIndex == _pageIndex
        && Math.Abs(state.ScrollOffset - _scrollOffset) < 0.001;

    private void RememberViewportReadingPosition() => _viewportReadingState =
        new(_content!, _layout!, _settings, _pageIndex, _scrollOffset);

    private ViewportReadingAnchor CaptureViewportReadingAnchor()
    {
        var height = Math.Max(1, _layout!.Pages[0].Height);
        var first = IsScroll ? (int)(_scrollOffset / height) : _pageIndex;
        first = Math.Clamp(first, 0, _layout.Pages.Count - 1);
        var page = _layout.Pages[first];
        if (!IsScroll)
        {
            if (page.TextStartOffset < 0 && page.Images.FirstOrDefault() is { } image)
                return CaptureImageReadingAnchor(first, image, image.Rect.Top, 0);
            return new(page.TextStartOffset, first,
                page.Runs.FirstOrDefault(run => run.TextStart >= 0 && run.TextLength > 0)?.OriginY ?? page.InsetVertical, 0);
        }

        var slice = _scrollOffset - first * height;
        for (var index = first; index <= Math.Min(first + 1, _layout.Pages.Count - 1); index++)
        {
            var localTop = _scrollOffset - index * height;
            var run = _layout.Pages[index].Runs
                .Where(run => run.TextStart >= 0 && run.TextLength > 0 && run.OriginY >= localTop)
                .OrderBy(run => run.OriginY).ThenBy(run => run.TextStart).FirstOrDefault();
            var image = _layout.Pages[index].Images.Where(image => image.Rect.Bottom > localTop)
                .OrderBy(image => image.Rect.Top).FirstOrDefault();
            if (image is not null && (run is null || image.Rect.Top < run.OriginY - run.FontSize))
                return CaptureImageReadingAnchor(index, image, image.Rect.Top - localTop, slice / height);
            if (run is not null)
                return new(run.TextStart, index, run.OriginY - localTop, slice / height);
        }
        var fallback = page.Runs.FirstOrDefault(run => run.TextStart >= 0 && run.TextLength > 0);
        return new(fallback?.TextStart ?? page.TextStartOffset, first,
            (fallback?.OriginY ?? page.InsetVertical) - slice, slice / height);
    }

    private ViewportReadingAnchor CaptureImageReadingAnchor(int page, PlacedImage image, double y, double sliceFraction)
    {
        var ordinal = _layout!.Pages.Take(page).Sum(item => item.Images.Count)
            + _layout.Pages[page].Images.IndexOf(image);
        return new(-1, page, y, sliceFraction, ordinal);
    }

    private (int PageIndex, PlacedImage? Image) ResolveViewportReadingAnchor(ViewportReadingAnchor anchor)
    {
        if (anchor.ImageIndex is { } ordinal)
        {
            foreach (var item in _layout!.Pages)
            {
                if (ordinal < item.Images.Count) return (item.Index, item.Images[ordinal]);
                ordinal -= item.Images.Count;
            }
        }
        var page = anchor.TextOffset >= 0 ? _layout!.GetPageIndexOfOffset(anchor.TextOffset) : -1;
        return (page >= 0 ? page : Math.Clamp(anchor.PageIndex, 0, _layout!.Pages.Count - 1), null);
    }

    private void RestoreViewportReadingAnchor(ViewportReadingAnchor anchor)
    {
        if (_layout is null || _layout.Pages.Count == 0) return;
        var (page, image) = ResolveViewportReadingAnchor(anchor);
        if (IsScroll)
        {
            var height = Math.Max(1, _layout.Pages[0].Height);
            var run = _layout.Pages[page].Runs.FirstOrDefault(run => run.TextStart >= 0
                && run.TextStart <= anchor.TextOffset && run.TextStart + run.TextLength > anchor.TextOffset);
            var slice = image is not null ? image.Rect.Top - anchor.RunY
                : run is not null ? run.OriginY - anchor.RunY : anchor.SliceFraction * height;
            _scrollOffset = ClampScrollOffset(page * height + slice);
        }
        else
        {
            _pageIndex = IsSpread ? page - page % 2 : page;
        }
    }

    private void ScheduleRelayout()
    {
        if (_disposed || _content is null || _composePending)
        {
            return;
        }

        PreservePositionForViewportChange();
        _relayoutTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Background, async (_, _) =>
        {
            _relayoutTimer!.Stop();
            if (!_disposed && _content is not null && !_composePending)
            {
                await (RelayoutTask = RecomposeAsync());
                if (_disposed || _composePending) return;
                ApplyPendingPosition();
                _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, PageCount - 1));
                _bitmapDirty = true;
                InvalidateVisual();
                EmitScroll();
            }
        });
        _relayoutTimer.Stop();
        _relayoutTimer.Start();
    }
}
