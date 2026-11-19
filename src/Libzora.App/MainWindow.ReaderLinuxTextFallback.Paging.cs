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
/// Fallback page rebuilding and anchors.
/// </summary>
public partial class MainWindow
{
    private void RebuildLinuxReaderTextFallbackPages(int? reflowAnchor = null)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        if (_readerLayout.FlowMode != 1)
        {
            _readerLinuxTextFallbackPages.Clear();
            _readerLinuxTextFallbackPageItems.Clear();
            return;
        }

        var fontSize = Math.Max(10, 16d * _readerLayout.FontScale);
        var lineHeight = Math.Max(fontSize + 2, fontSize * _readerLayout.LineHeight);
        var overlayWidth = Math.Max(320, ReaderLinuxTextFallbackOverlay.Bounds.Width);
        var overlayHeight = Math.Max(320, ReaderLinuxTextFallbackOverlay.Bounds.Height);
        var pageInsets = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
            overlayWidth,
            overlayHeight,
            _readerLayout.BodyPadding,
            _readerLayout.MaxWidth);
        ReaderLinuxTextFallbackPagedRoot.Padding = new Thickness(
            pageInsets.Horizontal,
            pageInsets.Vertical);
        // Use the already-arranged paged surface when it is available. The
        // overlay can retain its previous width for a compositor frame while
        // the TOC opens/closes; using that stale width lets a centered page
        // extend into the TOC column before the next reflow pass.
        var availableContentWidth = Math.Max(
            1,
            overlayWidth - pageInsets.Horizontal * 2);
        // In vertical mode the page control is centered inside this grid and
        // its explicit Width is assigned below. Reading the grid's current
        // Bounds.Width here feeds the previous page width back into the next
        // pagination pass, so a narrow page keeps shrinking and leaves a
        // large unused area beside the text. Use the actual viewport width
        // for vertical pages; the horizontal path can still reuse the
        // arranged width for two-page spreads.
        var arrangedContentWidth = _readerLayout.VerticalWriting
            ? availableContentWidth
            : ReaderLinuxTextFallbackPagedContent.Bounds.Width;
        if (!double.IsFinite(arrangedContentWidth) || arrangedContentWidth <= 0)
            arrangedContentWidth = availableContentWidth;
        arrangedContentWidth = Math.Clamp(
            arrangedContentWidth,
            1,
            availableContentWidth);
        var pageWidth = _readerLayout.TwoPageMode
            ? Math.Max(1, (arrangedContentWidth - 28) / 2)
            : Math.Max(1, Math.Min(_readerLayout.MaxWidth, arrangedContentWidth));
        var pageHeight = Math.Max(
            180,
            overlayHeight - pageInsets.Vertical * 2);
        var linesPerPage = Math.Max(4, (int)Math.Floor(pageHeight / lineHeight));
        // Vertical writing paginates on the same column grid the drawing
        // surface uses, so a page boundary always lands on a column start.
        var verticalGrid = _readerLayout.VerticalWriting
            ? ReaderLinuxTextFallbackVerticalPage.ComputeGrid(
                pageWidth,
                pageHeight,
                fontSize,
                lineHeight)
            : default;
        // Use the real Avalonia text formatter for the visible fallback pages
        // so the split point is the same one used by the wrapping control.
        _readerLinuxTextFallbackPages.Clear();
        _readerLinuxTextFallbackPageItems.Clear();

        var textBuilder = new StringBuilder();
        var footnotes = new List<ReaderLinuxTextFallbackFootnote>();
        var textOffset = 0;
        var chapterTitleStart = -1;
        var chapterTitleLength = 0;
        var previousTextWasTitle = false;
        var paginationStreamOffset = 0;

        void FlushTextPages()
        {
            if (textBuilder.Length == 0) return;

            var mixedText = textBuilder.ToString();
            var measuredPages = new List<(string Text, int Start, bool StartsParagraph)>();

            void AddMeasuredPages(string segment, int segmentStart)
            {
                if (string.IsNullOrWhiteSpace(segment)) return;
                List<string> segmentPageTexts;
                if (_readerLayout.VerticalWriting)
                {
                    var startsWithParagraph = segmentStart == 0
                        || (segmentStart > 0 && mixedText[segmentStart - 1] == '\n');
                    segmentPageTexts = ReaderLinuxVerticalPagingPolicy.Paginate(
                        segment,
                        verticalGrid.CharsPerColumn,
                        verticalGrid.ColumnsPerPage,
                        _readerLayout.ParagraphIndent,
                        startsWithParagraph).Select(page => page.Text).ToList();
                }
                else
                {
                    segmentPageTexts = PaginateReaderPlainTextWithMeasuredLayout(
                        segment,
                        pageWidth,
                        linesPerPage,
                        fontSize,
                        lineHeight,
                        ReaderLinuxTextFallbackPageLeft.FontFamily);
                }
                var searchOffset = 0;
                foreach (var segmentPage in segmentPageTexts)
                {
                    var start = segment.IndexOf(
                        segmentPage,
                        Math.Clamp(searchOffset, 0, segment.Length),
                        StringComparison.Ordinal);
                    if (start < 0)
                        start = searchOffset;
                    var absoluteStart = segmentStart + Math.Max(0, start);
                    measuredPages.Add((
                        segmentPage,
                        absoluteStart,
                        absoluteStart == 0 || mixedText[absoluteStart - 1] == '\n'));
                    searchOffset = Math.Clamp(start + segmentPage.Length, 0, segment.Length);
                }
            }

            var localReflowAnchor = reflowAnchor is { } anchor
                ? anchor - paginationStreamOffset
                : -1;
            if (localReflowAnchor > 0 && localReflowAnchor < mixedText.Length)
            {
                // Preserve the exact previous first line as the new first
                // line. Reflowing the whole chapter and merely selecting the
                // page that contains the anchor can place it halfway down.
                AddMeasuredPages(mixedText[..localReflowAnchor], 0);
                AddMeasuredPages(mixedText[localReflowAnchor..], localReflowAnchor);
            }
            else
            {
                AddMeasuredPages(mixedText, 0);
            }
            var footnoteIndex = 0;
            foreach (var measuredPage in measuredPages)
            {
                var page = measuredPage.Text;
                var pageStart = measuredPage.Start;

                var notesOnPage = new List<ReaderLinuxTextFallbackFootnote>();
                foreach (var character in page)
                {
                    if (character != ReaderLinuxTextFallbackFootnoteMarker || footnoteIndex >= footnotes.Count)
                        continue;
                    notesOnPage.Add(footnotes[footnoteIndex++]);
                }

                var localTitleStart = -1;
                var localTitleLength = 0;
                if (chapterTitleStart >= 0 && chapterTitleLength > 0)
                {
                    var titleEnd = chapterTitleStart + chapterTitleLength;
                    var overlapStart = Math.Max(chapterTitleStart, pageStart);
                    var overlapEnd = Math.Min(titleEnd, pageStart + page.Length);
                    if (overlapEnd > overlapStart)
                    {
                        localTitleStart = overlapStart - pageStart;
                        localTitleLength = overlapEnd - overlapStart;
                    }
                }

                _readerLinuxTextFallbackPages.Add(page);
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    page,
                    Math.Max(0, textOffset),
                    null,
                    notesOnPage,
                    localTitleStart,
                    localTitleLength,
                    paginationStreamOffset + Math.Max(0, pageStart),
                    measuredPage.StartsParagraph));
            }

            paginationStreamOffset += mixedText.Length + 1;
            textBuilder.Clear();
            footnotes.Clear();
            chapterTitleStart = -1;
            chapterTitleLength = 0;
            previousTextWasTitle = false;
        }

        foreach (var block in ReaderLinuxTextFallbackBlocks)
        {
            if (block.Image is not null)
            {
                FlushTextPages();
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    string.Empty,
                    0,
                    block.Image,
                    PaginationOffset: paginationStreamOffset));
                paginationStreamOffset++;
                continue;
            }

            if (block.IsFootnote)
            {
                if (textBuilder.Length == 0)
                    textOffset = 0;
                textBuilder.Append(ReaderLinuxTextFallbackFootnoteMarker);
                footnotes.Add(new ReaderLinuxTextFallbackFootnote(
                    block.FootnoteLabel,
                    block.FootnoteHref));
                continue;
            }

            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            if (textBuilder.Length == 0)
                textOffset = block.TextOffset;
            else if (textBuilder[^1] != '\n')
            {
                // A normal paragraph starts on the next body line, matching
                // the line grid inside the paragraph. Chapter headings get
                // one additional line before the first body paragraph.
                var separatorLines = previousTextWasTitle || block.IsChapterTitle ? 2 : 1;
                textBuilder.Append('\n', separatorLines);
            }
            var blockStart = textBuilder.Length;
            textBuilder.Append(block.Text);
            if (block.IsChapterTitle)
            {
                chapterTitleStart = blockStart;
                chapterTitleLength = block.Text.Length;
            }
            previousTextWasTitle = block.IsChapterTitle;
            if (block.HasInlineFootnotes)
                footnotes.AddRange(block.InlineFootnotes);
        }

        FlushTextPages();

        if (_readerLinuxTextFallbackPageItems.Count == 0)
        {
            var fallbackMeasuredPages = new List<(string Text, int Start)>();
            if (_readerLayout.VerticalWriting)
            {
                fallbackMeasuredPages.AddRange(
                    ReaderLinuxVerticalPagingPolicy.Paginate(
                        _readerLinuxTextFallbackText,
                        verticalGrid.CharsPerColumn,
                        verticalGrid.ColumnsPerPage,
                        _readerLayout.ParagraphIndent));
            }
            else
            {
                var searchOffset = Math.Clamp(
                    paginationStreamOffset,
                    0,
                    _readerLinuxTextFallbackText.Length);
                foreach (var pageText in PaginateReaderPlainTextWithMeasuredLayout(
                    _readerLinuxTextFallbackText,
                    pageWidth,
                    linesPerPage,
                    fontSize,
                    lineHeight,
                    ReaderLinuxTextFallbackPageLeft.FontFamily))
                {
                    var start = _readerLinuxTextFallbackText.IndexOf(
                        pageText,
                        searchOffset,
                        StringComparison.Ordinal);
                    if (start < 0) start = searchOffset;
                    fallbackMeasuredPages.Add((pageText, start));
                    searchOffset = Math.Clamp(
                        start + pageText.Length,
                        0,
                        _readerLinuxTextFallbackText.Length);
                }
            }

            _readerLinuxTextFallbackPages = fallbackMeasuredPages
                .Select(page => page.Text)
                .ToList();
            foreach (var page in fallbackMeasuredPages)
            {
                _readerLinuxTextFallbackPageItems.Add(new ReaderLinuxTextFallbackPageItem(
                    page.Text,
                    0,
                    null,
                    PaginationOffset: Math.Max(0, page.Start),
                    StartsParagraph: page.Start == 0
                        || (page.Start > 0 && _readerLinuxTextFallbackText[page.Start - 1] == '\n')));
                paginationStreamOffset = Math.Clamp(
                    page.Start + page.Text.Length,
                    0,
                    _readerLinuxTextFallbackText.Length);
            }
        }

        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        _readerLinuxTextFallbackPageIndex = reflowAnchor is { } anchor
            ? ReaderLinuxTextFallbackPagingPolicy.ResolveAnchorPageIndex(
                _readerLinuxTextFallbackPageItems
                    .Select(item => item.IsImage ? -1 : item.PaginationOffset)
                    .ToArray(),
                anchor,
                spreadSize)
            : ReaderLinuxTextFallbackPagingPolicy.ResolvePageIndex(
                _readerLinuxTextFallbackPageIndex,
                _readerScrollPosition,
                _readerLinuxTextFallbackMoveToChapterEnd,
                GetLinuxReaderTextFallbackPageCount(),
                spreadSize);
        _readerLinuxTextFallbackMoveToChapterEnd = false;

        // Pagination is calculated from this exact rectangle. Keep the
        // selectable controls at that rectangle instead of letting their
        // content-derived DesiredSize change when a selection starts or ends
        // on a line boundary and triggering a second wrap pass.
        ReaderLinuxTextFallbackPageLeft.Width = pageWidth;
        ReaderLinuxTextFallbackPageLeft.MinWidth = pageWidth;
        ReaderLinuxTextFallbackPageLeft.MaxWidth = pageWidth;
        ReaderLinuxTextFallbackPageLeft.Height = pageHeight;
        ReaderLinuxTextFallbackPageLeft.MinHeight = pageHeight;
        ReaderLinuxTextFallbackPageLeft.MaxHeight = pageHeight;
        ReaderLinuxTextFallbackPageRight.Width = pageWidth;
        ReaderLinuxTextFallbackPageRight.MinWidth = pageWidth;
        ReaderLinuxTextFallbackPageRight.MaxWidth = pageWidth;
        ReaderLinuxTextFallbackPageRight.Height = pageHeight;
        ReaderLinuxTextFallbackPageRight.MinHeight = pageHeight;
        ReaderLinuxTextFallbackPageRight.MaxHeight = pageHeight;
        if (_readerLayout.VerticalWriting)
        {
            // Vertical pages draw on the same rectangle the paginator used.
            // A fixed size keeps the column grid identical across renders.
            ReaderLinuxTextFallbackPageVertical.Width = pageWidth;
            ReaderLinuxTextFallbackPageVertical.MinWidth = pageWidth;
            ReaderLinuxTextFallbackPageVertical.MaxWidth = pageWidth;
            ReaderLinuxTextFallbackPageVertical.Height = pageHeight;
            ReaderLinuxTextFallbackPageVertical.MinHeight = pageHeight;
            ReaderLinuxTextFallbackPageVertical.MaxHeight = pageHeight;
        }
        RenderLinuxReaderTextFallbackPage();
    }
}
