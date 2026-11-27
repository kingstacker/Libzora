using Xunit;

namespace Libzora.Ui.Tests;

public sealed class SeriesShelfScrollTests
{
    private const long Freq = 1000;

    [Fact]
    public void FasterMouseDragProducesHigherReleaseVelocityAndLongerGlide()
    {
        var slowSamples = new (double PointerX, long TimestampTicks)[]
        {
            (400d, 0),
            (370d, 60)
        };
        var fastSamples = new (double PointerX, long TimestampTicks)[]
        {
            (400d, 0),
            (220d, 60)
        };

        var slowVelocity = MainWindow.ComputeSeriesShelfReleaseVelocity(slowSamples, releaseTicks: 70, tickFrequency: Freq);
        var fastVelocity = MainWindow.ComputeSeriesShelfReleaseVelocity(fastSamples, releaseTicks: 70, tickFrequency: Freq);

        Assert.True(slowVelocity > 0d);
        Assert.True(fastVelocity > slowVelocity * 4d);

        var slowFinal = SimulateGlide(startOffset: 0d, velocity: slowVelocity, maxX: 2000d);
        var fastFinal = SimulateGlide(startOffset: 0d, velocity: fastVelocity, maxX: 2000d);

        Assert.True(slowFinal > 50d);
        Assert.True(fastFinal > slowFinal * 4d);
    }

    [Fact]
    public void PausingBeforeReleaseCancelsInertiaVelocity()
    {
        var samples = new (double PointerX, long TimestampTicks)[]
        {
            (400d, 0),
            (200d, 50)
        };

        var velocity = MainWindow.ComputeSeriesShelfReleaseVelocity(samples, releaseTicks: 200, tickFrequency: Freq);
        Assert.Equal(0d, velocity);
    }

    [Fact]
    public void DraggingRightProducesNegativeScrollVelocity()
    {
        var samples = new (double PointerX, long TimestampTicks)[]
        {
            (200d, 0),
            (350d, 50)
        };

        var velocity = MainWindow.ComputeSeriesShelfReleaseVelocity(samples, releaseTicks: 55, tickFrequency: Freq);
        Assert.True(velocity < -1000d);
    }

    [Fact]
    public void InertiaIntoEdgeTriggersRubberBandOverscrollAndBounceBack()
    {
        double offset = 450d;
        double rawOverscroll = 0d;
        double velocity = 1600d;
        const double maxX = 500d;
        var sawOverscroll = false;
        var triggeredBounceBack = false;

        for (var i = 0; i < 200; i++)
        {
            var step = MainWindow.StepSeriesShelfInertia(offset, rawOverscroll, velocity, maxX, 0.016d);
            offset = step.OffsetX;
            rawOverscroll = step.RawOverscrollX;
            velocity = step.VelocityX;
            if (Math.Abs(rawOverscroll) > 0.5d) sawOverscroll = true;
            if (step.ShouldBounceBack)
            {
                triggeredBounceBack = true;
                break;
            }
            if (step.Finished) break;
        }

        Assert.Equal(maxX, offset);
        Assert.True(sawOverscroll);
        Assert.True(triggeredBounceBack);
    }

    private static double SimulateGlide(double startOffset, double velocity, double maxX)
    {
        double offset = startOffset;
        double rawOverscroll = 0d;
        for (var i = 0; i < 300; i++)
        {
            var step = MainWindow.StepSeriesShelfInertia(offset, rawOverscroll, velocity, maxX, 0.016d);
            offset = step.OffsetX;
            rawOverscroll = step.RawOverscrollX;
            velocity = step.VelocityX;
            if (step.Finished || step.ShouldBounceBack) break;
        }
        return offset;
    }
}
