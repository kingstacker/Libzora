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
/// Relayout scheduling, recomposition and appearance.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private void RetainInstalledLayoutFonts()
    {
        // Keep every face used by every page, not just the selected main face
        // or the visible page. Old system choices can become glyph fallbacks.
        // The worker's composition scope spans shaping through installation,
        // so deferred cleanup cannot unload its unpublished layout either.
        var fonts = FontLibrary;
        fonts.SetRetainedFontPaths(_layout!.Pages.SelectMany(page => page.Runs)
            .Select(run => run.FontPath));
        fonts.ReleaseSystemFontsExcept(fonts.MainFontPath);
    }

    private async Task RecomposeAsync(ViewportReadingAnchor? readingAnchor = null, bool anchorPageStart = false)
    {
        if (_disposed || _content is null)
        {
            return;
        }

        var completingPartialLayout = _layout is { IsComplete: false };
        var viewportAnchor = readingAnchor ?? (_preserveViewportPosition && ViewportReadingPositionMatches()
            ? _viewportReadingAnchor : null);
        _preserveViewportPosition = false;
        var width = Math.Max(1, ComposePageWidth(Bounds.Width));
        var height = Math.Max(1, Bounds.Height);
        var startOffset = _layout is not null
            && _pageIndex < _layout.Pages.Count
            ? _layout.Pages[_pageIndex].TextStartOffset
            : -1;

        var initialPage = _pageIndex;
        var initialScroll = _scrollOffset;
        var content = _content;
        var settings = _settings;
        var options = BuildOptions(settings, width, height, _showVerticalDebugBoxes, SnapshotNavigationFragments());
        var companionPath = _spreadCompanionChapterPath;
        var isSpread = IsSpread;
        _relayoutCts?.Cancel();
        using var request = new CancellationTokenSource();
        _relayoutCts = request;
        var token = request.Token;
        TypesetEngine? workerEngine = null;
        IDisposable? fontUse = null;
        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var fonts = FontLibrary;
                fontUse = fonts.BeginComposition();
                workerEngine = new TypesetEngine(fonts, ownsFonts: false);
                SelectReaderMainFont(workerEngine, settings.FontFamily);
                SelectReaderLatinFont(workerEngine, settings.LatinFontFamily);
                var layout = workerEngine.Compose(content, options, token,
                    anchorPageStart ? viewportAnchor?.TextOffset : null);
                var companionLayout = ComposeSpreadCompanion(
                    isSpread,
                    companionPath,
                    options,
                    layout,
                    workerEngine,
                    token);
                return (layout, companionLayout);
            }, token);
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(content, _content)) return;
            if (_layout is { Pages.Count: > 0 }
                && (_pageIndex != initialPage || Math.Abs(_scrollOffset - initialScroll) > 0.001))
                viewportAnchor = CaptureViewportReadingAnchor();
            lock (_engineGate)
            {
                var previous = _engine;
                _engine = workerEngine;
                workerEngine = null;
                _options = options;
                _layout = result.layout;
                _spreadCompanionLayout = result.companionLayout;
                previous?.Dispose();
                RetainInstalledLayoutFonts();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        finally
        {
            workerEngine?.Dispose();
            fontUse?.Dispose();
            if (ReferenceEquals(_relayoutCts, request)) _relayoutCts = null;
        }
        InvalidateSpeechTextSnapshot();
        _composePending = false;

        if (IsScroll)
        {
            _scrollOffset = ClampScrollOffset(_scrollOffset);
        }
        else
        {
            if (IsSpread)
            {
                _pageIndex -= _pageIndex % 2;
            }

            if (startOffset >= 0)
            {
                var page = _layout.GetPageIndexOfOffset(startOffset);
                if (IsSpread && page > 0)
                {
                    page -= page % 2;
                }

                _pageIndex = page >= 0 ? page : 0;
            }
        }
        if (viewportAnchor is not null)
        {
            RestoreViewportReadingAnchor(viewportAnchor);
            _viewportReadingAnchor = viewportAnchor;
            RememberViewportReadingPosition();
        }
        else
        {
            _viewportReadingAnchor = null;
            _viewportReadingState = null;
        }
        PrefetchVisibleImages();

        if (!_disposed && completingPartialLayout && _layout is { IsComplete: true })
        {
            LayoutCompleted?.Invoke(this, EventArgs.Empty);
            EmitScroll();
        }
    }

    /// <summary>
    /// Starts the full layout pass for a chapter whose first screen was
    /// composed for immediate display. Idempotent while a completion is
    /// already running.
    /// </summary>
    public void CompleteLayoutInBackground()
    {
        if (_disposed || _layout is not { IsComplete: false } || _layoutCompletionScheduled) return;
        _layoutCompletionScheduled = true;
        RelayoutTask = CompleteLayoutAfterPaintAsync();
    }

    /// <summary>
    /// Waits for the background completion before an operation that needs the
    /// whole chapter (page turn, seek, speech). Starts a fresh pass if the
    /// previous one was cancelled or installed another partial layout.
    /// </summary>
    public async Task EnsureCompleteLayoutAsync()
    {
        for (var attempt = 0; attempt < 4 && !_disposed && _layout is { IsComplete: false }; attempt++)
        {
            var running = RelayoutTask;
            if (!running.IsCompleted)
            {
                try { await running; }
                catch (OperationCanceledException) { }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Console.WriteLine($"[Libzora] layout completion failed: {exception.Message}");
                    return;
                }
            }

            if (_disposed || _layout is not { IsComplete: false }) return;
            await (RelayoutTask = RecomposeAsync());
        }
    }

    private async Task CompleteLayoutAfterPaintAsync()
    {
        try
        {
            await RecomposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine($"[Libzora] background layout completion failed: {exception.Message}");
        }
        finally
        {
            _layoutCompletionScheduled = false;
        }
    }

    /// <summary>Power-saving mode caps render scaling at 2x and uses pure OLED black for dark paper.</summary>
    public bool PowerSavingMode { get; private set; }

    public void SetPowerSavingMode(bool enabled)
    {
        if (PowerSavingMode == enabled) return;
        PowerSavingMode = enabled;
        _palette = ReaderPalette.For(_appearance.Theme, PowerSavingMode || _pureBlackBackground);
        _paintTheme = _palette.PaintTheme;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void SetPureBlackBackground(bool enabled)
    {
        if (_pureBlackBackground == enabled) return;
        _pureBlackBackground = enabled;
        _palette = ReaderPalette.For(_appearance.Theme, PowerSavingMode || _pureBlackBackground);
        _paintTheme = _palette.PaintTheme;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    /// <summary>Repaint only: selection, layout, page index and scroll offset stay intact.</summary>
    public void SetAppearance(ReaderAppearanceSettings appearance)
    {
        appearance = ReaderAppearanceSettings.Normalize(appearance);
        var palette = ReaderPalette.For(appearance.Theme, PowerSavingMode || _pureBlackBackground);
        if (_appearance == appearance && _palette == palette) return;
        _appearance = appearance;
        _palette = palette;
        _paintTheme = _palette.PaintTheme;
        ReaderAppearanceResources.PopulateControls(_scrollBar.Resources, appearance, PowerSavingMode || _pureBlackBackground);
        _bitmapDirty = true;
        InvalidateVisual();
    }

    private double CurrentScaling
    {
        get
        {
            var raw = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            return PowerSavingMode ? Math.Min(2.0, raw) : raw;
        }
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(1, Bounds.Height);
        var scaling = CurrentScaling;
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scaling));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scaling));

        if (_bitmap is null
            || _bitmapPixelWidth != pixelWidth
            || _bitmapPixelHeight != pixelHeight)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Premul);
            _bitmapPixelWidth = pixelWidth;
            _bitmapPixelHeight = pixelHeight;
            _bitmapDirty = true;
        }

        if (_bitmapDirty)
        {
            PaintPageIntoBitmap(pixelWidth, pixelHeight, scaling);
            _bitmapDirty = false;
        }

        // ReaderRoot owns the paper surface. Keep this canvas transparent so
        // its texture continues seamlessly behind the native text and overlays.
        if (_bitmap is not null)
        {
            context.DrawImage(_bitmap, new Rect(0, 0, width, height));
        }
    }
}
