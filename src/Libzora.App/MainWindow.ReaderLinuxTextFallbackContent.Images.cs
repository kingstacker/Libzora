using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Libzora;

/// <summary>
/// Fallback image sizing and block widths.
/// </summary>
public partial class MainWindow
{
    private static int IndexOfReaderPlainTextBlock(string fullText, string blockText, int startIndex)
    {
        if (string.IsNullOrWhiteSpace(fullText) || string.IsNullOrWhiteSpace(blockText))
            return -1;

        startIndex = Math.Clamp(startIndex, 0, fullText.Length);
        var index = fullText.IndexOf(blockText, startIndex, StringComparison.Ordinal);
        if (index >= 0) return index;

        var firstLine = blockText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine)) return -1;
        return fullText.IndexOf(firstLine.Trim(), startIndex, StringComparison.Ordinal);
    }

    private void UpdateLinuxReaderTextFallbackImages(IReadOnlyList<string> imagePaths)
    {
        ClearLinuxReaderTextFallbackImages();
        ReaderLinuxTextFallbackBlocks.Clear();
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        foreach (var imagePath in imagePaths.Take(12))
        {
            try
            {
                var image = new ReaderLinuxTextFallbackImage(
                    new Bitmap(imagePath),
                    maxWidth,
                    maxHeight);
                ReaderLinuxTextFallbackImages.Add(image);
                ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(image));
            }
            catch
            {
            }
        }
    }

    private void UpdateLinuxReaderTextFallbackImageSizes()
    {
        if (ReaderLinuxTextFallbackImages.Count == 0) return;
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        foreach (var image in ReaderLinuxTextFallbackImages)
            image.Resize(maxWidth, maxHeight);
        if (_readerLayout.FlowMode == 1)
            RenderLinuxReaderTextFallbackPage();
    }

    /// <summary>
    /// Pins the scrolling fallback paragraphs to a measured width so that
    /// starting or ending a selection on a line boundary cannot re-wrap the
    /// body text (the paged surface pins its rectangle the same way).
    /// </summary>
    private void UpdateLinuxReaderTextFallbackBlockWidths()
    {
        if (ReaderLinuxTextFallbackBlocks.Count == 0) return;
        var width = GetLinuxReaderTextFallbackBlockWidth();
        foreach (var block in ReaderLinuxTextFallbackBlocks)
        {
            if (block.IsText)
                block.ResizeText(width);
        }
    }

    private double GetLinuxReaderTextFallbackBlockWidth()
    {
        var available = ReaderLinuxTextFallbackScroll.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = ReaderLinuxTextFallbackOverlay.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = ReaderWebViewHost.Bounds.Width;
        if (!double.IsFinite(available) || available <= 0)
            available = Math.Max(320, _readerLayout.MaxWidth);

        // ScrollViewer padding (24 per side) plus room for the vertical scrollbar.
        available -= 48 + 18;
        var maxWidth = Math.Max(320, _readerLayout.MaxWidth);
        return Math.Floor(Math.Max(240, Math.Min(maxWidth, available)));
    }

    private (double MaxWidth, double MaxHeight) GetLinuxReaderTextFallbackImageBounds()    {
        var overlayWidth = ReaderLinuxTextFallbackOverlay.Bounds.Width;
        if (!double.IsFinite(overlayWidth) || overlayWidth <= 0)
            overlayWidth = ReaderWebViewHost.Bounds.Width;
        var overlayHeight = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        if (!double.IsFinite(overlayHeight) || overlayHeight <= 0)
            overlayHeight = ReaderWebViewHost.Bounds.Height;

        var availableWidth = Math.Max(80, overlayWidth - 48);
        var availableHeight = Math.Max(80, overlayHeight - 36);
        return (availableWidth, availableHeight);
    }

    private (double MaxWidth, double MaxHeight) GetLinuxReaderTextFallbackPagedImageBounds(SelectableTextBlock textBlock)
    {
        var slotWidth = double.IsFinite(textBlock.MaxWidth) && textBlock.MaxWidth > 0
            ? textBlock.MaxWidth
            : 0;
        if (slotWidth <= 0)
        {
            var contentWidth = ReaderLinuxTextFallbackPagedContent.Bounds.Width;
            if (!double.IsFinite(contentWidth) || contentWidth <= 0)
                contentWidth = ReaderLinuxTextFallbackPagedRoot.Bounds.Width;
            if (!double.IsFinite(contentWidth) || contentWidth <= 0)
                contentWidth = ReaderLinuxTextFallbackOverlay.Bounds.Width;
            slotWidth = _readerLayout.TwoPageMode
                ? Math.Max(80, (contentWidth - 28) / 2)
                : Math.Max(80, Math.Min(_readerLayout.MaxWidth, contentWidth));
        }

        var slotHeight = ReaderLinuxTextFallbackPagedRoot.Bounds.Height;
        if (!double.IsFinite(slotHeight) || slotHeight <= 0)
            slotHeight = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        slotHeight -= ReaderLinuxTextFallbackPagedRoot.Padding.Top + ReaderLinuxTextFallbackPagedRoot.Padding.Bottom;
        return (Math.Max(80, slotWidth), Math.Max(80, slotHeight));
    }

    private void ClearLinuxReaderTextFallbackImages()
    {
        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageRight.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
        foreach (var image in ReaderLinuxTextFallbackImages)
            image.Dispose();
        ReaderLinuxTextFallbackImages.Clear();
        ReaderLinuxTextFallbackBlocks.Clear();
    }
}
