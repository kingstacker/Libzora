using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Libzora;

/// <summary>Named overlay elements the reader transition player drives.</summary>
/// <param name="SnapshotSource">The live reader surface to photograph.</param>
/// <param name="Snapshot">Image showing the outgoing frame.</param>
/// <param name="Trail">Wave trail, fade-through backdrop veil, or cover dimming veil.</param>
/// <param name="Front">Wave leading band.</param>
/// <param name="Edge">Slide/cover edge shadow.</param>
/// <param name="BackdropSource">Panel providing the paper background behind transparent reader hosts.</param>
/// <param name="IncomingSheet">Optional live control transformed for Push and backward Cover transitions.</param>
internal sealed record ReaderTransitionSurface(
    Visual SnapshotSource,
    Image Snapshot,
    Rectangle Trail,
    Rectangle Front,
    Rectangle Edge,
    Panel BackdropSource,
    Control? IncomingSheet = null);

/// <summary>
/// Native playback of reader page-turn animations: a Retina-scaled
/// RenderTargetBitmap of the outgoing frame is composited with the live
/// incoming frame and driven on VSync (<see cref="TopLevel.RequestAnimationFrame"/>)
/// with a headless-safe timer fallback.
///
/// Snapshot capture failure degrades silently to an instant switch so a
/// cosmetic hiccup can never block navigation.
/// </summary>
internal static class ReaderTransitionPlayer
{
    // Mirrors the private ReaderAnimation* constants in MainWindow.
    internal const int AnimationNone = 0;
    internal const int AnimationFade = 1;
    internal const int AnimationSlide = 2;
    internal const int AnimationWave = 3;
    internal const int AnimationPush = 4;
    internal const int AnimationCover = 5;
    internal const int AnimationCurl = 6;

    private const int FallbackIntervalMs = 16;

    // Smooth cubic-bezier(.25,.1,.25,1) curve: starts immediately without a
    // harsh front-loaded snap and glides into place at a natural reading pace.
    private const double EaseX1 = 0.25;
    private const double EaseY1 = 0.1;
    private const double EaseX2 = 0.25;
    private const double EaseY2 = 1.0;

    private const double FadeOutMs = 190;
    private const double FadeInMs = 210;
    private const double FadeEaseX1 = 0.4;
    private const double FadeEaseY1 = 0.0;
    private const double FadeEaseX2 = 0.6;
    private const double FadeEaseY2 = 1.0;
    private const double SlideDurationMs = 430;
    private const double PushDurationMs = 410;
    private const double CoverDurationMs = 420;
    private const double CurlDurationMs = 460;
    private const double SlideShadowWidthRatio = 0.025;
    private const double SlideShadowMinWidth = 6;
    private const double SlideShadowMaxWidth = 14;
    private const byte LightShadowAlpha = 20;
    private const byte DarkShadowAlpha = 52;
    private const byte LightCurlCastAlpha = 46;
    private const byte DarkCurlCastAlpha = 104;
    private const double LightCoverDimOpacity = 0.055;
    private const double DarkCoverDimOpacity = 0.22;
    private const double WaveSweepMs = 368;
    private const double WaveBandWidthRatio = 0.11;
    private const double WaveLeadBandAlpha = 0.055;

    // Canonical sibling Z order restored by Reset(): content < snapshot <
    // trail/front bands + slide shadow on top.
    private const int ZContent = 0;
    private const int ZSnapshotBehind = 10;
    private const int ZDimBetween = 15;
    private const int ZSnapshot = 20;
    private const int ZIncomingCover = 25;
    private const int ZTrail = 30;
    private const int ZTop = 40;

    private static readonly SolidColorBrush BlackDimBrush = new(Colors.Black);
    private static readonly LinearGradientBrush LightShadowRight = CreateHorizontalShadowGradient(true, LightShadowAlpha);
    private static readonly LinearGradientBrush LightShadowLeft = CreateHorizontalShadowGradient(false, LightShadowAlpha);
    private static readonly LinearGradientBrush DarkShadowRight = CreateHorizontalShadowGradient(true, DarkShadowAlpha);
    private static readonly LinearGradientBrush DarkShadowLeft = CreateHorizontalShadowGradient(false, DarkShadowAlpha);
    private static readonly LinearGradientBrush LightCurlCastRight = CreateHorizontalShadowGradient(true, LightCurlCastAlpha);
    private static readonly LinearGradientBrush DarkCurlCastRight = CreateHorizontalShadowGradient(true, DarkCurlCastAlpha);
    private static readonly LinearGradientBrush WaveBandBrush = CreateWaveBandBrush(WaveLeadBandAlpha);

