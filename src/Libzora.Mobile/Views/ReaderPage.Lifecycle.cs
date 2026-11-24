using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Bookmarks, page quotes, teardown and pause/resume.
/// </summary>
public partial class ReaderPage
{
    private const int QuoteLength = 72;

    /// <summary>The opening words of the visible page, used to recognize a bookmark.</summary>
    private string? CurrentPageQuote()
    {
        if (_readerHost is IReaderPositionHost host) return host.GetCurrentPageQuote(QuoteLength);
        if (_readerHost is not NativePdfReaderHost pdf) return null;
        var text = pdf.GetPageContent(pdf.PageNumber)?.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > QuoteLength ? line[..QuoteLength] + "…" : line;
    }

    internal static readonly string[] ReadableFormats = ["epub", "pdf", "azw3", "mobi", "txt"];

    private static BookFile? ChooseReadableFile(Book book, Guid? preferredFileId = null)
    {
        if (preferredFileId is { } id)
        {
            // A bookmark's position belongs to its exact file, not whichever
            // format currently wins the ordinary open preference.
            return book.Files.FirstOrDefault(candidate => candidate.Id == id && candidate.BookId == book.Id
                && ReadableFormats.Contains(
                    candidate.Format.Trim().TrimStart('.'), StringComparer.OrdinalIgnoreCase));
        }
        foreach (var format in ReadableFormats)
        {
            var file = book.Files.FirstOrDefault(candidate =>
                candidate.Format.Trim().TrimStart('.').Equals(format, StringComparison.OrdinalIgnoreCase));
            if (file is not null) return file;
        }
        return book.Files.FirstOrDefault();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (!_disposed && _started)
        {
            _disposed = true;
            _shell.ActualThemeVariantChanged -= OnAppThemeVariantChanged;
            _longPress.Stop();
            CancelInteractiveDragImmediate();
            CancelActiveTransition();
            CancelSpeechPreparation();
            ClearSpeechSleepTimer();
            _tts?.Dispose();
            _shell.Services.SystemBars?.SetEdgeGesturesCaptured(false);
            _shell.ClearPaperSystemBars();
            var now = DateTimeOffset.UtcNow;
            Interlocked.Add(ref _unsavedListeningSeconds, _listeningClock.SetPlaying(false, now));
            _ = SaveAndSyncOnExitAsync();
            StopReadingStatsTimer();
            _bookPageCountCancellation?.Cancel();
            NativeCancelSearch();
            _lifetime.Cancel();
            _ = _epubPreparationSession?.DisposeAsync().AsTask();
            if (_readerHost is not null)
            {
                _readerHost.WebMessageReceived -= OnReaderWebMessage;
                if (_readerHost is IReaderPresentationFrameHost frameHost)
                    frameHost.PresentationFrameRendered -= OnNativePresentationFrameRendered;
                if (_readerHost is NativeReaderHost epub)
                {
                    epub.LayoutCompleted -= OnReaderLayoutCompleted;
                    epub.SetAnnotations([]);
                }
                _readerHost.Dispose();
                _readerHost = null;
            }
            ReaderHostSlot.Content = null;
            _lifetime.Dispose();
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async Task SaveAndSyncOnExitAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        if (await SaveProgressCoreAsync(force: true))
            _shell.RequestAutoSync(MobileShell.AutoSyncGap.AfterReading);
    }

    public Task PauseAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _isReaderForeground = false;
        Interlocked.Add(ref _unsavedSeconds, _activityClock.Pause(now));
        Interlocked.Add(ref _unsavedListeningSeconds, _listeningClock.CountUntil(now));
        return SaveProgressSoonAsync(force: true);
    }

    public void Resume()
    {
        _isReaderForeground = true;
        ResetTouches();
        if (!_listeningClock.IsPlaying)
            _activityClock.Resume(DateTimeOffset.UtcNow);
    }

    private sealed record ReaderSelection(
        string Text,
        int Start,
        int End,
        string Prefix,
        string Suffix,
        double? X,
        double? Y,
        double? Bottom);

    private sealed record TocEntry(string Title, int ChapterIndex, string? Target, int Level)
    {
        public string? UiTextTemplate { get; init; }
        public string DisplayTitle => UiTextTemplate is null
            ? Title : UiText.Get(UiTextTemplate, ChapterIndex + 1);
    }
}
