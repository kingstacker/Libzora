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
    private EpubReaderDocument? _bookPageCountDocument;
    private bool _bookPageCountsAreExact;
    private CancellationTokenSource? _bookPageCountCancellation;
    private HashSet<string> _auxiliaryChapterPaths = new(StringComparer.Ordinal);

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

        EnsureBookPageCountsScheduled(document);
        if (_bookPageCounts.Length != document.Chapters.Count)
            return $"{chapterPageIndex + 1} / {chapterPages}";

        var position = ReaderBookProgressPolicy.ResolveBookPagePosition(
            _bookPageCounts, _chapterIndex, chapterPageIndex + 1, chapterPages,
            chapterPageCountIsExact: true);
        return $"{position.CurrentPage} / {position.TotalPages}";
    }

    private void EnsureBookPageCountsScheduled(EpubReaderDocument document)
    {
        if (_readerHost is not NativeReaderHost host) return;
        var (width, height) = ResolveBookPageCountViewport(host);
        if (width >= 200 && height >= 200)
            ScheduleBookPageCounts(document, width, height);
    }

    private (double Width, double Height) ResolveBookPageCountViewport(NativeReaderHost host)
    {
        if (host.Bounds is { Width: >= 200, Height: >= 200 } bounds)
            return (bounds.Width, bounds.Height);
        if (_nativeViewportHint is { Width: >= 200, Height: >= 200 } hint)
            return (hint.Width, hint.Height);
        return (0, 0);
    }

    private double CalculateEpubProgressFraction(int chapterPageIndex, int chapterPages)
    {
        if (_epubDocument is not { Chapters.Count: > 0 } document)
            return 0;

        EnsureBookPageCountsScheduled(document);
        if (_bookPageCounts.Length == document.Chapters.Count)
        {
            var position = ReaderBookProgressPolicy.ResolveBookPagePosition(
                _bookPageCounts, _chapterIndex, chapterPageIndex + 1, chapterPages,
                chapterPageCountIsExact: true);
            return ReaderBookProgressPolicy.CalculateFraction(position.CurrentPage, position.TotalPages);
        }

        var chapterRatio = chapterPages > 1 ? (double)chapterPageIndex / (chapterPages - 1) : 0;
        return FallbackProgressFraction(chapterRatio);
    }

    private double CalculateEpubProgressFraction(double chapterRatio)
    {
        if (_epubDocument is not { Chapters.Count: > 0 } document)
            return 0;

        EnsureBookPageCountsScheduled(document);
        if (_bookPageCounts.Length == document.Chapters.Count)
        {
            return ReaderBookProgressPolicy.CalculateFraction(
                _bookPageCounts, _chapterIndex, chapterRatio);
        }

        return FallbackProgressFraction(chapterRatio);
    }

    /// <summary>
    /// Before exact pagination exists, chapter ordinals still give a usable
    /// estimate. Auxiliary note containers are skipped so they never hold the
    /// label below 100% once the reader reaches the last content chapter.
    /// </summary>
    private double FallbackProgressFraction(double chapterRatio)
    {
        if (_epubDocument is not { } document) return 0;
        var clamped = Math.Clamp(chapterRatio, 0, 1);
        var readingChapters = 0;
        var ordinal = 0;
        for (var index = 0; index < document.Chapters.Count; index++)
        {
            if (IsAuxiliaryChapter(index)) continue;
            readingChapters++;
            if (index < _chapterIndex) ordinal++;
        }
        if (readingChapters == 0) return 0;
        return Math.Clamp((ordinal + clamped) / readingChapters, 0, 1);
    }

    private void RefreshAuxiliaryChapters(EpubReaderDocument? document)
    {
        _auxiliaryChapterPaths = document is { AuxiliaryChapters.Count: > 0 }
            ? new HashSet<string>(document.AuxiliaryChapters.Select(Path.GetFullPath), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    private bool IsAuxiliaryChapter(int index) =>
        _epubDocument is { } document
        && index >= 0 && index < document.Chapters.Count
        && _auxiliaryChapterPaths.Contains(Path.GetFullPath(document.Chapters[index]));

    /// <summary>
    /// Walks past footnote containers in the requested direction. A jump that
    /// runs out of reading chapters returns an out-of-range index so the
    /// caller can show its edge notice instead of a blank notes page.
    /// </summary>
    private int ResolveReadingChapter(int chapter, int direction)
    {
        if (_epubDocument is not { } document) return chapter;
        var step = direction < 0 ? -1 : 1;
        var target = chapter;
        while (target >= 0 && target < document.Chapters.Count && IsAuxiliaryChapter(target))
            target += step;
        return target;
    }

    private void ScheduleBookPageCounts(
        EpubReaderDocument document, double width, double height)
    {
        if (_disposed) return;
        if (_bookPageCountLayout == _layout
            && Math.Abs(_bookPageCountWidth - width) < 0.5
            && Math.Abs(_bookPageCountHeight - height) < 0.5
            && ReferenceEquals(_bookPageCountDocument, document)
            && _bookPageCounts.Length == document.Chapters.Count)
            return;

        _bookPageCountLayout = _layout;
        _bookPageCountWidth = width;
        _bookPageCountHeight = height;
        _bookPageCountDocument = document;
        _bookPageCountCancellation?.Cancel();
        _bookPageCountCancellation?.Dispose();
        _bookPageCountCancellation = null;
        var sequence = ++_bookPageCountSequence;

        var host = _readerHost as IReaderPageCountHost;
        var knownChapter = IsAuxiliaryChapter(_chapterIndex)
            ? null
            : host?.GetCurrentChapterPageCountFor(_layout, width, height);
        // Cheap byte-size totals appear immediately, so the footer switches
        // to a whole-book page number on the very first turn. In power-saving
        // mode, keep the instant estimate and skip full-book background layout.
        _bookPageCounts = BuildCheapBookPageCounts(document, knownChapter);
        _bookPageCountsAreExact = false;
        if (IsPowerSavingActive) return;

        // The source is created only once the refresh actually runs; the
        // power-saving exit path schedules again, so nothing is left linked
        // to the page lifetime in the meantime.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _bookPageCountCancellation = cancellation;
        _ = RefreshBookPageCountsAsync(
            document, _layout, width, height, host, knownChapter, sequence, cancellation);
    }

    /// <summary>
    /// Drops the counts that belong to a superseded document. Background
    /// preparation replaces the opening document once the whole book is
    /// indexed; its auxiliary chapter list and page totals can differ.
    /// </summary>
    private void InvalidateBookPageCounts()
    {
        _bookPageCountLayout = null;
        _bookPageCountDocument = null;
    }

    /// <summary>
    /// Restarts exact pagination after power saving ends. The cached estimate
    /// matches the current layout, so the staleness check in
    /// <see cref="ScheduleBookPageCounts"/> would otherwise keep it forever.
    /// </summary>
    private void ResumeBookPageCountsAfterPowerSaving()
    {
        if (_disposed || _bookPageCountsAreExact) return;
        _bookPageCountLayout = null;
        RefreshPositionLabels();
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

            // Footnote containers are not reading content; paginating them
            // would both waste work and leave a phantom page in the total.
            var readingPaths = new List<string>(document.Chapters.Count);
            foreach (var path in document.Chapters)
            {
                if (!IsAuxiliaryPath(document, path)) readingPaths.Add(path);
            }

            var readingCounts = await Task.Run(
                () => host.EstimateChapterPageCounts(
                    readingPaths,
                    layout,
                    width,
                    height,
                    token,
                    knownChapter),
                token);
            token.ThrowIfCancellationRequested();

            var counts = new int[document.Chapters.Count];
            var readingIndex = 0;
            for (var index = 0; index < counts.Length; index++)
            {
                if (IsAuxiliaryPath(document, document.Chapters[index]))
                {
                    counts[index] = 0;
                    continue;
                }
                counts[index] = readingIndex < readingCounts.Length
                    ? Math.Max(1, readingCounts[readingIndex])
                    : 1;
                readingIndex++;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed
                    || sequence != _bookPageCountSequence
                    || !ReferenceEquals(document, _epubDocument)
                    || token.IsCancellationRequested)
                    return;
                _bookPageCounts = counts;
                _bookPageCountsAreExact = true;
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

    private static bool IsAuxiliaryPath(EpubReaderDocument document, string path)
    {
        if (document.AuxiliaryChapters.Count == 0) return false;
        var fullPath = Path.GetFullPath(path);
        foreach (var auxiliary in document.AuxiliaryChapters)
        {
            if (string.Equals(Path.GetFullPath(auxiliary), fullPath, StringComparison.Ordinal))
                return true;
        }
        return false;
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
            if (IsAuxiliaryPath(document, document.Chapters[index]))
            {
                counts[index] = 0;
                continue;
            }
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
