using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

public partial class MainWindow
{
    private ReaderOpenTransitionState? _readerOpenTransition;

    /// <summary>
    /// Lift the actual shelf cover, retaining its hover crop and reading scrim.
    /// A frozen shelf keeps the background continuous even when opening the
    /// document hides LibraryRoot before the first reading page is ready.
    /// </summary>
    private void StartReaderOpenTransition(BookCardViewModel card, BookFile? requestedFile)
    {
        if (_readerOpenTransition is not null || ReaderRoot.IsVisible || !LibraryRoot.IsVisible
            || _libraryViewMode != LibraryViewMode.Grid || !BookGrid.IsVisible)
            return;

        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files, _appSettings.PreferredOpenFormat);
        if (file is null || !IsInternalReaderFormat(file.Format)) return;

        // The overlay is a sibling of WindowShadowHost, not a child of
        // RootShellGrid. Use its own parent for both coordinates and capture
        // so window-frame insets never shift the first cover frame.
        var root = ReaderOpenTransitionLayer.GetVisualParent() as Control;
        var index = ViewModel.Books.IndexOf(card);
        if (root is null || index < 0 || BookGrid.ContainerFromIndex(index) is not Control container)
            return;

        var cover = container.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Classes.Contains("bookCover"));
        var image = cover?.GetVisualDescendants().OfType<Image>().FirstOrDefault();
        if (cover is null || image?.Source is null || cover.Bounds.Width < 1 || cover.Bounds.Height < 1
            || root.Bounds.Width < 1 || root.Bounds.Height < 1
            || cover.TranslatePoint(default, root) is not { } origin
            || cover.TranslatePoint(new Point(cover.Bounds.Width, cover.Bounds.Height), root) is not { } end)
            return;
        var size = new Size(end.X - origin.X, end.Y - origin.Y);
        if (size.Width < 1 || size.Height < 1) return;

        var state = new ReaderOpenTransitionState(
            cover, new ReaderOpenTransitionMotion(new Rect(origin, size), root.Bounds.Size));
        _readerOpenTransition = state;

        try
        {
            // Layout the clone once. Every subsequent frame changes only render
            // transforms and opacity, never the shelf or the cover's dimensions.
            Canvas.SetLeft(ReaderOpenTransitionCover, origin.X);
            Canvas.SetTop(ReaderOpenTransitionCover, origin.Y);
            ReaderOpenTransitionCover.Width = size.Width;
            ReaderOpenTransitionCover.Height = size.Height;
            ReaderOpenTransitionCover.RenderTransform = state.Transform;
            ReaderOpenTransitionCover.Opacity = 1;
            ReaderOpenTransitionCoverFrame.Background = cover.Background;
            ReaderOpenTransitionCoverFrame.BorderBrush = cover.BorderBrush;
            ReaderOpenTransitionCoverFrame.BorderThickness = cover.BorderThickness;
            // The grid can recycle the original card while a slow open is in
            // flight. Own this small bitmap independently of the shelf cache.
            if (image.Source is Bitmap sourceBitmap)
                state.CoverBitmap = sourceBitmap.CreateScaledBitmap(sourceBitmap.PixelSize, BitmapInterpolationMode.HighQuality);
            ReaderOpenTransitionImage.Source = state.CoverBitmap ?? image.Source;
            ReaderOpenTransitionImage.Stretch = image.Stretch;
            ReaderOpenTransitionImage.RenderTransformOrigin = image.RenderTransformOrigin;
            ReaderOpenTransitionImage.RenderTransform = new MatrixTransform(image.RenderTransform?.Value ?? Matrix.Identity);
            ReaderOpenTransitionReadAction.IsVisible = cover.GetVisualDescendants().OfType<Button>()
                .Any(button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);
            ReaderOpenTransitionReadAction.Opacity = 1;

            // Photograph the shelf without this cover so the moving clone never
            // leaves a second copy behind. Restore the exact source opacity on
            // success, failure and window shutdown.
            cover.Opacity = 0;
            state.ShelfSnapshot = CaptureReaderOpenShelf(root);
            ReaderOpenTransitionShelf.Source = state.ShelfSnapshot;
            ReaderOpenTransitionShelf.IsVisible = state.ShelfSnapshot is not null;
            ReaderOpenTransitionBackdrop.Opacity = state.ShelfSnapshot is null ? 1 : 0;

            var target = state.Motion.Target;
            var labelWidth = Math.Min(root.Bounds.Width, target.Width + 100);
            ReaderOpenTransitionLoadingText.Width = labelWidth;
            Canvas.SetLeft(ReaderOpenTransitionLoadingText, (root.Bounds.Width - labelWidth) / 2);
            Canvas.SetTop(ReaderOpenTransitionLoadingText, target.Bottom + 22);
            ReaderOpenTransitionLoadingText.Text = T("正在加载…");
            ReaderOpenTransitionLoadingText.Opacity = 0;
            ReaderOpenTransitionLayer.Opacity = 1;
            ReaderOpenTransitionLayer.IsVisible = true;

            state.Timer.Tick += (_, _) =>
            {
                if (!ReferenceEquals(_readerOpenTransition, state))
                {
                    state.Timer.Stop();
                    return;
                }

                try
                {
                    if (_lifetimeCancellation.IsCancellationRequested)
                    {
                        ResetReaderOpenTransition();
                        return;
                    }
                    DrawReaderOpenTransitionFrame(state);
                }
                catch
                {
                    // A cosmetic failure must never strand the reader behind
                    // an opaque overlay or keep the book-open guard occupied.
                    ResetReaderOpenTransition();
                }
            };
            state.Clock.Start();
            state.Timer.Start();
        }
        catch
        {
            ResetReaderOpenTransition();
        }
    }

    private static bool IsInternalReaderFormat(string format) =>
        format.Equals("epub", StringComparison.OrdinalIgnoreCase)
        || format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
        || format.Equals("mobi", StringComparison.OrdinalIgnoreCase)
        || format.Equals("azw3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Crossfade to the restored reading page as soon as it is ready. The reveal
    /// overlaps the end of the lift instead of waiting for a second animation.
    /// Slow loads hold a still cover, with no additional entrance delay.
    /// </summary>
    private Task CompleteReaderOpenTransitionAsync()
    {
        var state = _readerOpenTransition;
        if (state is null) return Task.CompletedTask;
        if (!ReaderRoot.IsVisible || _lifetimeCancellation.IsCancellationRequested)
        {
            ResetReaderOpenTransition();
            return Task.CompletedTask;
        }

        state.RevealAtMs ??= Math.Max(
            state.Clock.Elapsed.TotalMilliseconds, ReaderOpenTransitionMotion.MinimumCoverVisibleMs);
        ReaderOpenTransitionLoadingText.Opacity = 0;
        return state.Completion.Task;
    }

    private void DrawReaderOpenTransitionFrame(ReaderOpenTransitionState state)
    {
        var frame = state.Motion.Sample(state.Clock.Elapsed.TotalMilliseconds, state.RevealAtMs);
        state.Scale.ScaleX = frame.Scale;
        state.Scale.ScaleY = frame.Scale;
        state.Translation.X = frame.OffsetX;
        state.Translation.Y = frame.OffsetY;
        ReaderOpenTransitionReadAction.Opacity = frame.ReadActionOpacity;
        ReaderOpenTransitionBackdrop.Opacity = state.ShelfSnapshot is null ? 1 : frame.BackdropOpacity;
        // Child opacity is inherited during the final crossfade. Remove the
        // outgoing shelf once the paper is opaque so it cannot ghost back
        // through the now-translucent paper when the reading page is revealed.
        ReaderOpenTransitionShelf.IsVisible = state.ShelfSnapshot is not null && frame.BackdropOpacity < 1;
        ReaderOpenTransitionLoadingText.Opacity = frame.LoadingOpacity;
        ReaderOpenTransitionLayer.Opacity = frame.LayerOpacity;

        if (frame.IsComplete) ResetReaderOpenTransition();
    }

    private RenderTargetBitmap? CaptureReaderOpenShelf(Control root)
    {
        RenderTargetBitmap? bitmap = null;
        try
        {
            var size = root.Bounds.Size;
            var scaling = Math.Min(2, RenderScaling);
            bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(root);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            // Snapshot failure degrades to a quiet paper-backed entrance.
            return null;
        }
    }

    private void ResetReaderOpenTransition()
    {
        var state = _readerOpenTransition;
        _readerOpenTransition = null;
        state?.Timer.Stop();
        ReaderOpenTransitionLayer.IsVisible = false;
        ReaderOpenTransitionLayer.Opacity = 1;
        ReaderOpenTransitionShelf.Source = null;
        ReaderOpenTransitionShelf.IsVisible = false;
        ReaderOpenTransitionBackdrop.Opacity = 0;
        ReaderOpenTransitionCover.RenderTransform = null;
        ReaderOpenTransitionCover.Width = double.NaN;
        ReaderOpenTransitionCover.Height = double.NaN;
        ReaderOpenTransitionImage.Source = null;
        ReaderOpenTransitionImage.RenderTransform = null;
        ReaderOpenTransitionReadAction.Opacity = 1;
        ReaderOpenTransitionReadAction.IsVisible = false;
        ReaderOpenTransitionLoadingText.Opacity = 0;
        ReaderOpenTransitionLoadingText.Text = string.Empty;
        if (state is null) return;

        state.SourceCover.Opacity = state.SourceOpacity;
        state.ShelfSnapshot?.Dispose();
        state.CoverBitmap?.Dispose();
        state.Completion.TrySetResult();
    }

    private sealed class ReaderOpenTransitionState
    {
        public ReaderOpenTransitionState(Border sourceCover, ReaderOpenTransitionMotion motion)
        {
            SourceCover = sourceCover;
            SourceOpacity = sourceCover.Opacity;
            Motion = motion;
            Transform.Children.Add(Scale);
            Transform.Children.Add(Translation);
        }

        public Border SourceCover { get; }
        public double SourceOpacity { get; }
        public ReaderOpenTransitionMotion Motion { get; }
        public ScaleTransform Scale { get; } = new(1, 1);
        public TranslateTransform Translation { get; } = new();
        public TransformGroup Transform { get; } = new();
        public Stopwatch Clock { get; } = new();
        public DispatcherTimer Timer { get; } = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RenderTargetBitmap? ShelfSnapshot { get; set; }
        public Bitmap? CoverBitmap { get; set; }
        public double? RevealAtMs { get; set; }
    }
}