    public static async Task<T> RunAsync<T>(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        Func<Task<T>> changeContentAsync,
        CancellationToken cancellationToken,
        bool holdOutgoingPage = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (animation == AnimationNone && !holdOutgoingPage)
            return await changeContentAsync();

        // Clear stale overlays before photographing so the snapshot shows
        // only live reader content.
        Reset(surface);
        var scaling = ResolveCaptureScaling(surface.SnapshotSource);
        using var backdrop = CaptureBackdrop(surface, scaling);
        var snapshot = backdrop is null ? null : Capture(surface.SnapshotSource, backdrop, scaling);
        if (snapshot is null)
        {
            Reset(surface);
            return await changeContentAsync();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Begin(surface, snapshot, backdrop!, animation, visualDirection);

            // Keep the outgoing snapshot visible while the new content is
            // being composed/configured. Starting the clock before this task
            // completes could expose a blank or partially laid-out page on a
            // slow chapter switch.
            var changedContent = await changeContentAsync();
            cancellationToken.ThrowIfCancellationRequested();

            // With animation disabled, chapter loads still keep the old page
            // in place until the new one is ready, then reveal it immediately.
            if (animation != AnimationNone)
            {
                await PlayFramesAsync(
                    surface,
                    animation,
                    visualDirection,
                    cancellationToken);
            }
            return changedContent;
        }
        finally
        {
            Reset(surface);
            snapshot.Dispose();
        }
    }

    /// <summary>
    /// Captures the outgoing page and prepares the surface for 1:1 interactive
    /// drag tracking (Push or Cover). Returns null if capture fails or the
    /// animation does not support interactive dragging.
    /// </summary>
    internal static InteractiveTransitionSession? BeginInteractive(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection)
    {
        if (animation is not (AnimationPush or AnimationCover or AnimationSlide or AnimationCurl))
            return null;

        Reset(surface);
        var scaling = ResolveCaptureScaling(surface.SnapshotSource);
        using var backdrop = CaptureBackdrop(surface, scaling);
        var snapshot = backdrop is null ? null : Capture(surface.SnapshotSource, backdrop, scaling);
        if (snapshot is null)
        {
            Reset(surface);
            return null;
        }

        Begin(surface, snapshot, backdrop!, animation, visualDirection);
        DrawProgress(surface, animation, visualDirection, 0d);
        return new InteractiveTransitionSession(surface, snapshot, animation, visualDirection);
    }

    internal sealed class InteractiveTransitionSession : IDisposable
    {
        private readonly ReaderTransitionSurface _surface;
        private readonly RenderTargetBitmap _snapshot;
        private readonly int _animation;
        private readonly int _visualDirection;
        private double _progress;
        private bool _disposed;

        internal InteractiveTransitionSession(
            ReaderTransitionSurface surface,
            RenderTargetBitmap snapshot,
            int animation,
            int visualDirection)
        {
            _surface = surface;
            _snapshot = snapshot;
            _animation = animation;
            _visualDirection = visualDirection;
        }

        public double Progress => _progress;

        public void Update(double progress)
        {
            if (_disposed) return;
            _progress = Math.Clamp(progress, 0d, 1d);
            DrawProgress(_surface, _animation, _visualDirection, _progress);
        }

        public async Task FinishAsync(
            double targetProgress,
            double durationMs,
            CancellationToken cancellationToken,
            Action? onBeforeReset = null)
        {
            if (_disposed) return;
            var start = _progress;
            var target = Math.Clamp(targetProgress, 0d, 1d);
            try
            {
                if (durationMs > 8 && Math.Abs(target - start) > 0.002)
                {
                    await PlayProgressAsync(
                        _surface,
                        _animation,
                        _visualDirection,
                        start,
                        target,
                        durationMs,
                        cancellationToken);
                }
                else
                {
                    DrawProgress(_surface, _animation, _visualDirection, target);
                }
            }
            finally
            {
                onBeforeReset?.Invoke();
                Dispose();
            }
        }

        public void Cancel(Action? onBeforeReset = null)
        {
            if (_disposed) return;
            try
            {
                onBeforeReset?.Invoke();
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Reset(_surface);
            _snapshot.Dispose();
        }
    }

    private static Task PlayFramesAsync(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        CancellationToken cancellationToken)
    {
        return RunVSyncLoopAsync(
            surface.SnapshotSource,
            elapsed => DrawFrame(surface, animation, visualDirection, elapsed),
            cancellationToken);
    }

