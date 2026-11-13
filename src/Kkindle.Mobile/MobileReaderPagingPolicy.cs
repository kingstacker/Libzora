namespace Kkindle.Mobile;

public static class MobileReaderPagingPolicy
{
    public static int TapDirection(double ratio, bool verticalWriting, bool isPdf = false)
    {
        var direction = ratio < 1d / 3 ? -1 : ratio > 2d / 3 ? 1 : 0;
        return VisualDirection(direction, verticalWriting, isPdf);
    }

    public static int SwipeDirection(double dx, bool verticalWriting, bool isPdf = false) =>
        VisualDirection(dx < 0 ? 1 : dx > 0 ? -1 : 0, verticalWriting, isPdf);

    public static int VisualDirection(int direction, bool verticalWriting, bool isPdf = false) =>
        verticalWriting && !isPdf ? -direction : direction;
}
