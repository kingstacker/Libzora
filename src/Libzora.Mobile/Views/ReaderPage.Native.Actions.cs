using System.Text.Json;
using Libzora.Core;
using Libzora.ApplicationServices.Tts;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Native annotation actions.
/// </summary>
public partial class ReaderPage
{
    public async Task<bool> NativeJumpToSearchAsync(int id, string? searchId = null, CancellationToken token = default)
    {
        NativeSearchEntry? entry;
        lock (_nativeSearchLock)
        {
            if (searchId is not null && searchId != _nativeSearchId) return false;
            _nativeSearchEntriesById.TryGetValue(id, out entry);
        }
        if (entry is null || _turning) return false;
        using var jump = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _nativeSearchJumpCancellation = jump;
        try
        {
            var jumped = await JumpToSearchHitAsync(entry.Hit, jump.Token);
            if (!jumped || jump.IsCancellationRequested) return false;
            SetChromeVisible(false);
            return true;
        }
        finally
        {
            Interlocked.CompareExchange(ref _nativeSearchJumpCancellation, null, jump);
        }
    }

    public Task NativeAnnotateAsync(string? note)
    {
        if (_selection is not { } selection || _bookFile is null)
            throw new InvalidOperationException(UiText.Get("请先长按并选择正文。"));
        var annotation = NewAnnotation(selection);
        if (_annotations.Any(item => item.ChapterPath == annotation.ChapterPath
            && annotation.StartOffset < item.EndOffset && annotation.EndOffset > item.StartOffset))
            throw new InvalidOperationException(UiText.Get("所选文字已有批注，请点击已有划线进行编辑。"));
        annotation.Note = note ?? string.Empty;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeEditAnnotationAsync(Guid id, string note)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException(UiText.Get("批注已不存在。"));
        annotation.Note = note;
        return SaveAnnotationAsync(annotation);
    }

    public Task NativeDeleteAnnotationAsync(Guid id)
    {
        var annotation = _annotations.Find(item => item.Id == id)
            ?? throw new InvalidOperationException(UiText.Get("批注已不存在。"));
        return DeleteAnnotationAsync(annotation);
    }
}
