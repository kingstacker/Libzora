using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Series stack interactions shared by the computer and device shelves.
/// </summary>
public partial class MainWindow
{
    private void BookSeriesBadge_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCardViewModel card } target
            || !card.IsSeriesStack)
            return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var member in card.SeriesMembers)
        {
            var volume = member;
            menu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                () => PromoteLocalSeriesVolumeAsync(card, volume)));
        }
        menu.ShowAt(target);
        e.Handled = true;
    }

    private MenuItem BuildLocalSeriesMenu(BookCardViewModel card)
    {
        var menu = new MenuItem
        {
            Header = T("展开系列：{0}", card.SeriesTitle ?? card.SeriesCountLabel)
        };
        ApplyLegacyMenuItemSize(menu);
        menu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var member in card.SeriesMembers)
        {
            var volume = member;
            menu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                () => PromoteLocalSeriesVolumeAsync(card, volume)));
        }
        return menu;
    }

    private Task PromoteLocalSeriesVolumeAsync(BookCardViewModel stack, BookCardViewModel member)
    {
        ViewModel.PromoteSeriesVolume(stack, member);
        var promoted = ViewModel.ShelfBooks.FirstOrDefault(card => ReferenceEquals(card, member));
        if (promoted is null) return Task.CompletedTask;

        _selectedBookIds.Clear();
        _selectedBookIds.Add(promoted.Book.Id);
        _selectedCard = promoted;
        _multiSelectAnchor = promoted;
        SelectBook(promoted);
        UpdateMultiSelectionUi();
        return Task.CompletedTask;
    }

    private void DeviceSeriesBadge_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: KindleBookCardViewModel card } target
            || !card.IsSeriesStack)
            return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var member in card.SeriesMembers)
        {
            var volume = member;
            menu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(null, volume.Title),
                () => PromoteDeviceSeriesVolume(stack: card, member: volume)));
        }
        menu.ShowAt(target);
        e.Handled = true;
    }

    private MenuItem BuildDeviceSeriesMenu(KindleBookCardViewModel card)
    {
        var menu = new MenuItem
        {
            Header = T("展开系列：{0}", card.SeriesTitle ?? card.SeriesCountLabel)
        };
        ApplyLegacyMenuItemSize(menu);
        menu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var member in card.SeriesMembers)
        {
            var volume = member;
            menu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(null, volume.Title),
                () => PromoteDeviceSeriesVolume(stack: card, member: volume)));
        }
        return menu;
    }

    private Task PromoteDeviceSeriesVolume(
        KindleBookCardViewModel stack,
        KindleBookCardViewModel member)
    {
        if (string.IsNullOrWhiteSpace(stack.SeriesKey)) return Task.CompletedTask;
        _deviceSeriesFronts[stack.SeriesKey!] = member.Book.RelativePath;
        RebuildDeviceShelfBooks();
        foreach (var candidate in DeviceBooks)
            candidate.IsSelected = ReferenceEquals(candidate, member);
        _deviceMultiSelectAnchor = member;
        UpdateDeviceBookSelectionUi();
        return Task.CompletedTask;
    }

    private static string SeriesVolumeLabel(double? index, string title) =>
        index is { } position && double.IsFinite(position) && position > 0
            ? $"{position:0.#} · {title}"
            : title;
}
