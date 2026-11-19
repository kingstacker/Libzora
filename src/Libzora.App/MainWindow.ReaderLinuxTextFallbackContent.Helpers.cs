using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Libzora;

/// <summary>
/// Page text, offsets and visual helpers.
/// </summary>
public partial class MainWindow
{
    private string GetLinuxReaderTextFallbackPageText(int relativePageIndex)
    {
        if (_readerLinuxTextFallbackPages.Count == 0)
            return string.Empty;
        var pageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex + relativePageIndex,
            0,
            Math.Max(0, GetLinuxReaderTextFallbackPageCount() - 1));
        return pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && !_readerLinuxTextFallbackPageItems[pageIndex].IsImage
            ? _readerLinuxTextFallbackPageItems[pageIndex].Text
            : string.Empty;
    }

    private int GetLinuxReaderTextFallbackPageOffset(int pageIndex)
    {
        if (_readerLinuxTextFallbackPageItems.Count == 0) return 0;
        pageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, GetLinuxReaderTextFallbackPageCount() - 1));
        return !_readerLinuxTextFallbackPageItems[pageIndex].IsImage
            // Selection and annotation painting must use the same coordinate
            // space. TextOffset is the source-block offset and is shared by
            // every measured page produced from that block group, so using it
            // makes selections on later pages save against the first page.
            ? _readerLinuxTextFallbackPageItems[pageIndex].PaginationOffset
            : 0;
    }

    private static bool IsWithinReaderLinuxTextContent(Visual visual)
    {
        if (visual is SelectableTextBlock or Image)
            return true;
        var current = visual.GetVisualParent();
        while (current is not null)
        {
            if (current is SelectableTextBlock or Image)
                return true;
            current = current.GetVisualParent();
        }
        return false;
    }

    private static bool IsReaderLinuxTextFallbackScrollBarVisual(Visual visual)
    {
        var current = visual;
        while (current is not null)
        {
            if (current is ScrollBar)
                return true;
            current = current.GetVisualParent();
        }

        return false;
    }

    private static double GetReaderPlainTextCharUnits(char ch)
    {
        if (char.IsWhiteSpace(ch)) return 0.35;
        if (ch <= 0x007f) return char.IsLetterOrDigit(ch) ? 0.56 : 0.38;
        return 1.0;
    }

    private void ClampLinuxReaderTextFallbackOffset()
    {
        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var current = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
        var clamped = Math.Clamp(current, 0, maximum);
        if (Math.Abs(clamped - current) > 0.5)
            ReaderLinuxTextFallbackScroll.Offset = new Vector(0, clamped);
    }

    private void SetLinuxReaderTextFallbackOffset(double offset, bool saveProgress = true)
    {
        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        var target = Math.Clamp(offset, 0, maximum);
        ReaderLinuxTextFallbackScroll.Offset = new Vector(0, target);
        SyncLinuxReaderTextFallbackScrollState(saveProgress);
    }
}
