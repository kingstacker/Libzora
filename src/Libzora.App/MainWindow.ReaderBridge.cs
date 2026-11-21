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
/// Reader bridge message dispatch from the WebView document.
/// </summary>
public partial class MainWindow
{
    private void HandleReaderBridgeMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            switch (typeElement.GetString())
            {
                case "ready":
                    ResetReaderStatusText();
                    break;
                case "pdfPage":
                    if (_readerIsPdf && root.TryGetProperty("page", out var pdfPage)
                        && pdfPage.TryGetInt32(out var page))
                    {
                        var previousPdfPage = _readerPdfPage;
                        _readerPdfPage = Math.Clamp(page, 1, Math.Max(1, _readerPdfPages.Count));
                        _readerChapterIndex = _readerPdfPage - 1;
                        RememberCurrentPdfPageText();
                        if (_readerPdfPage != previousPdfPage)
                        {
                            HideReaderSelectionPopup();
                            // Continuous scrolling can change the current page
                            // while the edited annotation's page is still visible.
                            // Explicit navigation closes the editor itself.
                            if (!ReaderAnnotationInputPopup.IsOpen)
                            {
                                _selectedReaderAnnotation = null;
                                HideReaderAnnotationHoverPopup();
                            }
                        }
                        ReaderChapterText.Text = GetReaderChapterPositionLabel();
                        UpdateReaderToolbar();
                    }
                    goto case "scroll";
                case "scroll":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    var horizontalScroll = !_readerIsPdf && (IsReaderPaginated || _readerLayout.VerticalWriting);
                    if (!_readerIsPdf)
                    {
                        var reportedFragment = ReadString(root, "fragment").TrimStart('#');
                        try { reportedFragment = Uri.UnescapeDataString(reportedFragment); } catch { }
                        _readerCurrentFragment = string.IsNullOrWhiteSpace(reportedFragment)
                            ? null
                            : reportedFragment;
                    }
                    _readerScrollPosition = horizontalScroll
                        ? ReadDouble(root, "left")
                        : ReadDouble(root, "top");
                    // Vertical writing reports a negative scrollLeft range;
                    // track the distance from the origin so progress, saved
                    // positions and edge detection stay sign-agnostic.
                    if (!_readerIsPdf && _readerLayout.VerticalWriting)
                        _readerScrollPosition = Math.Abs(_readerScrollPosition);
                    _readerScrollWidth = ReadDouble(root, "scrollWidth");
                    _readerScrollHeight = ReadDouble(root, "scrollHeight");
                    _readerClientWidth = ReadDouble(root, "clientWidth");
                    _readerClientHeight = ReadDouble(root, "clientHeight");
                    var max = horizontalScroll
                        ? Math.Max(0, _readerScrollWidth - _readerClientWidth)
                        : Math.Max(0, _readerScrollHeight - _readerClientHeight);
                    _readerScrollRatio = max > 0 ? Math.Clamp(_readerScrollPosition / max, 0, 1) : 0;
                    SyncReaderTocSelectionWithReadingPosition();
                    _ = SaveReaderProgressAfterScrollAsync(++_readerProgressSaveSequence);
                    // The bridge already supplied the exact scroll position
                    // and fragment. Reusing that snapshot avoids issuing a
                    // second WebView script call for every animation frame.
                    UpdateReaderBookmarkIndicatorFromTrackedLocation();
                    break;
                case "selectionPageTurn":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    UpdateReaderToolbar();
                    break;
                case "selection":
                    if (IsLinuxReaderTextFallbackActive())
                        break;
                    _readerPendingSelection = root.TryGetProperty("text", out var selection)
                        ? selection.GetString()
                        : null;
                    _readerPendingSelectionStartOffset = ReadInt(root, "startOffset");
                    _readerPendingSelectionEndOffset = ReadInt(root, "endOffset");
                    _readerPendingSelectionPrefix = ReadString(root, "prefix");
                    _readerPendingSelectionSuffix = ReadString(root, "suffix");
                    if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
                    {
                        _selectedReaderAnnotation = null;
                        if (CurrentReaderHost is IReaderSelectionHost)
                        {
                            Point? placementPoint = null;
                            double? selectionBottom = null;
                            if (root.TryGetProperty("x", out var selectionX)
                                && root.TryGetProperty("y", out var selectionY)
                                && selectionX.TryGetDouble(out var x)
                                && selectionY.TryGetDouble(out var y))
                            {
                                placementPoint = new Point(x, y);
                                if (root.TryGetProperty("bottom", out var bottom)
                                    && bottom.TryGetDouble(out var bottomValue))
                                {
                                    selectionBottom = bottomValue;
                                }

                                // NativeReaderHost is nested in a ContentControl
                                // inside ReaderWebViewHost. Translate its local
                                // selection geometry before placing the popup,
                                // otherwise the bar is offset by the host slot's
                                // margin on resized reader windows.
                                if (CurrentReaderHost.View is Control nativeView
                                    && nativeView.TranslatePoint(new Point(0, 0), ReaderWebViewHost)
                                        is { } nativeOrigin)
                                {
                                    placementPoint = new Point(
                                        placementPoint.Value.X + nativeOrigin.X,
                                        placementPoint.Value.Y + nativeOrigin.Y);
                                    if (selectionBottom is { } translatedBottom)
                                    {
                                        selectionBottom = translatedBottom + nativeOrigin.Y;
                                    }
                                }
                            }

                            // The self-drawn EPUB surface has no DOM action bar;
                            // use the Avalonia popup for the same selection
                            // actions used by the fallback reader.
                            ShowReaderSelectionPopup(placementPoint, selectionBottom);
                        }
                    }
                    else
                    {
                        _readerPendingSelection = null;
                        _readerPendingSelectionStartOffset = 0;
                        _readerPendingSelectionEndOffset = 0;
                        _readerPendingSelectionPrefix = string.Empty;
                        _readerPendingSelectionSuffix = string.Empty;
                        HideReaderSelectionPopup();
                    }
                    break;
                case "selectionAction":
                    DispatchReaderSelectionAction(root);
                    break;
                case "pdfZoom":
                case "mangaZoom":
                    UpdateReaderZoomLabel();
                    break;
                case "pdfLayout":
                    UpdateReaderToolbar();
                    break;
                case "pdfHistory":
                    UpdateReaderPdfNavigationUi();
                    break;
                case "pdfHistoryNavigate":
                    _ = ObserveReaderTaskAsync(NavigateReaderPdfHistoryAsync(ReadInt(root, "direction")));
                    break;
                case "pdfLink":
                    _ = ObserveReaderTaskAsync(FollowReaderPdfLinkAsync(root));
                    break;
                case "pdfLabels":
                    ReaderChapterText.Text = GetReaderChapterPositionLabel();
                    ReaderPdfThumbnailsList.ItemsSource = null;
                    UpdateReaderPdfNavigationUi();
                    break;
                case "pdfError":
                    ReaderStatusText.Text = UiText.Localize(ReadString(root, "message"));
                    break;
                case "annotationSelect":
                    PdfAnnotTrace.Write($"bridge annotationSelect id={ReadString(root, "id")} isPdf={_readerIsPdf}");
                    if (_readerIsPdf
                        && Guid.TryParse(ReadString(root, "id"), out var selectedId))
                    {
                        // Highlight/underline picked with the left button so the
                        // Delete key or a right click can remove it. Selecting
                        // must not open the note editor.
                        (CurrentReaderHost as IPdfAnnotationsHost)?.SelectAnnotation(selectedId);
                        HideReaderSelectionPopup();
                    }
                    break;
                case "annotationClick":
                    if (Guid.TryParse(ReadString(root, "id"), out var annotationId)
                        && ReaderAnnotations.FirstOrDefault(item => item.Id == annotationId) is { } clickedAnnotation)
                    {
                        if (_readerIsPdf)
                        {
                            EditReaderPdfAnnotation(clickedAnnotation);
                            (CurrentReaderHost as IPdfAnnotationsHost)?.SelectAnnotation(clickedAnnotation.Id);
                        }
                        else
                            EditReaderAnnotation(clickedAnnotation);
                    }
                    break;
                case "annotationContextMenu":
                    if (Guid.TryParse(ReadString(root, "id"), out var menuId)
                        && ReaderAnnotations.FirstOrDefault(item => item.Id == menuId) is { } menuAnnotation)
                        ShowReaderAnnotationMenu(menuAnnotation);
                    break;
                case "pdfAnnotationDelete":
                    PdfAnnotTrace.Write($"bridge pdfAnnotationDelete id={ReadString(root, "id")} isPdf={_readerIsPdf}");
                    if (_readerIsPdf && Guid.TryParse(ReadString(root, "id"), out var deleteId)
                        && ReaderAnnotations.FirstOrDefault(item => item.Id == deleteId) is { } deleteAnnotation)
                        _ = DeleteReaderAnnotationAsync(deleteAnnotation);
                    break;
                case "annotationBlankClick":
                    if (_readerIsPdf)
                        (CurrentReaderHost as IPdfAnnotationsHost)?.ClearAnnotationSelection();
                    HideReaderAnnotationHoverPopup();
                    break;
                case "pdfPointAnnotation":
                    if (_readerIsPdf
                        && root.TryGetProperty("page", out var pointPage)
                        && pointPage.TryGetInt32(out var pointPageNumber)
                        && root.TryGetProperty("pageX", out var pageX)
                        && root.TryGetProperty("pageY", out var pageY)
                        && root.TryGetProperty("x", out var hostX)
                        && root.TryGetProperty("y", out var hostY)
                        && pageX.TryGetDouble(out var normalizedX)
                        && pageY.TryGetDouble(out var normalizedY)
                        && hostX.TryGetDouble(out var localX)
                        && hostY.TryGetDouble(out var localY))
                    {
                        BeginReaderPdfPointAnnotation(
                            pointPageNumber,
                            normalizedX,
                            normalizedY,
                            localX,
                            localY);
                    }
                    break;
                case "link":
                    if (root.TryGetProperty("href", out var href))
                    {
                        var showFootnote = root.TryGetProperty("footnote", out var footnote)
                            && footnote.ValueKind == JsonValueKind.True;
                        var hrefText = href.GetString() ?? string.Empty;
                        var navigateFootnote = showFootnote
                            && root.TryGetProperty("navigate", out var navigate)
                            && navigate.ValueKind == JsonValueKind.True;
                        if (navigateFootnote)
                        {
                            _ = ObserveReaderTaskAsync(HandleReaderLinkAsync(hrefText, true));
                        }
                        else if (showFootnote && CurrentReaderHost is IReaderFootnoteVisualHost)
                        {
                            Point? placementPoint = null;
                            if (root.TryGetProperty("x", out var linkX)
                                && root.TryGetProperty("y", out var linkY)
                                && linkX.TryGetDouble(out var x)
                                && linkY.TryGetDouble(out var y))
                            {
                                placementPoint = new Point(Math.Max(0, x), Math.Max(0, y));
                            }

                            var directFootnoteText = ReadString(root, "footnoteText");
                            if (!string.IsNullOrWhiteSpace(directFootnoteText))
                            {
                                ShowDirectReaderFootnote(
                                    hrefText,
                                    directFootnoteText,
                                    placementPoint);
                            }
                            else
                            {
                                // Native footnote definitions are kept in the
                                // offset stream but are not composed. Resolve
                                // the definition here so click and hover both
                                // show the same popup content.
                                _ = ObserveReaderTaskAsync(
                                    HandleReaderFootnoteHoverAsync(
                                        hrefText,
                                        isFootnote: true,
                                        placementPoint: placementPoint));
                            }
                        }
                        else
                        {
                            _ = ObserveReaderTaskAsync(HandleReaderLinkAsync(hrefText, showFootnote));
                        }
                    }
                    break;
                case "page":
                    // PDF arrow keys turn physical pages in continuous mode too.
                    if ((_readerIsPdf || IsReaderPaginated)
                        && root.TryGetProperty("direction", out var pageDirection)
                        && pageDirection.TryGetInt32(out var pageTurnDirection))
                    {
                        pageTurnDirection = Math.Sign(pageTurnDirection);
                        if (pageTurnDirection != 0)
                            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(pageTurnDirection));
                    }
                    break;
                case "pageClick":
                    if (!_readerIsPdf && (_readerLayout.FlowMode == 1 || IsNativeReaderPaginated)
                        && root.TryGetProperty("side", out var pageSide))
                    {
                        var side = pageSide.GetString();
                        if (side is "left" or "right")
                        {
                            var clickTurnDirection = ReaderPaginationPolicy.GetClickDirection(
                                side == "left",
                                IsReaderRightToLeftPaging);
                            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(clickTurnDirection));
                        }
                    }
                    break;
                case "bookmarkToggle":
                    _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
                    break;
                case "imageZoom":
                {
                    var imagePath = ReadString(root, "path");
                    if (!_readerIsPdf && _readerDocument is not null
                        && !string.IsNullOrWhiteSpace(imagePath)
                        && IsPathInside(_readerDocument.RootPath, imagePath))
                    {
                        OpenReaderImageZoom(imagePath);
                    }
                    break;
                }
                case "footnoteHover":
                    if (root.TryGetProperty("href", out var footnoteHref))
                    {
                        Point? placementPoint = null;
                        if (root.TryGetProperty("x", out var footnoteX)
                            && root.TryGetProperty("y", out var footnoteY)
                            && footnoteX.TryGetDouble(out var x)
                            && footnoteY.TryGetDouble(out var y))
                        {
                                placementPoint = new Point(Math.Max(0, x), Math.Max(0, y));
                        }
                        var hrefText = footnoteHref.GetString() ?? string.Empty;
                        var directFootnoteText = ReadString(root, "footnoteText");
                        if (!string.IsNullOrWhiteSpace(directFootnoteText))
                        {
                            ShowDirectReaderFootnote(hrefText, directFootnoteText, placementPoint);
                        }
                        else
                        {
                            _ = ObserveReaderTaskAsync(
                                HandleReaderFootnoteHoverAsync(
                                    hrefText,
                                    true,
                                    placementPoint));
                        }
                    }
                    break;
                case "footnoteLeave":
                    // Opening the native Popup above WebView2 can itself make
                    // Chromium report pointerout. On Windows the screen-space
                    // hover poll is authoritative and dismisses after the
                    // pointer has genuinely left the marker.
                    if (!OperatingSystem.IsWindows() || IsNativeReaderPaginated)
                        HideReaderFootnotePopup();
                    break;
                case "annotationHover":
                {
                    Point? annotationPlacementPoint = null;
                    if (root.TryGetProperty("x", out var annotationX)
                        && root.TryGetProperty("y", out var annotationY)
                        && annotationX.TryGetDouble(out var annotationPx)
                        && annotationY.TryGetDouble(out var annotationPy))
                    {
                        annotationPlacementPoint = new Point(Math.Max(0, annotationPx), Math.Max(0, annotationPy));
                    }
                    var hoveredAnnotation = Guid.TryParse(ReadString(root, "id"), out var hoveredAnnotationId)
                        ? ReaderAnnotations.FirstOrDefault(item => item.Id == hoveredAnnotationId)
                        : null;
                    ShowReaderAnnotationHoverPopup(
                        ReadString(root, "note"),
                        annotationPlacementPoint,
                        hoveredAnnotation);
                    break;
                }
                case "annotationLeave":
                    // The native host emits this from Avalonia pointer events,
                    // which stay authoritative regardless of platform.
                    var hoverSequence = _readerAnnotationHoverSequence;
                    // Let the pointer cross the gap into the scrollable preview.
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (hoverSequence == _readerAnnotationHoverSequence
                            && !ReaderAnnotationHoverPopup.IsPointerOver)
                            HideReaderAnnotationHoverPopup();
                    }, TimeSpan.FromMilliseconds(150));
                    break;
                case "resize":
                    ScheduleReaderRelayout();
                    break;
                case "wheel":
                    // Paginated EPUB pages and the PDF text view translate the
                    // vertical wheel into page turns, mirroring the WinUI
                    // reference's low-level mouse hook (120 units per page,
                    // direction flips reset the remainder, and the browser is
                    // told to ignore the event so it never double-scrolls).
                    if (IsReaderPaginated
                        && root.TryGetProperty("deltaY", out var wheel))
                    {
                        var delta = (int)Math.Round(wheel.GetDouble());
                        if (delta != 0)
                        {
                            if (_readerWheelDeltaRemainder != 0
                                && Math.Sign(_readerWheelDeltaRemainder) != Math.Sign(delta))
                            {
                                _readerWheelDeltaRemainder = 0;
                            }
                            _readerWheelDeltaRemainder += delta;
                            if (Math.Abs(_readerWheelDeltaRemainder) >= 120)
                            {
                                var direction = _readerWheelDeltaRemainder > 0 ? 1 : -1;
                                _readerWheelDeltaRemainder %= 120;
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                            }
                        }
                    }
                    break;
                case "continuousEdge":
                    if (!_readerIsPdf
                        && _readerLayout.FlowMode == 0
                        && !IsNativeReaderPaginated
                        && root.TryGetProperty("direction", out var edgeDirection)
                        && edgeDirection.TryGetInt32(out var continuousDirection))
                    {
                        TryMoveReaderChapterFromContinuousEdge(Math.Sign(continuousDirection));
                    }
                    break;
                case "pointermove":
                    // Zen chrome wake-up for pointer movement over the webview
                    // (an HWND island whose events never reach Avalonia). The
                    // page throttles the reports; keep a host-side tick guard
                    // so a burst of messages cannot restart the timer faster
                    // than the 80 ms window.
                    if (_readerZenMode && !OperatingSystem.IsWindows())
                    {
                        var moveNow = Environment.TickCount64;
                        if (moveNow - _readerZenLastMouseMoveTick > 80)
                        {
                            _readerZenLastMouseMoveTick = moveNow;
                            if (root.TryGetProperty("x", out var pointerX)
                                && root.TryGetProperty("y", out var pointerY)
                                && root.TryGetProperty("width", out var surfaceWidth))
                            {
                                UpdateReaderZenChromeForPointer(
                                    pointerX.GetDouble(),
                                    pointerY.GetDouble(),
                                    surfaceWidth.GetDouble());
                            }
                            else if (!_readerZenChromeVisible)
                            {
                                UpdateReaderZenChrome(visible: true);
                            }
                            else
                            {
                                RestartReaderZenChromeHideTimer();
                            }
                        }
                    }
                    break;
                case "key":
                    if (root.TryGetProperty("key", out var key))
                    {
                        var keyName = key.GetString();
                        if (CurrentReaderHost is IReaderLocationHost nativeReader
                            && keyName is "Home" or "End")
                        {
                            nativeReader.SeekToBoundary(string.Equals(keyName, "End", StringComparison.Ordinal));
                            _ = ObserveReaderTaskAsync(UpdateReaderScrollStateAsync(nativeReader));
                            break;
                        }
                        if (IsReaderPaginated)
                        {
                            // Single-page and two-column EPUB layouts share the
                            // same key map: up/down change chapters while
                            // left/right turn pages. PDF keeps all arrows as
                            // page turns.
                            var chapterDirection = !_readerIsPdf
                                ? string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                    ? -1
                                    : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                        ? 1
                                        : 0
                                : 0;
                            if (chapterDirection != 0)
                            {
                                _ = ObserveReaderTaskAsync(
                                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                                break;
                            }
                            // Vertical writing mirrors the horizontal page
                            // order like classical Chinese books: left turns
                            // forward and right turns backward. Up/down and
                            // PageUp/PageDown keep their horizontal meaning.
                            var verticalPageOrder = IsReaderRightToLeftPaging;
                            var direction = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                ? (verticalPageOrder ? 1 : -1)
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    ? (verticalPageOrder ? -1 : 1)
                                    : string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                        || string.Equals(keyName, "PageUp", StringComparison.Ordinal)
                                        ? -1
                                        : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                            || string.Equals(keyName, "PageDown", StringComparison.Ordinal)
                                            ? 1
                                            : 0;
                            if (direction != 0)
                                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                        }
                        else if (_readerLayout.FlowMode == 0
                            && !IsNativeReaderPaginated
                            && _readerDocument is not null)
                        {
                            // Continuous mode (WinUI reference): left/right own
                            // chapter navigation; up/down scroll smoothly and
                            // stop at chapter edges instead of advancing.
                            var chapterDirection = string.Equals(keyName, "ArrowLeft", StringComparison.Ordinal)
                                ? -1
                                : string.Equals(keyName, "ArrowRight", StringComparison.Ordinal)
                                    ? 1
                                    : 0;
                            if (chapterDirection != 0)
                            {
                                _ = ObserveReaderTaskAsync(
                                    TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                            }
                            else
                            {
                                var scrollDirection = string.Equals(keyName, "ArrowUp", StringComparison.Ordinal)
                                    ? -1
                                    : string.Equals(keyName, "ArrowDown", StringComparison.Ordinal)
                                        ? 1
                                        : 0;
                                if (scrollDirection != 0)
                                    _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                            }
                        }
                    }
                    break;
                case "shortcut":
                    HandleReaderBridgeShortcut(
                        ReadString(root, "key"),
                        root.TryGetProperty("ctrlKey", out var ctrlKey)
                            && ctrlKey.ValueKind == JsonValueKind.True);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    // The selection bar now lives inside the reader page (the webview is a
    // native HWND island Avalonia cannot paint over), so its buttons arrive as
    // bridge messages instead of XAML clicks. Each action mirrors the WinUI
    // reference's selection-bar handlers.
}
