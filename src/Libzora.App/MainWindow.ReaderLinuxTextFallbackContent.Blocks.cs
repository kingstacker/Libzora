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
/// Block updates, annotations and chapter title promotion.
/// </summary>
public partial class MainWindow
{
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
}
