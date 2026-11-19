using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

namespace Libzora;

/// <summary>
/// Title-bar controls, window-state animations, layout sizing and
/// accessibility tooltips.
/// </summary>
public partial class MainWindow
{
    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        BeginMoveDrag(e);
    }

    private void MinimizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (WindowState == WindowState.Minimized || _windowMinimizeAnimationInProgress)
            return;

        _windowMinimizeAnimationInProgress = true;
        var animationVersion = ++_windowStateAnimationVersion;
        var animationCancellation = BeginWindowStateAnimation();
        var animationToken = animationCancellation.Token;
        _ = FinishWindowMinimizeAnimationAsync(
            animationVersion,
            animationCancellation,
            animationToken,
            Math.Clamp(Opacity, 0, 1));
    }

    private async Task FinishWindowMinimizeAnimationAsync(
        int animationVersion,
        CancellationTokenSource animationCancellation,
        CancellationToken animationToken,
        double fromOpacity)
    {
        try
        {
            await RunWindowOpacityAnimationAsync(
                fromOpacity,
                0,
                new CubicEaseIn(),
                animationToken);
            if (animationVersion == _windowStateAnimationVersion
                && IsVisible
                && WindowState != WindowState.Minimized)
            {
                WindowState = WindowState.Minimized;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A platform compositor failure must not leave the window in a
            // half-faded, interactive state. The version guard prevents an
            // older animation from changing a newer restore/minimize request.
            if (animationVersion == _windowStateAnimationVersion)
            {
                Opacity = 0;
                if (IsVisible && WindowState != WindowState.Minimized)
                    WindowState = WindowState.Minimized;
            }
        }
        finally
        {
            if (animationVersion == _windowStateAnimationVersion)
            {
                _windowMinimizeAnimationInProgress = false;
                CompleteWindowStateAnimation(animationCancellation);
            }
        }
    }

    private void BeginWindowRestoreAnimation()
    {
        var animationVersion = ++_windowStateAnimationVersion;
        var animationCancellation = BeginWindowStateAnimation();
        var animationToken = animationCancellation.Token;
        Opacity = 0;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (animationVersion != _windowStateAnimationVersion
                    || animationToken.IsCancellationRequested
                    || !IsVisible
                    || WindowState == WindowState.Minimized)
                    return;

                _ = FinishWindowRestoreAnimationAsync(
                    animationVersion,
                    animationCancellation,
                    animationToken);
            },
            DispatcherPriority.Render);
    }

    private async Task FinishWindowRestoreAnimationAsync(
        int animationVersion,
        CancellationTokenSource animationCancellation,
        CancellationToken animationToken)
    {
        try
        {
            await RunWindowOpacityAnimationAsync(
                0,
                1,
                new CubicEaseOut(),
                animationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Restoring the window must remain reliable even when an optional
            // compositor does not support animated top-level opacity.
        }
        finally
        {
            if (animationVersion == _windowStateAnimationVersion)
            {
                if (!animationToken.IsCancellationRequested
                    && IsVisible
                    && WindowState != WindowState.Minimized)
                {
                    Opacity = 1;
                }
                CompleteWindowStateAnimation(animationCancellation);
            }
        }
    }

    private CancellationTokenSource BeginWindowStateAnimation()
    {
        CancelWindowStateAnimation();
        var cancellation = new CancellationTokenSource();
        _windowStateAnimationCancellation = cancellation;
        return cancellation;
    }

    private void CompleteWindowStateAnimation(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_windowStateAnimationCancellation, cancellation))
            return;

        _windowStateAnimationCancellation = null;
        cancellation.Dispose();
    }

    private void CancelWindowStateAnimation()
    {
        var cancellation = _windowStateAnimationCancellation;
        _windowStateAnimationCancellation = null;
        if (cancellation is null)
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task RunWindowOpacityAnimationAsync(
        double from,
        double to,
        Easing easing,
        CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(WindowStateAnimationDurationMs),
            Easing = easing,
            FillMode = FillMode.Forward
        };
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, from) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, to) }
        });
        await animation.RunAsync(this, cancellationToken);
    }

    private async void MaximizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_readerZenMode)
        {
            await ExitReaderZenModeSmoothlyAsync();
            return;
        }

        ToggleMaximized();
    }

    // The caption X saves and closes the current reading session. From the
    // library it parks the application in the system tray; 退出 exits it.
    private async void CloseWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Repeated clicks while the reading checkpoint is being saved must
        // not hide the main window as soon as the reader view disappears.
        if (Volatile.Read(ref _readerCloseInProgress) != 0) return;
        if (ReaderRoot.IsVisible)
        {
            await CloseReaderAsync();
            return;
        }

        _windowStateAnimationVersion++;
        _windowMinimizeAnimationInProgress = false;
        CancelWindowStateAnimation();
        Opacity = 1;
        Hide();
    }

    private void ToggleMaximized()
        => WindowState = WindowState is WindowState.Maximized or WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        MaximizeWindowGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        AutomationProperties.SetName(MaximizeWindowButton, isMaximized ? T("还原") : T("最大化"));
        if (ReaderZenPopupMaximizeGlyph is not null)
            ReaderZenPopupMaximizeGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        if (ReaderZenPopupMaximizeButton is not null)
            AutomationProperties.SetName(ReaderZenPopupMaximizeButton, isMaximized ? T("还原") : T("最大化"));
    }

    private void LibraryRoot_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (LibraryRoot.ColumnDefinitions.Count < 3) return;
        // All feature pages, including settings, share the main content column.
        UpdateSidebarWidth();
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[1], new GridLength(1, GridUnitType.Star));
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[2], new GridLength(0));
    }

    private void UpdateSidebarWidth()
    {
        if (LibraryRoot.ColumnDefinitions.Count < 1) return;
        // English navigation labels are wider than their Chinese counterparts;
        // give them enough room instead of trimming every section name.
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[0], new GridLength(UiText.IsEnglish ? 260 : 200));
    }

    // Cards keep their fixed slot. The virtualizing panel's width follows the
    // viewport so row breaks stay aligned with the visible shelf instead of
    // holding onto an old six-card desired width.
    private void UpdateBookGridLayout()
    {
        if (_bookGridPanel is null) return;
        _bookGridPanel.ItemWidth = BookGridSlotWidth;
        _bookGridPanel.ItemHeight = BookGridSlotHeight;
        BookGrid.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        var viewportWidth = LibraryContentHost.Bounds.Width > 0
            ? LibraryContentHost.Bounds.Width
            : LibraryWorkspace.Bounds.Width > 0
                ? LibraryWorkspace.Bounds.Width
                : BookGrid.Bounds.Width;
        if (viewportWidth > 0 && Math.Abs(_bookGridPanel.ViewportWidth - viewportWidth) > 0.5)
        {
            BookGrid.Width = viewportWidth;
            BookGrid.MinWidth = 0;
            _bookGridPanel.ViewportWidth = viewportWidth;
            _bookGridPanel.Width = viewportWidth;
            _bookGridPanel.MinWidth = 0;
        }
    }

    private static void SetGridColumnWidth(ColumnDefinition column, GridLength width)
    {
        if (!column.Width.Equals(width))
            column.Width = width;
    }

    private void SidebarSectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BookManagementSectionButton))
            BookManagementChildren.IsVisible = !BookManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, DeviceManagementSectionButton))
            DeviceManagementChildren.IsVisible = !DeviceManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, ReadingSectionButton))
            ReadingChildren.IsVisible = !ReadingChildren.IsVisible;

        UpdateSidebarSectionVisuals();
    }

    private void UpdateSidebarSectionVisuals()
    {
        BookManagementChevron.Data = Geometry.Parse(
            BookManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        DeviceManagementChevron.Data = Geometry.Parse(
            DeviceManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        ReadingChevron.Data = Geometry.Parse(
            ReadingChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);

        var sectionButtons = new[]
        {
            BookManagementSectionButton,
            DeviceManagementSectionButton,
            ReadingSectionButton
        };
        foreach (var button in sectionButtons)
        {
            var active = ReferenceEquals(button, _activeNavigationSectionButton);
            button.Classes.Set("active", active);
        }
    }

    // Interactive-control tool tips, mirroring the WinUI ToolTips.cs pass: the
    // first time the window opens, every Button/TextBox/ComboBox/NumberBox/
    // Slider/ToggleSwitch/CheckBox without an explicit tool tip gets one built
    // from its accessible name, content text or placeholder.
    private bool _interactiveToolTipsApplied;
    private readonly Dictionary<Control, string> _interactiveAutoToolTips = [];

    private void EnsureInteractiveControlToolTips()
    {
        if (_interactiveToolTipsApplied) return;
        _interactiveToolTipsApplied = true;
        ApplyInteractiveControlToolTips(this);
    }

    private void ApplyInteractiveControlToolTips(Visual root)
    {
        if (root is Control control)
        {
            if (!control.Classes.Contains("noAutoToolTip") && ToolTip.GetTip(control) is null)
            {
                var text = BuildControlToolTip(control);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ToolTip.SetTip(control, text);
                    _interactiveAutoToolTips[control] = text;
                }
            }
            foreach (var child in root.GetVisualChildren())
                ApplyInteractiveControlToolTips(child);
        }
    }

    private void RefreshInteractiveControlToolTips()
    {
        foreach (var entry in _interactiveAutoToolTips.ToArray())
        {
            var control = entry.Key;
            if (ToolTip.GetTip(control) is not string current
                || !string.Equals(current, entry.Value, StringComparison.Ordinal))
            {
                // A later feature-specific tooltip owns this control now.
                _interactiveAutoToolTips.Remove(control);
                continue;
            }

            var text = BuildControlToolTip(control);
            if (string.IsNullOrWhiteSpace(text))
            {
                ToolTip.SetTip(control, null);
                _interactiveAutoToolTips.Remove(control);
            }
            else
            {
                ToolTip.SetTip(control, text);
                _interactiveAutoToolTips[control] = text;
            }
        }
    }

    private static string? BuildControlToolTip(Control control)
    {
        var accessibleName = AutomationProperties.GetName(control);
        return control switch
        {
            ToggleSwitch toggleSwitch => DescribeField(accessibleName, T("切换开关")),
            CheckBox checkBox => DescribeField(accessibleName, T("切换选项")),
            // Visible text already describes the action. Icon templates may
            // still use a string as their content and need an explanation.
            Button button when button.ContentTemplate is null
                && !string.IsNullOrWhiteSpace(ReadContentText(button.Content)) => null,
            Button button => FirstNonEmpty(accessibleName, ReadContentText(button.Content)),
            ComboBox comboBox => DescribeField(accessibleName, T("选择选项")),
            NumericUpDown numberBox => DescribeField(accessibleName, T("输入或调整数值")),
            TextBox textBox => DescribeField(
                accessibleName,
                string.IsNullOrWhiteSpace(textBox.PlaceholderText) ? T("输入文本") : textBox.PlaceholderText),
            Slider slider => DescribeField(accessibleName, T("拖动以调整数值")),
            _ => null
        };
    }

    private static string? DescribeField(string? accessibleName, string action)
    {
        return string.IsNullOrWhiteSpace(accessibleName) ? action : $"{accessibleName}：{action}";
    }

    private static string? ReadContentText(object? content) => content switch
    {
        string text => text.Trim(),
        TextBlock textBlock => textBlock.Text?.Trim(),
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
