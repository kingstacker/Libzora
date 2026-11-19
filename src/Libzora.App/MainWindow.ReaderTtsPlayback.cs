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
/// TTS voices, preview and playback commands.
/// </summary>
public partial class MainWindow
{
    private void ReaderTtsVoiceBox_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (ReaderTtsVoiceBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string voiceId)
        {
            return;
        }
        _readerTtsSettings.Voice = voiceId;
        _readerTtsSettings.VoiceCulture = _readerTtsLastVoices
            .FirstOrDefault(voice => string.Equals(
                voice.Id,
                voiceId,
                StringComparison.OrdinalIgnoreCase))
            ?.Culture ?? string.Empty;
        ReaderTtsPreviewSampleText.Text = T(
            "试听内容：{0}",
            TtsService.GetPreviewText(
                _readerTtsSettings.Voice,
                _readerTtsSettings.VoiceCulture));
    }

    private async void ReaderTtsPreviewButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_readerTtsPreviewInProgress) return;

        try
        {
            CaptureReaderTtsSettingsFromControls();
            if (IsReaderTtsActiveState(_readerTts.State))
            {
                // Auditioning a different voice should always work, even when
                // the reader is paused or speaking. Stop the current session
                // first; the reading position remains available for restart.
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync();
            }
            _readerTtsPreviewInProgress = true;
            _readerTtsNotice = T(
                "正在试听：{0}",
                TtsService.GetPreviewText(
                    _readerTtsSettings.Voice,
                    _readerTtsSettings.VoiceCulture));
            UpdateReaderTtsUi();

            await _readerTts.PreviewAsync(
                _readerTtsSettings,
                ReaderToken);
            _readerTtsNotice = T("试听完成。");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "声音试听失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            _readerTtsPreviewInProgress = false;
            UpdateReaderTtsUi();
        }
    }

    private async Task RefreshReaderTtsVoicesAsync()
    {
        if (_readerTtsVoicesLoading) return;
        _readerTtsVoicesLoading = true;
        var request = ++_readerTtsVoicesRequest;
        _readerTtsNotice = T("正在加载语音…");
        UpdateReaderTtsUi();
        try
        {
            var voices = await _readerTts.GetVoicesAsync(ReaderToken);
            if (request != _readerTtsVoicesRequest) return;
            _readerTtsLastVoices = voices;
            var localEngine = _readerTts.EngineId == LocalSystemTtsEngine.ProviderId;
            var supportedVoices = localEngine
                ? TtsVoicePicker.Select(
                    voices,
                    _readerTtsSettings.ShowAllVoices,
                    _readerTtsSettings.Voice)
                : voices
                    .Where(voice => TtsSelectionVoicePolicy.IsSupportedVoice(voice))
                    .ToArray();
            var items = supportedVoices
                .Select(voice => new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(voice.Culture)
                        ? voice.Name
                        : $"{voice.Name} · {voice.Culture}",
                    Tag = voice.Id
                })
                .ToArray();
            ReaderTtsVoiceBox.ItemsSource = items;
            var selected = items.FirstOrDefault(item =>
                string.Equals(
                    item.Tag as string,
                    _readerTtsSettings.Voice,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                selected = items.FirstOrDefault(item =>
                    string.Equals(
                        item.Tag as string,
                        TtsOptions.DefaultVoice,
                        StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault(item => voices.Any(voice => voice.Id == (item.Tag as string)
                        && voice.Culture == "zh-CN"))
                    ?? items.FirstOrDefault();
                if (selected?.Tag is string fallbackVoice)
                    _readerTtsSettings.Voice = fallbackVoice;
            }
            ReaderTtsVoiceBox.SelectedItem = selected;
            ReaderTtsVoiceBox.Text = _readerTtsSettings.Voice;
            _readerTtsSettings.VoiceCulture = voices
                .FirstOrDefault(voice => string.Equals(
                    voice.Id,
                    _readerTtsSettings.Voice,
                    StringComparison.OrdinalIgnoreCase))
                ?.Culture ?? _readerTtsSettings.VoiceCulture;
            _readerTtsNotice = items.Length == 0
                ? _readerTts.Availability is { IsAvailable: false } unavailable
                    ? unavailable.Message
                    : T("未找到可用语音，请检查系统语音设置。")
                : null;
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "加载语音失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
        finally
        {
            _readerTtsVoicesLoading = false;
        }
    }

    private async void ReaderTtsApplyButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            CaptureReaderTtsSettingsFromControls();
            if (_readerTts.State is not TtsPlaybackState.Stopped
                and not TtsPlaybackState.Error)
            {
                CancelReaderTtsContinuation();
                await _readerTts.StopAsync();
            }

            await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
            _readerTtsNotice = T("设置已保存。");
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "保存听书设置失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private async void ReaderTtsPlayButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_readerTts.State is TtsPlaybackState.Playing
                or TtsPlaybackState.Paused)
            {
                _readerTts.PauseOrResume();
                return;
            }

            CaptureReaderTtsSettingsFromControls();
            await _ttsSettingsStore.SaveAsync(_readerTtsSettings, ReaderToken);
            await StartReaderTtsWithSavedSettingsAsync("启动听书失败：{0}");
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T(
                "启动听书失败：{0}",
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
        }
    }

    private async void ReaderSelectionSpeakButton_Click(object? sender, RoutedEventArgs e)
        => await SpeakReaderSelectionAsync();

    private async Task SpeakReaderSelectionAsync()
    {
        var text = (_readerPendingSelection ?? string.Empty).Trim();
        if (_readerIsPdf)
            text = PdfTextSelectionLayout.NormalizeCopy(text);
        if (string.IsNullOrWhiteSpace(text)) return;
        var document = GetCurrentReaderTtsDocument();
        if (document is null) return;

        CancelReaderTtsContinuation();
        // Keep this alive for playback; the next reader action or reader close
        // cancels and disposes it.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerSelectionTtsCancellation = cancellation;
        HideReaderSelectionPopup();
        try
        {
            // Preserve the live selection. PDF columns can have discontinuous
            // source offsets, so selection sentence offsets cannot be used as
            // full-page highlight offsets.
            var selection = new ReaderTtsDocument(
                document.Key, text, 0,
                (_, _) => Task.CompletedTask,
                () => { },
                BookKey: document.BookKey,
                ChapterKey: document.ChapterKey);
            var settings = _readerTtsSettings.Clone();
            if (_readerTts.EngineId != LocalSystemTtsEngine.ProviderId)
                settings.Voice = TtsSelectionVoicePolicy.SelectVoice(text, settings.Voice);
            _readerTtsNotice = null;
            _readerTtsFloatingRequested = true;
            ShowReaderTtsFloatingPanelTemporarily();
            await _readerTts.StartSelectionAsync(selection, settings, cancellation.Token);
            UpdateReaderTtsUi();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _readerTtsNotice = T("启动听书失败：{0}", UiText.Localize(exception.Message));
            ReaderStatusText.Text = _readerTtsNotice;
            UpdateReaderTtsUi();
        }
    }

    private async void ReaderTtsStopButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        CancelReaderTtsContinuation();
        await _readerTts.StopAsync();
    }

    private void ReaderTtsFloatingToggleButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        if (_readerTts.State is TtsPlaybackState.Playing
            or TtsPlaybackState.Paused)
        {
            _readerTts.PauseOrResume();
        }
        else if (_readerTts.State is TtsPlaybackState.Stopped
            or TtsPlaybackState.Error)
        {
            ReaderTtsPlayButton_Click(sender, e);
        }
    }

    private async void ReaderTtsFloatingStopButton_Click(
        object? sender,
        RoutedEventArgs e)
    {
        CancelReaderTtsContinuation();
        await _readerTts.StopAsync();
    }

    private async Task<bool> StartReaderTtsWithSavedSettingsAsync(
        string failureFormat,
        bool isNavigationResume = false)
    {
        try
        {
            LogTtsDebug("start.begin", $"isNavigationResume={isNavigationResume}");
            if (!isNavigationResume)
                CancelReaderTtsContinuation();
            _readerTtsNotice = null;
            _readerTtsFloatingRequested = true;
            ShowReaderTtsFloatingPanelTemporarily();
            await _readerTts.StartAsync(_readerTtsSettings, ReaderToken);
            LogTtsDebug("start.done", "StartAsync returned");
            UpdateReaderTtsUi();
            return true;
        }
        catch (OperationCanceledException)
            when (ReaderToken.IsCancellationRequested)
        {
            LogTtsDebug("start.cancelled", "reader token cancelled");
            return false;
        }
        catch (Exception exception)
        {
            LogTtsDebug("start.exception", exception.ToString());
            _readerTtsNotice = T(
                failureFormat,
                UiText.Localize(exception.Message));
            UpdateReaderTtsUi();
            return false;
        }
    }

    private async Task ResumeReaderTtsAfterNavigationAsync(bool shouldResume)
    {
        if (!shouldResume
            || !ReaderRoot.IsVisible
            || GetCurrentReaderTtsDocument() is null)
        {
            return;
        }

        await StartReaderTtsWithSavedSettingsAsync(
            "切换章节后启动听书失败：{0}",
            isNavigationResume: true);
    }

    private async void ReaderTtsPreviousSegmentButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await _readerTts.SkipSegmentAsync(-1);

    private async void ReaderTtsNextSegmentButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await _readerTts.SkipSegmentAsync(1);

    private async void ReaderTtsFloatingPreviousButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await MoveReaderTtsChapterAsync(-1, positionAtStart: true);

    private async void ReaderTtsFloatingNextButton_Click(
        object? sender,
        RoutedEventArgs e)
        => await MoveReaderTtsChapterAsync(1);

    private async Task MoveReaderTtsChapterAsync(
        int direction,
        bool positionAtStart = false)
    {
        var state = _readerTts.State;
        var shouldContinue = _readerTtsResumeAfterNavigation
            || _readerTtsFloatingRequested
            || IsReaderTtsActiveState(state);
        var navigationRequestVersion = BeginReaderTtsNavigationRequest(
            shouldContinue);
        var previousChapter = _readerChapterIndex;

        try
        {
            await _readerTts.StopAsync();
            if (!IsCurrentReaderTtsNavigationRequest(navigationRequestVersion))
                return;

            await MoveReaderChapterAsync(
                direction,
                positionAtStart: positionAtStart);

            if (!CanResumeReaderTtsNavigation(
                    navigationRequestVersion,
                    shouldContinue)
                || previousChapter == _readerChapterIndex
                || !ReaderRoot.IsVisible
                || GetCurrentReaderTtsDocument() is null)
            {
                return;
            }

            await ResumeReaderTtsAfterNavigationAsync(true);
        }
        finally
        {
            CompleteReaderTtsNavigationRequest(navigationRequestVersion);
        }
    }
}
