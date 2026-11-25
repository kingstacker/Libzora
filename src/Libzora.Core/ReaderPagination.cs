namespace Libzora.Core;

// Geometry shared by the reader host and its pagination scripts. Keeping the
// page model independent from WinUI makes the boundary math easy to test and
// prevents each navigation path from inventing a different page step.
public static class ReaderPaginationDefaults
{
    public const double HorizontalPadding = 24;
    public const double ColumnGap = 48;
    // Keep all four page edges on the same inset so each pagination viewport
    // has balanced whitespace around its content.
    public const double TopPadding = HorizontalPadding;
    public const double BottomPadding = HorizontalPadding;
    public const double SnapTolerance = 4;

    public static double GetColumnWidth(
        double viewportWidth,
        int pagesPerView = 1,
        double horizontalPadding = HorizontalPadding)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return 0;

        var safePagesPerView = Math.Clamp(pagesPerView, 1, 2);
        var safeHorizontalPadding = double.IsFinite(horizontalPadding)
            ? Math.Max(0, horizontalPadding)
            : HorizontalPadding;
        // The multicol container is border-box sized, so its left/right
        // padding is outside the usable column area. A spread must reserve
        // both outer insets plus the gaps between its columns; otherwise two
        // requested columns do not actually fit in one viewport and the
        // browser redistributes them onto uneven page boundaries.
        var columnGap = safeHorizontalPadding * 2;
        var reservedWidth = safeHorizontalPadding * 2
            + columnGap * (safePagesPerView - 1);
        return Math.Max(0, (viewportWidth - reservedWidth) / safePagesPerView);
    }
}

public static class ReaderPaginationPolicy
{
    // Horizontal books keep the established click zones. Only vertical-rl
    // books mirror them so the left edge advances in classical book order.
    public static int GetClickDirection(bool onLeft, bool verticalWriting) =>
        onLeft != verticalWriting ? -1 : 1;

    // Navigation stays logical (+1 next, -1 previous), while a vertical-rl
    // book presents that turn in the opposite physical X direction.
    public static int GetVisualTurnDirection(int navigationDirection, bool verticalWriting)
    {
        var normalized = Math.Sign(navigationDirection);
        return verticalWriting ? -normalized : normalized;
    }

    public static double SnapScrollLeft(
        double scrollLeft,
        double clientWidth,
        double scrollWidth)
    {
        if (!double.IsFinite(clientWidth) || clientWidth <= 0)
            return 0;

        var max = GetLastPageScrollLeft(clientWidth, scrollWidth);
        var safeScrollLeft = double.IsFinite(scrollLeft) ? scrollLeft : 0;
        if (safeScrollLeft >= max - ReaderPaginationDefaults.SnapTolerance)
            return max;

        var nearest = Math.Round(
            safeScrollLeft / clientWidth,
            MidpointRounding.AwayFromZero)
            * clientWidth;

        return Math.Clamp(nearest, 0, max);
    }

    public static bool CanTurn(
        double scrollLeft,
        int direction,
        double clientWidth,
        double scrollWidth)
    {
        if (direction == 0 || !double.IsFinite(clientWidth) || clientWidth <= 0)
            return false;

        var current = SnapScrollLeft(scrollLeft, clientWidth, scrollWidth);
        var max = GetLastPageScrollLeft(clientWidth, scrollWidth);
        return direction < 0
            ? current > ReaderPaginationDefaults.SnapTolerance
            : current < max - ReaderPaginationDefaults.SnapTolerance;
    }

    public static double GetTurnTarget(
        double scrollLeft,
        int direction,
        double clientWidth,
        double scrollWidth)
    {
        var current = SnapScrollLeft(scrollLeft, clientWidth, scrollWidth);
        if (direction == 0 || !double.IsFinite(clientWidth) || clientWidth <= 0)
            return current;

        var max = GetLastPageScrollLeft(clientWidth, scrollWidth);
        var delta = direction < 0 ? -clientWidth : clientWidth;
        return Math.Clamp(current + delta, 0, max);
    }

