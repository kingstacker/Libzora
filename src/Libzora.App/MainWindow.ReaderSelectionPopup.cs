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
/// Selection action bar: copy, highlight, annotate, AI and dictionary
/// actions.
/// </summary>
public partial class MainWindow
{
    private void ShowReaderSelectionPopup(Point? placementPoint = null, double? selectionBottom = null)
    {
        if (ReaderTranslationHostPopup.IsOpen)
            HideReaderTranslationPopup(clearSelection: false);
        if (ReaderDictionaryHostPopup.IsOpen)
            HideReaderDictionaryPopup(clearSelection: false);
        if (string.IsNullOrWhiteSpace(_readerPendingSelection))
        {
            HideReaderSelectionPopup();
            return;
        }

        // Remember where the selection sits so the annotation input window can
        // open at the same spot after the bar hands off to it.
        _readerLastSelectionPopupAnchor = placementPoint;
        _readerLastSelectionPopupBottom = selectionBottom;
        ShowReaderPopupNearSelection(
            ReaderSelectionHostPopup,
            ReaderSelectionPopupBar,
            fallbackWidth: 321,
            fallbackHeight: 40,
            placementPoint,
            selectionBottom);
    }

    // Shared placement math for popups anchored to the live text selection:
    // center on the selection's left edge, prefer above, clamp inside the
    // reader body, and never let the positioner flip against the desktop.
    private void ShowReaderPopupNearSelection(
        Popup popup,
        Control content,
        double fallbackWidth,
        double fallbackHeight,
        Point? placementPoint,
        double? selectionBottom,
        bool preferBelow = false)
    {
        var hostWidth = Math.Max(1, ReaderWebViewHost.Bounds.Width);
        var hostHeight = Math.Max(1, ReaderWebViewHost.Bounds.Height);
        var anchor = placementPoint ?? new Point(
            Math.Max(24, hostWidth / 2),
            Math.Max(24, hostHeight / 2));
        var barWidth = content.Bounds.Width;
        var barHeight = content.Bounds.Height;
        // On the first open Avalonia may not have arranged the Popup content
        // yet. Use a conservative fallback size; forcing an infinite measure
        // from a selection event can invalidate the reader's layout pass.
        if (!double.IsFinite(barWidth) || barWidth <= 0) barWidth = fallbackWidth;
        if (!double.IsFinite(barHeight) || barHeight <= 0) barHeight = fallbackHeight;

        const double bodyInset = 8;
        const double aboveGap = 10;
        const double belowGap = 12;

        // Top/Bottom gravity centers the bar on the placement point. Shift
        // that point by half the bar width so the bar's left edge aligns with
        // the selection's left edge. If the selection is too close to the
        // reader's right edge, clamp the whole bar back inside the body.
        var minimumLeft = bodyInset;
        var maximumLeft = Math.Max(minimumLeft, hostWidth - bodyInset - barWidth);
        var left = Math.Clamp(anchor.X, minimumLeft, maximumLeft);
        var anchorX = left + barWidth / 2;
        var bottom = selectionBottom is { } value && double.IsFinite(value)
            ? value
            : anchor.Y;
        var roomAbove = anchor.Y - barHeight - aboveGap >= bodyInset;
        var roomBelow = bottom + belowGap + barHeight <= hostHeight - bodyInset;
        var placeAbove = preferBelow
            ? !roomBelow && roomAbove
            : roomAbove || !roomBelow;
        var anchorY = placeAbove
            ? Math.Clamp(
                anchor.Y,
                bodyInset + barHeight + aboveGap,
                Math.Max(bodyInset + barHeight + aboveGap, hostHeight - bodyInset))
            : Math.Clamp(
                bottom,
                bodyInset,
                Math.Max(bodyInset, hostHeight - bodyInset - barHeight - belowGap));
        popup.PlacementTarget = ReaderWebViewHost;
        popup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
        popup.PlacementRect = new Rect(anchorX, anchorY, 1, 1);
        popup.PlacementAnchor =
            Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        popup.PlacementGravity =
            placeAbove
                ? Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.Top
                : Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.Bottom;
        popup.PlacementConstraintAdjustment =
            Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.None;
        popup.HorizontalOffset = 0;
        popup.VerticalOffset = placeAbove ? -aboveGap : belowGap;
        popup.IsOpen = true;
    }