    private static Task PlayProgressAsync(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        double startProgress,
        double endProgress,
        double durationMs,
        CancellationToken cancellationToken)
    {
        return RunVSyncLoopAsync(
            surface.SnapshotSource,
            elapsed =>
            {
                if (elapsed >= durationMs)
                {
                    DrawProgress(surface, animation, visualDirection, endProgress);
                    return false;
                }

                var t = EaseOut(elapsed / durationMs);
                var progress = startProgress + (endProgress - startProgress) * t;
                DrawProgress(surface, animation, visualDirection, progress);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Drives frame updates on the host <see cref="TopLevel"/>'s VSync callback
    /// (supporting 120Hz ProMotion on iOS/macOS) with a DispatcherTimer watchdog
    /// so Avalonia Headless tests advance without manual render-timer ticks.
    /// </summary>
    private static async Task RunVSyncLoopAsync(
        Visual anchor,
        Func<double, bool> step,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var topLevel = TopLevel.GetTopLevel(anchor);
        var finished = false;
        var lastRafMs = -100d;
        DispatcherTimer? watchdog = null;

        void Complete(Exception? error = null, bool canceled = false)
        {
            if (finished) return;
            finished = true;
            watchdog?.Stop();
            if (error is not null) completion.TrySetException(error);
            else if (canceled) completion.TrySetCanceled(cancellationToken);
            else completion.TrySetResult();
        }

        void Tick(bool fromRaf)
        {
            if (finished) return;
            if (fromRaf) lastRafMs = clock.Elapsed.TotalMilliseconds;
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Complete(canceled: true);
                    return;
                }

                if (!step(clock.Elapsed.TotalMilliseconds))
                {
                    Complete();
                    return;
                }

                if (fromRaf && topLevel is not null)
                    topLevel.RequestAnimationFrame(_ => Tick(fromRaf: true));
            }
            catch (Exception ex)
            {
                Complete(error: ex);
            }
        }

        using var cancelRegistration = cancellationToken.Register(() =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Complete(canceled: true);
            else Dispatcher.UIThread.Post(() => Complete(canceled: true), DispatcherPriority.Send);
        });

        if (topLevel is not null)
            topLevel.RequestAnimationFrame(_ => Tick(fromRaf: true));

        watchdog = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(FallbackIntervalMs)
        };
        watchdog.Tick += (_, _) =>
        {
            // When VSync RequestAnimationFrame is actively firing (real displays),
            // let RAF own every frame without double-stepping.
            if (clock.Elapsed.TotalMilliseconds - lastRafMs < FallbackIntervalMs * 1.5)
                return;
            Tick(fromRaf: false);
        };
        watchdog.Start();

        await completion.Task;
    }

    internal static bool DrawFrame(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        double elapsed)
    {
        return animation switch
        {
            AnimationFade => DrawFadeFrame(surface, elapsed),
            AnimationSlide => DrawTimedProgress(surface, animation, visualDirection, elapsed, SlideDurationMs),
            AnimationPush => DrawTimedProgress(surface, animation, visualDirection, elapsed, PushDurationMs),
            AnimationCover => DrawTimedProgress(surface, animation, visualDirection, elapsed, CoverDurationMs),
            AnimationCurl => DrawTimedProgress(surface, animation, visualDirection, elapsed, CurlDurationMs),
            AnimationWave => DrawWaveFrame(surface, visualDirection, elapsed),
            _ => false
        };
    }

    private static bool DrawTimedProgress(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        double elapsed,
        double durationMs)
    {
        if (elapsed >= durationMs)
            return false;

        var progress = EaseOut(elapsed / durationMs);
        DrawProgress(surface, animation, visualDirection, progress);
        return true;
    }

    internal static void DrawProgress(
        ReaderTransitionSurface surface,
        int animation,
        int visualDirection,
        double progress)
    {
        progress = Math.Clamp(progress, 0d, 1d);
        switch (animation)
        {
            case AnimationPush:
                DrawPushProgress(surface, visualDirection, progress);
                break;
            case AnimationCover:
                DrawCoverProgress(surface, visualDirection, progress);
                break;
            case AnimationSlide:
                DrawSlideProgress(surface, visualDirection, progress);
                break;
            case AnimationCurl:
                DrawCurlProgress(surface, visualDirection, progress);
                break;
        }
    }

