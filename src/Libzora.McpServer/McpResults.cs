using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>Paginated library output returned by list_library and search_books.</summary>
public sealed record BookListResult(int TotalCount, IReadOnlyList<BookSummary> Books);

/// <summary>Compact book data used in library and search results.</summary>
public sealed record BookSummary(
    Guid Id,
    string Title,
    string Authors,
    double? ProgressPercent,
    DateTimeOffset? ProgressUpdatedAt,
    string ReadingStatus,
    IReadOnlyList<string> Formats);

/// <summary>Full local book metadata and its stored files.</summary>
public sealed record BookMetadataResult(
    Guid Id,
    string Title,
    string Authors,
    string? Series,
    double? SeriesIndex,
    string? Description,
    string? Publisher,
    string? PublishDate,
    string? Isbn,
    string? PageCount,
    string? Binding,
    double? OnlineRating,
    int? OnlineRatingCount,
    string Tags,
    string Category,
    bool IsFavorite,
    string ReadingStatus,
    string? CoverPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    BookProgressInfo? Progress,
    IReadOnlyList<BookFileInfo> Files);

/// <summary>Latest persisted progress snapshot for one book file.</summary>
public sealed record BookProgressInfo(
    double ProgressPercent,
    DateTimeOffset UpdatedAt,
    Guid BookFileId,
    string Format,
    string Source);

/// <summary>Stored local file metadata belonging to a library book.</summary>
public sealed record BookFileInfo(
    Guid Id,
    string Format,
    string RelativePath,
    long Size,
    string Sha256);

/// <summary>Connected reader-device list output.</summary>
public sealed record DeviceListResult(int DeviceCount, IReadOnlyList<DeviceInfo> Devices);

/// <summary>Current device connection and storage status output.</summary>
public sealed record DeviceStatusResult(
    bool IsConnected,
    int DeviceCount,
    string? RequestedDeviceId,
    IReadOnlyList<DeviceInfo> Devices);

/// <summary>Read-only view of a connected reader device.</summary>
public sealed record DeviceInfo(
    string Id,
    string Name,
    string Family,
    string Model,
    string RootPath,
    string Transport,
    bool IsReady,
    long TotalBytes,
    long FreeBytes,
    double? FreePercentage,
    bool CanReadNotes);

/// <summary>Detailed persisted progress for one book and each of its files.</summary>
public sealed record ReadingProgressResult(
    Guid BookId,
    string Title,
    ReadingProgressFile? Latest,
    IReadOnlyList<ReadingProgressFile> Files);

public sealed record ReadingProgressFile(
    Guid BookFileId,
    string Format,
    double? ProgressPercent,
    DateTimeOffset? UpdatedAt,
    string? ChapterPath,
    string? Fragment,
    int? ChapterIndex,
    int? ScrollPosition,
    int? FlowMode,
    ReaderContentPosition? ContentPosition,
    long? CumulativeSeconds,
    int? CompletedChapters,
    int? TotalChapters,
    string? Source);

/// <summary>Recent reading entries and the dashboard statistics used to build them.</summary>
public sealed record RecentBooksResult(
    ReadingDashboardSummary Summary,
    IReadOnlyList<RecentBookInfo> Books);

public sealed record ReadingDashboardSummary(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount);

public sealed record RecentBookInfo(
    Guid BookId,
    Guid BookFileId,
    string Title,
    string? Authors,
    double ProgressPercent,
    long CumulativeSeconds,
    DateTimeOffset UpdatedAt,
    bool IsInLibrary,
    IReadOnlyList<string> Formats);

public sealed record TagListResult(IReadOnlyList<string> Tags);

public sealed record CollectionListResult(IReadOnlyList<CollectionInfo> Collections);

public sealed record CollectionInfo(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt,
    int BookCount);

/// <summary>Absolute local paths for a book's stored files.</summary>
public sealed record BookFilePathResult(
    Guid BookId,
    string Title,
    IReadOnlyList<BookFilePathInfo> Files);

public sealed record BookFilePathInfo(
    Guid Id,
    string Format,
    string AbsolutePath,
    long Size,
    bool Exists);

public sealed record DeviceLibraryResult(IReadOnlyList<DeviceLibraryInfo> Devices);

public sealed record DeviceLibraryInfo(
    string DeviceId,
    string DeviceName,
    string RootPath,
    IReadOnlyList<DeviceBookInfo> Books);

