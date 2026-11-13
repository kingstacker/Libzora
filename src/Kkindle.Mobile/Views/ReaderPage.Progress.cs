using Kkindle.ApplicationServices;
using Kkindle.ApplicationServices.Tts;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views;

public partial class ReaderPage
{
    private bool _readingTimesRequested;
    private bool _readingTimesLoading;
    private double?[]? _readingSectionSeconds;
    private ReaderTtsTextSnapshot? _readingTimeSnapshot;
    private double _currentTextRemainingSeconds;
    private bool _hasPdfOutline;

    private string ProgressDetail(string pages)
    {
        switch (_shell.Preferences.Current.ReaderProgressDisplay)
        {
            case MobileReaderProgressDisplay.Chapters:
                if (_isPdf)
                {
                    var selected = CurrentTocIndex();
                    return _hasPdfOutline && selected >= 0
                        ? $"第 {selected + 1} / {_tocEntries.Count} 章" : pages;
                }
                return _epubDocument is { } document
                    ? $"第 {_chapterIndex + 1} / {document.Chapters.Count} 章" : pages;
            case MobileReaderProgressDisplay.ChapterTimeRemaining:
                return RemainingTimeLabel(wholeBook: false);
            case MobileReaderProgressDisplay.BookTimeRemaining:
                return RemainingTimeLabel(wholeBook: true);
            default:
                return pages;
        }
    }

    private string RemainingTimeLabel(bool wholeBook)
    {
        var scope = wholeBook ? "全书" : _isPdf && !_hasPdfOutline ? "本页" : "本章";
        var seconds = TextRemainingSeconds();
        if (_isPdf || wholeBook)
        {
            EnsureReadingTimesLoaded();
            if (_readingSectionSeconds is null)
                return _readingTimesLoading ? $"{scope}正在估算…" : $"{scope}剩余 —";

            var start = _isPdf ? Math.Max(0, _pdfPage - 1) : _chapterIndex + 1;
            var end = _readingSectionSeconds.Length;
            if (_isPdf && !wholeBook)
            {
                end = Math.Min(start + 1, end);
                if (_hasPdfOutline && CurrentTocIndex() is var selected && selected >= 0)
                {
                    end = _readingSectionSeconds.Length;
                    var level = _tocEntries[selected].Level;
                    for (var index = selected + 1; index < _tocEntries.Count; index++)
                    {
                        var entry = _tocEntries[index];
                        if (entry.Level <= level && entry.ChapterIndex > start)
                        {
                            end = Math.Min(entry.ChapterIndex, end);
                            break;
                        }
                    }
                }
            }
            var remaining = SectionReadingSeconds(start, end);
            var current = seconds ?? _readingSectionSeconds.ElementAtOrDefault(_chapterIndex);
            seconds = _isPdf ? remaining : current is { } text && remaining is { } rest ? text + rest : null;
            if (seconds == 0) seconds = null;
        }
        return seconds is { } estimate
            ? $"{scope}剩余约 {ReaderReadingTimeEstimate.FormatDuration(estimate)}"
            : $"{scope}剩余 —";
    }

    private double? TextRemainingSeconds()
    {
        if (_readerHost is not IReaderSpeechHost speechHost || speechHost.GetSpeechText() is not { } snapshot)
            return null;
        if (!ReferenceEquals(snapshot, _readingTimeSnapshot))
        {
            _readingTimeSnapshot = snapshot;
            _currentTextRemainingSeconds = ReaderReadingTimeEstimate.EstimateSeconds(snapshot.Text.AsSpan(snapshot.StartOffset));
        }
        return _currentTextRemainingSeconds;
    }

    private double? SectionReadingSeconds(int start, int end)
    {
        if (_readingSectionSeconds is not { } sections || start < 0 || start > sections.Length) return null;
        var seconds = 0.0;
        for (var index = start; index < Math.Min(end, sections.Length); index++)
        {
            if (sections[index] is not { } section) return null;
            seconds += section;
        }
        return seconds;
    }

    private void EnsureReadingTimesLoaded()
    {
        if (_readingTimesRequested || _disposed) return;
        _readingTimesRequested = true;
        _readingTimesLoading = true;
        _ = LoadReadingTimesAsync();
    }

    private async Task LoadReadingTimesAsync()
    {
        // Only the time display needs this scan. It reads the prepared local
        // text off the UI thread and retains small per-section totals.
        var token = _lifetime.Token;
        try
        {
            using var operation = MobileDataCoordinator.TryBeginOperation();
            if (operation is null) return;
            if (_isPdf && _bookFile is { } file)
            {
                var pages = _pdfText ?? await new PdfTextService().ExtractAsync(
                    _modules.Library.Books.GetAbsoluteFilePath(file), token);
                token.ThrowIfCancellationRequested();
                var sections = await Task.Run(() => pages.Select(page =>
                    (double?)ReaderReadingTimeEstimate.EstimateSeconds(page.Text.AsSpan())).ToArray(), token);
                if (_disposed) return;
                _pdfText ??= pages;
                _readingSectionSeconds = sections;
            }
            else if (_epubDocument is { } document)
            {
                var sections = await Task.Run(() =>
                {
                    var result = new double?[document.Chapters.Count];
                    for (var index = 0; index < result.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var text = ReaderSpeechTextBuilder.Load(document.Chapters[index], paragraphIndent: false, token)?.Text;
                            result[index] = ReaderReadingTimeEstimate.EstimateSeconds(text.AsSpan());
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            // A missing chapter must not invalidate the reader
                            // or turn an incomplete total into a precise time.
                        }
                    }
                    return result;
                }, token);
                if (_disposed) return;
                _readingSectionSeconds = sections;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally
        {
            _readingTimesLoading = false;
            if (!_disposed && !token.IsCancellationRequested) RefreshPositionLabels();
        }
    }

    private void RefreshPositionLabels()
    {
        if (_isPdf) UpdatePdfLabels();
        else UpdateEpubLabels();
    }
}
