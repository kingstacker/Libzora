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
            Assert.Null(await reader.NativeCapturePageAsync(CancellationToken.None));
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

            async Task<(NativeReaderSearchBatch Final, List<NativeReaderSearchItem> Items)> Search(string id, string? obsoleteId = null)
            {
                var completion = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                var completedOnUi = true;
                var items = new List<NativeReaderSearchItem>();
                void OnBatch(NativeReaderSearchBatch batch)
                {
                    if (batch.SearchId != id) return;
                    if (!batch.IsIncremental) items.Clear();
                    items.AddRange(batch.Items);
                    if (!batch.IsFinal) return;
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
                    return (batch, items);
                }
                finally { reader.NativeSearchBatchChanged -= OnBatch; }
            }

            var initial = await Search("first-request");
            Assert.Equal(2, initial.Items.Count);
            Assert.False(initial.Final.HasMore);
            var cached = await Search("cached-request", "first-request");
            Assert.Equal(initial.Items.Select(item => item.Excerpt), cached.Items.Select(item => item.Excerpt));
            Assert.Equal(2, cached.Final.ScannedChapters);
            Assert.False(cached.Final.HasMore);
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

    [Fact]
    public Task ProgressiveEpubSearchPausesAt200AndResumesWithoutDuplicatesOrMissedHits() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progressive-resume-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "大书断点续搜" };
        var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('e', 64) };
        var reader = new ReaderPage(book, modules, shell);
        try
        {
            Directory.CreateDirectory(root);
            int[] countsPerChapter = [40, 30, 60, 60, 60];
            var chapterPaths = new List<string>(countsPerChapter.Length);
            var chapterTitles = new List<string>(countsPerChapter.Length);
            for (var chapter = 0; chapter < countsPerChapter.Length; chapter++)
            {
                var path = Path.Combine(root, $"ch{chapter}.xhtml");
                var body = string.Join(" ", Enumerable.Range(0, countsPerChapter[chapter])
                    .Select(index => $"第{chapter}章段落{index}目标词"));
                await File.WriteAllTextAsync(path, $"<html><body><p>{body}</p></body></html>");
                chapterPaths.Add(path);
                chapterTitles.Add($"第 {chapter + 1} 章");
            }
            var document = new EpubReaderDocument(root, chapterPaths, [], chapterTitles);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ReaderPage).GetField("_bookFile", flags)!.SetValue(reader, file);
            typeof(ReaderPage).GetField("_epubDocument", flags)!.SetValue(reader, document);
            typeof(ReaderPage).GetField("_chapterIndex", flags)!.SetValue(reader, 2);
            await modules.Reader.Data.InitializeAsync();

            var collected = new List<NativeReaderSearchItem>();
            TaskCompletionSource<NativeReaderSearchBatch>? waitFinal = null;
            const string searchId = "resume-epub";
            void OnBatch(NativeReaderSearchBatch batch)
            {
                if (batch.SearchId != searchId) return;
                if (!batch.IsIncremental) collected.Clear();
                collected.AddRange(batch.Items);
                if (batch.IsFinal) waitFinal?.TrySetResult(batch);
            }

            reader.NativeSearchBatchChanged += OnBatch;
            try
            {
                waitFinal = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                reader.NativeStartSearch("目标词", searchId);
                var firstBatch = await waitFinal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(firstBatch.Error);
                Assert.Equal(200, collected.Count);
                Assert.Equal(200, firstBatch.TotalHits);
                Assert.True(firstBatch.HasMore);
                Assert.False((await modules.Reader.Data.GetBookContentIndexSnapshotAsync(file.Id, file.Sha256)).IsComplete);

                waitFinal = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                reader.NativeContinueSearch(searchId);
                var secondBatch = await waitFinal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(secondBatch.Error);
                Assert.Equal(250, collected.Count);
                Assert.Equal(250, secondBatch.TotalHits);
                Assert.False(secondBatch.HasMore);
                Assert.Equal(5, secondBatch.ScannedChapters);
                Assert.Equal(5, secondBatch.TotalChapters);
                Assert.Equal(250, collected.Select(item => item.Id).Distinct().Count());
                Assert.Equal(250, collected.Select(item => (item.ChapterIndex, item.Offset)).Distinct().Count());
                Assert.True((await modules.Reader.Data.GetBookContentIndexSnapshotAsync(file.Id, file.Sha256)).IsComplete);
            }
            finally
            {
                reader.NativeSearchBatchChanged -= OnBatch;
            }
        }
        finally
        {
            reader.NativeCancelSearch();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var restore = await MobileDataCoordinator.BeginRestoreAsync(timeout.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task ProgressivePdfSearchPausesAt200AndResumesAllPages() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-progressive-pdf-resume-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var book = new Book { Id = Guid.NewGuid(), Title = "PDF断点续搜" };
        var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "pdf", Sha256 = new string('f', 64) };
        var reader = new ReaderPage(book, modules, shell);
        try
        {
            Directory.CreateDirectory(root);
            await modules.Reader.Data.InitializeAsync();
            int[] countsPerPage = [45, 40, 60, 50, 50];
            var pages = countsPerPage
                .Select((count, index) => new PdfPageText(
                    index + 1,
                    string.Join(" ", Enumerable.Range(0, count).Select(hit => $"p{index + 1}-#{hit}-needle"))))
                .ToArray();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(ReaderPage).GetField("_bookFile", flags)!.SetValue(reader, file);
            typeof(ReaderPage).GetField("_isPdf", flags)!.SetValue(reader, true);
            typeof(ReaderPage).GetField("_pdfPage", flags)!.SetValue(reader, 3);
            typeof(ReaderPage).GetField("_pdfPageCount", flags)!.SetValue(reader, pages.Length);
            typeof(ReaderPage).GetField("_pdfText", flags)!.SetValue(reader, pages);

            var collected = new List<NativeReaderSearchItem>();
            TaskCompletionSource<NativeReaderSearchBatch>? waitFinal = null;
            const string searchId = "resume-pdf";
            void OnBatch(NativeReaderSearchBatch batch)
            {
                if (batch.SearchId != searchId) return;
                if (!batch.IsIncremental) collected.Clear();
                collected.AddRange(batch.Items);
                if (batch.IsFinal) waitFinal?.TrySetResult(batch);
            }

            reader.NativeSearchBatchChanged += OnBatch;
            try
            {
                waitFinal = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                reader.NativeStartSearch("needle", searchId);
                var firstBatch = await waitFinal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(firstBatch.Error);
                Assert.Equal(200, collected.Count);
                Assert.True(firstBatch.HasMore);

                waitFinal = new TaskCompletionSource<NativeReaderSearchBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                reader.NativeContinueSearch(searchId);
                var secondBatch = await waitFinal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(secondBatch.Error);
                Assert.Equal(245, collected.Count);
                Assert.False(secondBatch.HasMore);
                Assert.Equal(5, secondBatch.ScannedChapters);
                Assert.Equal(245, collected.Select(item => (item.ChapterIndex, item.Offset)).Distinct().Count());
            }
            finally
            {
                reader.NativeSearchBatchChanged -= OnBatch;
            }
        }
        finally
        {
            reader.NativeCancelSearch();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var restore = await MobileDataCoordinator.BeginRestoreAsync(timeout.Token);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }, CancellationToken.None);

    [Fact]
    public Task DirectTxtBookFileOpensWithAutomaticChapters() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-direct-txt-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        await modules.Library.Books.InitializeAsync();
        await modules.Reader.Data.InitializeAsync();

        var txtRelative = Path.Combine("library", "direct-novel.txt");
        var txtPath = Path.Combine(paths.Data, txtRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(txtPath)!);
        await File.WriteAllTextAsync(txtPath, "第一章 开端\n直接打开阅读。\n第二章 后续\n目录也自动生成。");
        var book = new Book { Id = Guid.NewGuid(), Title = "直接阅读 TXT" };
        var file = new BookFile
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Format = "txt",
            RelativePath = txtRelative,
            Size = new FileInfo(txtPath).Length,
            Sha256 = await Hashing.Sha256Async(txtPath)
        };
        book.Files.Add(file);

        var reader = new ReaderPage(book, modules, shell);
        var states = new List<NativeReaderState>();
        reader.NativeStateChanged += states.Add;
        reader.UseNativeFrontend();
        var window = new Window { Width = 390, Height = 844, Content = reader };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!states.Any(state => state.Ready || state.Error is not null))
                await Task.Delay(15, timeout.Token);

            var readyState = reader.GetNativeState();
            Assert.Null(readyState.Error);
            Assert.True(readyState.Ready);
            var toc = reader.GetNativeToc();
            Assert.Equal(["第一章 开端", "第二章 后续"], toc.Select(item => item.Title).ToArray());
        }
        finally
        {
            window.Close();
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
