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
/// TTS environment setup, settings and control visuals.
/// </summary>
public partial class MainWindow
{
    public Task InitializeTtsEnvironmentAsync()
    {
        if (_readerTtsEnvironmentTask is { } existing)
            return existing;

        _readerTtsEnvironmentTask = InitializeTtsEnvironmentCoreAsync();
        return _readerTtsEnvironmentTask;
    }

    private async Task InitializeTtsEnvironmentCoreAsync()
    {
        // Reports posted through Progress<T> can execute on the UI thread
        // after the bootstrap finished (stale callbacks); ignore them.
        var bootstrapCompleted = false;
        try
        {
            LogTtsDebug("env.begin", "bootstrap starting");
            _readerTtsNotice = T("正在自动准备听书环境…");
            UpdateReaderTtsUi();
            var progress = new Progress<TtsSetupProgress>(update =>
            {
                if (bootstrapCompleted) return;
                _readerTtsNotice = update.Message;
                LogTtsDebug("env.progress", update.Message);
                UpdateReaderTtsUi();
            });
            // Respect the first-run choice on later launches too. Read-aloud
            // still prepares the environment when the user actually uses it.
            var setupDeferred = !_appSettings.OnboardingCompleted
                || _appSettings.DeferTtsInstallationUntilUse;
            var availability = setupDeferred
                ? await _readerTts.CheckEnvironmentAsync(
                        _lifetimeCancellation.Token)
                    .ConfigureAwait(false)
                : await _readerTts.EnsureEnvironmentReadyAsync(
                        progress,
                        _lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            _readerTtsEnvironmentChecked = true;
            _readerTtsNotice = availability.IsAvailable
                ? null
                : availability.Message;
            LogTtsDebug("env.done", $"isAvailable={availability.IsAvailable} message={availability.Message}");
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
            LogTtsDebug("env.cancelled", "lifetime token cancelled");
        }
        catch (Exception exception)
        {
            _readerTtsEnvironmentChecked = false;
            _readerTtsNotice = T(
                "自动准备听书环境失败：{0}",
                UiText.Localize(exception.Message));
            LogTtsDebug("env.exception", exception.ToString());
        }
        finally
        {
            bootstrapCompleted = true;
            UpdateReaderTtsUi();
        }
    }

    private async Task InitializeReaderTtsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            _readerTtsSettings = await _ttsSettingsStore.LoadAsync(cancellationToken);
            ApplyReaderTtsSettingsToControls();
            if (!_readerTtsEnvironmentChecked)
            {
                // Dependency downloads/checks must not delay the first page.
                // The bootstrap handles failures and refreshes the TTS UI.
                _ = InitializeTtsEnvironmentAsync();
            }

            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "读取听书设置失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private void ApplyReaderTtsSettingsToControls()
    {
        if (ReaderTtsVoiceBox is null) return;

        _readerTtsSettings = TtsSettings.Normalize(_readerTtsSettings);
        ReaderTtsVoiceBox.Text = _readerTtsSettings.Voice;
        ReaderTtsSpeedSlider.Value = _readerTtsSettings.Speed;
        ReaderTtsVolumeSlider.Value = _readerTtsSettings.Volume;
        ReaderTtsPitchSlider.Value = _readerTtsSettings.Pitch;
        ReaderTtsAutoAdvanceCheck.IsChecked = _readerTtsSettings.AutoAdvance;
        ReaderTtsShowAllVoicesCheck.IsChecked = _readerTtsSettings.ShowAllVoices;
        UpdateReaderTtsValueLabels();
        RefreshReaderTtsModels();
    }

    private void CaptureReaderTtsSettingsFromControls()
    {
        _readerTtsSettings.Provider = TtsSettings.DefaultProvider;
        var selectedVoice = ReaderTtsVoiceBox.SelectedItem as ComboBoxItem;
        _readerTtsSettings.Voice = selectedVoice?.Tag as string
            ?? ReaderTtsVoiceBox.Text?.Trim()
            ?? TtsOptions.DefaultVoice;
        _readerTtsSettings.VoiceCulture = _readerTtsLastVoices
            .FirstOrDefault(voice => string.Equals(
                voice.Id,
                _readerTtsSettings.Voice,
                StringComparison.OrdinalIgnoreCase))
            ?.Culture ?? string.Empty;
        _readerTtsSettings.Speed = ReaderTtsSpeedSlider.Value;
        _readerTtsSettings.Volume = (int)Math.Round(ReaderTtsVolumeSlider.Value);
        _readerTtsSettings.Pitch = (int)Math.Round(ReaderTtsPitchSlider.Value);
        _readerTtsSettings.AutoAdvance = ReaderTtsAutoAdvanceCheck.IsChecked == true;
        _readerTtsSettings.ShowAllVoices = ReaderTtsShowAllVoicesCheck.IsChecked == true;
        if (ReaderTtsModelBox?.SelectedItem is ComboBoxItem selectedModel
            && selectedModel.Tag is string modelId)
        {
            _readerTtsSettings.Model = modelId;
        }
        if (TtsModelCatalog.TryParseVoiceId(
                _readerTtsSettings.Voice,
                out var voicePackageId,
                out _))
        {
            _readerTtsSettings.Model = voicePackageId;
        }
        _readerTtsSettings = TtsSettings.Normalize(_readerTtsSettings);
    }

    private void UpdateReaderTtsValueLabels()
    {
        if (ReaderTtsSpeedValueText is not null && ReaderTtsSpeedSlider is not null)
        {
            ReaderTtsSpeedValueText.Text = ReaderTtsSpeedSlider.Value
                .ToString("0.0×", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (ReaderTtsVolumeValueText is not null && ReaderTtsVolumeSlider is not null)
            ReaderTtsVolumeValueText.Text = $"{ReaderTtsVolumeSlider.Value:0}%";
        if (ReaderTtsPitchValueText is not null && ReaderTtsPitchSlider is not null)
            ReaderTtsPitchValueText.Text = $"{ReaderTtsPitchSlider.Value:+0;-0;0} Hz";
    }

    private void ReaderTtsSpeedSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void ReaderTtsVolumeSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void ReaderTtsPitchSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs e)
        => UpdateReaderTtsValueLabels();

    private void UpdateReaderTtsUi()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateReaderTtsUi);
            return;
        }
        if (ReaderTtsButton is null) return;

