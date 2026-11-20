using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.Mobile.Views;
using SkiaSharp;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class ReaderMediaGestureTests(MobileUiSession session)
{
    [Theory]
    [InlineData(0, 100, false, false, false)] // A vertical drag ends inside the image.
    [InlineData(-80, 0, false, false, false)] // A page swipe also ends inside the image.
    [InlineData(0, 80, true, false, false)] // Returning to the start must not revive a tap.
    [InlineData(6, 4, false, true, false)] // Normal finger jitter still counts as a tap.
    [InlineData(0, 0, false, true, false)]
    [InlineData(0, 100, false, false, true)] // Dragging after a long press is not an image tap.
    [InlineData(0, 80, true, false, true)]
    public Task OnlyDoubleTapsOpenTheImage(double dx, double dy, bool returnToStart, bool tapEligible, bool longPress) =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-image-gesture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            var reader = new ReaderPage(new Book { Id = Guid.NewGuid(), Title = "图片触摸回归" }, modules, shell);
            reader.UseNativeFrontend();
            // Load a real chapter directly, without importing a book or starting
            // the shell's unrelated account/sync and progress machinery.
            typeof(ReaderPage).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reader, true);
            using var host = new NativeReaderHost { Focusable = false, ReaderObjectsEnabled = true };
            var handler = typeof(ReaderPage).GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            reader.FindControl<ContentControl>("ReaderHostSlot")!.Content = host;
            var media = new List<NativeReaderMedia>();
            reader.NativeReaderMediaRequested += media.Add;
            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var imagePath = Path.Combine(root, "figure.png");
                using (var bitmap = new SKBitmap(260, 420))
                {
                    bitmap.Erase(SKColors.LightBlue);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(imagePath, data.ToArray());
                }
                var chapterPath = Path.Combine(root, "chapter.xhtml");
                await File.WriteAllTextAsync(chapterPath,
                    "<html><body><p><img src='figure.png' width='260' height='420'/></p></body></html>");
                window.Show();
                await Render();
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(chapterPath));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Render();
                var layout = (ChapterLayout)typeof(NativeReaderHost)
                    .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var figure = Assert.Single(layout.Pages[0].Images);
                var start = host.TranslatePoint(new Point(figure.Rect.MidX, figure.Rect.MidY), window)!.Value;
                var end = start + new Vector(dx, dy);
                // Swipes can dismiss visible chrome without navigating away
                // from the figure, so its release remains a valid image tap.
                reader.SetChromeVisible(true);
                window.MouseDown(start, MouseButton.Left);
                if (longPress)
                {
                    typeof(ReaderPage).GetMethod("OnLongPress", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(reader, [null, EventArgs.Empty]);
                }
                window.MouseMove(end);
                if (returnToStart)
                {
                    window.MouseMove(start);
                    end = start;
                }
                window.MouseUp(end, MouseButton.Left);
                await Render();
                // Single tap never opens the image viewer on its own.
                Assert.Empty(media);

                if (tapEligible)
                {
                    // A second tap within the double-tap window opens the image.
                    window.MouseDown(end, MouseButton.Left);
                    window.MouseUp(end, MouseButton.Left);
                    await Render();
                    Assert.Single(media);
                    Assert.Equal("image", media[0].Kind);
                    Assert.Equal(imagePath, media[0].Path);
                }
                else
                {
                    // Cancelling a drag must release the host's capture and
                    // leave a subsequent double-tap usable (while single tap still stays quiet).
                    Assert.False((bool)typeof(NativeReaderHost)
                        .GetField("_pointerDown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!);
                    window.MouseDown(start, MouseButton.Left);
                    window.MouseUp(start, MouseButton.Left);
                    await Render();
                    Assert.Empty(media);
                    window.MouseDown(start, MouseButton.Left);
                    window.MouseUp(start, MouseButton.Left);
                    await Render();
                    Assert.Single(media);
                }
            }
            finally
            {
                host.WebMessageReceived -= handler;
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

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
