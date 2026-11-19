using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Library listing, metadata, progress, recent and tags.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    /// <summary>
    /// Lists books in the local Libzora library, including author, formats,
    /// library reading status, and the latest persisted reading progress.
    /// </summary>
    /// <param name="limit">Maximum number of books to return, from 1 to 200.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>The first page of the library and its total book count.</returns>
    [McpServerTool(
        Name = "list_library",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List books in the local Libzora library with title, author, reading progress, and format.")]
    public Task<BookListResult> ListLibraryAsync(
        [Description("Maximum number of books to return (1-200). Defaults to 100.")] int limit = DefaultLibraryLimit,
        CancellationToken cancellationToken = default) =>
        ListBooksAsync(query: null, ValidateLimit(limit, nameof(limit)), cancellationToken);

    /// <summary>
    /// Searches the local library by the existing library service's title,
    /// author, tag, and series matching rules.
    /// </summary>
    /// <param name="query">Non-empty keyword or phrase, up to 200 characters.</param>
    /// <param name="limit">Maximum number of matching books to return, from 1 to 200.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Matching books and the total number of matches.</returns>
    [McpServerTool(
        Name = "search_books",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search Libzora's local library by book title, author, tag, or series.")]
    public Task<BookListResult> SearchBooksAsync(
        [Description("Search keyword or phrase; must not be empty and must be at most 200 characters.")] string query,
        [Description("Maximum number of matching books to return (1-200). Defaults to 50.")] int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = ValidateQuery(query);
        return ListBooksAsync(
            normalizedQuery,
            ValidateLimit(limit, nameof(limit)),
            cancellationToken);
    }

    /// <summary>
    /// Gets full metadata and all stored file records for one local library
    /// book, together with its latest persisted reading progress.
    /// </summary>
    /// <param name="bookId">The book's GUID from the library.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Detailed metadata for the requested book.</returns>
    [McpServerTool(
        Name = "get_book_metadata",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get detailed metadata, formats, files, and reading progress for one Libzora library book.")]
    public async Task<BookMetadataResult> GetBookMetadataAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await _library.GetBookAsync(id, cancellationToken)
            ?? throw new McpException($"Book '{id}' was not found in the Libzora library.");
        var progress = await ReadProgressAsync(book, cancellationToken);
        return ToBookMetadata(book, progress);
    }

    /// <summary>
    /// Detects currently connected reader devices using Libzora's existing
    /// mounted-storage device service.
    /// </summary>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Connected device summaries and their count.</returns>
    [McpServerTool(
        Name = "list_devices",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List currently connected Kindle or other supported reader devices.")]
    public async Task<DeviceListResult> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        var summaries = devices.Select(ToDeviceInfo).ToArray();
        return new DeviceListResult(summaries.Length, summaries);
    }

    /// <summary>
    /// Returns the current connection and storage status for all detected
    /// devices, or for one device when an identity, root path, or name is
    /// supplied.
    /// </summary>
    /// <param name="deviceId">Optional device identity, root path, or exact name.</param>
    /// <param name="cancellationToken">Cancellation requested by the MCP client.</param>
    /// <returns>Current connected state and device status records.</returns>
    [McpServerTool(
        Name = "device_status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get current connection and storage status for connected reader devices.")]
    public async Task<DeviceStatusResult> DeviceStatusAsync(
        [Description("Optional device identity, root path, or exact device name; omit for all devices.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices
                .Where(device => DeviceMatches(device, filter))
                .ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        var statuses = devices.Select(ToDeviceInfo).ToArray();
        return new DeviceStatusResult(
            statuses.Length > 0,
            statuses.Length,
            filter,
            statuses);
    }

    /// <summary>
    /// Returns the persisted reader position and reading statistics for every
    /// format belonging to one local book.
    /// </summary>
    [McpServerTool(
        Name = "get_reading_progress",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get reading percentage, chapter/scroll position, reading statistics, and update time for a book.")]
    public async Task<ReadingProgressResult> GetReadingProgressAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var files = new List<ReadingProgressFile>(book.Files.Count);

        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = await _readerData.GetProgressAsync(file.Id, cancellationToken);
            var stats = await _readerData.GetReadingStatsAsync(file.Id, cancellationToken);
            var progressIsLatest = progress is not null
                && (stats is null || progress.UpdatedAt >= stats.UpdatedAt);
            var updatedAt = progress is null
                ? stats?.UpdatedAt
                : stats is null
                    ? progress.UpdatedAt
                    : progress.UpdatedAt >= stats.UpdatedAt ? progress.UpdatedAt : stats.UpdatedAt;
            var source = progress is null
                ? stats is null ? null : "ReaderReadingStats"
                : stats is null
                    ? "ReaderProgress"
                    : progressIsLatest ? "ReaderProgress" : "ReaderReadingStats";

            files.Add(new ReadingProgressFile(
                file.Id,
                file.Format,
                progress is null && stats is null
                    ? null
                    : ClampProgress(progressIsLatest ? progress!.ProgressPercent : stats!.ProgressPercent),
                updatedAt,
                progress?.ChapterPath,
                progress?.Fragment,
                progress?.ChapterIndex,
                progress?.ScrollPosition,
                progress?.FlowMode,
                progress?.ContentPosition,
                stats?.CumulativeSeconds,
                stats?.CompletedChapters,
                stats?.TotalChapters,
                source));
        }

        var ordered = files
            .OrderByDescending(file => file.UpdatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(file => file.BookFileId)
            .ToArray();
        return new ReadingProgressResult(
            book.Id,
            book.Title,
            ordered.FirstOrDefault(),
            ordered);
    }

    /// <summary>
    /// Returns the reader service's recent-book dashboard entries, including
    /// cumulative reading time and the latest progress snapshot.
    /// </summary>
    [McpServerTool(
        Name = "list_recent",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List recently read books with progress, cumulative reading time, and update time.")]
    public async Task<RecentBooksResult> ListRecentAsync(
        [Description("Maximum number of recent books to return (1-100). Defaults to 20.")] int limit = DefaultRecentLimit,
        CancellationToken cancellationToken = default)
    {
        var dashboard = await _readerData.GetReadingDashboardAsync(
            ValidateRecentLimit(limit),
            cancellationToken);
        var recent = new List<RecentBookInfo>(dashboard.RecentBooks.Count);
        foreach (var item in dashboard.RecentBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = item.IsInLibrary
                ? await _library.GetBookAsync(item.BookId, cancellationToken)
                : null;
            recent.Add(new RecentBookInfo(
                item.BookId,
                item.BookFileId,
                string.IsNullOrWhiteSpace(item.Title) ? book?.Title ?? string.Empty : item.Title,
                book?.Authors,
                item.ProgressPercent,
                item.CumulativeSeconds,
                item.UpdatedAt,
                item.IsInLibrary,
                book?.Files
                    .Select(file => file.Format.Trim().ToUpperInvariant())
                    .Where(format => format.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? []));
        }

        return new RecentBooksResult(
            new ReadingDashboardSummary(
                dashboard.BooksStarted,
                dashboard.BooksFinished,
                dashboard.TotalSeconds,
                ClampProgress(dashboard.AverageProgress),
                dashboard.BookmarkCount,
                dashboard.AnnotationCount),
            recent);
    }

    /// <summary>Lists the normalized tag values already exposed by the library filters.</summary>
    [McpServerTool(
        Name = "list_tags",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all tags currently used by books in the local Libzora library.")]
    public async Task<TagListResult> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        var filters = await _library.GetFilterOptionsAsync(cancellationToken);
        return new TagListResult(filters.Tags);
    }
}