        // An image-only page/chapter has no current speech document, but it is
        // still a valid starting point: TtsService will advance to the next
        // chapter containing visible text when playback starts.
        var documentAvailable = _readerIsPdf
            ? _readerPdfPages.Any(page => !string.IsNullOrWhiteSpace(page.Text))
            : _readerDocument is not null
                && CurrentReaderHost is not null;
        var state = _readerTts.State;
        var isActive = IsReaderTtsActiveState(state);
        var buttonText = _readerTts.EnvironmentSetupInProgress
            ? T("准备中")
            : state switch
            {
                TtsPlaybackState.Generating => T("生成中"),
                TtsPlaybackState.Playing => T("朗读中"),
                TtsPlaybackState.Paused => T("已暂停"),
                TtsPlaybackState.AdvancingChapter => T("跳转中"),
                _ => T("听书")
            };
        // The header button is icon-only now; the state text keeps living in
        // the accessibility name so screen readers still announce 状态.
        AutomationProperties.SetName(ReaderTtsButton, buttonText);
        ToolTip.SetTip(
            ReaderTtsButton,
            _readerTts.EnvironmentSetupInProgress
                ? T("准备听书环境")
                : isActive ? T("停止听书") : T("开始听书"));
        UpdateReaderTtsButtonVisualState(state);
        ReaderTtsButton.IsEnabled = _readerTts.CanOpen
            && documentAvailable
            && !_readerTts.EnvironmentSetupInProgress;
        ReaderTtsTitleText.Text = T("听书");
        ReaderTtsDescriptionText.Text = _readerIsPdf
            ? T("当前 PDF 页面有可提取文本时可朗读；扫描图片页需要 OCR，当前版本不自动识别。")
            : (_readerTts.EngineId == LocalSystemTtsEngine.ProviderId
                ? T("使用本机离线语音（系统语音或已下载语音包），朗读无需联网，正文不会上传。")
                : T("在线语音需要联网，朗读文本将发送给语音服务商。"));
        ReaderTtsProviderLabelText.Text = T("语音引擎");
        ReaderTtsEngineText.Text = _readerTts.EngineId == LocalSystemTtsEngine.ProviderId
            ? T("本机离线语音") : _readerTts.EngineId;
        ReaderTtsPitchSlider.IsEnabled = _readerTts.EngineId != LocalSystemTtsEngine.ProviderId;
        ReaderTtsVoiceLabelText.Text = T("声音");
        ReaderTtsVoiceBox.PlaceholderText = T("选择语音");
        if (ReaderTtsShowAllVoicesCheck is not null)
        {
            ReaderTtsShowAllVoicesCheck.IsVisible =
                _readerTts.EngineId == LocalSystemTtsEngine.ProviderId;
            ReaderTtsShowAllVoicesCheck.Content = T("显示全部声音");
            ReaderTtsShowAllVoicesCheck.IsChecked = _readerTtsSettings.ShowAllVoices;
        }
        ReaderTtsPreviewButton.Content = _readerTtsPreviewInProgress
            ? T("试听中…")
            : T("试听");
        ReaderTtsPreviewSampleText.Text = T(
            "试听内容：{0}",
            TtsService.GetPreviewText(
                _readerTtsSettings.Voice,
                _readerTtsSettings.VoiceCulture));
        ReaderTtsRefreshVoicesButton.Content = T("刷新语音");
        ReaderTtsSpeedLabelText.Text = T("语速");
        ReaderTtsVolumeLabelText.Text = T("音量");
        ReaderTtsVolumeSlider.Maximum = _readerTts.EngineId == LocalSystemTtsEngine.ProviderId ? 100 : 200;
        ReaderTtsPitchLabelText.Text = T("音调");
        ReaderTtsAutoAdvanceCheck.Content = T("自动朗读下一章");
        ReaderTtsApplyButton.Content = T("应用设置");
        ReaderTtsPreviousSegmentButton.Content = T("上一段");
        ReaderTtsNextSegmentButton.Content = T("下一段");
        ReaderTtsStopButton.Content = T("停止");

