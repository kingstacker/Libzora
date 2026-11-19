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

    private bool IsScroll => _presentation == ReaderPresentation.Scroll;

    /// <summary>Composed pages turned per step: two for the spread layout.</summary>
    private int PageStep => IsSpread ? 2 : 1;

    private double ComposePageWidth(double viewportWidth) =>
        IsSpread ? Math.Max(1, viewportWidth / 2) : viewportWidth;

    private double MaxScrollOffset =>
        Math.Max(0, (_layout?.Pages.Count ?? 0) * Math.Max(1, Bounds.Height) - Math.Max(1, Bounds.Height));

    private double ClampScrollOffset(double value) => Math.Clamp(value, 0, MaxScrollOffset);

    public int PageCount => _layout?.Pages.Count ?? 0;

    public int CurrentPage => _pageIndex;

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
