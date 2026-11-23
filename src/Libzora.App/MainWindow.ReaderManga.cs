using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Per-book desktop manga mode detection, settings, and layout/appearance projection.
/// </summary>
public partial class MainWindow
{
    private MangaBookSettings _readerMangaSettings = new();
    private MangaReadingDirection _readerDetectedMangaDirection = MangaReadingDirection.RightToLeft;
    private bool _suppressReaderMangaChange;

    private bool IsReaderMangaMode => !_readerIsPdf && _readerMangaSettings.Enabled;

    private bool IsReaderMangaVerticalScroll =>
        IsReaderMangaMode && _readerMangaSettings.Direction == MangaReadingDirection.VerticalScroll;

    private bool IsReaderMangaBlackCanvas =>
        IsReaderMangaMode && _readerMangaSettings.BlackBackground;

    private bool IsReaderRightToLeftPaging =>
        !_readerIsPdf
        && ((IsReaderMangaMode && _readerMangaSettings.Direction == MangaReadingDirection.RightToLeft)
            || _readerLayout.VerticalWriting);

    private void InitializeReaderMangaSettings(EpubReaderDocument document)
    {
        if (document.Chapters.Count == 0 || _readerBookFile is null)
        {
            _readerMangaSettings = new MangaBookSettings();
            _readerDetectedMangaDirection = MangaReadingDirection.RightToLeft;
            return;
        }

        if (_appSettings.TryGetMangaSettings(_readerBookFile.Id, out var saved))
        {
            _readerDetectedMangaDirection = saved.LastPaginatedDirection ?? MangaReadingDirection.RightToLeft;
            _readerMangaSettings = saved;
            return;
        }

        var detected = EpubMangaDetector.Detect(document);
        var direction = detected.IsLeftToRight
            ? MangaReadingDirection.LeftToRight
            : MangaReadingDirection.RightToLeft;
        _readerDetectedMangaDirection = direction;
        _readerMangaSettings = MangaBookSettings.Normalize(new MangaBookSettings
        {
            Enabled = detected.IsManga,
            Direction = direction,
            LastPaginatedDirection = direction
        });
        if (detected.IsManga)
        {
            _appSettings = AppSettings.Normalize(
                _appSettings.WithMangaSettings(_readerBookFile.Id, _readerMangaSettings));
            ScheduleAppSettingsAutoSave(showSavedStatus: false);
        }
    }

    private void ResetReaderMangaSession()
    {
        _readerMangaSettings = new MangaBookSettings();
        _readerDetectedMangaDirection = MangaReadingDirection.RightToLeft;
    }

    private ReaderLayoutSettings GetBaseReaderLayout() =>
        IsReaderMangaMode
            ? NormalizeReaderLayoutForPlatform(_readerBaseLayout)
            : NormalizeReaderLayoutForPlatform(_readerLayout);

    internal static ReaderLayoutSettings BuildEffectiveProseLayout(ReaderLayoutSettings normalizedBase) =>
        normalizedBase with
        {
            MangaMode = false,
            MangaAutoCrop = false,
            MangaRightToLeft = false,
            ParagraphSpacingMatchesLineHeight = normalizedBase.ParagraphSpacing <= 0,
            OverridePublisherLineHeight = true,
            OverridePublisherParagraphSpacing = true,
            NaturalLatinTypography = true
        };

    private ReaderLayoutSettings BuildEffectiveReaderLayout(ReaderLayoutSettings baseLayout)
    {
        var normalizedBase = NormalizeReaderLayoutForPlatform(baseLayout);
        if (_readerIsPdf || !_readerMangaSettings.Enabled)
        {
            return BuildEffectiveProseLayout(normalizedBase);
        }

        var verticalScroll = _readerMangaSettings.Direction == MangaReadingDirection.VerticalScroll;
        return normalizedBase with
        {
            FlowMode = verticalScroll ? 0 : 1,
            VerticalWriting = false,
            TwoPageMode = !verticalScroll && _readerMangaSettings.TwoPageSpread,
            BodyPadding = 0,
            BodyBottomPadding = 0,
            MangaMode = true,
            MangaAutoCrop = _readerMangaSettings.AutoCrop,
            MangaRightToLeft = _readerMangaSettings.Direction == MangaReadingDirection.RightToLeft
        };
    }

