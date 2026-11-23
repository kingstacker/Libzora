using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Reading materials and highlights collected from the library and
/// connected devices.
/// </summary>
public partial class MainWindow
{
    private async void ReaderNotesNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        SetReadingMaterialsExportMode(false);
        ShowStage3Page(ReadingMaterialsPage);
        ReadingMaterialsPageTitle.Text = T("笔记管理");
        ReadingMaterialsStatusText.Text = T("统一浏览本地书籍与设备的划线、笔记、批注和读后思考。");
        ReadingMaterialsNotesActions.IsVisible = true;
        ReadingMaterialsSummaryBorder.IsVisible = true;
        await RefreshReadingMaterialsAsync();
    }

    private void ExportReadingMaterialsToggleButton_Click(object? sender, RoutedEventArgs e)
        => SetReadingMaterialsExportMode(!_readingMaterialsExportMode);

    private void CancelReadingMaterialsExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readingMaterialsExportInProgress) return;
        SetReadingMaterialsExportMode(false);
        ExportReadingMaterialsToggleButton.Focus();
    }

    private void SetReadingMaterialsExportMode(bool enabled)
    {
        _readingMaterialsExportMode = enabled;
        SetReadingMaterialsSelection(false);
        if (enabled)
            foreach (var item in ReadingMaterials) item.IsSelected = true;
        ReadingMaterialsExportPanel.IsVisible = enabled;
        SelectAllReadingMaterialsButton.IsVisible = enabled;
        ExportReadingMaterialsToggleButton.IsVisible = !enabled;
        ReadingMaterialsLocateButton.IsVisible = !enabled;
        DeleteReadingMaterialsButton.IsVisible = !enabled;
        RefreshReadingMaterialsButton.IsVisible = !enabled;
        ReadingMaterialsSummaryText.IsVisible = !enabled;
        ReadingMaterialsExportSummaryText.IsVisible = enabled;
        UpdateKindleAnnotationSyncButtonVisibility();
        ApplyReadingMaterialsFilter();
    }

    private bool TryExitReadingMaterialsExport(KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_readingMaterialsExportMode
            || !ReadingMaterialsPage.IsVisible || ReaderRoot.IsVisible
            || _readingMaterialsExportInProgress
            || _bookRemovalCompletion is not null || _deviceDuplicateCompletion is not null)
            return false;
        // Esc closes the source menu first, then leaves export on the next press.
        if (ReadingMaterialsSourceBox.IsDropDownOpen) return false;
        SetReadingMaterialsExportMode(false);
        ExportReadingMaterialsToggleButton.Focus();
        e.Handled = true;
        return true;
    }

    private async Task RefreshReadingMaterialsAsync()
    {
        var selectedKeys = _allStage3ReadingMaterials.Where(item => item.IsSelected)
            .Select(GetReadingMaterialSelectionKey).ToHashSet();
        RememberReadingMaterialGroupStates();
        foreach (var item in _allStage3ReadingMaterials) item.Dispose();
        _allStage3ReadingMaterials.Clear();
        ReadingMaterials.Clear();
        foreach (var group in ReadingMaterialGroups) group.Dispose();
        ReadingMaterialGroups.Clear();
        _readingMaterialCoverPaths.Clear();
        try
        {
            if (_deviceWarmTask is not null)
                await _deviceWarmTask;
            // Kindle covers below come from DeviceBooks. A same-identity
            // reconnect never re-arms _deviceWarmTask and leaves that list
            // empty while clippings can still be re-read, which would drop
            // every Kindle cover from this page.
            if (_kindle is not null && CurrentDevice is not null && !_deviceBooksLoaded)
                await RefreshDeviceBooksAsync(_lifetimeCancellation.Token);
            foreach (var card in DeviceBooks)
            {
                if (!string.IsNullOrWhiteSpace(card.Book.CoverPath) && File.Exists(card.Book.CoverPath))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Device, card.Title)] = card.Book.CoverPath;
            }
            var annotations = await _readerData.GetAllAnnotationsAsync(
                _lifetimeCancellation.Token,
                MaxReadingMaterials);
            var reflections = (await _readerData.GetAllBookReflectionsAsync(
                    _lifetimeCancellation.Token,
                    MaxReadingMaterials))
                .Where(reflection => !string.IsNullOrWhiteSpace(reflection.Content))
                .ToArray();
            var readingMaterialsTruncated = annotations.Count >= MaxReadingMaterials
                || reflections.Length >= MaxReadingMaterials;
            var materialBookIds = annotations.Select(annotation => annotation.BookId)
                .Concat(reflections.Select(reflection => reflection.BookId))
                .Distinct()
                .ToArray();
            var displayInfos = await _library.GetBookDisplayInfosAsync(
                materialBookIds,
                _lifetimeCancellation.Token);
            var titles = displayInfos.ToDictionary(pair => pair.Key, pair => pair.Value.Title);
            foreach (var info in displayInfos.Values)
            {
                if (string.IsNullOrWhiteSpace(info.CoverPath)) continue;
                var path = Path.GetFullPath(Path.Combine(_paths.Data, info.CoverPath));
                if (File.Exists(path))
                    _readingMaterialCoverPaths[BuildReadingMaterialCoverKey(ReadingMaterialSource.Local, info.Title)] = path;
            }
            var chapterTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bookId in annotations.Select(annotation => annotation.BookId).Distinct())
            {
                foreach (var chunk in await _readerData.GetBookOverviewChunksAsync(
                             bookId,
                             256,
                             _lifetimeCancellation.Token))
                {
                    if (string.IsNullOrWhiteSpace(chunk.ChapterTitle)) continue;
                    chapterTitles.TryAdd(
                        BuildReadingMaterialChapterKey(chunk.BookFileId, chunk.ChapterPath),
                        chunk.ChapterTitle.Trim());
                }
            }
            foreach (var annotation in annotations)
            {
                var chapter = ResolveReadingMaterialChapterLabel(annotation, chapterTitles);
                _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                    ReadingMaterialSource.Local,
                    titles.GetValueOrDefault(annotation.BookId, T("已删除的本地书籍")),
                    string.IsNullOrWhiteSpace(annotation.Note) ? "划线" : "划线与笔记",
                    chapter,
                    $"{annotation.ChapterPath} · {annotation.StartOffset}-{annotation.EndOffset}",
                    annotation.SelectedText,
                    annotation.Note,
                    annotation.UpdatedAt,
                    annotation,
                    null)
                {
                    IsDeletedBook = !displayInfos.TryGetValue(annotation.BookId, out var info) || info.IsDeleted
                });
            }
            foreach (var reflection in reflections)
            {
                _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                    ReadingMaterialSource.Local,
                    titles.GetValueOrDefault(reflection.BookId, T("已删除的本地书籍")),
                    "读后思考",
                    "读后思考",
                    "书籍级",
                    string.Empty,
                    BookReflectionEditorSurface.NormalizeMarkdown(reflection.Content),
                    reflection.UpdatedAt,
                    null,
                    null,
                    localReflection: reflection)
                {
                    IsDeletedBook = !displayInfos.TryGetValue(reflection.BookId, out var info) || info.IsDeleted
                });
            }

            if (CurrentDevice is { CanReadNotes: true } device && _kindle is not null)
            {
                IReadOnlyList<KindleClipping>? clippings = null;
                await TrackDeviceOperationAsync(async () =>
                {
                    if (_deviceClippingCache.TryGetValue(device.Identity, out var cached)
                        && !cached.Any(clip => clip.IsReadOnly && clip.Type == KindleClippingType.Highlight && clip.SourceBook is null))
                        clippings = cached;
                    else
                    {
                        clippings = await _kindle.ReadClippingsAsync(
                            device,
                            _lifetimeCancellation.Token,
                            MaxReadingMaterials + 1);
                        _deviceClippingCache[device.Identity] = clippings;
                    }
                    if (clippings.Any(KindleAnnotationClippingTextResolver.NeedsResolution))
                    {
                        var booksToResolve = DeviceBooks.Select(card => card.Book).ToArray();
                        var clippingsToResolve = clippings;
                        await RunKindleAnnotationTaskAsync(
                            T("读取 Kindle 划线原文"),
                            cancellable: false,
                            async (progress, _) =>
                            {
                                await KindleAnnotationClippingTextResolver.ResolveAsync(device,
                                    booksToResolve, clippingsToResolve, _kindle,
                                    _lifetimeCancellation.Token, progress);
                                return true;
                            },
                            _lifetimeCancellation.Token);
                    }
                    await PersistDeviceAuxiliaryCacheAsync(device);
                });
                readingMaterialsTruncated |= (clippings?.Count ?? 0) > MaxReadingMaterials;
                var clippingInput = (clippings ?? [])
                    .Take(MaxReadingMaterials + 1)
                    .ToArray();
                if (!IsCurrentDevice(device)) return;
                var pairs = device.Profile.SupportsClippings
                    ? KindleClippingsParser.PairForDisplay(clippingInput)
                    : clippingInput.Select(clipping => new KindleClippingsParser.DisplayPair(clipping, clipping.PairedNote)).ToArray();
                foreach (var pair in pairs.Take(MaxReadingMaterials))
                {
                    var clipping = pair.Clipping;
                    _allStage3ReadingMaterials.Add(new Stage3ReadingMaterialViewModel(
                        ReadingMaterialSource.Device,
                        string.IsNullOrWhiteSpace(clipping.BookTitle) || clipping.BookTitle == "未知书籍"
                            ? T("未知书籍")
                            : clipping.BookTitle,
                        pair.PairedNote is null ? string.Empty : "划线与笔记",
                        clipping.Metadata,
                        clipping.Metadata,
                        clipping.Type == KindleClippingType.Note ? string.Empty : clipping.Content,
                        clipping.Type == KindleClippingType.Note
                            ? clipping.Content
                            : pair.PairedNote?.Content ?? string.Empty,
                        MaxAddedAt(clipping.AddedAt, pair.PairedNote?.AddedAt),
                        null,
                        clipping,
                        pair.PairedNote,
                        device.Identity,
                        _deviceDisplayName ?? device.Name,
                        device.Profile.CanDeleteNotes
                            && !clipping.IsReadOnly
                            && !(pair.PairedNote?.IsReadOnly ?? false)));
                }
            }

            if (_readingMaterialsExportMode)
                foreach (var item in _allStage3ReadingMaterials)
                    item.IsSelected = selectedKeys.Contains(GetReadingMaterialSelectionKey(item));
            ApplyReadingMaterialsFilter();
            _readingMaterialsDirty = false;
            var kindleCount = _allStage3ReadingMaterials.Count(item => item.Source == ReadingMaterialSource.Device);
            ReadingMaterialsStatusText.Text = T("本地资料已读取 · 设备 {0} 条{1}", kindleCount, readingMaterialsTruncated ? T("（已限制最多 {0} 条）", MaxReadingMaterials) : string.Empty);
            if (CurrentDevice is { CanReadNotes: false })
                ReadingMaterialsStatusText.Text = T("本地资料已读取；当前设备暂不支持读取笔记。");
            UpdateKindleAnnotationSyncButtonVisibility();
            // The check copies shared books from the device; refreshing the
            // list must not wait for it. It handles its own errors.
            if (CurrentDevice is { CanReadNotes: true } currentDevice)
                _ = PromptKindleAnnotationSyncIfNeededAsync(currentDevice);
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T("读取阅读资料失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void ApplyReadingMaterialsFilter()
    {
        if (!_stage3Ready
            || ReadingMaterialsSearchBox is null
            || ReadingMaterialsSourceBox is null
            || ReadingMaterialsEmptyText is null
            || ReadingMaterialsStatusText is null)
            return;
        var source = (ReadingMaterialsSourceBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var query = ReadingMaterialsSearchBox.Text?.Trim() ?? string.Empty;
        RememberReadingMaterialGroupStates();

        var filtered = _allStage3ReadingMaterials
            .Where(item => source == "all"
                || source == "local" && item.Source == ReadingMaterialSource.Local
                || (source == "device" || source == "kindle") && item.Source == ReadingMaterialSource.Device)
            .Where(item => query.Length == 0 || item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        ReadingMaterials.Clear();
        foreach (var item in filtered)
        {
            item.IsExportMode = _readingMaterialsExportMode;
            ReadingMaterials.Add(item);
        }
        foreach (var group in ReadingMaterialGroups) group.Dispose();
        ReadingMaterialGroups.Clear();
        var grouped = source == "all"
            ? filtered.GroupBy(item => item.BookTitleKey, StringComparer.CurrentCultureIgnoreCase)
                .Select(items => (Source: items.First().Source, Title: items.First().BookTitle, Items: items.ToArray(), IsMixed: items.Select(item => item.Source).Distinct().Count() > 1))
            : filtered.GroupBy(item => (item.Source, item.BookTitleKey), new ReadingMaterialGroupKeyComparer())
                .Select(items => (Source: items.Key.Source, Title: items.First().BookTitle, Items: items.ToArray(), IsMixed: false));
        foreach (var group in grouped
            .Select(items => new Stage3ReadingMaterialGroupViewModel(
                items.Source,
                items.Title,
                items.Items,
                GetReadingMaterialCoverPathForGroup(items.Items),
                isExpanded: _readingMaterialGroupStates.TryGetValue((items.Source, items.Title), out var wasExpanded)
                    ? wasExpanded
                    : !_appSettings.ReadingMaterialsCollapsedByDefault,
                isMixedSource: items.IsMixed,
                isExportMode: _readingMaterialsExportMode))
            .OrderByDescending(group => group.Items.Max(item => item.UpdatedAt ?? DateTimeOffset.MinValue)))
        {
            ReadingMaterialGroups.Add(group);
        }
        ReadingMaterialsEmptyText.IsVisible = filtered.Length == 0;
        ReadingMaterialsEmptyText.Text = T("没有符合条件的划线、笔记、批注与读后思考");
        var localCount = filtered.Count(item => item.Source == ReadingMaterialSource.Local);
        var kindleCount = filtered.Count(item => item.Source == ReadingMaterialSource.Device);
        ReadingMaterialsSummaryText.Text = T("本地 {0} 条 · 设备 {1} 条 · 当前显示 {2} 条", localCount, kindleCount, filtered.Length);
        UpdateReadingMaterialsActionState();
    }

    private void ReadingMaterialsSearchBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyReadingMaterialsFilter();
    private void ReadingMaterialsSourceBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyReadingMaterialsFilter();

    private void SelectAllReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectAll = ReadingMaterials.Count > 0 && ReadingMaterials.Any(item => !item.IsSelected);
        foreach (var item in ReadingMaterials)
            item.IsSelected = selectAll;
        UpdateReadingMaterialsActionState();
    }

    private void SetReadingMaterialsSelection(bool isSelected)
    {
        foreach (var item in _allStage3ReadingMaterials)
            item.IsSelected = isSelected;
    }

    private static (ReadingMaterialSource Source, string? DeviceId, string Id)
        GetReadingMaterialSelectionKey(Stage3ReadingMaterialViewModel item) =>
        (item.Source, item.SourceDeviceId, item.LocalAnnotation?.Id.ToString("N")
            ?? item.LocalReflection?.BookId.ToString("N") ?? item.KindleClipping?.Id ?? string.Empty);

    private void ReadingMaterialSelectionChanged(object? sender, RoutedEventArgs e)
    {
        UpdateReadingMaterialsActionState();
    }

    private void UpdateReadingMaterialsActionState()
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        DeleteReadingMaterialsButton.IsEnabled = !_readingMaterialsExportMode && selected.Length > 0
            && selected.All(CanDeleteReadingMaterial);
        ToolTip.SetTip(DeleteReadingMaterialsButton, selected.Any(item => !CanDeleteReadingMaterial(item))
            ? T("所选设备笔记仅支持读取和导出，或来源设备已断开。") : DeleteReadingMaterialsButton.Content);
        ReadingMaterialsLocateButton.IsEnabled = !_readingMaterialsExportMode
            && selected.Any(item => item.LocalAnnotation is not null);
        if (SelectAllReadingMaterialsButton is not null)
        {
            SelectAllReadingMaterialsButton.Content = ReadingMaterials.Count > 0
                && ReadingMaterials.All(item => item.IsSelected)
                ? T("取消全选")
                : T("全选");
            SelectAllReadingMaterialsButton.IsEnabled = ReadingMaterials.Count > 0
                && !_readingMaterialsExportInProgress;
        }
        var canExport = _readingMaterialsExportMode && selected.Length > 0
            && !_readingMaterialsExportInProgress;
        ExportReadingMaterialsMarkdownButton.IsEnabled = canExport;
        ExportReadingMaterialsTextButton.IsEnabled = canExport;
        CancelReadingMaterialsExportButton.IsEnabled = !_readingMaterialsExportInProgress;
        if (ReadingMaterialsExportSummaryText is not null)
            ReadingMaterialsExportSummaryText.Text =
                T("已选 {0} 条 · 当前显示 {1} 条", selected.Length, ReadingMaterials.Count);
    }

    private void ReadingMaterialGroupToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Stage3ReadingMaterialGroupViewModel group })
            group.IsExpanded = !group.IsExpanded;
    }

    private void MarkReadingMaterialsDirty() => _readingMaterialsDirty = true;

    private void RememberReadingMaterialGroupStates()
    {
        foreach (var group in ReadingMaterialGroups)
            _readingMaterialGroupStates[(group.Source, group.BookTitleKey)] = group.IsExpanded;
    }

    private async Task RefreshReadingMaterialsIfDirtyAsync()
    {
        if (!_readingMaterialsDirty || !ReadingMaterialsPage.IsVisible) return;
        await RefreshReadingMaterialsAsync();
    }

    private async void ReadingMaterialsLocateButton_Click(object? sender, RoutedEventArgs e)
    {
        var item = ReadingMaterials.FirstOrDefault(candidate => candidate.IsSelected);
        if (item is null)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选一条本地批注。");
            return;
        }
        await LocateReadingMaterialAsync(item);
    }

    private void ReadingMaterialEntry_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readingMaterialsExportMode
            || sender is not Control { DataContext: Stage3ReadingMaterialViewModel item }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.Source is Visual source && source.GetVisualAncestors().OfType<Button>().Any())
            return;

        var additive = (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        if (additive)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            var wasSoleSelected = item.IsSelected && ReadingMaterials.Count(candidate => candidate.IsSelected) == 1;
            foreach (var candidate in _allStage3ReadingMaterials)
                candidate.IsSelected = !wasSoleSelected && ReferenceEquals(candidate, item);
        }
        UpdateReadingMaterialsActionState();
    }

    private async void ReadingMaterialEntry_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: Stage3ReadingMaterialViewModel item }) return;
        e.Handled = true;
        await OpenReadingMaterialAsync(item);
    }

    private async void ReadingMaterialOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: Stage3ReadingMaterialViewModel item })
            await OpenReadingMaterialAsync(item);
    }

    private async void ReadingMaterialDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Control { DataContext: Stage3ReadingMaterialViewModel item })
            await DeleteReadingMaterialsAsync([item]);
    }

    private async Task OpenReadingMaterialAsync(Stage3ReadingMaterialViewModel item)
    {
        if (item.LocalReflection is { } reflection)
        {
            await OpenBookReflectionFromReadingMaterialsAsync(reflection);
            return;
        }
        await LocateReadingMaterialAsync(item);
    }

    private void ReadingMaterialEntry_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border border
            || border.DataContext is not Stage3ReadingMaterialViewModel { IsBookReflection: true } item
            || string.IsNullOrWhiteSpace(item.ReflectionPreviewMarkdown))
            return;

        CloseReadingMaterialPreviewPopup();
        var preview = new KreaderMarkdownTextBlock
        {
            Width = 330,
            Markdown = item.ReflectionPreviewMarkdown,
            FontSize = 12,
            Foreground = AppAppearanceResources.GetBrush("InkBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        var host = new Border
        {
            Width = 360,
            MaxHeight = 300,
            Padding = new Thickness(14),
            Background = AppAppearanceResources.GetBrush("PaperBrush"),
            BorderBrush = AppAppearanceResources.GetBrush("InkBrush"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new ScrollViewer
            {
                MaxHeight = 270,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = preview
            }
        };
        _readingMaterialPreviewTarget = border;
        _readingMaterialPreviewPopup = new Popup
        {
            Child = host,
            PlacementTarget = border,
            Placement = PlacementMode.Pointer,
            HorizontalOffset = 6,
            VerticalOffset = 6,
            IsLightDismissEnabled = false,
            ShouldUseOverlayLayer = false,
            TakesFocusFromNativeControl = false,
            IsHitTestVisible = false
        };
        _readingMaterialPreviewPopup.IsOpen = true;
    }

    private void ReadingMaterialEntry_PointerExited(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(sender, _readingMaterialPreviewTarget))
            CloseReadingMaterialPreviewPopup();
    }

    private void CloseReadingMaterialPreviewPopup()
    {
        if (_readingMaterialPreviewPopup is not null)
        {
            _readingMaterialPreviewPopup.IsOpen = false;
            _readingMaterialPreviewPopup.Child = null;
        }

        _readingMaterialPreviewPopup = null;
        _readingMaterialPreviewTarget = null;
    }

    private async Task LocateReadingMaterialAsync(Stage3ReadingMaterialViewModel item)
    {
        if (item.LocalAnnotation is not { } annotation)
        {
            ReadingMaterialsStatusText.Text = item.LocalReflection is not null
                ? T("读后思考没有可在正文中定位的位置，请双击打开书籍详情。")
                : T("设备笔记没有可在电脑端定位的正文位置。");
            return;
        }

        var card = ViewModel.Books.FirstOrDefault(candidate => candidate.Book.Id == annotation.BookId);
        var temporaryCard = false;
        if (card is null)
        {
            var book = await _library.GetBookAsync(annotation.BookId, _lifetimeCancellation.Token);
            if (book is not null)
            {
                card = new BookCardViewModel(book, _paths.Data);
                temporaryCard = true;
            }
        }
        var file = card?.Book.Files.FirstOrDefault(candidate => candidate.Id == annotation.BookFileId);
        if (card is null || file is null)
        {
            if (temporaryCard) card?.Dispose();
            ReadingMaterialsStatusText.Text = T("找不到这条批注对应的本地书籍文件。");
            return;
        }

        await OpenBookAsync(card, file);
        if (temporaryCard) card.Dispose();
        if (!ReaderRoot.IsVisible) return;
        ShowReaderNotesTab();
        var loaded = ReaderAnnotations.FirstOrDefault(candidate => candidate.Id == annotation.Id);
        if (loaded is not null) await NavigateToReaderAnnotationAsync(loaded);
    }

    private static string BuildReadingMaterialCoverKey(ReadingMaterialSource source, string title) =>
        $"{source}\u001F{title}";

    private static string BuildReadingMaterialChapterKey(Guid bookFileId, string? chapterPath) =>
        $"{bookFileId:N}\u001F{(chapterPath ?? string.Empty).Replace('\\', '/').TrimStart('/')}";

    private static string ResolveReadingMaterialChapterLabel(
        ReaderAnnotation annotation,
        IReadOnlyDictionary<string, string> chapterTitles)
    {
        if (chapterTitles.TryGetValue(
                BuildReadingMaterialChapterKey(annotation.BookFileId, annotation.ChapterPath),
                out var title)
            && !string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        if (annotation.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase))
        {
            var pageText = annotation.ChapterPath.Split(':').LastOrDefault();
            return int.TryParse(pageText, out var page) && page > 0 ? T("第 {0} 页", page) : "PDF";
        }

        var fileName = Path.GetFileNameWithoutExtension(
            annotation.ChapterPath.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) ? T("未指定章节") : fileName;
    }

    private string? GetReadingMaterialCoverPath(ReadingMaterialSource source, string title) =>
        _readingMaterialCoverPaths.GetValueOrDefault(BuildReadingMaterialCoverKey(source, title))
        ?? _readingMaterialCoverPaths
            .Where(pair => pair.Key.StartsWith($"{source}\u001F", StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Title: pair.Key[(pair.Key.IndexOf('\u001F') + 1)..], Path: pair.Value))
            .Where(pair => ReadingMaterialCoverMatcher.AreTitlesRelated(pair.Title, title))
            .OrderByDescending(pair => Math.Min(
                ReadingMaterialCoverMatcher.NormalizeTitle(pair.Title).Length,
                ReadingMaterialCoverMatcher.NormalizeTitle(title).Length))
            .Select(pair => pair.Path)
            .FirstOrDefault();

    private string? GetReadingMaterialCoverPathForGroup(IReadOnlyList<Stage3ReadingMaterialViewModel> items)
    {
        var title = items.FirstOrDefault()?.BookTitleKey;
        if (string.IsNullOrWhiteSpace(title)) return null;
        return GetReadingMaterialCoverPath(ReadingMaterialSource.Local, title)
            ?? GetReadingMaterialCoverPath(ReadingMaterialSource.Device, title);
    }

    private sealed class ReadingMaterialGroupKeyComparer : IEqualityComparer<(ReadingMaterialSource Source, string BookTitle)>
    {
        public bool Equals(
            (ReadingMaterialSource Source, string BookTitle) left,
            (ReadingMaterialSource Source, string BookTitle) right) =>
            left.Source == right.Source
            && string.Equals(left.BookTitle, right.BookTitle, StringComparison.CurrentCultureIgnoreCase);

        public int GetHashCode((ReadingMaterialSource Source, string BookTitle) value) =>
            HashCode.Combine(value.Source, StringComparer.CurrentCultureIgnoreCase.GetHashCode(value.BookTitle));
    }

    private async void RefreshReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentDevice is { } currentDevice) _deviceClippingCache.Remove(currentDevice.Identity);
        await RefreshDevicesAsync(scanBooks: false);
        await RefreshReadingMaterialsAsync();
    }

    private async void DeleteReadingMaterialsButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        await DeleteReadingMaterialsAsync(selected);
    }

    private async Task DeleteReadingMaterialsAsync(Stage3ReadingMaterialViewModel[] selected)
    {
        if (selected.Any(item => !CanDeleteReadingMaterial(item)))
        {
            ReadingMaterialsStatusText.Text = T("所选设备笔记仅支持读取和导出，或来源设备已断开。");
            return;
        }
        if (selected.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选要删除的记录。");
            return;
        }
        var noteDevice = selected.Any(item => item.Source == ReadingMaterialSource.Device) ? CurrentDevice : null;
        var confirmation = noteDevice?.Profile.SupportsClippings == true
            ? T("确定删除选中的 {0} 条记录吗？Kindle 记录只会从 My Clippings.txt 删除。", selected.Length)
            : T("确定删除选中的 {0} 条记录吗？", selected.Length);
        if (!await ConfirmAsync(T("删除阅读资料"), confirmation)) return;
        if (selected.Any(item => !CanDeleteReadingMaterial(item)))
        {
            ReadingMaterialsStatusText.Text = T("所选设备笔记仅支持读取和导出，或来源设备已断开。");
            return;
        }
        try
        {
            var kindleIds = selected
                .SelectMany(item => new[] { item.KindleClipping?.Id, item.PairedKindleClipping?.Id })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToArray();
            if (kindleIds.Length > 0 && noteDevice is { } device && _kindle is not null)
            {
                await EnsureCurrentDeviceAsync(device);
                await TrackDeviceOperationAsync(() => _kindle.DeleteClippingsAsync(device, kindleIds, _lifetimeCancellation.Token));
                _deviceClippingCache.Remove(device.Identity);
                await PersistDeviceAuxiliaryCacheAsync(device);
            }
            foreach (var item in selected.Where(item => item.LocalAnnotation is not null))
            {
                if (item.LocalAnnotation is { } annotation)
                    await _readerData.DeleteAnnotationAsync(annotation.Id, _lifetimeCancellation.Token);
            }
            foreach (var item in selected.Where(item => item.LocalReflection is not null))
            {
                if (item.LocalReflection is { } reflection)
                    await _readerData.DeleteBookReflectionAsync(reflection.BookId, _lifetimeCancellation.Token);
            }
            await RefreshReadingMaterialsAsync();
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T("删除失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void ExportReadingMaterialsMarkdownButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: true);

    private async void ExportReadingMaterialsTextButton_Click(object? sender, RoutedEventArgs e)
        => await ExportReadingMaterialsAsync(markdown: false);

    private async Task ExportReadingMaterialsAsync(bool markdown)
    {
        if (!_readingMaterialsExportMode || _readingMaterialsExportInProgress) return;
        var selected = ReadingMaterials.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("请先勾选要导出的记录，或点击“全选”。");
            return;
        }
        var records = selected.Select(item => item.ToRecord()).ToArray();
        if (records.Length == 0)
        {
            ReadingMaterialsStatusText.Text = T("当前筛选结果没有可导出的记录。");
            return;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        _readingMaterialsExportInProgress = true;
        UpdateReadingMaterialsActionState();
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("导出阅读资料"),
                SuggestedFileName = T("Libzora-阅读资料-{0}.{1}", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), markdown ? "md" : "txt"),
                FileTypeChoices = [new FilePickerFileType(markdown ? "Markdown" : T("文本"))
                {
                    Patterns = [markdown ? "*.md" : "*.txt"]
                }]
            });
            var path = file?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;
            await using (var stream = new FileStream(
                             path,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (markdown)
                    await ReadingMaterialsExport.WriteMarkdownAsync(stream, records, _lifetimeCancellation.Token);
                else
                    await ReadingMaterialsExport.WritePlainTextAsync(stream, records, _lifetimeCancellation.Token);
            }
            ReadingMaterialsStatusText.Text = T("已导出 {0} 条记录到 {1}。", records.Length, path);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReadingMaterialsStatusText.Text = T("导出失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _readingMaterialsExportInProgress = false;
            UpdateReadingMaterialsActionState();
        }
    }

    private string GetReadingMaterialsSourceLabel(string source) => source switch
    {
        "local" => T("本地书库"),
        "device" or "kindle" => _deviceDisplayName ?? T("当前设备"),
        _ => T("全部来源")
    };

    private static DateTimeOffset? MaxAddedAt(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;
}
