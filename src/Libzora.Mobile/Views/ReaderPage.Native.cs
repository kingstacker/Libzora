using System.Text.Json;
using Libzora.Core;
using Libzora.ApplicationServices.Tts;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

public sealed record NativeReaderState(
    bool Ready, bool ChromeVisible, bool IsPdf, string Title, string Chapter,
    string Position, double Progress, bool Bookmark, string? Error,
    TtsPlaybackState SpeechState, string SpeechStatus, string? SelectedText,
    bool FootnoteReturnVisible = false)
{
    public NativeReaderSelectionBounds? SelectionBounds { get; init; }
    /// <summary>Changes only for ordinary navigation, never for a search-result jump or reflow.</summary>
    public long NavigationRevision { get; init; }
}

public sealed record NativeReaderSelectionBounds(double X, double Y, double Bottom,
    double ViewportWidth, double ViewportHeight);

/// <summary>One in-page note card. Anchors use the reader surface's coordinates.</summary>
public sealed record NativeReaderFootnote(long Id, bool Visible, string Title, string Href, string? Text,
    bool Loading, bool CanJump, NativeReaderFootnoteAnchor Anchor, string? Error);

public sealed record NativeReaderFootnoteAnchor(double X, double Y, double Width, double Height,
    double ViewportWidth, double ViewportHeight)
{
    public static readonly NativeReaderFootnoteAnchor Empty = new(0, 0, 0, 0, 0, 0);
}

public sealed record NativeReaderTocItem(int Id, string Title, int Level, bool Selected);
public sealed record NativeReaderSearchItem(int Id, string Title, string Excerpt);

/// <summary>
/// One progressive search update. Items are cumulative and keep stable ids so
/// a card can be tapped while later chapters are still being scanned.
/// </summary>
public sealed record NativeReaderSearchBatch(
    int Generation,
    string SearchId,
    string Query,
    NativeReaderSearchItem[] Items,
    int ScannedChapters,
    int TotalChapters,
    bool IsFinal,
    string? Error);

public sealed record NativeReaderDialog(string Kind, string Title, string Text, Guid? AnnotationId = null, string? Note = null);

/// <summary>A figure or table the native frontend should show full screen.</summary>
public sealed record NativeReaderMedia(Guid Id, string Kind, string Title, string Path);

/// <summary>
/// Data/actions for a native frontend around the existing rendering surface.
/// This mode has no Avalonia buttons, popups, sliders, or selection toolbar in
/// the displayed tree. The renderer still owns text hit testing and pagination.
/// </summary>
public partial class ReaderPage
{
    private bool _usesNativeFrontend;
    private bool? _nativeAppAppearanceDark;
    private int _nativeAppAppearanceRevision = -1;
    private bool _nativeChromeVisible = false;
    private long _nativeNavigationRevision;
    private string? _nativeError;
    private Func<string>? _nativeErrorText;
    private readonly object _nativeSearchLock = new();
    private readonly HashSet<string> _nativeSearchSeen = new(StringComparer.Ordinal);
    private readonly List<NativeSearchEntry> _nativeSearchEntries = [];
    private readonly Dictionary<int, NativeSearchEntry> _nativeSearchEntriesById = [];
    private CancellationTokenSource? _nativeSearchCancellation;
    private CancellationTokenSource? _nativeSearchJumpCancellation;
    private int _nativeSearchGeneration;
    private int _nativeSearchNextId;
    private string _nativeSearchQuery = string.Empty;
    private string _nativeSearchId = string.Empty;
    private const int NativeSearchWindowChapters = 10;

    public event Action<NativeReaderState>? NativeStateChanged;
    public event Action<NativeReaderDialog>? NativeDialogRequested;
    public event Action<NativeReaderMedia>? NativeReaderMediaRequested;
    public event Action<NativeReaderSearchBatch>? NativeSearchBatchChanged;

