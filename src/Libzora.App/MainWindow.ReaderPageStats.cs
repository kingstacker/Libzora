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
/// Reader page counts, page position and progress helpers.
/// </summary>
public partial class MainWindow
{
    private static readonly Geometry ReaderFlowScrollIcon =
        Geometry.Parse("M5 4.5h14v15h-14Z M12 8.5v6.5 M9.3 12.3L12 15L14.7 12.3");
    // 单页：折角文档页，避免与双栏共用空心矩形观感。
    private static readonly Geometry ReaderFlowSingleIcon =
        Geometry.Parse("M6 4h7.5l4.5 4.5V20H6Z M13.5 4v4.5H18");
    private static readonly Geometry ReaderFlowDoubleIcon =
        Geometry.Parse("M5 4.5h14v15h-14Z M12 4.5v15");

    private static Geometry GetReaderFlowIcon(int flowMode, bool twoPage)
        => flowMode == 0
            ? ReaderFlowScrollIcon
            : twoPage
                    ? ReaderFlowDoubleIcon
                    : ReaderFlowSingleIcon;

    private void UpdateReaderToolbar()
    {
        UpdateReaderPdfNavigationUi();
        if (ReaderFlowButton is not null)
        {
            ReaderFlowButton.IsVisible = true;
        }
        SyncReaderFlowMenu();
        SyncReaderAnimationMenu();
        UpdateReaderZoomLabel();
        if (ReaderProgressSlider is not null)
        {
            _readerProgressSliderUpdating = true;
            if (_readerIsPdf)
            {
                var pageCount = Math.Max(1, _readerPdfPages.Count);
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = pageCount;
                ReaderProgressSlider.Value = Math.Clamp(_readerPdfPage, 1, pageCount);
            }
            else if (_readerTocItems.Count > 0)
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = _readerTocItems.Count;
                ReaderProgressSlider.Value = Math.Clamp(GetCurrentReaderTocIndex() + 1, 1, _readerTocItems.Count);
            }
            else
            {
                ReaderProgressSlider.Minimum = 1;
                ReaderProgressSlider.Maximum = 1;
                ReaderProgressSlider.Value = 1;
            }
            _readerProgressSliderUpdating = false;
        }
        // PDF zoom is performed with the pointer wheel; keep the shared controls
        // for EPUB font sizing only.
        if (ReaderZoomOutButton is not null && ReaderZoomText is not null && ReaderZoomInButton is not null)
        {
            var isManga = IsReaderMangaMode;
            ReaderZoomOutButton.IsVisible = !_readerIsPdf;
            ReaderZoomText.IsVisible = !_readerIsPdf;
            ReaderZoomInButton.IsVisible = !_readerIsPdf;
            var zoomOutLabel = (_readerIsPdf || isManga) ? T("缩小页面") : T("减小字号");
            var zoomInLabel = (_readerIsPdf || isManga) ? T("放大页面") : T("增大字号");
            ReaderZoomOutButton.Content = (_readerIsPdf || isManga) ? "−" : "A−";
            ReaderZoomInButton.Content = (_readerIsPdf || isManga) ? "+" : "A+";
            ToolTip.SetTip(ReaderZoomOutButton, zoomOutLabel);
            ToolTip.SetTip(ReaderZoomInButton, zoomInLabel);
            AutomationProperties.SetName(ReaderZoomOutButton, zoomOutLabel);
            AutomationProperties.SetName(ReaderZoomInButton, zoomInLabel);
        }
        if (ReaderPdfFitButton is not null)
        {
            ReaderPdfFitButton.IsVisible = _readerIsPdf;
            var pdf = CurrentReaderHost as IPdfReadingHost;
            ReaderPdfFitWidthItem.IsChecked = pdf?.FitMode == PdfReaderFitMode.Width && Math.Abs(pdf.Zoom - 1) < 0.005;
            ReaderPdfFitPageItem.IsChecked = pdf?.FitMode == PdfReaderFitMode.Page && Math.Abs(pdf.Zoom - 1) < 0.005;
        }
        if (ReaderPdfBadge is not null)
            ReaderPdfBadge.IsVisible = _readerIsPdf;
        if (ReaderPdfRotateButton is not null)
            ReaderPdfRotateButton.IsVisible = _readerIsPdf;
        if (ReaderPdfHandToolButton is not null)
        {
            ReaderPdfHandToolButton.IsVisible = _readerIsPdf;
            UpdateReaderPdfHandToolButton(
                (CurrentReaderHost as IPdfDocumentHost)?.IsHandToolMode == true);
        }
        if (ReaderPdfPointNoteButton is not null)
        {
            ReaderPdfPointNoteButton.IsVisible = _readerIsPdf;
            UpdateReaderPdfPointNoteButton(
                (CurrentReaderHost as IPdfDocumentHost)?.IsPointAnnotationMode == true);
        }
        if (ReaderPreviousButton is not null)
            ReaderPreviousButton.IsEnabled = GetReaderFooterTocIndex() > 0;
        if (ReaderNextButton is not null)
            ReaderNextButton.IsEnabled = GetReaderFooterTocIndex() is var tocIndex
                    && tocIndex >= 0
                    && tocIndex + 1 < _readerTocItems.Count;
        UpdateReaderSearchCount();
        UpdateReaderTtsUi();
    }

    private (double Width, double Height) GetReaderPageCountMeasure()
    {
        var width = _readerClientWidth > 0
            ? _readerClientWidth
            : ReaderWebViewHost.Bounds.Width;
        var height = _readerClientHeight > 0
            ? _readerClientHeight
            : ReaderWebViewHost.Bounds.Height;
        if (IsLinuxReaderTextFallbackActive())
        {
            if (ReaderLinuxTextFallbackOverlay.Bounds.Width > 0)
                width = ReaderLinuxTextFallbackOverlay.Bounds.Width;
            if (ReaderLinuxTextFallbackOverlay.Bounds.Height > 0)
                height = ReaderLinuxTextFallbackOverlay.Bounds.Height;
        }

        // A native WebView can report zero bounds for a few compositor frames
        // while it is being attached. Do not turn that transient state into a
        // book with thousands of tiny pages; use the normal reader surface's
        // minimum usable geometry until the host reports its real viewport.
        if (width < 640)
        {
            var rootWidth = ReaderRoot.Bounds.Width;
            width = rootWidth >= 640
                ? Math.Min(_readerLayout.MaxWidth, Math.Max(640, rootWidth - 320))
                : 960;
        }
        if (height < 360)
        {
            var rootHeight = ReaderRoot.Bounds.Height;
            height = rootHeight >= 360 ? Math.Max(360, rootHeight - 100) : 600;
        }

        return (
            Math.Max(320, double.IsFinite(width) ? width : 0),
            Math.Max(240, double.IsFinite(height) ? height : 0));
    }

    private void ScheduleReaderBookPageCountRefresh()
    {
        if (_readerIsPdf
            || _readerDocument is not { Chapters.Count: > 0 } document)
            return;

        var (width, height) = GetReaderPageCountMeasure();
        if (width <= 0 || height <= 0) return;

        _readerBookPageCountCancellation?.Cancel();
        _readerBookPageCountCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _readerSession.GetTokenOr(_lifetimeCancellation.Token));
        _readerBookPageCountCancellation = cancellation;
        var sequence = ++_readerBookPageCountSequence;
        var layout = _readerLayout;
        _ = ObserveReaderTaskAsync(
            RefreshReaderBookPageCountsAsync(
                document,
                layout,
                width,
                height,
                sequence,
                cancellation));
    }

    private async Task RefreshReaderBookPageCountsAsync(
        EpubReaderDocument document,
        ReaderLayoutSettings layout,
        double viewportWidth,
        double viewportHeight,
        int sequence,
        CancellationTokenSource cancellation)
    {
        try
        {
            var nativeHost = CurrentReaderHost as IReaderPageCountHost;
            var useNativeLayout = nativeHost is not null;
            var knownChapter = nativeHost?.GetCurrentChapterPageCountFor(
                layout, viewportWidth, viewportHeight);

            // Show a usable total immediately from chapter byte sizes. Exact
            // pagination waits until the user has actually started reading, so
            // opening a 500-chapter book no longer competes with the first
            // page turn.
            var cheapCounts = BuildCheapReaderPageCounts(document, knownChapter);
            if (sequence == _readerBookPageCountSequence
                && !cancellation.IsCancellationRequested
                && ReferenceEquals(document, _readerDocument))
            {
                _readerBookPageCounts = cheapCounts;
                UpdateReaderToolbar();
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);

            var counts = await Task.Run(
                () => useNativeLayout
                    ? nativeHost!.EstimateChapterPageCounts(
                        document.Chapters,
                        layout,
                        viewportWidth,
                        viewportHeight,
                        cancellation.Token,
                        knownChapter)
                    : document.Chapters
                        .Select(path => EstimateReaderChapterPageCount(
                            path,
                            layout,
                            viewportWidth,
                            viewportHeight))
                        .Select(count => Math.Max(1, count))
                        .ToArray(),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (sequence != _readerBookPageCountSequence
                    || !ReferenceEquals(document, _readerDocument)
                    || cancellation.IsCancellationRequested)
                    return;
                _readerBookPageCounts = counts;
                UpdateReaderToolbar();
            });
        }
        finally
        {
            if (ReferenceEquals(_readerBookPageCountCancellation, cancellation))
            {
                _readerBookPageCountCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private static int[] BuildCheapReaderPageCounts(
        EpubReaderDocument document,
        (string ChapterPath, int PageCount)? knownChapter)
    {
        var counts = new int[document.Chapters.Count];
        var bytesPerPage = 4096d;
        if (knownChapter is { PageCount: > 0 } known)
        {
            try
            {
                var knownLength = new FileInfo(known.ChapterPath).Length;
                if (knownLength > 0)
                    bytesPerPage = Math.Max(512d, knownLength / (double)known.PageCount);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        for (var index = 0; index < counts.Length; index++)
        {
            try
            {
                var length = new FileInfo(document.Chapters[index]).Length;
                counts[index] = Math.Max(1, (int)Math.Round(length / bytesPerPage));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                counts[index] = 1;
            }
        }
        return counts;
    }

    private static int EstimateReaderChapterPageCount(
        string path,
        ReaderLayoutSettings layout,
        double viewportWidth,
        double viewportHeight)
    {
        if (!File.Exists(path)) return 1;

        var fontSize = Math.Max(10, 16d * layout.FontScale);
        var lineHeight = Math.Max(fontSize + 2, fontSize * layout.LineHeight);
        var pageInsets = layout.VerticalWriting
            ? ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
                viewportWidth,
                viewportHeight,
                layout.BodyPadding,
                layout.MaxWidth)
            : (Horizontal: Math.Max(0, layout.BodyPadding), Vertical: Math.Max(0, layout.BodyPadding));
        var availablePageWidth = Math.Max(1, viewportWidth - pageInsets.Horizontal * 2);
        var pageWidth = layout.TwoPageMode
            ? Math.Max(180, (availablePageWidth - 28) / 2)
            : Math.Max(240, Math.Min(layout.MaxWidth, availablePageWidth));
        var pageHeight = Math.Max(180, viewportHeight - pageInsets.Vertical * 2);
        var linesPerPage = Math.Max(4, (int)Math.Floor(pageHeight / lineHeight));
        var lineUnits = Math.Max(8, pageWidth / fontSize);
        var content = ExtractReaderFallbackContent(path);
        var text = new StringBuilder();
        var pages = 0;

        void FlushText()
        {
            if (text.Length == 0) return;
            pages += PaginateReaderPlainText(text.ToString(), lineUnits, linesPerPage).Count;
            text.Clear();
        }

        foreach (var block in content.Blocks)
        {
            if (!string.IsNullOrWhiteSpace(block.ImagePath))
            {
                FlushText();
                pages++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(block.FootnoteHref))
            {
                text.Append(ReaderLinuxTextFallbackFootnoteMarker);
                continue;
            }

            var blockText = NormalizeReaderPlainText(block.Text ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(blockText))
                text.Append(blockText);
        }

        if (content.Blocks.Count == 0 && !string.IsNullOrWhiteSpace(content.Text))
            text.Append(NormalizeReaderPlainText(content.Text));

        FlushText();
        return Math.Max(1, pages);
    }

    private (int CurrentPage, int TotalPages) GetReaderPagePosition()
    {
        var local = GetReaderChapterPagePosition();
        if (_readerDocument is not { Chapters.Count: > 0 } document
            || _readerBookPageCounts.Length != document.Chapters.Count
            || _readerIsPdf)
            return local;

        if (_readerChapterIndex >= 0 && _readerChapterIndex < _readerBookPageCounts.Length)
            _readerBookPageCounts[_readerChapterIndex] = Math.Max(
                1,
                Math.Max(_readerBookPageCounts[_readerChapterIndex], local.TotalPages));

        var totalPages = _readerBookPageCounts.Sum(count => Math.Max(1, count));
        var prefixPages = _readerBookPageCounts
            .Take(Math.Clamp(_readerChapterIndex, 0, _readerBookPageCounts.Length))
            .Sum(count => Math.Max(1, count));
        return (
            Math.Clamp(prefixPages + local.CurrentPage, 1, Math.Max(1, totalPages)),
            Math.Max(1, totalPages));
    }

    private (int CurrentPage, int TotalPages) GetReaderChapterPagePosition()
    {
        if (_readerIsPdf)
            return (
                Math.Clamp(_readerPdfPage, 1, Math.Max(1, _readerPdfPages.Count)),
                Math.Max(1, _readerPdfPages.Count));

        if (_readerLayout.FlowMode == 1 && IsLinuxReaderTextFallbackActive())
        {
            var pageCount = GetLinuxReaderTextFallbackPageCount();
            var pageIndex = _readerLinuxTextFallbackPageIndex < 0
                ? pageCount - 1
                : _readerLinuxTextFallbackPageIndex;
            return (Math.Clamp(pageIndex + 1, 1, pageCount), pageCount);
        }

        if (CurrentReaderHost is IReaderPaginationHost nativeReader)
        {
            var pageCount = Math.Max(1, nativeReader.PageCount);
            return (
                Math.Clamp(nativeReader.CurrentPage + 1, 1, pageCount),
                pageCount);
        }

        var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
        var viewport = horizontal ? _readerClientWidth : _readerClientHeight;
        var extent = horizontal ? _readerScrollWidth : _readerScrollHeight;
        if (viewport <= 0 || extent <= 0)
            return (1, 1);

        var position = Math.Max(0, _readerScrollPosition);
        if (_readerLayout.FlowMode == 1)
        {
            // Paginated WebView content is snapped to one viewport per page.
            // Natural vertical flow may end with a partial viewport, whereas
            // horizontal multicol extends to a rounded logical boundary.
            var maximum = Math.Max(0, extent - viewport);
            var viewCount = _readerLayout.VerticalWriting
                ? Math.Max(1, (int)Math.Ceiling(extent / viewport))
                : Math.Max(1, (int)Math.Round(maximum / viewport) + 1);
            var currentView = _readerLayout.VerticalWriting && position >= maximum - 4
                ? viewCount
                : Math.Clamp(
                    (int)Math.Round(position / viewport) + 1,
                    1,
                    viewCount);
            var pagesPerView = _readerLayout.TwoPageMode ? 2 : 1;
            return (
                Math.Clamp((currentView - 1) * pagesPerView + 1, 1, viewCount * pagesPerView),
                viewCount * pagesPerView);
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(extent / viewport));
        var currentPage = Math.Clamp((int)Math.Floor(position / viewport) + 1, 1, totalPages);
        return (currentPage, totalPages);
    }

    private void UpdateReaderSearchCount()
    {
        var text = _readerSearchCount <= 0
            ? "0/0"
            : $"{_readerSearchIndex + 1}/{_readerSearchCount}";
        ReaderInPageSearchCountText.Text = text;
    }

    private int FindReaderTocIndex(EpubReaderNavigationItem item)
    {
        for (var index = 0; index < _readerTocItems.Count; index++)
            if (ReferenceEquals(_readerTocItems[index], item) || _readerTocItems[index] == item) return index;
        return -1;
    }

    private int FindReaderTocIndexForChapter(int chapterIndex) =>
        _readerTocItems.FirstOrDefault(item => item.ChapterIndex == chapterIndex) is { } item
            ? FindReaderTocIndex(item)
            : -1;

    private string? GetReaderChapterPath(IReaderHost? host = null)
    {
        if (_readerDocument is null) return null;
        if (host?.Source is { IsFile: true } source)
        {
            var sourcePath = Path.GetFullPath(source.LocalPath);
            if (IsPathInside(_readerDocument.RootPath, sourcePath))
            {
                return Path.GetRelativePath(_readerDocument.RootPath, sourcePath)
                    .Replace('\\', '/');
            }
        }
        if (_readerChapterIndex < 0 || _readerChapterIndex >= _readerDocument.Chapters.Count)
            return null;
        return Path.GetRelativePath(
                _readerDocument.RootPath,
                _readerDocument.Chapters[_readerChapterIndex])
            .Replace('\\', '/');
    }

    private double CalculateReaderProgressPercent()
    {
        if (_readerIsPdf)
        {
            if (_readerPdfPages.Count <= 1) return _readerPdfPages.Count == 0 ? 0 : 100;
            return Math.Clamp((_readerPdfPage - 1d) * 100d / (_readerPdfPages.Count - 1d), 0, 100);
        }
        if (_readerDocument is null || _readerDocument.Chapters.Count == 0) return 0;
        return Math.Clamp(
            (_readerChapterIndex + _readerScrollRatio) * 100d / _readerDocument.Chapters.Count,
            0,
            100);
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) && double.IsFinite(value)
            ? value
            : 0;

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value)
            ? Math.Max(0, value)
            : 0;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string NormalizeReaderAnnotationColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#000000";
        var normalized = value.Trim();
        if (normalized.Length != 7 || normalized[0] != '#') return "#000000";
        return int.TryParse(
            normalized.AsSpan(1),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out _)
                ? normalized.ToUpperInvariant()
                : "#000000";
    }

    private static int ParseScriptInt(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return 0;
        try
        {
            using var json = JsonDocument.Parse(result);
            var element = json.RootElement;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        catch (JsonException)
        {
        }
        return int.TryParse(result.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallback)
            ? fallback
            : 0;
    }

    private static string EscapeJavaScriptSingleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeCssString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Replace(";", string.Empty, StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
