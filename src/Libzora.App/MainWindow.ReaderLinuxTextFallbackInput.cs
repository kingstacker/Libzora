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
/// Linux plain-text fallback input handling: pointer, keyboard and
/// wheel events.
/// </summary>
public partial class MainWindow
{
    private void ReaderLinuxTextFallback_Tapped(object? sender, TappedEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var tappedPoint = e.GetPosition(ReaderLinuxTextFallbackOverlay);
        var tappedButton = FindReaderFootnoteButton(e.Source as Visual)
            ?? FindReaderFootnoteButtonAt(tappedPoint);
        if (tappedButton is not null)
        {
            e.Handled = true;
            return;
        }
        if (e.Source is Visual visual
            && TrySyncLinuxReaderTextFallbackSelectionFromVisual(
                visual,
                e.GetPosition(ReaderWebViewHost)))
        {
            return;
        }
        if (e.Source is Visual source && !IsWithinReaderLinuxTextContent(source))
            ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ReaderLinuxTextFallback_ContentPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is SelectableTextBlock block
            && block.DataContext is ReaderLinuxTextFallbackBlock textBlock
            && TrySyncLinuxReaderTextFallbackSelectionFromBlock(block, textBlock))
        {
            var point = e.GetPosition(ReaderWebViewHost);
            var bottom = block.TranslatePoint(new Point(0, block.Bounds.Height), ReaderWebViewHost)?.Y;
            var anchor = GetReaderSelectionAnchorRect(block);
            ShowReaderSelectionPopup(
                anchor?.Position ?? point,
                anchor?.Bottom ?? bottom);
            return;
        }

