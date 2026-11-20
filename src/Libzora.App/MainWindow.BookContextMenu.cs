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
    private ContextMenu BuildBookContextMenu(BookCardViewModel card)
    {
        var menu = new ContextMenu();

        var openMenu = new MenuItem { Header = T("打开书籍") };
        ApplyLegacyMenuItemSize(openMenu);
        openMenu.Resources["FlyoutThemeMinWidth"] = 0d;
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
                openMenu.Items.Add(item);
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
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var baseLabel = GetBookFileMenuLabel(file);
                labels.TryGetValue(baseLabel, out var occurrence);
                occurrence++;
                labels[baseLabel] = occurrence;
                var label = occurrence == 1 ? baseLabel : $"{baseLabel} ({occurrence})";
                formatMenu.Items.Add(CreateMenuItem(
                    label,
                    () => OpenBookAsync(card, file)));
            }
            openMenu.Items.Add(formatMenu);
        }
        openMenu.IsEnabled = openMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(openMenu);
        if (card.IsSeriesStack)
        {
            menu.Items.Add(BuildLocalSeriesMenu(card));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(CreateMenuItem(T("重命名书籍"), () => RenameBookFromContextAsync(card)));
        menu.Items.Add(CreateMenuItem(T("打开书籍所在目录"), () => OpenBookDirectoryFromContextAsync(card)));
        menu.Items.Add(new Separator());

        var convertMenu = new MenuItem { Header = T("转换为") };
        ApplyLegacyMenuItemSize(convertMenu);
        convertMenu.Resources["FlyoutThemeMinWidth"] = 0d;
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
            convertMenu.Items.Add(targetMenu);
        }
        menu.Items.Add(convertMenu);
        menu.Items.Add(new Separator());

        if (AddPluginBookActionMenuItems(menu, card))
            menu.Items.Add(new Separator());

        menu.Items.Add(CreateBookVariantMenuItem(
            T("发送到设备"),
            CurrentDevice is { } targetDevice
                ? DeviceTransferPolicy.GetCandidates(targetDevice.Profile, card.Book.Files)
                : card.Book.Files,
            sourceFile => SendSelectedBookToKindleCoreAsync(sourceFile)));
        menu.Items.Add(CreateBookVariantMenuItem(
            T("发送到 Kindle 邮箱"),
            KindleEmailSelectionPolicy.GetCandidates(card.Book.Files),
            sourceFile => SendSelectedBookByEmailCoreAsync(sourceFile)));
        if (IsSendToKindleWebEnabled())
        {
            var webFiles = KindleWebFilePolicy.GetCandidates(card.Book.Files);
            var webMenu = CreateBookVariantMenuItem(
                T("发送到 Kindle Web"),
                webFiles,
                sourceFile => sourceFile is null ? Task.CompletedTask : AddLibraryFilesToKindleWebAsync([sourceFile]));
            webMenu.IsEnabled = webFiles.Count > 0;
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
                item.IsChecked = card.Book.CollectionIds.Contains(folder.Collection.Id);
            }
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await ChangeBookCollectionAsync(card, folder, exclusiveCollections);
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
                await AssignBookToCollectionAsync(card, collection.Id);
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
        foreach (var format in new[] { "EPUB", "PDF", "MOBI", "AZW3" })
        {
            var files = card.Book.Files.Where(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
            var formatFiles = files.ToArray();
            if (formatFiles.Length > 1)
            {
                // A book can contain several EPUBs (original, translated,
                // bilingual, and pinyin). Keep the format entry, but expose
                // every file below it so no edition is hidden behind the
                // first matching format.
                deleteFormatMenu.Items.Add(CreateBookVariantMenuItem(
                    format,
                    formatFiles,
                    file => file is null ? Task.CompletedTask : DeleteFileAsync(file),
                    width: 80));
                continue;
            }

            var file = formatFiles.FirstOrDefault();
            var isPinyinVersion = file is not null && PinyinBookPolicy.IsGeneratedPinyinVersion(file);
            var item = new MenuItem
            {
                Header = isPinyinVersion
                    ? $"{format} · {GetBookFileMenuLabel(file!)}"
                    : format,
                Width = isPinyinVersion ? double.NaN : 80,
                MinWidth = 0,
                IsEnabled = file is not null,
                Tag = file
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) =>
            {
                if (item.Tag is BookFile selectedFile)
                    await DeleteFileAsync(selectedFile);
            };
            deleteFormatMenu.Items.Add(item);
        }
        deleteFormatMenu.IsEnabled = deleteFormatMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(deleteFormatMenu);
        deleteFormatMenu.Items.Add(new Separator());
        deleteFormatMenu.Items.Add(CreateMenuItem(T("全部"), () => DeleteBookFromContextAsync(card)));

        AttachInstantMenuHover(menu);
        return menu;
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
            var labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var baseLabel = GetBookFileMenuLabel(file);
                labels.TryGetValue(baseLabel, out var occurrence);
                occurrence++;
                labels[baseLabel] = occurrence;
                var label = occurrence == 1 ? baseLabel : $"{baseLabel} ({occurrence})";
                item.Items.Add(CreateMenuItem(label, () => action(file)));
            }
        }

        if (width is { } fixedWidth)
        {
            item.Width = fixedWidth;
            item.MinWidth = 0;
        }

        return item;
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
