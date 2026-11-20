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
/// Image cache, decoding and visible-image prefetch.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    /// <summary>
    /// Warm the decoded-image cache for the visible page and its neighbour in
    /// the background, so the first paint after a page turn does not block on
    /// a full-size decode.
    /// </summary>
    private void PrefetchVisibleImages()
    {
        if (_disposed) return;
        var manga = _settings.MangaMode;
        if (IsScroll && !manga) return;
        var layout = _layout;
        if (layout is null || layout.Pages.Count == 0) return;

        var currentSlice = IsScroll
            ? Math.Clamp((int)(_scrollOffset / Math.Max(1, Bounds.Height)), 0, layout.Pages.Count - 1)
            : Math.Clamp(_pageIndex, 0, layout.Pages.Count - 1);
        var first = manga ? Math.Max(0, currentSlice - 1) : currentSlice;
        var last = Math.Min(layout.Pages.Count - 1, currentSlice + (manga ? 2 : IsSpread ? 2 : 1));
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = first; index <= last; index++)
        {
            foreach (var image in layout.Pages[index].Images)
            {
                if (!string.IsNullOrWhiteSpace(image.Path) && seen.Add(image.Path))
                    paths.Add(image.Path);
            }
        }
        if (paths.Count == 0) return;

        var warmCrop = manga && _settings.MangaAutoCrop;
        _imagePrefetchCancellation?.Cancel();
        _imagePrefetchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _imagePrefetchCancellation = cancellation;
        _ = Task.Run(() =>
        {
            foreach (var path in paths)
            {
                if (cancellation.IsCancellationRequested) return;
                try
                {
                    using var imagePins = PinImages([path]);
                    ResolveImage(path);
                    if (warmCrop && !cancellation.IsCancellationRequested)
                        ImageCropDetector.GetCachedImageCropBox(path);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }, cancellation.Token);
    }

    private IReadOnlyList<SKRect>? SelectionBandsFor(LayoutPage page)
    {
        if (_selectionStart < 0 || _selectionEnd <= _selectionStart || _layout is null)
        {
            return null;
        }

        return _layout.GetOverlayRects(page.Index, _selectionStart, _selectionEnd - _selectionStart);
    }

    private IReadOnlyList<TypesetAnnotationOverlay>? AnnotationOverlaysFor(LayoutPage page)
    {
        if (_annotations.Count == 0 || _layout is null)
        {
            return null;
        }

        var overlays = new List<TypesetAnnotationOverlay>();
        foreach (var annotation in _annotations)
        {
            if (!TryGetAnnotationRange(annotation, out var start, out var end)
                || end <= start)
            {
                continue;
            }

            var bands = _layout.GetOverlayRects(
                page.Index,
                start,
                end - start);
            if (bands.Count == 0)
            {
                continue;
            }

            overlays.Add(new TypesetAnnotationOverlay
            {
                Bands = bands,
                Style = annotation.UnderlineStyle,
                Color = ParseAnnotationColor(annotation.Color, annotation.UnderlineStyle == "marker"),
            });
        }

        return overlays.Count > 0 ? overlays : null;
    }

    private SKColor ParseAnnotationColor(string? value, bool isMarker)
    {
        if (value is { Length: 7 }
            && value[0] == '#'
            && uint.TryParse(
                value.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var rgb))
        {
            var color = new SKColor(
                (byte)(rgb >> 16),
                (byte)(rgb >> 8),
                (byte)rgb);
            // Marker colors are translucent fills. Preserve black as the
            // legacy inversion sentinel, even when the theme uses light ink.
            if (isMarker) return color;
            // Existing annotations use black ink. Keep that semantic ink
            // readable on every paper, including previously saved notes.
            if (color.Red <= 0x33 && color.Green <= 0x33 && color.Blue <= 0x33)
                return _paintTheme.Text;
            return color;
        }

        return isMarker ? SKColors.Black : _paintTheme.Text;
    }

    private IReadOnlyList<SKRect>? SearchBandsFor(LayoutPage page)
    {
        if (_searchHits is not { Count: > 0 } || _layout is null)
        {
            return null;
        }

        var bands = new List<SKRect>();
        for (var index = 0; index < _searchHits.Count; index++)
        {
            // With a focused hit (Ctrl+F) the current match paints as the
            // black-white inversion and the rest stay on the muted band.
            if (_focusSearchHit is { } focus && index == focus)
            {
                continue;
            }

            bands.AddRange(_layout.GetOverlayRects(page.Index, _searchHits[index].Start, _searchHits[index].Length));
        }

        return bands.Count > 0 ? bands : null;
    }

    private IReadOnlyList<SKRect>? SpeechBandsFor(LayoutPage page)
    {
        if (_speechHighlight is not { } highlight || _layout is null)
        {
            return null;
        }

        var bands = _layout.GetOverlayRects(page.Index, highlight.Start, highlight.Length);
        return bands.Count > 0 ? bands : null;
    }

    /// <summary>
    /// Bands rendered as the black-white inversion: the focused Ctrl+F hit,
    /// or — when no focus exists, as after a whole-book search jump — every
    /// hit on the page.
    /// </summary>
    private IReadOnlyList<SKRect>? FocusedSearchBandsFor(LayoutPage page)
    {
        if (_searchHits is not { Count: > 0 } || _layout is null)
        {
            return null;
        }

        var bands = new List<SKRect>();
        if (_focusSearchHit is { } focus
            && focus >= 0
            && focus < _searchHits.Count)
        {
            bands.AddRange(_layout.GetOverlayRects(
                page.Index,
                _searchHits[focus].Start,
                _searchHits[focus].Length));
            return bands.Count > 0 ? bands : null;
        }

        foreach (var (start, length) in _searchHits)
        {
            bands.AddRange(_layout.GetOverlayRects(page.Index, start, length));
        }

        return bands.Count > 0 ? bands : null;
    }

    // ---- input and bridge protocol ----------------------------------------
}
