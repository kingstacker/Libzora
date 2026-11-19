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
/// Linux plain-text fallback surface: activation, scroll state and page
/// rendering.
/// </summary>
public partial class MainWindow
{
    private async Task UpdateLinuxReaderTextFallbackAsync(
        CancellationToken cancellationToken,
        IReaderHost? host = null)
    {
        // The native WebKit surface is authoritative for every Linux reading
        // mode, including vertical writing. The old Avalonia text surface is
        // intentionally kept unreachable so Linux uses the same HTML/CSS
        // layout engine for horizontal and vertical EPUB content.
        if (!UseLinuxPlainTextRecoveryFallback)
        {
            HideLinuxReaderTextFallback();
            if (OperatingSystem.IsLinux() && !_readerIsPdf)
                SetReaderHostLayer(revealActiveHost: true);
            return;
        }

        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || _readerDocument is null
            || _readerChapterIndex < 0
            || _readerChapterIndex >= _readerDocument.Chapters.Count)
        {
            HideLinuxReaderTextFallback();
            return;
        }

        var chapterPath = _readerDocument.Chapters[_readerChapterIndex];
        if (!File.Exists(chapterPath))
        {
            HideLinuxReaderTextFallback();
            return;
        }

        // This branch is retained only for diagnostics and historical tests;
        // the production reader never enters it and always shows WebKit.

        var targetTitle = _readerLinuxTextFallbackTargetTitle
            ?? GetReaderChapterDisplayName(_readerChapterIndex);
        var fragment = string.IsNullOrWhiteSpace(targetTitle)
            ? null
            : DecodeReaderFragment(_readerCurrentFragment);
        var endFragment = DecodeReaderFragment(_readerLinuxTextFallbackEndFragment);
        var content = await Task.Run(
            () => ExtractReaderFallbackContent(chapterPath, fragment, targetTitle, endFragment),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                Environment.GetEnvironmentVariable("LIBZORA_TRACE_FALLBACK_TITLE"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"fallback-content target={targetTitle} fragment={fragment} end={endFragment} blocks="
                + string.Join(
                    " || ",
                    content.Blocks.Take(10).Select(block =>
                        $"title={block.IsChapterTitle},boundary={block.IsParagraphBoundary},text={block.Text}")));
        }

        var text = content.Text;
        if (string.IsNullOrWhiteSpace(text))
            text = GetReaderChapterDisplayName(_readerChapterIndex);

        var fontSize = 16d * _readerLayout.FontScale;
        var visible = !string.IsNullOrWhiteSpace(text)
            || content.Blocks.Any(block =>
                !string.IsNullOrWhiteSpace(block.ImagePath)
                || !string.IsNullOrWhiteSpace(block.FootnoteHref));
        _readerLinuxTextFallbackUpdating = true;
        try
        {
            ClearLinuxReaderTextFallbackSelectionState();
            _readerLinuxTextFallbackText = text;
            ReaderLinuxTextFallbackText.Text = text;
            ReaderLinuxTextFallbackText.FontSize = fontSize;
            ReaderLinuxTextFallbackText.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackText.MaxWidth = Math.Max(320, _readerLayout.MaxWidth);
            ReaderLinuxTextFallbackPageLeft.FontSize = fontSize;
            ReaderLinuxTextFallbackPageLeft.LineHeight = fontSize * _readerLayout.LineHeight;
            ReaderLinuxTextFallbackPageRight.FontSize = fontSize;
            ReaderLinuxTextFallbackPageRight.LineHeight = fontSize * _readerLayout.LineHeight;
            UpdateLinuxReaderTextFallbackBlocks(content.Blocks, text, fontSize);
            if (_readerLayout.FlowMode == 0)
                ReaderLinuxTextFallbackScroll.Offset = new Vector(0, Math.Max(0, _readerScrollPosition));
            else
                _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackMoveToChapterEnd || _readerScrollPosition < 0
                    ? -1
                    : (int)Math.Round(Math.Max(0, _readerScrollPosition));
            ReaderLinuxTextFallbackOverlay.IsVisible = visible;
            UpdateLinuxReaderTextFallbackMode();
        }
        finally
        {
            _readerLinuxTextFallbackUpdating = false;
        }

