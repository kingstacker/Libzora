using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Linux plain-text fallback content: pagination, blocks, images,
/// annotations and selection synchronisation.
/// </summary>
public partial class MainWindow
{
    private static List<string> PaginateReaderPlainText(
        string text,
        double lineUnits,
        int linesPerPage)
    {
        var pages = new List<string>();
        var page = new StringBuilder();
        var line = new StringBuilder();
        var lines = 0;

        void CommitLine()
        {
            if (page.Length > 0) page.AppendLine();
            page.Append(line);
            line.Clear();
            lines++;
            if (lines < linesPerPage) return;
            pages.Add(page.ToString().TrimEnd());
            page.Clear();
            lines = 0;
        }

        void CommitBlankLine()
        {
            if (line.Length > 0) CommitLine();
            if (page.Length > 0 && lines + 1 < linesPerPage)
            {
                page.AppendLine();
                lines++;
            }
        }

        var paragraphs = System.Text.RegularExpressions.Regex.Split(
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(),
            @"\n{2,}");
        foreach (var paragraph in paragraphs)
        {
            var linesInParagraph = paragraph.Split('\n');
            if (linesInParagraph.All(lineText => string.IsNullOrWhiteSpace(lineText)))
            {
                CommitBlankLine();
                continue;
            }

            foreach (var paragraphLine in linesInParagraph)
            {
                var remaining = paragraphLine.Trim();
                if (remaining.Length == 0)
                {
                    CommitBlankLine();
                    continue;
                }

                var currentUnits = 0d;
                for (var index = 0; index < remaining.Length; index++)
                {
                    var ch = remaining[index];
                    var unit = GetReaderPlainTextCharUnits(ch);
                    if (line.Length > 0 && currentUnits + unit > lineUnits)
                    {
                        CommitLine();
                        currentUnits = 0;
                    }
                    line.Append(ch);
                    currentUnits += unit;
                }
                CommitLine();
            }
            CommitBlankLine();
        }

        if (line.Length > 0) CommitLine();
        if (page.Length > 0) pages.Add(page.ToString().TrimEnd());
        if (pages.Count == 0) pages.Add(string.Empty);
        return pages;
    }

    private static List<string> PaginateReaderPlainTextWithMeasuredLayout(
        string text,
        double pageWidth,
        int linesPerPage,
        double fontSize,
        double lineHeight,
        FontFamily fontFamily)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            return [string.Empty];

        try
        {
            var layout = new TextLayout(
                normalized,
                new Typeface(fontFamily, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal),
                Math.Max(1, fontSize),
                Brushes.Transparent,
                TextAlignment.Left,
                TextWrapping.Wrap,
                TextTrimming.None,
                null,
                FlowDirection.LeftToRight,
                Math.Max(1, pageWidth),
                double.PositiveInfinity,
                Math.Max(1, lineHeight),
                0,
                0,
                null,
                null);
            var lines = layout.TextLines
                .Select(line =>
                {
                    var start = Math.Clamp(line.FirstTextSourceIndex, 0, normalized.Length);
                    var end = Math.Clamp(start + line.Length, start, normalized.Length);
                    return (Start: start, End: end);
                })
                .Where(line => line.End > line.Start)
                .ToArray();
            if (lines.Length == 0)
                return [string.Empty];

            var pages = new List<string>();
            var safeLinesPerPage = Math.Max(1, linesPerPage);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex += safeLinesPerPage)
            {
                var start = lines[lineIndex].Start;
                var nextLineIndex = Math.Min(lineIndex + safeLinesPerPage, lines.Length);
                var end = nextLineIndex < lines.Length
                    ? lines[nextLineIndex].Start
                    : normalized.Length;
                end = Math.Clamp(end, start, normalized.Length);
                var page = normalized[start..end].TrimEnd();
                if (page.Length > 0)
                    pages.Add(page);
            }

