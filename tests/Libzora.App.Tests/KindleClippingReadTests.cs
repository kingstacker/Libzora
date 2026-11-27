using System.Reflection;
using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task KindleClippingConcurrentPageLoadsShareOneDeviceRead() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var before = s.Service.NotesRequests;
        var release = new TaskCompletionSource<IReadOnlyList<KindleClipping>>();
        s.Service.NotesReader = token => release.Task.WaitAsync(token);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var first = scope.Call<Task>("RefreshReadingMaterialsAsync");
        await Until(() => s.Service.NotesRequests > before);
        var second = scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Same(first, second);
        release.SetResult(s.Service.Notes);
        await Task.WhenAll(first, second);
        Assert.Equal(before + 1, s.Service.NotesRequests);
        Assert.Single(scope.Window.ReadingMaterials);
    });

    [Fact]
    public Task KindleClippingReloadKeepsExactVersionTextButRereadsChangedEdition() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var book = Assert.Single(scope.Window.DeviceBooks).Book;
        book.ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        book.Size = 1234;
        var record = Assert.Single(s.Service.AnnotationSnapshot!.Records);
        s.Service.NotesReader = _ => Task.FromResult<IReadOnlyList<KindleClipping>>([new()
        {
            Id = "kindle-db:" + record.AnnotationId, BookTitle = book.Title,
            Type = KindleClippingType.Highlight, SourceBook = record.Book,
            StartPosition = record.Start, EndPosition = record.End, IsReadOnly = true,
            Content = "划线原文待定位"
        }]);
        var before = s.Service.ExportRequests;
        scope.Call("RequestKindleClippingReload", s.Device, false);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal(before + 1, s.Service.ExportRequests);
        var saved = Assert.Single((await new KindleDeviceAuxiliaryCacheStore(scope.Paths).GetAsync(s.Device.Identity))!.Clippings);
        Assert.True(saved.IsTextResolved);
        Assert.Equal("你好，世界。😀", saved.Content);
        Assert.NotNull(saved.TextResolutionStamp);

        scope.Call("RequestKindleClippingReload", s.Device, true);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal(before + 1, s.Service.ExportRequests);
        var tasks = AnnotationTasks(scope);
        Assert.Single(tasks.GetSnapshot(), task => task.Title == "读取 Kindle 划线原文");

        book.ModifiedAt = book.ModifiedAt.Value.AddMinutes(1);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal(before + 2, s.Service.ExportRequests);
    });

    [Fact]
    public Task KindleClippingMutationDuringActiveLoadQueuesOneFreshReload() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var before = s.Service.NotesRequests;
        var release = new TaskCompletionSource<IReadOnlyList<KindleClipping>>();
        s.Service.NotesReader = token => s.Service.NotesRequests == before + 1
            ? release.Task.WaitAsync(token) : Task.FromResult(s.Service.Notes);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var first = scope.Call<Task>("RefreshReadingMaterialsAsync");
        await Until(() => s.Service.NotesRequests > before);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var second = scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Same(first, second);
        release.SetResult([]);
        await first;
        Assert.Equal(before + 2, s.Service.NotesRequests);
        Assert.Single(scope.Window.ReadingMaterials);
    });

    [Fact]
    public Task KindleClippingReadWaitsForAuthoritativeShelfScan() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var before = s.Service.NotesRequests;
        var scan = new TaskCompletionSource();
        scope.Set("_deviceBookScanTask", scan.Task);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var read = scope.Call<Task>("RefreshReadingMaterialsAsync");
        await Until(() => scope.Field<CancellationTokenSource?>("_kindleClippingReadCancellation") is not null);
        Assert.Equal(before, s.Service.NotesRequests);
        scan.SetResult();
        await read;
        Assert.Equal(before + 1, s.Service.NotesRequests);
    });

    [Fact]
    public Task LocalAnnotationChangeDuringSharedLoadIsNotLost() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var before = s.Service.NotesRequests;
        var release = new TaskCompletionSource<IReadOnlyList<KindleClipping>>();
        s.Service.NotesReader = token => release.Task.WaitAsync(token);
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var read = scope.Call<Task>("RefreshReadingMaterialsAsync");
        await Until(() => s.Service.NotesRequests > before);
        await scope.Field<ReaderDataService>("_readerData").SaveAnnotationAsync(new()
        {
            BookId = s.Book.Id, BookFileId = s.File.Id, ChapterPath = "Text/chapter.xhtml",
            StartOffset = 0, EndOffset = 4, SelectedText = "新批注",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        scope.Call("MarkReadingMaterialsDirty");
        release.SetResult(s.Service.Notes);
        await read;
        Assert.Single(scope.Window.ReadingMaterials, item => item.Source == ReadingMaterialSource.Local);
    });

    [Fact]
    public Task KindleClippingDisplayTaskCanBeCanceledAndRetried() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        var s = await LoadAnnotationScenarioAsync(scope);
        var book = Assert.Single(scope.Window.DeviceBooks).Book;
        book.ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var record = Assert.Single(s.Service.AnnotationSnapshot!.Records);
        s.Service.NotesReader = _ => Task.FromResult<IReadOnlyList<KindleClipping>>([new()
        {
            Id = "kindle-db:" + record.AnnotationId, BookTitle = book.Title,
            Type = KindleClippingType.Highlight, SourceBook = record.Book,
            StartPosition = record.Start, EndPosition = record.End, IsReadOnly = true
        }]);
        var export = s.Service.ExportHandler;
        var release = new TaskCompletionSource<string>();
        s.Service.ExportHandler = _ => release.Task;
        scope.Call("RequestKindleClippingReload", s.Device, false);
        var read = scope.Call<Task>("RefreshReadingMaterialsAsync");
        await Until(() => AnnotationTasks(scope).GetSnapshot().Any(task => task.Title == "读取 Kindle 划线原文"));
        var task = Assert.Single(AnnotationTasks(scope).GetSnapshot(), task => task.Title == "读取 Kindle 划线原文");
        Assert.DoesNotContain(task.Id, scope.Field<HashSet<Guid>>("_displayOnlyTaskIds"));
        scope.Call("CancelKindleAnnotationAnalysis");
        await read;
        Assert.Equal(BackgroundTaskStatus.Canceled, Assert.Single(AnnotationTasks(scope).GetSnapshot(), item => item.Id == task.Id).Status);
        s.Service.ExportHandler = export;
        await scope.Call<Task>("RefreshReadingMaterialsAsync");
        Assert.Equal("你好，世界。😀", Assert.Single(scope.Window.ReadingMaterials).KindleClipping!.Content);
        release.SetResult(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Azw3", "native-compressed.azw3"));
    });

    private static BackgroundTaskCenterService AnnotationTasks(TestWindow scope) =>
        (BackgroundTaskCenterService)typeof(MainWindow).GetProperty("BackgroundTasks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(scope.Window)!;
}
