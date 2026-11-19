using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Libzora;

/// <summary>
/// TOC row building and refresh.
/// </summary>
public partial class MainWindow
{
    private void BuildReaderTocRows()
    {
        // Re-realising the source is a full rebuild of the rail, so the spacer
        // repair runs at most once per book open.
        _readerTocTopSpacerRepaired = false;
        _readerTocRows = _readerTocItems
            .Select((item, index) => new ReaderTocRow(
                item,
                HasReaderTocChildren(index),
                isExpanded: false))
            .ToArray();
    }

    private bool HasReaderTocChildren(int index) =>
        index + 1 < _readerTocItems.Count
        && _readerTocItems[index + 1].Level > _readerTocItems[index].Level;

    private bool ExpandReaderTocAncestors(int index)
    {
        if (index < 0 || index >= _readerTocRows.Length) return false;
        var changed = false;
        var level = _readerTocRows[index].Level;
        for (var scan = index - 1; scan >= 0 && level > 0; scan--)
        {
            if (_readerTocRows[scan].Level >= level) continue;
            if (!_readerTocRows[scan].IsExpanded)
            {
                _readerTocRows[scan].IsExpanded = true;
                changed = true;
            }
            level = _readerTocRows[scan].Level;
        }
        return changed;
    }

    private void SetReaderTocCurrentRow(ReaderTocRow? current)
    {
        foreach (var row in _readerTocRows)
            row.IsCurrent = ReferenceEquals(row, current);
    }

    private void RefreshReaderTocRows()
    {
        // The rows are already in tree order. Skip a collapsed subtree in a
        // single pass instead of walking back through every preceding sibling
        // for each chapter of a large novel.
        var visible = new List<ReaderTocRow>();
        int? collapsedLevel = null;
        foreach (var row in _readerTocRows)
        {
            if (collapsedLevel is { } level && row.Level > level) continue;
            collapsedLevel = row.HasChildren && !row.IsExpanded ? row.Level : null;
            visible.Add(row);
        }

        // Replacing ItemsSource resets the virtualizing panel, which throws
        // away the measured row heights it estimates scroll offsets from. Rows
        // wrap, so re-estimating lands the rail a few pixels off and the list
        // creeps downward each time. Selecting a row inside an already open
        // branch changes nothing here, so leave the panel alone.
        if (!ReaderTocScrollPolicy.RequiresRowRebuild(
                ReaderTocList.ItemsSource as IReadOnlyList<ReaderTocRow>,
                visible))
        {
            return;
        }

        var selected = ReaderTocList.SelectedItem as ReaderTocRow;
        _suppressReaderTocSelectionNavigation = true;
        try
        {
            // Avalonia allows multiple programmatic selections even when the
            // control is single-select for pointer input. Clear the old set
            // before rebuilding the folded source, otherwise recycled item
            // containers can keep painting every previously visited row.
            if (PdfAnnotTrace.Enabled) PdfAnnotTrace.Write($"toc rows replace: {visible.Count} rows");
            ReaderTocList.SelectedItems?.Clear();
            ReaderTocList.ItemsSource = visible;
            // Replacing the source drops the selection; restore it when the
            // row survived the fold so the current chapter stays marked.
            if (selected is not null && visible.Contains(selected))
                ReaderTocList.SelectedItems?.Add(selected);
        }
        finally
        {
            _suppressReaderTocSelectionNavigation = false;
        }
    }

    // Opening a book with every branch expanded buries the structure the fold
    // exists to expose (one library book yields 380 entries). Show the top
    // level plus the branch that contains the restored reading position.
    private void CollapseReaderTocToCurrentChapter(int chapterIndex)
    {
        foreach (var row in _readerTocRows) row.IsExpanded = false;

        var current = -1;
        for (var index = 0; index < _readerTocItems.Count; index++)
        {
            if (_readerTocItems[index].ChapterIndex > chapterIndex) break;
            current = index;
        }

        if (current >= 0) ExpandReaderTocAncestors(current);
        RefreshReaderTocRows();
    }

    private void ReaderTocToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ReaderTocRow row } || !row.HasChildren) return;
        row.IsExpanded = !row.IsExpanded;
        RefreshReaderTocRows();
        // The row itself must not navigate: folding is a view action.
        e.Handled = true;
    }

    // Read-only lookup: the auto-hide scroll bar owns _readerTocScrollViewer
    // together with its event subscriptions, so never assign it from here.
    private ScrollViewer? GetReaderTocScrollViewer() =>
        _readerTocScrollViewer ?? FindDescendants<ScrollViewer>(ReaderTocList).FirstOrDefault();

    private bool ShouldScrollReaderTocRowIntoView(ReaderTocRow row)
    {
        // The presenter's bounds are the viewport itself, so measuring the row
        // in its coordinate space keeps any template padding out of the sum.
        if (GetReaderTocScrollViewer() is not { } scrollViewer
            || FindDescendants<ScrollContentPresenter>(scrollViewer).FirstOrDefault() is not { } viewport)
        {
            // Nothing to scroll before the rail is templated; opening the TOC
            // re-syncs the selection and places the row then.
            return false;
        }

        // No container means the panel virtualized the row away, so it is
        // off-screen by definition.
        if (ReaderTocList.ContainerFromItem(row) is not { } container) return true;

        try
        {
            if (container.TranslatePoint(default, viewport) is not { } origin) return true;
            return ReaderTocScrollPolicy.RequiresScrollIntoView(
                origin.Y,
                container.Bounds.Height,
                viewport.Bounds.Height);
        }
        catch (InvalidOperationException)
        {
            // The row may be between visual trees during a layout pass; the
            // next selection update will place it.
            return false;
        }
    }

    private void ReaderTocList_TemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        QueueReaderTocScrollBarAttach();
    }
}
