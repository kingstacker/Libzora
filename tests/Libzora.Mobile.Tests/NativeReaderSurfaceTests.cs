using Avalonia.Controls;
using Avalonia.VisualTree;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class NativeReaderSurfaceTests(MobileUiSession session)
{
    [Fact]
    public Task FailedNativeOpenShowsAnEventWithoutControlsOrOverwritingProgress() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-native-reader-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "文件暂不可用" };
        var fileId = Guid.NewGuid();
        var previous = new ReaderProgressRow(book.Id, fileId, "chapter.xhtml", null, 2, 219, 43.5, 1, DateTimeOffset.UtcNow);
        await modules.Reader.Data.InitializeAsync();
        await modules.Reader.Data.SaveProgressAsync(previous);
        var reader = new ReaderPage(book, modules, shell);
        var states = new List<NativeReaderState>();
        reader.NativeStateChanged += states.Add;
        reader.UseNativeFrontend();
        var window = new Window { Width = 390, Height = 844, Content = reader };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!states.Any(state => state.Error is not null)) await Task.Delay(10, timeout.Token);
            var failed = reader.GetNativeState();
            Assert.False(failed.Ready);
            Assert.Equal(book.Title, failed.Title);
            Assert.Contains("没有可读取", failed.Error);
            Assert.DoesNotContain(reader.GetVisualDescendants().OfType<Button>(), button => button.IsEffectivelyVisible);
            Assert.False(reader.FindControl<Border>("ErrorPanel")!.IsVisible);
            Assert.False(reader.FindControl<Border>("LoadingPanel")!.IsVisible);
            await reader.PauseAsync();
            window.Close();
            await Task.Delay(50);
            Assert.Equal(previous, await modules.Reader.Data.GetProgressAsync(fileId));
        }
        finally
        {
            window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task ProgressiveSearchKeepsRepeatedHitsAndIgnoresAnOldCancellation() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progressive-search-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "渐进搜索" };
        var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('d', 64) };
        var reader = new ReaderPage(book, modules, shell);
        try
        {
            Directory.CreateDirectory(root);
            var first = Path.Combine(root, "first.xhtml");
            var second = Path.Combine(root, "second.xhtml");
            await File.WriteAllTextAsync(first, "<html><body><p></p></body></html>");
            await File.WriteAllTextAsync(second, "<html><body><p>前面的目标与后面的目标应该分别出现。</p></body></html>");
            var document = new EpubReaderDocument(root, [first, second], [], ["空章", "正文"]);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ReaderPage).GetField("_bookFile", flags)!.SetValue(reader, file);
            typeof(ReaderPage).GetField("_epubDocument", flags)!.SetValue(reader, document);
            await modules.Reader.Data.InitializeAsync();

            async Task<NativeReaderSearchBatch> Search(string id, string? obsoleteId = null)
            {
                var completion = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completedOnUi = true;
                void OnBatch(NativeReaderSearchBatch batch)
                {
                    if (batch.SearchId != id || !batch.IsFinal) return;
                    completedOnUi = Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
                    completion.TrySetResult(batch);
                }
                reader.NativeSearchBatchChanged += OnBatch;
                try
                {
                    reader.NativeStartSearch("目标", id);
                    if (obsoleteId is not null) reader.NativeCancelSearch(obsoleteId);
                    var batch = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(completedOnUi);
                    Assert.Null(batch.Error);
                    return batch;
                }
                finally { reader.NativeSearchBatchChanged -= OnBatch; }
            }

            var initial = await Search("first-request");
            Assert.Equal(2, initial.Items.Length);
            var cached = await Search("cached-request", "first-request");
            Assert.Equal(initial.Items.Select(item => item.Excerpt), cached.Items.Select(item => item.Excerpt));
            Assert.Equal(2, cached.ScannedChapters);
            Assert.True((await modules.Reader.Data.GetBookContentIndexSnapshotAsync(file.Id, file.Sha256)).IsComplete);
        }
        finally
        {
            reader.NativeCancelSearch();
            // Wait for the search's operation lease before deleting its files.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var restore = await MobileDataCoordinator.BeginRestoreAsync(timeout.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
