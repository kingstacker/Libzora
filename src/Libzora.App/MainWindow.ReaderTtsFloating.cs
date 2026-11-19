using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

namespace Libzora;

/// <summary>
/// Floating TTS panel: edge activation, collapse and pointer tracking.
/// </summary>
public partial class MainWindow
{
    private void UpdateReaderTtsFloatingUi(TtsPlaybackState state)
    {
        if (ReaderTtsFloatingPanel is null
            || ReaderTtsFloatingEdgeActivationRegion is null
            || ReaderTtsFloatingToggleButton is null
            || ReaderTtsFloatingStopButton is null)
        {
            return;
        }

        var active = IsReaderTtsFloatingActive();
        if (!active)
        {
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingExpanded = false;
            _readerTtsFloatingEdgeActivationArmed = true;
            _readerTtsFloatingCollapseTimer?.Stop();
            _readerTtsFloatingAutoHideTimer?.Stop();
        }

        ReaderTtsFloatingPanel.IsVisible = active;
        // The collapsed wheel intentionally leaves a small visual sliver at
        // the edge, but that sliver must not be an independent wake-up target.
        // Otherwise PointerEntered on the panel bypasses the edge arming
        // guard and can reopen the wheel in a collapse/reopen loop.
        ReaderTtsFloatingPanel.IsHitTestVisible = active
            && _readerTtsFloatingExpanded;
        ReaderTtsFloatingEdgeActivationRegion.IsVisible = active;
        // The edge strip is a wake-up target only while the wheel is hidden.
        // Leaving it hit-testable while expanded can re-enter the strip as
        // the pointer leaves the wheel and cancel the collapse timer.
        ReaderTtsFloatingEdgeActivationRegion.IsHitTestVisible = active
            && !_readerTtsFloatingExpanded;
        if (ReaderTtsFloatingPanel.RenderTransform is TranslateTransform transform)
        {
            transform.X = active && _readerTtsFloatingExpanded
                ? 0
                : ReaderTtsFloatingCollapsedOffset;
        }

        UpdateReaderTtsFloatingPointerWatch();
        if (!active) return;

        var canToggle = state is not TtsPlaybackState.Generating
            and not TtsPlaybackState.AdvancingChapter;
        ReaderTtsFloatingToggleButton.Content = state switch
        {
            TtsPlaybackState.Playing => "Ⅱ",
            TtsPlaybackState.Generating
                or TtsPlaybackState.AdvancingChapter => "…",
            _ => "▶"
        };
        ReaderTtsFloatingToggleButton.IsEnabled = canToggle;
        ReaderTtsFloatingStopButton.IsEnabled = state is not TtsPlaybackState.Stopped;
        ReaderTtsFloatingPreviousButton.IsEnabled = _readerChapterIndex > 0;
        ReaderTtsFloatingNextButton.IsEnabled = _readerDocument is not null
            && _readerChapterIndex >= 0
            && _readerChapterIndex + 1 < _readerDocument.Chapters.Count;
        ToolTip.SetTip(ReaderTtsFloatingMenuButton, T("听书菜单"));
        ToolTip.SetTip(ReaderTtsFloatingPreviousButton, T("上一章"));
        ToolTip.SetTip(ReaderTtsFloatingNextButton, T("下一章"));
        ToolTip.SetTip(
            ReaderTtsFloatingToggleButton,
            state == TtsPlaybackState.Playing ? T("暂停") : T("继续"));
        ToolTip.SetTip(ReaderTtsFloatingStopButton, T("停止听书"));
    }

    private bool IsReaderTtsFloatingActive()
        => _readerTtsFloatingRequested
            && (IsReaderTtsActiveState(_readerTts.State)
                || _readerTtsFloatingStartupShowing)
            && ReaderRoot is not null
            && ReaderRoot.IsVisible
            && !_readerIsPdf
            && _readerDocument is not null;

    private void UpdateReaderTtsFloatingPointerWatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (_readerZenMode || IsReaderTtsFloatingActive())
            StartReaderZenPointerWatch();
        else
            _readerZenPointerWatchTimer?.Stop();
    }

    private void ExpandReaderTtsFloatingPanel()
    {
        if (!IsReaderTtsFloatingActive()) return;

        _readerTtsFloatingCollapseTimer?.Stop();
        _readerTtsFloatingAutoHideTimer?.Stop();
        _readerTtsFloatingStartupShowing = false;
        if (_readerTtsFloatingExpanded) return;

        _readerTtsFloatingExpanded = true;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void CollapseReaderTtsFloatingPanel()
    {
        _readerTtsFloatingCollapseTimer?.Stop();
        _readerTtsFloatingAutoHideTimer?.Stop();
        _readerTtsFloatingStartupShowing = false;
        if (!_readerTtsFloatingExpanded) return;

        // The edge region is temporarily non-hit-testable while the wheel is
        // expanded. Re-sample the real cursor before enabling it again so a
        // pointer that stayed on the right cannot generate a synthetic
        // PointerEntered and reopen the wheel.
        SyncReaderTtsFloatingEdgeActivationArmWithCursor();
        _readerTtsFloatingExpanded = false;
        UpdateReaderTtsFloatingUi(_readerTts.State);
    }

    private void SyncReaderTtsFloatingEdgeActivationArmWithCursor()
    {
        if (ReaderTtsFloatingEdgeActivationRegion is null
            || !TryGetReaderCursorScreenPoint(out var cursor))
        {
            return;
        }

        _readerTtsFloatingEdgeActivationArmed = !ContainsReaderScreenPoint(
            GetReaderScreenRect(ReaderTtsFloatingEdgeActivationRegion),
            cursor);
    }

    private void TryExpandReaderTtsFloatingFromEdge()
    {
        if (!_readerTtsFloatingEdgeActivationArmed)
            return;

        _readerTtsFloatingEdgeActivationArmed = false;
        ExpandReaderTtsFloatingPanel();
    }

    private void ShowReaderTtsFloatingPanelTemporarily()
    {
        // The header/popup start action is away from the wheel. Give the
        // user a short visual confirmation without making the wheel stay on
        // screen for the whole reading session.
        _readerTtsFloatingStartupShowing = true;
        if (!IsReaderTtsFloatingActive())
        {
            _readerTtsFloatingStartupShowing = false;
            return;
        }

        _readerTtsFloatingExpanded = true;
        _readerTtsFloatingCollapseTimer?.Stop();
        UpdateReaderTtsFloatingUi(_readerTts.State);
        StartReaderTtsFloatingAutoHideTimer();
    }

    private void StartReaderTtsFloatingAutoHideTimer()
    {
        if (!IsReaderTtsFloatingActive()
            || !_readerTtsFloatingExpanded
            || ReaderTtsPopup.IsOpen)
        {
            return;
        }

        _readerTtsFloatingAutoHideTimer ??= new DispatcherTimer();
        _readerTtsFloatingAutoHideTimer.Stop();
        _readerTtsFloatingAutoHideTimer.Interval =
            TimeSpan.FromMilliseconds(ReaderTtsFloatingAutoHideDelayMs);
        _readerTtsFloatingAutoHideTimer.Tick -=
            ReaderTtsFloatingAutoHideTimer_Tick;
        _readerTtsFloatingAutoHideTimer.Tick +=
            ReaderTtsFloatingAutoHideTimer_Tick;
        _readerTtsFloatingAutoHideTimer.Start();
    }

    private void ReaderTtsFloatingAutoHideTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _readerTtsFloatingAutoHideTimer?.Stop();
        if (!ReaderTtsPopup.IsOpen && _readerTtsFloatingStartupShowing)
            CollapseReaderTtsFloatingPanel();
    }

    private void ScheduleReaderTtsFloatingCollapse(
        int delayMs = ReaderTtsFloatingCollapseDelayMs)
    {
        if (!IsReaderTtsFloatingActive()
            || !_readerTtsFloatingExpanded
            || ReaderTtsPopup.IsOpen)
        {
            return;
        }

        _readerTtsFloatingCollapseTimer ??= new DispatcherTimer();
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        // The Windows pointer watcher samples every 80ms. Do not restart an
        // already-running countdown on every sample, or it can never tick.
        // A newly requested shorter delay is still allowed to win.
        if (_readerTtsFloatingCollapseTimer.IsEnabled
            && _readerTtsFloatingCollapseTimer.Interval <= interval)
        {
            return;
        }

        _readerTtsFloatingCollapseTimer.Stop();
        _readerTtsFloatingCollapseTimer.Interval = interval;
        _readerTtsFloatingCollapseTimer.Tick -=
            ReaderTtsFloatingCollapseTimer_Tick;
        _readerTtsFloatingCollapseTimer.Tick +=
            ReaderTtsFloatingCollapseTimer_Tick;
        _readerTtsFloatingCollapseTimer.Start();
    }

    private void ReaderTtsFloatingCollapseTimer_Tick(object? sender, EventArgs e)
    {
        _readerTtsFloatingCollapseTimer?.Stop();
        if (!ReaderTtsPopup.IsOpen)
            CollapseReaderTtsFloatingPanel();
    }

    private void UpdateReaderTtsFloatingForPointer(
        double x,
        double y,
        double surfaceWidth,
        double surfaceHeight)
    {
        if (!IsReaderTtsFloatingActive()
            || surfaceWidth <= 0
            || surfaceHeight <= 0)
        {
            return;
        }

        if (ReaderTtsPopup.IsOpen)
        {
            _readerTtsFloatingCollapseTimer?.Stop();
            return;
        }

        var panelLeft = surfaceWidth
            - ReaderTtsFloatingPanelRightMargin
            - ReaderTtsFloatingPanelWidth;
        var panelTop = surfaceHeight
            - ReaderTtsFloatingPanelBottomMargin
            - ReaderTtsFloatingPanelHeight;
        var insidePanel = x >= panelLeft
            && x <= panelLeft + ReaderTtsFloatingPanelWidth
            && y >= panelTop
            && y <= panelTop + ReaderTtsFloatingPanelHeight;
        var insideEdge = x >= surfaceWidth - ReaderTtsFloatingEdgeActivationWidth
            && y >= panelTop - ReaderTtsFloatingEdgeActivationTopPadding
            && y <= surfaceHeight - ReaderTtsFloatingEdgeActivationBottomPadding;

        if (!insideEdge)
            _readerTtsFloatingEdgeActivationArmed = true;
        else if (_readerTtsFloatingExpanded)
            _readerTtsFloatingEdgeActivationArmed = false;

        // The edge strip is only an activation target while the wheel is
        // collapsed. Once the wheel is open, leaving the wheel for that strip
        // must start the collapse timer instead of keeping it open forever.
        var keepExpanded = _readerTtsFloatingExpanded
            ? insidePanel
            : insideEdge;
        if (keepExpanded)
        {
            if (!_readerTtsFloatingExpanded)
                TryExpandReaderTtsFloatingFromEdge();
            else
            {
                _readerTtsFloatingStartupShowing = false;
                _readerTtsFloatingAutoHideTimer?.Stop();
                _readerTtsFloatingCollapseTimer?.Stop();
            }
        }
        else
        {
            // Do not let the global pointer watcher cancel the startup grace
            // period: the start button is normally far from the wheel.
            if (!_readerTtsFloatingStartupShowing)
            {
                ScheduleReaderTtsFloatingCollapse(
                    ReaderTtsFloatingPointerExitCollapseDelayMs);
            }
        }
    }

    private void UpdateReaderTtsFloatingForScreenPointer(PixelPoint cursor)
    {
        if (!IsReaderTtsFloatingActive()
            || ReaderTtsFloatingPanel is null
            || ReaderTtsFloatingEdgeActivationRegion is null)
        {
            return;
        }

        if (ReaderTtsPopup.IsOpen)
        {
            _readerTtsFloatingCollapseTimer?.Stop();
            return;
        }

        // Use the controls' real screen rectangles. This includes the row
        // offset, display scaling and the current slide transform, so native
        // WebView mouse movement cannot leave the wheel permanently expanded.
        var insidePanel = _readerTtsFloatingExpanded
            && ContainsReaderScreenPoint(
                GetReaderScreenRect(ReaderTtsFloatingPanel),
                cursor);
        var insideEdge = ContainsReaderScreenPoint(
            GetReaderScreenRect(ReaderTtsFloatingEdgeActivationRegion),
            cursor);

        if (!insideEdge)
            _readerTtsFloatingEdgeActivationArmed = true;
        else if (_readerTtsFloatingExpanded)
            _readerTtsFloatingEdgeActivationArmed = false;

        if (insidePanel)
        {
            _readerTtsFloatingStartupShowing = false;
            _readerTtsFloatingAutoHideTimer?.Stop();
            _readerTtsFloatingCollapseTimer?.Stop();
        }
        else if (!_readerTtsFloatingExpanded && insideEdge)
        {
            TryExpandReaderTtsFloatingFromEdge();
        }
        else if (!_readerTtsFloatingStartupShowing)
        {
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        }
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerEntered(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
        {
            _readerTtsFloatingEdgeActivationArmed = false;
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        }
        else
            TryExpandReaderTtsFloatingFromEdge();
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
        {
            _readerTtsFloatingEdgeActivationArmed = false;
            ScheduleReaderTtsFloatingCollapse(
                ReaderTtsFloatingPointerExitCollapseDelayMs);
        }
        else
            TryExpandReaderTtsFloatingFromEdge();
    }

    private void ReaderTtsFloatingEdgeActivationRegion_PointerExited(
        object? sender,
        PointerEventArgs e)
    {
        // Expansion disables hit testing on this strip and can raise
        // PointerExited even though the physical pointer is still there.
        // Re-arm only after checking the actual screen cursor position.
        SyncReaderTtsFloatingEdgeActivationArmWithCursor();
        ScheduleReaderTtsFloatingCollapse(
            ReaderTtsFloatingPointerExitCollapseDelayMs);
    }

    private void ReaderTtsFloatingPanel_PointerEntered(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
            ExpandReaderTtsFloatingPanel();
    }

    private void ReaderTtsFloatingPanel_PointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (_readerTtsFloatingExpanded)
            ExpandReaderTtsFloatingPanel();
    }

    private void ReaderTtsFloatingPanel_PointerExited(
        object? sender,
        PointerEventArgs e)
        => ScheduleReaderTtsFloatingCollapse(
            ReaderTtsFloatingPointerExitCollapseDelayMs);

    private void ReaderTtsPopup_Closed(object? sender, EventArgs e)
    {
        if (_readerTtsFloatingStartupShowing)
            StartReaderTtsFloatingAutoHideTimer();
        else
            ScheduleReaderTtsFloatingCollapse();
    }
}
