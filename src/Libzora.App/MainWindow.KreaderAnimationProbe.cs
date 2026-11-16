#if DEBUG
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using SkiaSharp;

namespace Libzora;

public partial class MainWindow
{
    // DEBUG-only diagnostics: opens the validation EPUB in paged mode and
    // drives a real page turn for every page-turn animation, photographing the
    // reader surface (snapshot overlay included) mid-transition so both the
    // turn pipeline and the visual overlays are covered by the same probe.
    private async Task RunKreaderAnimationProbeAndExitAsync()
    {
        var logPath = Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Path.Combine(_paths.Logs, "kreader-animation-probe.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var exitCode = 0;
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        try
        {
            await log.WriteLineAsync($"Kreader animation probe started: {DateTimeOffset.Now:O}");

            var assetRoot = Path.Combine(Path.GetTempPath(), "LibzoraAnimationProbe", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(assetRoot);
            var epubPath = Path.Combine(assetRoot, "animation-probe.epub");
            var probeReaderOpen = Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE_MODE") == "reader-open";
            CreateKreaderValidationEpub(epubPath, includeCover: probeReaderOpen);

            var importResult = await ViewModel.ImportAsync([epubPath], cancellationToken: _lifetimeCancellation.Token);
            if (importResult.FailureCount != 0)
                throw new InvalidOperationException("import failed");
            // Older probe runs can merge the same title into an existing card;
            // resolve the bytes we just imported so the animation frames always
            // come from today's fixture.
            var epubCard = await ResolveImportedValidationBookAsync(
                importResult,
                epubPath,
                titleHint: "Linux Kreader Validation Long Numbers");
            var epubFile = await ResolveImportedValidationFile(epubCard, epubPath, log);
            if (probeReaderOpen)
            {
                await ProbeReaderOpenTransitionAsync(epubCard, log);
                return;
            }
            await OpenBookAsync(epubCard, epubFile);
            if (CurrentReaderHost is not NativeReaderHost nativeHost)
                throw new InvalidOperationException("reader host is not the native surface");
            await nativeHost.ReadyTask;
            await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false);
            await WaitForKreaderNativePageAsync(nativeHost);
            await log.WriteLineAsync("PASS native reader ready");

            foreach (var (name, animation) in new[]
                     {
                         ("fade", ReaderAnimationFade),
                         ("slide", ReaderAnimationSlide),
                         ("wave", ReaderAnimationWave)
                     })
            {
                _readerPageAnimation = animation;
                SyncReaderAnimationMenu();
                await Task.Delay(150);
                await log.WriteLineAsync($"--- {name} ---");

                // Drive the app's real turn pipeline without awaiting it, then
                // photograph the mid-transition frames as they render.
                var turn = TurnReaderPageCoreAsync(1, chapterOnly: false);
                for (var sample = 0; sample < 14; sample++)
                {
                    await Task.Delay(50);
                    await SaveAnimationProbeFrameAsync(name, sample);
                    if (sample == 0)
                    {
                        var hostBounds = nativeHost.Bounds;
                        var layerBounds = ReaderNativeTransitionLayer.Bounds;
                        var snapshotBounds = ReaderNativeTransitionSnapshot.Bounds;
                        var scaling = TopLevel.GetTopLevel(nativeHost)?.RenderScaling ?? 1;
                        var hostOrigin = nativeHost.TranslatePoint(
                            new Point(0, 0),
                            ReaderNativeTransitionLayer);
                        var layerOrigin = ReaderNativeTransitionLayer.TranslatePoint(
                            new Point(0, 0),
                            ReaderWebViewHost);
                        var snapshotOrigin = ReaderNativeTransitionSnapshot.TranslatePoint(
                            new Point(0, 0),
                            ReaderWebViewHost);
                        await log.WriteLineAsync(
                            $"METRICS {name}: host={hostBounds.Width:F2}x{hostBounds.Height:F2} "
                            + $"layer={layerBounds.Width:F2}x{layerBounds.Height:F2} "
                            + $"snapshot={snapshotBounds.Width:F2}x{snapshotBounds.Height:F2} "
                            + $"assigned={ReaderNativeTransitionSnapshot.Width:F2}x{ReaderNativeTransitionSnapshot.Height:F2} "
                            + $"scaling={scaling:F3} stretch={ReaderNativeTransitionSnapshot.Stretch} "
                            + $"origins hostInLayer={FormatPoint(hostOrigin)} "
                            + $"layerInHost={FormatPoint(layerOrigin)} "
                            + $"snapshotInHost={FormatPoint(snapshotOrigin)}");
                        if (ReaderNativeTransitionSnapshot.Source is RenderTargetBitmap captured)
                        {
                            using var snapshotStream = new MemoryStream();
                            captured.Save(snapshotStream, PngBitmapEncoderOptions.Default);
                            var capturedLogPath = Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE_LOG");
                            var directory = string.IsNullOrWhiteSpace(capturedLogPath)
                                ? _paths.Logs
                                : Path.GetDirectoryName(capturedLogPath)!;
                            await File.WriteAllBytesAsync(
                                Path.Combine(directory, $"kreader-animation-snapshot-{name}.png"),
                                snapshotStream.ToArray());
                        }
                    }
                }
                await turn;
                await log.WriteLineAsync($"PASS {name} turn completed");
                await Task.Delay(300);
            }

            await log.WriteLineAsync("probe completed");
        }
        catch (Exception exception)
        {
            exitCode = 2;
            await log.WriteLineAsync("FAIL " + exception);
        }
        finally
        {
            await log.FlushAsync();
            Environment.Exit(exitCode);
        }
    }

    private async Task ProbeReaderOpenTransitionAsync(BookCardViewModel card, TextWriter log)
    {
        await log.WriteLineAsync("DATA " + _paths.Root);
        foreach (var name in new[] { "open-cold", "open-warm" })
        {
            ShowLibraryPage();
            SetLibraryViewMode(LibraryViewMode.Grid, persist: false);
            BookGrid.ScrollIntoView(card);
            card.LoadCover();
            await card.CoverLoadingTask;
            BookGrid.SelectedItem = card;
            card.IsSoleSelected = true;
            await Dispatcher.UIThread.InvokeAsync(() => RootShellGrid.UpdateLayout(), DispatcherPriority.Loaded);
            await Task.Delay(300);
            var item = BookGrid.ContainerFromIndex(ViewModel.Books.IndexOf(card))
                ?? throw new InvalidOperationException("probe cover was not realized");
            var readButton = item.GetVisualDescendants().OfType<Button>()
                .First(button => button.Classes.Contains("bookCoverRead"));
            await SaveAnimationProbeFrameAsync(name + "-shelf", 0, this);

            // Drive the actual shelf button, without injecting system mouse
            // events or changing the user's running application window.
            var clock = Stopwatch.StartNew();
            readButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UpdateLayout();
            if (!ReaderOpenTransitionLayer.IsVisible || ReaderOpenTransitionLayer.Bounds.Width < 16
                || ReaderOpenTransitionCover.Bounds.Width < 16)
                throw new InvalidOperationException("reader-open layer did not start or arrange");
            await log.WriteLineAsync($"PASS {name} started from shelf button");
            for (var sample = 0; sample < 16; sample++)
            {
                await Task.Delay(35);
                await SaveAnimationProbeFrameAsync(name, sample, this);
                var matrix = ReaderOpenTransitionCover.RenderTransform?.Value ?? Matrix.Identity;
                await log.WriteLineAsync(
                    $"FRAME {name} {sample}: elapsed={clock.Elapsed.TotalMilliseconds:F0} "
                    + $"visible={ReaderOpenTransitionLayer.IsVisible} opacity={ReaderOpenTransitionLayer.Opacity:F3} "
                    + $"scale={matrix.M11:F3},{matrix.M22:F3} offset={matrix.M31:F1},{matrix.M32:F1} "
                    + $"backdrop={ReaderOpenTransitionBackdrop.Opacity:F3} loading={ReaderOpenTransitionLoadingText.Opacity:F3}");
                if (_bookOpenInProgress == 0) break;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (_bookOpenInProgress > 0) await Task.Delay(20, timeout.Token);
            if (!ReaderRoot.IsVisible || CurrentReaderHost is not NativeReaderHost nativeHost)
                throw new InvalidOperationException("probe reader did not open");
            await WaitForKreaderNativePageAsync(nativeHost);
            if (ReaderOpenTransitionLayer.IsVisible || ReaderOpenTransitionImage.Source is not null
                || ReaderOpenTransitionShelf.Source is not null)
                throw new InvalidOperationException("reader-open layer was not released");
            await SaveAnimationProbeFrameAsync(name + "-ready", 0, this);
            await log.WriteLineAsync($"PASS {name} completed and released all overlays");
            await CloseReaderAsync();
            await Task.Delay(200);
        }
    }

    private static void AddReaderOpenProbeCover(ZipArchive archive)
    {
        using var bitmap = new SKBitmap(320, 448);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(232, 227, 215));
        using var paint = new SKPaint { Color = new SKColor(52, 60, 53), IsAntialias = true, StrokeWidth = 1 };
        using var typeface = SKTypeface.FromFamilyName("Georgia");
        using var title = new SKFont(typeface, 44);
        using var caption = new SKFont(typeface, 14);
        canvas.DrawText("A QUIET", 28, 126, title, paint);
        canvas.DrawText("MOMENT", 28, 182, title, paint);
        canvas.DrawText("TO READ", 28, 238, title, paint);
        canvas.DrawLine(28, 272, 292, 272, paint);
        canvas.DrawText("LIBZORA", 28, 398, caption, paint);
        using var stream = archive.CreateEntry("EPUB/cover.png").Open();
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
    }

    private static async Task WaitForKreaderNativePageAsync(
        NativeReaderHost host)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (host.CaptureVisiblePageAsync(CancellationToken.None).Result is { Length: > 0 })
                return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("native reader page did not render.");
    }

    private async Task SaveAnimationProbeFrameAsync(string name, int sample, Visual? visual = null)
    {
        try
        {
            visual ??= ReaderWebViewHost as Visual ?? (Visual)this;
            var bounds = visual.Bounds;
            var scaling = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1;
            var pixelWidth = (int)Math.Ceiling(bounds.Width * scaling);
            var pixelHeight = (int)Math.Ceiling(bounds.Height * scaling);
            if (pixelWidth < 16 || pixelHeight < 16) return;

            using var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(visual);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            var logPath = Environment.GetEnvironmentVariable("LIBZORA_ANIMATION_PROBE_LOG");
            var directory = string.IsNullOrWhiteSpace(logPath)
                ? _paths.Logs
                : Path.GetDirectoryName(logPath)!;
            await File.WriteAllBytesAsync(
                Path.Combine(directory, $"kreader-animation-{name}-{sample}.png"),
                stream.ToArray());
        }
        catch
        {
            // Diagnostics must never affect reading or navigation.
        }
    }

    private static string FormatPoint(Point? point) => point is { } value
        ? $"{value.X:F2},{value.Y:F2}"
        : "n/a";
}
#endif
