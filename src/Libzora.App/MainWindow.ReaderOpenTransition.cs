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
    private ReaderBookTransitionState? _readerOpenTransition;

    /// <summary>
    /// Lifts the shelf cover proportionally toward the center while the frozen
    /// shelf dissolves to paper, then reveals the ready reader surface cleanly.
    /// </summary>
    private void StartReaderOpenTransition(BookCardViewModel card, BookFile? requestedFile)
    {
        if (_readerOpenTransition is not null || ReaderRoot.IsVisible)
            return;

        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files, _appSettings.PreferredOpenFormat);
        if (file is null || !IsInternalReaderFormat(file.Format)) return;

        var root = ReaderOpenTransitionLayer.GetVisualParent() as Control;
        if (root is null || root.Bounds.Width < 1 || root.Bounds.Height < 1)
            return;

        var target = ResolveShelfCoverTarget(card, root);
        var sourceRect = target?.Bounds ?? ReaderOpenTransitionMotion.CenteredSourceRect(root.Bounds.Size);
        var isCentered = target is null;
        var restingCorner = target?.Cover.CornerRadius.TopLeft ?? 6;
        var motion = new ReaderOpenTransitionMotion(sourceRect, root.Bounds.Size, isCentered, restingCorner);

        var state = new ReaderBookTransitionState(
            isClosing: false,
            card: card,
            sourceCover: target?.Cover,
            motion: motion);
        _readerOpenTransition = state;

        try
        {
            ConfigureTransitionCoverVisuals(
                state,
                card,
                target?.Cover,
                target?.FrontImage,
                target?.ShowReadAction == true,
                sourceRect);

            if (target?.Cover is { } cover)
                cover.Opacity = 0;

            state.ShelfSnapshot = CaptureControlSnapshot(WindowShadowHost);
            ReaderOpenTransitionShelf.Source = state.ShelfSnapshot;
            ReaderOpenTransitionShelf.IsVisible = state.ShelfSnapshot is not null;
            ReaderOpenTransitionBackdrop.Opacity = state.ShelfSnapshot is null ? 1 : 0;
            ReaderOpenTransitionPage.IsVisible = false;
            ReaderOpenTransitionPageImage.Source = null;

            var labelWidth = Math.Min(root.Bounds.Width, motion.Target.Width + 100);
            ReaderOpenTransitionLoadingText.Width = labelWidth;
            Canvas.SetLeft(ReaderOpenTransitionLoadingText, (root.Bounds.Width - labelWidth) / 2);
            Canvas.SetTop(ReaderOpenTransitionLoadingText, motion.Target.Bottom + 22);
            ReaderOpenTransitionLoadingText.Text = T("正在加载…");
            ReaderOpenTransitionLoadingText.Opacity = 0;

            DrawBookTransitionFrame(state, motion.SampleOpen(0));
            ReaderOpenTransitionLayer.Opacity = 1;
            ReaderOpenTransitionLayer.IsVisible = true;

            StartBookTransitionLoop(state);
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
    /// Crossfades to the restored reading page once ready without an extra delay.
    /// </summary>
    private Task CompleteReaderOpenTransitionAsync()
    {
        var state = _readerOpenTransition;
        if (state is null || state.IsClosing) return Task.CompletedTask;
        if (!ReaderRoot.IsVisible || _lifetimeCancellation.IsCancellationRequested)
        {
            ResetReaderOpenTransition();
            return Task.CompletedTask;
        }

        state.RevealAtMs ??= Math.Max(
            state.Clock.Elapsed.TotalMilliseconds,
            ReaderOpenTransitionMotion.MinimumCoverVisibleMs);
        ReaderOpenTransitionLoadingText.Opacity = 0;
        return state.Completion.Task;
    }

    /// <summary>
    /// Photographs the live reader page before cleanup hides ReaderRoot so the
    /// closing transition can dissolve into paper and glide the cover back to
    /// the shelf.
    /// </summary>
    private ReaderBookTransitionState? PrepareReaderCloseTransition(BookCardViewModel? card)
    {
        ResetReaderOpenTransition();
        if (!ReaderRoot.IsVisible || _lifetimeCancellation.IsCancellationRequested)
            return null;

        var root = ReaderOpenTransitionLayer.GetVisualParent() as Control;
        if (root is null || root.Bounds.Width < 1 || root.Bounds.Height < 1)
            return null;

        WindowShadowHost.UpdateLayout();
        var pageSnapshot = CaptureControlSnapshot(WindowShadowHost);
        if (pageSnapshot is null)
            return null;

        var viewport = root.Bounds.Size;
        var fallbackRect = ReaderOpenTransitionMotion.CenteredSourceRect(viewport);
        var motion = new ReaderOpenTransitionMotion(fallbackRect, viewport, isCentered: true, restingCornerRadius: 6);
        var state = new ReaderBookTransitionState(
            isClosing: true,
            card: card,
            sourceCover: null,
            motion: motion)
        {
            PageSnapshot = pageSnapshot
        };
        _readerOpenTransition = state;

        try
        {
            ConfigureTransitionCoverVisuals(
                state,
                card,
                sourceCover: null,
                sourceImage: null,
                showReadAction: false,
                fallbackRect);

            ReaderOpenTransitionShelf.Source = null;
            ReaderOpenTransitionShelf.IsVisible = false;
            ReaderOpenTransitionPageImage.Source = pageSnapshot;
            ReaderOpenTransitionPage.IsVisible = true;
            ReaderOpenTransitionPage.RenderTransform = state.PageTransform;
            ReaderOpenTransitionLoadingText.Opacity = 0;

            DrawBookTransitionFrame(state, motion.SampleClose(0));
            ReaderOpenTransitionLayer.Opacity = 1;
            ReaderOpenTransitionLayer.IsVisible = true;
            return state;
        }
        catch
        {
            ResetReaderOpenTransition();
            return null;
        }
    }

    /// <summary>
    /// Dissolves the reader page into paper and glides the proportional cover
    /// back onto its resting shelf slot (or gently dissolves when returning to
    /// list/other views).
    /// </summary>
    private Task PlayReaderCloseTransitionAsync(ReaderBookTransitionState state)
    {
        if (!ReferenceEquals(_readerOpenTransition, state) || !state.IsClosing)
            return Task.CompletedTask;

        var root = ReaderOpenTransitionLayer.GetVisualParent() as Control;
        if (root is null || root.Bounds.Width < 1 || root.Bounds.Height < 1
            || !LibraryRoot.IsVisible || _lifetimeCancellation.IsCancellationRequested)
        {
            ResetReaderOpenTransition();
            return Task.CompletedTask;
        }

        root.UpdateLayout();
        var target = state.Card is not null ? ResolveShelfCoverTarget(state.Card, root) : null;
        var targetRect = target?.Bounds ?? ReaderOpenTransitionMotion.CenteredSourceRect(root.Bounds.Size);
        var isCentered = target is null;
        var restingCorner = target?.Cover.CornerRadius.TopLeft ?? 6;

        state.Motion = new ReaderOpenTransitionMotion(targetRect, root.Bounds.Size, isCentered, restingCorner);
        Canvas.SetLeft(ReaderOpenTransitionCover, targetRect.X);
        Canvas.SetTop(ReaderOpenTransitionCover, targetRect.Y);
        ReaderOpenTransitionCover.Width = targetRect.Width;
        ReaderOpenTransitionCover.Height = targetRect.Height;
        ReaderOpenTransitionCoverFrame.CornerRadius = new CornerRadius(restingCorner);
        ReaderOpenTransitionCover.CornerRadius = new CornerRadius(restingCorner);

        if (target?.Cover is { } cover)
        {
            state.BindSourceCover(cover);
            ReaderOpenTransitionCoverFrame.Background = cover.Background;
            ReaderOpenTransitionCoverFrame.BorderBrush = cover.BorderBrush;
            ReaderOpenTransitionCoverFrame.BorderThickness = cover.BorderThickness;
            cover.Opacity = 0;
        }

        DrawBookTransitionFrame(state, state.Motion.SampleClose(0));
        StartBookTransitionLoop(state);
        return state.Completion.Task;
    }

    private void StartBookTransitionLoop(ReaderBookTransitionState state)
    {
        var anchor = ReaderOpenTransitionLayer.GetVisualParent() as Visual ?? ReaderOpenTransitionLayer;
        var topLevel = TopLevel.GetTopLevel(anchor);
        var lastRafMs = -100d;
        const double FallbackIntervalMs = 16;

        void Step(bool fromRaf)
        {
            if (!ReferenceEquals(_readerOpenTransition, state))
            {
                state.Timer.Stop();
                return;
            }
            if (fromRaf)
                lastRafMs = state.Clock.Elapsed.TotalMilliseconds;

            try
            {
                if (_lifetimeCancellation.IsCancellationRequested)
                {
                    ResetReaderOpenTransition();
                    return;
                }

                var elapsed = state.Clock.Elapsed.TotalMilliseconds;
                var frame = state.IsClosing
                    ? state.Motion.SampleClose(elapsed)
                    : state.Motion.SampleOpen(elapsed, state.RevealAtMs);
                DrawBookTransitionFrame(state, frame);
                if (frame.IsComplete)
                {
                    ResetReaderOpenTransition();
                    return;
                }

                if (fromRaf && topLevel is not null)
                    topLevel.RequestAnimationFrame(_ => Step(fromRaf: true));
            }
            catch
            {
                ResetReaderOpenTransition();
            }
        }

        state.Timer.Interval = TimeSpan.FromMilliseconds(FallbackIntervalMs);
        state.Timer.Tick += (_, _) =>
        {
            if (state.Clock.Elapsed.TotalMilliseconds - lastRafMs < FallbackIntervalMs * 1.5)
                return;
            Step(fromRaf: false);
        };

        state.Clock.Start();
        if (topLevel is not null)
            topLevel.RequestAnimationFrame(_ => Step(fromRaf: true));
        state.Timer.Start();
    }

    private void ConfigureTransitionCoverVisuals(
        ReaderBookTransitionState state,
        BookCardViewModel? card,
        Border? sourceCover,
        Image? sourceImage,
        bool showReadAction,
        Rect coverRect)
    {
        Canvas.SetLeft(ReaderOpenTransitionCover, coverRect.X);
        Canvas.SetTop(ReaderOpenTransitionCover, coverRect.Y);
        ReaderOpenTransitionCover.Width = coverRect.Width;
        ReaderOpenTransitionCover.Height = coverRect.Height;
        ReaderOpenTransitionCover.RenderTransform = state.CoverTransform;
        ReaderOpenTransitionCover.Opacity = 1;

        var restingCorner = new CornerRadius(sourceCover?.CornerRadius.TopLeft ?? (sourceCover is null ? 6 : 0));
        ReaderOpenTransitionCover.CornerRadius = restingCorner;
        ReaderOpenTransitionCoverFrame.CornerRadius = restingCorner;
        ReaderOpenTransitionCoverFrame.Background = sourceCover?.Background
            ?? AppAppearanceResources.GetBrush("PressedBrush");
        ReaderOpenTransitionCoverFrame.BorderBrush = sourceCover?.BorderBrush
            ?? AppAppearanceResources.GetBrush("HairlineBrush");
        ReaderOpenTransitionCoverFrame.BorderThickness = sourceCover?.BorderThickness
            ?? new Thickness(1);

        var sourceBitmap = sourceImage?.Source as Bitmap ?? card?.CoverImage;
        if (sourceBitmap is not null)
            state.CoverBitmap = sourceBitmap.CreateScaledBitmap(sourceBitmap.PixelSize, BitmapInterpolationMode.HighQuality);

        var hasCover = state.CoverBitmap is not null || sourceImage?.Source is not null;
        ReaderOpenTransitionImage.Source = state.CoverBitmap ?? sourceImage?.Source;
        ReaderOpenTransitionImage.IsVisible = hasCover;
        ReaderOpenTransitionImage.Stretch = sourceImage?.Stretch ?? Stretch.UniformToFill;
        ReaderOpenTransitionImage.RenderTransformOrigin = sourceImage?.RenderTransformOrigin
            ?? new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        ReaderOpenTransitionImage.RenderTransform = new MatrixTransform(sourceImage?.RenderTransform?.Value ?? Matrix.Identity);

        ReaderOpenTransitionFallbackCover.IsVisible = !hasCover;
        var title = card?.Title?.Trim() ?? string.Empty;
        ReaderOpenTransitionFallbackInitial.Text = title.Length > 0 ? title[..1] : "BOOK";
        ReaderOpenTransitionFallbackInitial.FontSize = Math.Max(18, coverRect.Width * 0.24);

        ReaderOpenTransitionReadAction.IsVisible = showReadAction;
        ReaderOpenTransitionReadAction.Opacity = showReadAction ? 1 : 0;
    }

    private void DrawBookTransitionFrame(ReaderBookTransitionState state, ReaderOpenTransitionFrame frame)
    {
        state.CoverScale.ScaleX = frame.Scale;
        state.CoverScale.ScaleY = frame.Scale;
        state.CoverTranslation.X = frame.OffsetX;
        state.CoverTranslation.Y = frame.OffsetY;
        ReaderOpenTransitionCover.Opacity = frame.CoverOpacity;

        ReaderOpenTransitionCover.BoxShadow = frame.ShadowAlpha > 0
            ? new BoxShadows(new BoxShadow
            {
                Blur = frame.ShadowBlur,
                OffsetY = frame.ShadowOffsetY,
                Color = Color.FromArgb(frame.ShadowAlpha, 0, 0, 0)
            })
            : default;

        ReaderOpenTransitionReadAction.Opacity = frame.ReadActionOpacity;
        ReaderOpenTransitionBackdrop.Opacity = !state.IsClosing && state.ShelfSnapshot is null
            ? 1
            : frame.BackdropOpacity;
        // Hide the frozen shelf once paper becomes opaque so it cannot ghost
        // through during the final reveal crossfade.
        ReaderOpenTransitionShelf.IsVisible = !state.IsClosing
            && state.ShelfSnapshot is not null
            && frame.BackdropOpacity < 1;

        if (state.IsClosing)
        {
            ReaderOpenTransitionPage.Opacity = frame.PageOpacity;
            state.PageScale.ScaleX = frame.PageScale;
            state.PageScale.ScaleY = frame.PageScale;
        }

        ReaderOpenTransitionLoadingText.Opacity = state.IsClosing ? 0 : frame.LoadingOpacity;
        ReaderOpenTransitionLayer.Opacity = frame.LayerOpacity;
    }

    private ShelfCoverTarget? ResolveShelfCoverTarget(BookCardViewModel card, Control root)
    {
        if (!LibraryRoot.IsVisible)
            return null;

        if (LibraryWorkspace.IsVisible && _libraryViewMode == LibraryViewMode.Grid && BookGrid.IsVisible)
        {
            var index = FindShelfBookIndex(card);
            if (index >= 0 && BookGrid.ContainerFromIndex(index) is Control container)
            {
                var cover = container.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Classes.Contains("bookCover"));
                if (TryResolveVisibleCoverTarget(cover, root, card.CoverImage, out var gridTarget))
                    return gridTarget;
            }
        }

        if (LibraryDetailPane.IsVisible && ReferenceEquals(_selectedCard, card)
            && DetailCoverImage.Source is not null
            && TryResolveVisibleCoverTarget(DetailCoverFrame, root, card.CoverImage, out var detailTarget))
        {
            return detailTarget;
        }

        return null;
    }

    private int FindShelfBookIndex(BookCardViewModel card)
    {
        var shelf = ViewModel.ShelfBooks;
        for (var i = 0; i < shelf.Count; i++)
        {
            if (ReferenceEquals(shelf[i], card) || shelf[i].Book.Id == card.Book.Id)
                return i;
        }
        for (var i = 0; i < shelf.Count; i++)
        {
            if (shelf[i].SeriesMembers.Any(member => member.Book.Id == card.Book.Id))
                return i;
        }
        return -1;
    }

    private static bool TryResolveVisibleCoverTarget(
        Border? cover,
        Control root,
        Bitmap? preferredCoverBitmap,
        out ShelfCoverTarget target)
    {
        target = default!;
        if (cover is null || !cover.IsEffectivelyVisible
            || cover.Bounds.Width < 12 || cover.Bounds.Height < 16
            || root.Bounds.Width < 1 || root.Bounds.Height < 1
            || cover.TranslatePoint(default, root) is not { } origin
            || cover.TranslatePoint(new Point(cover.Bounds.Width, cover.Bounds.Height), root) is not { } end)
            return false;

        var rect = new Rect(origin, new Size(end.X - origin.X, end.Y - origin.Y));
        if (rect.Width < 12 || rect.Height < 16)
            return false;

        var viewport = new Rect(0, 0, root.Bounds.Width, root.Bounds.Height);
        var visible = rect.Intersect(viewport);
        if (visible.Width <= 0 || visible.Height <= 0)
            return false;
        if ((visible.Width * visible.Height) / (rect.Width * rect.Height) < 0.45)
            return false;

        var images = cover.GetVisualDescendants().OfType<Image>().ToList();
        var frontImage = images.FirstOrDefault(img => img.Classes.Contains("seriesFront"))
            ?? (preferredCoverBitmap is not null
                ? images.LastOrDefault(img => ReferenceEquals(img.Source, preferredCoverBitmap))
                : null)
            ?? images.LastOrDefault(img => img.IsEffectivelyVisible && img.Source is not null)
            ?? images.LastOrDefault();

        var showReadAction = cover.GetVisualDescendants().OfType<Button>()
            .Any(button => button.Classes.Contains("bookCoverRead") && button.IsEffectivelyVisible);

        target = new ShelfCoverTarget(cover, frontImage, rect, showReadAction);
        return true;
    }

    private RenderTargetBitmap? CaptureControlSnapshot(Control control)
    {
        RenderTargetBitmap? bitmap = null;
        try
        {
            var size = control.Bounds.Size;
            if (size.Width < 1 || size.Height < 1) return null;
            var scaling = Math.Min(2, RenderScaling);
            bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)),
                new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(control);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
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

        ReaderOpenTransitionPage.IsVisible = false;
        ReaderOpenTransitionPage.Opacity = 1;
        ReaderOpenTransitionPage.RenderTransform = null;
        ReaderOpenTransitionPageImage.Source = null;

        ReaderOpenTransitionCover.RenderTransform = null;
        ReaderOpenTransitionCover.Width = double.NaN;
        ReaderOpenTransitionCover.Height = double.NaN;
        ReaderOpenTransitionCover.Opacity = 1;
        ReaderOpenTransitionCover.BoxShadow = default;
        ReaderOpenTransitionFallbackCover.IsVisible = false;
        ReaderOpenTransitionFallbackInitial.Text = string.Empty;
        ReaderOpenTransitionImage.Source = null;
        ReaderOpenTransitionImage.RenderTransform = null;
        ReaderOpenTransitionImage.IsVisible = true;
        ReaderOpenTransitionReadAction.Opacity = 1;
        ReaderOpenTransitionReadAction.IsVisible = false;

        ReaderOpenTransitionLoadingText.Opacity = 0;
        ReaderOpenTransitionLoadingText.Text = string.Empty;

        if (state is null) return;

        if (state.SourceCover is not null)
            state.SourceCover.Opacity = state.SourceOpacity;
        state.ShelfSnapshot?.Dispose();
        state.PageSnapshot?.Dispose();
        state.CoverBitmap?.Dispose();
        state.Completion.TrySetResult();
    }

    private readonly record struct ShelfCoverTarget(
        Border Cover,
        Image? FrontImage,
        Rect Bounds,
        bool ShowReadAction);

    private sealed class ReaderBookTransitionState
    {
        public ReaderBookTransitionState(
            bool isClosing,
            BookCardViewModel? card,
            Border? sourceCover,
            ReaderOpenTransitionMotion motion)
        {
            IsClosing = isClosing;
            Card = card;
            Motion = motion;
            CoverTransform.Children.Add(CoverScale);
            CoverTransform.Children.Add(CoverTranslation);
            if (sourceCover is not null)
                BindSourceCover(sourceCover);
        }

        public bool IsClosing { get; }
        public BookCardViewModel? Card { get; }
        public Border? SourceCover { get; private set; }
        public double SourceOpacity { get; private set; } = 1;
        public ReaderOpenTransitionMotion Motion { get; set; }
        public ScaleTransform CoverScale { get; } = new(1, 1);
        public TranslateTransform CoverTranslation { get; } = new();
        public TransformGroup CoverTransform { get; } = new();
        public ScaleTransform PageScale { get; } = new(1, 1);
        public TransformGroup PageTransform => new() { Children = { PageScale } };
        public Stopwatch Clock { get; } = new();
        public DispatcherTimer Timer { get; } = new(DispatcherPriority.Render);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RenderTargetBitmap? ShelfSnapshot { get; set; }
        public RenderTargetBitmap? PageSnapshot { get; set; }
        public Bitmap? CoverBitmap { get; set; }
        public double? RevealAtMs { get; set; }

        public void BindSourceCover(Border cover)
        {
            SourceCover = cover;
            SourceOpacity = cover.Opacity;
        }
    }
}

