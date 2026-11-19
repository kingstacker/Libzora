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
/// Location application, fragments and TOC selection.
/// </summary>
public partial class MainWindow
{
    private async Task ApplyReaderLocationAsync(
        IReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        ReaderNavigationIntent intent,
        bool hasPendingRestorePosition)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is not IReaderLocationHost nativeReader)
        {
            return;
        }

        var nativeFragment = DecodeReaderFragment(target.Fragment);
        if (intent is ReaderNavigationIntent.Search or ReaderNavigationIntent.AiSource)
        {
            await ScrollToPendingReaderChunkAsync(host, cancellationToken);
        }
        else if (intent == ReaderNavigationIntent.Annotation && _readerPendingAnnotation is { } pending)
        {
            var startOffset = nativeReader.TryGetAnnotationRange(
                pending,
                out var relocatedStart,
                out _)
                ? relocatedStart
                : Math.Max(0, pending.StartOffset);
            nativeReader.ScrollToOffset(startOffset);
            _readerPendingAnnotation = null;
        }
        else if (intent == ReaderNavigationIntent.Bookmark)
        {
            if (nativeReader.RestoreContentPosition(_readerPendingBookmarkContentPosition))
            {
                // The semantic destination survives changes to the global
                // reading mode, font size and viewport since the bookmark.
            }
            else if (!string.IsNullOrWhiteSpace(_readerPendingBookmarkQuote)
                && nativeReader.BodyText is { } body
                && body.IndexOf(_readerPendingBookmarkQuote, StringComparison.Ordinal) is var quoteOffset
                && quoteOffset >= 0
                && body.IndexOf(_readerPendingBookmarkQuote, quoteOffset + 1, StringComparison.Ordinal) < 0)
            {
                // Older bookmarks sometimes have a unique visible quote even
                // though they do not have a portable content anchor yet.
                nativeReader.ScrollToOffset(quoteOffset);
            }
            else if (_readerPendingBookmarkPosition is { } bookmarkPosition)
            {
                if (nativeReader.Vertical)
                {
                    nativeReader.ScrollToOffset((int)Math.Max(0, bookmarkPosition));
                }
                else
                {
                    nativeReader.SeekToPixelScroll(bookmarkPosition);
                }
            }
            else if (!string.IsNullOrWhiteSpace(nativeFragment))
            {
                nativeReader.ScrollToFragment(nativeFragment);
            }
            ClearReaderBookmarkPendingLocation();
        }
        else if (ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
                     intent,
                     target,
                     hasPendingRestorePosition))
        {
            // A plain TOC/progress target can point at the same XHTML that is
            // already loaded. Reset the native page explicitly; otherwise the
            // selection changes while the old page remains visible.
            nativeReader.SeekToBoundary(toEnd: false);
        }
        else if (intent == ReaderNavigationIntent.Footnote && !string.IsNullOrWhiteSpace(nativeFragment))
        {
            await nativeReader.RevealFootnoteAsync(nativeFragment);
        }
        else if (!string.IsNullOrWhiteSpace(nativeFragment))
        {
            nativeReader.ScrollToFragment(nativeFragment);
        }

        await UpdateReaderScrollStateAsync(host);
        UpdateReaderToolbar();
    }

    private static string? GetReaderTargetFragment(Uri target)
    {
        var fragment = target.Fragment.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        return DecodeReaderFragment(fragment);
    }

    private static string? DecodeReaderFragment(string? value)
        => ReaderResourcePolicy.DecodeFragment(value);

    private void SetReaderTocSelection(EpubReaderNavigationItem? item)
    {
        var index = item is null ? -1 : FindReaderTocIndex(item);
        // A selected entry nested under a collapsed parent would be invisible
        // and unreachable, so open its branch before selecting it.
        if (index >= 0 && ExpandReaderTocAncestors(index))
        {
            RefreshReaderTocRows();
        }

        var row = index >= 0 && index < _readerTocRows.Length ? _readerTocRows[index] : null;
        SetReaderTocCurrentRow(row);
        var selectedItems = ReaderTocList.SelectedItems;
        var hasOnlyExpectedSelection = row is null
            ? selectedItems is null || selectedItems.Count == 0
            : selectedItems is { Count: 1 }
                && ReferenceEquals(selectedItems[0], row);
        if (!hasOnlyExpectedSelection)
        {
            _suppressReaderTocSelectionNavigation = true;
            try
            {
                // SelectionMode only constrains pointer input in Avalonia;
                // programmatic SelectedItem assignments can otherwise retain
                // old rows. Keep exactly one visual current-row state.
                if (selectedItems is not null)
                {
                    selectedItems.Clear();
                    if (row is not null)
                        selectedItems.Add(row);
                }
            }
            finally
            {
                _suppressReaderTocSelectionNavigation = false;
            }
        }

        // Scrolling a row that is already on screen is what made the rail
        // creep: the virtualizing panel re-anchors from estimated heights and
        // settles a little lower each time. Only chase a row that is off-screen.
        if (row is not null && ShouldScrollReaderTocRowIntoView(row))
            ReaderTocList.ScrollIntoView(row);
        SetReaderCompactSelectedItem(item);
        if (_readerIsPdf && ReaderPreviousButton is not null && ReaderNextButton is not null)
        {
            ReaderPreviousButton.IsEnabled = index > 0;
            ReaderNextButton.IsEnabled = index >= 0 && index + 1 < _readerTocItems.Count;
        }
    }

    private void SetReaderTocSelectionForChapter(int chapterIndex)
    {
        SetReaderTocSelectionForLocation(chapterIndex, null);
    }

    private void SetReaderTocSelectionForLocation(int chapterIndex, string? fragment)
    {
        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == chapterIndex)
            .ToArray();
        var decodedFragment = DecodeReaderFragment(fragment);
        var selected = string.IsNullOrWhiteSpace(decodedFragment)
            ? null
            : chapterItems.FirstOrDefault(item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    decodedFragment,
                    StringComparison.Ordinal));
        selected ??= chapterItems.FirstOrDefault();
        selected ??= _readerTocItems
            .Where(item => item.ChapterIndex <= chapterIndex)
            .OrderByDescending(item => item.ChapterIndex)
            .FirstOrDefault();
        SetReaderTocSelection(selected);
    }

    private void SyncReaderTocSelectionWithReadingPosition()
    {
        if (_readerIsPdf) { SyncReaderPdfTocSelection(); return; }
        if (_readerDocument is { } document && CurrentReaderHost?.Source is { } source
            && _readerChapterIndex >= 0 && _readerChapterIndex < document.Chapters.Count
            && !ReaderNavigationLocationPolicy.TargetsSameDocument(source, new Uri(document.Chapters[_readerChapterIndex])))
            return;

        var index = GetCurrentReaderTocIndex();
        var item = index >= 0 ? _readerTocItems[index] : null;
        // Preserve the user's TOC scroll and the realized rows while reading
        // within one section. Only a new section needs selection/scrolling.
        if (ReferenceEquals((ReaderTocList.SelectedItem as ReaderTocRow)?.Item, item)) return;
        SetReaderTocSelection(item);
        ReaderChapterText.Text = GetReaderChapterPositionLabel();
    }

    private EpubReaderNavigationItem? FindAdjacentReaderSubchapter(int direction)
    {
        direction = Math.Sign(direction);
        if (direction == 0) return null;

        var chapterItems = _readerTocItems
            .Where(item => item.ChapterIndex == _readerChapterIndex)
            .ToArray();
        if (chapterItems.Length < 2) return null;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        var currentIndex = -1;
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.Equals(
                    GetReaderTargetFragment(target),
                    currentFragment,
                    StringComparison.Ordinal));
        }

        if (currentIndex < 0)
        {
            currentIndex = Array.FindIndex(chapterItems, item =>
                Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                && string.IsNullOrWhiteSpace(GetReaderTargetFragment(target)));
        }

        // When every entry is anchored, the first one normally represents the
        // containing chapter heading and therefore acts as the current base.
        if (currentIndex < 0) currentIndex = 0;
        var targetIndex = currentIndex + direction;
        return targetIndex >= 0 && targetIndex < chapterItems.Length
            ? chapterItems[targetIndex]
            : null;
    }

    private int GetReaderFooterTocIndex()
    {
        if ((ReaderTocList.SelectedItem as ReaderTocRow)?.Item is { } selected
            && (_readerIsPdf || selected.ChapterIndex == _readerChapterIndex)
            && FindReaderTocIndex(selected) is var index && index >= 0)
            return index;
        return _readerIsPdf ? -1 : GetCurrentReaderTocIndex();
    }

    private int GetCurrentReaderTocIndex()
    {
        if (_readerTocItems.Count == 0) return -1;

        var currentFragment = DecodeReaderFragment(_readerCurrentFragment);
        if (!string.IsNullOrWhiteSpace(currentFragment))
        {
            for (var index = 0; index < _readerTocItems.Count; index++)
            {
                var item = _readerTocItems[index];
                if (item.ChapterIndex == _readerChapterIndex
                    && Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                    && string.Equals(
                        GetReaderTargetFragment(target),
                        currentFragment,
                        StringComparison.Ordinal))
                {
                    return index;
                }
            }
        }

        var sameChapterIndex = -1;
        var precedingIndex = -1;
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            var item = _readerTocItems[index];
            if (item.ChapterIndex == _readerChapterIndex && sameChapterIndex < 0)
                sameChapterIndex = index;
            if (item.ChapterIndex <= _readerChapterIndex)
                precedingIndex = index;
        }
        return sameChapterIndex >= 0 ? sameChapterIndex : precedingIndex;
    }
}
