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
/// Footnote links, hover popovers and annotation hover.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> HandleReaderLinkAsync(string href, bool showFootnote = false)
    {
        _readerFootnoteHoverSequence++;
        if (_readerDocument is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || !uri.IsFile)
            return false;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path))
            return false;

        var match = _readerDocument.Chapters
            .Select((chapter, index) => (chapter, index))
            .FirstOrDefault(item => string.Equals(Path.GetFullPath(item.chapter), path, StringComparison.OrdinalIgnoreCase));
        if (match.chapter is null)
            return false;
        var chapterIndex = match.index;
        HideReaderFootnotePopup();
        if (!showFootnote
            && chapterIndex == _readerChapterIndex
            && !string.IsNullOrWhiteSpace(uri.Fragment)
            && CurrentReaderHost is NativeReaderHost nativeHost
            && nativeHost.TryGetFragmentMediaTarget(uri.Fragment, out _, out var isOnVisiblePage, out var imagePath, out _)
            && isOnVisiblePage
            && !string.IsNullOrWhiteSpace(imagePath)
            && IsPathInside(_readerDocument.RootPath, imagePath))
        {
            OpenReaderImageZoom(imagePath);
            return true;
        }

        var item = new EpubReaderNavigationItem(
            T("第 {0} 章", chapterIndex + 1),
            uri.AbsoluteUri,
            chapterIndex);
        return await NavigateToReaderItemAsync(
            item,
            _readerSession.GetTokenOr(CancellationToken.None),
            showFootnote ? ReaderNavigationIntent.Footnote : ReaderNavigationIntent.Link);
    }

    private async Task HandleReaderFootnoteHoverAsync(
        string href,
        bool isFootnote,
        Point? placementPoint = null)
    {
        if (!isFootnote
            || _readerDocument is null
            || !Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrWhiteSpace(uri.Fragment))
            return;
        if (OperatingSystem.IsWindows() && GetCursorPos(out var cursor))
            _readerFootnoteAnchorScreenPoint = new PixelPoint(cursor.X, cursor.Y);
        var sequence = ++_readerFootnoteHoverSequence;
        var path = Path.GetFullPath(uri.LocalPath);
        if (!IsPathInside(_readerDocument.RootPath, path)) return;

        var targets = await _footnotes.ResolveAsync(
            _readerDocument.RootPath,
            [uri.AbsoluteUri],
            ReaderToken);
        if (sequence == _readerFootnoteHoverSequence
            && targets.TryGetValue(EpubFootnoteResolver.NormalizeTargetKey(uri.AbsoluteUri), out var footnote))
        {
            _readerFootnoteHref = uri.AbsoluteUri;
            ShowReaderFootnotePopup(footnote, placementPoint);
        }
    }

    private void ShowDirectReaderFootnote(
        string href,
        string text,
        Point? placementPoint)
    {
        // Some reader exports encode the complete note in an image's alt
        // attribute and have no target XHTML fragment to resolve. Keep the
        // synthetic href as the hover identity, but bypass the URI resolver.
        _readerFootnoteHoverSequence++;
        _readerFootnoteHref = href;
        ShowReaderFootnotePopup(text, placementPoint);
    }

    private void StartReaderFootnoteHoverPoll()
    {
        if (!OperatingSystem.IsWindows()) return;
        _readerFootnoteHoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _readerFootnoteHoverTimer.Stop();
        _readerFootnoteHoverTimer.Tick -= ReaderFootnoteHoverTimer_Tick;
        _readerFootnoteHoverTimer.Tick += ReaderFootnoteHoverTimer_Tick;
        _readerFootnoteHoverTimer.Start();
    }

    private void StopReaderFootnoteHoverPoll()
    {
        _readerFootnoteHoverTimer?.Stop();
        _readerFootnotePollRunning = false;
        _readerFootnoteHoverMissCount = 0;
        _readerFootnoteHref = null;
        _readerFootnotePlacementPoint = null;
        _readerFootnoteAnchorScreenPoint = null;
        HideReaderFootnotePopup();
    }

    private async void ReaderFootnoteHoverTimer_Tick(object? sender, EventArgs e)
    {
        if (_readerFootnotePollRunning) return;
        _readerFootnotePollRunning = true;
        try
        {
            await PollReaderFootnoteHoverAsync();
        }
        finally
        {
            _readerFootnotePollRunning = false;
        }
    }

    private async Task PollReaderFootnoteHoverAsync()
    {
        // NativeReaderHost reports exact layout hot-zones from Avalonia
        // pointer events. Its script probe is intentionally a no-op, so a
        // Windows polling tick must not dismiss a popup opened by that host.
        if (CurrentReaderHost is IReaderFootnoteVisualHost)
            return;

        if (_readerIsPdf
            || !OperatingSystem.IsWindows()
            || !ReaderRoot.IsVisible
            || ReaderLayoutSettingsPopup.IsOpen
            || !GetCursorPos(out var cursor))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var windowScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (ReaderFootnoteHostPopup.IsOpen
            && _readerFootnoteAnchorScreenPoint is { } anchor
            && Math.Abs(cursor.X - anchor.X) <= 24 * windowScaling
            && Math.Abs(cursor.Y - anchor.Y) <= 24 * windowScaling)
        {
            _readerFootnoteHoverMissCount = 0;
            return;
        }

        if (CurrentReaderHost is not { View: Control view } host)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        var relativeX = cursor.X - topLeft.X;
        var relativeY = cursor.Y - topLeft.Y;
        if (relativeX < 0 || relativeX >= width || relativeY < 0 || relativeY >= height)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        string? result;
        try
        {
            result = await host.InvokeScriptAsync(
                CreateReaderFootnoteHoverProbeScript(relativeX, relativeY, width, height));
        }
        catch
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        var raw = DecodeReaderScriptString(result);
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        string href;
        try
        {
            using var document = JsonDocument.Parse(raw);
            href = ReadString(document.RootElement, "href");
        }
        catch (JsonException)
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }

        if (string.IsNullOrWhiteSpace(href))
        {
            RegisterReaderFootnoteHoverMiss();
            return;
        }
        _readerFootnoteHoverMissCount = 0;
        if (ReaderFootnoteHostPopup.IsOpen
            && string.Equals(_readerFootnoteHref, href, StringComparison.Ordinal))
        {
            return;
        }

        await HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            placementPoint: new Point(relativeX / scaling, relativeY / scaling));
    }

    private static string CreateReaderFootnoteHoverProbeScript(
        double relativeX,
        double relativeY,
        double hostWidth,
        double hostHeight)
    {
        var x = relativeX.ToString("0.###", CultureInfo.InvariantCulture);
        var y = relativeY.ToString("0.###", CultureInfo.InvariantCulture);
        var width = hostWidth.ToString("0.###", CultureInfo.InvariantCulture);
        var height = hostHeight.ToString("0.###", CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const root = document.documentElement;
              const vw = root.clientWidth || document.body?.clientWidth || window.innerWidth || 0;
              const vh = root.clientHeight || document.body?.clientHeight || window.innerHeight || 0;
              if (!vw || !vh) return null;
              const x = Math.max(0, Math.min(vw - 1, Math.round({{x}} * vw / {{width}})));
              const y = Math.max(0, Math.min(vh - 1, Math.round({{y}} * vh / {{height}})));
              const element = document.elementFromPoint(x, y);
              const anchor = element?.closest?.('a');
              if (!anchor || !isFootnoteLink(anchor)) return null;
              let url;
              try {
                const href = anchor.getAttribute('href')
                  || anchor.getAttribute('data-libzora-footnote-href')
                  || '';
                url = new URL(href, location.href);
              }
              catch { return null; }
              if (!url.hash) return null;
              return JSON.stringify({ href: url.href });
            })();
            """;
    }

    private void ShowReaderFootnotePopup(string text, Point? placementPoint = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideReaderFootnotePopup();
            return;
        }

        ReaderFootnoteText.Text = text;
        _readerFootnoteHoverMissCount = 0;
        ReaderFootnotePopup.IsVisible = true;
        if (placementPoint is { } point)
            _readerFootnotePlacementPoint = point;
        var placementTarget = IsLinuxReaderTextFallbackActive()
            ? ReaderWebViewHost
            : CurrentReaderHost?.View as Control;
        if (placementTarget is { } view
            && _readerFootnotePlacementPoint is { } anchor)
        {
            ReaderFootnoteHostPopup.PlacementTarget = view;
            ReaderFootnoteHostPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderFootnoteHostPopup.PlacementRect = new Rect(anchor.X, anchor.Y, 1, 1);
            ReaderFootnoteHostPopup.PlacementAnchor =
                Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            ReaderFootnoteHostPopup.PlacementGravity =
                Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            ReaderFootnoteHostPopup.PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
                | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY;
        }
        else
        {
            ReaderFootnoteHostPopup.PlacementTarget = ReaderWebViewHost;
            ReaderFootnoteHostPopup.Placement = Avalonia.Controls.PlacementMode.Pointer;
        }
        ReaderFootnoteHostPopup.HorizontalOffset = 12;
        ReaderFootnoteHostPopup.VerticalOffset = 14;
        ReaderFootnoteHostPopup.IsOpen = true;
    }

    private void RegisterReaderFootnoteHoverMiss()
    {
        if (!ReaderFootnoteHostPopup.IsOpen || ++_readerFootnoteHoverMissCount >= 2)
            HideReaderFootnotePopup();
    }

    private Point? _readerAnnotationHoverPlacementPoint;
    private Guid? _readerAnnotationHoverAnnotationId;
    private int _readerAnnotationHoverSequence;

    private void ShowReaderAnnotationHoverPopup(
        string note,
        Point? placementPoint,
        ReaderAnnotation? annotation = null,
        bool fromAnnotationClick = false)
    {
        if (_readerIsPdf && ReaderAnnotationInputPopup.IsOpen) return;
        if (_readerIsPdf
            && (annotation is null || !TryGetReaderPdfPage(annotation.ChapterPath, out _))) return;
        ReaderAnnotationHoverNote.Text = note ?? string.Empty;
        ReaderAnnotationHoverHeading.IsVisible = false;
        if (ReaderAnnotationAccentBrushConverter.TryGetHighlightBrush(annotation, isReaderTheme: true, out var accentBrush))
        {
            ReaderAnnotationHoverAccentBar.Background = accentBrush;
            ReaderAnnotationHoverAccentBar.IsVisible = true;
        }
        else
        {
            ReaderAnnotationHoverAccentBar.IsVisible = false;
        }
        _readerAnnotationHoverSequence++;
        ReaderAnnotationHoverNote.IsVisible = !string.IsNullOrWhiteSpace(note);
        _readerAnnotationHoverAnnotationId = annotation?.Id;
        ReaderAnnotationHoverHostPopup.IsHitTestVisible = true;
        ReaderAnnotationHoverPopup.IsHitTestVisible = true;
        if (placementPoint is { } point)
            _readerAnnotationHoverPlacementPoint = point;
        ReaderAnnotationHoverPopup.IsVisible = true;
        var measureHeight = ReaderAnnotationCardLayout.CreateMeasure(ReaderAnnotationHoverPopup, ReaderAnnotationHoverNote);
        var viewport = (CurrentReaderHost?.View as Control)?.Bounds.Size ?? ReaderWebViewHost.Bounds.Size;
        var bands = annotation is null ? [] : CurrentReaderHost is IReaderAnnotationBoundsHost boundsHost
            ? boundsHost.GetAnnotationBounds(annotation)
            : Array.Empty<Rect>();
        var visibleBands = bands.Select(rect => rect.Intersect(new Rect(viewport)))
            .Where(rect => rect.Width > 0 && rect.Height > 0).ToArray();
        var quote = visibleBands.Length > 0
            ? visibleBands.Aggregate((left, right) => left.Union(right))
            : new Rect(_readerAnnotationHoverPlacementPoint ?? new Point(8, 8), new Size(1, 1));
        var cardBounds = ReaderAnnotationCardLayout.Place(quote, viewport, measureHeight);
        ReaderAnnotationHoverPopup.Width = cardBounds.Width;
        ReaderAnnotationHoverPopup.Height = cardBounds.Height;
        if (CurrentReaderHost?.View is Control view)
        {
            ReaderAnnotationHoverHostPopup.PlacementTarget = view;
            ReaderAnnotationHoverHostPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderAnnotationHoverHostPopup.PlacementRect = new Rect(cardBounds.Position, new Size(1, 1));
            ReaderAnnotationHoverHostPopup.PlacementAnchor =
                Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            ReaderAnnotationHoverHostPopup.PlacementGravity =
                Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            ReaderAnnotationHoverHostPopup.PlacementConstraintAdjustment =
                Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None;
        }
        else
        {
            ReaderAnnotationHoverHostPopup.PlacementTarget = ReaderWebViewHost;
            ReaderAnnotationHoverHostPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderAnnotationHoverHostPopup.PlacementRect = new Rect(cardBounds.Position, new Size(1, 1));
            ReaderAnnotationHoverHostPopup.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            ReaderAnnotationHoverHostPopup.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            ReaderAnnotationHoverHostPopup.PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None;
        }
        ReaderAnnotationHoverHostPopup.HorizontalOffset = 0;
        ReaderAnnotationHoverHostPopup.VerticalOffset = 0;
        ReaderAnnotationHoverHostPopup.IsOpen = true;
    }

    private void HideReaderAnnotationHoverPopup()
    {
        _readerAnnotationHoverSequence++;
        if (!ReaderAnnotationInputPopup.IsOpen && CurrentReaderHost is IPdfAnnotationVisualHost pdf)
            pdf.ClearAnnotationCallout();
        ReaderAnnotationHoverHostPopup.IsOpen = false;
        ReaderAnnotationHoverPopup.IsVisible = false;
        ReaderAnnotationHoverAccentBar.IsVisible = false;
        ReaderAnnotationHoverNote.Text = string.Empty;
        _readerAnnotationHoverPlacementPoint = null;
        _readerAnnotationHoverAnnotationId = null;
        ReaderAnnotationHoverHostPopup.IsHitTestVisible = false;
        ReaderAnnotationHoverPopup.IsHitTestVisible = false;
    }

    private void ReaderAnnotationHoverNote_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readerAnnotationHoverAnnotationId is not { } annotationId
            || ReaderAnnotations.FirstOrDefault(item => item.Id == annotationId) is not { } annotation)
            return;

        var properties = e.GetCurrentPoint(ReaderAnnotationHoverNote).Properties;
        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            ShowReaderAnnotationMenu(annotation);
        }
        else if (_readerIsPdf && properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            EditReaderPdfAnnotation(annotation);
            (CurrentReaderHost as IPdfAnnotationsHost)?.SelectAnnotation(annotation.Id);
        }
    }

    private void ReaderAnnotationHoverPopup_PointerExited(object? sender, PointerEventArgs e)
    {
        HideReaderAnnotationHoverPopup();
    }

    private void ReaderAnnotationHoverCloseButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideReaderAnnotationHoverPopup();
    }

    private void HideReaderFootnotePopup()
    {
        // Invalidate a resolver that is still awaiting the note text. This
        // prevents a popup from reappearing after the pointer has left.
        _readerFootnoteHoverSequence++;
        ReaderFootnoteHostPopup.IsOpen = false;
        ReaderFootnotePopup.IsVisible = false;
        ReaderFootnoteText.Text = string.Empty;
        _readerFootnotePlacementPoint = null;
        _readerFootnoteHref = null;
        _readerFootnoteAnchorScreenPoint = null;
        _readerFootnoteHoverMissCount = 0;
    }

    // Baseline reader status: the bridge and host navigation both restore it.
    private void ResetReaderStatusText()
    {
        ReaderStatusText.Text = string.Empty;
    }
}
