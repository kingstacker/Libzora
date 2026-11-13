using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Drives the colourful wash from the covers that are actually on screen. The
/// library palette is rebuilt after scrolling settles, while pages without a
/// shelf (settings, account centre, reader) reuse a small fallback palette
/// sampled from the most recently updated books.
/// </summary>
public partial class MainWindow
{
    private const int MaximumAmbientRegions = 18;
    private const int MaximumFallbackCovers = 4;

    private readonly DispatcherTimer _ambientRefreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };
    private CancellationTokenSource? _ambientRefreshCancellation;
    private int _ambientGeneration;
    private IReadOnlyList<AmbientColor>? _ambientFallbackColors;
    private bool _ambientInitialized;
    // Runtime state of the toggle; persistence follows through the normal
    // debounced settings save, so the wash reacts immediately.
    private bool _colorfulModeActive;

    private void InitializeAmbientBackground()
    {
        if (_ambientInitialized) return;
        _ambientInitialized = true;
        _ambientRefreshTimer.Tick += (_, _) =>
        {
            _ambientRefreshTimer.Stop();
            _ = RefreshAmbientColorsAsync();
        };
        ReaderAmbientBackdrop.WashOpacity = ReaderAmbientWashOpacity(CurrentReaderAppearance.Theme);
        UpdateColorfulMode(_appSettings.ColorfulMode);
    }

    private void UpdateColorfulMode(bool enabled, bool forceRefresh = false)
    {
        _widgetSnapshots?.Schedule();
        if (!_ambientInitialized) return;
        _colorfulModeActive = enabled;
        if (!enabled)
        {
            _ambientRefreshTimer.Stop();
            _ambientRefreshCancellation?.Cancel();
            _ambientRefreshCancellation?.Dispose();
            _ambientRefreshCancellation = null;
            _ambientGeneration++;
            PublishAmbientWash([]);
            return;
        }
        if (forceRefresh || AmbientBackdrop.TargetColors.Count == 0)
            ScheduleAmbientRefresh(immediate: true);
    }

    private void UpdateReaderAmbientOpacity()
    {
        ReaderAmbientBackdrop.WashOpacity = ReaderAmbientWashOpacity(CurrentReaderAppearance.Theme);
    }

    // The reader stays much softer than the library. Avalonia adds each
    // brush's alpha as it paints, so these numbers are below the iOS
    // group-opacity values; the effective tint still matches the phone.
    private static double ReaderAmbientWashOpacity(ReaderTheme theme) =>
        theme == ReaderTheme.Ink ? 0.10 : 0.07;

    private void InvalidateAmbientFallback() => _ambientFallbackColors = null;

    /// <summary>Coalesces scroll and layout churn into one sampling pass.</summary>
    private void ScheduleAmbientRefresh(bool immediate = false)
    {
        if (!_ambientInitialized || !_colorfulModeActive) return;
        if (ReaderRoot.IsVisible) return;
        _ambientRefreshTimer.Stop();
        if (immediate)
        {
            _ = RefreshAmbientColorsAsync();
            return;
        }
        _ambientRefreshTimer.Start();
    }

    private async Task RefreshAmbientColorsAsync()
    {
        if (!_ambientInitialized || !_colorfulModeActive || ReaderRoot.IsVisible) return;
        var generation = ++_ambientGeneration;
        var previous = _ambientRefreshCancellation;
        previous?.Cancel();
        previous?.Dispose();
        var cancellation = new CancellationTokenSource();
        _ambientRefreshCancellation = cancellation;
        try
        {
            var regions = CollectAmbientRegions();
            if (regions.Count == 0)
            {
                await PublishAmbientFallbackAsync(cancellation.Token);
                return;
            }

            var samples = await Task.WhenAll(
                regions.Select(region => SampleRegionAsync(region, cancellation.Token)));
            if (cancellation.IsCancellationRequested || generation != _ambientGeneration) return;
            var available = samples
                .Where(sample => sample is not null)
                .Select(sample => sample!.Value)
                .ToArray();
            var vivid = available.Where(sample => sample.IsVivid).ToArray();
            var effective = vivid.Length > 0 ? vivid : available;
            if (effective.Length == 0)
            {
                await PublishAmbientFallbackAsync(cancellation.Token);
                return;
            }
            PublishAmbientWash(AmbientPalette.RegionalColors(
                effective.Select(sample => sample.Sample).ToArray()));
        }
        catch (OperationCanceledException)
        {
            // A newer scroll position superseded this pass.
        }
        catch (Exception)
        {
            // Decorative: a bad cover or a shutdown race must not surface.
        }
        finally
        {
            if (ReferenceEquals(_ambientRefreshCancellation, cancellation))
                _ambientRefreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private List<AmbientRegion> CollectAmbientRegions()
    {
        var regions = new List<AmbientRegion>();
        if (!LibraryRoot.IsVisible || !LibraryWorkspace.IsVisible) return regions;
        Control? activeList = BookGrid.IsVisible ? BookGrid : BookList.IsVisible ? BookList : null;
        if (activeList is null || !activeList.IsEffectivelyVisible) return regions;
        var origin = activeList.TranslatePoint(default, AmbientBackdrop);
        if (origin is null) return regions;
        var width = AmbientBackdrop.Bounds.Width;
        var height = AmbientBackdrop.Bounds.Height;
        if (width <= 0 || height <= 0) return regions;
        var viewport = new Rect(activeList.Bounds.Size);

        foreach (var (control, card) in _bookCardControls)
        {
            if (!control.IsEffectivelyVisible) continue;
            var cover = control.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(border => border.Classes.Contains("ambientCover"));
            if (cover is null || !cover.IsEffectivelyVisible) continue;
            var position = cover.TranslatePoint(default, activeList);
            if (position is null) continue;
            var coverBounds = new Rect(position.Value, cover.Bounds.Size);
            var visible = coverBounds.Intersect(viewport);
            if (visible.Width <= 1 || visible.Height <= 1) continue;
            var area = coverBounds.Width * coverBounds.Height;
            if (area <= 0) continue;
            var visibility = visible.Width * visible.Height / area;
            if (visibility < 0.1) continue;

            // Fields are anchored to the whole wash surface; map the cover
            // centre into that space instead of the scrolling viewport.
            var centerX = origin.Value.X + coverBounds.Center.X;
            var centerY = origin.Value.Y + coverBounds.Center.Y;
            var column = Math.Clamp((int)(centerX / width * 3), 0, 2);
            var row = Math.Clamp((int)(centerY / height * 3), 0, 2);
            regions.Add(new AmbientRegion(card, column, row, visibility));
        }

        return regions.Count <= MaximumAmbientRegions
            ? regions
            : regions.OrderByDescending(region => region.Visibility)
                .Take(MaximumAmbientRegions)
                .ToList();
    }

    private async Task<AmbientRegionSample?> SampleRegionAsync(
        AmbientRegion region, CancellationToken cancellationToken)
    {
        var path = ResolveBookCoverAbsolutePath(region.Card.Book);
        if (path is null) return null;
        var selection = await AmbientCoverSampler.LoadAsync(path, cancellationToken);
        return selection is { } value
            ? new AmbientRegionSample(
                new AmbientSample(value.Color, region.Column, region.Row, region.Visibility),
                value.IsVivid)
            : null;
    }

    private async Task PublishAmbientFallbackAsync(CancellationToken cancellationToken)
    {
        var colors = _ambientFallbackColors;
        if (colors is null)
        {
            var paths = ViewModel.LibraryBooks
                .OrderByDescending(book => book.UpdatedAt)
                .Select(ResolveBookCoverAbsolutePath)
                .Where(path => path is not null)
                .Take(MaximumFallbackCovers)
                .Select(path => path!)
                .ToArray();
            var selections = await Task.WhenAll(
                paths.Select(path => AmbientCoverSampler.LoadAsync(path, cancellationToken)));
            if (cancellationToken.IsCancellationRequested) return;
            var vivid = selections
                .Where(selection => selection is { IsVivid: true })
                .Select(selection => selection!.Value.Color)
                .ToArray();
            var seeds = vivid.Length > 0
                ? vivid
                : selections
                    .Where(selection => selection is not null)
                    .Select(selection => selection!.Value.Color)
                    .Take(MaximumFallbackCovers)
                    .ToArray();
            colors = AmbientPalette.ExpandFallback(seeds);
            _ambientFallbackColors = colors;
        }
        if (!cancellationToken.IsCancellationRequested) PublishAmbientWash(colors);
    }

    private void PublishAmbientWash(IReadOnlyList<AmbientColor> colors)
    {
        AmbientBackdrop.SetWash(colors);
        ReaderAmbientBackdrop.SetWash(colors);
    }

    private sealed record AmbientRegion(
        BookCardViewModel Card, int Column, int Row, double Visibility);

    private readonly record struct AmbientRegionSample(AmbientSample Sample, bool IsVivid);
}
