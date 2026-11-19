using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Result projection and small helpers.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    private async Task<BookListResult> ListBooksAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = await _library.SearchPageAsync(
            query: query,
            sortMode: LibrarySortMode.UpdatedDescending,
            pageIndex: 0,
            pageSize: limit,
            cancellationToken: cancellationToken);

        var books = new List<BookSummary>(page.Books.Count);
        foreach (var book in page.Books)
        {
            cancellationToken.ThrowIfCancellationRequested();
            books.Add(await ToBookSummaryAsync(book, cancellationToken));
        }

        return new BookListResult(page.TotalCount, books);
    }

    private async Task<BookSummary> ToBookSummaryAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var progress = await ReadProgressAsync(book, cancellationToken);
        return new BookSummary(
            book.Id,
            book.Title,
            book.Authors,
            progress?.ProgressPercent,
            progress?.UpdatedAt,
            book.ReadingStatus.ToString(),
            book.Files
                .Select(file => file.Format.Trim().ToUpperInvariant())
                .Where(format => format.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static BookMetadataResult ToBookMetadata(Book book, BookProgressInfo? progress) =>
        new(
            book.Id,
            book.Title,
            book.Authors,
            book.Series,
            book.SeriesIndex,
            book.Description,
            book.Publisher,
            book.PublishDate,
            book.Isbn,
            book.PageCount,
            book.Binding,
            book.OnlineRating,
            book.OnlineRatingCount,
            book.Tags,
            book.Category,
            book.IsFavorite,
            book.ReadingStatus.ToString(),
            book.CoverPath,
            book.CreatedAt,
            book.UpdatedAt,
            progress,
            book.Files
                .Select(file => new BookFileInfo(
                    file.Id,
                    file.Format,
                    file.RelativePath,
                    file.Size,
                    file.Sha256))
                .ToArray());

    private async Task<BookProgressInfo?> ReadProgressAsync(
        Book book,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ProgressCandidate>(book.Files.Count * 2);
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await _readerData.GetProgressAsync(file.Id, cancellationToken);
            if (progress is not null)
            {
                candidates.Add(new ProgressCandidate(
                    file,
                    ClampProgress(progress.ProgressPercent),
                    progress.UpdatedAt,
                    "ReaderProgress"));
            }

            var stats = await _readerData.GetReadingStatsAsync(file.Id, cancellationToken);
            if (stats is not null)
            {
                candidates.Add(new ProgressCandidate(
                    file,
                    ClampProgress(stats.ProgressPercent),
                    stats.UpdatedAt,
                    "ReaderReadingStats"));
            }
        }

        var latest = candidates
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenBy(candidate => candidate.File.Id)
            .FirstOrDefault();
        return latest is null
            ? null
            : new BookProgressInfo(
                latest.ProgressPercent,
                latest.UpdatedAt,
                latest.File.Id,
                latest.File.Format,
                latest.Source);
    }

    private static DeviceInfo ToDeviceInfo(KindleDevice device)
    {
        var totalBytes = Math.Max(0, device.TotalBytes);
        var freeBytes = Math.Clamp(device.FreeBytes, 0, totalBytes);
        double? freePercentage = totalBytes == 0 ? null : freeBytes * 100d / totalBytes;
        return new DeviceInfo(
            device.Identity,
            device.Name,
            device.Profile.Family.ToString(),
            device.Profile.DisplayName,
            device.RootPath,
            device.Transport.ToString(),
            device.IsReady,
            totalBytes,
            freeBytes,
            freePercentage,
            device.CanReadNotes);
    }

    private static bool DeviceMatches(KindleDevice device, string filter) =>
        device.Identity.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || device.RootPath.Equals(filter, StringComparison.OrdinalIgnoreCase)
        || device.Name.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static int ValidateLimit(int value, string parameterName)
    {
        if (value is < 1 or > MaximumResultLimit)
            throw new McpException($"{parameterName} must be between 1 and {MaximumResultLimit}; received {value}.");
        return value;
    }

    private static string ValidateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query must not be empty.");
        var normalized = query.Trim();
        if (normalized.Length > MaximumQueryLength)
            throw new McpException($"query must be at most {MaximumQueryLength} characters.");
        return normalized;
    }

    private static Guid ParseBookId(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId) || !Guid.TryParse(bookId.Trim(), out var id))
            throw new McpException("bookId must be a valid GUID.");
        return id;
    }

    private static string? ValidateOptionalDeviceId(string? deviceId)
    {
        if (deviceId is null) return null;
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new McpException("deviceId must not be blank when provided.");
        var normalized = deviceId.Trim();
        if (normalized.Length > MaximumDeviceIdLength)
            throw new McpException($"deviceId must be at most {MaximumDeviceIdLength} characters.");
        return normalized;
    }

    private static double ClampProgress(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private async Task<(Guid Id, string Name)> RequireCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var collections = await _library.GetCollectionsAsync(cancellationToken);
        var collection = collections.FirstOrDefault(candidate => candidate.Id == collectionId)
            ?? throw new McpException($"Collection '{collectionId}' was not found in the Libzora library.");
        return (collection.Id, collection.Name);
    }

    private static async Task RunLibraryMutationAsync(
        Func<CancellationToken, Task> action,
        string actionName,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException($"{actionName} failed: {exception.Message}");
        }
    }

    private static async Task<T> RunLibraryMutationAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string actionName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new McpException($"{actionName} failed: {exception.Message}");
        }
    }

    private static string ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException("name must not be blank.");
        var normalized = name.Trim();
        if (normalized.Length > 200)
            throw new McpException("name must be at most 200 characters.");
        return normalized;
    }

    private static int ValidateSearchContentLimit(int value)
    {
        if (value is < 1 or > 100)
            throw new McpException($"limit must be between 1 and 100; received {value}.");
        return value;
    }

    private static int ValidateAnnotationLimit(int value)
    {
        if (value is < 1 or > 1000)
            throw new McpException($"limit must be between 1 and 1000; received {value}.");
        return value;
    }
}