    /// <summary>Fade-through-background: the outgoing page dissolves into an
    /// opaque paper backdrop, then the new page is revealed underneath. Only
    /// one page's glyphs can be visible during either half.</summary>
    private static bool DrawFadeFrame(ReaderTransitionSurface surface, double elapsed)
    {
        if (elapsed < FadeOutMs)
        {
            var progress = EaseFade(elapsed / FadeOutMs);
            surface.Snapshot.Opacity = 1d - progress;
            surface.Trail.Opacity = 1;
            return true;
        }

        surface.Snapshot.IsVisible = false;
        var revealElapsed = elapsed - FadeOutMs;
        if (revealElapsed < FadeInMs)
        {
            surface.Trail.Opacity = 1d - EaseFade(revealElapsed / FadeInMs);
            return true;
        }

        surface.Trail.Opacity = 0;
        return false;
    }

    /// <summary>Side-by-side push (平移): outgoing and incoming sheets move
    /// together in lockstep with no overlay shadow bar.</summary>
    private static void DrawPushProgress(
        ReaderTransitionSurface surface,
        int visualDirection,
        double p)
    {
        var forward = visualDirection >= 0;
        var width = surface.SnapshotSource.Bounds.Width;
        var outgoingX = (forward ? -width : width) * p;
        var incomingX = forward ? outgoingX + width : outgoingX - width;

        SetTranslateX(surface.Snapshot, outgoingX);
        if (surface.IncomingSheet is { } incoming)
        {
            EnsureIncomingPaperBacking(incoming, surface.BackdropSource);
            SetTranslateX(incoming, incomingX);
        }

        surface.Edge.IsVisible = false;
        surface.Trail.IsVisible = false;
    }

    /// <summary>Bidirectional cover (覆盖): forward slides the outgoing page off
    /// to the left exposing the incoming page; backward slides the incoming page
    /// in from the left over the stationary outgoing page.</summary>
    private static void DrawCoverProgress(
        ReaderTransitionSurface surface,
        int visualDirection,
        double p)
    {
        var forward = visualDirection >= 0;
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var dark = IsDarkPaper(surface.BackdropSource);
        var dimPeak = dark ? DarkCoverDimOpacity : LightCoverDimOpacity;

        if (forward || surface.IncomingSheet is null)
        {
            var offsetX = (forward ? -width : width) * p;
            surface.Snapshot.ZIndex = ZSnapshot;
            SetTranslateX(surface.Snapshot, offsetX);
            if (surface.IncomingSheet is { } incoming)
            {
                incoming.ZIndex = ZContent;
                incoming.RenderTransform = null;
            }

            // Subtle full-screen dim over the exposed page fades smoothly to 0.
            var dim = surface.Trail;
            dim.ZIndex = ZDimBetween;
            dim.Fill = BlackDimBrush;
            dim.Width = width;
            dim.Height = height;
            dim.Opacity = dimPeak * (1d - p);
            dim.IsVisible = dim.Opacity > 0.002;

            ConfigureEdgeShadow(
                surface.Edge,
                width,
                height,
                forward ? offsetX + width : offsetX - ShadowWidth(width),
                opaqueAtStart: forward,
                dark,
                EdgeFadeEnvelope(p));
            return;
        }

        // Backward cover: incoming sheet slides in from the left (-width -> 0)
        // on top of the stationary outgoing snapshot.
        var incomingSheet = surface.IncomingSheet;
        var incomingX = -width * (1d - p);

        surface.Snapshot.ZIndex = ZSnapshotBehind;
        surface.Snapshot.RenderTransform = null;

        EnsureIncomingPaperBacking(incomingSheet, surface.BackdropSource);
        incomingSheet.ZIndex = ZIncomingCover;
        SetTranslateX(incomingSheet, incomingX);

        var veil = surface.Trail;
        veil.ZIndex = ZDimBetween;
        veil.Fill = BlackDimBrush;
        veil.Width = width;
        veil.Height = height;
        veil.Opacity = dimPeak * p;
        veil.IsVisible = veil.Opacity > 0.002;

        // Soft trailing shadow sits on the RIGHT edge of the incoming sheet and
        // fades out before either screen edge so it never lingers as a gray bar.
        ConfigureEdgeShadow(
            surface.Edge,
            width,
            height,
            incomingX + width,
            opaqueAtStart: true,
            dark,
            EdgeFadeEnvelope(p));
    }