        if (visible)
        {
            ReaderActiveHostSlot.IsVisible = false;
            ReaderActiveHostSlot.IsHitTestVisible = false;
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!IsLinuxReaderTextFallbackActive()) return;
                    ClampLinuxReaderTextFallbackOffset();
                    RebuildLinuxReaderTextFallbackPages();
                    SyncLinuxReaderTextFallbackState(saveProgress: false);
                    PrimeReaderContinuousEdgeTracking();
                    ReaderLinuxTextFallbackOverlay.Focus();
                },
                DispatcherPriority.Loaded);
        }
    }

    private void HideLinuxReaderTextFallback()
    {
        ReaderLinuxTextFallbackOverlay.IsVisible = false;
        ReaderLinuxTextFallbackText.Text = string.Empty;
        ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
        ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
        ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
        ReaderLinuxTextFallbackPageRight.Text = string.Empty;
        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageRight.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteLeft.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
        ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
        ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
        ReaderLinuxTextFallbackPageVertical.FootnoteHrefResolver = null;
        ReaderLinuxTextFallbackPageVertical.Text = string.Empty;
        ReaderLinuxTextFallbackPageVertical.SelectionStart = 0;
        ReaderLinuxTextFallbackPageVertical.SelectionEnd = 0;
        ReaderLinuxTextFallbackPageVertical.IsVisible = false;
        _readerLinuxTextFallbackText = string.Empty;
        _readerLinuxTextFallbackPages.Clear();
        _readerLinuxTextFallbackPageItems.Clear();
        _readerLinuxTextFallbackMoveToChapterEnd = false;
        _readerLinuxTextFallbackEndFragment = null;
        _readerLinuxTextFallbackPendingReflowAnchor = null;
        _readerLinuxTextFallbackReflowSequence++;
        ClearLinuxReaderTextFallbackImages();
        ClearLinuxReaderTextFallbackSelectionState();
    }

    private void ReaderLinuxTextFallbackScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_readerLinuxTextFallbackUpdating) return;
        if (_readerLayout.FlowMode != 0) return;
        if (_readerLinuxTextFallbackSelectionScrollLocked)
        {
            RestoreLinuxTextFallbackSelectionScrollOffset();
            return;
        }
        SyncLinuxReaderTextFallbackScrollState(saveProgress: true);
    }

    private void RestoreLinuxTextFallbackSelectionScrollOffset()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked
            || _readerLinuxTextFallbackRestoringSelectionScroll)
        {
            return;
        }

        var target = new Vector(
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.X),
            Math.Max(0, _readerLinuxTextFallbackSelectionScrollOffset.Y));
        var current = ReaderLinuxTextFallbackScroll.Offset;
        if (Math.Abs(current.X - target.X) <= 0.5
            && Math.Abs(current.Y - target.Y) <= 0.5)
            return;

        _readerLinuxTextFallbackRestoringSelectionScroll = true;
        try
        {
            ReaderLinuxTextFallbackScroll.Offset = target;
        }
        finally
        {
            _readerLinuxTextFallbackRestoringSelectionScroll = false;
        }
    }

    private void ReleaseLinuxTextFallbackSelectionScrollLock()
    {
        if (!_readerLinuxTextFallbackSelectionScrollLocked) return;

        RestoreLinuxTextFallbackSelectionScrollOffset();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!IsLinuxReaderTextFallbackActive() || _readerLayout.FlowMode != 0)
                {
                    _readerLinuxTextFallbackSelectionScrollLocked = false;
                    return;
                }

                RestoreLinuxTextFallbackSelectionScrollOffset();
                _readerLinuxTextFallbackSelectionScrollLocked = false;
                SyncLinuxReaderTextFallbackScrollState(saveProgress: false);
            },
            DispatcherPriority.Background);
    }

    private bool IsLinuxReaderTextFallbackActive() =>
        OperatingSystem.IsLinux()
        && !_readerIsPdf
        && ReaderLinuxTextFallbackOverlay.IsVisible;

    private ReaderScrollState? CaptureLinuxReaderTextFallbackState()
    {
        if (!IsLinuxReaderTextFallbackActive()) return null;

        if (_readerLayout.FlowMode == 0)
        {
            var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
            var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
            var position = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
            var maximum = Math.Max(0, extent - viewport);
            return new ReaderScrollState(
                position,
                maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width),
                extent,
                Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width),
                viewport);
        }

        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximumPage = Math.Max(0, pageCount - spreadSize);
        var pageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximumPage
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximumPage);
        if (_readerLayout.TwoPageMode && pageIndex % 2 != 0)
            pageIndex--;
        return new ReaderScrollState(
            pageIndex,
            maximumPage > 0 ? Math.Clamp(pageIndex / (double)maximumPage, 0, 1) : 0,
            pageCount,
            pageCount,
            spreadSize,
            spreadSize);
    }

    private bool TryApplyLinuxReaderTextFallbackState()
    {
        var state = CaptureLinuxReaderTextFallbackState();
        if (state is null) return false;
        ApplyReaderScrollState(state);
        return true;
    }

    private void ApplyReaderScrollState(ReaderScrollState state)
    {
        _readerScrollPosition = state.Position;
        _readerScrollRatio = state.Ratio;
        _readerScrollWidth = state.ScrollWidth;
        _readerScrollHeight = state.ScrollHeight;
        _readerClientWidth = state.ClientWidth;
        _readerClientHeight = state.ClientHeight;
    }

    private void SyncLinuxReaderTextFallbackScrollState(bool saveProgress)
    {
        if (!OperatingSystem.IsLinux()
            || _readerIsPdf
            || !ReaderLinuxTextFallbackOverlay.IsVisible)
        {
            return;
        }

        var extent = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Height);
        var viewport = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Height);
        var maximum = Math.Max(0, extent - viewport);
        _readerScrollPosition = Math.Max(0, ReaderLinuxTextFallbackScroll.Offset.Y);
        _readerScrollRatio = maximum > 0 ? Math.Clamp(_readerScrollPosition / maximum, 0, 1) : 0;
        _readerScrollHeight = extent;
        _readerClientHeight = viewport;
        _readerScrollWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Extent.Width);
        _readerClientWidth = Math.Max(0, ReaderLinuxTextFallbackScroll.Viewport.Width);
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackPagedState(bool saveProgress)
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        var pageCount = GetLinuxReaderTextFallbackPageCount();
        var spreadSize = _readerLayout.TwoPageMode ? 2 : 1;
        var maximum = Math.Max(0, pageCount - spreadSize);
        _readerLinuxTextFallbackPageIndex = _readerLinuxTextFallbackPageIndex < 0
            ? maximum
            : Math.Clamp(_readerLinuxTextFallbackPageIndex, 0, maximum);
        if (_readerLayout.TwoPageMode && _readerLinuxTextFallbackPageIndex % 2 != 0)
            _readerLinuxTextFallbackPageIndex--;
        RenderLinuxReaderTextFallbackPage();
        _readerScrollPosition = _readerLinuxTextFallbackPageIndex;
        _readerScrollRatio = maximum > 0
            ? Math.Clamp(_readerLinuxTextFallbackPageIndex / (double)maximum, 0, 1)
            : 0;
        _readerScrollWidth = pageCount;
        _readerClientWidth = spreadSize;
        _readerScrollHeight = pageCount;
        _readerClientHeight = spreadSize;
        UpdateReaderToolbar();
        if (saveProgress)
            _ = ObserveReaderTaskAsync(SaveReaderProgressAsync(CancellationToken.None));
    }

    private void SyncLinuxReaderTextFallbackState(bool saveProgress)
    {
        if (_readerLayout.FlowMode == 0)
            SyncLinuxReaderTextFallbackScrollState(saveProgress);
        else
            SyncLinuxReaderTextFallbackPagedState(saveProgress);
    }

    private void UpdateLinuxReaderTextFallbackMode()
    {
        var paged = _readerLayout.FlowMode == 1;
        ReaderLinuxTextFallbackScroll.IsVisible = !paged;
        ReaderLinuxTextFallbackPagedRoot.IsVisible = paged;
        var twoPage = paged && _readerLayout.TwoPageMode;
        ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Clear();
        if (twoPage)
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28)));
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            ReaderLinuxTextFallbackPagedContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ReaderLinuxTextFallbackPagedContent.HorizontalAlignment = HorizontalAlignment.Center;
        }
        Grid.SetColumn(ReaderLinuxTextFallbackPageRight, twoPage ? 2 : 0);
        Grid.SetColumn(ReaderLinuxTextFallbackPageImageRight, twoPage ? 2 : 0);
        if (paged)
            RebuildLinuxReaderTextFallbackPages();
    }

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

    private int? CaptureLinuxReaderTextFallbackPageAnchor()
    {
        if (_readerLayout.FlowMode != 1
            || _readerLinuxTextFallbackPageItems.Count == 0)
        {
            return null;
        }

        var pageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            _readerLinuxTextFallbackPageItems.Count - 1);
        for (var index = pageIndex; index < _readerLinuxTextFallbackPageItems.Count; index++)
        {
            var item = _readerLinuxTextFallbackPageItems[index];
            if (!item.IsImage && !string.IsNullOrWhiteSpace(item.Text))
                return item.PaginationOffset;
        }

        return null;
    }

    // Vertical writing replaces the selectable text slots with the custom
    // drawing page. Single-page only — ReaderLayoutDefaults.Normalize forces
    // vertical into FlowMode 1 with two-page spreads disabled.
    private void RenderLinuxReaderTextFallbackVerticalPage()
    {
        var control = ReaderLinuxTextFallbackPageVertical;
        var fontSize = Math.Max(10, 16d * _readerLayout.FontScale);
        control.FontSize = fontSize;
        control.LineHeight = Math.Max(fontSize + 2, fontSize * _readerLayout.LineHeight);

        void HideHorizontalSlots()
        {
            ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
            ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
            ReaderLinuxTextFallbackPageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
            ClearLinuxReaderTextFallbackSelectionState();
        }

        var pageCount = GetLinuxReaderTextFallbackPageCount();
        if (pageCount == 0 || _readerLinuxTextFallbackPageItems.Count == 0)
        {
            HideHorizontalSlots();
            control.FootnoteHrefResolver = null;
            control.Text = string.Empty;
            control.IsVisible = false;
            ReaderLinuxTextFallbackPageImageLeft.Source = null;
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
            return;
        }

        _readerLinuxTextFallbackPageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            Math.Max(0, pageCount - 1));
        var item = _readerLinuxTextFallbackPageItems[_readerLinuxTextFallbackPageIndex];
        if (item.Image is { } image)
        {
            // Image pages keep the shared slot; rotating covers is wrong.
            var (slotMaxWidth, slotMaxHeight) =
                GetLinuxReaderTextFallbackPagedImageBounds(ReaderLinuxTextFallbackPageLeft);
            ReaderLinuxTextFallbackPageImageLeft.Source = image.Source;
            ReaderLinuxTextFallbackPageImageLeft.MaxWidth =
                Math.Min(image.MaxWidth, slotMaxWidth);
            ReaderLinuxTextFallbackPageImageLeft.MaxHeight =
                Math.Min(image.MaxHeight, slotMaxHeight);
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = true;
            control.FootnoteHrefResolver = null;
            control.Text = string.Empty;
            control.IsVisible = false;
            return;
        }

        ReaderLinuxTextFallbackPageImageLeft.Source = null;
        ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageLeft.IsVisible = false;
        ReaderLinuxTextFallbackPageRight.IsVisible = false;

        var titleStart = item.ChapterTitleStart;
        var titleLength = item.ChapterTitleLength;
        var isFirstTextPage = !_readerLinuxTextFallbackPageItems
            .Take(_readerLinuxTextFallbackPageIndex)
            .Any(other => !other.IsImage && !string.IsNullOrWhiteSpace(other.Text));
        if ((titleStart < 0 || titleLength <= 0)
            && isFirstTextPage
            && ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
                item.Text,
                NormalizeReaderPlainTextLine(
                    _readerLinuxTextFallbackTargetTitle
                    ?? GetReaderChapterDisplayName(_readerChapterIndex)),
                out var recoveredStart,
                out var recoveredLength))
        {
            titleStart = recoveredStart;
            titleLength = recoveredLength;
        }

        control.FootnoteHrefResolver = index =>
            index >= 0 && index < item.InlineFootnotes.Count
                ? item.InlineFootnotes[index].Href
                : null;
        control.SelectionStart = 0;
        control.SelectionEnd = 0;
        control.Text = item.Text;
        control.ChapterTitleStart = titleStart;
        control.ChapterTitleLength = titleLength;
        control.ParagraphIndent = _readerLayout.ParagraphIndent;
        // A chapter heading is centered as its own visual unit and must not
        // consume the body paragraph's two-row indent. The first body page
        // after the heading is marked by the newline inside the page text.
        control.StartsParagraph = item.StartsParagraph
            && !(titleStart == 0 && titleLength > 0);
        control.AnnotationRanges = GetLinuxReaderTextFallbackAnnotationRanges(
            item.PaginationOffset,
            item.Text.Length);
        control.IsVisible = true;
    }

    private void RenderLinuxReaderTextFallbackPage()
    {
        if (_readerLayout.VerticalWriting)
        {
            RenderLinuxReaderTextFallbackVerticalPage();
            return;
        }

        ReaderLinuxTextFallbackPageVertical.IsVisible = false;
        var pageCount = GetLinuxReaderTextFallbackPageCount();
        if (pageCount == 0)
        {
            ReaderLinuxTextFallbackPageLeft.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageLeft.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageImageLeft.Source = null;
            ReaderLinuxTextFallbackPageImageRight.Source = null;
            ReaderLinuxTextFallbackPageImageLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteLeft.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteLeft.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteLeft.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
            return;
        }

        _readerLinuxTextFallbackPageIndex = Math.Clamp(
            _readerLinuxTextFallbackPageIndex,
            0,
            Math.Max(0, pageCount - 1));
        RenderLinuxReaderTextFallbackSlot(
            ReaderLinuxTextFallbackPageLeft,
            ReaderLinuxTextFallbackPageImageLeft,
            ReaderLinuxTextFallbackPageFootnoteLeft,
            _readerLinuxTextFallbackPageIndex);

        var rightPageIndex = _readerLinuxTextFallbackPageIndex + 1;
        if (_readerLayout.TwoPageMode && rightPageIndex < pageCount)
        {
            RenderLinuxReaderTextFallbackSlot(
                ReaderLinuxTextFallbackPageRight,
                ReaderLinuxTextFallbackPageImageRight,
                ReaderLinuxTextFallbackPageFootnoteRight,
                rightPageIndex);
        }
        else
        {
            ReaderLinuxTextFallbackPageRight.Inlines?.Clear();
            ReaderLinuxTextFallbackPageRight.Text = string.Empty;
            ReaderLinuxTextFallbackPageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageImageRight.Source = null;
            ReaderLinuxTextFallbackPageImageRight.IsVisible = false;
            ReaderLinuxTextFallbackPageFootnoteRight.Content = string.Empty;
            ReaderLinuxTextFallbackPageFootnoteRight.Tag = null;
            ReaderLinuxTextFallbackPageFootnoteRight.IsVisible = false;
        }
    }

    private void RenderLinuxReaderTextFallbackSlot(
        SelectableTextBlock textBlock,
        Image imageControl,
        Button footnoteButton,
        int pageIndex)
    {
        textBlock.Inlines?.Clear();
        textBlock.Text = string.Empty;
        if (textBlock is ReaderLinuxTextFallbackTextBlock styledTextBlock)
        {
            styledTextBlock.ChapterTitleStart = -1;
            styledTextBlock.ChapterTitleLength = 0;
            styledTextBlock.LayoutText = string.Empty;
            styledTextBlock.AnnotationRanges = [];
        }
        footnoteButton.Content = string.Empty;
        footnoteButton.Tag = null;
        footnoteButton.IsVisible = false;

        if (pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && _readerLinuxTextFallbackPageItems[pageIndex] is { Image: { } image })
        {
            var (slotMaxWidth, slotMaxHeight) = GetLinuxReaderTextFallbackPagedImageBounds(textBlock);
            imageControl.Source = image.Source;
            imageControl.MaxWidth = Math.Min(image.MaxWidth, slotMaxWidth);
            imageControl.MaxHeight = Math.Min(image.MaxHeight, slotMaxHeight);
            imageControl.IsVisible = true;
            textBlock.Text = string.Empty;
            textBlock.IsVisible = false;
            return;
        }

        if (pageIndex >= 0
            && pageIndex < _readerLinuxTextFallbackPageItems.Count
            && !_readerLinuxTextFallbackPageItems[pageIndex].IsImage)
        {
            var page = _readerLinuxTextFallbackPageItems[pageIndex];
            if (textBlock is ReaderLinuxTextFallbackTextBlock pageStyledTextBlock)
            {
                var titleStart = page.ChapterTitleStart;
                var titleLength = page.ChapterTitleLength;
                var isFirstTextPage = !_readerLinuxTextFallbackPageItems
                    .Take(pageIndex)
                    .Any(item => !item.IsImage && !string.IsNullOrWhiteSpace(item.Text));
                if ((titleStart < 0 || titleLength <= 0)
                    && isFirstTextPage
                    && ReaderLinuxTextFallbackTitleLinePolicy.TryFindLeadingTitleRange(
                        page.Text,
                        NormalizeReaderPlainTextLine(
                            _readerLinuxTextFallbackTargetTitle
                                ?? GetReaderChapterDisplayName(_readerChapterIndex)),
                        out var recoveredTitleStart,
                        out var recoveredTitleLength))
                {
                    titleStart = recoveredTitleStart;
                    titleLength = recoveredTitleLength;
                }
                pageStyledTextBlock.ChapterTitleStart = titleStart;
                pageStyledTextBlock.ChapterTitleLength = titleLength;
                pageStyledTextBlock.LayoutText = page.Text;
                pageStyledTextBlock.AnnotationRanges =
                    GetLinuxReaderTextFallbackAnnotationRanges(
                        page.PaginationOffset,
                        page.Text.Length);
            }
            if (page.HasInlineFootnotes)
                RenderLinuxReaderTextFallbackInlinePage(textBlock, page);
            else
                textBlock.Text = page.Text;
            textBlock.IsVisible = true;
        }
        else
        {
            textBlock.Text = string.Empty;
            textBlock.IsVisible = false;
        }
        imageControl.Source = null;
        imageControl.IsVisible = false;
    }

    private void RenderLinuxReaderTextFallbackInlinePage(
        SelectableTextBlock textBlock,
        ReaderLinuxTextFallbackPageItem page)
    {
        var footnoteIndex = 0;
        var textStart = 0;
        var text = page.Text;

        void AddRun(string value)
        {
            if (value.Length > 0)
                textBlock.Inlines?.Add(new Run(value));
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != ReaderLinuxTextFallbackFootnoteMarker)
                continue;

            AddRun(text[textStart..index]);
            if (footnoteIndex < page.InlineFootnotes.Count)
            {
                var footnote = page.InlineFootnotes[footnoteIndex++];
                textBlock.Inlines?.Add(new InlineUIContainer(
                    CreateLinuxReaderInlineFootnoteButton(footnote)));
            }
            else
            {
                AddRun(UiText.Get("注"));
            }
            textStart = index + 1;
        }

        AddRun(text[textStart..]);
    }

    private Button CreateLinuxReaderInlineFootnoteButton(
        ReaderLinuxTextFallbackFootnote footnote)
    {
        var button = new Button
        {
            Content = footnote.Label,
            Tag = footnote.Href,
            Focusable = true,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0)
        };
        button.Classes.Add("readerFootnoteMarker");
        button.PointerEntered += ReaderLinuxTextFallbackFootnoteButton_PointerEntered;
        button.PointerExited += ReaderLinuxTextFallbackFootnoteButton_PointerExited;
        return button;
    }

    private int GetLinuxReaderTextFallbackPageCount() =>
        Math.Max(1, _readerLinuxTextFallbackPageItems.Count > 0
            ? _readerLinuxTextFallbackPageItems.Count
            : _readerLinuxTextFallbackPages.Count);

    private int GetLinuxReaderTextFallbackImagePageCount()
    {
        return _readerLinuxTextFallbackPageItems.Count(item => item.IsImage);
    }
}
