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
/// Zen mode: window chrome, cursor watching and chrome auto-hide.
/// </summary>
public partial class MainWindow
{
    private bool _readerZenChromeVisible = true;
    private DispatcherTimer? _readerZenChromeHideTimer;
    private DispatcherTimer? _readerZenPointerWatchTimer;
    private DispatcherTimer? _readerZenNativeChromeWatchTimer;
    private long _readerZenLastMouseMoveTick;
    private int? _readerWindowStyleBeforeZen;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderNativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderNativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out ReaderNativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out ReaderNativeRect rect);

    private const int ReaderWindowStyleIndex = -16;
    private const int ReaderWindowCaptionStyle = 0x00C00000;
    private const int ReaderWindowResizeFrameStyle = 0x00040000;
    private const int ReaderWindowSystemMenuStyle = 0x00080000;
    private const int ReaderWindowMinimizeBoxStyle = 0x00020000;
    private const int ReaderWindowMaximizeBoxStyle = 0x00010000;
    private const int ReaderWindowZenNativeChromeStyles =
        ReaderWindowCaptionStyle
        | ReaderWindowResizeFrameStyle
        | ReaderWindowSystemMenuStyle
        | ReaderWindowMinimizeBoxStyle
        | ReaderWindowMaximizeBoxStyle;
    private const uint ReaderSetWindowPosNoSize = 0x0001;
    private const uint ReaderSetWindowPosNoMove = 0x0002;
    private const uint ReaderSetWindowPosNoZOrder = 0x0004;
    private const uint ReaderSetWindowPosNoActivate = 0x0010;
    private const uint ReaderSetWindowPosFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetReaderWindowStyle(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetReaderWindowStyle(IntPtr window, int index, int style);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(
        IntPtr display,
        IntPtr window,
        out IntPtr root,
        out IntPtr child,
        out int rootX,
        out int rootY,
        out int windowX,
        out int windowY,
        out uint mask);

    private static IntPtr _readerX11Display;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReaderCoreGraphicsPoint
    {
        public double X;
        public double Y;
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern ReaderCoreGraphicsPoint CGEventGetLocation(IntPtr @event);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);

    private static bool TryGetReaderCursorScreenPoint(out PixelPoint point)
    {
        point = default;
        if (OperatingSystem.IsWindows())
        {
            if (!GetCursorPos(out var cursor)) return false;
            point = new PixelPoint(cursor.X, cursor.Y);
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var eventHandle = CGEventCreate(IntPtr.Zero);
                if (eventHandle == IntPtr.Zero) return false;
                try
                {
                    // CoreGraphics and Avalonia's macOS backend both use screen
                    // points with a top-left origin, so no scaling is applied.
                    var location = CGEventGetLocation(eventHandle);
                    point = new PixelPoint(
                        (int)Math.Round(location.X),
                        (int)Math.Round(location.Y));
                    return true;
                }
                finally
                {
                    CFRelease(eventHandle);
                }
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            if (_readerX11Display == IntPtr.Zero)
                _readerX11Display = XOpenDisplay(IntPtr.Zero);
            if (_readerX11Display == IntPtr.Zero) return false;
            var rootWindow = XDefaultRootWindow(_readerX11Display);
            if (rootWindow == IntPtr.Zero
                || XQueryPointer(
                    _readerX11Display,
                    rootWindow,
                    out _,
                    out _,
                    out var rootX,
                    out var rootY,
                    out _,
                    out _,
                    out _) == 0)
                return false;
            point = new PixelPoint(rootX, rootY);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void StartReaderZenPointerWatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        _readerZenPointerWatchTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _readerZenPointerWatchTimer.Tick -= ReaderZenPointerWatchTimer_Tick;
        _readerZenPointerWatchTimer.Tick += ReaderZenPointerWatchTimer_Tick;
        if (!_readerZenPointerWatchTimer.IsEnabled)
            _readerZenPointerWatchTimer.Start();
    }

    private void ReaderZenPointerWatchTimer_Tick(object? sender, EventArgs e)
    {
        var ttsFloatingActive = IsReaderTtsFloatingActive();
        if ((!_readerZenMode && !ttsFloatingActive)
            || !OperatingSystem.IsWindows())
        {
            _readerZenPointerWatchTimer?.Stop();
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero
            || !GetCursorPos(out var cursor)
            || !GetWindowRect(handle, out var window))
        {
            return;
        }

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (_readerZenMode)
        {
            var activationWidth = ReaderZenActivationWidth * scaling;
            var activationHeight = ReaderZenActivationHeight * scaling;
            var insideTopRight = cursor.X >= window.Right - activationWidth
                && cursor.X <= window.Right
                && cursor.Y >= window.Top
                && cursor.Y <= window.Top + activationHeight;
            if (insideTopRight != _readerZenChromeVisible)
                UpdateReaderZenChrome(insideTopRight);
        }

        if (ttsFloatingActive)
            UpdateReaderTtsFloatingForScreenPointer(
                new PixelPoint(cursor.X, cursor.Y));
    }

    private void ReaderRoot_PointerMoved(object? sender, PointerEventArgs e)
    {
        // Windows uses the native screen-space watcher above for both the
        // Avalonia surface and the child WebView HWND. Mixing its physical
        // pixels with these DIP coordinates makes the two paths fight at
        // non-100% display scaling and repeatedly opens/closes the popup.
        if (OperatingSystem.IsWindows()) return;

        var rootPoint = e.GetPosition(ReaderRoot);
        var contentPoint = e.GetPosition(ReaderReadingArea);
        UpdateReaderTtsFloatingForPointer(
            contentPoint.X,
            contentPoint.Y,
            ReaderReadingArea.Bounds.Width,
            ReaderReadingArea.Bounds.Height);

        if (!_readerZenMode) return;
        var now = Environment.TickCount64;
        if (now - _readerZenLastMouseMoveTick <= 80) return;
        _readerZenLastMouseMoveTick = now;
        UpdateReaderZenChromeForPointer(
            rootPoint.X,
            rootPoint.Y,
            ReaderRoot.Bounds.Width);
    }

    private void UpdateReaderZenChromeForPointer(double x, double y, double surfaceWidth)
    {
        if (!_readerZenMode) return;

        var insideTopRight = y >= 0
            && y <= ReaderZenActivationHeight
            && x >= Math.Max(0, surfaceWidth - ReaderZenActivationWidth);
        if (insideTopRight)
        {
            if (!_readerZenChromeVisible)
                UpdateReaderZenChrome(visible: true);
            else
                RestartReaderZenChromeHideTimer();
        }
        else if (_readerZenChromeVisible)
        {
            UpdateReaderZenChrome(visible: false);
        }
    }

    private void ReaderTitleControls_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_readerZenMode) return;
        if (!_readerZenChromeVisible)
            UpdateReaderZenChrome(visible: true);
        _readerZenChromeHideTimer?.Stop();
    }

    private void ReaderTitleControls_PointerExited(object? sender, PointerEventArgs e)
    {
        // On Windows the native watcher closes the popup as soon as the
        // pointer leaves the complete activation region. A delayed close from
        // the smaller button row can otherwise race that watcher and flicker.
        if (_readerZenMode && !OperatingSystem.IsWindows())
            RestartReaderZenChromeHideTimer(500);
    }

    private void RestartReaderZenChromeHideTimer(int delayMs = 1200)
    {
        _readerZenChromeHideTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };
        _readerZenChromeHideTimer.Stop();
        _readerZenChromeHideTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _readerZenChromeHideTimer.Tick -= ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Tick += ReaderZenChromeHideTimer_Tick;
        _readerZenChromeHideTimer.Start();
    }

    private void ReaderZenChromeHideTimer_Tick(object? sender, EventArgs e)
    {
        _readerZenChromeHideTimer?.Stop();
        if (_readerZenMode) UpdateReaderZenChrome(visible: false);
    }

    private void ApplyReaderZenWindowChrome()
    {
        if (!_readerZenMode) return;

        // Hide the Avalonia-drawn title row, which is the second row shown in
        // the Windows build. ReaderZenControlsPopup remains independent and
        // continues to provide the custom zen exit row.
        ReaderWindowTitleBar.IsVisible = false;
        // AppKit needs the native titled window to complete its fullscreen
        // transition. Fullscreen itself hides the native title bar and lights.
        if (!OperatingSystem.IsMacOS())
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;

        // FullScreen/Maximized can cause Windows to restore the native
        // overlapped-window styles after Avalonia has applied
        // WindowDecorations=None. Strip the complete caption/control group at
        // the native level too, then notify DWM that the frame changed. Removing
        // only WS_CAPTION is insufficient: WS_SYSMENU plus the box styles can
        // still leave a thin native row with the window title and buttons.
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var style = GetReaderWindowStyle(handle, ReaderWindowStyleIndex);
        var zenStyle = style & ~ReaderWindowZenNativeChromeStyles;
        if (zenStyle != style)
        {
            SetReaderWindowStyle(handle, ReaderWindowStyleIndex, zenStyle);
        }
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            ReaderSetWindowPosNoSize
                | ReaderSetWindowPosNoMove
                | ReaderSetWindowPosNoZOrder
                | ReaderSetWindowPosNoActivate
                | ReaderSetWindowPosFrameChanged);
    }

    private void CaptureReaderWindowStyleBeforeZen()
    {
        if (!OperatingSystem.IsWindows() || _readerWindowStyleBeforeZen.HasValue)
            return;
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
            _readerWindowStyleBeforeZen = GetReaderWindowStyle(handle, ReaderWindowStyleIndex);
    }

    private void StartReaderZenNativeChromeWatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        _readerZenNativeChromeWatchTimer ??= new DispatcherTimer
        {
            // The Windows platform may apply its FullScreen/Maximized frame
            // after the property notification. Keep checking while zen mode is
            // active so that late platform passes cannot resurrect the native
            // title row above the custom popup.
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _readerZenNativeChromeWatchTimer.Tick -= ReaderZenNativeChromeWatchTimer_Tick;
        _readerZenNativeChromeWatchTimer.Tick += ReaderZenNativeChromeWatchTimer_Tick;
        if (!_readerZenNativeChromeWatchTimer.IsEnabled)
            _readerZenNativeChromeWatchTimer.Start();
    }

    private void ReaderZenNativeChromeWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (!_readerZenMode)
        {
            _readerZenNativeChromeWatchTimer?.Stop();
            return;
        }

        ApplyReaderZenWindowChrome();
    }

    private void RestoreReaderWindowChromeAfterZen()
    {
        _readerZenNativeChromeWatchTimer?.Stop();
        if (!OperatingSystem.IsWindows())
        {
            _readerWindowStyleBeforeZen = null;
            return;
        }

        var originalStyle = _readerWindowStyleBeforeZen;
        _readerWindowStyleBeforeZen = null;
        if (originalStyle is not { } style) return;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        SetReaderWindowStyle(handle, ReaderWindowStyleIndex, style);
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            ReaderSetWindowPosNoSize
                | ReaderSetWindowPosNoMove
                | ReaderSetWindowPosNoZOrder
                | ReaderSetWindowPosNoActivate
                | ReaderSetWindowPosFrameChanged);
    }

    private void UpdateReaderZenChrome(bool visible)
    {
        _readerZenChromeVisible = visible;
        // The titlebar layer itself is hidden for zen mode. Keep the invisible
        // activation region disabled as well so it cannot leave a hit-test or
        // composition strip at the top of the reader.
        ReaderZenActivationRegion.IsVisible = false;
        if (_readerZenMode) ApplyReaderZenWindowChrome();
        ReaderZenTitleTocButton.IsVisible = false;
        ReaderZenTitleExitButton.IsVisible = false;
        SetWindowsCaptionButtonsVisible(visible);
        if (_readerZenMode && visible)
        {
            // Anchor the fallback/native popup to the reader surface instead
            // of the hidden titlebar activation region.
            ReaderZenControlsPopup.PlacementTarget = ReaderRoot;
            ReaderZenControlsPopup.Placement = Avalonia.Controls.PlacementMode.AnchorAndGravity;
            ReaderZenControlsPopup.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopRight;
            ReaderZenControlsPopup.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomLeft;
            ReaderZenControlsPopup.HorizontalOffset = 0;
            ReaderZenControlsPopup.VerticalOffset = 0;
            ReaderZenControlsPopup.IsOpen = true;
        }
        else
        {
            ReaderZenControlsPopup.IsOpen = false;
        }
        // The brand text floats over the minimal TOC rail in zen mode, so it
        // stays hidden there; it is restored when returning to the bookshelf.
        WindowBrandText.IsVisible = !_readerZenMode
            && visible
            && ReaderRoot.IsVisible;

        if (visible && !OperatingSystem.IsWindows())
            RestartReaderZenChromeHideTimer();
        else
            _readerZenChromeHideTimer?.Stop();
    }

    // 滚动 / 单页 / 双栏 three-state icon for the flow (模式) button.
}