/// <summary>
/// Pure motion sampling for the macOS Apple Books-style proportional cover
/// lift + paper crossfade open/close transition.
/// </summary>
internal sealed class ReaderOpenTransitionMotion
{
    internal const double EntranceDurationMs = 280;
    internal const double MinimumCoverVisibleMs = 220;
    internal const double RevealDurationMs = 180;
    internal const double CloseCoverDurationMs = 340;
    internal const double CloseCenterDurationMs = 220;
    internal const double LoadingDelayMs = 500;
    internal const double CenteredAspectRatio = 1.42;
    private const double PressDurationMs = 60;
    private const double PressScale = 0.985;
    private static readonly SplineEasing TravelEasing = new() { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 };

    private readonly Rect _source;
    private readonly bool _isCentered;

    internal ReaderOpenTransitionMotion(
        Rect source,
        Size viewport,
        bool isCentered = false,
        double restingCornerRadius = 0)
    {
        _source = source;
        _isCentered = isCentered;
        var scale = Math.Max(1, Math.Min(1.7,
            Math.Min(Math.Min(viewport.Width * 0.28, 300) / Math.Max(1, source.Width),
                viewport.Height * 0.52 / Math.Max(1, source.Height))));
        var size = source.Size * scale;
        Target = new Rect(
            new Point((viewport.Width - size.Width) * 0.5, (viewport.Height - size.Height) * 0.5),
            size);
        _ = restingCornerRadius;
    }

