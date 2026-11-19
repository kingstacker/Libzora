using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Compact markers, hover labels and chapter preview.
/// </summary>
public partial class MainWindow
{
    private void UpdateReaderCompactHoverLabel(
        Control? target,
        EpubReaderNavigationItem? item)
    {
        if (target is null || item is null)
        {
            HideReaderCompactHoverLabel();
            return;
        }

        try
        {
            var index = GetReaderNavigationItemIndex(item);
            ShowReaderChapterPreview(target, index, placeAbove: false, includeBodyPreview: true);
        }
        catch (InvalidOperationException)
        {
            HideReaderCompactHoverLabel();
        }
    }

    private void HideReaderCompactHoverLabel()
    {
        _readerChapterPreviewRequestVersion++;
        if (ReaderChapterPreviewPopup is not null)
            ReaderChapterPreviewPopup.IsOpen = false;
        if (ReaderChapterPreviewBodyText is not null)
        {
            ReaderChapterPreviewBodyText.Text = string.Empty;
            ReaderChapterPreviewBodyText.IsVisible = false;
        }
        _readerChapterPreviewTarget = null;
    }

    private int GetReaderNavigationItemIndex(EpubReaderNavigationItem item)
    {
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            if (ReferenceEquals(_readerTocItems[index], item)
                || _readerTocItems[index].Target.Equals(item.Target, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return Math.Clamp(item.ChapterIndex, 0, Math.Max(0, _readerTocItems.Count - 1));
    }

    private void ShowReaderChapterPreview(
        Control target,
        int index,
        bool placeAbove,
        bool includeBodyPreview = false)
    {
        if (ReaderChapterPreviewPopup is null || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            return;

        var requestVersion = ++_readerChapterPreviewRequestVersion;
        ReaderChapterPreviewBodyText.Text = string.Empty;
        ReaderChapterPreviewBodyText.IsVisible = false;
        if (_readerIsPdf)
        {
            var total = Math.Max(1, _readerPdfPages.Count);
            var item = includeBodyPreview && index >= 0 && index < _readerTocItems.Count ? _readerTocItems[index] : null;
            var page = Math.Clamp((item?.ChapterIndex ?? index) + 1, 1, total);
            ReaderChapterPreviewTitleText.Text = item is null ? T("第 {0} 页", page) : $"{item.Title} · {T("第 {0} 页", page)}";
        }
        else
        {
            if (_readerTocItems.Count == 0) return;
            index = Math.Clamp(index, 0, _readerTocItems.Count - 1);
            ReaderChapterPreviewTitleText.Text = _readerTocItems[index].Title;
            if (includeBodyPreview)
            {
                ReaderChapterPreviewBodyText.Text = T("正在读取正文…");
                ReaderChapterPreviewBodyText.IsVisible = true;
            }
        }

        var targetChanged = !ReferenceEquals(target, _readerChapterPreviewTarget)
            || placeAbove != _readerChapterPreviewAbove;
        if (targetChanged && ReaderChapterPreviewPopup.IsOpen)
            ReaderChapterPreviewPopup.IsOpen = false;

        _readerChapterPreviewTarget = target;
        _readerChapterPreviewAbove = placeAbove;
        ReaderChapterPreviewPopup.PlacementTarget = target;
        ReaderChapterPreviewPopup.Placement = placeAbove
            ? PlacementMode.Top
            : PlacementMode.Right;
        ReaderChapterPreviewPopup.HorizontalOffset = placeAbove ? 0 : 6;
        ReaderChapterPreviewPopup.VerticalOffset = placeAbove ? -8 : 0;
        ReaderChapterPreviewPopup.IsOpen = true;

        if (includeBodyPreview && !_readerIsPdf)
        {
            _ = LoadReaderChapterPreviewBodyAsync(
                target,
                _readerTocItems[index],
                requestVersion);
        }
    }

    private async Task LoadReaderChapterPreviewBodyAsync(
        Control target,
        EpubReaderNavigationItem item,
        int requestVersion)
    {
        if (_readerDocument is null
            || item.ChapterIndex < 0
            || item.ChapterIndex >= _readerDocument.Chapters.Count
            || !Uri.TryCreate(item.Target, UriKind.Absolute, out var targetUri)
            || !targetUri.IsFile
            || !IsPathInside(_readerDocument.RootPath, targetUri.LocalPath))
        {
            return;
        }

        var chapterPath = Path.GetFullPath(targetUri.LocalPath);
        var fragment = GetReaderTargetFragment(targetUri);
        try
        {
            var cacheKey = $"{chapterPath}\0{fragment}\0{item.Title}";
            if (!_readerChapterPreviewTextCache.TryGetValue(cacheKey, out var preview))
            {
                var plainText = await Task.Run(() => ExtractReaderPlainText(chapterPath, fragment, item.Title));
                preview = BuildReaderChapterPreviewText(plainText, item.Title);
                _readerChapterPreviewTextCache[cacheKey] = preview;
            }

            if (requestVersion != _readerChapterPreviewRequestVersion
                || !ReferenceEquals(target, _readerChapterPreviewTarget)
                || ReaderChapterPreviewPopup is null
                || !ReaderChapterPreviewPopup.IsOpen)
            {
                return;
            }

            ReaderChapterPreviewBodyText.Text = preview;
            ReaderChapterPreviewBodyText.IsVisible = !string.IsNullOrWhiteSpace(preview);
        }
        catch (Exception)
        {
            // A hover preview is optional; keep the title visible when the
            // chapter cannot be decoded or is removed during cleanup.
        }
    }

    private static string BuildReaderChapterPreviewText(string plainText, string title)
    {
        plainText = EnsureReaderPlainTextStartsWithTitle(plainText, title);
        return string.Join(
            Environment.NewLine,
            plainText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(4));
    }

    private static void SetReaderCompactMarkerWidth(Border marker, double width)
    {
        marker.Width = width;
        if (marker.RenderTransform is not TranslateTransform translation)
        {
            translation = new TranslateTransform();
            marker.RenderTransform = translation;
        }

        // The resting marker stays centered. Render translation does not take
        // part in layout, so shifting by half of the added width precisely
        // cancels the centered marker's leftward growth. Its left edge remains
        // fixed and only the right half-wave extends into the reading area.
        translation.X = (width - ReaderCompactMarkerMinimumWidth) / 2;
    }

    private static IEnumerable<T> FindDescendants<T>(Visual root) where T : Visual
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T match) yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    private void SetReaderTocMinimal(bool minimal)
    {
        _readerTocMinimal = minimal;
        _readerTocExpanded = !minimal;
        _readerCompactPointerActive = false;
        HideReaderCompactHoverLabel();
        ApplyReaderPanelLayout();
        UpdateReaderZenTocToggle();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void SetReaderCompactNavigationItems(IReadOnlyList<EpubReaderNavigationItem> items)
    {
        StopReaderCompactScrollAnimation();
        _readerChapterPreviewTextCache.Clear();
        _readerCompactNavigationItems = items;
        RefreshReaderCompactMarkers();
        QueueReaderCompactScrollIndicatorUpdate();
    }

    private void ClearReaderCompactNavigationItems()
    {
        StopReaderCompactScrollAnimation();
        _readerCompactNavigationItems = [];
        _readerCompactSelectedTarget = null;
        HideReaderCompactHoverLabel();
        ReaderTocCompactList.ItemsSource = null;
        QueueReaderCompactScrollIndicatorUpdate();
    }
}
