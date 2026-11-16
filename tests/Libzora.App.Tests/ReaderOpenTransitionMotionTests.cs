using Avalonia;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed class ReaderOpenTransitionMotionTests
{
    private static readonly Rect Source = new(235, 140, 154, 214);
    private static ReaderOpenTransitionMotion CreateMotion() => new(Source, new Size(1294, 818));

    [Fact]
    public void FirstFrameMatchesTheShelfExactly()
    {
        var frame = CreateMotion().Sample(0);
        Assert.Equal(1, frame.Scale);
        Assert.Equal(0, frame.OffsetX);
        Assert.Equal(0, frame.OffsetY);
        Assert.Equal(0, frame.BackdropOpacity);
        Assert.Equal(1, frame.ReadActionOpacity);
        Assert.Equal(0, frame.LoadingOpacity);
        Assert.Equal(1, frame.LayerOpacity);
        Assert.False(frame.IsComplete);
    }

    [Fact]
    public void CoverSettlesAtTheTargetWithoutStretchingOrChangingItsLayout()
    {
        var motion = CreateMotion();
        var frame = motion.Sample(ReaderOpenTransitionMotion.EntranceDurationMs);
        var transformed = new Rect(
            Source.X + frame.OffsetX, Source.Y + frame.OffsetY,
            Source.Width * frame.Scale, Source.Height * frame.Scale);
        Assert.Equal(motion.Target.X, transformed.X, 8);
        Assert.Equal(motion.Target.Y, transformed.Y, 8);
        Assert.Equal(motion.Target.Width, transformed.Width, 8);
        Assert.Equal(motion.Target.Height, transformed.Height, 8);
        Assert.Equal(Source.Width / Source.Height, transformed.Width / transformed.Height, 8);
        Assert.Equal(1, frame.BackdropOpacity);
        Assert.Equal(0, frame.ReadActionOpacity);
    }

    [Fact]
    public void PressIsSubtleAndTheLiftNeverFlattensTheCover()
    {
        var motion = CreateMotion();
        var pressed = motion.Sample(60);
        Assert.Equal(0.985, pressed.Scale, 8);
        var previous = pressed.Scale;
        for (var elapsed = 70; elapsed <= ReaderOpenTransitionMotion.EntranceDurationMs; elapsed += 10)
        {
            var frame = motion.Sample(elapsed);
            Assert.InRange(Math.Round(frame.Scale, 8), Math.Round(previous, 8), 1.7);
            previous = frame.Scale;
        }
    }

    [Fact]
    public void FastOpenOverlapsLiftAndRevealAndFinishesIn380Milliseconds()
    {
        var motion = CreateMotion();
        var revealAt = ReaderOpenTransitionMotion.MinimumCoverVisibleMs;
        Assert.True(revealAt < ReaderOpenTransitionMotion.EntranceDurationMs);
        Assert.Equal(1, motion.Sample(revealAt - 1, revealAt).LayerOpacity);
        Assert.InRange(motion.Sample(280, revealAt).LayerOpacity, 0.01, 0.99);
        Assert.False(motion.Sample(379, revealAt).IsComplete);
        var complete = motion.Sample(380, revealAt);
        Assert.True(complete.IsComplete);
        Assert.Equal(0, complete.LayerOpacity);
    }

    [Fact]
    public void SlowOpenHoldsAStillCoverThenRevealsWithoutAnotherEntrance()
    {
        var motion = CreateMotion();
        var settled = motion.Sample(280);
        var waiting = motion.Sample(900);
        Assert.Equal(settled.Scale, waiting.Scale);
        Assert.Equal(settled.OffsetX, waiting.OffsetX);
        Assert.Equal(settled.OffsetY, waiting.OffsetY);
        Assert.Equal(1, waiting.LayerOpacity);
        Assert.Equal(1, waiting.LoadingOpacity);
        Assert.False(waiting.IsComplete);

        var ready = motion.Sample(900, 900);
        Assert.Equal(0, ready.LoadingOpacity);
        Assert.Equal(1, ready.LayerOpacity);
        Assert.InRange(motion.Sample(980, 900).LayerOpacity, 0.49, 0.51);
        Assert.True(motion.Sample(900 + ReaderOpenTransitionMotion.RevealDurationMs, 900).IsComplete);
    }

    [Fact]
    public void WaitingHintDoesNotFlashForFastLoads()
    {
        var motion = CreateMotion();
        Assert.Equal(0, motion.Sample(0).LoadingOpacity);
        Assert.Equal(0, motion.Sample(380).LoadingOpacity);
        Assert.Equal(0, motion.Sample(ReaderOpenTransitionMotion.LoadingDelayMs).LoadingOpacity);
        Assert.InRange(motion.Sample(560).LoadingOpacity, 0.01, 0.99);
        Assert.Equal(1, motion.Sample(620).LoadingOpacity);
        Assert.Equal(0, motion.Sample(620, 620).LoadingOpacity);
    }

    [Theory]
    [InlineData(320, 480)]
    [InlineData(1024, 650)]
    [InlineData(1294, 818)]
    [InlineData(2560, 1440)]
    public void TargetStaysCenteredBoundedAndProportional(double width, double height)
    {
        var motion = new ReaderOpenTransitionMotion(Source, new Size(width, height));
        Assert.Equal(width / 2, motion.Target.Center.X, 8);
        Assert.Equal(height / 2, motion.Target.Center.Y, 8);
        Assert.InRange(Math.Round(motion.Target.Width / Source.Width, 8), 1, 1.7);
        Assert.InRange(motion.Target.Width, Source.Width, 300);
        Assert.InRange(motion.Target.X, 0, width);
        Assert.InRange(motion.Target.Right, 0, width);
        Assert.InRange(motion.Target.Y, 0, height);
        Assert.InRange(motion.Target.Bottom, 0, height);
        Assert.Equal(Source.Width / Source.Height, motion.Target.Width / motion.Target.Height, 8);
    }
}