    internal Rect Target { get; }

    internal static Rect CenteredSourceRect(Size viewport)
    {
        var safeW = Math.Max(120, double.IsFinite(viewport.Width) ? viewport.Width : 1000);
        var safeH = Math.Max(200, double.IsFinite(viewport.Height) ? viewport.Height : 700);
        var maxW = Math.Min(safeW * 0.20, 200);
        var maxH = safeH * 0.36;
        var width = Math.Max(96, Math.Min(maxW, maxH / CenteredAspectRatio));
        var height = width * CenteredAspectRatio;
        return new Rect((safeW - width) * 0.5, (safeH - height) * 0.5, width, height);
    }

    internal ReaderOpenTransitionFrame SampleOpen(double elapsedMs, double? revealAtMs = null)
    {
        elapsedMs = Math.Max(0, elapsedMs);
        var travel = TravelEasing.Ease(Progress(elapsedMs - 40, EntranceDurationMs - 40));
        var baseScale = elapsedMs < PressDurationMs
            ? 1 - (1 - PressScale) * EaseOut(Progress(elapsedMs, PressDurationMs))
            : PressScale + (Target.Width / _source.Width - PressScale)
                * TravelEasing.Ease(Progress(elapsedMs - PressDurationMs, EntranceDurationMs - PressDurationMs));

        var reveal = revealAtMs.HasValue ? Progress(elapsedMs - revealAtMs.Value, RevealDurationMs) : 0;
        var revealSmooth = reveal * reveal * (3 - 2 * reveal);
        var scale = baseScale * (1 + 0.03 * revealSmooth);
        var center = _source.Center + (Target.Center - _source.Center) * travel;
        var centeredReveal = _isCentered ? EaseOut(Progress(elapsedMs, 140)) : 1;
        var coverOpacity = centeredReveal * (1 - revealSmooth);
        var arc = Math.Sin(Math.PI * travel);
        var shadowAlpha = (byte)Math.Clamp(Math.Round((0.11 + 0.14 * arc) * (1 - revealSmooth) * 255), 0, 255);

        return new ReaderOpenTransitionFrame(
            scale,
            center.X - _source.Width * scale * 0.5 - _source.X,
            center.Y - _source.Height * scale * 0.5 - _source.Y - 8 * arc,
            coverOpacity,
            EaseOut(Progress(elapsedMs, 200)),
            1 - EaseOut(Progress(elapsedMs, 100)),
            revealAtMs.HasValue ? 0 : EaseOut(Progress(elapsedMs - LoadingDelayMs, 120)),
            1 - revealSmooth,
            PageOpacity: 0,
            PageScale: 1,
            shadowAlpha,
            10 + 18 * arc,
            4 + 8 * arc,
            reveal >= 1);
    }