/// <summary>Pure motion sampling keeps timing and aspect-ratio guarantees testable.</summary>
internal sealed class ReaderOpenTransitionMotion
{
    internal const double EntranceDurationMs = 280;
    internal const double MinimumCoverVisibleMs = 220;
    internal const double RevealDurationMs = 160;
    internal const double LoadingDelayMs = 500;
    private const double PressDurationMs = 60;
    private const double PressScale = 0.985;
    private static readonly SplineEasing TravelEasing = new() { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 };
    private readonly Rect _source;

    internal ReaderOpenTransitionMotion(Rect source, Size viewport)
    {
        _source = source;
        // The cover is a link to the page, not a full-screen splash image.
        // A bounded enlargement also keeps small shelf thumbnails crisp.
        var scale = Math.Max(1, Math.Min(1.7,
            Math.Min(Math.Min(viewport.Width * 0.28, 300) / source.Width,
                viewport.Height * 0.52 / source.Height)));
        var size = source.Size * scale;
        Target = new Rect(new Point((viewport.Width - size.Width) / 2, (viewport.Height - size.Height) / 2), size);
    }

    internal Rect Target { get; }

    internal ReaderOpenTransitionFrame Sample(double elapsedMs, double? revealAtMs = null)
    {
        var travel = TravelEasing.Ease(Progress(elapsedMs - 40, EntranceDurationMs - 40));
        var scale = elapsedMs < PressDurationMs
            ? 1 - (1 - PressScale) * EaseOut(Progress(elapsedMs, PressDurationMs))
            : PressScale + (Target.Width / _source.Width - PressScale)
                * TravelEasing.Ease(Progress(elapsedMs - PressDurationMs, EntranceDurationMs - PressDurationMs));
        var center = _source.Center + (Target.Center - _source.Center) * travel;
        var reveal = revealAtMs.HasValue ? Progress(elapsedMs - revealAtMs.Value, RevealDurationMs) : 0;
        return new ReaderOpenTransitionFrame(
            scale,
            center.X - _source.Width * scale / 2 - _source.X,
            center.Y - _source.Height * scale / 2 - _source.Y - 8 * Math.Sin(Math.PI * travel),
            EaseOut(Progress(elapsedMs, 200)),
            1 - EaseOut(Progress(elapsedMs, 100)),
            revealAtMs.HasValue ? 0 : EaseOut(Progress(elapsedMs - LoadingDelayMs, 120)),
            1 - reveal * reveal * (3 - 2 * reveal),
            reveal >= 1);
    }

    private static double Progress(double elapsed, double duration) => Math.Clamp(elapsed / duration, 0, 1);
    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);
}

internal readonly record struct ReaderOpenTransitionFrame(
    double Scale, double OffsetX, double OffsetY, double BackdropOpacity,
    double ReadActionOpacity, double LoadingOpacity, double LayerOpacity, bool IsComplete);
