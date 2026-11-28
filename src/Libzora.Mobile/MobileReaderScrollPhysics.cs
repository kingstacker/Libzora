namespace Libzora.Mobile;

/// <summary>Time-based inertia independent of display/input event frequency.</summary>
public static class MobileReaderScrollPhysics
{
    public const double StopVelocity = 25;
    public const double DragMultiplier = 1.08;

    public static double ScaleDragDelta(double delta) => double.IsFinite(delta) ? delta * DragMultiplier : 0;
    private const double DecelerationRate = 4.8;

    public static double ReleaseVelocity(double fingerVelocity, double secondsSinceLastMovement, bool amplifyDrag = true)
    {
        if (!double.IsFinite(fingerVelocity) || !double.IsFinite(secondsSinceLastMovement)
            || secondsSinceLastMovement < 0 || secondsSinceLastMovement >= 0.12) return 0;
        var velocity = Math.Clamp(amplifyDrag ? ScaleDragDelta(-fingerVelocity) : -fingerVelocity, -6000, 6000);
        // A held finger must not retain the velocity of an earlier flick.
        if (secondsSinceLastMovement > 0.035)
            velocity *= Math.Exp(-(secondsSinceLastMovement - 0.035) / 0.025);
        return Math.Abs(velocity) >= 100 ? velocity : 0;
    }

    public static (double Distance, double Velocity) Advance(double velocity, double elapsedSeconds)
    {
        if (!double.IsFinite(velocity) || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            return (0, double.IsFinite(velocity) ? velocity : 0);
        var decay = Math.Exp(-DecelerationRate * elapsedSeconds);
        return (velocity * (1 - decay) / DecelerationRate, velocity * decay);
    }
}