            return pages.Count > 0 ? pages : [string.Empty];
        }
        catch
        {
            // TextLayout is unavailable before an Avalonia text backend is
            // initialized (for example during a headless page estimate). The
            // deterministic unit fallback still keeps every character and
            // is used only for that exceptional path.
            return PaginateReaderPlainText(
                normalized,
                Math.Max(8, pageWidth / Math.Max(1, fontSize)),
                linesPerPage);
        }
    }

    private void UpdateLinuxReaderTextFallbackBlocks(
        IReadOnlyList<ReaderLinuxTextFallbackRawBlock> rawBlocks,
        string fallbackText,
        double fontSize)
    {
        ClearLinuxReaderTextFallbackImages();
        ReaderLinuxTextFallbackBlocks.Clear();
        var (maxWidth, maxHeight) = GetLinuxReaderTextFallbackImageBounds();
        var imageCache = new Dictionary<string, ReaderLinuxTextFallbackImage>(StringComparer.OrdinalIgnoreCase);
        var lineHeight = fontSize * _readerLayout.LineHeight;
        var textMaxWidth = Math.Max(320, _readerLayout.MaxWidth);
        // Mirror the WebView cover rule: a bare "封面" caption next to the
        // cover raster is metadata, not body copy. Drop it only when the
        // chapter actually renders an image so real chapters keep their text.
        var hasImageBlock = rawBlocks.Any(block => !string.IsNullOrWhiteSpace(block.ImagePath));
        var textOffset = 0;
        var pendingText = new StringBuilder();
        var pendingFootnotes = new List<ReaderLinuxTextFallbackFootnote>();
        var pendingOffset = 0;
        var pendingChapterTitle = false;

        void FlushTextBlock()
        {
            var text = NormalizeReaderPlainText(pendingText.ToString());
            pendingText.Clear();
            if (string.IsNullOrWhiteSpace(text))
            {
                pendingFootnotes.Clear();
                pendingChapterTitle = false;
                return;
            }

            ReaderLinuxTextFallbackBlocks.Add(pendingFootnotes.Count == 0
                ? new ReaderLinuxTextFallbackBlock(
                    text,
                    Math.Clamp(pendingOffset, 0, fallbackText.Length),
                    fontSize,
                    lineHeight,
                    textMaxWidth,
                    pendingChapterTitle)
                : new ReaderLinuxTextFallbackBlock(
                    text,
                    Math.Clamp(pendingOffset, 0, fallbackText.Length),
                    fontSize,
                    lineHeight,
                    textMaxWidth,
                    pendingFootnotes.ToArray(),
                    pendingChapterTitle));
            pendingFootnotes.Clear();
            pendingChapterTitle = false;
        }

        foreach (var rawBlock in rawBlocks)
        {
            if (rawBlock.IsParagraphBoundary
                && string.IsNullOrWhiteSpace(rawBlock.Text)
                && string.IsNullOrWhiteSpace(rawBlock.ImagePath)
                && string.IsNullOrWhiteSpace(rawBlock.FootnoteHref))
            {
                FlushTextBlock();
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rawBlock.ImagePath))
            {
                FlushTextBlock();
                if (!imageCache.TryGetValue(rawBlock.ImagePath, out var image))
                {
                    try
                    {
                        image = new ReaderLinuxTextFallbackImage(
                            new Bitmap(rawBlock.ImagePath),
                            maxWidth,
                            maxHeight);
                        imageCache[rawBlock.ImagePath] = image;
                        ReaderLinuxTextFallbackImages.Add(image);
                    }
                    catch
                    {
                        continue;
                    }
                }
                ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(image));
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rawBlock.FootnoteHref))
            {
                // Keep chapter headings as a plain, centered text run. A
                // publisher footnote attached to the heading itself must not
                // turn the whole heading into an inline-control layout, which
                // would lose its bold/centered rendering on Linux.
                if (pendingChapterTitle && pendingText.Length > 0)
                    continue;
                if (pendingText.Length == 0)
                    pendingOffset = textOffset;
                pendingText.Append(ReaderLinuxTextFallbackFootnoteMarker);
                pendingFootnotes.Add(new ReaderLinuxTextFallbackFootnote(
                    string.IsNullOrWhiteSpace(rawBlock.FootnoteLabel) ? UiText.Get("注") : rawBlock.FootnoteLabel.Trim(),
                    rawBlock.FootnoteHref));
                continue;
            }

            var normalizedText = NormalizeReaderPlainText(rawBlock.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedText)) continue;
            var textParts = System.Text.RegularExpressions.Regex.Split(
                normalizedText,
                @"(?:\r\n|\n){2,}");
            for (var partIndex = 0; partIndex < textParts.Length; partIndex++)
            {
                var text = textParts[partIndex].Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (hasImageBlock
                    && string.Equals(text.Replace(" ", string.Empty), "封面", StringComparison.Ordinal))
                    continue;

                var start = IndexOfReaderPlainTextBlock(fallbackText, text, textOffset);
                if (start < 0) start = textOffset;
                if (pendingText.Length == 0)
                    pendingOffset = start;
                pendingText.Append(text);
                pendingChapterTitle |= rawBlock.IsChapterTitle && partIndex == 0;
                textOffset = Math.Clamp(start + text.Length, 0, fallbackText.Length);
                if (partIndex < textParts.Length - 1 || rawBlock.IsParagraphBoundary)
                    FlushTextBlock();
            }
        }

        FlushTextBlock();
        PromoteLinuxReaderFirstChapterTitle();

        if (string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "fallback-blocks "
                + string.Join(
                    " || ",
                    ReaderLinuxTextFallbackBlocks.Take(10).Select(block =>
                        $"title={block.IsChapterTitle},text={block.Text}")));
        }

        if (ReaderLinuxTextFallbackBlocks.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
        {
            ReaderLinuxTextFallbackBlocks.Add(new ReaderLinuxTextFallbackBlock(
                fallbackText,
                0,
                fontSize,
                lineHeight,
                textMaxWidth));
        }

        ApplyLinuxReaderTextFallbackAnnotationRanges();
        UpdateLinuxReaderTextFallbackBlockWidths();
    }

    private IReadOnlyList<ReaderLinuxTextFallbackAnnotationRange> GetLinuxReaderTextFallbackAnnotationRanges(
        int textOffset,
        int textLength)
    {
        if (textLength <= 0) return [];
        var chapterPath = GetReaderChapterPath();
        if (string.IsNullOrWhiteSpace(chapterPath)) return [];
        var textEnd = textOffset + textLength;
        return ReaderAnnotations
            .Where(item => string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.EndOffset > textOffset && item.StartOffset < textEnd)
            .Select(item =>
            {
                var start = Math.Max(textOffset, item.StartOffset);
                var end = Math.Min(textEnd, Math.Max(item.StartOffset, item.EndOffset));
                return new ReaderLinuxTextFallbackAnnotationRange(
                    start - textOffset,
                    Math.Max(0, end - start),
                    NormalizeReaderAnnotationStyle(item.UnderlineStyle),
                    NormalizeReaderAnnotationColor(item.Color));
            })
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private void ApplyLinuxReaderTextFallbackAnnotationRanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var block in ReaderLinuxTextFallbackBlocks.Where(item => item.IsText))
        {
            block.SetAnnotationRanges(GetLinuxReaderTextFallbackAnnotationRanges(
                block.TextOffset,
                block.Text.Length));
        }

        if (IsLinuxReaderTextFallbackActive() && _readerLayout.FlowMode == 1)
            RenderLinuxReaderTextFallbackPage();
    }

    private void PromoteLinuxReaderFirstChapterTitle()
    {
        var expectedTitle = NormalizeReaderPlainTextLine(
            _readerLinuxTextFallbackTargetTitle
                ?? GetReaderChapterDisplayName(_readerChapterIndex));
        if (string.IsNullOrWhiteSpace(expectedTitle)) return;

        var firstTextIndex = -1;
        for (var index = 0; index < ReaderLinuxTextFallbackBlocks.Count; index++)
        {
            if (ReaderLinuxTextFallbackBlocks[index].IsText)
            {
                firstTextIndex = index;
                break;
            }
        }

        if (firstTextIndex < 0) return;
        var block = ReaderLinuxTextFallbackBlocks[firstTextIndex];
        if (block.IsChapterTitle) return;
        var firstLine = NormalizeReaderPlainTextLine(
            NormalizeReaderPlainText(block.Text)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault());
        if (!string.Equals(firstLine, expectedTitle, StringComparison.OrdinalIgnoreCase))
            return;

        ReaderLinuxTextFallbackBlocks[firstTextIndex] = block.HasInlineFootnotes
            ? new ReaderLinuxTextFallbackBlock(
                block.Text,
                block.TextOffset,
                block.FontSize,
                block.LineHeight,
                block.MaxWidth,
                block.InlineFootnotes,
                isChapterTitle: true)
            : new ReaderLinuxTextFallbackBlock(
                block.Text,
                block.TextOffset,
                block.FontSize,
                block.LineHeight,
                block.MaxWidth,
                isChapterTitle: true);
    }

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

    private void ClearLinuxReaderTextFallbackSelectionState()
    {
        ClearLinuxReaderTextFallbackVisualSelection();
        HideReaderSelectionPopup();
        _readerPendingSelection = null;
        _readerPendingSelectionStartOffset = 0;
        _readerPendingSelectionEndOffset = 0;
        _readerPendingSelectionPrefix = string.Empty;
        _readerPendingSelectionSuffix = string.Empty;
        _selectedReaderAnnotation = null;
    }

    private void ClearLinuxReaderTextFallbackVisualSelection()
    {
        var blocks = new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct();

        foreach (var block in blocks)
            block.ClearSelection();
        ReaderLinuxTextFallbackPageVertical.ClearSelection();
    }

    private bool HasLinuxReaderTextFallbackSelection()
    {
        if (!string.IsNullOrWhiteSpace(_readerPendingSelection)
            || ReaderSelectionHostPopup.IsOpen)
        {
            return true;
        }

        return new[]
            {
                ReaderLinuxTextFallbackText,
                ReaderLinuxTextFallbackPageLeft,
                ReaderLinuxTextFallbackPageRight
            }
            .Concat(ReaderLinuxTextFallbackOverlay
                .GetVisualDescendants()
                .OfType<SelectableTextBlock>())
            .Distinct()
            .Any(block => block.SelectionStart != block.SelectionEnd)
            || (ReaderLinuxTextFallbackPageVertical.IsVisible
                && ReaderLinuxTextFallbackPageVertical.SelectionStart
                    != ReaderLinuxTextFallbackPageVertical.SelectionEnd);
    }

    private void SyncLinuxReaderTextFallbackSelectionState(Point? placementPoint = null, double? selectionBottom = null)
    {
        if (!IsLinuxReaderTextFallbackActive())
            return;

        if (_readerLayout.FlowMode == 0)
        {
            ClearLinuxReaderTextFallbackSelectionState();
            return;
        }

        if (ReaderLinuxTextFallbackPageVertical.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromVertical())
        {
            var anchor = GetVerticalReaderSelectionAnchorRect();
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageLeft,
                GetLinuxReaderTextFallbackPageText(0),
                _readerLinuxTextFallbackPageIndex))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageLeft);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        if (ReaderLinuxTextFallbackPageRight.IsVisible
            && TrySyncLinuxReaderTextFallbackSelectionFromBlock(
                ReaderLinuxTextFallbackPageRight,
                GetLinuxReaderTextFallbackPageText(1),
                _readerLinuxTextFallbackPageIndex + 1))
        {
            var anchor = GetReaderSelectionAnchorRect(ReaderLinuxTextFallbackPageRight);
            ShowReaderSelectionPopup(
                anchor?.Position ?? placementPoint,
                anchor?.Bottom ?? selectionBottom);
            return;
        }

        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ScheduleLinuxReaderTextFallbackSelectionSync(
        Point placementPoint,
        double? selectionBottom = null)
    {
        var sequence = ++_readerLinuxTextFallbackSelectionSyncSequence;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (sequence != _readerLinuxTextFallbackSelectionSyncSequence
                    || !IsLinuxReaderTextFallbackActive()
                    || ReaderSelectionHostPopup.IsOpen)
                    return;

                // SelectableTextBlock occasionally commits SelectionEnd after
                // its PointerReleased route has completed. Re-read it on the
                // next input frame so a valid drag never loses its toolbar.
                SyncLinuxReaderTextFallbackSelectionState(
                    placementPoint,
                    selectionBottom);
            },
            DispatcherPriority.Input);
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        string pageText,
        int pageIndex)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);
        var pageOffset = _readerLayout.FlowMode == 0 ? 0 : GetLinuxReaderTextFallbackPageOffset(pageIndex);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = pageOffset + start;
        _readerPendingSelectionEndOffset = pageOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetReaderSelectionAnchorRect(SelectableTextBlock block)
    {
        try
        {
            var start = Math.Max(0, Math.Min(block.SelectionStart, block.SelectionEnd));
            var end = Math.Max(0, Math.Max(block.SelectionStart, block.SelectionEnd));
            if (block.TextLayout is not { } layout)
                return null;

            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var boundsBottom = double.NegativeInfinity;
            var lineTop = 0d;
            foreach (var line in layout.TextLines)
            {
                var lineStart = Math.Max(0, line.FirstTextSourceIndex);
                var lineEnd = Math.Max(lineStart, lineStart + line.Length);
                var selectedStart = Math.Max(start, lineStart);
                var selectedEnd = Math.Min(end, lineEnd);
                if (selectedEnd <= selectedStart)
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                var segmentLeft = double.PositiveInfinity;
                var segmentRight = double.NegativeInfinity;
                foreach (var textBounds in line.GetTextBounds(
                             selectedStart,
                             selectedEnd - selectedStart))
                {
                    segmentLeft = Math.Min(segmentLeft, textBounds.Rectangle.Left);
                    segmentRight = Math.Max(segmentRight, textBounds.Rectangle.Right);
                }

                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight))
                {
                    var startHit = layout.HitTestTextPosition(selectedStart);
                    var endHit = layout.HitTestTextPosition(Math.Max(selectedStart, selectedEnd - 1));
                    segmentLeft = selectedStart <= lineStart
                        ? line.Start
                        : startHit.X;
                    segmentRight = selectedEnd >= lineEnd
                        ? line.Start + line.WidthIncludingTrailingWhitespace
                        : endHit.Right;
                }
                if (!double.IsFinite(segmentLeft)
                    || !double.IsFinite(segmentRight)
                    || !double.IsFinite(lineTop)
                    || !double.IsFinite(line.Height))
                {
                    lineTop += Math.Max(0, line.Height);
                    continue;
                }

                left = Math.Min(left, segmentLeft);
                top = Math.Min(top, lineTop);
                right = Math.Max(right, Math.Max(segmentLeft, segmentRight));
                boundsBottom = Math.Max(boundsBottom, lineTop + line.Height);
                lineTop += Math.Max(0, line.Height);
            }

            if (!double.IsFinite(left)
                || !double.IsFinite(top)
                || !double.IsFinite(right)
                || !double.IsFinite(boundsBottom))
                return null;

            var topLeft = block.TranslatePoint(new Point(left, top), ReaderWebViewHost);
            var bottomRight = block.TranslatePoint(new Point(right, boundsBottom), ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromBlock(
        SelectableTextBlock block,
        ReaderLinuxTextFallbackBlock textBlock)
    {
        var selectedText = block.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageText = textBlock.Text;
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = textBlock.TextOffset + start;
        _readerPendingSelectionEndOffset = textBlock.TextOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    // Vertical pages keep selection state on the custom drawing control; the
    // offsets map onto the raw page text exactly like the horizontal slots.
    private bool TrySyncLinuxReaderTextFallbackSelectionFromVertical()
    {
        var control = ReaderLinuxTextFallbackPageVertical;
        var selectedText = control.SelectedText?.Trim();
        if (string.IsNullOrWhiteSpace(selectedText))
            return false;

        var pageIndex = _readerLinuxTextFallbackPageIndex;
        if (pageIndex < 0 || pageIndex >= _readerLinuxTextFallbackPageItems.Count)
            return false;
        var item = _readerLinuxTextFallbackPageItems[pageIndex];
        if (item.IsImage)
            return false;

        var pageText = item.Text;
        var start = Math.Min(control.SelectionStart, control.SelectionEnd);
        var end = Math.Max(control.SelectionStart, control.SelectionEnd);
        start = Math.Clamp(start, 0, pageText.Length);
        end = Math.Clamp(end, 0, pageText.Length);

        _readerPendingSelection = selectedText;
        _readerPendingSelectionStartOffset = item.PaginationOffset + start;
        _readerPendingSelectionEndOffset = item.PaginationOffset + end;
        _readerPendingSelectionPrefix = pageText[..start];
        _readerPendingSelectionSuffix = end < pageText.Length ? pageText[end..] : string.Empty;
        _selectedReaderAnnotation = null;
        return true;
    }

    private Rect? GetVerticalReaderSelectionAnchorRect()
    {
        try
        {
            var control = ReaderLinuxTextFallbackPageVertical;
            var start = Math.Max(0, Math.Min(control.SelectionStart, control.SelectionEnd));
            var end = Math.Max(0, Math.Max(control.SelectionStart, control.SelectionEnd));
            if (end <= start
                || control.GetRangeAnchorRect(start, end) is not { } localRect)
            {
                return null;
            }

            var topLeft = control.TranslatePoint(localRect.TopLeft, ReaderWebViewHost);
            var bottomRight = control.TranslatePoint(localRect.BottomRight, ReaderWebViewHost);
            return topLeft is { } topPoint && bottomRight is { } bottomPoint
                ? new Rect(topPoint, bottomPoint)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private bool TrySyncLinuxReaderTextFallbackSelectionFromVisual(
        Visual visual,
        Point placementPoint)
    {
        Visual? current = visual;
        while (current is not null)
        {
            if (current is SelectableTextBlock block
                && block.DataContext is ReaderLinuxTextFallbackBlock textBlock
                && TrySyncLinuxReaderTextFallbackSelectionFromBlock(block, textBlock))
            {
                var anchor = GetReaderSelectionAnchorRect(block);
                ShowReaderSelectionPopup(
                    anchor?.Position ?? placementPoint,
                    anchor?.Bottom);
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

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