        if (ReaderTtsModelPanel is not null)
        {
            ReaderTtsModelPanel.IsVisible =
                _readerTts.EngineId == LocalSystemTtsEngine.ProviderId;
        }
        UpdateReaderTtsModelUi();

        ReaderTtsPlayButton.Content = state switch
        {
            TtsPlaybackState.Playing => T("暂停"),
            TtsPlaybackState.Paused => T("继续"),
            TtsPlaybackState.Generating
                or TtsPlaybackState.AdvancingChapter => T("处理中…"),
            _ => T("播放")
        };
        ReaderTtsPlayButton.IsEnabled = _readerTts.CanOpen
            && documentAvailable
            && !_readerTtsPreviewInProgress
            && !_readerTts.EnvironmentSetupInProgress
            && state is not TtsPlaybackState.Generating
                and not TtsPlaybackState.AdvancingChapter;
        ReaderTtsPreviewButton.IsEnabled = _readerTts.CanOpen
            && !_readerTtsPreviewInProgress
            && !_readerTts.EnvironmentSetupInProgress;
        ReaderTtsStopButton.IsEnabled = state is not TtsPlaybackState.Stopped;
        ReaderTtsPreviousSegmentButton.IsEnabled = _readerTts.CanSkipPrevious;
        ReaderTtsNextSegmentButton.IsEnabled = _readerTts.CanSkipNext;
        ReaderTtsStatusText.Text = _readerTtsPreviewInProgress
            ? T("正在试听：{0}", TtsService.GetPreviewText(
                _readerTtsSettings.Voice,
                _readerTtsSettings.VoiceCulture))
            : state switch
            {
                TtsPlaybackState.Generating => T(
                    "正在生成语音… {0}/{1}",
                    _readerTts.SegmentIndex,
                    _readerTts.SegmentCount),
                TtsPlaybackState.Playing => T(
                    "正在朗读… {0}/{1}",
                    _readerTts.SegmentIndex,
                    _readerTts.SegmentCount),
                TtsPlaybackState.Paused => T("听书已暂停。"),
                TtsPlaybackState.AdvancingChapter => T("正在切换章节…"),
                TtsPlaybackState.Error => T(
                    "听书失败：{0}",
                    UiText.Localize(_readerTts.Message ?? T("未知错误"))),
                _ => _readerTts.EnvironmentSetupInProgress
                    ? _readerTts.EnvironmentMessage ?? T("正在自动准备听书环境…")
                    : _readerTtsNotice
                    ?? _readerTts.EnvironmentMessage
                    ?? (_readerTts.Availability is { IsAvailable: false } unavailable
                        ? unavailable.Message
                        : string.Empty)
            };

