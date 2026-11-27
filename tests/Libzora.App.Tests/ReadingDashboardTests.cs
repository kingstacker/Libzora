using Avalonia;
using Avalonia.Controls;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN", "Archived reading book（已移出书库）")]
    [InlineData("en-US", "Archived reading book (removed from library)")]
    public Task ReadingDashboard_RetainsArchivedTitlesWithoutDuplicateRows(string language, string archivedLabel) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        var archivedBook = Guid.NewGuid();
        var archivedFile = Guid.NewGuid();
        var currentBook = Guid.NewGuid();
        var currentFile = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={scope.Paths.Database}"))
        {
            await connection.OpenAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Books (Id, Title, Authors, CreatedAt, UpdatedAt) VALUES
                    ($archivedBook, 'Archived reading book', 'Author', $time, $time),
                    ($currentBook, 'Current reading book', 'Author', $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256) VALUES
                    ($archivedFile, $archivedBook, 'epub', 'library/archived/book.epub', 1, $archivedHash),
                    ($currentFile, $currentBook, 'pdf', 'library/current/book.pdf', 1, $currentHash);
                """;
            insert.Parameters.AddWithValue("$archivedBook", archivedBook.ToString());
            insert.Parameters.AddWithValue("$archivedFile", archivedFile.ToString());
            insert.Parameters.AddWithValue("$currentBook", currentBook.ToString());
            insert.Parameters.AddWithValue("$currentFile", currentFile.ToString());
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$archivedHash", new string('a', 64));
            insert.Parameters.AddWithValue("$currentHash", new string('b', 64));
            await insert.ExecuteNonQueryAsync();

            var reader = scope.Field<ReaderDataService>("_readerData");
            await reader.AddReadingTimeAsync(archivedBook, archivedFile, 120, 50, 5, 10);
            await reader.AddReadingTimeAsync(currentBook, currentFile, 60, 20, 2, 10);
            using var remove = connection.CreateCommand();
            remove.CommandText = "DELETE FROM BookFiles WHERE Id = $file; DELETE FROM Books WHERE Id = $book;";
            remove.Parameters.AddWithValue("$file", archivedFile.ToString());
            remove.Parameters.AddWithValue("$book", archivedBook.ToString());
            await remove.ExecuteNonQueryAsync();
        }

        await scope.Call<Task>("RefreshReadingDashboardAsync");
        Assert.False(scope.Get<TextBlock>("DashboardStatusText").IsVisible);
        Assert.Equal(2, scope.Window.DashboardRecentItems.Count);
        Assert.Contains(scope.Window.DashboardRecentItems, item => item.Title == archivedLabel && item.Seconds == 120);
        Assert.Contains(scope.Window.DashboardRecentItems, item => item.Title == "Current reading book");
        Assert.Equal(archivedLabel, scope.Window.DashboardBookTimes[0].Label);

        await scope.Call<Task>("RefreshReadingDashboardAsync");
        Assert.Equal(2, scope.Window.DashboardRecentItems.Count);
        Assert.Equal(2, scope.Window.DashboardBookTimes.Count);
    });

    [Theory]
    [InlineData("zh-CN", "听书")]
    [InlineData("en-US", "listening")]
    public Task ReadingDashboard_DisplaysListeningDurationBreakdown(string language, string expectedToken) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={scope.Paths.Database}"))
        {
            await connection.OpenAsync();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Books (Id, Title, Authors, CreatedAt, UpdatedAt)
                VALUES ($bookId, 'Audio Companion Book', 'Author', $time, $time);
                INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256)
                VALUES ($fileId, $bookId, 'epub', 'library/audio/book.epub', 1, $hash);
                """;
            insert.Parameters.AddWithValue("$bookId", bookId.ToString());
            insert.Parameters.AddWithValue("$fileId", fileId.ToString());
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$hash", new string('c', 64));
            await insert.ExecuteNonQueryAsync();
        }

        var reader = scope.Field<ReaderDataService>("_readerData");
        await reader.AddReadingTimeAsync(bookId, fileId, activeSeconds: 900, progressPercent: 30, completedChapters: 2, totalChapters: 10, listeningSeconds: 600);

        await scope.Call<Task>("RefreshReadingDashboardAsync");

        var avgText = scope.Get<TextBlock>("DashboardDailyAverageText").Text ?? "";
        Assert.DoesNotContain(expectedToken, avgText, StringComparison.OrdinalIgnoreCase);
        var listeningText = scope.Get<TextBlock>("DashboardListeningHintText");
        Assert.True(listeningText.IsVisible);
        Assert.Contains(expectedToken, listeningText.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedToken, scope.Window.DashboardBookTimes[0].ValueLabel, StringComparison.OrdinalIgnoreCase);
        Assert.True(scope.Window.DashboardBookTimes[0].HasListening);
        Assert.True(scope.Window.DashboardBookTimes[0].ListeningBarWidth > scope.Window.DashboardBookTimes[0].VisualBarWidth);
        Assert.Equal(600, scope.Window.DashboardRecentItems[0].ListeningSeconds);
        Assert.Contains(expectedToken, scope.Window.DashboardRecentItems[0].DurationLabel, StringComparison.OrdinalIgnoreCase);
        var todayColumn = Assert.Single(scope.Window.DashboardDays, day => !string.IsNullOrEmpty(day.TimeLabel));
        Assert.Contains(expectedToken, todayColumn.DetailLabel, StringComparison.OrdinalIgnoreCase);
    });
}
