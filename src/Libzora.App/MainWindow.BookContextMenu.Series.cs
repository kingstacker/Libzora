using Avalonia.Controls;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Series-stack context menu builders balancing whole-series batch operations,
/// per-volume actions, and multi-format/multi-edition file choices.
/// </summary>
public partial class MainWindow
{
    private void PopulateSeriesOpenItems(
        ItemCollection items,
        IReadOnlyList<BookCardViewModel> volumes)
    {
        foreach (var member in volumes)
        {
            var volume = member;
            var label = SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title);
            var supportedFiles = ReaderBookSelectionPolicy.GetSupportedFiles(volume.Book.Files);
            if (supportedFiles.Count == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = label,
                    IsEnabled = false
                };
                ApplyLegacyMenuItemSize(emptyItem);
                items.Add(emptyItem);
                continue;
            }

            if (supportedFiles.Count == 1)
            {
                var singleFile = supportedFiles[0];
                var isPinyin = PinyinBookPolicy.IsGeneratedPinyinVersion(singleFile);
                var header = isPinyin
                    ? $"{label} · {GetBookFileMenuLabel(singleFile)}"
                    : label;
                items.Add(CreateMenuItem(header, () => OpenBookAsync(volume, singleFile)));
                continue;
            }

            var volumeMenu = new MenuItem { Header = label };
            ApplyLegacyMenuItemSize(volumeMenu);
            volumeMenu.Resources["FlyoutThemeMinWidth"] = 0d;

            var distinctFormatCount = supportedFiles
                .Select(file => file.Format.Trim().TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinctFormatCount == 1)
            {
                // Keep same-format multi-edition volumes at one submenu depth.
                PopulateBookFileVariantItems(
                    volumeMenu.Items,
                    supportedFiles,
                    file => OpenBookAsync(volume, file));
            }
            else
            {
                PopulateSingleBookOpenItems(volumeMenu.Items, volume);
            }