        UpdateReaderTtsFloatingUi(state);
    }

    private void UpdateReaderTtsButtonVisualState(TtsPlaybackState state)
    {
        if (ReaderTtsButton is null
            || ReaderTtsButtonIcon is null
            || ReaderTtsSpectrum is null)
        {
            return;
        }

        var active = IsReaderTtsActiveState(state);
        var playing = state == TtsPlaybackState.Playing;
        ReaderTtsButton.Classes.Set("ttsActive", active);
        ReaderTtsButtonIcon.IsVisible = !playing;
        // Icon-only header button: active-but-not-yet-speaking states
        // (环境准备、生成中、跳转中、已暂停) dim the speaker so a click
        // visibly registers instead of looking ignored.
        ReaderTtsButtonIcon.Opacity =
            !playing && (active || _readerTts.EnvironmentSetupInProgress) ? 0.35 : 1;
        ReaderTtsSpectrum.IsVisible = playing;
        LogTtsDebug(
            "visualState",
            $"state={state} active={active} playing={playing} dim={ReaderTtsButtonIcon.Opacity < 1} enabled={ReaderTtsButton.IsEnabled} documentAvailable={_readerIsPdf || (_readerDocument is not null && CurrentReaderHost is not null)}");
        if (!playing)
        {
            _readerTtsButtonBreathingTimer?.Stop();
            _readerTtsButtonBreathingPhase = 0;
            ResetReaderTtsSpectrum();
            return;
        }

        _readerTtsButtonBreathingTimer ??= CreateReaderTtsButtonBreathingTimer();
        if (_readerTtsButtonBreathingTimer.IsEnabled) return;

        _readerTtsButtonBreathingPhase = 0;
        UpdateReaderTtsSpectrum();
        _readerTtsButtonBreathingTimer.Start();
    }

    private DispatcherTimer CreateReaderTtsButtonBreathingTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ReaderTtsButtonBreathingIntervalMs)
        };
        timer.Tick += ReaderTtsButtonBreathingTimer_Tick;
        return timer;
    }

    private void ReaderTtsButtonBreathingTimer_Tick(object? sender, EventArgs e)
    {
        var active = _readerTts.State == TtsPlaybackState.Playing;
        if (ReaderTtsButton is null
            || ReaderTtsButtonIcon is null
            || ReaderTtsSpectrum is null
            || !active)
        {
            _readerTtsButtonBreathingTimer?.Stop();
            if (ReaderTtsButton is not null)
                ReaderTtsButton.Classes.Set("ttsActive", false);
            if (ReaderTtsButtonIcon is not null)
            {
                ReaderTtsButtonIcon.IsVisible = true;
                ReaderTtsButtonIcon.Opacity = 1;
            }
            if (ReaderTtsSpectrum is not null)
                ReaderTtsSpectrum.IsVisible = false;
            ResetReaderTtsSpectrum();
            return;
        }

        UpdateReaderTtsSpectrum();
        _readerTtsButtonBreathingPhase +=
            2 * Math.PI * ReaderTtsButtonBreathingIntervalMs
            / ReaderTtsButtonBreathingPeriodMs;
        if (_readerTtsButtonBreathingPhase >= 2 * Math.PI)
            _readerTtsButtonBreathingPhase -= 2 * Math.PI;
    }

    private void UpdateReaderTtsSpectrum()
    {
        var bars = new[]
        {
            ReaderTtsSpectrumBar1,
            ReaderTtsSpectrumBar2,
            ReaderTtsSpectrumBar3,
            ReaderTtsSpectrumBar4
        };
        var offsets = new[] { 0.1, 1.3, 2.6, 3.8 };
        var ranges = new[] { 0.55, 0.82, 1.00, 0.68 };
        for (var index = 0; index < bars.Length; index++)
        {
            var wave = (1 - Math.Cos(
                _readerTtsButtonBreathingPhase + offsets[index])) / 2;
            bars[index].Height = ReaderTtsSpectrumMinimumHeight
                + (wave * ranges[index]
                    * (ReaderTtsSpectrumMaximumHeight
                        - ReaderTtsSpectrumMinimumHeight));
        }
    }

    private void ResetReaderTtsSpectrum()
    {
        if (ReaderTtsSpectrumBar1 is null)
            return;

        ReaderTtsSpectrumBar1.Height = 6;
        ReaderTtsSpectrumBar2.Height = 11;
        ReaderTtsSpectrumBar3.Height = 17;
        ReaderTtsSpectrumBar4.Height = 22;
    }
}
