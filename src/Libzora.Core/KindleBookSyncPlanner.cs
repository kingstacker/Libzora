namespace Libzora.Core;

public sealed record KindleBookSyncPlan(
    IReadOnlyList<Book> BooksToDevice,
    IReadOnlyList<KindleBook> BooksToComputer,
    int SharedBookCount,
    int SkippedLocalBookCount,
    int SkippedDeviceBookCount)
{
    public bool HasBookChanges => BooksToDevice.Count > 0 || BooksToComputer.Count > 0;
}

/// <summary>
/// Computes a safe union sync plan between the computer library and a connected
/// Kindle device. Books unique to either side are queued for copy; neither side
/// deletes books automatically.
/// </summary>
public static class KindleBookSyncPlanner
{
    private static readonly HashSet<string> ImportableDeviceFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "epub", "pdf", "mobi", "azw3", "kfx"
    };

    private static readonly HashSet<string> TransferableLocalFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "azw3", "mobi", "azw", "prc", "epub", "pdf", "txt"
    };

    public static KindleBookSyncPlan Plan(
        IReadOnlyList<Book> localBooks,
        IReadOnlyList<KindleBook> deviceBooks,
        ReaderDeviceProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(localBooks);
        ArgumentNullException.ThrowIfNull(deviceBooks);

        var comparison = BookLibraryComparer.Compare(localBooks, deviceBooks);

        var booksToDevice = new List<Book>();
        var skippedLocal = 0;
        foreach (var book in localBooks)
        {
            if (comparison.BooksOnKindle.Contains(book.Id))
                continue;
            if (HasTransferableFormat(book, profile))
                booksToDevice.Add(book);
            else
                skippedLocal++;
        }

        var booksToComputer = new List<KindleBook>();
        var skippedDevice = 0;
        foreach (var book in deviceBooks)
        {
            if (comparison.KindleBooksOnComputer.Contains(book.RelativePath))
                continue;
            var format = NormalizeFormat(book.Format);
            if (ImportableDeviceFormats.Contains(format))
                booksToComputer.Add(book);
            else
                skippedDevice++;
        }

        return new KindleBookSyncPlan(
            booksToDevice,
            booksToComputer,
            comparison.KindleBooksOnComputer.Count,
            skippedLocal,
            skippedDevice);
    }

    private static bool HasTransferableFormat(Book book, ReaderDeviceProfile? profile)
    {
        foreach (var file in book.Files)
        {
            var format = NormalizeFormat(file.Format);
            if (string.IsNullOrEmpty(format)) continue;
            if (profile is not null && profile.BookFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                return true;
            if (TransferableLocalFormats.Contains(format))
                return true;
        }
        return false;
    }

    private static string NormalizeFormat(string? format) =>
        (format ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
}
