using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed partial class KindleBookmarkAndBookSyncTests
{
    [Fact]
    public async Task BookmarkPullPushAndActualDatabaseReadbackKeepExactPositionAndConverge()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var azw3 = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
            var epub = Path.Combine(root, "native.epub");
            await Azw3ReaderService.PrepareEpubAsync(azw3, epub);
            var library = new SqliteBookLibraryService(paths, new BookMetadataService());
            await library.InitializeAsync();
            await library.ImportAsync([epub]);
            var book = Assert.Single(await library.SearchAsync());
            var file = Assert.Single(book.Files);
            var reader = new ReaderDataService(paths);
            await reader.InitializeAsync();
            var raw = await KindleBookTextService.ReadAsync(azw3);
            Assert.True(raw.TryFindRange("你好，世界。😀", null, null, out var expectedPosition, out _));
            var db = Path.Combine(root, "device.db");
            var candidate = Path.Combine(root, "candidate.db");
            var backup = Path.Combine(root, "backup.db");
            await CreateBookmarkDatabaseAsync(db);
            await ClearBookmarks(db);
            var time = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            var payload = JsonSerializer.Serialize(new
            {
                type = "BOOKMARK", position_type = 0, created_time = time, last_modified = time,
                json_metadata = "{\"mchl_color\":\"dark_blue\"}",
                book_data = new { asin = raw.Asin, guid = raw.CreateGuid(), contentType = "EBOK", isOwnedByCustomer = 0, isSample = 0 },
                start_position = new { longPosition = expectedPosition.ToString(), shortPosition = expectedPosition },
                end_position = new { longPosition = expectedPosition.ToString(), shortPosition = expectedPosition }
            });
            await KindleAnnotationDatabaseWriter.BuildCandidateAsync(db, candidate,
                [new("native-bookmark", raw.CreateBookId()!, 2, expectedPosition, expectedPosition, time, time, "", payload)]);
            File.Copy(candidate, db, overwrite: true);

            async Task<KindleAnnotationSnapshot?> ReadSnapshot(CancellationToken token)
            {
                var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(db, cancellationToken: token, requireCompleteSnapshot: true);
                return new("system/ksdk/.annotations/fixture/ksdk_annotation_v1.db", await Hashing.Sha256Async(db, token),
                    records.Select(record => new KindleAnnotationRecord(record.Id,
                        new(record.Book.BookId, record.Book.Asin, record.Book.Guid), record.Dataset, record.Type,
                        record.Start, record.End, record.CreatedAt?.ToUnixTimeMilliseconds() ?? 0,
                        record.UpdatedAt?.ToUnixTimeMilliseconds() ?? 0, record.Note, record.SerializedPayload)).ToArray());
            }
            var writes = 0;
            var fake = new BookmarkFakeKindleService((await ReadSnapshot(CancellationToken.None))!, azw3)
            {
                SnapshotReader = ReadSnapshot,
                Writer = async (snapshot, changes, token) =>
                {
                    Assert.Equal(snapshot.Sha256, await Hashing.Sha256Async(db, token));
                    File.Copy(db, backup, overwrite: true);
                    var writeCandidate = Path.Combine(root, "candidate-" + Guid.NewGuid().ToString("N") + ".db");
                    var hash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(db, writeCandidate, changes, token);
                    File.Copy(writeCandidate, db, overwrite: true);
                    Assert.Equal(hash, await Hashing.Sha256Async(db, token));
                    writes++;
                    return new(changes.Count, snapshot.Sha256, hash, backup, null);
                }
            };
            var sync = new KindleAnnotationSyncService(paths, library, reader, new EpubBookContentService(reader));
            var device = new KindleDevice { RootPath = "fixture", VolumeSerial = "ROUNDTRIP", Transport = KindleTransport.Mtp };
            var deviceBook = new KindleBook { RelativePath = "documents/native.azw3", Title = book.Title, Authors = book.Authors, Format = "azw3" };
            var pull = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(1, pull.BookmarksToComputer);
            var imported = Assert.Single(await reader.GetBookmarksAsync(file.Id));
            Assert.False(string.IsNullOrWhiteSpace(imported.ContentPosition!.AnchorQuote));
            Assert.False((await sync.AnalyzeAsync(device, [deviceBook], fake)).HasChanges);

            await ClearBookmarks(db); // simulate a second endpoint lacking this bookmark
            var hashBeforePush = await Hashing.Sha256Async(db);
            var push = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(1, push.BookmarksToDevice);
            Assert.Equal(hashBeforePush, await Hashing.Sha256Async(backup));
            var readback = Assert.Single(await KindleAnnotationDatabaseReader.ReadRecordsAsync(db, requireCompleteSnapshot: true));
            Assert.Equal(expectedPosition, readback.Start);
            Assert.Equal(expectedPosition, readback.End);
            Assert.Equal(2, readback.Dataset);
            Assert.Equal("libzora.bookmark." + imported.Id.ToString("N"), readback.Id);
            Assert.False((await sync.AnalyzeAsync(device, [deviceBook], fake)).HasChanges);
            var repeat = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(0, repeat.ChangesToComputer + repeat.ChangesToDevice);
            Assert.Equal(1, writes);
            Assert.Single(await reader.GetBookmarksAsync(file.Id));
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task ClearBookmarks(string db)
    {
        await using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM nonsyncable_annotations;";
        await command.ExecuteNonQueryAsync();
    }
}