            items.Add(volumeMenu);
        }
    }

    private MenuItem BuildSeriesRenameMenu(
        BookCardViewModel stack,
        IReadOnlyList<BookCardViewModel> volumes)
    {
        var renameMenu = new MenuItem { Header = T("重命名") };
        ApplyLegacyMenuItemSize(renameMenu);
        renameMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        renameMenu.Items.Add(CreateMenuItem(
            T("重命名系列（{0} 本）", volumes.Count),
            () => RenameSeriesFromContextAsync(stack)));
        renameMenu.Items.Add(new Separator());
        foreach (var member in volumes)
        {
            var volume = member;
            renameMenu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                () => RenameBookFromContextAsync(volume)));
        }
        return renameMenu;
    }

    private MenuItem BuildSeriesOpenDirectoryMenu(IReadOnlyList<BookCardViewModel> volumes)
    {
        var directoryMenu = new MenuItem { Header = T("打开书籍所在目录") };
        ApplyLegacyMenuItemSize(directoryMenu);
        directoryMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var member in volumes)
        {
            var volume = member;
            directoryMenu.Items.Add(CreateMenuItem(
                SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                () => OpenBookDirectoryFromContextAsync(volume)));
        }
        return directoryMenu;
    }

    private void PopulateSeriesConvertItems(
        ItemCollection items,
        IReadOnlyList<BookCardViewModel> volumes)
    {
        foreach (var target in new[] { "epub", "azw3", "pdf" })
        {
            var targetMenu = new MenuItem
            {
                Header = target.ToUpperInvariant(),
                Width = 80,
                MinWidth = 0
            };
            ApplyLegacyMenuItemSize(targetMenu);
            targetMenu.Resources["FlyoutThemeMinWidth"] = 0d;

            var eligibleVolumes = new List<BookCardViewModel>();
            var volumeItems = new List<MenuItem>();
            foreach (var member in volumes)
            {
                var volume = member;
                var sourceCandidates = BookFormatConversionPolicy.GetSourceCandidates(volume.Book.Files, target);
                var canConvert = sourceCandidates.Count > 0 && !HasBookFormat(volume.Book.Files, target);
                if (canConvert)
                    eligibleVolumes.Add(volume);

                var volumeItem = CreateBookVariantMenuItem(
                    SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                    sourceCandidates,
                    sourceFile => ConvertBookAsync(volume, target, sourceFile));
                volumeItem.IsEnabled = canConvert;
                volumeItems.Add(volumeItem);
            }

            var convertAllItem = CreateMenuItem(
                T("全部转换（{0} 本）", eligibleVolumes.Count),
                () => ConvertSeriesBooksAsync(eligibleVolumes, target));
            convertAllItem.IsEnabled = eligibleVolumes.Count > 0;
            targetMenu.Items.Add(convertAllItem);
            targetMenu.Items.Add(new Separator());
            foreach (var volumeItem in volumeItems)
                targetMenu.Items.Add(volumeItem);

            targetMenu.IsEnabled = eligibleVolumes.Count > 0;
            items.Add(targetMenu);
        }
    }

    private void PopulateSeriesDeleteFormatItems(
        ItemCollection items,
        BookCardViewModel stack,
        IReadOnlyList<BookCardViewModel> volumes)
    {
        foreach (var format in SupportedLibraryDeleteFormats)
        {
            var matchedVolumes = volumes
                .Select(volume => (
                    Volume: volume,
                    Files: volume.Book.Files
                        .Where(candidate => string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase))
                        .ToArray()))
                .Where(entry => entry.Files.Length > 0)
                .ToArray();
            var totalFiles = matchedVolumes.Sum(entry => entry.Files.Length);
            if (totalFiles == 0)
            {
                var emptyItem = new MenuItem
                {
                    Header = format,
                    IsEnabled = false
                };
                ApplyLegacyMenuItemSize(emptyItem);
                items.Add(emptyItem);
                continue;
            }

            var formatMenu = new MenuItem { Header = format };
            ApplyLegacyMenuItemSize(formatMenu);
            formatMenu.Resources["FlyoutThemeMinWidth"] = 0d;

            if (totalFiles > 1)
            {
                formatMenu.Items.Add(CreateMenuItem(
                    T("删除全部 {0}（{1} 个）", format, totalFiles),
                    () => DeleteSeriesFormatFilesAsync(stack, format)));
                formatMenu.Items.Add(new Separator());
            }

            foreach (var (volume, files) in matchedVolumes)
            {
                var label = SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title);
                if (files.Length == 1)
                {
                    var file = files[0];
                    var itemHeader = PinyinBookPolicy.IsGeneratedPinyinVersion(file)
                        ? $"{label} · {GetBookFileMenuLabel(file)}"
                        : label;
                    formatMenu.Items.Add(CreateMenuItem(
                        itemHeader,
                        () => DeleteFileAsync(volume, file)));
                }
                else
                {
                    var volumeFormatMenu = CreateBookVariantMenuItem(
                        label,
                        files,
                        file => file is null ? Task.CompletedTask : DeleteFileAsync(volume, file));
                    formatMenu.Items.Add(volumeFormatMenu);
                }
            }

            items.Add(formatMenu);
        }

        items.Add(new Separator());

        var byVolumeMenu = new MenuItem { Header = T("按分册删除") };
        ApplyLegacyMenuItemSize(byVolumeMenu);
        byVolumeMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var member in volumes)
        {
            var volume = member;
            var volumeMenu = new MenuItem
            {
                Header = SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title)
            };
            ApplyLegacyMenuItemSize(volumeMenu);
            volumeMenu.Resources["FlyoutThemeMinWidth"] = 0d;
            PopulateSingleBookDeleteFormatItems(volumeMenu.Items, volume, fixedFormatWidth: 80);
            volumeMenu.Items.Add(new Separator());
            volumeMenu.Items.Add(CreateMenuItem(T("全部"), () => DeleteBookFromContextAsync(volume)));
            byVolumeMenu.Items.Add(volumeMenu);
        }
        items.Add(byVolumeMenu);

        items.Add(new Separator());
        items.Add(CreateMenuItem(
            T("全部（{0} 本）", volumes.Count),
            () => DeleteSeriesFromContextAsync(stack)));
    }
}