    /// <summary>
    /// Figures open immediately; tables are re-composed on a wide canvas in
    /// the background because the phone page may have flattened them to text.
    /// </summary>
    private void OpenReaderObject(JsonElement root)
    {
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        if (kind == "image")
        {
            var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                RaiseReaderMedia("image", path);
            }
            else
            {
                _shell.ShowToast(UiText.Get("图片文件已丢失"));
            }

            return;
        }

        if (kind == "table"
            && root.TryGetProperty("blockIndex", out var indexElement)
            && indexElement.TryGetInt32(out var blockIndex))
        {
            _ = OpenTablePreviewAsync(blockIndex);
        }
    }

    private void RaiseReaderMedia(string kind, string path) =>
        NativeReaderMediaRequested?.Invoke(
            new NativeReaderMedia(Guid.NewGuid(), kind, _book.Title, path));

    private async Task OpenTablePreviewAsync(int blockIndex)
    {
        if (_readerHost is not NativeReaderHost host) return;
        try
        {
            var width = Math.Max(720, ReaderHostSlot.Bounds.Width * 2.4);
            var path = await host.RenderTablePreviewAsync(blockIndex, width, _lifetime.Token);
            if (string.IsNullOrWhiteSpace(path))
            {
                _shell.ShowToast(UiText.Get("这个表格暂时无法单独显示"));
                return;
            }

            RaiseReaderMedia("table", path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Get("无法打开表格"));
            Console.WriteLine($"[Libzora] table preview failed: {exception.Message}");
        }
    }

    public void UseNativeFrontend()
    {
        _usesNativeFrontend = true;
        // The native shell opens its own viewer for figures and tables; the
        // Avalonia shells keep the existing tap-to-turn behavior.
        if (_readerHost is NativeReaderHost native) native.ReaderObjectsEnabled = true;
        TopChrome.IsVisible = false;
        BottomChrome.IsVisible = false;
        ReaderFooter.IsVisible = false;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        SelectionLayer.IsVisible = false;
        SpeechBar.IsVisible = false;
        SpeechDock.IsVisible = false;
        ProgressSliderGroup.SetHostVisible(false);
        NotifyNativeState();
    }

    public NativeReaderState GetNativeState() => new(
        _ready, _nativeChromeVisible, _isPdf, _book.Title,
        FooterChapter.Text ?? string.Empty, FooterPosition.Text ?? string.Empty,
        _overallProgress * 100, FindCurrentBookmark() is not null, _nativeError,
        _tts?.State ?? TtsPlaybackState.Stopped, SpeechStatusText.Text ?? string.Empty,
        _selection?.Text, _footnoteReturn.Count > 0)
    {
        NavigationRevision = _nativeNavigationRevision,
        SelectionBounds = _selection is { X: { } x, Y: { } y } selected
            ? new(x, y, selected.Bottom ?? y, ReaderPaper.Bounds.Width, ReaderPaper.Bounds.Height)
            : null
    };

    /// <summary>
    /// Refreshes UI labels after a language change without navigating, reflowing
    /// the document, clearing selection, or interrupting speech and AI sessions.
    /// Call on the UI thread.
    /// </summary>
    public void NativeRefreshUiText()
    {
        if (_disposed) return;
        TypographyLabel.Text = _isPdf ? UiText.Get("显示") : UiText.Get("排版");
        if (_ready) RefreshPositionUiText();
        RefreshSpeechUiText(_tts?.State ?? TtsPlaybackState.Stopped,
            _tts?.SegmentIndex ?? 0, _tts?.SegmentCount ?? 0);
        if (_nativeErrorText is { } errorText)
        {
            _nativeError = errorText();
            ErrorText.Text = _nativeError;
        }
        RefreshFootnoteUiText();
        NotifyNativeState();
    }

    private void NotifyNativeState()
    {
        if (_usesNativeFrontend && !_disposed) NativeStateChanged?.Invoke(GetNativeState());
    }

    public IReadOnlyList<NativeReaderTocItem> GetNativeToc()
    {
        // Match the legacy sheet: exactly one entry is current, so the native
        // list can scroll to it even when the page has no entry of its own.
        var selected = CurrentTocIndex();
        return _tocEntries.Select((item, index) =>
            new NativeReaderTocItem(index, item.DisplayTitle, item.Level, index == selected)).ToArray();
    }

    private void CancelNativeSearchJump()
    {
        var pending = Interlocked.Exchange(ref _nativeSearchJumpCancellation, null);
        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void NativeClearSearchHighlight()
    {
        if (_readerHost is NativeReaderHost epub) epub.SetSearchHighlights(null, null);
        else if (_readerHost is NativePdfReaderHost pdf) pdf.ClearSearch();
    }

    private void MarkNativeNavigation()
    {
        if (!_usesNativeFrontend || _disposed) return;
        CancelNativeSearchJump();
        NativeClearSearchHighlight();
        _nativeNavigationRevision++;
        NotifyNativeState();
    }

    public Task NativeJumpToTocAsync(int index)
    {
        if (index < 0 || index >= _tocEntries.Count) return Task.CompletedTask;
        MarkNativeNavigation();
        ClearFootnoteNavigation();
        return JumpToAsync(_tocEntries[index]);
    }

    public Task NativeSeekAsync(double percent)
    {
        MarkNativeNavigation();
        ClearFootnoteNavigation();
        return SeekAsync(Math.Clamp(percent, 0, 100));
    }

    public Task NativeMoveChapterAsync(int direction)
    {
        MarkNativeNavigation();
        ClearFootnoteNavigation();
        return MoveChapterAsync(Math.Sign(direction));
    }
    public Task NativeApplySettingsAsync() => ApplyReaderSettingsAsync();

    public void NativeSetAppAppearance(bool dark, int revision)
    {
        if (revision <= _nativeAppAppearanceRevision) return;
        _nativeAppAppearanceRevision = revision;
        if (_nativeAppAppearanceDark == dark) return;
        _nativeAppAppearanceDark = dark;
        if (_started && !_disposed) RefreshReaderAppearance();
    }

    public Task NativeToggleSpeechAsync(CancellationToken cancellationToken = default) => ToggleListeningAsync(cancellationToken);
    public async Task NativeSpeakSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        cancellationToken.ThrowIfCancellationRequested();
        var text = _selection?.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        var request = BeginSpeechPreparation(cancellationToken);
        var token = request.Token;
        var started = false;
        try
        {
            await EnsureLayoutCompleteAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            if (_shell.Services is not { CreateTtsEngine: { } createEngine, CreateTtsPlayer: { } createPlayer })
                throw new InvalidOperationException(UiText.Get("这台设备不支持朗读"));
            var settings = await _modules.Reader.TtsSettings.LoadAsync(token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var document = BuildSpeechDocument()
                ?? throw new InvalidOperationException(UiText.Get("当前页面没有可朗读的文字。"));
            _tts ??= CreateSpeech(createEngine(), createPlayer());
            settings.AudioFormat = "wav";
            settings.Voice = TtsSelectionVoicePolicy.SelectVoice(text, settings.Voice);
            var selection = new ReaderTtsDocument(document.Key, text, 0,
                (_, _) => Task.CompletedTask, () => { },
                BookKey: document.BookKey, ChapterKey: document.ChapterKey);
            HideSelectionBar();
            SetChromeVisible(false);
            _speechExpanded = true;
            await _tts.StartSelectionAsync(selection, settings, token);
            started = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { CompleteSpeechPreparation(request, started); }
    }

    public void NativePauseSpeech() => _tts?.PauseOrResume();
    public async Task NativeStopSpeechAsync()
    {
        CancelSpeechPreparation();
        if (_tts is { } speech) await speech.StopAsync();
    }
    public string NativePageText() => CurrentPageText();
    public void NativeClearSelection() => ClearSelection();

    /// <summary>
    /// Starts a chapter-prioritised search without holding the reader
    /// navigation gate. The visible page is published synchronously, the
    /// current chapter follows on a worker, and the remaining chapters arrive
    /// in reading order as they are found or indexed.
    /// </summary>
}
