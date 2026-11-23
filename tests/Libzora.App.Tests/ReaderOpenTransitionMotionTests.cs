using Avalonia;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed class ReaderOpenTransitionMotionTests
{
    private static readonly Rect Source = new(235, 140, 154, 214);
    private static readonly Size Viewport = new(1294, 818);
    private static ReaderOpenTransitionMotion CreateMotion(bool isCentered = false) =>
        new(Source, Viewport, isCentered);

    [Fact]
    public void FirstFrameMatchesTheShelfExactly()
    {
        var frame = CreateMotion().SampleOpen(0);
        Assert.Equal(1, frame.Scale);
        Assert.Equal(0, frame.OffsetX);
        Assert.Equal(0, frame.OffsetY);
        Assert.Equal(1, frame.CoverOpacity);
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
        var frame = motion.SampleOpen(ReaderOpenTransitionMotion.EntranceDurationMs);
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
        var pressed = motion.SampleOpen(60);
        Assert.Equal(0.985, pressed.Scale, 8);
        var previous = pressed.Scale;
        for (var elapsed = 70; elapsed <= ReaderOpenTransitionMotion.EntranceDurationMs; elapsed += 10)
        {
            var frame = motion.SampleOpen(elapsed);
            Assert.InRange(Math.Round(frame.Scale, 8), Math.Round(previous, 8), 1.7);
            previous = frame.Scale;
        }
    }

    [Fact]
    public void FastOpenOverlapsLiftAndRevealCleanly()
    {
        var motion = CreateMotion();
        var revealAt = ReaderOpenTransitionMotion.MinimumCoverVisibleMs;
        Assert.True(revealAt < ReaderOpenTransitionMotion.EntranceDurationMs);
        Assert.Equal(1, motion.SampleOpen(revealAt - 1, revealAt).LayerOpacity);
        Assert.InRange(motion.SampleOpen(280, revealAt).LayerOpacity, 0.01, 0.99);
        var complete = motion.SampleOpen(revealAt + ReaderOpenTransitionMotion.RevealDurationMs, revealAt);
        Assert.True(complete.IsComplete);
        Assert.Equal(0, complete.LayerOpacity);
        Assert.Equal(0, complete.CoverOpacity);
    }

    [Fact]
    public void SlowOpenHoldsAStillCoverThenRevealsWithoutAnotherEntrance()
    {
        var motion = CreateMotion();
        var settled = motion.SampleOpen(280);
        var waiting = motion.SampleOpen(900);
        Assert.Equal(settled.Scale, waiting.Scale);
        Assert.Equal(settled.OffsetX, waiting.OffsetX);
        Assert.Equal(settled.OffsetY, waiting.OffsetY);
        Assert.Equal(1, waiting.LayerOpacity);
        Assert.Equal(1, waiting.LoadingOpacity);
        Assert.False(waiting.IsComplete);

        var ready = motion.SampleOpen(900, 900);
        Assert.Equal(0, ready.LoadingOpacity);
        Assert.Equal(1, ready.LayerOpacity);
        Assert.InRange(motion.SampleOpen(990, 900).LayerOpacity, 0.49, 0.51);
        Assert.True(motion.SampleOpen(900 + ReaderOpenTransitionMotion.RevealDurationMs, 900).IsComplete);
    }

    [Fact]
    public void WaitingHintDoesNotFlashForFastLoads()
    {
        var motion = CreateMotion();
        Assert.Equal(0, motion.SampleOpen(0).LoadingOpacity);
        Assert.Equal(0, motion.SampleOpen(380).LoadingOpacity);
        Assert.Equal(0, motion.SampleOpen(ReaderOpenTransitionMotion.LoadingDelayMs).LoadingOpacity);
        Assert.InRange(motion.SampleOpen(560).LoadingOpacity, 0.01, 0.99);
        Assert.Equal(1, motion.SampleOpen(620).LoadingOpacity);
        Assert.Equal(0, motion.SampleOpen(620, 620).LoadingOpacity);
    }

    [Fact]
    public void CloseDissolvesReaderPageAndGlidesCoverProportionallyBackToShelf()
    {
        var motion = CreateMotion();
        var start = motion.SampleClose(0);
        Assert.Equal(1, start.PageOpacity);
        Assert.Equal(0, start.CoverOpacity);
        Assert.Equal(1, start.BackdropOpacity);
        Assert.False(start.IsComplete);

        var mid = motion.SampleClose(160);
        Assert.Equal(0, mid.PageOpacity);
        Assert.Equal(1, mid.CoverOpacity);
        Assert.InRange(mid.Scale, 1, 1.75);

        var end = motion.SampleClose(ReaderOpenTransitionMotion.CloseCoverDurationMs);
        Assert.Equal(1, end.Scale, 8);
        Assert.Equal(0, end.OffsetX, 8);
        Assert.Equal(0, end.OffsetY, 8);
        Assert.Equal(0, end.BackdropOpacity, 8);
        Assert.Equal(0, end.PageOpacity, 8);
        Assert.True(end.IsComplete);
    }

    [Fact]
    public void CenteredCloseSoftlyDissolvesReaderPageWithoutCoverJump()
    {
        var motion = CreateMotion(isCentered: true);
        var start = motion.SampleClose(0);
        Assert.Equal(1, start.PageOpacity);
        Assert.Equal(0, start.CoverOpacity);
        Assert.Equal(0, start.BackdropOpacity);
        Assert.False(start.IsComplete);

        var end = motion.SampleClose(ReaderOpenTransitionMotion.CloseCenterDurationMs);
        Assert.Equal(0, end.PageOpacity);
        Assert.Equal(0.98, end.PageScale, 8);
        Assert.True(end.IsComplete);
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