    private void HideReaderSelectionPopup()
    {
        if (ReaderSelectionPopupBar.Child is Panel actions)
            foreach (var button in actions.Children.OfType<Button>())
                ToolTip.SetIsOpen(button, false);
        HideReaderTranslationPopup(clearSelection: false);
        HideReaderDictionaryPopup(clearSelection: false);
        ReaderMarkerPaletteFlyout?.Hide();
        StopReaderSelectionHighlightPointerTracking();
        if (ReaderSelectionHighlightMenuButton?.Flyout is PopupFlyoutBase { IsOpen: true } flyout)
            flyout.Hide();
        if (ReaderSelectionHostPopup is not null)
            ReaderSelectionHostPopup.IsOpen = false;
    }

    private void ReaderRoot_SelectionDismissPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((!ReaderSelectionHostPopup.IsOpen
                && !ReaderTranslationHostPopup.IsOpen
                && !ReaderDictionaryHostPopup.IsOpen)
            || string.IsNullOrWhiteSpace(_readerPendingSelection)
            || !e.GetCurrentPoint(ReaderRoot).Properties.IsLeftButtonPressed)
            return;

        // The flyout presenter has its own visual root, so its menu items are
        // not visual descendants of ReaderSelectionPopupBar. They are still
        // part of the selection UI: treating one as an outside click clears
        // the pending selection before the style Click handler can save it.
        if (e.Source is Visual source
            && (ReferenceEquals(source, ReaderSelectionPopupBar)
                || source.GetVisualAncestors().Contains(ReaderSelectionPopupBar)
                || source is MenuFlyoutPresenter
                || source.GetVisualAncestors().OfType<MenuFlyoutPresenter>().Any()
                || ReferenceEquals(source, ReaderSelectionMarkerPalette)
                || source.GetVisualAncestors().Contains(ReaderSelectionMarkerPalette)
                || ReferenceEquals(source, ReaderTranslationPopup)
                || source.GetVisualAncestors().Contains(ReaderTranslationPopup)
                || ReferenceEquals(source, ReaderDictionaryPopup)
                || source.GetVisualAncestors().Contains(ReaderDictionaryPopup)
                || source is ComboBoxItem))
            return;

