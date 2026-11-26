using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task FullKindleSyncImportsNewSharedBookAndItsBookmarkInOneRun() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3");
        var metadata = await new BookMetadataService().ReadMetadataAsync(source);
        var device = Reader(scope, "KINDLE-BOOKMARK-FULL", ReaderDeviceProfiles.Kindle);
        var deviceBook = new KindleBook { RelativePath = "documents/native.azw3", Format = "azw3",
            Title = metadata.Title ?? "Fixture", Authors = metadata.Authors ?? "" };
        const string asin = "ed787037-f332-4e71-a7dd-240cd71acf82";
        const string guid = "native-rich:FEB7174A";
        var identity = new KindleAnnotationBookIdentity($"{asin}-EBOK-{guid}-0", asin, guid);
        var time = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var bookmark = new KindleAnnotationRecord("native-bookmark", identity, 2, KindleClippingType.Bookmark,
            1537, 1537, time, time, "", "{}", deviceBook.Title);
        var service = new ReaderService
        {
            Devices = [device], CachedBooks = [deviceBook], BooksReader = _ => Task.FromResult<IReadOnlyList<KindleBook>>([deviceBook]),
            ExportHandler = _ => Task.FromResult(source),
            AnnotationSnapshot = new("system/ksdk/.annotations/fixture/ksdk_annotation_v1.db", new string('a', 64), [bookmark])
        };
        await UseReaderServiceAsync(scope, service);
        await scope.Call<Task>("RefreshDevicesAsync", true, CancellationToken.None);
        scope.Call("ShowStage3Page", scope.Get<Grid>("DevicePage"), null);
        var library = scope.Field<IBookLibraryService>("_library");
        Assert.Empty(await library.SearchAsync());
        var run = scope.Call<Task>("RunKindleFullSyncAsync", device);
        await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
        scope.Get<Button>("ConfirmationOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
        Assert.Contains("书签", scope.Get<TextBlock>("MessageBodyText").Text);
        Assert.DoesNotContain("失败", scope.Get<TextBlock>("MessageBodyText").Text);
        var imported = Assert.Single(await library.SearchAsync());
        var reader = scope.Field<ReaderDataService>("_readerData");
        var bookmarks = new List<ReaderBookmark>();
        foreach (var file in imported.Files) bookmarks.AddRange(await reader.GetBookmarksAsync(file.Id));
        var saved = Assert.Single(bookmarks);
        Assert.Contains("你好", saved.Quote);
        Assert.NotNull(saved.ContentPosition!.AnchorQuote);
        Assert.True(service.AnnotationSnapshotRequests >= 3);
        Assert.Equal(0, service.AnnotationWriteRequests);
        scope.Get<Button>("MessageOkButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await run;
    });
}