    public static double GetMaxScrollLeft(double clientWidth, double scrollWidth)
    {
        if (!double.IsFinite(clientWidth) || clientWidth <= 0
            || !double.IsFinite(scrollWidth))
            return 0;

        return Math.Max(0, scrollWidth - clientWidth);
    }

    public static double GetLastPageScrollLeft(
        double clientWidth,
        double scrollWidth,
        double trailingInset = ReaderPaginationDefaults.HorizontalPadding)
    {
        var rawMax = GetMaxScrollLeft(clientWidth, scrollWidth);
        if (rawMax <= 0) return 0;

        var safeInset = double.IsFinite(trailingInset) ? Math.Max(0, trailingInset) : 0;
        var aligned = Math.Round(
                Math.Max(0, rawMax - safeInset) / clientWidth,
                MidpointRounding.AwayFromZero)
            * clientWidth;
        return Math.Clamp(aligned, 0, rawMax);
    }
}

/// <summary>
/// Boundary rules for a text selection whose endpoint can cross paginated
/// content. Selection offsets are caret boundaries: the start of a page is
/// before its first character and the end is after its last character.
/// </summary>
public static class ReaderSelectionPagingPolicy
{
    /// <summary>
    /// Allows the hit-test tolerance around the first glyph to count as the
    /// page-start boundary. This keeps a drag that begins just inside the
    /// first glyph from getting stuck at the page edge.
    /// </summary>
    public static bool IsAtPageStart(int offset, int pageStart, int pageEnd) =>
        HasText(pageStart, pageEnd) && offset <= pageStart + 1;

    /// <summary>
    /// Allows the hit-test tolerance around the last glyph to count as the
    /// page-end boundary. The returned endpoint remains exclusive.
    /// </summary>
    public static bool IsAtPageEnd(int offset, int pageStart, int pageEnd) =>
        HasText(pageStart, pageEnd) && offset >= pageEnd - 1;

    /// <summary>
    /// Returns the endpoint to use after moving the selection onto the target
    /// page. Forward selection ends after the target page's first character
    /// so that character joins the selection; backward selection starts on
    /// its last character for the same reason.
    /// </summary>
    public static int GetCrossPageEndpoint(int direction, int pageStart, int pageEnd)
    {
        if (!HasText(pageStart, pageEnd) || direction == 0)
        {
            return -1;
        }

        return direction > 0 ? pageStart + 1 : pageEnd - 1;
    }

    private static bool HasText(int pageStart, int pageEnd) =>
        pageStart >= 0 && pageEnd > pageStart;
}

/// <summary>
/// Whole-book page position, progress fraction, and slider seek mapping
/// derived from per-chapter page counts. A chapter whose count is zero is an
/// auxiliary document (for example a footnotes container) and takes no part
/// in the reading total.
/// </summary>
public static class ReaderBookProgressPolicy
{
    public static (int CurrentPage, int TotalPages) ResolveBookPagePosition(
        int[] chapterPageCounts,
        int chapterIndex,
        int chapterPageOneBased,
        int chapterPages,
        bool chapterPageCountIsExact = false)
    {
        if (chapterPageCounts.Length == 0)
        {
            var fallbackTotal = Math.Max(1, chapterPages);
            return (Math.Clamp(chapterPageOneBased, 1, fallbackTotal), fallbackTotal);
        }

        var safeChapter = Math.Clamp(chapterIndex, 0, chapterPageCounts.Length - 1);
        if (chapterPages > 0)
        {
            // A complete layout is authoritative. Until then a partially
            // composed chapter only guarantees a minimum, because its prefix
            // can be shorter than the chapter.
            chapterPageCounts[safeChapter] = chapterPageCountIsExact
                ? Math.Max(1, chapterPages)
                : Math.Max(1, Math.Max(chapterPageCounts[safeChapter], chapterPages));
        }

        var totalPages = 0L;
        var prefixPages = 0L;
        for (var i = 0; i < chapterPageCounts.Length; i++)
        {
            var count = Math.Max(0, chapterPageCounts[i]);
            if (i < safeChapter) prefixPages += count;
            totalPages += count;
        }

        var safeTotal = (int)Math.Clamp(totalPages, 1, int.MaxValue);
        var currentPage = (int)Math.Clamp(prefixPages + Math.Max(1, chapterPageOneBased), 1, safeTotal);
        return (currentPage, safeTotal);
    }

