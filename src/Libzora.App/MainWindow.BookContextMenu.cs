using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

namespace Libzora;

/// <summary>
/// Book context menu construction and hover behaviour.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] SupportedLibraryDeleteFormats = ["EPUB", "PDF", "MOBI", "AZW3"];

    private ContextMenu BuildBookContextMenu(BookCardViewModel card) =>
        BuildBookContextMenuCore(card, allowSeriesActions: true);

    private ContextMenu BuildSingleBookContextMenu(BookCardViewModel card) =>
        BuildBookContextMenuCore(card, allowSeriesActions: false);

    private ContextMenu BuildBookContextMenuCore(BookCardViewModel card, bool allowSeriesActions)
    {
        var menu = new ContextMenu();
        var isSeries = allowSeriesActions && card.IsSeriesStack;
        var seriesVolumes = isSeries ? OrderLocalSeriesMembers(card) : [];

        var openMenu = new MenuItem { Header = T("打开书籍") };
        ApplyLegacyMenuItemSize(openMenu);
        openMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        if (isSeries)
            PopulateSeriesOpenItems(openMenu.Items, seriesVolumes);
        else
            PopulateSingleBookOpenItems(openMenu.Items, card);
        openMenu.IsEnabled = openMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(openMenu);

        if (isSeries)
        {
            menu.Items.Add(BuildLocalSeriesMenu(card));
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildSeriesRenameMenu(card, seriesVolumes));
            menu.Items.Add(BuildSeriesOpenDirectoryMenu(seriesVolumes));
        }
        else
        {
            menu.Items.Add(CreateMenuItem(T("重命名书籍"), () => RenameBookFromContextAsync(card)));
            menu.Items.Add(CreateMenuItem(T("打开书籍所在目录"), () => OpenBookDirectoryFromContextAsync(card)));
        }
        menu.Items.Add(new Separator());

        var convertMenu = new MenuItem { Header = T("转换为") };
        ApplyLegacyMenuItemSize(convertMenu);
        convertMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        if (isSeries)
            PopulateSeriesConvertItems(convertMenu.Items, seriesVolumes);
        else
            PopulateSingleBookConvertItems(convertMenu.Items, card);
        menu.Items.Add(convertMenu);
        menu.Items.Add(new Separator());

        if (AddPluginBookActionMenuItems(menu, card, allowSeriesActions: allowSeriesActions))
            menu.Items.Add(new Separator());

        menu.Items.Add(CreateSeriesSendMenuItem(
            T("发送到设备"),
            card,
            book => CurrentDevice is { } targetDevice
                ? DeviceTransferPolicy.GetCandidates(targetDevice.Profile, book.Files)
                : book.Files,
            (volume, sourceFile) => TrackDeviceOperationAsync(
                () => SendSelectedBookToKindleCoreAsync(sourceFile, volume)),
            volumes => SendBooksToKindleAsync(volumes),
            allowSeriesActions: allowSeriesActions,
            orderedMembers: seriesVolumes));
        menu.Items.Add(CreateSeriesSendMenuItem(
            T("发送到 Kindle 邮箱"),
            card,
            book => KindleEmailSelectionPolicy.GetCandidates(book.Files),
            (volume, sourceFile) => SendSelectedBookByEmailCoreAsync(sourceFile, volume),
            volumes => SendBooksByEmailAsync(volumes),
            allowSeriesActions: allowSeriesActions,
            orderedMembers: seriesVolumes));
        if (IsSendToKindleWebEnabled())
        {
            var webMenu = CreateSeriesSendMenuItem(
                T("发送到 Kindle Web"),
                card,
                book => KindleWebFilePolicy.GetCandidates(book.Files),
                (_, sourceFile) => sourceFile is null
                    ? Task.CompletedTask
                    : AddLibraryFilesToKindleWebAsync([sourceFile]),
                volumes =>
                {
                    var files = volumes
                        .Select(volume => KindleWebFilePolicy.GetCandidates(volume.Book.Files).FirstOrDefault())
                        .OfType<BookFile>()
                        .ToArray();
                    return files.Length == 0
                        ? Task.CompletedTask
                        : AddLibraryFilesToKindleWebAsync(files);
                },
                allowSeriesActions: allowSeriesActions,
                disableWhenEmpty: true,
                orderedMembers: seriesVolumes);
            menu.Items.Add(webMenu);
        }

        var collectionMenu = new MenuItem { Header = T("收藏夹") };
        ApplyLegacyMenuItemSize(collectionMenu);
        collectionMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        var customCollections = CollectionFolders
            .Where(folder => !string.Equals(
                folder.Collection.Name,
                BookLibraryDefaults.UncollectedCollectionName,
                StringComparison.Ordinal))
            .ToArray();
        var exclusiveCollections = _appSettings.CollectionsMutuallyExclusive;
        foreach (var folder in customCollections)
        {
            var item = new MenuItem { Header = folder.Name, Tag = folder };
            if (!exclusiveCollections)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = ResolveCollectionTargets(card, allowSeriesActions)
                    .All(target => target.Book.CollectionIds.Contains(folder.Collection.Id));
            }
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await ChangeBookCollectionAsync(card, folder, exclusiveCollections, allowSeriesActions);
            collectionMenu.Items.Add(item);
        }
        if (customCollections.Length > 0)
            collectionMenu.Items.Add(new Separator());
        collectionMenu.Items.Add(CreateMenuItem(T("新建收藏夹…"), async () =>
        {
            var name = await PromptCollectionNameAsync();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var collection = await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
                await AssignBookToCollectionAsync(card, collection.Id, allowSeriesActions);
                await RefreshLibraryAsync();
                SetTaskStatus(T("已创建并加入收藏夹“{0}”。", name));
            }
            catch (Exception exception)
            {
                SetTaskStatus(T("创建收藏夹失败：{0}", UiText.Localize(exception.Message)));
            }
        }));
        if (customCollections.Length > 0)
        {
            var deleteCollectionMenu = new MenuItem { Header = T("解散收藏夹") };
            ApplyLegacyMenuItemSize(deleteCollectionMenu);
            foreach (var folder in customCollections)
                deleteCollectionMenu.Items.Add(CreateMenuItem(folder.Name, () => DissolveCollectionAsync(folder)));
            collectionMenu.Items.Add(deleteCollectionMenu);
        }
        menu.Items.Add(collectionMenu);
        menu.Items.Add(new Separator());

        var deleteFormatMenu = new MenuItem { Header = T("删除格式") };
        ApplyLegacyMenuItemSize(deleteFormatMenu);
        deleteFormatMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        if (isSeries)
        {
            PopulateSeriesDeleteFormatItems(deleteFormatMenu.Items, card, seriesVolumes);
            deleteFormatMenu.IsEnabled = true;
        }
        else
        {
            PopulateSingleBookDeleteFormatItems(deleteFormatMenu.Items, card, fixedFormatWidth: 80);
            deleteFormatMenu.IsEnabled = deleteFormatMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
            deleteFormatMenu.Items.Add(new Separator());
            deleteFormatMenu.Items.Add(CreateMenuItem(T("全部"), () => DeleteBookFromContextAsync(card)));
        }
        menu.Items.Add(deleteFormatMenu);

        AttachInstantMenuHover(menu);
        return menu;
    }

    private void PopulateSingleBookOpenItems(ItemCollection items, BookCardViewModel card)
    {
        var supportedFiles = ReaderBookSelectionPolicy.GetSupportedFiles(card.Book.Files);
        foreach (var fileGroup in supportedFiles.GroupBy(
                     file => file.Format.Trim().TrimStart('.'),
                     StringComparer.OrdinalIgnoreCase))
        {
            var files = fileGroup.ToArray();
            var format = fileGroup.Key.ToUpperInvariant();
            if (files.Length == 1)
            {
                var isPinyinVersion = PinyinBookPolicy.IsGeneratedPinyinVersion(files[0]);
                var item = new MenuItem
                {
                    Header = isPinyinVersion
                        ? $"{format} · {GetBookFileMenuLabel(files[0])}"
                        : format,
                    Width = isPinyinVersion ? double.NaN : 80,
                    MinWidth = 0,
                    IsEnabled = true,
                    Tag = files[0]
                };
                ApplyLegacyMenuItemSize(item);
                item.Click += async (_, _) =>
                {
                    if (item.Tag is BookFile selectedFile)
                        await OpenBookAsync(card, selectedFile);
                };
                items.Add(item);
                continue;
            }

            // Keep same-format editions together, but expose every file. This
            // is important for translation: original, translated and
            // bilingual EPUBs intentionally belong to one book record.
            var formatMenu = new MenuItem
            {
                Header = format,
                Width = 80,
                MinWidth = 0,
                IsEnabled = true
            };
            ApplyLegacyMenuItemSize(formatMenu);
            formatMenu.Resources["FlyoutThemeMinWidth"] = 0d;
            PopulateBookFileVariantItems(
                formatMenu.Items,
                files,
                file => OpenBookAsync(card, file));
            items.Add(formatMenu);
        }
    }

    private void PopulateSingleBookConvertItems(ItemCollection items, BookCardViewModel card)
    {
        foreach (var target in new[] { "epub", "azw3", "pdf" })
        {
            var sourceCandidates = BookFormatConversionPolicy.GetSourceCandidates(card.Book.Files, target);
            var targetMenu = CreateBookVariantMenuItem(
                target.ToUpperInvariant(),
                sourceCandidates,
                sourceFile => ConvertBookAsync(card, target, sourceFile),
                width: 80);
            targetMenu.IsEnabled = sourceCandidates.Count > 0
                && !card.Book.Files.Any(file => string.Equals(
                    file.Format.Trim().TrimStart('.'), target, StringComparison.OrdinalIgnoreCase));
            items.Add(targetMenu);
        }
    }

    private void PopulateSingleBookDeleteFormatItems(
        ItemCollection items,
        BookCardViewModel card,
        double? fixedFormatWidth)
    {
        foreach (var format in SupportedLibraryDeleteFormats)
        {
            var formatFiles = card.Book.Files
                .Where(candidate => string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (formatFiles.Length > 1)
            {
                // A book can contain several EPUBs (original, translated,
                // bilingual, and pinyin). Keep the format entry, but expose
                // every file below it so no edition is hidden behind the
                // first matching format.
                items.Add(CreateBookVariantMenuItem(
                    format,
                    formatFiles,
                    file => file is null ? Task.CompletedTask : DeleteFileAsync(card, file),
                    width: fixedFormatWidth));
                continue;
            }

            var file = formatFiles.FirstOrDefault();
            var isPinyinVersion = file is not null && PinyinBookPolicy.IsGeneratedPinyinVersion(file);
            var item = new MenuItem
            {
                Header = isPinyinVersion
                    ? $"{format} · {GetBookFileMenuLabel(file!)}"
                    : format,
                Width = isPinyinVersion || fixedFormatWidth is null ? double.NaN : fixedFormatWidth.Value,
                MinWidth = 0,
                IsEnabled = file is not null,
                Tag = file
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) =>
            {
                if (item.Tag is BookFile selectedFile)
                    await DeleteFileAsync(card, selectedFile);
            };
            items.Add(item);
        }
    }

    private static void PopulateBookFileVariantItems(
        ItemCollection items,
        IReadOnlyList<BookFile> files,
        Func<BookFile, Task> action)
    {
        var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var baseLabel = GetBookFileMenuLabel(file);
            labels.TryGetValue(baseLabel, out var occurrence);
            occurrence++;
            labels[baseLabel] = occurrence;
            var label = occurrence == 1 ? baseLabel : $"{baseLabel} ({occurrence})";
            items.Add(CreateMenuItem(label, () => action(file)));
        }
    }

    private static string GetBookFileMenuLabel(BookFile file)
    {
        // File choices must expose the extension so different formats of the
        // same book remain distinguishable in transfer and conversion menus.
        var name = Path.GetFileName(file.RelativePath).Trim();
        var extension = file.Format.Trim().TrimStart('.');
        if (string.IsNullOrWhiteSpace(name))
            return $".{extension}";
        var suffix = $".{extension}";
        var stem = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
        // Some imported files carry a 32-character identifier. Keep it on
        // disk, but omit it from the menu so it cannot crowd out the format.
        var identifierStart = stem.LastIndexOf('_');
        if (identifierStart >= 0 && stem.Length - identifierStart - 1 == 32
            && stem.AsSpan(identifierStart + 1).IndexOfAnyExcept(
                "0123456789abcdefABCDEF".AsSpan()) < 0)
            stem = stem[..identifierStart];
        // Stored names may end in a dotted content hash rather than an actual
        // extension. Only the recorded format identifies the file suffix.
        return $"{stem}{suffix}";
    }

    private static MenuItem CreateBookVariantMenuItem(
        string header,
        IReadOnlyList<BookFile> files,
        Func<BookFile?, Task> action,
        double? width = null)
    {
        MenuItem item;
        if (files.Count <= 1)
        {
            item = CreateMenuItem(header, () => action(files.FirstOrDefault()));
        }
        else
        {
            item = new MenuItem { Header = header };
            ApplyLegacyMenuItemSize(item);
            item.Resources["FlyoutThemeMinWidth"] = 0d;
            PopulateBookFileVariantItems(item.Items, files, file => action(file));
        }

        if (width is { } fixedWidth)
        {
            item.Width = fixedWidth;
            item.MinWidth = 0;
        }

        return item;
    }

    private static MenuItem CreateSeriesSendMenuItem(
        string header,
        BookCardViewModel card,
        Func<Book, IReadOnlyList<BookFile>> getCandidates,
        Func<BookCardViewModel, BookFile?, Task> sendSingle,
        Func<IReadOnlyList<BookCardViewModel>, Task> sendAll,
        bool allowSeriesActions = true,
        bool disableWhenEmpty = false,
        IReadOnlyList<BookCardViewModel>? orderedMembers = null)
    {
        if (!allowSeriesActions || !card.IsSeriesStack)
        {
            var files = getCandidates(card.Book);
            var singleItem = CreateBookVariantMenuItem(
                header,
                files,
                sourceFile => sendSingle(card, sourceFile));
            if (disableWhenEmpty && files.Count == 0)
                singleItem.IsEnabled = false;
            return singleItem;
        }

        var members = orderedMembers is { Count: > 0 } ? orderedMembers : card.SeriesMembers;
        var parent = new MenuItem { Header = header };
        ApplyLegacyMenuItemSize(parent);
        parent.Resources["FlyoutThemeMinWidth"] = 0d;
        var hasEligibleVolume = members.Any(volume => getCandidates(volume.Book).Count > 0);
        var sendAllItem = CreateMenuItem(
            T("全部发送（{0} 本）", members.Count),
            () => sendAll(members));
        if (disableWhenEmpty && !hasEligibleVolume)
            sendAllItem.IsEnabled = false;
        parent.Items.Add(sendAllItem);
        parent.Items.Add(new Separator());
        foreach (var member in members)
        {
            var volume = member;
            var volumeFiles = getCandidates(volume.Book);
            var volumeItem = CreateBookVariantMenuItem(
                SeriesVolumeLabel(volume.Book.SeriesIndex, volume.Title),
                volumeFiles,
                sourceFile => sendSingle(volume, sourceFile));
            if (disableWhenEmpty && volumeFiles.Count == 0)
                volumeItem.IsEnabled = false;
            parent.Items.Add(volumeItem);
        }
        if (disableWhenEmpty && !hasEligibleVolume)
            parent.IsEnabled = false;
        return parent;
    }

    private async Task OpenBookFormatAsync(BookCardViewModel card, string format)
    {
        var file = ReaderBookSelectionPolicy.GetSupportedFiles(card.Book.Files)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            SetTaskStatus(T("所选格式文件不存在或不支持。"));
            return;
        }

        await OpenBookAsync(card, file);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        ApplyLegacyMenuItemSize(item);
        item.Click += async (_, _) => await action();
        return item;
    }

    private static void ApplyLegacyMenuItemSize(MenuItem item)
    {
        // Book names are literal text: the default access-key presenter hides
        // underscores (for example, "规模_pinyin" becomes "规模pinyin").
        item.HeaderTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>(
            (text, _) =>
            {
                var textBlock = new TextBlock { Text = text };
                textBlock[!TextBlock.ForegroundProperty] = new Binding
                {
                    Path = nameof(MenuItem.Foreground),
                    RelativeSource = new RelativeSource
                    {
                        Mode = RelativeSourceMode.FindAncestor,
                        AncestorType = typeof(MenuItem)
                    }
                };
                return textBlock;
            });
        // Avalonia uses device-independent units: 32 units become the legacy
        // menu's 40 physical pixels on a 125%-scaled Windows display.
        item.Height = 32;
        item.MinHeight = 32;
        item.MaxHeight = 32;
        item.Padding = new Thickness(8, 4, 8, 7);
    }

    private static void AttachInstantMenuHover(ContextMenu menu)
    {
        foreach (var item in EnumerateMenuItems(menu.Items))
            item.PointerEntered += (_, _) => ActivateInstantMenuBranch(menu.Items, item);

        menu.Closed += (_, _) => ClearInstantMenuHover(menu.Items);
    }

    private static void AttachInstantMenuHover(MenuFlyout menu)
    {
        foreach (var item in EnumerateMenuItems(menu.Items))
            item.PointerEntered += (_, _) => ActivateInstantMenuBranch(menu.Items, item);

        menu.Closed += (_, _) => ClearInstantMenuHover(menu.Items);
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in EnumerateMenuItems(item.Items))
                yield return child;
        }
    }

    private static bool ActivateInstantMenuBranch(IEnumerable<object?> items, MenuItem activeItem)
    {
        var containsActiveItem = false;
        foreach (var item in items.OfType<MenuItem>())
        {
            var activeChild = ActivateInstantMenuBranch(item.Items, activeItem);
            var isActiveBranch = ReferenceEquals(item, activeItem) || activeChild;
            SetInstantMenuHover(item, isActiveBranch);

            if (!isActiveBranch && item.IsSubMenuOpen)
                item.IsSubMenuOpen = false;

            containsActiveItem |= isActiveBranch;
        }

        return containsActiveItem;
    }

    private static void ClearInstantMenuHover(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            SetInstantMenuHover(item, false);
            ClearInstantMenuHover(item.Items);
        }
    }

    private static void SetInstantMenuHover(MenuItem item, bool isActive)
    {
        if (isActive)
            item.Classes.Add(InstantMenuHoverClass);
        else
            item.Classes.Remove(InstantMenuHoverClass);
    }

    // 右键菜单流程会同步 ListBox 选中项，但 SelectionChanged 处理器里的
    // SelectBook 会弹出详情页；用该标志在右键期间抑制弹窗行为。
    private bool _suppressSelectionPane;
    // ListBox 原生「右键按下即选中」在我们收到 ContextRequested 之前就会
    // 触发一次 SelectionChanged；此标志把随之而来的弹面板抑制掉。
    private bool _suppressNextSelectionPane;
}
