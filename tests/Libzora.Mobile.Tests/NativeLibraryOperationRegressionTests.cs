using System.Text.Json;
using Libzora.Core;
using Libzora.iOSHead;
using Xunit;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.Mobile.Tests;

public sealed class NativeLibraryOperationRegressionTests
{
    [Fact]
    public void ExplicitUnreadPreservesProgressAndRejectsChangedMetadata()
    {
        var id = Guid.NewGuid();
        var original = new MobilePreferences();
        var changed = original.WithManualReadingStatus(id, LibraryReadingStatus.Unread);
        Assert.True(changed.HasManualReadingStatus(id, LibraryReadingStatus.Unread));
        Assert.False(changed.HasManualReadingStatus(id, LibraryReadingStatus.Finished));
        Assert.Null(original.ManualBookReadingStatuses);
        Assert.Equal(original.ReaderFontScale, changed.ReaderFontScale);
        Assert.Equal(original.PdfZoom, changed.PdfZoom);
    }

    [Fact]
    public void InvalidStatusPreferenceKeysAndValuesAreRemoved()
    {
        var id = Guid.NewGuid();
        var result = MobilePreferences.NormalizeManualReadingStatuses(new()
        {
            [id.ToString()] = 1, ["not-a-book"] = 0, [Guid.NewGuid().ToString()] = 999
        });
        Assert.Single(result!);
        Assert.Equal(1, result![id.ToString("N")]);
    }

    [Fact]
    public void RecentFileSelectionNeverUsesForeignOrUnsupportedLocations()
    {
        var book = new Book { Id = Guid.NewGuid() };
        var epub = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub" };
        var pdf = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "PDF" };
        var foreign = new BookFile { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Format = "pdf" };
        var unsupported = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "docx" };
        book.Files = [epub, pdf, foreign, unsupported];
        ReaderProgressRow Row(BookFile file, int seconds) => new(book.Id, file.Id, "", null, 0, 0, 0, 1,
            DateTimeOffset.UtcNow.AddSeconds(seconds));
        Assert.Same(pdf, IOSNativeBookLibraryPolicy.MostRecentlyReadFile(book,
            [Row(epub, 0), Row(pdf, 1), Row(foreign, 2), Row(unsupported, 3)]));
        Assert.Null(IOSNativeBookLibraryPolicy.MostRecentlyReadFile(book, [Row(pdf, 1) with { BookId = Guid.NewGuid() }]));
    }

    [Fact]
    public void NewPreferencesAndFilesRoundTripThroughAotContract()
    {
        using var old = JsonDocument.Parse("{}");
        var legacy = Read(old.RootElement, new MobilePreferences(), IOSNativeJsonContext.Default.MobilePreferences);
        Assert.False(legacy.PreviewTxtImport);
        Assert.Null(legacy.ManualBookReadingStatuses);
        var snapshot = new NativeBookFilesSnapshot([new NativeBookFile(Guid.NewGuid(), "Book.pdf", "PDF", "1 MB", true, false)], null);
        using var json = JsonDocument.Parse(Serialize(snapshot));
        Assert.True(json.RootElement.GetProperty("files")[0].GetProperty("readable").GetBoolean());
    }

    [Fact]
    public async Task ExportIsByteExactAndKeepsSourceAfterDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "libzora-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.pdf");
            var content = Enumerable.Range(0, 10000).Select(value => (byte)(value % 256)).ToArray();
            await File.WriteAllBytesAsync(source, content);
            string exported;
            using (var export = await MobileBookFileExport.CreateAsync(source, "Book.pdf"))
            {
                exported = export.Path;
                Assert.Equal(content, await File.ReadAllBytesAsync(exported));
            }
            Assert.False(File.Exists(exported));
            Assert.Equal(content, await File.ReadAllBytesAsync(source));
            await Assert.ThrowsAsync<ArgumentException>(() => MobileBookFileExport.CreateAsync(source, "../other.pdf"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MobileBookFileExport.CreateAsync(source, "Cancelled.pdf", new CancellationToken(true)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
