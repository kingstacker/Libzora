using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using SkiaSharp;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed partial class ReaderPageTransitionTests(MobileUiSession session)
{
    [Fact]
    public void PageAnimationEnumUsesNewMobileDefaults()
    {
        Assert.Equal(0, (int)MobilePageAnimation.None);
        Assert.Equal(1, (int)MobilePageAnimation.Fade);
        Assert.Equal(3, (int)MobilePageAnimation.Push);
        Assert.Equal(MobilePageAnimation.Push, MobilePreferences.NormalizePageAnimation((MobilePageAnimation)2));
        Assert.Equal(MobilePageAnimation.Push, new MobilePreferences().ReaderPageAnimation);
    }

    [Fact]
    public Task SurfaceInsetsPadHostWhileKeepingFullScreenPaperAndInteractiveDragWorks() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-transition-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with { ReaderPageAnimation = MobilePageAnimation.Push });

            var book = new Book { Id = Guid.NewGuid(), Title = "翻页动画测试" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('a', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            reader.NativeSetSurfaceInsets(new Thickness(20, 44, 20, 34));
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);

            using var host = new NativeReaderHost { Focusable = false };
            var handler = typeof(ReaderPage)
                .GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            SetField(reader, "_readerHost", host);
            var hostSlot = reader.FindControl<ContentControl>("ReaderHostSlot")!;
            var paperGrid = reader.FindControl<Grid>("ReaderPaper")!;
            hostSlot.Content = host;

            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var chapterPath = Path.Combine(root, "chapter.xhtml");
                var paragraphs = string.Concat(Enumerable.Range(1, 80).Select(i =>
                    $"<p>第{i}段测试正文，用于生成足够多的分页以便验证全屏插槽与跟手滑动翻页。The quick brown fox jumps over the lazy dog.</p>"));
                await File.WriteAllTextAsync(chapterPath, $"<html><body>{paragraphs}</body></html>");
                SetField(reader, "_epubDocument", new EpubReaderDocument(root, [chapterPath], [], ["第一章"]));

                window.Show();
                await Render();

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(chapterPath));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.EnsureCompleteLayoutAsync();
                await Render();

                Assert.True(host.PageCount >= 3);
                Assert.Equal(0, host.CurrentPage);
                Assert.Equal(390, paperGrid.Bounds.Width, 1);
                Assert.Equal(844, paperGrid.Bounds.Height, 1);
                Assert.Equal(350, host.Bounds.Width, 1);
                Assert.Equal(844 - 44 - 34, host.Bounds.Height, 1);
                var snapshot = await reader.NativeCapturePageAsync(CancellationToken.None);
                Assert.True(snapshot.HasValue);
                var captured = snapshot.GetValueOrDefault();
                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, captured.Image.Take(8));
                Assert.Equal(20, captured.Frame.X, 1);
                Assert.Equal(44, captured.Frame.Y, 1);
                Assert.Equal(host.Bounds.Size, captured.Frame.Size);
                using var pixels = SKBitmap.Decode(captured.Image);
                Assert.Contains(pixels.Pixels, pixel => pixel.Red < 128 && pixel.Green < 128 && pixel.Blue < 128);
                Assert.Equal(0, host.CurrentPage);

                // Short slow drag (< 25% width) snaps back to page 0.
                window.MouseDown(new Point(320, 400), MouseButton.Left);
                await Task.Delay(30);
                window.MouseMove(new Point(280, 400));
                await Task.Delay(250);
                window.MouseUp(new Point(280, 400), MouseButton.Left);
                await WaitUntil(() => hostSlot.RenderTransform is null && !reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);
                Assert.Equal(0, host.CurrentPage);

                // Long drag (> 25% width) commits to page 1.
                window.MouseDown(new Point(340, 400), MouseButton.Left);
                await Task.Delay(20);
                window.MouseMove(new Point(160, 400));
                await Render();
                Assert.True(reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);
                window.MouseUp(new Point(160, 400), MouseButton.Left);
                await WaitUntil(() => hostSlot.RenderTransform is null && !reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);
                Assert.Equal(1, host.CurrentPage);
            }
            finally
            {
                host.WebMessageReceived -= handler;
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Render();
            await Task.Delay(15);
        }
        Assert.True(condition());
    }

    [Fact]
    public Task PortraitSupportsScrollModeAndLandscapeSwitchesToTwoColumns() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-orientation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with { ReaderFlowMode = 0, ReaderTwoPageInLandscape = true });

            var book = new Book { Id = Guid.NewGuid(), Title = "竖屏滚动横屏双栏测试" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('b', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);

            using var host = new NativeReaderHost { Focusable = false };
            var handler = typeof(ReaderPage)
                .GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            SetField(reader, "_readerHost", host);
            reader.FindControl<ContentControl>("ReaderHostSlot")!.Content = host;

            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var chapterPath = Path.Combine(root, "chapter.xhtml");
                var paragraphs = string.Concat(Enumerable.Range(1, 80).Select(i =>
                    $"<p>第{i}段测试正文，用于验证竖屏滚动模式与横屏双栏模式之间的自动切换与触摸滑动。</p>"));
                await File.WriteAllTextAsync(chapterPath, $"<html><body>{paragraphs}</body></html>");
                SetField(reader, "_epubDocument", new EpubReaderDocument(root, [chapterPath], [], ["第一章"]));

                window.Show();
                await Render();

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(chapterPath));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.EnsureCompleteLayoutAsync();
                await Render();

                // Portrait applies ReaderFlowMode = 0 (Scroll mode).
                typeof(ReaderPage).GetMethod("OnReaderSurfaceSizeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(reader, [null, null!]);
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 0
                    && !GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);

                // Vertical touch drag scrolls down in portrait scroll mode.
                window.MouseDown(new Point(200, 600), MouseButton.Left);
                await Task.Delay(20);
                window.MouseMove(new Point(200, 380));
                await Render();
                window.MouseUp(new Point(200, 380), MouseButton.Left);
                await WaitUntil(() => host.GetScrollState().Position > 50);

                // Rotate window to landscape -> auto switches to two-column spread.
                window.Width = 844;
                window.Height = 390;
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 1
                    && GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);
                await host.EnsureCompleteLayoutAsync();
                Assert.True(host.PageCount >= 2);

                // Rotate back to portrait -> returns to portrait scroll mode.
                window.Width = 390;
                window.Height = 844;
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 0
                    && !GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);

                // Native iPad viewport hint drives two-column <-> single-column transitions
                // when the AI sidebar narrows the reading viewport without changing window bounds.
                window.Width = 1194;
                window.Height = 834;
                reader.NativeSetSurfaceInsets(new Thickness(0, 0, 0, 0), viewportWidth: 1194, viewportHeight: 834);
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 1
                    && GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);

                reader.NativeSetSurfaceInsets(new Thickness(0, 0, 420, 0), viewportWidth: 774, viewportHeight: 834);
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 0
                    && !GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);

                reader.NativeSetSurfaceInsets(new Thickness(0, 0, 0, 0), viewportWidth: 1194, viewportHeight: 834);
                await WaitUntil(() => GetField<ReaderLayoutSettings>(reader, "_layout").FlowMode == 1
                    && GetField<ReaderLayoutSettings>(reader, "_layout").TwoPageMode);
            }
            finally
            {
                host.WebMessageReceived -= handler;
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    [Fact]
    public Task PowerSavingModeForcesOledPureBlackEvenFromLightPaperAndRestoresOnOff() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-powersaving-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with
            {
                ReaderThemeFollowsAppearance = false,
                ReaderTheme = ReaderTheme.Classic,
                ReaderPowerSavingMode = MobileReaderPowerSavingMode.Off
            });

            var book = new Book { Id = Guid.NewGuid(), Title = "省电模式纯黑测试" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('c', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);

            using var host = new NativeReaderHost { Focusable = false };
            SetField(reader, "_readerHost", host);
            reader.FindControl<ContentControl>("ReaderHostSlot")!.Content = host;

            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                window.Show();
                await Render();

                typeof(ReaderPage).GetMethod("RefreshReaderAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(reader, null);
                var surface = reader.FindControl<ContentControl>("Surface")!;
                Assert.False(reader.IsPowerSavingActive);
                Assert.Equal(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)surface.Background!).Color);
                Assert.False(host.PowerSavingMode);

                // Enable power-saving mode: switches light Classic paper directly to OLED pure black.
                shell.Preferences.Update(p => p with { ReaderPowerSavingMode = MobileReaderPowerSavingMode.On });
                await reader.NativeApplySettingsAsync();
                Assert.True(reader.IsPowerSavingActive);
                Assert.True(host.PowerSavingMode);
                Assert.Equal(Avalonia.Media.Colors.Black, ((Avalonia.Media.ISolidColorBrush)surface.Background!).Color);
                Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, reader.FindControl<ThemeVariantScope>("ThemeScope")!.RequestedThemeVariant);

                // Turn off power-saving mode: restores Classic white paper.
                shell.Preferences.Update(p => p with { ReaderPowerSavingMode = MobileReaderPowerSavingMode.Off });
                await reader.NativeApplySettingsAsync();
                Assert.False(reader.IsPowerSavingActive);
                Assert.False(host.PowerSavingMode);
                Assert.Equal(Avalonia.Media.Colors.White, ((Avalonia.Media.ISolidColorBrush)surface.Background!).Color);
            }
            finally
            {
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    [Fact]
    public Task HostPointerCaptureLostAndPauseCancelInFlightSwipeTransition() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-capture-lost-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with { ReaderPageAnimation = MobilePageAnimation.Push });

            var book = new Book { Id = Guid.NewGuid(), Title = "手势中断测试" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('b', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);

            using var host = new NativeReaderHost { Focusable = false };
            var handler = typeof(ReaderPage)
                .GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            SetField(reader, "_readerHost", host);
            var hostSlot = reader.FindControl<ContentControl>("ReaderHostSlot")!;
            hostSlot.Content = host;

            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var chapterPath = Path.Combine(root, "chapter.xhtml");
                var paragraphs = string.Concat(Enumerable.Range(1, 80).Select(i =>
                    $"<p>第{i}段测试正文，用于生成多页以验证多任务切换手势取消。</p>"));
                await File.WriteAllTextAsync(chapterPath, $"<html><body>{paragraphs}</body></html>");
                SetField(reader, "_epubDocument", new EpubReaderDocument(root, [chapterPath], [], ["第一章"]));

                window.Show();
                await Render();

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(chapterPath));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.EnsureCompleteLayoutAsync();
                await Render();

                Assert.True(host.PageCount >= 3);
                Assert.Equal(0, host.CurrentPage);

                // Start an interactive horizontal drag; NativeReaderHost captures the pointer.
                window.MouseDown(new Point(340, 400), MouseButton.Left);
                await Task.Delay(20);
                window.MouseMove(new Point(160, 400));
                await Render();
                Assert.True(reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);

                // Simulate OS multitasking gesture cancelling the touch on the capturing child host.
                var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Touch, isPrimary: true);
                host.RaiseEvent(new PointerCaptureLostEventArgs(host, pointer));
                await Render();

                Assert.False(reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);
                Assert.Null(GetField<object?>(reader, "_interactiveDrag"));
                Assert.Null(GetField<Point?>(reader, "_pressPoint"));
                Assert.Equal(0, host.CurrentPage);

                // Pause/Resume must also clear any remaining gesture state cleanly.
                window.MouseDown(new Point(340, 400), MouseButton.Left);
                await Task.Delay(20);
                window.MouseMove(new Point(160, 400));
                await Render();
                await reader.PauseAsync();
                reader.Resume();
                await Render();

                Assert.False(reader.FindControl<Image>("TransitionSnapshot")!.IsVisible);
                Assert.Null(GetField<object?>(reader, "_interactiveDrag"));
                Assert.False(GetField<bool>(reader, "_suppressScrollUpdatesDuringDrag"));
                Assert.Equal(0, host.CurrentPage);

                // Normal center tap must still reveal chrome even when the host
                // releases pointer capture inside OnPointerReleased.
                Assert.False(reader.IsChromeVisible);
                window.MouseDown(new Point(195, 400), MouseButton.Left);
                await Task.Delay(20);
                window.MouseUp(new Point(195, 400), MouseButton.Left);
                await Render();
                Assert.True(reader.IsChromeVisible);
            }
            finally
            {
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    [Fact]
    public Task ContinuousScrollCrossesChapterWithoutBlankingPreviewOrStoppingInertia() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-continuous-seam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            shell.Preferences.Update(p => p with { ReaderFlowMode = 0, ReaderVerticalWriting = false });

            var book = new Book { Id = Guid.NewGuid(), Title = "正文连续滑动跨章测试" };
            var file = new BookFile { Id = Guid.NewGuid(), BookId = book.Id, Format = "epub", Sha256 = new string('c', 64) };
            var reader = new ReaderPage(book, modules, shell);
            reader.UseNativeFrontend();
            SetField(reader, "_started", true);
            SetField(reader, "_ready", true);
            SetField(reader, "_bookFile", file);

            using var host = new NativeReaderHost { Focusable = false };
            var handler = typeof(ReaderPage)
                .GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            SetField(reader, "_readerHost", host);
            var hostSlot = reader.FindControl<ContentControl>("ReaderHostSlot")!;
            hostSlot.Content = host;

            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var ch1 = Path.Combine(root, "ch1.xhtml");
                var ch2 = Path.Combine(root, "ch2.xhtml");
                var ch3 = Path.Combine(root, "ch3.xhtml");
                string MakeChapter(string heading, int count) =>
                    $"<html><body><h1>{heading}</h1>{string.Concat(Enumerable.Range(1, count).Select(i => $"<p>{heading} 第{i}段测试正文，用于验证正文连续滑动跨章节不闪烁且惯性不中断。</p>"))}</body></html>";
                await File.WriteAllTextAsync(ch1, MakeChapter("第一章", 28));
                await File.WriteAllTextAsync(ch2, MakeChapter("第二章", 60));
                await File.WriteAllTextAsync(ch3, MakeChapter("第三章", 40));
                SetField(reader, "_epubDocument", new EpubReaderDocument(root, [ch1, ch2, ch3], [], ["第一章", "第二章", "第三章"]));

                var scrollLayout = new ReaderLayoutSettings { FlowMode = 0, VerticalWriting = false };
                SetField(reader, "_layout", scrollLayout);

                window.Show();
                await Render();

                host.SetScrollNextChapterPath(ch2);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(ch1), scrollLayout, showVerticalDebugBoxes: false);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await host.EnsureCompleteLayoutAsync();
                await Render();

                // Next chapter preview must already be composed and survive updating the
                // next-chapter path before the incoming chapter finishes loading.
                var initialPreview = GetField<object?>(host, "_scrollNextChapterLayout");
                Assert.NotNull(initialPreview);
                var originalChapterLayout = GetField<object>(host, "_layout");
                var promotedPreviewWithoutRecompose = false;
                host.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess && args.Request == new Uri(ch2))
                        promotedPreviewWithoutRecompose = ReferenceEquals(initialPreview, GetField<object>(host, "_layout"));
                };
                host.SetScrollNextChapterPath(ch3);
                Assert.Same(initialPreview, GetField<object?>(host, "_scrollNextChapterLayout"));
                host.SetScrollNextChapterPath(ch2);

                var nextStartY = (double)typeof(NativeReaderHost)
                    .GetProperty("ContinuousNextChapterStartY", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(host)!;
                Assert.True(nextStartY > 100);

                // Position right before the chapter 1 -> chapter 2 seam and start inertia coasting across it.
                host.SeekToPixelScroll(Math.Max(0, nextStartY - 40));
                await Render();

                SetField(reader, "_dragVelocityY", -1800d);
                typeof(ReaderPage)
                    .GetMethod("StartScrollInertia", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(reader, [host]);

                await WaitUntil(() => GetField<int>(reader, "_chapterIndex") == 1 && !GetField<bool>(reader, "_turning"));
                // Allow inertia ticks to continue coasting inside chapter 2.
                for (var i = 0; i < 12; i++)
                {
                    await Render();
                    await Task.Delay(20);
                }

                Assert.Equal(1, GetField<int>(reader, "_chapterIndex"));
                Assert.True(promotedPreviewWithoutRecompose, "The painted next-chapter preview must be promoted directly, not typeset again at the seam.");
                Assert.NotNull(GetField<object?>(host, "_scrollPreviousChapterPreview"));
                var postHandoffOffset = GetField<double>(host, "_scrollOffset");
                Assert.True(postHandoffOffset > 120, $"Expected smooth coast into chapter 2, but stopped at offset {postHandoffOffset:F1}.");
                Assert.NotNull(GetField<object?>(host, "_scrollNextChapterLayout"));

                // Jumping to an offset-only highlight anchor in continuous scroll mode
                // must place the highlighted line visibly inside the viewport rather
                // than clipping its baseline at y = 0.
                var layout = GetField<Libzora.Layout.ChapterLayout>(host, "_layout");
                var targetPage = layout.Pages[Math.Min(2, layout.Pages.Count - 1)];
                var targetRun = targetPage.Runs.First(run => run.TextStart >= 0 && run.TextLength > 0);
                var highlightJump = new ReaderContentPosition { TextOffset = targetRun.TextStart };
                await host.Configure(scrollLayout, 0, null, restoreFromProgress: true, showVerticalDebugBoxes: false, contentPosition: highlightJump);
                await Render();

                var jumpedScroll = GetField<double>(host, "_scrollOffset");
                var renderedBaselineY = targetPage.Index * targetPage.Height + targetRun.OriginY - jumpedScroll;
                Assert.InRange(renderedBaselineY, targetRun.FontSize * 1.5, targetPage.Height * 0.6);

                typeof(ReaderPage).GetMethod("StopScrollInertia", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reader, null);
                host.SeekToPixelScroll(0);
                // One finger movement crosses the seam, then reverses slightly
                // before the asynchronous promotion commits. Every signed pixel
                // must survive, without a fixed jump or the old 1.5-screen cap.
                host.ScrollByPixel(-12);
                Assert.Equal(-12, GetField<double>(host, "_scrollOffset"), 5);
                host.ScrollByPixel(-1800);
                host.ScrollByPixel(35);
                await WaitUntil(() => GetField<int>(reader, "_chapterIndex") == 0 && !GetField<bool>(reader, "_turning"));
                Assert.Same(originalChapterLayout, GetField<object>(host, "_layout"));
                var previousStart = (double)typeof(NativeReaderHost)
                    .GetProperty("ContinuousNextChapterStartY", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                Assert.Equal(Math.Max(0, previousStart - 1777), GetField<double>(host, "_scrollOffset"), 4);
            }
            finally
            {
                host.WebMessageReceived -= handler;
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
