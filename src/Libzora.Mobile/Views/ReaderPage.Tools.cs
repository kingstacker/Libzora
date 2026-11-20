using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.ApplicationServices.Tts;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>Search, reading aloud, the AI assistant, translation and pinch zoom.</summary>
public partial class ReaderPage
{
    private const int MaxSearchResults = 200;
    private const int ExcerptPadding = 24;
    private const int ExcerptLength = 90;
    private const double MaxPdfZoom = 4;
    private const double ZoomStep = 0.04;
    private const int MaxContextCharacters = 4000;

    private TtsService? _tts;
    private CancellationTokenSource? _speechCancellation;
    private bool _speechPreparing;
    private bool _speechExpanded;
    private int _speechDockVersion;
    private IReadOnlyList<PdfPageText>? _pdfText;
    private string? _lastSearch;
    private bool _pinching;
    private double _pinchStartZoom = 1;
    private double _pinchRequestedZoom = 1;

    // ---------- search

    private void OnSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReaderSearchRow.IsVisible)
        {
            if (!string.IsNullOrWhiteSpace(ReaderSearchBox.Text)) ShowSearch();
            return;
        }
        HideSelectionBar();
        TitleText.IsVisible = false;
        ReaderSearchBox.Text = _lastSearch;
        ReaderSearchRow.IsVisible = true;
        ReaderSearchBox.Classes.Add("search-expand");
        ReaderSearchBox.Focus();
    }

    private void OnCloseReaderSearchClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ReaderSearchBox.Classes.Remove("search-expand");
        ReaderSearchRow.IsVisible = false;
        TitleText.IsVisible = true;
        SearchButton.Focus();
    }

    private void OnReaderSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(ReaderSearchBox.Text)) return;
        e.Handled = true;
        ShowSearch();
    }

    public void ShowSearch()
    {
        HideSelectionBar();
        var query = ReaderSearchRow.IsVisible ? ReaderSearchBox.Text : _lastSearch;
        var sheet = new SearchSheet(SearchBookAsync, hit => _ = JumpToSearchHitAsync(hit), query,
            searchImmediately: !string.IsNullOrWhiteSpace(query));
        SearchButton.Focus();
        _shell.ShowSheet(UiText.Get("搜索"), sheet, ThemeScope.RequestedThemeVariant, () =>
        {
            _lastSearch = sheet.Query;
            ReaderSearchBox.Text = _lastSearch;
        });
    }

    private async Task<IReadOnlyList<SearchHit>> SearchBookAsync(
        string query, IProgress<string> progress, CancellationToken token)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null || _bookFile is null) return [];
        _lastSearch = query;
        if (_isPdf)
        {
            if (_pdfText is null)
            {
                progress.Report(UiText.Get("正在读取 PDF 文字…"));
                _pdfText = await new PdfTextService().ExtractAsync(
                    _modules.Library.Books.GetAbsoluteFilePath(_bookFile), token);
            }
            return PdfTextService.Search(_pdfText, query, MaxSearchResults, token)
                .Select(result => new SearchHit(UiText.Get("第 {0} 页", result.PageNumber), result.Excerpt, query, result))
                .ToList();
        }

        if (_epubDocument is not { } document) return [];
        progress.Report(UiText.Get("正在搜索全书，第一次需要建立索引…"));
        await _modules.Reader.Content.EnsureIndexedAsync(_book, _bookFile, document, token);
        var chunks = await _modules.Reader.Data.SearchBookAsync(
            _book.Id, query, MaxSearchResults, token, exactPhraseOnly: true, bookFileId: _bookFile.Id);
        return chunks
            .OrderBy(chunk => chunk.ChapterIndex)
            .ThenBy(chunk => chunk.StartOffset)
            .Select(chunk => new SearchHit(
                string.IsNullOrWhiteSpace(chunk.ChapterTitle) ? UiText.Get("第 {0} 章", chunk.ChapterIndex + 1) : chunk.ChapterTitle,
                Excerpt(chunk.Content, query),
                query,
                chunk))
            .ToList();
    }

    private static string Excerpt(string content, string query)
    {
        var index = Math.Max(0, content.IndexOf(query, StringComparison.CurrentCultureIgnoreCase));
        var start = Math.Max(0, index - ExcerptPadding);
        var length = Math.Min(content.Length - start, ExcerptLength);
        return (start > 0 ? "…" : string.Empty) + content.Substring(start, length).ReplaceLineEndings(" ").Trim();
    }

    private async Task<bool> JumpToSearchHitAsync(SearchHit hit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _tts?.NotifyManualNavigation();
        _shell.HideSheet();
        if (!_usesNativeFrontend) SetChromeVisible(false);
        try
        {
            if (hit.Source is PdfSearchResult pdfHit)
            {
                await GoToPdfPageAsync(pdfHit.PageNumber);
                token.ThrowIfCancellationRequested();
                if (_pdfPage != pdfHit.PageNumber || _readerHost is not IPdfSearchHost pdf) return false;
                pdf.Find(hit.Query, pdfHit.MatchIndex);
                return true;
            }

            if (hit.Source is not BookContentChunk chunk
                || _readerHost is not NativeReaderHost host
                || _epubDocument is not { Chapters.Count: > 0 } document)
            {
                return false;
            }

            var chapter = Math.Clamp(chunk.ChapterIndex, 0, document.Chapters.Count - 1);
            if (chapter != _chapterIndex
                && !await NavigateToChapterAsync(host, chapter, new Uri(document.Chapters[chapter]))) return false;
            await EnsureLayoutCompleteAsync(token);
            token.ThrowIfCancellationRequested();

            // Index offsets come from plain text, the host's from its own layout;
            // the nearest rendered match to the indexed one is the hit.
            var matches = host.FindSearchMatches(hit.Query);
            if (matches.Count == 0)
            {
                host.ScrollToOffset(chunk.StartOffset);
            }
            else
            {
                var target = chunk.StartOffset
                    + Math.Max(0, chunk.Content.IndexOf(hit.Query, StringComparison.CurrentCultureIgnoreCase));
                var focus = Enumerable.Range(0, matches.Count)
                    .MinBy(index => Math.Abs(matches[index].Start - target));
                host.SetSearchHighlights(matches.Select(match => (match.Start, match.Length)).ToList(), focus);
                host.ScrollToSearchHit(focus);
            }
            UpdateEpubLabels();
            _ = SaveProgressSoonAsync(force: true);
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
            return false;
        }
    }

    // ---------- reading aloud

    private void OnListenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = ToggleListeningAsync();

    private async Task ToggleListeningAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        HideSelectionBar();
        if (_speechPreparing || _tts is { State: not (TtsPlaybackState.Stopped or TtsPlaybackState.Error) })
        {
            await NativeStopSpeechAsync();
            return;
        }
        if (_shell.Services is not { CreateTtsEngine: { } createEngine, CreateTtsPlayer: { } createPlayer })
        {
            _shell.ShowToast(UiText.Get("这台设备不支持朗读"));
            return;
        }

        var request = BeginSpeechPreparation(cancellationToken);
        var token = request.Token;
        var started = false;
        try
        {
            // The native bridge releases its navigation gate before waiting
            // here. Close/stop cancels this request without waiting for layout.
            await EnsureLayoutCompleteAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var settings = await _modules.Reader.TtsSettings.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            // Engine construction binds platform speech services and can throw.
            _tts ??= CreateSpeech(createEngine(), createPlayer());
            SetChromeVisible(false);
            _speechExpanded = true;
            SpeechBar.IsVisible = !_usesNativeFrontend;
            SpeechDock.IsVisible = false;
            if (!_usesNativeFrontend) ScheduleSpeechDock();
            SpeechStatusText.Text = UiText.Get("正在准备朗读…");
            settings.AudioFormat = "wav";
            await _tts.StartAsync(settings, token);
            started = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            _speechDockVersion++;
            _speechExpanded = false;
            SpeechBar.IsVisible = false;
            SpeechDock.IsVisible = false;
            _shell.ShowToast(UiText.Get("朗读失败：{0}", UiText.Localize(exception.Message)));
        }
        finally { CompleteSpeechPreparation(request, started); }
    }

    private CancellationTokenSource BeginSpeechPreparation(CancellationToken cancellationToken)
    {
        CancelSpeechPreparation();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _speechCancellation = request;
        _speechPreparing = true;
        return request;
    }

    private void CompleteSpeechPreparation(CancellationTokenSource request, bool started)
    {
        if (ReferenceEquals(_speechCancellation, request))
        {
            _speechPreparing = false;
            // Keep the lifetime link alive while playback continues.
            if (started) return;
            _speechCancellation = null;
        }
        request.Dispose();
    }

    private void CancelSpeechPreparation()
    {
        var request = _speechCancellation;
        _speechCancellation = null;
        _speechPreparing = false;
        request?.Cancel();
        request?.Dispose();
    }

    private TtsService CreateSpeech(ITtsEngine engine, ITtsAudioPlayer player)
    {
        var tts = new TtsService(
            engine,
            _modules.Reader.TtsCache,
            player,
            () => Dispatcher.UIThread.CheckAccess()
                ? BuildSpeechDocument()
                : Dispatcher.UIThread.Invoke(BuildSpeechDocument),
            token => Dispatcher.UIThread.InvokeAsync(AdvanceForSpeechAsync).WaitAsync(token));
        tts.StateChanged += (_, e) => Dispatcher.UIThread.Post(() => PresentSpeech(e));
        return tts;
    }

    /// <summary>The text from the current reading position to the end of the chapter or page.</summary>
    private ReaderTtsDocument? BuildSpeechDocument()
    {
        if (_disposed || _bookFile is null) return null;
        var fileKey = _bookFile.Id.ToString("N");
        if (_readerHost is NativePdfReaderHost pdf)
        {
            var page = pdf.PageNumber;
            var text = pdf.GetPageContent(page)?.Text;
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new ReaderTtsDocument(
                $"{fileKey}:pdf:{page}",
                text,
                0,
                (start, length) => Dispatcher.UIThread.InvokeAsync(() => pdf.SetSpeechHighlight(start, length)).GetTask(),
                () => Dispatcher.UIThread.Post(pdf.ClearSpeechHighlight),
                null,
                fileKey,
                $"pdf:{page}");
        }

        if (_readerHost is not IReaderSpeechHost host
            || host.GetSpeechText() is not { } speech
            || string.IsNullOrWhiteSpace(speech.Text))
        {
            return null;
        }
        var chapterIndex = _chapterIndex;
        var chapter = chapterIndex.ToString();
        return new ReaderTtsDocument(
            $"{fileKey}:{chapter}",
            speech.Text,
            speech.StartOffset,
            (start, length) => Dispatcher.UIThread.InvokeAsync(() => host.SetSpeechHighlight(start, length)).GetTask(),
            () => Dispatcher.UIThread.Post(host.ClearSpeechHighlight),
            speech.MapToSource,
            fileKey,
            chapter,
            speech.PageBreakOffsets,
            (offset, token) => Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _disposed || _chapterIndex != chapterIndex
                    || !ReferenceEquals(_readerHost, host)) return;
                host.NavigateToSpeechOffset(offset);
                UpdateEpubLabels();
                _ = SaveProgressSoonAsync(force: true);
            }).GetTask());
    }

    private async Task<bool> AdvanceForSpeechAsync()
    {
        if (_isPdf)
        {
            if (_pdfPage >= _pdfPageCount) return false;
            await GoToPdfPageAsync(_pdfPage + 1);
            return true;
        }
        if (_readerHost is not NativeReaderHost host || _epubDocument is null
            || _chapterIndex + 1 >= _epubDocument.Chapters.Count)
        {
            return false;
        }
        var next = _chapterIndex + 1;
        await GoToChapterAsync(host, next, fromEnd: false, direction: 1);
        return _chapterIndex == next;
    }

    private void PresentSpeech(TtsStateChangedEventArgs e)
    {
        if (_disposed) return;
        var active = e.State is not (TtsPlaybackState.Stopped or TtsPlaybackState.Error);
        if (!active)
        {
            _speechDockVersion++;
            _speechExpanded = false;
        }
        SpeechBar.IsVisible = !_usesNativeFrontend && active && _speechExpanded;
        SpeechDock.IsVisible = !_usesNativeFrontend && active && !_speechExpanded;
        RefreshSpeechUiText(e.State, e.SegmentIndex, e.SegmentCount);
        if (this.TryFindResource(e.State == TtsPlaybackState.Paused ? "IconPlay" : "IconPause", out var icon)
            && icon is Avalonia.Media.Geometry geometry)
        {
            SpeechPauseIcon.Data = geometry;
        }
        if (e.State == TtsPlaybackState.Error && !string.IsNullOrWhiteSpace(e.Message))
            _shell.ShowToast(UiText.Get("朗读失败：{0}", UiText.Localize(e.Message)));
        NotifyNativeState();
    }

    private void RefreshSpeechUiText(TtsPlaybackState state, int segmentIndex, int segmentCount)
    {
        var active = state is not (TtsPlaybackState.Stopped or TtsPlaybackState.Error);
        ListenLabel.Text = active ? UiText.Get("停止") : UiText.Get("听书");
        SpeechStatusText.Text = state switch
        {
            TtsPlaybackState.Generating or TtsPlaybackState.Playing => segmentCount > 0
                ? UiText.Get("朗读中 {0}/{1}", segmentIndex, segmentCount)
                : UiText.Get("正在准备朗读…"),
            TtsPlaybackState.Paused => UiText.Get("已暂停"),
            TtsPlaybackState.AdvancingChapter => _isPdf ? UiText.Get("翻到下一页…") : UiText.Get("翻到下一章…"),
            _ => string.Empty
        };
    }

    private void OnSpeechDockClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _speechExpanded = true;
        SpeechDock.IsVisible = false;
        SpeechBar.IsVisible = true;
        ScheduleSpeechDock();
    }

    private async void ScheduleSpeechDock()
    {
        var version = ++_speechDockVersion;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _lifetime.Token);
            if (_disposed || version != _speechDockVersion || !_speechExpanded) return;
            _speechExpanded = false;
            SpeechBar.IsVisible = false;
            SpeechDock.IsVisible = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async void OnSpeechSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScheduleSpeechDock();
        if (_shell.Services.CreateTtsEngine is not { } createEngine) return;
        try
        {
            var settings = await _modules.Reader.TtsSettings.LoadAsync(_lifetime.Token);
            using var engine = createEngine();
            var voices = await engine.GetVoicesAsync(_lifetime.Token);
            if (_disposed) return;
            _shell.ShowSheet(UiText.Get("朗读设置"), new SpeechSettingsSheet(
                _modules.Reader.TtsSettings, settings, voices,
                () => { _shell.HideSheet(); _shell.ShowToast(UiText.Get("朗读设置已保存")); },
                message => _shell.ShowToast(UiText.Localize(message))), ThemeScope.RequestedThemeVariant);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Get("无法获取系统语音：{0}", UiText.Localize(exception.Message)));
        }
    }

    private void OnSpeechPauseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _tts?.PauseOrResume();
        ScheduleSpeechDock();
    }

    private void OnSpeechStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = NativeStopSpeechAsync();

    // ---------- AI and translation

    private void OnAiClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowAssistant(null);

    private void ShowAssistant(string? selection)
    {
        HideSelectionBar();
        _shell.ShowSheet(UiText.Get("AI 助手"), new AiSheet(_modules, _shell, _book, CurrentPageText, selection),
            ThemeScope.RequestedThemeVariant);
    }

    private string CurrentPageText()
    {
        string? text = _readerHost switch
        {
            NativePdfReaderHost pdf => pdf.GetPageContent(pdf.PageNumber)?.Text,
            IReaderSpeechHost host when host.GetSpeechText() is { } speech => speech.Text[speech.StartOffset..],
            _ => null
        };
        text ??= string.Empty;
        return text.Length > MaxContextCharacters ? text[..MaxContextCharacters] : text;
    }

    /// <summary>Uses only the configured model service; never falls back to a web translator.</summary>
    private async Task TranslateAsync(string text)
    {
        HideSelectionBar();
        var sheet = new InfoSheet(UiText.Get("正在翻译…"), text);
        _shell.ShowSheet(UiText.Get("翻译"), sheet, ThemeScope.RequestedThemeVariant);
        try
        {
            var chinese = text.Count(character => character is >= '一' and <= '鿿');
            var target = chinese * 3 > text.Length ? "en" : "zh-CN";
            var ai = await _modules.Ai.Settings.LoadAsync(_lifetime.Token);
            if (!ai.IsConfigured) throw new InvalidOperationException(UiText.Get("请先配置模型服务地址和模型；远程服务还需要 API Key。"));
            if (!ai.IsLocalEndpoint && !await _shell.ConfirmAsync(UiText.Get("发送文本进行翻译"),
                UiText.Get("所选正文将发送至 {0} 进行翻译，可能产生服务费用。请勿发送无权提供的个人信息或保密内容。", ai.Destination), UiText.Get("同意并翻译"))) return;
            _lifetime.Token.ThrowIfCancellationRequested();
            _shell.ShowSheet(UiText.Get("翻译"), sheet, ThemeScope.RequestedThemeVariant);
            var provider = ReaderTranslationProvider.Ai;
            var translated = await _modules.Ai.Translation.TranslateAsync(text, provider, target, ai, _lifetime.Token);
            sheet.Present(translated, text);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            sheet.Present(UiText.Get("翻译失败：{0}", UiText.Localize(exception.Message)), text);
        }
    }

    // ---------- pinch zoom

    // Pinches are recognized here, ahead of the PDF surface, which otherwise
    // takes each finger as a pan or selection.
    private readonly Dictionary<int, Point> _touches = [];
    private double _pinchStartDistance;

    private void TrackTouchPressed(PointerPressedEventArgs e)
    {
        // The first finger down starts a fresh gesture; a release the system
        // swallowed (edge swipe, app switch) must not leave a phantom finger.
        if (e.Pointer.IsPrimary) ResetTouches();
        _touches[e.Pointer.Id] = e.GetPosition(ReaderHostSlot);
        if (_touches.Count == 2)
        {
            if (_readerHost is NativePdfReaderHost pdf)
            {
                _pinching = true;
                _pinchStartDistance = Math.Max(1, TouchDistance());
                _pinchStartZoom = _pinchRequestedZoom = pdf.Zoom;
            }
            else if (IsMangaMode && _readerHost is NativeReaderHost manga)
            {
                _pinching = true;
                _pinchStartDistance = Math.Max(1, TouchDistance());
                _pinchStartZoom = _pinchRequestedZoom = manga.MangaZoom;
            }
        }
    }

    /// <returns>True while a pinch owns the gesture.</returns>
    private bool TrackTouchMoved(PointerEventArgs e)
    {
        if (!_touches.ContainsKey(e.Pointer.Id)) return false;
        _touches[e.Pointer.Id] = e.GetPosition(ReaderHostSlot);
        if (!_pinching || _touches.Count < 2) return _pinching;

        var zoom = Math.Clamp(_pinchStartZoom * TouchDistance() / _pinchStartDistance, 1, MaxPdfZoom);
        if (_readerHost is NativePdfReaderHost pdf)
        {
            if (Math.Abs(zoom - _pinchRequestedZoom) >= ZoomStep)
            {
                _pinchRequestedZoom = zoom;
                _ = pdf.SetZoomAsync(zoom, TouchCenter());
            }
            return true;
        }

        if (IsMangaMode && _readerHost is NativeReaderHost manga)
        {
            _pinchRequestedZoom = zoom;
            manga.SetMangaZoom(zoom, TouchCenter());
            return true;
        }
        return _pinching;
    }

    /// <returns>True when this release ended a pinch.</returns>
    private bool TrackTouchReleased(PointerReleasedEventArgs e)
    {
        _touches.Remove(e.Pointer.Id);
        if (!_pinching) return false;
        if (_touches.Count > 0) return true;
        _pinching = false;
        // A pinch that ends near 1 snaps back to fit; otherwise preserve the
        // anchored pan instead of re-zooming from the viewport center.
        var zoom = _pinchRequestedZoom < 1.1 ? 1 : Math.Round(_pinchRequestedZoom, 2);
        if (_readerHost is NativePdfReaderHost pdf)
        {
            _shell.Preferences.Update(value => value.WithPdfZoom(_bookFile?.Id, zoom));
            _ = ApplyPdfZoomAsync(pdf, preserveZoomAnchor: zoom > 1);
        }
        else if (IsMangaMode && _readerHost is NativeReaderHost manga)
        {
            manga.SetMangaZoom(zoom);
        }
        return true;
    }

    private void OnSurfaceCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _touches.Remove(e.Pointer.Id);
        if (_touches.Count == 0) _pinching = false;
    }

    private void ResetTouches()
    {
        _touches.Clear();
        _pinching = false;
    }

    private double TouchDistance()
    {
        var points = _touches.Values.Take(2).ToArray();
        var delta = points[0] - points[1];
        return Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
    }

    private Point TouchCenter()
    {
        var points = _touches.Values.Take(2).ToArray();
        if (points.Length < 2) return points.Length == 1 ? points[0] : default;
        return new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
    }
}
