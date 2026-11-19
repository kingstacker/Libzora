using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Text.RegularExpressions;

namespace Libzora;

/// <summary>
/// TOC, bookmark and search tabs.
/// </summary>
public partial class MainWindow
{
    private async Task RefreshReaderBookmarksAsync(CancellationToken cancellationToken)
    {
        ReaderBookmarks.Clear();
        if (_readerBookFile is null) return;
        var bookmarks = await _readerData.GetBookmarksAsync(
            _readerBookFile.Id,
            cancellationToken,
            MaxReaderBookmarks);
        foreach (var bookmark in bookmarks
                     .OrderBy(item => item.ChapterIndex)
                     .ThenBy(item => item.CreatedAt))
            ReaderBookmarks.Add(bookmark);
        ReaderBookmarkList.IsVisible = ReaderBookmarks.Count > 0;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
    }

    private async Task RefreshReaderAnnotationsAsync(CancellationToken cancellationToken)
    {
        ReaderAnnotations.Clear();
        _selectedReaderAnnotation = null;
        if (_readerBookFile is null) return;
        var annotations = await _readerData.GetAnnotationsAsync(
            _readerBookFile.Id,
            cancellationToken,
            MaxReaderAnnotations);
        foreach (var annotation in annotations) ReaderAnnotations.Add(annotation);
        ReaderAnnotationEmptyText.IsVisible = ReaderAnnotations.Count == 0;
    }

    private void ShowReaderTocTab()
    {
        ReaderTocView.IsVisible = true;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = false;
        ReaderTocEmptyText.IsVisible = _readerTocItems.Count == 0;
    }

    private void ShowReaderBookmarkTab()
    {
        // The toolbar bookmark command opens this page in the same left rail
        // as the TOC. Re-expand the rail when it had been collapsed or reduced
        // to the compact marker strip so the destination is always visible.
        _readerTocExpanded = true;
        _readerTocMinimal = false;
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = true;
        ReaderSearchPanel.IsVisible = false;
        ReaderBookmarkEmptyText.IsVisible = ReaderBookmarks.Count == 0;
        ApplyReaderPanelLayout();
    }

    private void ShowReaderSearchTab()
    {
        ReaderTocView.IsVisible = false;
        ReaderBookmarkPane.IsVisible = false;
        ReaderSearchPanel.IsVisible = true;
    }

    private void ShowReaderSearchStatus(string? message)
    {
        ReaderSearchStatusText.Text = message ?? string.Empty;
        ReaderSearchStatusText.IsVisible = message is not null;
        ReaderSearchResultList.IsVisible = message is null;
    }

    private void ReaderSearchToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readerSearchVisible)
            HideReaderSearchPanel();
        else
            ShowReaderSearchPanel();
    }

    private void ShowReaderSearchPanel()
    {
        if (!_readerSearchVisible)
        {
            _readerSearchPreviousTocExpanded = _readerTocExpanded;
            _readerSearchPreviousTocMinimal = _readerTocMinimal;
            _readerSearchPreviousBookmarkTabActive = ReaderBookmarkPane.IsVisible;
            _readerSearchLayoutCaptured = true;
            _readerTocExpanded = true;
            _readerTocMinimal = false;
            _readerSearchVisible = true;
        }

        ApplyReaderPanelLayout();
        ShowReaderSearchTab();
        ReaderTocSearchBox.PlaceholderText = T("搜索整本书…");
        ReaderTocSearchBox.Text = _readerSearchQuery;
        ShowReaderSearchStatus(string.IsNullOrWhiteSpace(_readerSearchQuery)
            ? T("输入关键词，实时搜索整本书。")
            : ReaderSearchResults.Count > 0 ? null : T("正在本地搜索…"));
        ReaderTocSearchBox.Focus();
        if (!string.IsNullOrWhiteSpace(_readerSearchQuery) && ReaderSearchResults.Count == 0)
        {
            var sequence = ++_readerWholeSearchSequence;
            _ = RefreshReaderWholeSearchAsync(_readerSearchQuery, sequence);
        }
    }

    private void HideReaderSearchPanel(bool restorePreviousLayout = true)
    {
        if (_readerSearchVisible)
            _readerSearchQuery = ReaderTocSearchBox.Text?.Trim() ?? string.Empty;
        _readerSearchVisible = false;
        _readerWholeSearchSequence++;
        _readerWholeSearchCancellation?.Cancel();
        ReaderSearchPanel.IsVisible = false;
        ReaderTocSearchBox.Text = string.Empty;
        if (_readerSearchPreviousBookmarkTabActive && restorePreviousLayout)
            ShowReaderBookmarkTab();
        else
            ShowReaderTocTab();

        if (_readerSearchLayoutCaptured)
        {
            _readerTocExpanded = restorePreviousLayout && _readerSearchPreviousTocExpanded;
            _readerTocMinimal = restorePreviousLayout && _readerSearchPreviousTocMinimal;
            _readerSearchLayoutCaptured = false;
            ApplyReaderPanelLayout();
        }
    }
}
