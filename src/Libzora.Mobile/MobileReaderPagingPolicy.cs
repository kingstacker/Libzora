namespace Libzora.Mobile;

public static class MobileReaderPagingPolicy
{
    /// <summary>Computes the horizontal tap ratio within the visible reading viewport.</summary>
    public static double TapRatio(double x, double viewportLeft, double viewportWidth)
    {
        if (!double.IsFinite(x) || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return 0.5;
        var left = double.IsFinite(viewportLeft) ? viewportLeft : 0;
        return Math.Clamp((x - left) / viewportWidth, 0, 1);
    }

    public static int TapDirection(double ratio, bool verticalWriting, bool isPdf = false, bool isRightToLeft = false)
    {
        var direction = ratio < 1d / 3 ? -1 : ratio > 2d / 3 ? 1 : 0;
        return VisualDirection(direction, verticalWriting, isPdf, isRightToLeft);
    }

    public static int SwipeDirection(double dx, bool verticalWriting, bool isPdf = false, bool isRightToLeft = false) =>
        VisualDirection(dx < 0 ? 1 : dx > 0 ? -1 : 0, verticalWriting, isPdf, isRightToLeft);

    public static int VisualDirection(int direction, bool verticalWriting, bool isPdf = false, bool isRightToLeft = false) =>
        (verticalWriting || isRightToLeft) && !isPdf ? -direction : direction;

    /// <summary>Text selection is suppressed while the reader toolbar chrome is visible.</summary>
    public static bool AllowsSurfaceSelection(bool chromeVisible, bool isMangaPannable) =>
        !chromeVisible && !isMangaPannable;
}