    /// <summary>Realistic paper-curl turn (仿真): clips the turning sheet along
    /// the moving fold line, renders a curved paper back-flap with cylindrical
    /// highlight/shading matched to the active paper tone, and casts a soft
    /// shadow onto the revealed underlying page.</summary>
    private static void DrawCurlProgress(
        ReaderTransitionSurface surface,
        int visualDirection,
        double p)
    {
        var forward = visualDirection >= 0;
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        if (width <= 1 || height <= 1) return;

        var dark = IsDarkPaper(surface.BackdropSource);
        var paperColor = ResolvePaperColor(surface.BackdropSource, dark);
        var dimPeak = dark ? DarkCoverDimOpacity : LightCoverDimOpacity;
        var envelope = Math.Sin(Math.Clamp(p, 0d, 1d) * Math.PI);
        var shadowEnvelope = EdgeFadeEnvelope(p);
        var castWidth = Math.Clamp(width * 0.085, 22d, 44d);

        if (forward || surface.IncomingSheet is null)
        {
            var spineX = width * (1d - p);
            surface.Snapshot.ZIndex = ZSnapshot;
            surface.Snapshot.RenderTransform = null;
            surface.Snapshot.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, spineX), height));

            if (surface.IncomingSheet is { } incoming)
            {
                incoming.ZIndex = ZContent;
                incoming.RenderTransform = null;
                incoming.Clip = null;
            }

            // Ambient shadow over the newly exposed right side fades as the page clears.
            var dim = surface.Trail;
            dim.ZIndex = ZDimBetween;
            dim.Fill = BlackDimBrush;
            dim.Width = Math.Max(0, width - spineX);
            dim.Height = height;
            SetTranslateX(dim, spineX);
            dim.Opacity = dimPeak * (1d - p);
            dim.IsVisible = dim.Opacity > 0.002 && dim.Width > 0.5;

            var flapWidth = Math.Min(width * 0.22, Math.Max(0, spineX)) * Math.Pow(Math.Max(0, envelope), 0.42);
            ConfigureCurlBackFlap(surface.Front, spineX, flapWidth, height, paperColor, dark);
            ConfigureCurlCastShadow(surface.Edge, spineX, castWidth, height, dark, shadowEnvelope);
            return;
        }

        // Backward curl: incoming page unrolls from the left (0 -> width) over the stationary outgoing snapshot.
        var incomingSheet = surface.IncomingSheet;
        var backSpineX = width * p;

        surface.Snapshot.ZIndex = ZSnapshotBehind;
        surface.Snapshot.RenderTransform = null;
        surface.Snapshot.Clip = null;

        EnsureIncomingPaperBacking(incomingSheet, surface.BackdropSource);
        incomingSheet.ZIndex = ZIncomingCover;
        incomingSheet.RenderTransform = null;
        incomingSheet.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, backSpineX), height));

        var veil = surface.Trail;
        veil.ZIndex = ZDimBetween;
        veil.Fill = BlackDimBrush;
        veil.Width = Math.Max(0, width - backSpineX);
        veil.Height = height;
        SetTranslateX(veil, backSpineX);
        veil.Opacity = dimPeak * p * 0.8;
        veil.IsVisible = veil.Opacity > 0.002 && veil.Width > 0.5;

        var backFlapWidth = Math.Min(width * 0.22, Math.Max(0, backSpineX)) * Math.Pow(Math.Max(0, envelope), 0.42);
        ConfigureCurlBackFlap(surface.Front, backSpineX, backFlapWidth, height, paperColor, dark);
        ConfigureCurlCastShadow(surface.Edge, backSpineX, castWidth, height, dark, shadowEnvelope);
    }

    private static void ConfigureCurlBackFlap(
        Rectangle flap,
        double spineX,
        double flapWidth,
        double height,
        Color paperColor,
        bool dark)
    {
        if (flapWidth <= 1.5)
        {
            flap.IsVisible = false;
            return;
        }

        flap.ZIndex = ZTop - 1;
        flap.Fill = CreateCurlBackBrush(paperColor, dark);
        flap.Width = flapWidth;
        flap.Height = height;
        flap.Opacity = 1;
        SetTranslateX(flap, spineX - flapWidth);
        flap.IsVisible = true;
    }

    private static void ConfigureCurlCastShadow(
        Rectangle edge,
        double spineX,
        double castWidth,
        double height,
        bool dark,
        double opacity)
    {
        if (opacity <= 0.005)
        {
            edge.IsVisible = false;
            return;
        }

        edge.ZIndex = ZTop;
        edge.Fill = dark ? DarkCurlCastRight : LightCurlCastRight;
        edge.Width = castWidth;
        edge.Height = height;
        edge.Opacity = opacity;
        SetTranslateX(edge, spineX);
        edge.IsVisible = true;
    }

    private static LinearGradientBrush CreateCurlBackBrush(Color paper, bool dark)
    {
        static Color Shade(Color c, double factor)
        {
            byte Scale(byte channel) => (byte)Math.Clamp(Math.Round(channel * factor), 0, 255);
            return Color.FromArgb(255, Scale(c.R), Scale(c.G), Scale(c.B));
        }

        static Color Lift(Color c, double amount)
        {
            byte Mix(byte channel) => (byte)Math.Clamp(Math.Round(channel + (255 - channel) * amount), 0, 255);
            return Color.FromArgb(255, Mix(c.R), Mix(c.G), Mix(c.B));
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        if (dark)
        {
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.72), 0.00));
            brush.GradientStops.Add(new GradientStop(Lift(paper, 0.06), 0.24));
            brush.GradientStops.Add(new GradientStop(Lift(paper, 0.14), 0.48));
            brush.GradientStops.Add(new GradientStop(Lift(paper, 0.04), 0.80));
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.58), 1.00));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.86), 0.00));
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.94), 0.20));
            brush.GradientStops.Add(new GradientStop(Lift(paper, 0.35), 0.44));
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.93), 0.78));
            brush.GradientStops.Add(new GradientStop(Shade(paper, 0.80), 1.00));
        }
        return brush;
    }

    private static Color ResolvePaperColor(Panel backdropSource, bool dark)
    {
        if (backdropSource.Background is ISolidColorBrush solid)
            return solid.Color;
        return dark ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xFF, 0xFF, 0xFF);
    }

    private static void DrawSlideProgress(
        ReaderTransitionSurface surface,
        int visualDirection,
        double p)
    {
        var forward = visualDirection >= 0;
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var offsetX = (forward ? -width : width) * p;
        SetTranslateX(surface.Snapshot, offsetX);

        var shadowWidth = ShadowWidth(width);
        ConfigureEdgeShadow(
            surface.Edge,
            width,
            height,
            forward ? offsetX + width : offsetX - shadowWidth,
            opaqueAtStart: forward,
            IsDarkPaper(surface.BackdropSource),
            EdgeFadeEnvelope(p));
    }

    private static double ShadowWidth(double viewportWidth) =>
        Math.Clamp(viewportWidth * SlideShadowWidthRatio, SlideShadowMinWidth, SlideShadowMaxWidth);

    /// <summary>Fades the edge shadow gently at both start and end so no vertical
    /// bar ever pops in or lingers against a screen edge.</summary>
    private static double EdgeFadeEnvelope(double p) =>
        Math.Clamp(Math.Min(p * 8d, (1d - p) * 4d), 0d, 1d);

    private static void ConfigureEdgeShadow(
        Rectangle edge,
        double viewportWidth,
        double height,
        double x,
        bool opaqueAtStart,
        bool dark,
        double opacity)
    {
        if (opacity <= 0.005)
        {
            edge.IsVisible = false;
            return;
        }

        edge.ZIndex = ZTop;
        edge.Fill = dark
            ? (opaqueAtStart ? DarkShadowRight : DarkShadowLeft)
            : (opaqueAtStart ? LightShadowRight : LightShadowLeft);
        edge.Width = ShadowWidth(viewportWidth);
        edge.Height = height;
        edge.Opacity = opacity;
        SetTranslateX(edge, x);
        edge.IsVisible = true;
    }

    private static void EnsureIncomingPaperBacking(Control incoming, Panel backdropSource)
    {
        if (incoming is ContentControl content && content.Background != backdropSource.Background)
            content.Background = backdropSource.Background;
    }

    private static void SetTranslateX(Visual visual, double x)
    {
        if (visual.RenderTransform is TranslateTransform existing)
            existing.X = x;
        else
            visual.RenderTransform = new TranslateTransform(x, 0);
    }

    private static bool DrawWaveFrame(
        ReaderTransitionSurface surface,
        int visualDirection,
        double elapsed)
    {
        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;
        var forward = visualDirection >= 0;

        if (elapsed < WaveSweepMs)
        {
            var p = EaseOut(elapsed / WaveSweepMs);
            var boundary = forward ? width * (1d - p) : width * p;
            var bandWidth = Math.Max(24, width * WaveBandWidthRatio);
            surface.Snapshot.Clip = new RectangleGeometry(forward
                ? new Rect(0, 0, boundary, height)
                : new Rect(boundary, 0, width - boundary, height));

            ConfigureWaveBand(surface.Front, boundary, bandWidth, height, forward);
            surface.Trail.IsVisible = false;
            return true;
        }

        // Sweep finished: remove every effect in the same frame so the new page
        // lands cleanly without an afterimage.
        surface.Front.IsVisible = false;
        surface.Trail.IsVisible = false;
        surface.Snapshot.Clip = null;
        surface.Snapshot.OpacityMask = null;
        surface.Snapshot.IsVisible = false;
        return false;
    }

    private static void ConfigureWaveBand(
        Rectangle band,
        double boundary,
        double bandWidth,
        double height,
        bool forward)
    {
        var x = forward ? boundary : boundary - bandWidth;
        band.Fill = WaveBandBrush;
        band.Width = bandWidth;
        band.Height = height;
        SetTranslateX(band, x);
        band.IsVisible = true;
    }

    private static LinearGradientBrush CreateWaveBandBrush(double peakAlpha)
    {
        var peak = (byte)Math.Round(Math.Clamp(peakAlpha, 0, 1) * 255);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.35), 0, 0, 0), 0.22));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(peak, 0, 0, 0), 0.5));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb((byte)Math.Round(peak * 0.35), 0, 0, 0), 0.78));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        return brush;
    }

    private static LinearGradientBrush CreateHorizontalShadowGradient(bool opaqueAtStart, byte alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative)
        };
        var transparent = Color.FromArgb(0, 0, 0, 0);
        var shadow = Color.FromArgb(alpha, 0, 0, 0);
        brush.GradientStops.Add(new GradientStop(opaqueAtStart ? shadow : transparent, 0));
        brush.GradientStops.Add(new GradientStop(
            opaqueAtStart
                ? Color.FromArgb((byte)Math.Round(alpha * 0.52), 0, 0, 0)
                : Color.FromArgb((byte)Math.Round(alpha * 0.10), 0, 0, 0),
            0.38));
        brush.GradientStops.Add(new GradientStop(
            opaqueAtStart
                ? Color.FromArgb((byte)Math.Round(alpha * 0.10), 0, 0, 0)
                : Color.FromArgb((byte)Math.Round(alpha * 0.52), 0, 0, 0),
            0.72));
        brush.GradientStops.Add(new GradientStop(opaqueAtStart ? transparent : shadow, 1));
        return brush;
    }

    private static bool IsDarkPaper(Panel backdropSource)
    {
        if (backdropSource.Background is ISolidColorBrush solid)
        {
            var c = solid.Color;
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        }
        return false;
    }

    private static void Begin(
        ReaderTransitionSurface surface,
        RenderTargetBitmap snapshot,
        RenderTargetBitmap backdrop,
        int animation,
        int visualDirection)
    {
        Reset(surface);

        var width = surface.SnapshotSource.Bounds.Width;
        var height = surface.SnapshotSource.Bounds.Height;

        Pin(surface.Snapshot, ZSnapshot);
        surface.Snapshot.Source = snapshot;
        surface.Snapshot.Width = width;
        surface.Snapshot.Height = height;
        // The composited snapshot is stored at 96 DPI with physical Retina pixel
        // dimensions so Avalonia's Skia SurfaceRenderTarget.Draw samples the full
        // pixel buffer (0..PixelWidth, 0..PixelHeight) without DPI cropping/magnification.
        surface.Snapshot.Stretch = Stretch.Fill;
        surface.Snapshot.Clip = null;
        surface.Snapshot.RenderTransform = null;
        surface.Snapshot.Opacity = 1;
        surface.Snapshot.IsVisible = true;

        if (animation == AnimationFade)
        {
            // Fade repurposes the trail rectangle as a background-coloured
            // veil between the incoming page and the outgoing snapshot.
            Pin(surface.Trail, ZDimBetween);
            surface.Trail.Fill = new ImageBrush(backdrop) { Stretch = Stretch.Fill };
            surface.Trail.Width = width;
            surface.Trail.Height = height;
            surface.Trail.Opacity = 1;
            surface.Trail.IsVisible = true;
        }
        else if (animation is AnimationPush or AnimationCover or AnimationCurl
                 && surface.IncomingSheet is { } incoming)
        {
            EnsureIncomingPaperBacking(incoming, surface.BackdropSource);
            if ((animation is AnimationCover or AnimationCurl) && visualDirection < 0)
            {
                // Keep snapshot on top during async loading until DrawProgress
                // takes over once the incoming page is ready.
                surface.Snapshot.ZIndex = ZSnapshot;
            }
        }
    }

    private static void Pin(Control control, int zIndex)
    {
        control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        control.ZIndex = zIndex;
    }

    private static void Reset(ReaderTransitionSurface surface)
    {
        ResetImage(surface.Snapshot, ZSnapshot);
        ResetRectangle(surface.Trail, ZTrail);
        ResetRectangle(surface.Front, ZTop);
        ResetRectangle(surface.Edge, ZTop);
        if (surface.IncomingSheet is { } incoming)
        {
            incoming.RenderTransform = null;
            incoming.Clip = null;
            incoming.ZIndex = ZContent;
            if (incoming is ContentControl content)
                content.Background = Brushes.Transparent;
        }
    }

    private static void ResetImage(Image image, int zIndex)
    {
        image.Source = null;
        image.IsVisible = false;
        image.Opacity = 1;
        image.OpacityMask = null;
        image.Clip = null;
        image.RenderTransform = null;
        image.Width = double.NaN;
        image.Height = double.NaN;
        Pin(image, zIndex);
    }

    private static void ResetRectangle(Rectangle rectangle, int zIndex)
    {
        rectangle.Fill = null;
        rectangle.IsVisible = false;
        rectangle.Opacity = 1;
        rectangle.Clip = null;
        rectangle.RenderTransform = null;
        rectangle.Width = double.NaN;
        rectangle.Height = double.NaN;
        Pin(rectangle, zIndex);
    }

    private static double ResolveCaptureScaling(Visual source)
    {
        var raw = TopLevel.GetTopLevel(source)?.RenderScaling ?? 1d;
        return double.IsFinite(raw) ? Math.Clamp(raw, 1d, 4d) : 1d;
    }

    internal static RenderTargetBitmap? CaptureBackdrop(ReaderTransitionSurface surface, double scaling)
    {
        RenderTargetBitmap? bitmap = null;
        try
        {
            var size = surface.SnapshotSource.Bounds.Size;
            if (size.Width < 16 || size.Height < 16) return null;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * scaling));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * scaling));
            // Keep 96 DPI on the final bitmap so Bitmap.Size equals PixelSize;
            // Avalonia's Skia SurfaceRenderTarget.Draw treats sourceRect as physical pixels.
            bitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
            var paper = surface.BackdropSource;
            var origin = surface.SnapshotSource.TranslatePoint(default, paper) ?? default;
            using (var context = bitmap.CreateDrawingContext())
            {
                // Sample the shared paper at the reader's actual offset so
                // texture does not jump when an overlay appears or disappears.
                using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
                using (context.PushTransform(Matrix.CreateTranslation(-origin.X, -origin.Y)))
                    context.FillRectangle(paper.Background ?? Brushes.White, new Rect(paper.Bounds.Size));
            }
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    internal static RenderTargetBitmap? Capture(Visual source, RenderTargetBitmap backdrop, double scaling)
    {
        RenderTargetBitmap? bitmap = null;
        try
        {
            var bounds = source.Bounds;
            var pixelWidth = (int)Math.Ceiling(bounds.Width * scaling);
            var pixelHeight = (int)Math.Ceiling(bounds.Height * scaling);
            if (bounds.Width < 16 || bounds.Height < 16 || pixelWidth < 16 || pixelHeight < 16)
                return null;

            var scaledDpi = new Vector(96 * scaling, 96 * scaling);
            using var content = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), scaledDpi);
            content.Render(source);
            bitmap = new RenderTargetBitmap(content.PixelSize, new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                var pixelRect = new Rect(0, 0, content.PixelSize.Width, content.PixelSize.Height);
                context.DrawImage(backdrop, pixelRect, pixelRect);
                context.DrawImage(content, pixelRect, pixelRect);
            }
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }

    /// <summary>Shared ease-out curve for crisp, tail-free spatial page transitions.</summary>
    private static double EaseOut(double t) =>
        EaseCubicBezier(t, EaseX1, EaseY1, EaseX2, EaseY2);

    /// <summary>Symmetric dissolve curve so opacity fades stay smooth without snapping.</summary>
    private static double EaseFade(double t) =>
        EaseCubicBezier(t, FadeEaseX1, FadeEaseY1, FadeEaseX2, FadeEaseY2);

    /// <summary>CSS cubic-bezier(x1,y1,x2,y2) progress evaluator via binary
    /// search on the curve parameter.</summary>
    private static double EaseCubicBezier(double t, double x1, double y1, double x2, double y2)
    {
        t = Math.Clamp(t, 0, 1);
        if (t <= 0) return 0;
        if (t >= 1) return 1;

        double Sample(double s) =>
            3 * s * (1 - s) * (1 - s) * y1
            + 3 * s * s * (1 - s) * y2
            + s * s * s;

        const double epsilon = 1e-5;
        var lo = 0d;
        var hi = 1d;
        var s = t;
        while (hi - lo > epsilon)
        {
            var x = 3 * s * (1 - s) * (1 - s) * x1
                + 3 * s * s * (1 - s) * x2
                + s * s * s;
            if (x < t) lo = s;
            else hi = s;
            s = (lo + hi) / 2;
        }

        return Sample(s);
    }
}
