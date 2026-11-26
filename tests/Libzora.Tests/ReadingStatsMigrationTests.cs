using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed class ReadingStatsMigrationTests
{
    [Fact]
    public async Task LegacyCompletionAndListeningMigrateOnceWithoutRevivingResetData()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            var book = Guid.NewGuid(); var file = Guid.NewGuid();
            await using (var connection = new SqliteConnection($"Data Source={paths.Database}"))
            {
                await connection.OpenAsync();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO Books (Id,Title,Authors,ReadingStatus,CreatedAt,UpdatedAt)
                    VALUES ($book,'Legacy fixture','Fixture',2,$now,$now);
                    INSERT INTO BookFiles (Id,BookId,Format,RelativePath,Size,Sha256)
                    VALUES ($file,$book,'epub','legacy.epub',0,$hash);
                    CREATE TABLE ReaderReadingStats (BookFileId TEXT PRIMARY KEY,BookId TEXT NOT NULL,
                        CumulativeSeconds INTEGER NOT NULL DEFAULT 0,ListeningSeconds INTEGER NOT NULL DEFAULT 0,
                        ProgressPercent REAL NOT NULL DEFAULT 0,CompletedChapters INTEGER NOT NULL DEFAULT 0,
                        TotalChapters INTEGER NOT NULL DEFAULT 0,UpdatedAt TEXT NOT NULL);
                    INSERT INTO ReaderReadingStats VALUES ($file,$book,120,120,10,1,10,$now);
                    """;
                seed.Parameters.AddWithValue("$book", book.ToString());
                seed.Parameters.AddWithValue("$file", file.ToString());
                seed.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                seed.Parameters.AddWithValue("$hash", new string('f', 64));
                await seed.ExecuteNonQueryAsync();
            }
            var data = new ReaderDataService(paths);
            await data.InitializeAsync();
            await data.InitializeAsync();
            var stats = (await data.GetReadingStatsAsync(file))!;
            Assert.True(stats.HasFinished);
            Assert.Equal(120, stats.CumulativeSeconds);
            Assert.Equal(120, stats.ListeningSeconds);
            Assert.Equal(10, stats.ProgressPercent);
            await data.ResetReadingDataAsync();
            await data.InitializeAsync();
            var dashboard = await data.GetReadingDashboardAsync();
            Assert.Equal(0, dashboard.BooksFinished);
            Assert.Equal(0, dashboard.TotalSeconds);
            Assert.Equal(0, dashboard.ListeningSeconds);
            Assert.Equal(LibraryReadingStatus.Finished, (await library.GetBookAsync(book))!.ReadingStatus);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }
}
