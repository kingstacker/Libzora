using System.Reflection;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed partial class S3SyncIntegrationTests
{
    [Fact]
    public async Task ConcurrentHighlightsOnSamePassageMergeFieldsAndRemoveDuplicate()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        var createdAt = DateTimeOffset.UtcNow;
        var noteA = new ReaderAnnotation
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            StartOffset = 100, EndOffset = 120, SelectedText = "the same passage",
            Color = "#ff0000", UnderlineStyle = "solid", Note = "A's note",
            CreatedAt = createdAt, UpdatedAt = createdAt
        };
        var noteB = new ReaderAnnotation
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            StartOffset = 100, EndOffset = 120, SelectedText = "the same passage",
            Color = "#00ff00", UnderlineStyle = "double", Note = "B's note",
            CreatedAt = createdAt.AddSeconds(1), UpdatedAt = createdAt.AddSeconds(1)
        };
        await a.Reader.SaveAnnotationAsync(noteA);
        await b.Reader.SaveAnnotationAsync(noteB);

        await a.SyncAsync();
        Assert.Contains("A's note", Assert.Single(bucket.Snapshot(a.SnapshotKey).Annotations).Note,
            StringComparison.Ordinal);
        await b.SyncAsync();
        var afterBMerge = Assert.Single(await b.Reader.GetAnnotationsAsync(book.FileId));
        Assert.Contains("B's note", afterBMerge.Note, StringComparison.Ordinal);
        Assert.Contains("A's note", afterBMerge.Note, StringComparison.Ordinal);
        Assert.Contains("B's note", Assert.Single(bucket.Snapshot(b.SnapshotKey).Annotations).Note, StringComparison.Ordinal);
        await a.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var merged = Assert.Single(await device.Reader.GetAnnotationsAsync(book.FileId));
            Assert.Equal(noteA.Id, merged.Id);
            Assert.Equal("#ff0000", merged.Color);
            Assert.Contains("A's note", merged.Note, StringComparison.Ordinal);
            Assert.Contains("B's note", merged.Note, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConcurrentBookmarksAtSamePositionMergeIntoOneButKeepDifferentTitles()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        var first = new ReaderBookmark
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            ChapterIndex = 2, ScrollPosition = 300, FlowMode = 1, Title = "Chapter 3"
        };
        var second = new ReaderBookmark
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            ChapterIndex = 2, ScrollPosition = 300, FlowMode = 1, Title = "Chapter 3"
        };
        var different = new ReaderBookmark
        {
            BookId = book.BookId, BookFileId = book.FileId, ChapterPath = "chapter.xhtml",
            ChapterIndex = 2, ScrollPosition = 300, FlowMode = 1, Title = "Important quote"
        };
        await a.Reader.SaveBookmarkAsync(first);
        await a.Reader.SaveBookmarkAsync(different);
        await b.Reader.SaveBookmarkAsync(second);

        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var bookmarks = await device.Reader.GetBookmarksAsync(book.FileId);
            Assert.Equal(2, bookmarks.Count);
            Assert.Contains(bookmarks, row => row.Title == "Chapter 3");
            Assert.Contains(bookmarks, row => row.Title == "Important quote");
        }
    }

    [Fact]
    public async Task ConcurrentBookEditsToDifferentFieldsAreBothKept()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        var created = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();

        var later = DateTimeOffset.UtcNow.AddMinutes(1);
        await a.SqlAsync("UPDATE Books SET Tags=$tags, UpdatedAt=$time WHERE Id=$id;",
            ("$tags", "history,classic"), ("$time", later.ToString("O")), ("$id", created.BookId.ToString()));
        await b.SqlAsync("UPDATE Books SET IsFavorite=1, UpdatedAt=$time WHERE Id=$id;",
            ("$time", later.AddMinutes(1).ToString("O")), ("$id", created.BookId.ToString()));

        await a.SyncAsync();
        Assert.Equal("history,classic", Assert.Single(bucket.Snapshot(a.SnapshotKey).Books).Tags);
        await b.SyncAsync();
        Assert.Equal("history,classic", Assert.Single(bucket.Snapshot(b.SnapshotKey).Books).Tags);
        Assert.True(Assert.Single(bucket.Snapshot(b.SnapshotKey).Books).IsFavorite);
        await a.SyncAsync();
        await b.SyncAsync();

        foreach (var device in new[] { a, b })
        {
            var book = Assert.Single(await device.Library.SearchAsync());
            Assert.Equal("history,classic", book.Tags);
            Assert.True(book.IsFavorite);
        }
    }

    [Fact]
    public async Task ThreeDevicesEditingDifferentBookFieldsKeepEveryField()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await using var c = await Device.CreateAsync(bucket);
        var created = await a.AddBookAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        await c.SyncAsync();

        var editTime = DateTimeOffset.UtcNow;
        await a.SqlAsync("UPDATE Books SET Title=$title, UpdatedAt=$time WHERE Id=$id;",
            ("$title", "A title"), ("$time", editTime.AddSeconds(3).ToString("O")),
            ("$id", created.BookId.ToString()));
        await b.SqlAsync("UPDATE Books SET Tags=$tags, UpdatedAt=$time WHERE Id=$id;",
            ("$tags", "history,classic"), ("$time", editTime.AddSeconds(1).ToString("O")),
            ("$id", created.BookId.ToString()));
        await c.SqlAsync("UPDATE Books SET Category=$category, UpdatedAt=$time WHERE Id=$id;",
            ("$category", "C category"), ("$time", editTime.AddSeconds(2).ToString("O")),
            ("$id", created.BookId.ToString()));

        await a.SyncAsync();
        await b.SyncAsync();
        await c.SyncAsync();

        await using var d = await Device.CreateAsync(bucket);
        await d.SyncAsync();
        foreach (var device in new[] { c, d })
        {
            var book = Assert.Single(await device.Library.SearchAsync());
            Assert.Equal("A title", book.Title);
            Assert.Equal("history,classic", book.Tags);
            Assert.Equal("C category", book.Category);
        }

        await a.SyncAsync();
        await b.SyncAsync();
        await c.SyncAsync();
        foreach (var device in new[] { a, b, c, d })
        {
            var book = Assert.Single(await device.Library.SearchAsync());
            Assert.Equal("A title", book.Title);
            Assert.Equal("history,classic", book.Tags);
            Assert.Equal("C category", book.Category);
        }
    }

    [Fact]
    public async Task ThreeDevicesEditingDifferentFieldsOfOneAnnotationKeepEveryField()
    {
        var bucket = new MemoryBucket();
        await using var a = await Device.CreateAsync(bucket);
        await using var b = await Device.CreateAsync(bucket);
        await using var c = await Device.CreateAsync(bucket);
        var book = await a.AddBookAsync();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-2);
        var annotationId = Guid.NewGuid();
        await a.Reader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = annotationId, BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", StartOffset = 100, EndOffset = 120,
            SelectedText = "the same passage", Color = "#000000",
            UnderlineStyle = "solid", Note = "base note",
            CreatedAt = baseTime, UpdatedAt = baseTime
        });
        await a.SyncAsync();
        await b.SyncAsync();
        await c.SyncAsync();

        ReaderAnnotation Edit(string color, string underline, string note, DateTimeOffset time) => new()
        {
            Id = annotationId, BookId = book.BookId, BookFileId = book.FileId,
            ChapterPath = "chapter.xhtml", StartOffset = 100, EndOffset = 120,
            SelectedText = "the same passage", Color = color,
            UnderlineStyle = underline, Note = note,
            CreatedAt = baseTime, UpdatedAt = time
        };
        // Deliberately give the three devices different wall-clock positions
        // after their last sync. The two older remote rows must not collapse
        // into the newest one before the field merge runs.
        var editTime = DateTimeOffset.UtcNow;
        await a.Reader.SaveAnnotationAsync(Edit("#000000", "solid", "A's note", editTime.AddSeconds(3)));
        await b.Reader.SaveAnnotationAsync(Edit("#00ff00", "solid", "base note", editTime.AddSeconds(1)));
        await c.Reader.SaveAnnotationAsync(Edit("#000000", "double", "base note", editTime.AddSeconds(2)));
        Assert.Equal("double", Assert.Single(await c.Reader.GetAnnotationsAsync(book.FileId)).UnderlineStyle);

        await a.SyncAsync();
        await b.SyncAsync();
        var bSnapshotRow = Assert.Single(bucket.Snapshot(b.SnapshotKey).Annotations);
        Assert.Equal("#00ff00", bSnapshotRow.Color);
        Assert.Equal("A's note", bSnapshotRow.Note);
        await c.SyncAsync();
        var cLocalAfter = Assert.Single(await c.Reader.GetAnnotationsAsync(book.FileId));
        Assert.Equal("double", cLocalAfter.UnderlineStyle);
        Assert.Equal("#00ff00", cLocalAfter.Color);
        var cSnapshotRow = Assert.Single(bucket.Snapshot(c.SnapshotKey).Annotations);
        Assert.Equal("#00ff00", cSnapshotRow.Color);
        Assert.Equal("double", cSnapshotRow.UnderlineStyle);
        Assert.Equal("A's note", cSnapshotRow.Note);

        // A device that has never seen any of the three edits must still
        // receive all of them, not just the row with the latest UpdatedAt.
        await using var d = await Device.CreateAsync(bucket);
        await d.SyncAsync();
        var merged = Assert.Single(await d.Reader.GetAnnotationsAsync(book.FileId));
        Assert.Equal(annotationId, merged.Id);
        Assert.Equal("A's note", merged.Note);
        Assert.Equal("#00ff00", merged.Color);
        Assert.Equal("double", merged.UnderlineStyle);

        await a.SyncAsync();
        await b.SyncAsync();
        await c.SyncAsync();
        foreach (var device in new[] { a, b, c, d })
        {
            var final = Assert.Single(await device.Reader.GetAnnotationsAsync(book.FileId));
            Assert.Equal("A's note", final.Note);
            Assert.Equal("#00ff00", final.Color);
            Assert.Equal("double", final.UnderlineStyle);
        }
    }

    [Fact]
    public async Task HybridClockAdvancesPastObservedRemoteVersion()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await S3SyncService.EnsureVersionSchemaAsync(connection, null, CancellationToken.None);
        var clock = await S3SyncClock.LoadAsync(connection, null, "device-a", CancellationToken.None);
        var remote = S3SyncVersion.Create(
            DateTimeOffset.UtcNow.AddDays(1).UtcTicks, 3, "device-b");
        clock.ObserveVersion(remote);
        var local = clock.Next(DateTimeOffset.UtcNow);
        Assert.True(S3SyncVersion.Compare(
            local, DateTimeOffset.UtcNow, remote, DateTimeOffset.UtcNow) > 0);
        Assert.True(S3SyncVersion.Compare(
            S3SyncVersion.Create(10, 1, "device-a"), DateTimeOffset.UnixEpoch,
            S3SyncVersion.Create(10, 0, "device-a"), DateTimeOffset.UnixEpoch) > 0);
    }

    [Fact]
    public void DuplicateAnnotationPredicateMatchesSamePassage()
    {
        var bookId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var a = new S3SyncAnnotation
        {
            Id = Guid.NewGuid(), BookId = bookId, BookFileId = fileId, ChapterPath = "chapter.xhtml",
            StartOffset = 100, EndOffset = 120, SelectedText = "the same passage"
        };
        var b = new S3SyncAnnotation
        {
            Id = Guid.NewGuid(), BookId = bookId, BookFileId = fileId, ChapterPath = "chapter.xhtml",
            StartOffset = 100, EndOffset = 120, SelectedText = "the same passage"
        };
        var method = typeof(S3SyncService).GetMethod(
            "IsDuplicateAnnotation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True((bool)method!.Invoke(null, [a, b])!);
    }

    [Fact]
    public void UnknownVersionedArraysMergeRowByRowForFutureClients()
    {
        static JsonElement Json(string value)
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }

        var now = DateTimeOffset.UtcNow;
        var target = new S3SyncSnapshot
        {
            DeviceId = "old-client",
            CreatedAt = now,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["listeningStats"] = Json("""
                    [{"id":"device-a","updatedAt":"2026-01-01T00:00:00Z","seconds":10}]
                    """)
            }
        };
        var newer = new S3SyncSnapshot
        {
            DeviceId = "new-client",
            CreatedAt = now.AddMinutes(1),
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["listeningStats"] = Json("""
                    [
                      {"id":"device-a","updatedAt":"2026-01-02T00:00:00Z","seconds":99},
                      {"id":"device-b","updatedAt":"2026-01-02T00:00:00Z","seconds":20}
                    ]
                    """)
            }
        };

        var method = typeof(S3SyncService).GetMethod(
            "PreserveForwardCompatibleData",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, [target, null, new[] { newer }]);

        var rows = target.ExtensionData!["listeningStats"].EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetString()!, row => row);
        Assert.Equal(2, rows.Count);
        Assert.Equal(99, rows["device-a"].GetProperty("seconds").GetInt32());
        Assert.Equal(20, rows["device-b"].GetProperty("seconds").GetInt32());
    }

    [Fact]
    public void SnapshotSizeLimitRejectsOversizedPayloads()
    {
        S3SyncService.EnsureSnapshotSize(100, 100);
        Assert.Throws<InvalidDataException>(() => S3SyncService.EnsureSnapshotSize(101, 100));
    }
}