        var fallbackPoint = e.GetPosition(ReaderWebViewHost);
        var fallbackBottom = sender is SelectableTextBlock fallbackBlock
            ? fallbackBlock.TranslatePoint(new Point(0, fallbackBlock.Bounds.Height), ReaderWebViewHost)?.Y
            : null;
        SyncLinuxReaderTextFallbackSelectionState(fallbackPoint, fallbackBottom);
        if (string.IsNullOrWhiteSpace(_readerPendingSelection))
            ScheduleLinuxReaderTextFallbackSelectionSync(fallbackPoint, fallbackBottom);
    }

    private void ReaderLinuxTextFallbackFootnoteButton_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Button { Tag: string href }) return;
        _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            e.GetPosition(ReaderWebViewHost)));
    }

    private void ReaderLinuxTextFallbackFootnoteButton_PointerExited(object? sender, PointerEventArgs e)
    {
        HideReaderFootnotePopup();
    }

    private static Button? FindReaderFootnoteButton(Visual? visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is Button { Tag: string href }
                && !string.IsNullOrWhiteSpace(href))
                return (Button)current;
            current = current.GetVisualParent();
        }
        return null;
    }

    private Button? FindReaderFootnoteButtonAt(Point point)
    {
        foreach (var visual in ReaderLinuxTextFallbackOverlay.GetVisualsAt(point).Reverse())
        {
            if (FindReaderFootnoteButton(visual) is { } button)
                return button;
        }
        return null;
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionAt(
        Point point,
        Point placementPoint)
    {
        foreach (var visual in ReaderLinuxTextFallbackOverlay.GetVisualsAt(point).Reverse())
        {
            if (TrySyncLinuxReaderTextFallbackSelectionFromVisual(visual, placementPoint))
                return true;
        }

        return false;
    }

    private void ReaderLinuxTextFallback_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var button = FindReaderFootnoteButton(e.Source as Visual)
            ?? FindReaderFootnoteButtonAt(e.GetPosition(ReaderLinuxTextFallbackOverlay));
        if (button is not { Tag: string href })
        {
            HideReaderFootnotePopup();
            return;
        }

        if (ReaderFootnoteHostPopup.IsOpen
            && string.Equals(_readerFootnoteHref, href, StringComparison.Ordinal))
            return;
        _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
            href,
            isFootnote: true,
            e.GetPosition(ReaderWebViewHost)));
    }

    private void ReaderLinuxTextFallback_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _readerLinuxTextFallbackSelectionSyncSequence++;
        if (_readerLinuxTextFallbackSelectionDismissPress)
        {
            // Consume the marker on the matching press. Keeping it until a
            // later release lets a footer/chrome dismiss leak across a page
            // turn and swallow the first selection drag on the new page.
            _readerLinuxTextFallbackSelectionDismissPress = false;
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            e.Handled = true;
            return;
        }

        if (!IsLinuxReaderTextFallbackActive()
            || (_readerLayout.FlowMode != 0 && _readerLayout.FlowMode != 1))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        var point = e.GetCurrentPoint(ReaderLinuxTextFallbackOverlay);
        if (!point.Properties.IsLeftButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        // A press exactly on the first/last glyph of a line can be hit-tested
        // as ScrollContentPresenter instead of SelectableTextBlock. It is
        // still a text-selection press, and SelectableTextBlock may scroll
        // itself to reveal the selection. Freeze the reader for every press
        // in the continuous viewport; leave the scrollbar free to scroll.
        if (_readerLayout.FlowMode == 0
            && e.Source is Visual source
            && IsReaderLinuxTextFallbackScrollBarVisual(source))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        _readerLinuxTextFallbackPointerPressed = true;
        _readerLinuxTextFallbackSelectionAtPointerPress =
            HasLinuxReaderTextFallbackSelection();
        _readerLinuxTextFallbackPointerStart = point.Position;
        if (_readerLayout.FlowMode == 0)
        {
            _readerLinuxTextFallbackSelectionScrollOffset =
                ReaderLinuxTextFallbackScroll.Offset;
            _readerLinuxTextFallbackSelectionScrollLocked = true;
        }
    }

    private void ReaderLinuxTextFallback_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_readerLinuxTextFallbackSelectionDismissPress)
        {
            _readerLinuxTextFallbackSelectionDismissPress = false;
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }

        if (!IsLinuxReaderTextFallbackActive())
        {
            _readerLinuxTextFallbackPointerPressed = false;
            _readerLinuxTextFallbackSelectionAtPointerPress = false;
            _readerLinuxTextFallbackSelectionScrollLocked = false;
            return;
        }

        var point = e.GetPosition(ReaderLinuxTextFallbackOverlay);
        var pointerWasPressed = _readerLinuxTextFallbackPointerPressed;
        var moved = pointerWasPressed
            ? Math.Abs(point.X - _readerLinuxTextFallbackPointerStart.X)
                + Math.Abs(point.Y - _readerLinuxTextFallbackPointerStart.Y)
            : double.PositiveInfinity;
        var dismissSelection = pointerWasPressed
            && _readerLinuxTextFallbackSelectionAtPointerPress
            && moved <= 12;
        _readerLinuxTextFallbackSelectionAtPointerPress = false;
        if (dismissSelection)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ClearLinuxReaderTextFallbackSelectionState();
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if ((FindReaderFootnoteButton(e.Source as Visual)
                ?? FindReaderFootnoteButtonAt(point)) is not null)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if (_readerLayout.VerticalWriting && _readerLinuxVerticalFootnoteHandledRelease)
        {
            // The vertical page already popped its footnote on release; do not
            // let the same tap also register as an edge click-turn.
            _readerLinuxVerticalFootnoteHandledRelease = false;
            _readerLinuxTextFallbackPointerPressed = false;
            ReleaseLinuxTextFallbackSelectionScrollLock();
            e.Handled = true;
            return;
        }
        if (_readerLayout.FlowMode == 0)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            var placementPoint = e.GetPosition(ReaderWebViewHost);
            var synced = e.Source is Visual selectedVisual
                && TrySyncLinuxReaderTextFallbackSelectionFromVisual(
                    selectedVisual,
                    placementPoint);
            if (!synced)
            {
                synced = TrySyncLinuxReaderTextFallbackSelectionAt(point, placementPoint);
            }
            if (synced)
            {
                // Keep the original viewport frozen until selection indices,
                // anchor geometry, and the popup have all been computed. A
                // selectable text block can request a bring-into-view during
                // that work; releasing the lock first reintroduces the jump
                // at a line boundary.
                ReleaseLinuxTextFallbackSelectionScrollLock();
                return;
            }
        }
        SyncLinuxReaderTextFallbackSelectionState(e.GetPosition(ReaderWebViewHost));
        if (_readerLayout.FlowMode == 0)
            ReleaseLinuxTextFallbackSelectionScrollLock();
        if (!string.IsNullOrWhiteSpace(_readerPendingSelection))
        {
            _readerLinuxTextFallbackPointerPressed = false;
            return;
        }
        if (pointerWasPressed && moved > 12)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            ScheduleLinuxReaderTextFallbackSelectionSync(
                e.GetPosition(ReaderWebViewHost));
            return;
        }

        if (!_readerLinuxTextFallbackPointerPressed || _readerLayout.FlowMode != 1)
        {
            _readerLinuxTextFallbackPointerPressed = false;
            return;
        }

        _readerLinuxTextFallbackPointerPressed = false;
        if (moved > 12) return;

        var width = Math.Max(1, ReaderLinuxTextFallbackOverlay.Bounds.Width);
        if (point.X < width / 3 || point.X > width * 2 / 3)
        {
            var onLeft = point.X < width / 3;
            var direction = ReaderPaginationPolicy.GetClickDirection(
                onLeft,
                _readerLayout.VerticalWriting);
            e.Handled = true;
            HideReaderSelectionPopup();
            _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
        }
    }

    private void ReaderLinuxTextFallback_KeyDown(object? sender, KeyEventArgs e)
    {
        TryHandleLinuxReaderTextFallbackKeyDown(e);
    }

    private bool TryHandleLinuxReaderTextFallbackKeyDown(KeyEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive() || IsReaderTextInputFocused()) return false;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return false;

        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleReaderZenMode();
            return true;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleReaderEscapeShortcut();
            return e.Handled;
        }
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.F)
        {
            e.Handled = true;
            OpenReaderSearchShortcut();
            return true;
        }
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.B)
        {
            e.Handled = true;
            _ = ObserveReaderTaskAsync(ToggleReaderBookmarkAsync());
            return true;
        }

        if (_readerLayout.FlowMode == 1)
        {
            var chapterDirection = !_readerIsPdf
                ? e.Key == Key.Up
                    ? -1
                    : e.Key == Key.Down
                        ? 1
                        : 0
                : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return true;
            }

            // Vertical writing mirrors the horizontal key map: left turns
            // forward and right turns backward; PageUp/PageDown stay put.
            var verticalFlip = !_readerIsPdf && _readerLayout.VerticalWriting;
            var direction = e.Key switch
            {
                Key.Left => verticalFlip ? 1 : -1,
                Key.PageUp => -1,
                Key.Right => verticalFlip ? -1 : 1,
                Key.PageDown => 1,
                _ => 0
            };
            if (direction != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
                return true;
            }
        }
        else if (!_readerIsPdf && _readerDocument is not null)
        {
            var chapterDirection = e.Key == Key.Left
                ? -1
                : e.Key == Key.Right
                    ? 1
                    : 0;
            if (chapterDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(chapterDirection, chapterOnly: true));
                return true;
            }

            var scrollDirection = e.Key == Key.Up
                ? -1
                : e.Key == Key.Down
                    ? 1
                    : 0;
            if (scrollDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(ScrollReaderWithKeyboardAsync(scrollDirection));
                return true;
            }

            var pageDirection = e.Key == Key.PageUp
                ? -1
                : e.Key == Key.PageDown
                    ? 1
                    : 0;
            if (pageDirection != 0)
            {
                e.Handled = true;
                ReaderLinuxTextFallbackOverlay.Focus();
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(pageDirection));
                return true;
            }
        }

        return false;
    }

    private void ReaderLinuxTextFallback_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        var direction = delta < 0 ? 1 : -1;
        if (_readerLayout.FlowMode == 1)
        {
            e.Handled = true;
            var wheelDelta = (int)Math.Round(-delta * (Math.Abs(delta) < 12 ? 120 : 1));
            if (_readerLinuxTextFallbackWheelDeltaRemainder != 0
                && Math.Sign(_readerLinuxTextFallbackWheelDeltaRemainder) != Math.Sign(wheelDelta))
            {
                _readerLinuxTextFallbackWheelDeltaRemainder = 0;
            }
            _readerLinuxTextFallbackWheelDeltaRemainder += wheelDelta;
            if (Math.Abs(_readerLinuxTextFallbackWheelDeltaRemainder) >= 120)
            {
                direction = _readerLinuxTextFallbackWheelDeltaRemainder > 0 ? 1 : -1;
                _readerLinuxTextFallbackWheelDeltaRemainder %= 120;
                _ = ObserveReaderTaskAsync(TurnReaderPageAsync(direction));
            }
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var current = Math.Clamp(ReaderLinuxTextFallbackScroll.Offset.Y, 0, maximum);
        var atEdge = direction < 0
            ? current <= 4
            : current >= maximum - 4;
        if (!atEdge)
        {
            _readerLinuxTextFallbackContinuousWheelDirection = direction;
            _readerLinuxTextFallbackContinuousWheelLastTick = Environment.TickCount64;
            return;
        }

        e.Handled = true;
        var now = Environment.TickCount64;
        var startsNewGesture = _readerLinuxTextFallbackContinuousWheelLastTick > 0
            && (direction != _readerLinuxTextFallbackContinuousWheelDirection
                || now - _readerLinuxTextFallbackContinuousWheelLastTick >= 180);
        _readerLinuxTextFallbackContinuousWheelDirection = direction;
        _readerLinuxTextFallbackContinuousWheelLastTick = now;
        if (startsNewGesture)
            TryMoveReaderChapterFromContinuousEdge(direction);
    }
}
