using System.Text.Json;
using System.Text.Json.Serialization;
using Kkindle.Core;
using SkiaSharp;

namespace Kkindle.Infrastructure;

/// <summary>
/// Publishes the small, read-only data set consumed by the iOS and macOS
/// WidgetKit extensions. The extensions run in their own process and can only
/// read the App Group container, so the app writes plain JSON and compact
/// cover thumbnails there whenever the library or reading data changes.
/// </summary>
public static class WidgetSnapshotPublisher
{
    public const string AppGroupIdentifier = "group.com.kingstacker.kkindle";
    public const string FolderName = "KkindleWidgets";
    public const string SnapshotFileName = "snapshot.json";
    public const string CoversFolderName = "covers";

    private const int CoverPixelSize = 420;
    private const int CoverQuality = 84;
    private const int RecentBookLimit = 9;
    private const int WidgetContinueBookLimit = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task PublishAsync(
        AppPaths paths,
        IBookLibraryService library,
        ReaderDataService readerData,
        bool colorfulMode,
        int themeMode,
        string sharedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(readerData);
        if (string.IsNullOrWhiteSpace(sharedRoot)) return;

        await library.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var books = await library.SearchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var dashboard = await readerData.GetReadingDashboardAsync(
            recentLimit: RecentBookLimit,
            cancellationToken: cancellationToken,
            dayCount: 30).ConfigureAwait(false);
        var annotations = await readerData.GetAllAnnotationsAsync(
            cancellationToken, limit: 1).ConfigureAwait(false);

        var byId = books.ToDictionary(book => book.Id);
        var progress = dashboard.Books
            .GroupBy(book => book.BookId)
            .ToDictionary(group => group.Key, group => group.Max(book => book.ProgressPercent));
        var recentBooks = books
            .OrderByDescending(book => book.UpdatedAt)
            .ThenBy(book => book.Id)
            .Take(RecentBookLimit)
            .ToArray();

        var continueBook = dashboard.Books
            .Where(book => book.IsInLibrary && book.ProgressPercent < 99.5 && byId.ContainsKey(book.BookId))
            .OrderByDescending(book => book.UpdatedAt)
            .Select(book => byId[book.BookId])
            .FirstOrDefault()
            ?? recentBooks.FirstOrDefault();
        // The medium continue widget lists several books. Reading statistics
        // order them by the moment they were actually read, which the library
        // list's update time does not track.
        var continueBooks = dashboard.Books
            .Where(book => book.IsInLibrary && book.ProgressPercent < 99.5 && byId.ContainsKey(book.BookId))
            .OrderByDescending(book => book.UpdatedAt)
            .Select(book => byId[book.BookId])
            .DistinctBy(book => book.Id)
            .Take(WidgetContinueBookLimit)
            .ToList();
        if (continueBook is not null && continueBooks.All(book => book.Id != continueBook.Id))
        {
            continueBooks.Insert(0, continueBook);
            if (continueBooks.Count > WidgetContinueBookLimit)
                continueBooks.RemoveAt(continueBooks.Count - 1);
        }

        var coverDirectory = Path.Combine(sharedRoot, CoversFolderName);
        Directory.CreateDirectory(coverDirectory);
        var publishedCovers = new Dictionary<Guid, string?>();
        foreach (var book in recentBooks
                     .Concat(continueBooks)
                     .Concat(continueBook is null ? [] : [continueBook]))
        {
            if (publishedCovers.ContainsKey(book.Id)) continue;
            publishedCovers[book.Id] = await WriteCoverAsync(
                ResolveCoverPath(paths, book), coverDirectory, book.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        RemoveStaleCovers(coverDirectory, publishedCovers.Values.OfType<string>());

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = dashboard.DailyReading
            .OrderBy(day => day.Date)
            .Select(day => new WidgetDay(day.Date.ToString("yyyy-MM-dd"), Math.Max(0, day.ActiveSeconds)))
            .ToArray();
        var todaySeconds = dashboard.DailyReading
            .Where(day => day.Date == today)
            .Sum(day => Math.Max(0, day.ActiveSeconds));
        var weekStart = today.AddDays(-6);
        var weekSeconds = dashboard.DailyReading
            .Where(day => day.Date >= weekStart && day.Date <= today)
            .Sum(day => Math.Max(0, day.ActiveSeconds));

        var quote = annotations.FirstOrDefault(annotation =>
            !string.IsNullOrWhiteSpace(annotation.SelectedText) || !string.IsNullOrWhiteSpace(annotation.Note));
        var quoteBook = quote is not null && byId.TryGetValue(quote.BookId, out var annotationBook)
            ? annotationBook
            : null;

        var snapshot = new WidgetSnapshot(
            Version: 1,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
            ColorfulMode: colorfulMode,
            ThemeMode: themeMode,
            ContinueReading: continueBook is null ? null : ToWidgetBook(
                continueBook, progress.GetValueOrDefault(continueBook.Id), publishedCovers),
            ContinueBooks: continueBooks.Select(book => ToWidgetBook(
                book, progress.GetValueOrDefault(book.Id), publishedCovers)).ToArray(),
            Books: recentBooks.Select(book => ToWidgetBook(
                book, progress.GetValueOrDefault(book.Id), publishedCovers)).ToArray(),
            Reading: new WidgetReading(
                TodaySeconds: todaySeconds,
                StreakDays: ReadingMedalCatalog.ComputeStreakDays(dashboard.DailyReading),
                WeekSeconds: weekSeconds,
                TotalSeconds: Math.Max(0, dashboard.TotalSeconds),
                BooksStarted: dashboard.BooksStarted,
                BooksFinished: dashboard.BooksFinished,
                BookmarkCount: dashboard.BookmarkCount,
                AnnotationCount: dashboard.AnnotationCount,
                Days: days),
            Quote: quote is null || quoteBook is null ? null : new WidgetQuote(
                Text: string.IsNullOrWhiteSpace(quote.SelectedText) ? quote.Note : quote.SelectedText,
                Note: string.IsNullOrWhiteSpace(quote.Note) ? null : quote.Note,
                BookTitle: quoteBook.Title,
                BookId: quoteBook.Id.ToString(),
                Kind: string.IsNullOrWhiteSpace(quote.Note) ? "highlight" : "note",
                UpdatedAt: quote.UpdatedAt.ToString("O")),
            LibraryCount: books.Count,
            FavoriteCount: books.Count(book => book.IsFavorite));

        var snapshotPath = Path.Combine(sharedRoot, SnapshotFileName);
        var temporaryPath = snapshotPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, snapshotPath, overwrite: true);
    }

    private static WidgetBook ToWidgetBook(
        Book book,
        double progress,
        IReadOnlyDictionary<Guid, string?> covers) => new(
        Id: book.Id.ToString(),
        Title: string.IsNullOrWhiteSpace(book.Title) ? "未命名书籍" : book.Title,
        Authors: string.IsNullOrWhiteSpace(book.Authors) ? "未知作者" : book.Authors,
        Progress: Math.Clamp(progress, 0, 100),
        Cover: covers.GetValueOrDefault(book.Id),
        UpdatedAt: book.UpdatedAt.ToString("O"));

    private static string? ResolveCoverPath(AppPaths paths, Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return null;
        var path = Path.IsPathRooted(book.CoverPath)
            ? book.CoverPath
            : Path.Combine(paths.Data, book.CoverPath);
        return File.Exists(path) ? path : null;
    }

    private static async Task<string?> WriteCoverAsync(
        string? sourcePath,
        string coverDirectory,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        if (sourcePath is null) return null;
        var fileName = $"{bookId:N}.jpg";
        var destination = Path.Combine(coverDirectory, fileName);
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var destinationInfo = new FileInfo(destination);
            if (destinationInfo.Exists && destinationInfo.Length > 0
                && destinationInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
                return $"{CoversFolderName}/{fileName}";

            var encoded = await Task.Run(() => EncodeCover(sourcePath), cancellationToken)
                .ConfigureAwait(false);
            if (encoded is null) return null;
            var temporary = destination + ".tmp";
            await File.WriteAllBytesAsync(temporary, encoded, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
            return $"{CoversFolderName}/{fileName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException
                                               or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? EncodeCover(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;

            var scale = Math.Min(1d, CoverPixelSize / (double)Math.Max(codec.Info.Width, codec.Info.Height));
            var dimensions = codec.GetScaledDimensions((float)scale);
            var info = new SKImageInfo(
                Math.Max(1, dimensions.Width),
                Math.Max(1, dimensions.Height),
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using var bitmap = SKBitmap.Decode(codec, info);
            if (bitmap is null) return null;

            using var surface = SKSurface.Create(new SKImageInfo(
                bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.White);
            surface.Canvas.DrawBitmap(bitmap, 0, 0);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, CoverQuality);
            return data?.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException
                                               or InvalidOperationException)
        {
            return null;
        }
    }

    private static void RemoveStaleCovers(string coverDirectory, IEnumerable<string> currentFiles)
    {
        var keep = currentFiles
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in Directory.EnumerateFiles(coverDirectory, "*.jpg"))
                if (!keep.Contains(Path.GetFileName(path)))
                    File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Stale thumbnails are harmless; the next publish retries cleanup.
        }
    }

    private sealed record WidgetSnapshot(
        int Version,
        string GeneratedAt,
        bool ColorfulMode,
        int ThemeMode,
        WidgetBook? ContinueReading,
        IReadOnlyList<WidgetBook> ContinueBooks,
        IReadOnlyList<WidgetBook> Books,
        WidgetReading Reading,
        WidgetQuote? Quote,
        int LibraryCount,
        int FavoriteCount);

    private sealed record WidgetBook(
        string Id,
        string Title,
        string Authors,
        double Progress,
        string? Cover,
        string UpdatedAt);

    private sealed record WidgetReading(
        long TodaySeconds,
        int StreakDays,
        long WeekSeconds,
        long TotalSeconds,
        int BooksStarted,
        int BooksFinished,
        int BookmarkCount,
        int AnnotationCount,
        IReadOnlyList<WidgetDay> Days);

    private sealed record WidgetDay(string Date, long Seconds);

    private sealed record WidgetQuote(
        string Text,
        string? Note,
        string BookTitle,
        string BookId,
        string Kind,
        string UpdatedAt);
}

/// <summary>
/// Coalesces library and reading changes into one snapshot write. Publishing
/// reads several services and decodes covers, so it must never run once per
/// database notification or on the UI thread.
/// </summary>
public sealed class WidgetSnapshotCoordinator : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(700);

    private readonly Func<CancellationToken, Task> _publish;
    private readonly Action? _published;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _scheduled;
    private bool _disposed;

    public WidgetSnapshotCoordinator(
        Func<CancellationToken, Task> publish,
        Action? published = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _published = published;
    }

    public void Schedule(bool immediate = false)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            var cancellation = new CancellationTokenSource();
            _scheduled = cancellation;
            _ = RunAsync(cancellation, immediate);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            _scheduled = null;
        }
        await PublishAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource scheduled, bool immediate)
    {
        try
        {
            if (!immediate)
                await Task.Delay(Debounce, scheduled.Token).ConfigureAwait(false);
            if (scheduled.IsCancellationRequested) return;
            await PublishAsync(scheduled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Widgets are an enhancement. A temporary database or file error
            // must not surface as an app error; the next change retries.
        }
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await _publish(cancellationToken).ConfigureAwait(false);
            _published?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            _scheduled = null;
        }
        // A publish may still be waiting on the gate. Disposing the semaphore
        // would turn its release into ObjectDisposedException; the coordinator
        // is short-lived, so letting GC reclaim it is safer.
    }
}
