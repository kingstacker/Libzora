using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Libzora.Core;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
[Trait("Category", "Slow")]
public sealed class ReaderPresentationFrameTests(SettingsUiSession session)
{
    [Fact]
    public Task EmptyMountedSurfaceDoesNotReportAReadyFrame() => Run(async () =>
    {
        using var host = new NativeReaderHost();
        var frames = new List<long>();
        host.PresentationFrameRendered += frames.Add;
        var window = new Window { Width = 390, Height = 844, Content = host };
        window.Show();
        try
        {
            host.RequestPresentationFrame(1);
            await ReaderTests.Render();
            Paint(host);
            await ReaderTests.Render();
            Assert.Empty(frames);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CurrentEpubPageReportsOnlyTheLatestRequestedDraw() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        var frames = new List<long>();
        host.PresentationFrameRendered += frames.Add;
        host.RequestPresentationFrame(1);
        host.RequestPresentationFrame(2);
        Assert.Empty(frames); // Ready/navigation alone does not send the draw event.
        Paint(host);
        await ReaderTests.Render();
        Assert.Equal(new long[] { 2 }, frames);
        Paint(host);
        await ReaderTests.Render();
        Assert.Single(frames); // Repainting chrome/background must not replay entry.
        host.RequestPresentationFrame(3);
        host.Dispose();
        await ReaderTests.Render();
        Assert.Single(frames); // A late callback cannot resurrect a disposed session.
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ValidBlankAndImagePagesCanFinishTheEntry(bool imageOnly) => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        var path = Path.Combine(scope.Paths.ReaderCache, "presentation-page.xhtml");
        if (imageOnly)
        {
            await File.WriteAllBytesAsync(Path.Combine(scope.Paths.ReaderCache, "pixel.png"),
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aG1sAAAAASUVORK5CYII="));
        }
        await File.WriteAllTextAsync(path, imageOnly
            ? "<html><body><img src='pixel.png' width='200' height='400'/></body></html>"
            : "<html><body><p><br/></p></body></html>");
        Assert.True(await scope.Call<Task<bool>>("NavigateReaderHostAndWaitAsync", host, new Uri(path), CancellationToken.None, false));
        var frames = new List<long>();
        host.PresentationFrameRendered += frames.Add;
        host.RequestPresentationFrame(1);
        Paint(host);
        await ReaderTests.Render();
        Assert.Equal(new long[] { 1 }, frames);
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Fact]
    public Task PartialLayoutBackgroundCompletionDoesNotBlockInitialPresentationFrame() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        using var host = await ReaderAppearanceTests.LoadChapter(scope, new());
        var path = Path.Combine(scope.Paths.ReaderCache, "presentation-partial.xhtml");
        var body = string.Join("", Enumerable.Range(0, 600).Select(index =>
            $"<p>Paragraph {index}: quick first-screen presentation must not wait for the entire chapter.</p>"));
        await File.WriteAllTextAsync(path, $"<html><body>{body}</body></html>");
        var navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigated(object? _, ReaderNavigationCompletedEventArgs e)
        {
            host.NavigationCompleted -= OnNavigated;
            navigated.TrySetResult(e.IsSuccess);
        }
        host.NavigationCompleted += OnNavigated;
        host.Navigate(new Uri(path), new(), false, composeTextOffset: 0, allowPartialCompose: true);
        Assert.True(await navigated.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(host.IsLayoutComplete);

        var frames = new List<long>();
        host.PresentationFrameRendered += frames.Add;
        host.RequestPresentationFrame(1);
        host.CompleteLayoutInBackground();
        Paint(host);
        await ReaderTests.Render();
        Assert.Equal(new long[] { 1 }, frames);
        scope.Set("_readerActiveHost", null);
        scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
    });

    [Fact]
    public Task PdfFrameNeedsATargetPageBitmapAndIgnoresSupersededRequests() => Run(async () =>
    {
        await using var scope = await ReaderTestWindow.Create();
        var path = PdfFixture.Write(scope.Paths.ReaderCache);
        using var host = new NativePdfReaderHost();
        var frames = new List<long>();
        host.PresentationFrameRendered += frames.Add;
        var window = new Window { Width = 390, Height = 844, Content = host };
        window.Show();
        try
        {
            host.RequestPresentationFrame(1);
            await ReaderTests.Render();
            Assert.Empty(frames);
            await host.PrepareDocumentAsync(path, CancellationToken.None);
            Assert.True(await host.NavigateAsync(new Uri(path), CancellationToken.None));
            frames.Clear();
            host.RequestPresentationFrame(2);
            host.RequestPresentationFrame(3);
            Paint(host);
            await ReaderTests.Render();
            Assert.Equal(new long[] { 3 }, frames);
        }
        finally { window.Close(); }
    });

    private static void Paint(Control host)
    {
        using var image = new RenderTargetBitmap(new PixelSize(
            Math.Max(1, (int)host.Bounds.Width), Math.Max(1, (int)host.Bounds.Height)));
        image.Render(host);
    }

    private Task Run(Func<Task> action) => session.Session.Dispatch(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);
}
