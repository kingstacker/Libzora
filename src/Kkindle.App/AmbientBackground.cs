using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kkindle;

/// <summary>
/// The colourful wash: a low-opacity base gradient plus nine screen-anchored
/// radial fields whose centres never move with the shelf scroll. Colours fade
/// over 0.8 seconds whenever the library palette changes. The control is
/// decorative and never participates in hit testing.
/// </summary>
public sealed class AmbientBackground : Control
{
    public static readonly StyledProperty<double> WashOpacityProperty =
        AvaloniaProperty.Register<AmbientBackground, double>(nameof(WashOpacity), 0.22);

    private static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly DispatcherTimer _timer;
    private AmbientColor[] _target = [];
    private AmbientColor[] _start = [];
    private AmbientColor[] _displayed = [];
    private long _fadeStartedAt;

    static AmbientBackground()
    {
        AffectsRender<AmbientBackground>(WashOpacityProperty);
    }

    public AmbientBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _timer.Tick += (_, _) => AdvanceFade();
    }

    /// <summary>Overall opacity of the wash, shared by the library and reader.</summary>
    public double WashOpacity
    {
        get => GetValue(WashOpacityProperty);
        set => SetValue(WashOpacityProperty, value);
    }

    internal IReadOnlyList<AmbientColor> TargetColors => _target;
    internal IReadOnlyList<AmbientColor> DisplayedColors => _displayed;
    internal bool IsFading => _timer.IsEnabled;

    internal void SetWash(IReadOnlyList<AmbientColor> colors)
    {
        var next = colors.Count == 0
            ? []
            : colors.Count == AmbientPalette.FieldCount
                ? colors.ToArray()
                : AmbientPalette.ExpandFallback(colors);
        if (next.AsSpan().SequenceEqual(_target)) return;

        // Fade in from transparency for the first palette; fade out to
        // transparency when the user turns the mode off.
        _start = _displayed.Length > 0
            ? _displayed.ToArray()
            : next.Select(color => color with { Strength = 0 }).ToArray();
        _target = next;
        _fadeStartedAt = Stopwatch.GetTimestamp();
        _timer.Stop();
        _timer.Start();
        InvalidateVisual();
    }

    internal void ClearWash()
    {
        _timer.Stop();
        _target = [];
        _start = [];
        _displayed = [];
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var colors = _displayed;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (colors.Length == 0 || width <= 0 || height <= 0) return;
        if (!double.IsFinite(WashOpacity) || WashOpacity <= 0) return;
        var wash = Math.Clamp(WashOpacity, 0, 1);

        using var clip = context.PushClip(new Rect(0, 0, width, height));

        // Avalonia's PushOpacity multiplies every primitive instead of
        // compositing the group, so overlapping fields would stack far past
        // the intended strength. Bake the opacity into each brush instead.
        var middle = colors[colors.Length / 2];
        var baseGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ToColor(colors[0], 0.16 * wash), 0),
                new GradientStop(ToColor(middle, 0.13 * wash), 0.5),
                new GradientStop(ToColor(colors[^1], 0.16 * wash), 1)
            }
        };
        context.FillRectangle(baseGradient, new Rect(0, 0, width, height));

        var radius = Math.Max(width * 0.55, height * 0.30);
        for (var index = 0; index < colors.Length; index++)
        {
            var column = index % 3;
            var row = index / 3;
            var center = new RelativePoint(
                (column + 0.5) / 3,
                (row + 0.5) / 3,
                RelativeUnit.Relative);
            var brush = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = new RelativeScalar(radius / width, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(radius / height, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(ToColor(colors[index], 0.34 * wash), 0),
                    new GradientStop(ToColor(colors[index], 0.23 * wash), 0.55),
                    new GradientStop(ToColor(colors[index], 0), 1)
                }
            };
            context.FillRectangle(brush, new Rect(0, 0, width, height));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void AdvanceFade()
    {
        var elapsed = Stopwatch.GetElapsedTime(_fadeStartedAt);
        var progress = Math.Clamp(elapsed.TotalSeconds / FadeDuration.TotalSeconds, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        var count = Math.Max(_start.Length, _target.Length);
        if (count == 0)
        {
            ClearWash();
            return;
        }

        var displayed = new AmbientColor[count];
        for (var index = 0; index < count; index++)
        {
            var from = index < _start.Length
                ? _start[index]
                : _target[index] with { Strength = 0 };
            var to = index < _target.Length
                ? _target[index]
                : _start[index] with { Strength = 0 };
            displayed[index] = Lerp(from, to, eased);
        }
        _displayed = displayed;
        InvalidateVisual();

        if (progress < 1) return;
        _timer.Stop();
        _displayed = _target;
        InvalidateVisual();
    }

    private static AmbientColor Lerp(AmbientColor from, AmbientColor to, double progress) => new(
        from.Red + (to.Red - from.Red) * progress,
        from.Green + (to.Green - from.Green) * progress,
        from.Blue + (to.Blue - from.Blue) * progress,
        from.Strength + (to.Strength - from.Strength) * progress);

    private static Color ToColor(AmbientColor color, double alpha)
    {
        var strength = Math.Clamp(color.Strength, 0, 1);
        var value = (byte)Math.Clamp(alpha * strength * 255, 0, 255);
        return Color.FromArgb(
            value,
            (byte)(Math.Clamp(color.Red, 0, 1) * 255),
            (byte)(Math.Clamp(color.Green, 0, 1) * 255),
            (byte)(Math.Clamp(color.Blue, 0, 1) * 255));
    }
}
