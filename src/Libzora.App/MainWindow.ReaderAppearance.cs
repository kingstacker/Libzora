using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

public partial class MainWindow
{
    private bool _suppressReaderAppearanceChange = true;
    private Task _readerAppearanceSaveTask = Task.CompletedTask;
    private ReaderBookLayoutProfile? _readerBookLayoutProfile;

    private bool CanUseReaderBookProfile =>
        !_readerIsPdf && _readerBookCard is not null && _readerBookFile is not null;

    private bool ReaderUsesBookOverride =>
        CanUseReaderBookProfile && _readerBookLayoutProfile?.UseBookOverride == true;

    private ReaderAppearanceSettings BaseReaderAppearance =>
        ReaderUsesBookOverride
            ? ReaderAppearanceSettings.Normalize(_readerBookLayoutProfile!.Appearance)
            : ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance);

    private Avalonia.Platform.IPlatformSettings? ResolvePlatformSettings() =>
        this.GetPlatformSettings() ?? Application.Current?.PlatformSettings;

    private bool IsSystemDarkAppearance() =>
        ResolvePlatformSettings()?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;

    private ReaderAppearanceSettings ResolveEffectiveReaderAppearance(ReaderAppearanceSettings appearance)
    {
        if (IsReaderMangaBlackCanvas)
        {
            return new ReaderAppearanceSettings
            {
                Theme = ReaderTheme.Ink,
                PaperEnabled = false,
                FibersEnabled = false,
                AnnotationDisplayMode = appearance.AnnotationDisplayMode
            };
        }

        if (!appearance.FollowSystemAppearance) return appearance;
        return appearance with
        {
            Theme = IsSystemDarkAppearance() ? ReaderTheme.Ink : ReaderTheme.Classic
        };
    }

    private ReaderAppearanceSettings CurrentReaderAppearance =>
        ResolveEffectiveReaderAppearance(BaseReaderAppearance);

    private void InitializeReaderAppearance()
    {
        ReaderBookInfoText.LayoutUpdated += (_, _) => UpdateReaderBookTitleToolTip();
        if (ResolvePlatformSettings() is { } platformSettings)
        {
            platformSettings.ColorValuesChanged += (_, _) =>
            {
                if (ReaderRoot.IsVisible && !IsReaderMangaBlackCanvas && BaseReaderAppearance.FollowSystemAppearance)
                    Avalonia.Threading.Dispatcher.UIThread.Post(ApplyReaderAppearance);
            };
        }
        ApplyReaderAppearance();
        ReaderRoot.PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;

            UpdateReaderTitleAppearance();
            if (!ReaderRoot.IsVisible)
                HideReaderDictionaryPopup(clearSelection: true);
        };
        ReaderRoot.SizeChanged += (_, _) =>
        {
            if (ReaderLayoutSettingsPopup.IsOpen) UpdateReaderSettingsPopupSize();
        };
        UpdateReaderTitleAppearance();
        SyncReaderAppearanceControls();
    }

    private void UpdateReaderBookTitleToolTip()
    {
        var title = ReaderBookInfoText.Text;
        var trimmed = !string.IsNullOrWhiteSpace(title)
            && ReaderBookInfoText.TextLayout.TextLines.Any(line => line.HasCollapsed);
        if (!trimmed)
        {
            ToolTip.SetIsOpen(ReaderBookInfoText, false);
            ToolTip.SetTip(ReaderBookInfoText, null);
            return;
        }

        if (ToolTip.GetTip(ReaderBookInfoText) is TextBlock tip && tip.Text == title) return;
        ToolTip.SetTip(ReaderBookInfoText, new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            // The title is a single-line, trimmed TextBlock. Override these
            // inherited text settings on the popup so it shows the whole title.
            MaxLines = 0,
            TextTrimming = TextTrimming.None,
            MaxWidth = 480
        });
    }

    private void ApplyReaderAppearance()
    {
        var appearance = CurrentReaderAppearance;
        var pureBlack = IsReaderMangaBlackCanvas;
        ReaderAppearanceResources.Populate(Resources, appearance, pureBlack);
        ApplyReaderTocFontSize();
        ReaderAppearanceResources.PopulateControls(ReaderThemeScope.Resources, appearance, pureBlack);
        ReaderThemeScope.RequestedThemeVariant = (appearance.Theme == ReaderTheme.Ink || pureBlack)
            ? ThemeVariant.Dark
            : ThemeVariant.Light;
        (_readerActiveHost as NativeReaderHost)?.SetPureBlackBackground(pureBlack);
        (_readerPreloadHost as NativeReaderHost)?.SetPureBlackBackground(pureBlack);
        (_readerActiveHost as IReaderAppearanceHost)?.SetAppearance(appearance);
        (_readerPreloadHost as IReaderAppearanceHost)?.SetAppearance(appearance);
        UpdateReaderTitleAppearance();
        // Updating marker brushes leaves the rail's scroll offset and hover
        // wave intact; rebuilding its ItemsSource would move the chapter map.
        foreach (var item in ReaderTocCompactList.ItemsSource?.OfType<ReaderTocMarker>() ?? [])
            item.Palette = ReaderPalette.For(appearance.Theme, pureBlack);
        UpdateReaderCompactMarkerWave();
    }

    private void UpdateReaderTitleAppearance()
    {
        var visible = ReaderRoot.IsVisible;
        ReaderThemeScope.IsVisible = visible;
        ReaderWindowTitleBar.Classes.Set("readerChrome", visible);
        ReaderWindowTitleBar.Background = visible ? (IBrush)Resources["ReaderPageBrush"]! : Brushes.Transparent;
    }

    private void SyncReaderAppearanceControls()
    {
        _suppressReaderAppearanceChange = true;
        try
        {
            var appearance = BaseReaderAppearance;
            foreach (var option in new[] { ReaderClassicThemeOption, ReaderBeigeThemeOption, ReaderGreenThemeOption, ReaderInkThemeOption })
                option.IsChecked = Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme) && theme == appearance.Theme;
            ReaderFollowSystemThemeCheck.IsChecked = appearance.FollowSystemAppearance;
            SelectReaderProgressDisplay(_appSettings.ReaderProgressDisplay);
            ReaderPaperEnabledCheck.IsChecked = appearance.PaperEnabled;
            ReaderPaperStrengthSlider.Value = appearance.PaperStrength * 100;
            ReaderTocFontSizeSlider.Value = _appSettings.ReaderTocFontSize;
            ReaderFibersEnabledCheck.IsChecked = appearance.FibersEnabled;
            SyncReaderMangaControls();
            ReaderLayoutResetButton.IsVisible = !_readerIsPdf;
            UpdateReaderAppearanceLabels();
        }
        finally { _suppressReaderAppearanceChange = false; }
    }

    private void UpdateReaderSettingsPopupSize()
    {
        var width = Math.Max(0, ReaderRoot.Bounds.Width);
        var height = Math.Max(0, ReaderRoot.Bounds.Height);
        ReaderLayoutSettingsOverlay.Width = width;
        ReaderLayoutSettingsOverlay.Height = height;
        ReaderLayoutSettingsCard.Width = Math.Min(540, Math.Max(1, width - 32));
        ReaderLayoutSettingsCard.MaxHeight = Math.Min(760, Math.Max(1, height - 32));
    }

    private void UpdateReaderAppearanceLabels()
    {
        var appearance = BaseReaderAppearance;
        ReaderPaperStrengthValueText.Text = $"{appearance.PaperStrength * 100:0}%";
        ReaderPaperOptions.IsEnabled = appearance.PaperEnabled;
        ReaderTocFontSizeValueText.Text = $"{_appSettings.ReaderTocFontSize:0} px";
    }

    private void ApplyReaderTocFontSize() =>
        Resources["ReaderTocFontSize"] = ReaderTocDefaults.NormalizeFontSize(_appSettings.ReaderTocFontSize);

    private void ReaderTocFontSize_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        var size = ReaderTocDefaults.NormalizeFontSize(ReaderTocFontSizeSlider.Value);
        if (_appSettings.ReaderTocFontSize == size) return;
        _appSettings = _appSettings with { ReaderTocFontSize = size };
        ApplyReaderTocFontSize();
        UpdateReaderAppearanceLabels();
        _readerAppearanceSaveTask = SaveReaderAppearanceAsync(globalOnly: true);
    }

    private void ReaderThemeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange || sender is not RadioButton { IsChecked: true } option
            || !Enum.TryParse<ReaderTheme>(option.Tag?.ToString(), out var theme)) return;
        _suppressReaderAppearanceChange = true;
        try { ReaderFollowSystemThemeCheck.IsChecked = false; }
        finally { _suppressReaderAppearanceChange = false; }
        ChangeReaderAppearance(BaseReaderAppearance with { Theme = theme, FollowSystemAppearance = false });
    }

    private void ReaderFollowSystemThemeCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(BaseReaderAppearance with
        {
            FollowSystemAppearance = ReaderFollowSystemThemeCheck.IsChecked == true
        });
    }

    private void SelectReaderProgressDisplay(ReaderProgressDisplayMode mode)
    {
        foreach (var option in new[]
        {
            ReaderProgressChaptersOption,
            ReaderProgressPagesOption,
            ReaderProgressTimeOption,
            ReaderProgressOffOption
        })
        {
            option.IsChecked = Enum.TryParse<ReaderProgressDisplayMode>(option.Tag?.ToString(), out var itemMode)
                && itemMode == mode;
        }
    }

    private void ReaderProgressDisplayOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange || sender is not RadioButton { IsChecked: true } option
            || !Enum.TryParse<ReaderProgressDisplayMode>(option.Tag?.ToString(), out var mode)) return;
        if (_appSettings.ReaderProgressDisplay == mode) return;
        _appSettings = _appSettings with { ReaderProgressDisplay = mode };
        UpdateReaderFooterProgressDisplay();
        _readerAppearanceSaveTask = SaveReaderAppearanceAsync(globalOnly: true);
    }

    private void ReaderPaperCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(BaseReaderAppearance with
        {
            PaperEnabled = ReaderPaperEnabledCheck.IsChecked == true,
            FibersEnabled = ReaderFibersEnabledCheck.IsChecked == true
        });
    }

    private void ReaderPaperStrength_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressReaderAppearanceChange) return;
        ChangeReaderAppearance(BaseReaderAppearance with { PaperStrength = ReaderPaperStrengthSlider.Value / 100 });
    }

    private void ChangeReaderAppearance(ReaderAppearanceSettings appearance)
    {
        appearance = ReaderAppearanceSettings.Normalize(appearance);
        if (BaseReaderAppearance == appearance) return;
        if (ReaderUsesBookOverride)
        {
            _readerBookLayoutProfile = _readerBookLayoutProfile! with { Appearance = appearance };
        }
        else
        {
            // Global appearance lives in the settings file. A per-book
            // override updates only that book's synced reader row.
            _appSettings = _appSettings with { ReaderAppearance = appearance };
        }
        ApplyReaderAppearance();
        UpdateReaderAppearanceLabels();
        _readerAppearanceSaveTask = SaveReaderAppearanceAsync();
    }

    private async Task SaveReaderAppearanceAsync(bool globalOnly = false)
    {
        if (!globalOnly && ReaderUsesBookOverride)
        {
            try
            {
                await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
                ReaderAppearanceSaveStatus.IsVisible = false;
            }
            catch (Exception exception)
            {
                ReaderAppearanceSaveStatus.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
                ReaderAppearanceSaveStatus.IsVisible = true;
            }
            return;
        }

        // Share the normal settings gate: shutdown's flush waits for this write,
        // even when the user closes immediately after changing a radio/slider.
        await _appSettingsSaveGate.WaitAsync();
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, CancellationToken.None);
            ReaderAppearanceSaveStatus.IsVisible = false;
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
        }
        catch (Exception exception)
        {
            ReaderAppearanceSaveStatus.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
            ReaderAppearanceSaveStatus.IsVisible = true;
        }
        finally { _appSettingsSaveGate.Release(); }
    }

    private async Task LoadReaderBookProfileAsync(CancellationToken cancellationToken)
    {
        _readerBookLayoutProfile = null;
        if (!CanUseReaderBookProfile) return;

        var profile = await _readerData.GetBookLayoutProfileAsync(
            _readerBookCard!.Book.Id,
            _readerBookFile!.Id,
            cancellationToken);
        _readerBookLayoutProfile = profile ?? new ReaderBookLayoutProfile(
            _readerBookCard.Book.Id,
            _readerBookFile.Id,
            false,
            NormalizeReaderLayoutForPlatform(_appSettings.DefaultReaderLayout),
            ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance));
    }

    private async Task SetReaderBookOverrideAsync(bool useBookOverride)
    {
        if (!CanUseReaderBookProfile) return;

        var globalLayout = NormalizeReaderLayoutForPlatform(_appSettings.DefaultReaderLayout);
        var globalAppearance = ReaderAppearanceSettings.Normalize(_appSettings.ReaderAppearance);
        var profile = _readerBookLayoutProfile ?? new ReaderBookLayoutProfile(
            _readerBookCard!.Book.Id,
            _readerBookFile!.Id,
            false,
            globalLayout,
            globalAppearance);

        if (useBookOverride && !profile.UseBookOverride)
        {
            // Enabling an override starts from the currently visible settings,
            // so the toggle itself never causes an unexpected visual jump.
            profile = profile with
            {
                UseBookOverride = true,
                Layout = GetBaseReaderLayout(),
                Appearance = BaseReaderAppearance
            };
        }
        else if (!useBookOverride)
        {
            profile = profile with
            {
                UseBookOverride = false,
                Layout = globalLayout,
                Appearance = globalAppearance
            };
        }
        else
        {
            profile = profile with { UseBookOverride = true };
        }

        _readerBookLayoutProfile = ReaderBookLayoutProfile.Normalize(profile);
        _readerBaseLayout = ReaderLayoutDefaults.ApplyPreferences(
            _readerBookLayoutProfile.Layout,
            globalLayout,
            _readerBookLayoutProfile.UseBookOverride);
        _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
        ApplyReaderAppearance();
        SyncReaderAppearanceControls();
        SyncReaderBookScopeControls();
        await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
        await SaveCurrentReaderPreferencesAsync(CancellationToken.None);
    }

    private async Task SaveCurrentReaderPreferencesAsync(
        CancellationToken cancellationToken)
    {
        if (_readerIsPdf) return;

        var layout = GetBaseReaderLayout();
        _readerBaseLayout = layout;
        if (!IsReaderMangaMode)
            _readerLayout = BuildEffectiveProseLayout(layout);
        if (ReaderUsesBookOverride)
        {
            _readerBookLayoutProfile = ReaderBookLayoutProfile.Normalize(
                _readerBookLayoutProfile! with { Layout = layout });
            await _readerData.SaveBookLayoutProfileAsync(
                _readerBookLayoutProfile,
                cancellationToken);
            return;
        }

        _appSettings = AppSettings.Normalize(_appSettings with
        {
            DefaultReaderLayout = layout
        });
        await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
        HandleLocalDataChanged(LocalDataChangeKind.Settings);
    }

    private void SyncReaderBookScopeControls()
    {
        if (ReaderBookScopeGlobalOption is null || ReaderBookScopeBookOption is null) return;
        var previous = _suppressReaderAppearanceChange;
        _suppressReaderAppearanceChange = true;
        try
        {
            ReaderBookScopeGlobalOption.IsChecked = !ReaderUsesBookOverride;
            ReaderBookScopeBookOption.IsChecked = ReaderUsesBookOverride;
            var canChooseScope = CanUseReaderBookProfile;
            ReaderBookScopePanel.IsVisible = canChooseScope;
            ReaderBookScopeGlobalOption.IsVisible = canChooseScope;
            ReaderBookScopeBookOption.IsVisible = canChooseScope;
            if (ReaderBookScopeHint is not null)
                ReaderBookScopeHint.IsVisible =
                    canChooseScope && ReaderUsesBookOverride;
        }
        finally
        {
            _suppressReaderAppearanceChange = previous;
        }
    }

    private async void ReaderBookScopeOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderAppearanceChange
            || sender is not RadioButton { IsChecked: true, Tag: string scope })
            return;
        await SetReaderBookOverrideAsync(
            string.Equals(scope, "book", StringComparison.Ordinal));
    }
}