        e.Handled = true;
        if (IsLinuxReaderTextFallbackActive())
        {
            // ReaderRoot receives the tunnel event before the fallback
            // content's own pointer handlers. Remember this press through its
            // matching release; otherwise clearing the selection here makes
            // the release handler believe it was an ordinary edge click and
            // it turns the page.
            var fallbackPoint = e.GetPosition(ReaderLinuxTextFallbackOverlay);
            _readerLinuxTextFallbackSelectionDismissPress =
                fallbackPoint.X >= 0
                && fallbackPoint.X <= ReaderLinuxTextFallbackOverlay.Bounds.Width
                && fallbackPoint.Y >= 0
                && fallbackPoint.Y <= ReaderLinuxTextFallbackOverlay.Bounds.Height;
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }

        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        if (!_readerIsPdf && CurrentReaderHost is { } host)
            _ = ClearCurrentReaderSelectionAsync(host);
    }

    private async void ReaderSelectionCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        await PerformReaderSelectionCopyAsync();
    }

    private async void ReaderSelectionHighlightButton_Click(object? sender, RoutedEventArgs e)
    {
        await ApplyReaderHighlightStyleAsync("solid");
    }

    private void ReaderSelectionHighlightMenuButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        if (sender is Button { Flyout: { IsOpen: false } flyout } button)
        {
            flyout.ShowAt(button);
            StartReaderSelectionHighlightPointerTracking();
        }
    }

    private void InitializeReaderSelectionToolTips()
    {
        if (ReaderSelectionPopupBar.Child is not Panel actions) return;
        foreach (var button in actions.Children.OfType<Button>())
        {
            IDisposable? hideTimer = null;
            button.PropertyChanged += (_, e) =>
            {
                if (e.Property != ToolTip.IsOpenProperty) return;
                hideTimer?.Dispose();
                hideTimer = null;
                if (ToolTip.GetIsOpen(button))
                    hideTimer = DispatcherTimer.RunOnce(
                        () => ToolTip.SetIsOpen(button, false),
                        TimeSpan.FromMilliseconds(ReaderSelectionToolTipDurationMs));
            };
        }
    }

    private void StartReaderSelectionHighlightPointerTracking()
    {
        StopReaderSelectionHighlightPointerTracking();
        _readerSelectionHighlightOpenedTick = Environment.TickCount64;
        _readerSelectionHighlightToolTipHoverTick = _readerSelectionHighlightOpenedTick;
        _readerSelectionHighlightOutsideTicks = 0;
        _readerSelectionHighlightPointerTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _readerSelectionHighlightPointerTimer.Tick -= ReaderSelectionHighlightPointerTimer_Tick;
        _readerSelectionHighlightPointerTimer.Tick += ReaderSelectionHighlightPointerTimer_Tick;
        _readerSelectionHighlightPointerTimer.Start();
    }

    private void StopReaderSelectionHighlightPointerTracking()
    {
        _readerSelectionHighlightPointerTimer?.Stop();
        UpdateReaderSelectionHighlightToolTip(false);
    }

    private void UpdateReaderSelectionHighlightToolTip(bool pointerOverButton)
    {
        if (!pointerOverButton)
        {
            _readerSelectionHighlightToolTipHoverTick = null;
            ToolTip.SetIsOpen(ReaderSelectionHighlightMenuButton, false);
            return;
        }

        // The menu's overlay clears IsPointerOver on the anchor button. Use
        // the same screen-pointer tracking that keeps this hover menu open.
        _readerSelectionHighlightToolTipHoverTick ??= Environment.TickCount64;
        var elapsed = Environment.TickCount64 - _readerSelectionHighlightToolTipHoverTick.Value;
        var delay = ToolTip.GetShowDelay(ReaderSelectionHighlightMenuButton);
        // Keep the hover start after dismissal so a stationary pointer cannot
        // repeatedly reopen the tooltip through the menu overlay.
        ToolTip.SetIsOpen(ReaderSelectionHighlightMenuButton,
            elapsed >= delay && elapsed < delay + ReaderSelectionToolTipDurationMs);
    }

    private void ReaderSelectionHighlightPointerTimer_Tick(object? sender, EventArgs e)
    {
        if (ReaderSelectionHighlightMenuButton.Flyout is not PopupFlyoutBase { IsOpen: true } flyout)
        {
            StopReaderSelectionHighlightPointerTracking();
            return;
        }
        // The palette has its own popup root. Keep the style menu open while
        // the pointer crosses into it; light dismiss closes the palette.
        if (ReaderMarkerPaletteFlyout is { IsOpen: true })
        {
            UpdateReaderSelectionHighlightToolTip(false);
            _readerSelectionHighlightOutsideTicks = 0;
            return;
        }
        if (!TryGetReaderCursorScreenPoint(out var screenPoint))
            return;

        var buttonRect = GetReaderScreenRect(ReaderSelectionHighlightMenuButton);
        UpdateReaderSelectionHighlightToolTip(ContainsReaderScreenPoint(buttonRect, screenPoint));
        var presenter = ReaderSelectionHighlightSolidItem
            .GetVisualAncestors()
            .OfType<MenuFlyoutPresenter>()
            .FirstOrDefault();
        if (presenter is null
            && Environment.TickCount64 - _readerSelectionHighlightOpenedTick < 400)
            return;
        PixelRect? presenterRect = presenter is null ? null : GetReaderScreenRect(presenter);
        if (ContainsReaderScreenPoint(buttonRect, screenPoint)
            || presenterRect is { } menuRect && ContainsReaderScreenPoint(menuRect, screenPoint)
            || IsReaderSelectionHighlightMenuBridge(screenPoint, buttonRect, presenterRect))
        {
            _readerSelectionHighlightOutsideTicks = 0;
            return;
        }

        // Require two consecutive outside samples. A single X11 sample can
        // briefly land between the button and a newly arranged flyout while
        // the compositor is moving it into its final tangent position.
        if (++_readerSelectionHighlightOutsideTicks < 2)
            return;

        flyout.Hide();
        StopReaderSelectionHighlightPointerTracking();
    }

    private static PixelRect GetReaderScreenRect(Control control)
    {
        var topLeft = control.PointToScreen(new Point(0, 0));
        var bottomRight = control.PointToScreen(new Point(control.Bounds.Width, control.Bounds.Height));
        return new PixelRect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private static bool ContainsReaderScreenPoint(PixelRect rect, PixelPoint point) =>
        point.X >= rect.X
        && point.X <= rect.X + rect.Width
        && point.Y >= rect.Y
        && point.Y <= rect.Y + rect.Height;

    private static bool IsReaderSelectionHighlightMenuBridge(
        PixelPoint point,
        PixelRect button,
        PixelRect? presenter)
    {
        if (presenter is not { } menu) return false;
        var left = Math.Max(button.X, menu.X);
        var right = Math.Min(button.X + button.Width, menu.X + menu.Width);
        var top = Math.Min(button.Y + button.Height, menu.Y + menu.Height);
        var bottom = Math.Max(button.Y, menu.Y);
        return left <= right
            && point.X >= left
            && point.X <= right
            && point.Y >= top
            && point.Y <= bottom;
    }

    private async void ReaderSelectionHighlightStyleItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string style }) return;
        await ApplyReaderHighlightStyleAsync(style, style == "marker" ? ReaderMarkerColorForSelection() : null);
    }

    private async void ReaderSelectionHighlightColorItem_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string color }) return;
        await ApplyReaderHighlightStyleAsync("marker", color);
    }

    private void ReaderSelectionAnnotateButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        ShowReaderAnnotationInputPopup();
    }

    private async void ReaderSelectionAiButton_Click(object? sender, RoutedEventArgs e)
    {
        await ExplainReaderSelectionWithAiAsync();
    }

    private async Task ExplainReaderSelectionWithAiAsync()
    {
        var selection = _readerPendingSelection;
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(selection)) return;
        ShowReaderAiTab();
        if (!_appSettings.AiEnabled)
        {
            await ShowMessageAsync(T("AI 助手"), T("AI 已在应用设置中关闭。"));
            return;
        }

        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("AI 助手"), T("网络访问已关闭，无法调用 AI 服务。"));
            return;
        }

        if (!_readerAiSettings.IsConfigured)
        {
            await ShowMessageAsync(
                T("AI 助手设置"),
                T("请先到设置面板的 AI 助手设置中填写 Base URL、模型和 API Key。"));
            return;
        }

        await SendReaderAiQuestionAsync(
            T("请解释下面这段文字的含义、上下文和隐含前提，并给出一个简单例子：\n\n{0}", selection),
            ReaderAiRequestKind.SelectionExplain);
    }

    private void ReaderSelectionSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        HideReaderSelectionPopup();
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        ShowReaderSearchPanel();
        ReaderTocSearchBox.Text = _readerPendingSelection;
        ReaderTocSearchBox.Focus();
    }

    private async void ReaderSelectionDictionaryButton_Click(object? sender, RoutedEventArgs e)
    {
        await PerformReaderSelectionDictionaryAsync();
    }

    private void DispatchReaderSelectionAction(JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement)) return;
        switch (actionElement.GetString())
        {
            case "copy":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionCopyAsync());
                break;
            case "speak":
                _ = ObserveReaderTaskAsync(SpeakReaderSelectionAsync());
                break;
            case "highlight":
                if (root.TryGetProperty("style", out var styleElement)
                    && styleElement.GetString() is { } style)
                {
                    var color = root.TryGetProperty("color", out var colorElement)
                        && colorElement.ValueKind == JsonValueKind.String
                        ? colorElement.GetString()
                        : style == "marker" ? "#000000" : null;
                    _ = ObserveReaderTaskAsync(ApplyReaderHighlightStyleAsync(style, color));
                }
                break;
            case "annotate":
                HideReaderSelectionPopup();
                ShowReaderAnnotationInputPopup();
                break;
            case "ai":
                _ = ObserveReaderTaskAsync(ExplainReaderSelectionWithAiAsync());
                break;
            case "search":
                if (string.IsNullOrWhiteSpace(_readerPendingSelection)) break;
                ShowReaderSearchPanel();
                ReaderTocSearchBox.Text = _readerPendingSelection;
                ReaderTocSearchBox.Focus();
                break;
            case "dictionary":
                _ = ObserveReaderTaskAsync(PerformReaderSelectionDictionaryAsync());
                break;
        }
    }

    // Continuous-mode short-chapter skip (WinUI reference's
    // SkipShortChapterIfNeededAsync): shortly after entering a chapter, if it
    // cannot scroll at all (content fits the viewport), advance to the next
    // one so the reader never stops on an empty page. Depth-capped so a chain
    // of empty chapters cannot loop forever.
}