public sealed record DeviceBookInfo(
    string RelativePath,
    string Title,
    string Authors,
    string Format,
    long Size,
    string Sha256,
    DateTimeOffset? ModifiedAt,
    bool IsManagedByLibzora);

public sealed record EjectDeviceResult(
    string DeviceId,
    string DeviceName,
    bool DryRun,
    bool Ejected,
    IReadOnlyList<string> Actions);

public sealed record SendToKindleResult(
    string Channel,
    bool DryRun,
    bool Sent,
    Guid? BookId,
    string Title,
    string SourcePath,
    string Format,
    string? DeviceId,
    string? DeviceName,
    string? Recipient,
    string? Sender,
    IReadOnlyList<string> Actions);

public sealed record ConvertBookResult(
    Guid? BookId,
    string SourcePath,
    string OutputPath,
    string TargetFormat,
    bool AddedToLibrary,
    bool OverwroteExistingFile);

public sealed record ImportBookResult(
    Guid BookId,
    string Title,
    string Format,
    IReadOnlyList<BookFilePathInfo> Files,
    string Message);

public sealed record DeleteBookResult(
    Guid BookId,
    string Title,
    bool DryRun,
    bool Deleted,
    IReadOnlyList<string> Actions);

public sealed record DeleteDeviceBookResult(
    string DeviceId,
    string DeviceName,
    string RelativePath,
    string Title,
    bool DryRun,
    bool Deleted,
    IReadOnlyList<string> Actions);

/// <summary>Outcome of a collection create/rename/delete/clear/merge operation.</summary>
public sealed record CollectionOperationResult(
    Guid CollectionId,
    string CollectionName,
    bool Done,
    IReadOnlyList<string> Actions);

/// <summary>Outcome of adding or removing a book in a collection.</summary>
public sealed record CollectionBookOperationResult(
    Guid BookId,
    Guid CollectionId,
    string CollectionName,
    bool Done,
    IReadOnlyList<string> Actions);

/// <summary>Matching passages found inside one book.</summary>
public sealed record BookContentSearchResult(
    Guid BookId,
    string Title,
    string Query,
    int TotalCount,
    IReadOnlyList<BookContentMatch> Matches);

/// <summary>One matching passage inside a book.</summary>
public sealed record BookContentMatch(
    Guid BookFileId,
    string Format,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content,
    double Rank);

/// <summary>Highlights and annotations belonging to one book.</summary>
public sealed record BookAnnotationsResult(
    Guid BookId,
    string Title,
    int TotalCount,
    IReadOnlyList<BookAnnotationInfo> Annotations);

/// <summary>One highlight/annotation (划线/批注).</summary>
public sealed record BookAnnotationInfo(
    Guid Id,
    Guid BookFileId,
    string Format,
    string ChapterPath,
    string? Fragment,
    int StartOffset,
    int EndOffset,
    string SelectedText,
    string Prefix,
    string Suffix,
    string Color,
    string UnderlineStyle,
    string Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Bookmarks belonging to one book.</summary>
public sealed record BookBookmarksResult(
    Guid BookId,
    string Title,
    int TotalCount,
    IReadOnlyList<BookBookmarkInfo> Bookmarks);

/// <summary>One bookmark (书签).</summary>
public sealed record BookBookmarkInfo(
    Guid Id,
    Guid BookFileId,
    string Format,
    string ChapterPath,
    string? Fragment,
    int ChapterIndex,
    int? ScrollPosition,
    int FlowMode,
    string Title,
    string Quote,
    DateTimeOffset CreatedAt,
    ReaderContentPosition? ContentPosition);

/// <summary>Aggregate reading statistics and recent activity.</summary>
public sealed record ReadingDashboardResult(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount,
    IReadOnlyList<DashboardRecentBookInfo> RecentBooks,
    IReadOnlyList<DashboardDayInfo> DailyReading);

/// <summary>One recently read book entry within the dashboard.</summary>
public sealed record DashboardRecentBookInfo(
    Guid BookId,
    Guid BookFileId,
    string Title,
    double ProgressPercent,
    long CumulativeSeconds,
    DateTimeOffset UpdatedAt,
    bool IsInLibrary,
    string? Authors);

/// <summary>Reading time aggregated for one day.</summary>
public sealed record DashboardDayInfo(DateOnly Date, long ActiveSeconds);