    public static double CalculateFraction(int currentPageOneBased, int totalPages)
    {
        if (totalPages <= 0) return 0;
        return Math.Clamp((double)Math.Max(1, currentPageOneBased) / totalPages, 0, 1);
    }

    public static double CalculateFraction(
        IReadOnlyList<int> chapterPageCounts,
        int chapterIndex,
        double chapterRatio)
    {
        if (chapterPageCounts.Count == 0) return 0;

        var safeChapter = Math.Clamp(chapterIndex, 0, chapterPageCounts.Count - 1);
        var clampedRatio = double.IsFinite(chapterRatio) ? Math.Clamp(chapterRatio, 0, 1) : 0;
        var totalPages = 0L;
        var prefixPages = 0L;
        for (var i = 0; i < chapterPageCounts.Count; i++)
        {
            var count = Math.Max(0, chapterPageCounts[i]);
            if (i < safeChapter) prefixPages += count;
            totalPages += count;
        }

        if (totalPages <= 0) return 0;
        var currentChapterPages = Math.Max(0, chapterPageCounts[safeChapter]);
        if (currentChapterPages == 0)
            return Math.Clamp((double)prefixPages / totalPages, 0, 1);
        var continuousPage = prefixPages + 1d + clampedRatio * Math.Max(0, currentChapterPages - 1);
        return Math.Clamp(continuousPage / totalPages, 0, 1);
    }

    public static (int ChapterIndex, double ChapterRatio) MapPercentToChapterTarget(
        IReadOnlyList<int> chapterPageCounts,
        double percent)
    {
        if (chapterPageCounts.Count == 0) return (0, 0);

        var clampedPercent = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        if (clampedPercent <= 0) return (FirstReadingChapter(chapterPageCounts), 0);

        var totalPages = 0L;
        for (var i = 0; i < chapterPageCounts.Count; i++)
            totalPages += Math.Max(0, chapterPageCounts[i]);
        if (totalPages <= 0) return (FirstReadingChapter(chapterPageCounts), 0);

        if (clampedPercent >= 100)
            return (LastReadingChapter(chapterPageCounts), 1);

        var targetPage = Math.Clamp(clampedPercent / 100d * totalPages, 1d, totalPages);
        var prefix = 0L;
        for (var i = 0; i < chapterPageCounts.Count; i++)
        {
            var count = Math.Max(0, chapterPageCounts[i]);
            if (count == 0) continue;
            if (targetPage <= prefix + count || i == chapterPageCounts.Count - 1)
            {
                var localPageIndex = Math.Clamp(targetPage - prefix - 1d, 0d, Math.Max(0, count - 1));
                var ratio = count > 1 ? Math.Clamp(localPageIndex / (count - 1d), 0, 1) : 0;
                return (i, ratio);
            }
            prefix += count;
        }

        return (LastReadingChapter(chapterPageCounts), 1);
    }

    private static int FirstReadingChapter(IReadOnlyList<int> chapterPageCounts)
    {
        for (var i = 0; i < chapterPageCounts.Count; i++)
            if (chapterPageCounts[i] > 0) return i;
        return 0;
    }

    private static int LastReadingChapter(IReadOnlyList<int> chapterPageCounts)
    {
        for (var i = chapterPageCounts.Count - 1; i >= 0; i--)
            if (chapterPageCounts[i] > 0) return i;
        return Math.Max(0, chapterPageCounts.Count - 1);
    }
}
