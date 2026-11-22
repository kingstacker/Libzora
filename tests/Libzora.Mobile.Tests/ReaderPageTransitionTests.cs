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
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class ReaderPageTransitionTests(MobileUiSession session)
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
