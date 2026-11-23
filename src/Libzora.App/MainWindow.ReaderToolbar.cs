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
/// Reader toolbar menus: TOC, search, flow mode, animation and
/// bookmarks.
/// </summary>
public partial class MainWindow
{
    private void ReaderTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerSearchVisible)
        {
            HideReaderSearchPanel(restorePreviousLayout: false);
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            ApplyReaderPanelLayout();
            return;
        }
        if (_readerTocMinimal)
        {
            _readerTocMinimal = false;
            _readerTocExpanded = true;
        }
        else
        {
            _readerTocExpanded = !_readerTocExpanded;
        }
        ApplyReaderPanelLayout();
        if (_readerTocExpanded)
        {
            ShowReaderTocTab();
            SetReaderTocSelectionForLocation(_readerChapterIndex, _readerCurrentFragment);
        }
    }

    private void ReaderTocList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderTocSelectionNavigation) return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReaderTocRow row)
        {
            if (_readerIsPdf)
            {
                NavigateToReaderTocItem(row.Item);
                return;
            }

            SetReaderTocCurrentRow(row);
            var item = row.Item;
            _ = ObserveReaderTaskAsync(
                NavigateToReaderItemAsync(
                    item,
                    _readerSession.GetTokenOr(CancellationToken.None),
                    ReaderNavigationIntent.Toc));
        }
    }

    private async void ReaderSearchButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (ReaderInPageSearchBar.IsVisible)
            {
                await ClearReaderSearchAsync();
                ReaderInPageSearchBar.IsVisible = false;
                ReaderInPageSearchBox.Text = string.Empty;
                return;
            }
            OpenReaderSearchShortcut();
        }
        catch (OperationCanceledException) when (ReaderToken.IsCancellationRequested)
        {
        }
    }

    private async void ReaderFlowModeItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (_readerIsPdf)
        {
            ReaderFlowButton.Flyout?.Hide();
            await SetReaderPdfDisplayModeAsync(tag switch
            {
                "scroll" => PdfReaderDisplayMode.Continuous,
                "double" => PdfReaderDisplayMode.TwoPage,
                _ => PdfReaderDisplayMode.SinglePage
            });
            return;
        }

        if (IsReaderMangaMode)
        {
            var direction = tag == "scroll"
                ? MangaReadingDirection.VerticalScroll
                : _readerMangaSettings.LastPaginatedDirection ?? _readerDetectedMangaDirection;
            var twoPageSpread = string.Equals(tag, "double", StringComparison.Ordinal);
            await ApplyReaderMangaSettingsChangeAsync(_readerMangaSettings with
            {
                Direction = direction,
                LastPaginatedDirection = direction is MangaReadingDirection.RightToLeft or MangaReadingDirection.LeftToRight
                    ? direction
                    : _readerMangaSettings.LastPaginatedDirection ?? _readerDetectedMangaDirection,
                TwoPageSpread = twoPageSpread
            });
            ShowReaderTransientStatus(
                direction == MangaReadingDirection.VerticalScroll
                    ? T("已切换为条漫阅读。")
                    : twoPageSpread
                        ? T("已切换为双页阅读。")
                        : T("已切换为单页阅读。"));
            return;
        }

        var requiredVerticalMode = "single";
        if (_readerLayout.VerticalWriting
            && !string.Equals(tag, requiredVerticalMode, StringComparison.Ordinal))
        {
            SyncReaderFlowMenu();
            ShowReaderTransientStatus(T("竖排模式仅支持单页阅读。关闭竖排后可选择滚动或双栏。"));
            return;
        }
            var flowMode = tag switch
            {
                "scroll" => 0,
                "double" => 1,
                _ => 1
            };
            var twoPage = string.Equals(tag, "double", StringComparison.Ordinal);
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage
        });
        SyncReaderFlowMenu();
        await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
            ShowReaderTransientStatus(
                _readerLayout.FlowMode == 0
                    ? T("已切换为滚动阅读。")
                    : _readerLayout.TwoPageMode
                        ? T("已切换为双栏阅读。")
                        : T("已切换为单页阅读。"));
    }

    private void SyncReaderFlowMenu()
    {
        if (ReaderScrollModeItem is null
            || ReaderSinglePageModeItem is null
            || ReaderTwoPageModeItem is null) return;
        var pdf = _readerIsPdf ? CurrentReaderHost as IPdfReadingHost : null;
        var flowMode = pdf is null ? _readerLayout.FlowMode : pdf.DisplayMode == PdfReaderDisplayMode.Continuous ? 0 : 1;
        var twoPage = pdf is null ? _readerLayout.TwoPageMode : pdf.DisplayMode == PdfReaderDisplayMode.TwoPage;
        var vertical = !_readerIsPdf && !IsReaderMangaMode && _readerLayout.VerticalWriting;
        ReaderScrollModeItem.Header = IsReaderMangaMode ? T("条漫") : T("滚动");
        ReaderTwoPageModeItem.Header = (_readerIsPdf || IsReaderMangaMode) ? T("双页") : T("双栏");
        if (vertical)
        {
            // Vertical writing always presents as single pages.
            flowMode = 1;
            twoPage = false;
        }

        ReaderScrollModeItem.IsChecked = flowMode == 0;
        ReaderSinglePageModeItem.IsChecked = flowMode == 1 && !twoPage;
        ReaderTwoPageModeItem.IsChecked = flowMode == 1 && twoPage;
        // The native engine now implements continuous scroll and the
        // two-column spread for horizontal writing; only vertical writing is
        // restricted to single pages.
        ReaderScrollModeItem.IsEnabled = !vertical;
        ReaderTwoPageModeItem.IsEnabled = !vertical;
        ReaderSinglePageModeItem.IsEnabled = true;
        if (ReaderFlowButton is not null)
        {
            // Icon-only button: keep the current mode in tooltip and
            // accessibility name, mirroring UpdateReaderToolbar.
            var flowLabel = flowMode == 0
                ? (IsReaderMangaMode ? T("条漫") : T("滚动"))
                : twoPage
                        ? ((_readerIsPdf || IsReaderMangaMode) ? T("双页") : T("双栏"))
                        : T("单页");
            ToolTip.SetTip(ReaderFlowButton, flowLabel);
            AutomationProperties.SetName(ReaderFlowButton, flowLabel);
            if (ReaderFlowIcon is not null)
                ReaderFlowIcon.Data = GetReaderFlowIcon(flowMode, twoPage);
        }
    }

    private void ReaderZenMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ReaderMoreButton.Flyout?.Hide();
        ToggleReaderZenMode();
    }

    private void ReaderLayoutSettingsMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        ReaderMoreButton.Flyout?.Hide();
        ReaderLayoutSettingsButton_Click(sender, e);
    }

    private void ReaderAnimationItem_Click(object? sender, RoutedEventArgs e)
    {
        // MenuFlyout can route Click through a generated MenuItem instance;
        // comparing the sender by object identity therefore loses the user's
        // choice and silently falls back to "无动画". The Tag is the stable
        // value declared in XAML and survives that routing behavior.
        var tag = (sender as MenuItem)?.Tag as string;
        _readerPageAnimation = tag switch
        {
            "fade" => ReaderAnimationFade,
            "slide" => ReaderAnimationSlide,
            "wave" => ReaderAnimationWave,
            "none" => ReaderAnimationNone,
            _ => _readerPageAnimation
        };
        SyncReaderAnimationMenu();
        ReaderMoreButton.Flyout?.Hide();
        ShowReaderTransientStatus(_readerPageAnimation switch
        {
            ReaderAnimationFade => T("翻页动画：淡入淡出"),
            ReaderAnimationSlide => T("翻页动画：左右滑动"),
            ReaderAnimationWave => T("翻页动画：电子墨水刷新"),
            _ => T("翻页动画：无动画")
        });
    }

    private void SyncReaderAnimationMenu()
    {
        if (ReaderAnimationNoneItem is null
            || ReaderAnimationFadeItem is null
            || ReaderAnimationSlideItem is null
            || ReaderAnimationWaveItem is null)
        {
            return;
        }
        ReaderAnimationNoneItem.IsChecked = _readerPageAnimation == ReaderAnimationNone;
        ReaderAnimationFadeItem.IsChecked = _readerPageAnimation == ReaderAnimationFade;
        ReaderAnimationSlideItem.IsChecked = _readerPageAnimation == ReaderAnimationSlide;
        ReaderAnimationWaveItem.IsChecked = _readerPageAnimation == ReaderAnimationWave;
    }

    private async void ReaderDecreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(-0.1);

    private async void ReaderIncreaseFontButton_Click(object? sender, RoutedEventArgs e) =>
        await ChangeReaderFontAsync(0.1);

    private async Task ChangeReaderFontAsync(double delta)
    {
        if (_readerIsPdf && CurrentReaderHost is IPdfZoomHost pdf)
        {
            await pdf.SetZoomAsync(pdf.Zoom + delta);
            UpdateReaderZoomLabel();
            return;
        }
        if (IsReaderMangaMode && CurrentReaderHost is NativeReaderHost manga)
        {
            var zoomStep = delta > 0 ? 0.25 : -0.25;
            manga.SetMangaZoom(manga.MangaZoom + zoomStep);
            UpdateReaderZoomLabel();
            return;
        }
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with { FontScale = _readerLayout.FontScale + delta });
        _readerBaseLayout = _readerLayout;
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
    }

    private async Task ResetReaderZoomOrFontAsync()
    {
        if (_readerIsPdf && CurrentReaderHost is IPdfReadingHost pdf)
        {
            await pdf.SetFitModeAsync(PdfReaderFitMode.Width);
            UpdateReaderZoomLabel();
            return;
        }
        if (IsReaderMangaMode && CurrentReaderHost is NativeReaderHost manga)
        {
            manga.ResetMangaZoom();
            UpdateReaderZoomLabel();
            return;
        }
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FontScale = ReaderLayoutDefaults.DefaultFontScale
        });
        _readerBaseLayout = _readerLayout;
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
    }

    private void ReaderBookmarkButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowReaderBookmarkTab();
    }

    private async void ReaderBookmarkCornerButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        await ToggleReaderBookmarkAsync();
    }

    private void UpdateReaderBookmarkCornerSurface()
    {
        // The corner click zone toggles a bookmark at the current position
        // for every reader surface: the native EPUB engine has no injected
        // page to draw the ribbon, so the Avalonia corner stays active here.
        ReaderBookmarkCornerButton.IsVisible = true;
        // PDF and Manga mode paint edge to edge. Outer padding and the old
        // WebView edge cover would expose bands or clip Webtoon artwork.
        ReaderWebViewHost.Margin = (_readerIsPdf || IsReaderMangaMode)
            ? new Thickness(0)
            : new Thickness(0, 0, 2, 0);
        ReaderWebViewBottomCover.IsVisible = !_readerIsPdf && !IsReaderMangaMode;
    }

    private async void ToggleReaderZenMode()
    {
        if (!_readerZenMode)
        {
            _readerWindowStateBeforeZen = WindowState;
            _readerWindowDecorationsThemeBeforeZen = WindowDecorationsTheme;
            WindowDecorationsTheme = (Avalonia.Styling.ControlTheme)Resources["ReaderZenWindowDecorations"]!;
            _readerAssistantVisibleBeforeZen = ReaderAssistantPanel.IsVisible;
            _readerTocExpandedBeforeZen = ReaderTocPanel.IsVisible;
            _readerTocMinimalBeforeZen = ReaderTocCompactPanel.IsVisible;
            CaptureReaderWindowStyleBeforeZen();
            _readerZenMode = true;
            // True fullscreen hides the taskbar. The empty decorations theme
            // removes Fluent's separate fullscreen caption popover.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 0;
            WindowState = WindowState.FullScreen;
            ApplyReaderZenWindowChrome();
            Dispatcher.UIThread.Post(
                ApplyReaderZenWindowChrome,
                DispatcherPriority.Render);
            ReaderAssistantPanel.IsVisible = false;
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
            ReaderAssistantToggleButton.IsVisible = false;
            ReaderZenBar.IsVisible = false;
            if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = true;
            // Zen mode starts with the minimal TOC rail, matching the WinUI
            // reference: the full TOC panel is collapsed and only the 52-DIP
            // marker rail keeps the chapter map visible. The content header and
            // footer bars collapse so the body fills the whole reading area.
            _readerTocExpanded = false;
            _readerTocMinimal = true;
            ReaderContentPanel.RowDefinitions[0].Height = new GridLength(0);
            ReaderHeaderBar.IsVisible = false;
            ReaderContentPanel.RowDefinitions[2].Height = new GridLength(0);
            ReaderFooterBar.IsVisible = false;
            ReaderTocPanel.Margin = new Thickness(0);
            ReaderTocCompactPanel.Margin = new Thickness(0);
            ReaderContentPanel.Margin = new Thickness(0);
            ReaderAssistantPanel.Margin = new Thickness(0);
            ReaderWebViewBottomCover.Margin = new Thickness(0);
            UpdateReaderBookmarkCornerSurface();
            ApplyReaderPanelLayout();
            UpdateReaderZenTocToggle();
            // The old reader enters distraction-free mode immediately. Mouse
            // movement reveals the title controls again when they are needed.
            UpdateReaderZenChrome(visible: false);
            StartReaderZenPointerWatch();
            StartReaderZenNativeChromeWatch();
        }
        else
        {
            await ExitReaderZenModeSmoothlyAsync();
        }
    }

    // Leaving zen restores the side panels, header/footer bars and the window
    // size in one go, which makes the paginated body text reflow and jump.
    // Mask that behind an opaque cover, restore everything, let the relayout
    // settle, then fade the cover away (WinUI reference behavior).
    private async Task ExitReaderZenModeSmoothlyAsync()
    {
        try
        {
            ReaderTransitionCover.Opacity = 1;
            ExitReaderZenModeCore();
            await Task.Delay(320);
            await FadeReaderTransitionCoverAsync(1, 0, 180);
        }
        catch
        {
            ReaderTransitionCover.Opacity = 0;
        }
    }

    private async Task FadeReaderTransitionCoverAsync(double from, double to, int durationMs)
    {
        try
        {
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(durationMs),
                FillMode = Avalonia.Animation.FillMode.Forward
            };
            var frame = new KeyFrame { Cue = new Cue(1d) };
            frame.Setters.Add(new Avalonia.Styling.Setter(Border.OpacityProperty, to));
            animation.Children.Add(frame);
            ReaderTransitionCover.Opacity = from;
            await animation.RunAsync(ReaderTransitionCover);
        }
        catch
        {
        }
        ReaderTransitionCover.Opacity = to;
    }

    private void ExitReaderZenMode()
    {
        if (!_readerZenMode) return;
        ReaderTransitionCover.Opacity = 0;
        ExitReaderZenModeCore();
    }

    private void ExitReaderZenModeCore()
    {
        if (!_readerZenMode) return;
        WindowState = _readerWindowStateBeforeZen;
        WindowDecorationsTheme = _readerWindowDecorationsThemeBeforeZen;
        // macOS restores its native traffic lights together with the title
        // row; Windows/Linux keep the self-drawn borderless frame.
        ExtendClientAreaToDecorationsHint = MacTrafficLights.IsSupported;
        ExtendClientAreaTitleBarHeightHint = MacTrafficLights.IsSupported
            ? MacTrafficLights.TitleBarHeight
            : -1;
        WindowDecorations = MacTrafficLights.IsSupported
            ? Avalonia.Controls.WindowDecorations.Full
            : Avalonia.Controls.WindowDecorations.None;
        _readerZenMode = false;
        RestoreReaderWindowChromeAfterZen();
        ReaderWindowTitleBar.IsVisible = true;
        ReaderTocPanel.IsVisible = false;
        ReaderAssistantPanel.IsVisible = _readerAssistantVisibleBeforeZen;
        if (_readerAssistantVisibleBeforeZen)
            SetReaderAiPanelWidth(GetReaderAssistantPanelWidth());
        else
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        ReaderAssistantToggleButton.IsVisible = true;
        ReaderZenBar.IsVisible = false;
        if (ReaderZenMenuItem is not null) ReaderZenMenuItem.IsChecked = false;
        _readerTocExpanded = _readerTocExpandedBeforeZen;
        _readerTocMinimal = _readerTocMinimalBeforeZen;
        ReaderContentPanel.RowDefinitions[0].Height = new GridLength(52);
        ReaderHeaderBar.IsVisible = true;
        ReaderContentPanel.RowDefinitions[2].Height = new GridLength(42);
        ReaderFooterBar.IsVisible = true;
        ReaderTocPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderTocCompactPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderContentPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderAssistantPanel.Margin = new Thickness(0, 38, 0, 0);
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0);
        ReaderWebViewBottomCover.Margin = new Thickness(0);
        UpdateReaderBookmarkCornerSurface();
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        // Leaving zen restores the chrome unconditionally; the hide timer must
        // not keep running for the bookshelf.
        _readerZenChromeHideTimer?.Stop();
        _readerZenChromeVisible = true;
        ReaderZenTitleTocButton.IsVisible = false;
        ReaderZenTitleExitButton.IsVisible = false;
        ReaderZenActivationRegion.IsVisible = false;
        ReaderZenControlsPopup.IsOpen = false;
        UpdateReaderTtsFloatingPointerWatch();
        SetWindowsCaptionButtonsVisible(true);
        WindowBrandText.IsVisible = ReaderRoot.IsVisible;
    }

    // Zen mode auto-hides the top chrome (brand text, zen title buttons and the
    // window caption buttons) so only the body remains; the minimal TOC rail on
    // the left is not part of this chrome and stays visible. Mouse movement
    // reveals it again, and it hides after ~2.5 s of inactivity.
}
