using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Mobile.Views;

public partial class ReaderPage
{
    private static readonly TimeSpan BookPageCountRefreshDelay = TimeSpan.FromSeconds(2);

    private int[] _bookPageCounts = [];
    private int _bookPageCountSequence;
    private ReaderLayoutSettings? _bookPageCountLayout;
    private double _bookPageCountWidth;
    private double _bookPageCountHeight;
    private CancellationTokenSource? _bookPageCountCancellation;

    /// <summary>
    /// Position within the whole book. The current chapter already knows its
    /// exact page count, so the total is usable after the first page turn;
    /// the other chapters start from byte-size estimates and are replaced by
    /// real pagination once the first page has settled.
    /// </summary>
    private string BookPagePosition(int chapterPageIndex, int chapterPages)
    {
        if (_isPdf || _epubDocument is not { Chapters.Count: > 0 } document)
            return $"{chapterPageIndex + 1} / {chapterPages}";

        if (_shell.Preferences.Current.ReaderProgressDisplay == MobileReaderProgressDisplay.Pages
            && _readerHost is NativeReaderHost host
            && host.Bounds is { Width: >= 200, Height: >= 200 } bounds)
            ScheduleBookPageCounts(document, bounds.Width, bounds.Height);

        if (_bookPageCounts.Length != document.Chapters.Count)
            return $"{chapterPageIndex + 1} / {chapterPages}";

        var chapterIndex = Math.Clamp(_chapterIndex, 0, _bookPageCounts.Length - 1);
        _bookPageCounts[chapterIndex] = Math.Max(
            _bookPageCounts[chapterIndex], chapterPages);
        var totalPages = _bookPageCounts.Sum(count => Math.Max(1, count));
        var prefixPages = _bookPageCounts
            .Take(chapterIndex)
            .Sum(count => Math.Max(1, count));
        var currentPage = Math.Clamp(
            prefixPages + chapterPageIndex + 1, 1, Math.Max(1, totalPages));
        return $"{currentPage} / {Math.Max(1, totalPages)}";
    }

    private void ScheduleBookPageCounts(
        EpubReaderDocument document, double width, double height)
    {
        if (_disposed) return;
        if (_bookPageCountLayout == _layout
            && Math.Abs(_bookPageCountWidth - width) < 0.5
            && Math.Abs(_bookPageCountHeight - height) < 0.5
            && _bookPageCounts.Length == document.Chapters.Count)
            return;

        _bookPageCountLayout = _layout;
        _bookPageCountWidth = width;
        _bookPageCountHeight = height;
        _bookPageCountCancellation?.Cancel();
        _bookPageCountCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _bookPageCountCancellation = cancellation;
        var sequence = ++_bookPageCountSequence;

        var host = _readerHost as IReaderPageCountHost;
        var knownChapter = host?.GetCurrentChapterPageCountFor(_layout, width, height);
        // Cheap byte-size totals appear immediately, so the footer switches
        // to a whole-book page number on the very first turn.
        _bookPageCounts = BuildCheapBookPageCounts(document, knownChapter);
        _ = RefreshBookPageCountsAsync(
            document, _layout, width, height, host, knownChapter, sequence, cancellation);
    }

    private async Task RefreshBookPageCountsAsync(
        EpubReaderDocument document,
        ReaderLayoutSettings layout,
        double width,
        double height,
        IReaderPageCountHost? host,
        (string ChapterPath, int PageCount)? knownChapter,
        int sequence,
        CancellationTokenSource cancellation)
    {
        // CancellationTokenSource members may throw after the source is
        // replaced and disposed; the token itself stays safe to inspect.
        var token = cancellation.Token;
        try
        {
            // Exact pagination waits for the opening turn to finish; measuring
            // every chapter of a large book must not compete with rendering.
            await Task.Delay(BookPageCountRefreshDelay, token);
            token.ThrowIfCancellationRequested();
            if (host is null) return;

            var counts = await Task.Run(
                () => host.EstimateChapterPageCounts(
                    document.Chapters,
                    layout,
                    width,
                    height,
                    token,
                    knownChapter),
                token);
            token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed
                    || sequence != _bookPageCountSequence
                    || !ReferenceEquals(document, _epubDocument)
                    || token.IsCancellationRequested)
                    return;
                _bookPageCounts = counts;
                RefreshPositionLabels();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // Keep the byte-size totals when a chapter cannot be measured.
        }
        finally
        {
            if (ReferenceEquals(_bookPageCountCancellation, cancellation))
            {
                _bookPageCountCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private static int[] BuildCheapBookPageCounts(
        EpubReaderDocument document,
        (string ChapterPath, int PageCount)? knownChapter)
    {
        var counts = new int[document.Chapters.Count];
        var bytesPerPage = 4096d;
        if (knownChapter is { PageCount: > 0 } known)
        {
            try
            {
                var knownLength = new FileInfo(known.ChapterPath).Length;
                if (knownLength > 0)
                    bytesPerPage = Math.Max(512d, knownLength / (double)known.PageCount);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Fall back to the default density.
            }
        }

        for (var index = 0; index < counts.Length; index++)
        {
            try
            {
                var length = new FileInfo(document.Chapters[index]).Length;
                counts[index] = Math.Max(1, (int)Math.Round(length / bytesPerPage));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                counts[index] = 1;
            }
        }
        return counts;
    }
}
