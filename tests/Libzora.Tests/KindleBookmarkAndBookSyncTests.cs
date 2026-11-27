using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed partial class KindleBookmarkAndBookSyncTests
{
    [Fact]
    public async Task ReaderAndWriterSupportDataset2Bookmarks()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "ksdk_annotation_v1.db");
            var candidate = Path.Combine(root, "candidate.db");
            await CreateBookmarkDatabaseAsync(db);

            var records = await KindleAnnotationDatabaseReader.ReadRecordsAsync(db, requireCompleteSnapshot: true);
            var bookmark = Assert.Single(records);
            Assert.Equal(2, bookmark.Dataset);
            Assert.Equal(KindleClippingType.Bookmark, bookmark.Type);
            Assert.Equal(8029, bookmark.Start);
            Assert.Equal(8029, bookmark.End);

            var payload = JsonSerializer.Serialize(new
            {
                book_data = new { asin = "asin-1", contentType = "EBOK", guid = "Gui_Mo:09F7C8F2", isOwnedByCustomer = 0, isSample = 0 },
                created_time = 1795520900000L,
                end_position = new { longPosition = "15675", shortPosition = 15675 },
                json_metadata = "{\"mchl_color\":\"dark_blue\"}",
                last_modified = 1795520900000L,
                position_type = 0,
                start_position = new { longPosition = "15675", shortPosition = 15675 },
                type = "BOOKMARK"
            });
            var hash = await KindleAnnotationDatabaseWriter.BuildCandidateAsync(
                db,
                candidate,
                [new KindleAnnotationMutation(
                    "libzora.bookmark.abc",
                    "asin-1-EBOK-Gui_Mo:09F7C8F2-0",
                    2,
                    15675,
                    15675,
                    1795520900000L,
                    1795520900000L,
                    string.Empty,
                    payload)]);
            Assert.False(string.IsNullOrWhiteSpace(hash));

            var after = await KindleAnnotationDatabaseReader.ReadRecordsAsync(candidate, requireCompleteSnapshot: true);
            Assert.Equal(2, after.Count);
            Assert.Contains(after, item => item.Id == "libzora.bookmark.abc" && item.Dataset == 2 && item.Start == 15675);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task BidirectionalBookmarkSyncPullsAndPushesWithoutDuplicates()
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
            var readerData = new ReaderDataService(paths);
            await readerData.InitializeAsync();

            var raw = await KindleBookTextService.ReadAsync(azw3);
            Assert.True(raw.TryFindRange("你好，世界。😀", null, null, out var start, out _));
            var bookId = raw.CreateBookId()!;
            var deviceBookmark = new KindleAnnotationRecord(
                $"kindle.bookmark-{start}",
                new KindleAnnotationBookIdentity(bookId, raw.Asin, raw.CreateGuid()),
                2,
                KindleClippingType.Bookmark,
                start,
                start,
                1_795_520_867_553,
                1_795_520_867_553,
                string.Empty,
                "{}",
                book.Title);

            var fake = new BookmarkFakeKindleService(new KindleAnnotationSnapshot(
                @"system\ksdk\.annotations\amzn1.account.test\ksdk_annotation_v1.db",
                new string('b', 64),
                [deviceBookmark]), azw3);
            var sync = new KindleAnnotationSyncService(paths, library, readerData, new EpubBookContentService(readerData));
            var device = new KindleDevice
            {
                RootPath = @"\\?\usb#test",
                VolumeSerial = "KINDLE-TEST",
                Name = "Kindle Scribe",
                Transport = KindleTransport.Mtp
            };
            var deviceBook = new KindleBook
            {
                RelativePath = "documents/native.azw3",
                Title = book.Title,
                Authors = book.Authors,
                Format = "azw3"
            };

            // 1. Pull bookmark from Kindle -> Computer
            var pullPreview = await sync.AnalyzeAsync(device, [deviceBook], fake);
            Assert.True(pullPreview.HasChanges);
            Assert.Equal(1, pullPreview.BookmarkDifferenceCount);
            var pullResult = await sync.SynchronizeAsync(device, [deviceBook], fake);
            Assert.Equal(1, pullResult.BookmarksToComputer);
            Assert.Equal(0, pullResult.BookmarksToDevice);

            var imported = Assert.Single(await readerData.GetBookmarksAsync(file.Id));
            Assert.Contains("你好，世界", imported.Quote);
            Assert.NotNull(imported.ContentPosition);
            Assert.True(imported.ContentPosition!.TextOffset >= 0);

            // 2. Re-analyze: already in sync, no duplicate
            var afterPull = await sync.AnalyzeAsync(device, [deviceBook], fake);
            Assert.False(afterPull.HasChanges);

            // 3. Push local bookmark to a device with empty snapshot
            var emptyDevice = new BookmarkFakeKindleService(fake.Snapshot with { Records = [] }, azw3);
            var pushPreview = await sync.AnalyzeAsync(device, [deviceBook], emptyDevice);
            Assert.True(pushPreview.HasChanges);
            Assert.Equal(1, pushPreview.Books.Sum(item => item.BookmarksToDevice));
            var pushResult = await sync.SynchronizeAsync(device, [deviceBook], emptyDevice);
            Assert.Equal(1, pushResult.BookmarksToDevice);

            var mutation = Assert.Single(emptyDevice.Mutations);
            Assert.Equal(2, mutation.Dataset);
            Assert.Equal(start, mutation.Start);
            Assert.Equal(mutation.Start, mutation.End);
            Assert.StartsWith("libzora.bookmark.", mutation.AnnotationId);
            Assert.Contains("\"type\":\"BOOKMARK\"", mutation.SerializedPayload);
            Assert.Contains("dark_blue", mutation.SerializedPayload);
            Assert.False((await sync.AnalyzeAsync(device, [deviceBook], emptyDevice)).HasChanges);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public void BookSyncPlannerComputesUnionWithoutDeleting()
    {
        var shared = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Shared Book",
            Authors = "Author A",
            Files = [new BookFile { Format = "epub", RelativePath = "shared.epub", Sha256 = "aaa" }]
        };
        var localOnly = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Computer Only",
            Authors = "Author B",
            Files = [new BookFile { Format = "epub", RelativePath = "local.epub", Sha256 = "bbb" }]
        };
        var localUnsupported = new Book
        {
            Id = Guid.NewGuid(),
            Title = "Unsupported Local",
            Authors = "Author C",
            Files = [new BookFile { Format = "cbz", RelativePath = "comic.cbz", Sha256 = "ccc" }]
        };

        KindleBook[] deviceBooks =
        [
            new KindleBook { RelativePath = "documents/shared.azw3", Title = "Shared Book", Authors = "Author A", Format = "azw3" },
            new KindleBook { RelativePath = "documents/device.azw3", Title = "Kindle Only", Authors = "Author D", Format = "azw3" },
            new KindleBook { RelativePath = "documents/raw.txt", Title = "Plain Notes", Authors = "", Format = "txt" }
        ];

        var plan = KindleBookSyncPlanner.Plan([shared, localOnly, localUnsupported], deviceBooks, ReaderDeviceProfiles.Kindle);

        Assert.True(plan.HasBookChanges);
        Assert.Equal(1, plan.SharedBookCount);
        Assert.Equal("Computer Only", Assert.Single(plan.BooksToDevice).Title);
        Assert.Equal("Kindle Only", Assert.Single(plan.BooksToComputer).Title);
        Assert.Equal(1, plan.SkippedLocalBookCount);
        Assert.Equal(1, plan.SkippedDeviceBookCount);
    }

    [Fact]
    public async Task BackupStoreSavesManifestListsBackupsAndValidatesIntegrity()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var device = new KindleDevice
            {
                RootPath = @"\\?\usb#test",
                VolumeSerial = "KINDLE-ROLLBACK",
                Name = "Kindle Scribe",
                Transport = KindleTransport.Mtp
            };

            var deviceKey = KindleAnnotationBackupStore.ComputeDeviceKey(device);
            var backupDir = Path.Combine(paths.Backups, "KindleAnnotations", deviceKey, "20261124-205000000");
            Directory.CreateDirectory(backupDir);
            var backupDb = Path.Combine(backupDir, "ksdk_annotation_v1.db");
            await CreateBookmarkDatabaseAsync(backupDb);

            var actualHash = await KindleAnnotationBackupStore.ValidateBackupFileAsync(backupDb, null);
            Assert.Equal(64, actualHash.Length);

            var dbRelative = "system/ksdk/.annotations/amzn1.account.test/ksdk_annotation_v1.db";
            KindleAnnotationBackupStore.SaveManifest(
                backupDb, device, dbRelative, actualHash, dbRelative + ".libzora-backup-1", "sync");

            var backups = KindleAnnotationBackupStore.ListBackups(paths, device);
            var entry = Assert.Single(backups);
            Assert.Equal(backupDb, entry.LocalBackupPath);
            Assert.Equal(dbRelative, entry.DatabaseRelativePath);
            Assert.Equal(actualHash, entry.Sha256);
            Assert.Equal("sync", entry.Reason);

            // Re-validating with matching SHA-256 succeeds; mismatched SHA-256 is rejected.
            Assert.Equal(actualHash, await KindleAnnotationBackupStore.ValidateBackupFileAsync(entry.LocalBackupPath, entry.Sha256));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                KindleAnnotationBackupStore.ValidateBackupFileAsync(entry.LocalBackupPath, new string('0', 64)));

            // Saving 5 newer backups retains only the newest 3.
            var seedDb = Path.Combine(root, "seed.db");
            File.Copy(backupDb, seedDb, overwrite: true);
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(1);
            for (var i = 1; i <= 5; i++)
            {
                var dir = Path.Combine(paths.Backups, "KindleAnnotations", deviceKey, $"20261124-21000{i}000");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "ksdk_annotation_v1.db");
                File.Copy(seedDb, file, overwrite: true);
                KindleAnnotationBackupStore.SaveManifest(
                    file, device, dbRelative, actualHash, null, $"sync-{i}", baseTime.AddMinutes(i));
            }
            var retained = KindleAnnotationBackupStore.ListBackups(paths, device);
            Assert.Equal(KindleAnnotationBackupStore.MaxRetainedBackups, retained.Count);
            Assert.Equal(
                ["sync-5", "sync-4", "sync-3"],
                retained.Select(item => item.Reason ?? string.Empty).ToArray());
            Assert.Equal(3, Directory.GetDirectories(Path.Combine(paths.Backups, "KindleAnnotations", deviceKey)).Length);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    private static async Task CreateBookmarkDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE nonsyncable_annotations (
                annotation_id TEXT NOT NULL,
                book_id TEXT NOT NULL,
                dataset INTEGER NOT NULL,
                start_position TEXT,
                end_position TEXT,
                created_time INTEGER,
                modified_time INTEGER,
                serialized_payload TEXT NOT NULL
            );
            INSERT INTO nonsyncable_annotations VALUES (
                'kindle.bookmark-8029',
                'asin-1-EBOK-Gui_Mo:09F7C8F2-0',
                2,
                '{"longPosition":"8029","shortPosition":8029}',
                '{"longPosition":"8029","shortPosition":8029}',
                1795520867553,
                1795520867553,
                '{"book_data":{"asin":"asin-1","contentType":"EBOK","guid":"Gui_Mo:09F7C8F2","isOwnedByCustomer":0,"isSample":0},"created_time":1795520867553,"end_position":{"longPosition":"8029","shortPosition":8029},"json_metadata":"{\"mchl_color\":\"dark_blue\"}","last_modified":1795520867553,"position_type":0,"start_position":{"longPosition":"8029","shortPosition":8029},"type":"BOOKMARK"}'
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class BookmarkFakeKindleService(KindleAnnotationSnapshot snapshot, string sourceBook) : IKindleDeviceService
    {
        public KindleAnnotationSnapshot Snapshot { get; private set; } = snapshot;
        public List<KindleAnnotationMutation> Mutations { get; } = [];
        public Func<CancellationToken, Task<KindleAnnotationSnapshot?>>? SnapshotReader { get; init; }
        public Func<KindleAnnotationSnapshot, IReadOnlyList<KindleAnnotationMutation>, CancellationToken, Task<KindleAnnotationWriteResult>>? Writer { get; init; }

        public async Task<KindleAnnotationSnapshot?> ReadAnnotationSnapshotAsync(KindleDevice device, CancellationToken cancellationToken = default)
        {
            if (SnapshotReader is null) return Snapshot;
            var value = await SnapshotReader(cancellationToken);
            if (value is not null) Snapshot = value;
            return value;
        }

        public Task<string> ExportBookAsync(KindleDevice device, KindleBook book, string destinationDirectory, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destinationDirectory);
            var target = Path.Combine(destinationDirectory, Path.GetFileName(sourceBook));
            File.Copy(sourceBook, target, overwrite: true);
            return Task.FromResult(target);
        }

        public Task<KindleAnnotationWriteResult> ApplyAnnotationMutationsAsync(
            KindleDevice device, KindleAnnotationSnapshot snapshot, IReadOnlyList<KindleAnnotationMutation> mutations, CancellationToken cancellationToken = default)
        {
            Mutations.AddRange(mutations);
            if (Writer is not null) return Writer(snapshot, mutations, cancellationToken);
            Snapshot = snapshot with
            {
                Records = snapshot.Records.Where(record => !mutations.Any(mutation =>
                        mutation.AnnotationId == record.AnnotationId && mutation.BookId == record.Book.BookId))
                    .Concat(mutations.Select(mutation =>
                    {
                        using var payload = JsonDocument.Parse(mutation.SerializedPayload);
                        var data = payload.RootElement.GetProperty("book_data");
                        return new KindleAnnotationRecord(mutation.AnnotationId,
                            new(mutation.BookId, data.GetProperty("asin").GetString(), data.GetProperty("guid").GetString()),
                            mutation.Dataset, KindleClippingType.Bookmark, mutation.Start, mutation.End,
                            mutation.CreatedTime, mutation.ModifiedTime, mutation.NoteText, mutation.SerializedPayload);
                    })).ToArray()
            };
            return Task.FromResult(new KindleAnnotationWriteResult(mutations.Count, snapshot.Sha256, new string('c', 64), null, null));
        }

        public Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KindleDevice>>([]);
        public Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KindleBook>>([]);
        public Task<IReadOnlyList<KindleBook>> ScanBooksProgressivelyAsync(KindleDevice device, IProgress<KindleScanProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KindleBook>>([]);
        public Task SendBookAsync(KindleDevice device, BookFile file, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default, string? coverOverridePath = null) => Task.CompletedTask;
        public Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KindleDeviceResource>> ScanResourcesAsync(KindleDevice device, KindleResourceKind kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KindleDeviceResource>>([]);
        public Task SendResourceAsync(KindleDevice device, KindleResourceKind kind, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportResourceAsync(KindleDevice device, KindleDeviceResource resource, string destinationDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveResourceAsync(KindleDevice device, KindleDeviceResource resource, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<KindleClipping>> ReadClippingsAsync(KindleDevice device, CancellationToken cancellationToken = default, int maxItems = int.MaxValue) => Task.FromResult<IReadOnlyList<KindleClipping>>([]);
        public Task DeleteClippingAsync(KindleDevice device, string clippingId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteClippingsAsync(KindleDevice device, IReadOnlyCollection<string> clippingIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
