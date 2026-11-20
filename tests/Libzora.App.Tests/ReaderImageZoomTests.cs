using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using SkiaSharp;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderImageZoomTests(SettingsUiSession session)
{
    [Fact]
    public Task DoubleClickOnBodyImageOpensZoomOverlayZoomsAndClosesWithEscape() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var bookRoot = Path.Combine(scope.Paths.Data, "reader-image-zoom-book");
        Directory.CreateDirectory(bookRoot);
        var imagePath = Path.Combine(bookRoot, "figure.png");
        using (var bitmap = new SKBitmap(320, 480))
        {
            bitmap.Erase(SKColors.SteelBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(imagePath, data.ToArray());
        }
        var chapterPath = Path.Combine(bookRoot, "chapter.xhtml");
        await File.WriteAllTextAsync(chapterPath,
            "<html><body><p>Adjacent caption words near figure</p><p><img src='figure.png' width='320' height='480'/></p></body></html>");

        scope.Set("_readerDocument", new EpubReaderDocument(bookRoot, [chapterPath], [], []));
        scope.Set("_readerIsPdf", false);
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
        scope.Call("ApplyReaderPanelLayout");
        await (Task)scope.Call("EnsureReaderHostsAsync")!;
        await Render();

        var host = Assert.IsType<NativeReaderHost>(scope.Field<IReaderHost?>("_readerActiveHost"));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
        host.Navigate(new Uri(chapterPath));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Render();

        var layout = (ChapterLayout)typeof(NativeReaderHost)
            .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var figure = Assert.Single(layout.Pages[0].Images);
        var target = host.TranslatePoint(new Point(figure.Rect.MidX, figure.Rect.MidY), scope.Window)!.Value;

        var overlay = scope.Get<Control>("ReaderImageZoomOverlay");
        Assert.False(overlay.IsVisible);

        // Two rapid left clicks inside the figure must open the lightbox
        // instead of turning the page or selecting adjacent text.
        scope.Window.MouseDown(target, MouseButton.Left);
        scope.Window.MouseUp(target, MouseButton.Left);
        scope.Window.MouseDown(target, MouseButton.Left);
        scope.Window.MouseUp(target, MouseButton.Left);
        await Render();

        Assert.True(overlay.IsVisible);
        Assert.NotNull(scope.Get<Image>("ReaderImageZoomImage").Source);
        Assert.Equal("100%", scope.Get<TextBlock>("ReaderImageZoomLabel").Text);
        Assert.Null(scope.Field<string?>("_readerPendingSelection"));
        Assert.Equal(0, (int)typeof(NativeReaderHost)
            .GetField("_pageIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!);

        scope.Get<Button>("ReaderImageZoomInButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal("125%", scope.Get<TextBlock>("ReaderImageZoomLabel").Text);

        scope.Get<Button>("ReaderImageZoomResetButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Render();
        Assert.Equal("100%", scope.Get<TextBlock>("ReaderImageZoomLabel").Text);

        var zoomedImage = scope.Get<Image>("ReaderImageZoomImage");
        var imageCenter = zoomedImage.TranslatePoint(
            new Point(zoomedImage.Bounds.Width / 2, zoomedImage.Bounds.Height / 2), scope.Window)!.Value;
        scope.Window.MouseDown(imageCenter, MouseButton.Left);
        scope.Window.MouseUp(imageCenter, MouseButton.Left);
        scope.Window.MouseDown(imageCenter, MouseButton.Left);
        scope.Window.MouseUp(imageCenter, MouseButton.Left);
        await Render();
        Assert.Equal("200%", scope.Get<TextBlock>("ReaderImageZoomLabel").Text);

        scope.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Render();
        Assert.False(overlay.IsVisible);
        Assert.Null(scope.Get<Image>("ReaderImageZoomImage").Source);
    });

    [Fact]
    public Task ImageZoomBridgeRejectsPathsOutsideActiveBookRoot() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var bookRoot = Path.Combine(scope.Paths.Data, "reader-book-root");
        var outsideRoot = Path.Combine(scope.Paths.Data, "outside-root");
        Directory.CreateDirectory(bookRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsideImage = Path.Combine(outsideRoot, "secret.png");
        using (var bitmap = new SKBitmap(32, 32))
        {
            bitmap.Erase(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(outsideImage, data.ToArray());
        }

        scope.Set("_readerDocument", new EpubReaderDocument(bookRoot, [], [], []));
        scope.Set("_readerIsPdf", false);
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;

        var escaped = outsideImage.Replace("\\", "\\\\").Replace("\"", "\\\"");
        scope.Call("HandleReaderBridgeMessage", $"{{\"type\":\"imageZoom\",\"path\":\"{escaped}\"}}");
        await Render();

        Assert.False(scope.Get<Control>("ReaderImageZoomOverlay").IsVisible);
    });

    private Task Run(Func<Task> action) => session.Session.Dispatch(action, CancellationToken.None);

    private static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
