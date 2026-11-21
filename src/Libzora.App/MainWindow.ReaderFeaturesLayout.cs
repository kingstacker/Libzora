using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Text.RegularExpressions;

namespace Libzora;

/// <summary>
/// Progress slider and reader layout settings panel.
/// </summary>
public partial class MainWindow
{
    private void ReaderProgressSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_readerProgressSliderUpdating || !IsInitialized) return;
        if (_readerSliderDragging || _readerSliderPreviewVisible)
            UpdateReaderSliderPreview(GetReaderProgressThumb());
        _ = ObserveReaderTaskAsync(NavigateReaderProgressAsync(e.NewValue));
    }

    // The footer thumb and compact TOC share the same in-app chapter preview;
    // binding hover directly to Thumb keeps the track itself passive.
    private bool _readerSliderDragging;
    private bool _readerSliderPreviewVisible;
    private Thumb? _readerProgressHoverThumb;

    private void ReaderProgressSlider_Loaded(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(AttachReaderProgressThumbHover, DispatcherPriority.Background);
    }

    private void AttachReaderProgressThumbHover()
    {
        var thumb = GetReaderProgressThumb();
        if (thumb is null || ReferenceEquals(thumb, _readerProgressHoverThumb)) return;
        if (_readerProgressHoverThumb is not null)
        {
            _readerProgressHoverThumb.PointerEntered -= ReaderProgressThumb_PointerEntered;
            _readerProgressHoverThumb.PointerExited -= ReaderProgressThumb_PointerExited;
        }

        _readerProgressHoverThumb = thumb;
        thumb.PointerEntered += ReaderProgressThumb_PointerEntered;
        thumb.PointerExited += ReaderProgressThumb_PointerExited;
    }

    private void ReaderProgressThumb_PointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateReaderSliderPreview(sender as Thumb);
    }

    private void ReaderProgressThumb_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_readerSliderDragging) CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(ReaderProgressSlider).Properties.IsLeftButtonPressed)
        {
            _readerSliderDragging = true;
            UpdateReaderSliderPreview(GetReaderProgressThumb());
        }
    }

    private void ReaderProgressSlider_PointerMoved(object? sender, PointerEventArgs e)
    {
        var thumb = GetReaderProgressThumb();
        if (_readerSliderDragging || IsPointerOverReaderProgressThumb(e, thumb))
        {
            UpdateReaderSliderPreview(thumb);
            return;
        }

        CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _readerSliderDragging = false;
        var thumb = GetReaderProgressThumb();
        if (IsPointerOverReaderProgressThumb(e, thumb))
            UpdateReaderSliderPreview(thumb);
        else
            CloseReaderSliderPreview();
    }

    private void ReaderProgressSlider_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_readerSliderDragging) CloseReaderSliderPreview();
    }

    private Thumb? GetReaderProgressThumb() =>
        ReaderProgressSlider is null
            ? null
            : FindDescendants<Thumb>(ReaderProgressSlider).FirstOrDefault();

    private static bool IsPointerOverReaderProgressThumb(PointerEventArgs e, Thumb? thumb)
    {
        if (thumb is null || thumb.Bounds.Width <= 0 || thumb.Bounds.Height <= 0) return false;
        var point = e.GetPosition(thumb);
        return point.X >= 0
            && point.X <= thumb.Bounds.Width
            && point.Y >= 0
            && point.Y <= thumb.Bounds.Height;
    }

    private void UpdateReaderSliderPreview(Thumb? thumb)
    {
        if (ReaderProgressSlider is null) return;
        thumb ??= GetReaderProgressThumb();
        if (thumb is null) return;
        _readerSliderPreviewVisible = true;
        var index = _readerIsPdf
            ? Math.Clamp((int)Math.Round(ReaderProgressSlider.Value), 1, Math.Max(1, _readerPdfPages.Count)) - 1
            : Math.Clamp((int)Math.Round(ReaderProgressSlider.Value) - 1, 0, Math.Max(0, _readerTocItems.Count - 1));
        ShowReaderChapterPreview(thumb, index, placeAbove: true);
    }

    private void CloseReaderSliderPreview()
    {
        _readerSliderPreviewVisible = false;
        if (ReaderTocCompactPanel is not null && ReaderTocCompactPanel.IsVisible)
            UpdateReaderCompactMarkerWave();
        else
            HideReaderCompactHoverLabel();
    }

    // EPUB uses the visible TOC sequence so fragment subchapters are reachable;
    // PDF remains page-granular.
    private async Task NavigateReaderProgressAsync(double value)
    {
        if (_readerIsPdf)
        {
            var page = Math.Clamp((int)Math.Round(value), 1, Math.Max(1, _readerPdfPages.Count));
            await NavigatePdfPageAsync(page, ReaderToken);
            return;
        }
        if (_readerTocItems.Count == 0) return;
        var tocIndex = Math.Clamp((int)Math.Round(value) - 1, 0, _readerTocItems.Count - 1);
        if (tocIndex == GetCurrentReaderTocIndex()) return;
        await NavigateToReaderItemAsync(
            _readerTocItems[tocIndex],
            ReaderToken,
            ReaderNavigationIntent.Toc,
            transitionDirection: tocIndex < GetCurrentReaderTocIndex() ? -1 : 1);
    }

    private void ReaderLayoutSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        SyncReaderAppearanceControls();
        SyncReaderBookScopeControls();
        ReaderVerticalWritingCheck.Content = OperatingSystem.IsLinux()
            ? T("竖排排版（自绘单页）")
            : T("竖排排版（仅支持单页）");
        _suppressReaderLayoutChange = true;
        try
        {
            var baseLayout = GetBaseReaderLayout();
            ReaderFontScaleSlider.Value = baseLayout.FontScale;
            ReaderLineHeightSlider.Value = baseLayout.LineHeight;
            ReaderMaxWidthSlider.Value = baseLayout.MaxWidth;
            ReaderBodyPaddingSlider.Value = baseLayout.BodyPadding;
            ReaderVerticalWritingCheck.IsChecked = baseLayout.VerticalWriting;
            ReaderParagraphIndentCheck.IsChecked = baseLayout.ParagraphIndent;
            ReaderHyphenationCheck.IsChecked = baseLayout.HyphenateEnglish;
            SelectReaderFontFamily(baseLayout.FontFamily);
            SelectReaderFlowMode(_readerLayout.FlowMode, _readerLayout.TwoPageMode);
            SelectReaderPageAnimation(_readerPageAnimation);
        }
        finally
        {
            _suppressReaderLayoutChange = false;
        }
        UpdateReaderLayoutSliderLabels();
        ReaderLayoutSettingsPopup.PlacementTarget = ReaderRoot;
        ReaderLayoutSettingsPopup.Placement = PlacementMode.AnchorAndGravity;
        ReaderLayoutSettingsPopup.PlacementAnchor = PopupAnchor.TopLeft;
        ReaderLayoutSettingsPopup.PlacementGravity = PopupGravity.BottomRight;
        ReaderLayoutSettingsPopup.HorizontalOffset = 0;
        ReaderLayoutSettingsPopup.VerticalOffset = 0;
        ReaderLayoutSettingsOverlay.Margin = new Thickness(0);
        UpdateReaderSettingsPopupSize();
        ReaderLayoutSettingsPopup.IsOpen = true;
    }

    // ValueChanged / SelectionChanged can fire while XAML is still being
    // parsed (slider Minimum/Maximum/Value assignment clamps the value and
    // raises the event before sibling controls exist). Guard every layout
    // event handler so a partially-initialized panel can never be touched.
    private bool AreReaderLayoutControlsReady() =>
        ReaderFontScaleSlider is not null
        && ReaderLineHeightSlider is not null
        && ReaderMaxWidthSlider is not null
        && ReaderBodyPaddingSlider is not null
        && ReaderFontFamilyBox is not null
        && ReaderVerticalWritingCheck is not null
        && ReaderParagraphIndentCheck is not null
        && ReaderHyphenationCheck is not null;

    private bool _suppressReaderLayoutChange;
    private CancellationTokenSource? _readerLayoutApplyCancellation;

    private void ReaderLayoutSettingChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        UpdateReaderLayoutSliderLabels();
        ScheduleReaderLayoutApply();
    }

    private void ReaderFontFamilyBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        ScheduleReaderLayoutApply();
    }

    private async void ReaderVerticalWritingCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;

        _readerBaseLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
        _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
        SyncReaderFlowMenu();
        try
        {
            UpdateReaderZoomLabel();
            await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
            await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (_readerSession.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private void ReaderParagraphIndentCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        ScheduleReaderLayoutApply();
    }

    private void ReaderHyphenationCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderLayoutChange || !AreReaderLayoutControlsReady()) return;
        ScheduleReaderLayoutApply();
    }

    private void ScheduleReaderLayoutApply()
    {
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = new CancellationTokenSource();
        var token = _readerLayoutApplyCancellation.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(160);
            if (token.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    _readerBaseLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
                    _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
                    _readerPageAnimation = GetSelectedReaderPageAnimation();
                    UpdateReaderZoomLabel();
                    await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
                    await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
                }
                catch
                {
                }
            });
        });
    }

    private ReaderLayoutSettings ReadReaderLayoutFromControls() => new ReaderLayoutSettings(
        ReaderFontScaleSlider.Value,
        ReaderLineHeightSlider.Value,
        ReaderMaxWidthSlider.Value,
        ReaderBodyPaddingSlider.Value,
        (ReaderFontFamilyBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
        GetBaseReaderLayout().FlowMode,
        ReaderVerticalWritingCheck.IsChecked == true,
        GetBaseReaderLayout().TwoPageMode)
    {
        ParagraphIndent = ReaderParagraphIndentCheck.IsChecked == true,
        HyphenateEnglish = ReaderHyphenationCheck.IsChecked != false
    };

    private void ReaderLayoutSettingsCloseButton_Click(object? sender, RoutedEventArgs e)
        => ReaderLayoutSettingsPopup.IsOpen = false;

    private async void ReaderLayoutResetButton_Click(object? sender, RoutedEventArgs e)
    {
        _readerLayoutApplyCancellation?.Cancel();
        _readerLayoutApplyCancellation?.Dispose();
        _readerLayoutApplyCancellation = null;

        if (IsReaderMangaMode)
        {
            await ApplyReaderMangaSettingsChangeAsync(new MangaBookSettings
            {
                Enabled = true,
                Direction = _readerDetectedMangaDirection,
                LastPaginatedDirection = _readerDetectedMangaDirection,
                AutoCrop = false,
                BlackBackground = false,
                TwoPageSpread = false
            });
            return;
        }

        if (CanUseReaderBookProfile && ReaderUsesBookOverride)
        {
            var globalLayout = NormalizeReaderLayoutForPlatform(
                _appSettings.DefaultReaderLayout);
            _readerBookLayoutProfile = ReaderBookLayoutProfile.Normalize(
                _readerBookLayoutProfile! with
                {
                    UseBookOverride = false,
                    Layout = globalLayout,
                    Appearance = ReaderAppearanceSettings.Normalize(
                        _appSettings.ReaderAppearance)
                });
            _readerBaseLayout = globalLayout;
            _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
            _suppressReaderLayoutChange = true;
            try
            {
                ReaderFontScaleSlider.Value = _readerLayout.FontScale;
                ReaderLineHeightSlider.Value = _readerLayout.LineHeight;
                ReaderMaxWidthSlider.Value = _readerLayout.MaxWidth;
                ReaderBodyPaddingSlider.Value = _readerLayout.BodyPadding;
                ReaderVerticalWritingCheck.IsChecked = _readerLayout.VerticalWriting;
                ReaderParagraphIndentCheck.IsChecked = _readerLayout.ParagraphIndent;
                ReaderHyphenationCheck.IsChecked = _readerLayout.HyphenateEnglish;
                SelectReaderFontFamily(_readerLayout.FontFamily);
                SelectReaderFlowMode(_readerLayout.FlowMode, _readerLayout.TwoPageMode);
            }
            finally
            {
                _suppressReaderLayoutChange = false;
            }
            UpdateReaderLayoutSliderLabels();
            ApplyReaderAppearance();
            SyncReaderAppearanceControls();
            SyncReaderBookScopeControls();
            await ApplyReaderLayoutToHostsAsync(ReaderToken);
            // Turning the override off makes SaveCurrentReaderPreferencesAsync
            // enter the global-settings branch. Persist the disabled per-book
            // profile explicitly so reopening the book does not restore it.
            await _readerData.SaveBookLayoutProfileAsync(
                _readerBookLayoutProfile!,
                CancellationToken.None);
            return;
        }

        _suppressReaderLayoutChange = true;
        try
        {
            ReaderFontScaleSlider.Value = ReaderLayoutDefaults.DefaultFontScale;
            ReaderLineHeightSlider.Value = ReaderLayoutDefaults.DefaultLineHeight;
            ReaderMaxWidthSlider.Value = ReaderLayoutDefaults.DefaultMaxWidth;
            ReaderBodyPaddingSlider.Value = ReaderLayoutDefaults.DefaultBodyPadding;
            ReaderVerticalWritingCheck.IsChecked = false;
            ReaderParagraphIndentCheck.IsChecked = false;
            ReaderHyphenationCheck.IsChecked = true;
            SelectReaderFontFamily(ReaderFontDefaults.DefaultFamily);
            SelectReaderFlowMode(1, false);
            SelectReaderPageAnimation(ReaderAnimationFade);
        }
        finally
        {
            _suppressReaderLayoutChange = false;
        }
        UpdateReaderLayoutSliderLabels();
        _readerBaseLayout = NormalizeReaderLayoutForPlatform(ReadReaderLayoutFromControls());
        _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
        _readerPageAnimation = ReaderAnimationFade;
        UpdateReaderZoomLabel();
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
    }

    private void UpdateReaderLayoutSliderLabels()
    {
        ReaderFontScaleValueText.Text = $"{ReaderFontScaleSlider.Value:0.00}×";
        ReaderLineHeightValueText.Text = ReaderLineHeightSlider.Value.ToString("0.00");
        ReaderMaxWidthValueText.Text = $"{ReaderMaxWidthSlider.Value:0} px";
        ReaderBodyPaddingValueText.Text = $"{ReaderBodyPaddingSlider.Value:0} px";
    }

    private void SelectReaderFontFamily(string family)
    {
        var item = ReaderFontFamilyBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, family, StringComparison.OrdinalIgnoreCase));
        ReaderFontFamilyBox.SelectedItem = item ?? ReaderFontFamilyBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private (int FlowMode, bool TwoPageMode) GetSelectedReaderFlowMode()
        => (_readerLayout.FlowMode, _readerLayout.TwoPageMode);

    private void SelectReaderFlowMode(int flowMode, bool twoPageMode)
    {
        // The flow mode lives in the header menu (ReaderFlowButton); keep the
        // menu state in sync when the layout settings panel is opened.
        if (!IsReaderMangaMode)
        {
            _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
            {
                FlowMode = flowMode,
                TwoPageMode = twoPageMode
            });
        }
        SyncReaderFlowMenu();
    }

    private int GetSelectedReaderPageAnimation() => _readerPageAnimation;

    private void SelectReaderPageAnimation(int animation)
    {
        _readerPageAnimation = animation;
        SyncReaderAnimationMenu();
    }

    private void ReaderZenMinimalTocButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_readerZenMode) return;
        _readerTocExpanded = false;
        _readerTocMinimal = !_readerTocMinimal;
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
    }

    private void ReaderExitZenButton_Click(object? sender, RoutedEventArgs e) => ExitReaderZenMode();

    private void ReaderAssistantToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerZenMode) return;
        var visible = !ReaderAssistantPanel.IsVisible;
        ReaderAssistantPanel.IsVisible = visible;
        if (visible)
            SetReaderAiPanelWidth(GetReaderAssistantPanelWidth());
        else
            ReaderRoot.ColumnDefinitions[2].Width = new GridLength(0);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }
}
