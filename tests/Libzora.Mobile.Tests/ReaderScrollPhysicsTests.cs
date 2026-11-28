using Xunit;

namespace Libzora.Mobile.Tests;

public sealed class ReaderScrollPhysicsTests
{
    [Theory]
    [InlineData(-200, 216)]
    [InlineData(-1600, 1728)]
    [InlineData(3200, -3456)]
    [InlineData(-9000, 6000)]
    public void ReleaseUsesTheFingerVelocityAndReadingDirection(double finger, double expected) =>
        Assert.Equal(expected, MobileReaderScrollPhysics.ReleaseVelocity(finger, 0), 8);

    [Theory]
    [InlineData(50, 54)]
    [InlineData(170, 183.6)]
    [InlineData(-50, -54)]
    public void FingerDisplacementHasOnlyASmallConsistentAmplification(double finger, double pixels) =>
        Assert.Equal(pixels, MobileReaderScrollPhysics.ScaleDragDelta(finger), 8);

    [Fact]
    public void MangaCanKeepItsExistingUnamplifiedVelocity() =>
        Assert.Equal(200, MobileReaderScrollPhysics.ReleaseVelocity(-200, 0, amplifyDrag: false));

    [Theory]
    [InlineData(0.12)]
    [InlineData(0.2)]
    [InlineData(1)]
    public void HoldingTheFingerStopsOldFlickInertia(double idleSeconds) =>
        Assert.Equal(0, MobileReaderScrollPhysics.ReleaseVelocity(-1800, idleSeconds));

    [Fact]
    public void FastFlicksTravelFartherThanSlowFlicks()
    {
        var slow = MobileReaderScrollPhysics.Advance(300, 0.3);
        var fast = MobileReaderScrollPhysics.Advance(2400, 0.3);
        Assert.Equal(slow.Distance * 8, fast.Distance, 8);
        Assert.True(fast.Distance > slow.Distance);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void CoastingIsIndependentOfFrameRate(int framesPerSecond)
    {
        var distance = 0d;
        var velocity = 2400d;
        for (var index = 0; index < framesPerSecond; index++)
        {
            var step = MobileReaderScrollPhysics.Advance(velocity, 1d / framesPerSecond);
            distance += step.Distance;
            velocity = step.Velocity;
        }
        var elapsed = MobileReaderScrollPhysics.Advance(2400, 1);
        Assert.Equal(elapsed.Distance, distance, 8);
        Assert.Equal(elapsed.Velocity, velocity, 8);
    }

    [Fact]
    public void DelayedFramesPreserveElapsedMotionRatherThanTakingOneFixedStep()
    {
        var delayed = MobileReaderScrollPhysics.Advance(1800, 0.12);
        var first = MobileReaderScrollPhysics.Advance(1800, 0.02);
        var remaining = MobileReaderScrollPhysics.Advance(first.Velocity, 0.1);
        Assert.Equal(delayed.Distance, first.Distance + remaining.Distance, 8);
        Assert.Equal(delayed.Velocity, remaining.Velocity, 8);
    }
}
