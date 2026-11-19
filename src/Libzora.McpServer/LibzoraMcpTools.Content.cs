using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Content search, annotations, bookmarks, dashboard and devices.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    /// <summary>Searches the indexed text content of one book and returns matching passages.</summary>
    [McpServerTool(
        Name = "search_book_content",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search inside one book's indexed text and return matching passages with chapter context; empty results usually mean the reader index for that book has not been built yet.")]
    public async Task<BookContentSearchResult> SearchBookContentAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Search keyword or phrase, up to 200 characters.")] string query,
        [Description("Maximum number of matching passages to return (1-100). Defaults to 6.")] int limit = 6,
        [Description("When true, only whole-phrase matches are returned.")] bool exactPhraseOnly = false,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var normalizedQuery = ValidateQuery(query);
        var clampedLimit = ValidateSearchContentLimit(limit);
        var chunks = await _readerData.SearchBookAsync(
            id,
            normalizedQuery,
            clampedLimit,
            cancellationToken,
            exactPhraseOnly);
        var formatsByFile = book.Files
            .GroupBy(file => file.Id)
            .ToDictionary(group => group.Key, group => group.First().Format);
        var matches = chunks
            .Select(chunk => new BookContentMatch(
                chunk.BookFileId,
                formatsByFile.TryGetValue(chunk.BookFileId, out var format) ? format : string.Empty,
                chunk.ChapterIndex,
                chunk.ChapterTitle,
                chunk.ChapterPath,
                chunk.StartOffset,
                chunk.EndOffset,
                chunk.Content,
                chunk.Rank))
            .ToArray();
        return new BookContentSearchResult(id, book.Title, normalizedQuery, matches.Length, matches);
    }

    /// <summary>Lists every highlight/annotation (划线/批注) saved for one book.</summary>
    [McpServerTool(
        Name = "list_book_annotations",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all highlights and annotations (划线/批注) for one book across its formats, newest first.")]
    public async Task<BookAnnotationsResult> ListBookAnnotationsAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Maximum annotations per book format to return (1-1000). Defaults to 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var clampedLimit = ValidateAnnotationLimit(limit);
        var annotations = new List<BookAnnotationInfo>();
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _readerData.GetAnnotationsAsync(file.Id, cancellationToken, clampedLimit);
            foreach (var annotation in items)
            {
                annotations.Add(new BookAnnotationInfo(
                    annotation.Id,
                    annotation.BookFileId,
                    file.Format,
                    annotation.ChapterPath,
                    annotation.Fragment,
                    annotation.StartOffset,
                    annotation.EndOffset,
                    annotation.SelectedText,
                    annotation.Prefix,
                    annotation.Suffix,
                    annotation.Color,
                    annotation.UnderlineStyle,
                    annotation.Note,
                    annotation.CreatedAt,
                    annotation.UpdatedAt));
            }
        }
        var ordered = annotations
            .OrderByDescending(annotation => annotation.UpdatedAt)
            .ThenBy(annotation => annotation.Id)
            .ToArray();
        return new BookAnnotationsResult(id, book.Title, ordered.Length, ordered);
    }

    /// <summary>Lists every bookmark (书签) saved for one book.</summary>
    [McpServerTool(
        Name = "list_bookmarks",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List all bookmarks (书签) for one book across its formats.")]
    public async Task<BookBookmarksResult> ListBookmarksAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        [Description("Maximum bookmarks per book format to return (1-1000). Defaults to 200.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        var clampedLimit = ValidateAnnotationLimit(limit);
        var bookmarks = new List<BookBookmarkInfo>();
        foreach (var file in book.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await _readerData.GetBookmarksAsync(file.Id, cancellationToken, clampedLimit);
            foreach (var bookmark in items)
            {
                bookmarks.Add(new BookBookmarkInfo(
                    bookmark.Id,
                    bookmark.BookFileId,
                    file.Format,
                    bookmark.ChapterPath,
                    bookmark.Fragment,
                    bookmark.ChapterIndex,
                    bookmark.ScrollPosition,
                    bookmark.FlowMode,
                    bookmark.Title,
                    bookmark.Quote,
                    bookmark.CreatedAt,
                    bookmark.ContentPosition));
            }
        }
        var ordered = bookmarks
            .OrderBy(bookmark => bookmark.ChapterIndex)
            .ThenBy(bookmark => bookmark.CreatedAt)
            .ToArray();
        return new BookBookmarksResult(id, book.Title, ordered.Length, ordered);
    }

    /// <summary>Returns aggregate reading statistics and recent reading activity.</summary>
    [McpServerTool(
        Name = "get_reading_dashboard",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get reading statistics (books started/finished, total reading time, average progress, bookmark and annotation counts) and recent books plus daily reading time.")]
    public async Task<ReadingDashboardResult> GetReadingDashboardAsync(
        [Description("Maximum number of recent books to include (1-50). Defaults to 12.")] int recentLimit = 12,
        CancellationToken cancellationToken = default)
    {
        var limit = recentLimit is < 1 or > 50
            ? throw new McpException($"recentLimit must be between 1 and 50; received {recentLimit}.")
            : recentLimit;
        var dashboard = await _readerData.GetReadingDashboardAsync(limit, cancellationToken);
        var recent = new List<DashboardRecentBookInfo>(dashboard.RecentBooks.Count);
        foreach (var item in dashboard.RecentBooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? authors = null;
            if (item.IsInLibrary)
            {
                var book = await _library.GetBookAsync(item.BookId, cancellationToken);
                authors = book?.Authors;
            }
            recent.Add(new DashboardRecentBookInfo(
                item.BookId,
                item.BookFileId,
                string.IsNullOrWhiteSpace(item.Title) ? authors ?? string.Empty : item.Title,
                ClampProgress(item.ProgressPercent),
                item.CumulativeSeconds,
                item.UpdatedAt,
                item.IsInLibrary,
                authors));
        }
        return new ReadingDashboardResult(
            dashboard.BooksStarted,
            dashboard.BooksFinished,
            dashboard.TotalSeconds,
            ClampProgress(dashboard.AverageProgress),
            dashboard.BookmarkCount,
            dashboard.AnnotationCount,
            recent,
            dashboard.DailyReading
                .Select(day => new DashboardDayInfo(day.Date, day.ActiveSeconds))
                .ToArray());
    }
    /// <summary>Returns absolute paths for all stored files of one book.</summary>
    [McpServerTool(
        Name = "get_book_file",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Get absolute local file paths and formats for every stored file of one library book; file contents are not returned.")]
    public async Task<BookFilePathResult> GetBookFileAsync(
        [Description("Book GUID returned by list_library or search_books.")] string bookId,
        CancellationToken cancellationToken = default)
    {
        var id = ParseBookId(bookId);
        var book = await GetBookOrThrowAsync(id, cancellationToken);
        return new BookFilePathResult(
            book.Id,
            book.Title,
            book.Files.Select(file => new BookFilePathInfo(
                file.Id,
                file.Format,
                Path.GetFullPath(_library.GetAbsoluteFilePath(file)),
                file.Size,
                File.Exists(_library.GetAbsoluteFilePath(file)))).ToArray());
    }

    /// <summary>
    /// Lists books currently present on one or all detected mounted reader
    /// devices. The scan is read-only but may read device metadata.
    /// </summary>
    [McpServerTool(
        Name = "list_device_library",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List books on connected Kindle or supported mounted reader devices; optionally filter by device ID, root path, or name.")]
    public async Task<DeviceLibraryResult> ListDeviceLibraryAsync(
        [Description("Optional device identity, root path, or exact device name; omit for all devices.")] string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var filter = ValidateOptionalDeviceId(deviceId);
        var devices = await _devices.DetectDevicesAsync(cancellationToken);
        if (filter is not null)
        {
            devices = devices.Where(device => DeviceMatches(device, filter)).ToArray();
            if (devices.Count == 0)
                throw new McpException($"No connected device matched '{filter}'.");
        }

        var result = new List<DeviceLibraryInfo>(devices.Count);
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var books = await _devices.ScanBooksAsync(device, cancellationToken);
            result.Add(new DeviceLibraryInfo(
                device.Identity,
                device.Name,
                device.RootPath,
                books.Select(book => new DeviceBookInfo(
                    book.RelativePath,
                    book.Title,
                    book.Authors,
                    book.Format,
                    book.Size,
                    book.Sha256,
                    book.ModifiedAt,
                    book.IsManagedByLibzora)).ToArray()));
        }

        return new DeviceLibraryResult(result);
    }

    /// <summary>
    /// Safely ejects a connected mounted device. dryRun defaults to true so a
    /// client must explicitly request the physical operation.
    /// </summary>
    [McpServerTool(
        Name = "eject_device",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Safely eject a connected reader device; this has a real device-side effect when dryRun is false, and dryRun defaults to true.")]
    public async Task<EjectDeviceResult> EjectDeviceAsync(
        [Description("Optional device identity, root path, or exact device name; required when more than one device is connected.")] string? deviceId = null,
        [Description("When true, only report the eject action. Defaults to true.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var device = await ResolveSingleDeviceAsync(deviceId, cancellationToken);
        var actions = new[]
        {
            $"Flush and safely eject '{device.Name}' ({device.RootPath})."
        };
        if (dryRun)
            return new EjectDeviceResult(device.Identity, device.Name, dryRun, false, actions);

        try
        {
            await (_ejectDevices ?? _devices).EjectAsync(device, cancellationToken);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new McpException($"The configured device service has no safe-eject implementation: {exception.Message}");
        }

        return new EjectDeviceResult(device.Identity, device.Name, false, true, actions);
    }
}