    internal ReaderOpenTransitionFrame SampleClose(double elapsedMs)
    {
        elapsedMs = Math.Max(0, elapsedMs);
        if (_isCentered)
        {
            var raw = Progress(elapsedMs, CloseCenterDurationMs);
            var smooth = raw * raw * (3 - 2 * raw);
            return new ReaderOpenTransitionFrame(
                1,
                0,
                0,
                CoverOpacity: 0,
                BackdropOpacity: 0,
                ReadActionOpacity: 0,
                LoadingOpacity: 0,
                LayerOpacity: 1,
                PageOpacity: 1 - smooth,
                PageScale: 1 - 0.02 * smooth,
                ShadowAlpha: 0,
                ShadowBlur: 0,
                ShadowOffsetY: 0,
                IsComplete: raw >= 1);
        }

        var pageFade = EaseOut(Progress(elapsedMs, 140));
        var glideRaw = Progress(elapsedMs - 55, CloseCoverDurationMs - 55);
        var glide = TravelEasing.Ease(glideRaw);
        var backdropFade = EaseOut(Progress(elapsedMs - 110, CloseCoverDurationMs - 110));

        var targetScale = Target.Width / _source.Width;
        var settleScale = targetScale * (1 + 0.03 * (1 - pageFade));
        var scale = settleScale + (1 - settleScale) * glide;
        var center = Target.Center + (_source.Center - Target.Center) * glide;
        var arc = Math.Sin(Math.PI * glide);
        var coverOpacity = Math.Min(1, pageFade * 1.25);
        var shadowAlpha = (byte)Math.Clamp(Math.Round((0.11 * (1 - glide) + 0.14 * arc) * coverOpacity * 255), 0, 255);

        return new ReaderOpenTransitionFrame(
            scale,
            center.X - _source.Width * scale * 0.5 - _source.X,
            center.Y - _source.Height * scale * 0.5 - _source.Y - 8 * arc,
            coverOpacity,
            BackdropOpacity: 1 - backdropFade,
            ReadActionOpacity: 0,
            LoadingOpacity: 0,
            LayerOpacity: 1,
            PageOpacity: 1 - pageFade,
            PageScale: 1 - 0.02 * pageFade,
            shadowAlpha,
            10 + 18 * arc,
            4 + 8 * arc,
            IsComplete: glideRaw >= 1);
    }

    private static double Progress(double elapsed, double duration) => duration <= 0 ? 1 : Math.Clamp(elapsed / duration, 0, 1);
    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 3);
}

internal readonly record struct ReaderOpenTransitionFrame(
    double Scale,
    double OffsetX,
    double OffsetY,
    double CoverOpacity,
    double BackdropOpacity,
    double ReadActionOpacity,
    double LoadingOpacity,
    double LayerOpacity,
    double PageOpacity,
    double PageScale,
    byte ShadowAlpha,
    double ShadowBlur,
    double ShadowOffsetY,
    bool IsComplete);