    private void SyncReaderMangaControls()
    {
        if (ReaderMangaSection is null) return;
        _suppressReaderMangaChange = true;
        try
        {
            var isEpub = !_readerIsPdf;
            var mangaEnabled = isEpub && _readerMangaSettings.Enabled;
            var isWebtoon = _readerMangaSettings.Direction == MangaReadingDirection.VerticalScroll;
            ReaderMangaSection.IsVisible = isEpub;
            ReaderMangaModeCheck.IsChecked = mangaEnabled;
            ReaderMangaOptionsPanel.IsVisible = mangaEnabled;
            ReaderMangaSpreadPanel.IsVisible = mangaEnabled && !isWebtoon;
            ReaderTextLayoutPanel.IsVisible = isEpub && !mangaEnabled;

            ReaderMangaRtlOption.IsChecked = _readerMangaSettings.Direction == MangaReadingDirection.RightToLeft;
            ReaderMangaLtrOption.IsChecked = _readerMangaSettings.Direction == MangaReadingDirection.LeftToRight;
            ReaderMangaWebtoonOption.IsChecked = isWebtoon;
            ReaderMangaSinglePageOption.IsChecked = !_readerMangaSettings.TwoPageSpread;
            ReaderMangaTwoPageOption.IsChecked = _readerMangaSettings.TwoPageSpread;
            ReaderMangaAutoCropCheck.IsChecked = _readerMangaSettings.AutoCrop;
            ReaderMangaBlackBgCheck.IsChecked = _readerMangaSettings.BlackBackground;
        }
        finally
        {
            _suppressReaderMangaChange = false;
        }
    }

    private async void ReaderMangaModeCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderMangaChange || _readerIsPdf) return;
        var enabled = ReaderMangaModeCheck.IsChecked == true;
        await ApplyReaderMangaSettingsChangeAsync(_readerMangaSettings with { Enabled = enabled });
        ShowReaderTransientStatus(enabled ? T("已开启漫画模式。") : T("已关闭漫画模式。"));
    }

    private async void ReaderMangaDirectionOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderMangaChange || _readerIsPdf
            || sender is not RadioButton { IsChecked: true, Tag: string tag })
            return;

        var direction = tag switch
        {
            "ltr" => MangaReadingDirection.LeftToRight,
            "webtoon" => MangaReadingDirection.VerticalScroll,
            _ => MangaReadingDirection.RightToLeft
        };
        var lastPaginated = direction is MangaReadingDirection.RightToLeft or MangaReadingDirection.LeftToRight
            ? direction
            : _readerMangaSettings.LastPaginatedDirection ?? _readerDetectedMangaDirection;
        var next = _readerMangaSettings with
        {
            Direction = direction,
            LastPaginatedDirection = lastPaginated,
            TwoPageSpread = direction != MangaReadingDirection.VerticalScroll && _readerMangaSettings.TwoPageSpread
        };
        await ApplyReaderMangaSettingsChangeAsync(next);
    }

    private async void ReaderMangaSpreadOption_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderMangaChange || _readerIsPdf
            || sender is not RadioButton { IsChecked: true, Tag: string tag })
            return;

        var twoPage = string.Equals(tag, "double", StringComparison.Ordinal);
        await ApplyReaderMangaSettingsChangeAsync(_readerMangaSettings with { TwoPageSpread = twoPage });
    }

    private async void ReaderMangaAutoCropCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderMangaChange || _readerIsPdf) return;
        var autoCrop = ReaderMangaAutoCropCheck.IsChecked == true;
        await ApplyReaderMangaSettingsChangeAsync(_readerMangaSettings with { AutoCrop = autoCrop });
    }

    private async void ReaderMangaBlackBgCheck_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressReaderMangaChange || _readerIsPdf) return;
        var blackBg = ReaderMangaBlackBgCheck.IsChecked == true;
        await ApplyReaderMangaSettingsChangeAsync(_readerMangaSettings with { BlackBackground = blackBg });
    }

    private async Task ApplyReaderMangaSettingsChangeAsync(MangaBookSettings next, bool persist = true)
    {
        if (!IsReaderMangaMode && next.Enabled)
        {
            _readerBaseLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
            {
                MangaMode = false,
                MangaAutoCrop = false,
                MangaRightToLeft = false
            });
        }

        var normalized = MangaBookSettings.Normalize(next);
        _readerMangaSettings = normalized;
        if (persist && _readerBookFile is not null)
        {
            _appSettings = AppSettings.Normalize(
                _appSettings.WithMangaSettings(_readerBookFile.Id, normalized));
            await SaveAppSettingsDirectAsync(CancellationToken.None);
        }

        _readerLayout = BuildEffectiveReaderLayout(_readerBaseLayout);
        SyncReaderMangaControls();
        SyncReaderFlowMenu();
        ApplyReaderAppearance();
        UpdateReaderBookmarkCornerSurface();
        UpdateReaderZoomLabel();
        try
        {
            await ApplyReaderLayoutToHostsAsync(_readerSession.GetTokenOr(CancellationToken.None));
        }
        catch (OperationCanceledException) when (_readerSession.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task SaveAppSettingsDirectAsync(CancellationToken cancellationToken)
    {
        await _appSettingsSaveGate.WaitAsync(cancellationToken);
        try
        {
            await _appSettingsStore.SaveAsync(_appSettings, cancellationToken);
            ReaderAppearanceSaveStatus.IsVisible = false;
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
        }
        catch (Exception exception)
        {
            ReaderAppearanceSaveStatus.Text = T("保存失败：{0}", UiText.Localize(exception.Message));
            ReaderAppearanceSaveStatus.IsVisible = true;
        }
        finally
        {
            _appSettingsSaveGate.Release();
        }
    }
}
