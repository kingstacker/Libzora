using System.Globalization;
using Libzora.Core;

namespace Libzora.iOSHead;

/// <summary>Library policies shared by the native bridge and portable regression tests.</summary>
internal static class IOSNativeBookLibraryPolicy
{
    internal static double? FiniteSeriesIndex(double? value) =>
        value is { } index && double.IsFinite(index) ? index : null;

    internal static void ApplySeries(Book book, string? series, string? indexText, bool hasIndex)
    {
        var nextSeries = string.IsNullOrWhiteSpace(series) ? null : series.Trim();
        double? index = null;
        if (nextSeries is not null)
        {
            if (hasIndex && !string.IsNullOrWhiteSpace(indexText))
            {
                if (!double.TryParse(indexText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    || !double.IsFinite(parsed))
                    throw new InvalidOperationException(UiText.Get("请输入有效的系列序号，或留空清除序号。"));
                index = parsed;
            }
            else if (!hasIndex && string.Equals(book.Series?.Trim(), nextSeries, StringComparison.OrdinalIgnoreCase))
            {
                // Older forms do not supply an index. Preserve it only while
                // editing the same series, never carry it into a new one.
                index = FiniteSeriesIndex(book.SeriesIndex);
            }
        }
        book.Series = nextSeries;
        book.SeriesIndex = index;
    }

    internal static bool CanAddFormat(ImportBookConflict conflict) =>
        !conflict.ExistingBook.Files.Any(file => string.Equals(
            file.Format.Trim().TrimStart('.'), conflict.Format.Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    internal static bool IsReadable(BookFile file) =>
        file.Format.Trim().TrimStart('.').ToLowerInvariant() is "epub" or "pdf" or "azw3" or "mobi" or "txt";

    internal static BookFile? MostRecentlyReadFile(Book book, IEnumerable<ReaderProgressRow> positions) =>
        positions.Where(position => position.BookId == book.Id)
            .OrderByDescending(position => position.UpdatedAt)
            .Select(position => book.Files.FirstOrDefault(file => file.Id == position.BookFileId
                && file.BookId == book.Id && IsReadable(file))).FirstOrDefault(file => file is not null);

    internal static string ExportFileName(string title, BookFile file) =>
        BookFileNamePolicy.CreateRenamedFileName(Path.GetFileName(file.RelativePath), title);

    internal static bool ImportFailedCompletely(ImportBatchResult result) =>
        result.FailureCount > 0 && result.SuccessCount == 0;

    internal static async Task<IReadOnlyDictionary<Guid, BookSyncStatus>?> OptionalSyncStatusesAsync(
        Func<CancellationToken, Task<IReadOnlyDictionary<Guid, BookSyncStatus>>> load,
        CancellationToken cancellationToken,
        TimeSpan? timeBudget = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeBudget ?? TimeSpan.FromMilliseconds(250));
        try
        {
            return await load(budget.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // Badges are optional. A busy network-sync gate, stale baseline or
            // unavailable state file must never delay or fail the local shelf.
            return null;
        }
    }
}
