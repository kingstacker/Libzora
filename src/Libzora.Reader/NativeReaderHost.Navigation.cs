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
/// Top-level navigation entry points.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    /// <summary>Loads and lays out a chapter. The URI fragment selects the anchor.</summary>
    public void Navigate(Uri uri) => Navigate(uri, _settings, _showVerticalDebugBoxes);

    public void Navigate(Uri uri, ReaderLayoutSettings settings, bool showVerticalDebugBoxes)
        => NavigateCoreFromUri(uri, settings, showVerticalDebugBoxes, null, false);

    public void Navigate(Uri uri, ReaderLayoutSettings settings, bool showVerticalDebugBoxes,
        int? composeTextOffset, bool allowPartialCompose)
        => NavigateCoreFromUri(uri, settings, showVerticalDebugBoxes, composeTextOffset, allowPartialCompose);

    private void NavigateCoreFromUri(Uri uri, ReaderLayoutSettings settings, bool showVerticalDebugBoxes,
        int? composeTextOffset, bool allowPartialCompose)
    {
        if (_disposed)
        {
            return;
        }

        ++_configurationVersion;
        // Apply the book's settings before the background layout. Configuring
        // only after NavigationCompleted would compose the first chapter twice,
        // with the second (potentially large) pass blocking the UI thread.
        _settings = settings;
        _presentation = DerivePresentation(settings);
        _showVerticalDebugBoxes = showVerticalDebugBoxes;
        Source = uri;
        var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
        string? fragment = null;
        if (!string.IsNullOrWhiteSpace(uri.Fragment))
        {
            try
            {
                fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            }
            catch (UriFormatException)
            {
                fragment = uri.Fragment.TrimStart('#');
            }
        }
        _ = NavigateCore(path, fragment, composeTextOffset: composeTextOffset,
            allowPartialCompose: allowPartialCompose);
    }

    // ---- native surface API used by the MainWindow seams ------------------

    public bool IsNative => true;

    /// <summary>
    /// True when the surface presents paginated pages. Horizontal scroll
    /// mode scrolls continuously instead, so the surrounding pipeline treats
    /// it like the WebView scroll reader (scroll-state saves, edge-driven
    /// chapter advance, keyboard scrolling).
    /// </summary>
    public bool IsPaginated => _presentation != ReaderPresentation.Scroll;

    private bool IsSpread => _presentation == ReaderPresentation.Spread;

    public bool IsShowingCompanionSpread =>
        IsSpread
        && _settings.MangaMode
        && _layout is { Pages.Count: 1 }
        && _spreadCompanionLayout is { Pages.Count: > 0 };

    public void SetSpreadCompanionChapterPath(string? chapterPath)
    {
        var normalized = string.IsNullOrWhiteSpace(chapterPath) ? null : Path.GetFullPath(chapterPath);
        if (string.Equals(
                _spreadCompanionChapterPath,
                normalized,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        _spreadCompanionChapterPath = normalized;
        _spreadCompanionLayout = null;
    }

    public void SetScrollNextChapterPath(string? chapterPath)
    {
        var normalized = string.IsNullOrWhiteSpace(chapterPath) ? null : chapterPath;
        if (string.Equals(_scrollNextChapterPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Keep the currently rendered _scrollNextChapterLayout intact until
        // NavigateCore or RecomposeAsync atomically installs the next chapter;
        // clearing it eagerly blanks the viewport during chapter handoff.
        _scrollNextChapterPath = normalized;
    }

    /// <summary>
    /// Supplies the exact scroll handoff mode to apply atomically when the next
    /// continuous-scroll chapter navigation commits, preventing any flash at
    /// offset 0 or bounce at the chapter seam.
    /// </summary>
    public void PrepareContinuousScrollHandoff(
        bool handoffDown = false,
        double? carryDownOffset = null,
        double? carryUpDelta = null)
    {
        _handoffScrollDown = handoffDown || carryDownOffset.HasValue;
        _handoffScrollOffset = carryDownOffset;
        _handoffFromNextChapterDelta = carryUpDelta;
        _activeHandoffDirection = _handoffScrollDown ? 1 : carryUpDelta.HasValue ? -1 : 0;
        _activeHandoffScrollDelta = 0;
        _activeHandoffScrollOrigin = _handoffScrollDown
            ? (HasNextContinuousPreview ? ContinuousNextChapterStartY : MaxScrollOffset) + (carryDownOffset ?? 0)
            : -(carryUpDelta ?? 0);
        _partialScrollOverflow = 0;
    }

    private bool IsScroll => _presentation == ReaderPresentation.Scroll;

    private bool IsContinuousProseScroll => IsScroll && !_settings.MangaMode;

    private const double ContinuousChapterGap = 52.0;

    private double ScrollPageHeight =>
        _options?.ViewportHeight is > 1 ? _options.ViewportHeight : Math.Max(1.0, Bounds.Height);

    private double ComputeContinuousContentHeight() => ComputeContinuousContentHeight(_layout);

    private double ComputeContinuousContentHeight(ChapterLayout? layout)
    {
        if (layout is null || layout.Pages.Count == 0) return 0;
        var pageHeight = ScrollPageHeight;
        var maxBottom = 0.0;
        for (var p = layout.Pages.Count - 1; p >= 0; p--)
        {
            var pageBottom = MeasureContinuousPageBottom(layout.Pages[p], p * pageHeight);
            if (pageBottom <= 0) continue;
            maxBottom = pageBottom;
            if (p > 0)
            {
                maxBottom = Math.Max(maxBottom, MeasureContinuousPageBottom(layout.Pages[p - 1], (p - 1) * pageHeight));
            }
            break;
        }
        return maxBottom > 0 ? maxBottom : layout.Pages.Count * pageHeight;
    }

    private static double MeasureContinuousPageBottom(LayoutPage page, double pageTop)
    {
        var maxBottom = 0.0;
        foreach (var run in page.Runs)
            maxBottom = Math.Max(maxBottom, pageTop + run.OriginY + run.FontSize * 0.45);
        foreach (var img in page.Images)
            maxBottom = Math.Max(maxBottom, pageTop + img.Rect.Bottom);
        foreach (var dec in page.Decorations)
            maxBottom = Math.Max(maxBottom, pageTop + dec.Rect.Bottom);
        foreach (var zone in page.HotZones)
            maxBottom = Math.Max(maxBottom, pageTop + zone.Rect.Bottom);
        return maxBottom;
    }

    private double ContinuousNextChapterStartY => ComputeContinuousContentHeight() + ContinuousChapterGap;

    /// <summary>Composed pages turned per step: two for the spread layout.</summary>
    private int PageStep => IsSpread ? 2 : 1;

    private double ComposePageWidth(double viewportWidth) =>
        IsSpread ? Math.Max(1, viewportWidth / 2) : viewportWidth;

    private double MaxScrollOffset
    {
        get
        {
            if (_layout is null || _layout.Pages.Count == 0) return 0;
            var pageHeight = ScrollPageHeight;
            if (!IsContinuousProseScroll)
                return Math.Max(0, _layout.Pages.Count * pageHeight - pageHeight);
            if (HasNextContinuousPreview)
                return Math.Max(0, ContinuousNextChapterStartY);
            return Math.Max(0, ComputeContinuousContentHeight() + 48.0 - pageHeight);
        }
    }

    /// <summary>
    /// Live continuous scroll limit including the pre-composed next-chapter
    /// runway, so dragging and inertia never stall at the chapter seam while
    /// the next chapter finishes loading off-thread.
    /// </summary>
    private double MaxContinuousScrollOffset
    {
        get
        {
            var baseMax = MaxScrollOffset;
            if (!HasNextContinuousPreview) return baseMax;
            var nextRunway = Math.Max(0.0, ComputeContinuousContentHeight(_scrollNextChapterLayout) - ScrollPageHeight);
            return Math.Max(baseMax, ContinuousNextChapterStartY + nextRunway);
        }
    }

    private double ClampScrollOffset(double value) => Math.Clamp(value, 0, MaxScrollOffset);

    private double ClampLiveScrollOffset(double value) =>
        Math.Clamp(value, ContinuousPreviousChapterStartY, MaxContinuousScrollOffset);

    public int PageCount => _layout?.Pages.Count ?? 0;

    public int CurrentPage => IsScroll ? VisibleFirstPageIndex : _pageIndex;

    /// <summary>True when the current chapter was reloaded to reveal a hidden footnote fragment.</summary>
    public bool HasLoadedFootnoteFragment => _loadedFootnoteFragment is not null;

    /// <summary>
    /// False while the opening pass has only composed the first screen of a
    /// large chapter. Page turns, seeking and speech wait for the background
    /// completion instead of treating the partial page count as the chapter.
    /// </summary>
    public bool IsLayoutComplete => _layout?.IsComplete ?? true;

    public int BodyTextLength => _content?.BodyText.Length ?? 0;

    /// <summary>
    /// Character offset of the first visible text. Progress uses this ratio
    /// while the layout is partial, because the page index would otherwise
    /// report the end of the composed prefix as the end of the chapter.
    /// </summary>
    public int CurrentTextOffset
    {
        get
        {
            if (_layout is null || _layout.Pages.Count == 0) return 0;
            var pageIndex = Math.Clamp(VisibleFirstPageIndex, 0, _layout.Pages.Count - 1);
            return Math.Max(0, _layout.Pages[pageIndex].TextStartOffset);
        }
    }
}
