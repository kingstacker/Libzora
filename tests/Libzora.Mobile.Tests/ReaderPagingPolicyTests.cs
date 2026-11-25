using Xunit;

namespace Libzora.Mobile.Tests;

public sealed class ReaderPagingPolicyTests
{
    [Theory]
    [InlineData(false, false, 0.1, -1)]
    [InlineData(false, false, 0.9, 1)]
    [InlineData(true, false, 0.1, 1)]
    [InlineData(true, false, 0.9, -1)]
    [InlineData(true, false, 0.5, 0)]
    [InlineData(true, true, 0.1, -1)]
    [InlineData(true, true, 0.9, 1)]
    public void TapUsesTheReadingOrder(bool vertical, bool pdf, double ratio, int expected) =>
        Assert.Equal(expected, MobileReaderPagingPolicy.TapDirection(ratio, vertical, pdf));

    [Theory]
    [InlineData(false, false, -100, 1, 1)]
    [InlineData(false, false, 100, -1, -1)]
    [InlineData(true, false, 100, 1, -1)]
    [InlineData(true, false, -100, -1, 1)]
    [InlineData(true, true, -100, 1, 1)]
    public void SwipesAndAnimationsUseTheSameVisualDirection(bool vertical, bool pdf, double dx, int logical, int visual)
    {
        Assert.Equal(logical, MobileReaderPagingPolicy.SwipeDirection(dx, vertical, pdf));
        Assert.Equal(visual, MobileReaderPagingPolicy.VisualDirection(logical, vertical, pdf));
    }

    [Theory]
    [InlineData(650, 0, 760, 1)]
    [InlineData(420, 0, 760, 0)]
    [InlineData(150, 0, 760, -1)]
    [InlineData(800, 59, 800, 1)]
    public void TapUsesVisibleViewportWhenAssistantSidebarNarrowsPage(double x, double left, double width, int expected)
    {
        var ratio = MobileReaderPagingPolicy.TapRatio(x, left, width);
        Assert.Equal(expected, MobileReaderPagingPolicy.TapDirection(ratio, verticalWriting: false));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void SurfaceSelectionIsBlockedWhenChromeIsVisibleOrMangaPannable(bool chromeVisible, bool mangaPannable, bool expected) =>
        Assert.Equal(expected, MobileReaderPagingPolicy.AllowsSurfaceSelection(chromeVisible, mangaPannable));
}
