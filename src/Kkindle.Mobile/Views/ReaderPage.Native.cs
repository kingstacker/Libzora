using System.Text.Json;
using Kkindle.Core;
using Kkindle.ApplicationServices.Tts;
using Kkindle.Mobile.Views.Sheets;

namespace Kkindle.Mobile.Views;

public sealed record NativeReaderState(
    bool Ready, bool ChromeVisible, bool IsPdf, string Title, string Chapter,
    string Position, double Progress, bool Bookmark, string? Error,
    TtsPlaybackState SpeechState, string SpeechStatus, string? SelectedText,
    bool FootnoteReturnVisible = false)
{
    public NativeReaderSelectionBounds? SelectionBounds { get; init; }
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
    private string? _nativeError;
    private IReadOnlyList<SearchHit> _nativeSearchHits = [];

    public event Action<NativeReaderState>? NativeStateChanged;
    public event Action<NativeReaderDialog>? NativeDialogRequested;
    public event Action<NativeReaderMedia>? NativeReaderMediaRequested;

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
                _shell.ShowToast("图片文件已丢失");
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
                _shell.ShowToast("这个表格暂时无法单独显示");
                return;
            }

            RaiseReaderMedia("table", path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast("无法打开表格");
            Console.WriteLine($"[Kkindle] table preview failed: {exception.Message}");
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
        SelectionBounds = _selection is { X: { } x, Y: { } y } selected
            ? new(x, y, selected.Bottom ?? y, ReaderPaper.Bounds.Width, ReaderPaper.Bounds.Height)
            : null
    };

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
            new NativeReaderTocItem(index, item.Title, item.Level, index == selected)).ToArray();
    }

    public Task NativeJumpToTocAsync(int index)
    {
        ClearFootnoteNavigation();
        return index >= 0 && index < _tocEntries.Count
            ? JumpToAsync(_tocEntries[index]) : Task.CompletedTask;
    }

    public Task NativeSeekAsync(double percent)
    {
        ClearFootnoteNavigation();
        return SeekAsync(Math.Clamp(percent, 0, 100));
    }

    public Task NativeMoveChapterAsync(int direction)
    {
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

    public Task NativeToggleSpeechAsync() => ToggleListeningAsync();
    public async Task NativeSpeakSelectionAsync()
    {
        var text = _selection?.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_shell.Services is not { CreateTtsEngine: { } createEngine, CreateTtsPlayer: { } createPlayer })
            throw new InvalidOperationException("这台设备不支持朗读");
        var document = BuildSpeechDocument()
            ?? throw new InvalidOperationException("当前页面没有可朗读的文字。");
        _tts ??= CreateSpeech(createEngine(), createPlayer());
        var settings = await _modules.Reader.TtsSettings.LoadAsync(_lifetime.Token);
        settings.AudioFormat = "wav";
        settings.Voice = TtsSelectionVoicePolicy.SelectVoice(text, settings.Voice);
        var selection = new ReaderTtsDocument(document.Key, text, 0,
            (_, _) => Task.CompletedTask, () => { },
            BookKey: document.BookKey, ChapterKey: document.ChapterKey);
        HideSelectionBar();
        SetChromeVisible(false);
        _speechExpanded = true;
        await _tts.StartSelectionAsync(selection, settings, _lifetime.Token);
    }

    public void NativePauseSpeech() => _tts?.PauseOrResume();
    public Task NativeStopSpeechAsync() => _tts?.StopAsync() ?? Task.CompletedTask;
    public string NativePageText() => CurrentPageText();
    public void NativeClearSelection() => ClearSelection();

    public async Task<IReadOnlyList<NativeReaderSearchItem>> NativeSearchAsync(string query, CancellationToken token)
    {
        var hits = await SearchBookAsync(query, new Progress<string>(), token);
        token.ThrowIfCancellationRequested();
        _nativeSearchHits = hits;
        return hits.Select((hit, index) => new NativeReaderSearchItem(index, hit.Title, hit.Excerpt)).ToArray();
    }

    public Task NativeJumpToSearchAsync(int index) => index >= 0 && index < _nativeSearchHits.Count
        ? JumpToSearchHitAsync(_nativeSearchHits[index]) : Task.CompletedTask;

    public Task NativeAnnotateAsync(string? note)
    {
        if (_selection is not { } selection || _bookFile is null)
            throw new InvalidOperationException("请先长按并选择正文。");
        var annotation = NewAnnotation(selection);
        if (_annotations.Any(item => item.ChapterPath == annotation.ChapterPath
            && annotation.StartOffset < item.EndOffset && annotation.EndOffset > item.StartOffset))
            throw new InvalidOperationException("所选文字已有批注，请点击已有划线进行编辑。");
        annotation.Note = note ?? string.Empty;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeEditAnnotationAsync(Guid id, string note)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException("批注已不存在。");
        annotation.Note = note;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeDeleteAnnotationAsync(Guid id)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException("批注已不存在。");
        return DeleteAnnotationAsync(annotation);
    }
}
