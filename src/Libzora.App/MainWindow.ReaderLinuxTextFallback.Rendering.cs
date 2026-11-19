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
/// Fallback page rendering and footnote buttons.
/// </summary>
public partial class MainWindow
{
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
