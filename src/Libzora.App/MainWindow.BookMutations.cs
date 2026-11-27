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
/// Favourite, reading status, collections, conversion and automatic
/// reader format generation.
/// </summary>
public partial class MainWindow
{
    private void BookCard_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
        _suppressSelectionPane = true;
        try
        {
            if (!_selectedBookIds.Contains(card.Book.Id))
            {
                _selectedBookIds.Clear();
                _selectedBookIds.Add(card.Book.Id);
                var activeList = BookGrid.IsVisible ? BookGrid : BookList;
                activeList.SelectedItems?.Clear();
                activeList.SelectedItems?.Add(card);
            }
        }
        finally
        {
            _suppressSelectionPane = false;
        }
        _selectedCard = card;
        _multiSelectAnchor = card;
        // 右键只更新选中与菜单目标，不主动弹出详情页；若详情页已经打开，
        // 顺手把内容切到当前书籍，避免面板与菜单目标不一致。
        if (LibraryDetailPane.IsVisible)
            SelectBook(card);
        UpdateMultiSelectionUi();
        var menu = BuildBookContextMenu(card);
        menu.Open(control);
        _suppressNextSelectionPane = false;
        e.Handled = true;
    }

    private static string GetReadingStatusName(LibraryReadingStatus status) => status switch
    {
        LibraryReadingStatus.Reading => T("阅读中"),
        LibraryReadingStatus.Finished => T("已读"),
        _ => T("待读")
    };

    private Task DeleteSelectedBooksAsync()
    {
        var cards = GetSelectedCards();
        return cards.Count == 0
            ? Task.CompletedTask
            : DeleteBooksBatchCoreAsync(
                cards,
                T("选中的 {0} 本书将从书库移除并移入回收站，可恢复。", cards.Count),
                T("已将 {0} 本书移入回收站。", cards.Count));
    }

    private async Task DeleteBooksBatchCoreAsync(
        IReadOnlyList<BookCardViewModel> cards,
        string confirmationMessage,
        string completionMessage)
    {
        if (cards.Count == 0) return;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读其中一本书，请先关闭阅读器。"));
            return;
        }
        var choice = await ConfirmBookRemovalAsync(confirmationMessage);
        if (choice == BookRemovalChoice.Cancel) return;

        var deletedIds = new HashSet<Guid>();
        try
        {
            var result = await _library.DeleteBatchAsync(
                cards.Select(card => card.Book.Id).ToArray(),
                _lifetimeCancellation.Token);
            deletedIds.UnionWith(result.DeletedIds);
            if (result.Failures.Count == 0)
            {
                if (SeriesShelfOverlay.IsVisible)
                    CloseSeriesShelfOverlay(immediate: true);
                ClearSelectedBook();
                ClearBookMultiSelection();
                await RefreshLibraryAsync();
                SetTaskStatus(completionMessage);
                ShowSettingsCapsule(completionMessage, 2400, success: true);
                return;
            }

            if (deletedIds.Count > 0)
            {
                _selectedBookIds.ExceptWith(deletedIds);
                if (_selectedCard is not null && deletedIds.Contains(_selectedCard.Book.Id)) ClearSelectedBook();
                try { await RefreshLibraryAsync(); }
                catch (Exception refreshException) { Debug.WriteLine($"Library refresh after partial deletion failed: {refreshException}"); }
                UpdateMultiSelectionUi();
            }
            var firstError = UiText.Localize(result.Failures[0].ErrorMessage);
            var message = deletedIds.Count > 0
                ? T("已将 {0} 本书移入回收站，剩余书籍未删除：{1}", deletedIds.Count, firstError)
                : firstError;
            SetTaskStatus(T("批量删除失败：{0}", message));
            await ShowMessageAsync(T("无法删除书籍"), message);
        }
        catch (Exception exception)
        {
            if (deletedIds.Count > 0)
            {
                _selectedBookIds.ExceptWith(deletedIds);
                if (_selectedCard is not null && deletedIds.Contains(_selectedCard.Book.Id)) ClearSelectedBook();
                try { await RefreshLibraryAsync(); }
                catch (Exception refreshException) { Debug.WriteLine($"Library refresh after partial deletion failed: {refreshException}"); }
                UpdateMultiSelectionUi();
            }
            if (_lifetimeCancellation.IsCancellationRequested) return;
            var message = deletedIds.Count > 0
                ? T("已将 {0} 本书移入回收站，剩余书籍未删除：{1}", deletedIds.Count, UiText.Localize(exception.Message))
                : UiText.Localize(exception.Message);
            SetTaskStatus(T("批量删除失败：{0}", message));
            await ShowMessageAsync(T("无法删除书籍"), message);
        }
    }

    private async Task DeleteBookFromContextAsync(BookCardViewModel card)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        var choice = await ConfirmBookRemovalAsync(T("《{0}》将从书库移除并移入回收站，可恢复。", card.Title));
        if (choice == BookRemovalChoice.Cancel) return;
        try
        {
            await _library.DeleteAsync(card.Book.Id, _lifetimeCancellation.Token);
            if (SeriesShelfOverlay.IsVisible)
                CloseSeriesShelfOverlay(immediate: true);
            _selectedBookIds.Remove(card.Book.Id);
            if (_selectedCard?.Book.Id == card.Book.Id) ClearSelectedBook();
            ClearBookMultiSelection();
            await RefreshLibraryAsync();
            var completionMessage = T("已将《{0}》移入回收站。", card.Title);
            SetTaskStatus(completionMessage);
            ShowSettingsCapsule(completionMessage, 2400, success: true);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task ToggleFavoriteAsync(BookCardViewModel card)
    {
        await SaveBookMetadataAsync(card, book =>
        {
            book.IsFavorite = !book.IsFavorite;
            return book.IsFavorite ? T("已加入收藏。") : T("已取消收藏。");
        });
    }

    private async Task UpdateReadingStatusAsync(BookCardViewModel card, LibraryReadingStatus status)
    {
        await SaveBookMetadataAsync(card, book =>
        {
            book.ReadingStatus = status;
            return T("已标记为“{0}”。", GetReadingStatusName(status));
        }, markFinished: status == LibraryReadingStatus.Finished);
    }

    private static IReadOnlyList<BookCardViewModel> ResolveCollectionTargets(
        BookCardViewModel card,
        bool includeSeriesMembers) =>
        includeSeriesMembers && card.IsSeriesStack
            ? card.SeriesMembers
            : [card];

    private async Task ToggleBookCollectionAsync(
        BookCardViewModel card,
        BookCollectionFolderViewModel folder,
        bool includeSeriesMembers = true)
    {
        try
        {
            var targets = ResolveCollectionTargets(card, includeSeriesMembers);
            var collectionId = folder.Collection.Id;
            var allInCollection = targets.All(target => target.Book.CollectionIds.Contains(collectionId));
            foreach (var target in targets)
            {
                if (allInCollection)
                {
                    if (target.Book.CollectionIds.Contains(collectionId))
                        await _library.RemoveBookFromCollectionAsync(target.Book.Id, collectionId, _lifetimeCancellation.Token);
                }
                else if (!target.Book.CollectionIds.Contains(collectionId))
                {
                    await _library.AddBookToCollectionAsync(target.Book.Id, collectionId, _lifetimeCancellation.Token);
                }
            }
            await RefreshLibraryAsync();
            SetTaskStatus(T("已更新“{0}”中的书籍归属。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }

    /// <summary>
    /// Puts the book (or the whole series stack when targeted from the shelf) into
    /// one collection, dropping its other memberships first when the settings ask
    /// for mutually exclusive collections.
    /// </summary>
    private async Task AssignBookToCollectionAsync(
        BookCardViewModel card,
        Guid collectionId,
        bool includeSeriesMembers = true)
    {
        foreach (var target in ResolveCollectionTargets(card, includeSeriesMembers))
        {
            if (_appSettings.CollectionsMutuallyExclusive)
            {
                foreach (var existing in target.Book.CollectionIds.Where(id => id != collectionId).ToArray())
                    await _library.RemoveBookFromCollectionAsync(target.Book.Id, existing, _lifetimeCancellation.Token);
            }
            if (!target.Book.CollectionIds.Contains(collectionId))
                await _library.AddBookToCollectionAsync(target.Book.Id, collectionId, _lifetimeCancellation.Token);
        }
    }

    private async Task ClearBookCollectionsAsync(
        BookCardViewModel card,
        bool includeSeriesMembers = true)
    {
        foreach (var target in ResolveCollectionTargets(card, includeSeriesMembers))
        {
            foreach (var existing in target.Book.CollectionIds.ToArray())
                await _library.RemoveBookFromCollectionAsync(target.Book.Id, existing, _lifetimeCancellation.Token);
        }
    }

    private void DetailCollectionsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is null || _updatingDetails) return;
        var menu = BuildDetailCollectionsMenu(_selectedCard);
        AttachInstantMenuHover(menu);
        menu.PlacementTarget = DetailCollectionsButton;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Open(DetailCollectionsButton);
    }

    /// <summary>
    /// Collections offered by the detail pane's selector. With mutually
    /// exclusive collections the entries carry no tick marks (choosing one
    /// replaces the previous one); otherwise each entry is a checkbox that
    /// toggles that membership.
    /// </summary>
    private ContextMenu BuildDetailCollectionsMenu(BookCardViewModel card)
    {
        var menu = new ContextMenu();
        var exclusive = _appSettings.CollectionsMutuallyExclusive;
        var customCollections = CollectionFolders
            .Where(folder => !IsUncollectedFolder(folder))
            .ToArray();

        foreach (var folder in customCollections)
        {
            var item = new MenuItem { Header = folder.Name, Tag = folder };
            if (!exclusive)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = ResolveCollectionTargets(card, includeSeriesMembers: true)
                    .All(target => target.Book.CollectionIds.Contains(folder.Collection.Id));
            }
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await ChangeBookCollectionAsync(card, folder, exclusive, includeSeriesMembers: true);
            menu.Items.Add(item);
        }
        if (customCollections.Length == 0)
            menu.Items.Add(new MenuItem { Header = T("还没有收藏夹。"), IsEnabled = false });

        if (exclusive && customCollections.Length > 0)
            menu.Items.Add(CreateMenuItem(T("未收藏"), () => ClearDetailCollectionsAsync(card)));

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem(T("新建收藏夹…"), async () =>
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

        return menu;
    }

    private async Task ChangeBookCollectionAsync(
        BookCardViewModel card,
        BookCollectionFolderViewModel folder,
        bool exclusive,
        bool includeSeriesMembers = true)
    {
        if (!exclusive)
        {
            await ToggleBookCollectionAsync(card, folder, includeSeriesMembers);
            return;
        }
        try
        {
            await AssignBookToCollectionAsync(card, folder.Collection.Id, includeSeriesMembers);
            await RefreshLibraryAsync();
            SetTaskStatus(T("已加入“{0}”。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task ClearDetailCollectionsAsync(BookCardViewModel card)
    {
        try
        {
            await ClearBookCollectionsAsync(card);
            await RefreshLibraryAsync();
            SetTaskStatus(T("已移出所有收藏夹。"));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private static bool IsUncollectedFolder(BookCollectionFolderViewModel folder) =>
        string.Equals(
            folder.Collection.Name,
            BookLibraryDefaults.UncollectedCollectionName,
            StringComparison.Ordinal);

    private async Task SaveBookMetadataAsync(BookCardViewModel card, Func<Book, string> applyChanges, bool markFinished = false)
    {
        var entered = false;
        try
        {
            await _bookMetadataSaveGate.WaitAsync(_lifetimeCancellation.Token);
            entered = true;
            var book = await _library.GetBookAsync(card.Book.Id, _lifetimeCancellation.Token)
                ?? throw new InvalidOperationException(T("书籍已不存在。"));
            var successMessage = applyChanges(book);
            await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
            if (markFinished) await _readerData.MarkBookFinishedAsync(book, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(successMessage);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("保存书籍信息失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            if (entered) _bookMetadataSaveGate.Release();
        }
    }

    private async Task ConvertBookAsync(
        BookCardViewModel card,
        string targetFormat,
        BookFile? requestedSource = null)
    {
        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
        {
            await ShowMessageAsync(T("格式转换"), T("已有一本书正在转换，请稍候。"));
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;
        if (card.Book.Files.Any(file =>
                string.Equals(BookFormatConversionPolicy.Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync(T("格式转换"), T("这本书已经有 {0} 格式。", target.ToUpperInvariant()));
            return;
        }

        var sourceCandidates = BookFormatConversionPolicy.GetSourceCandidates(card.Book.Files, target);
        var sourceFile = requestedSource is not null
            ? sourceCandidates.FirstOrDefault(file => file.Id == requestedSource.Id)
                ?? sourceCandidates.FirstOrDefault()
            : sourceCandidates.FirstOrDefault();
        if (sourceFile is null)
        {
            await ShowMessageAsync(T("格式转换"), T("需要 EPUB、AZW3、PDF 或 MOBI 作为转换源。"));
            return;
        }

        var sourcePath = await ResolveConversionSourcePathAsync(sourceFile, _lifetimeCancellation.Token);
        if (sourcePath is null) return;

        _conversionInProgress = true;
        _conversionCancellation?.Dispose();
        _conversionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var initialProgress = new FormatConversionProgress(
            0,
            T("准备将 {0} 转换为 {1}…", sourceFile.Format.ToUpperInvariant(), target.ToUpperInvariant()));
        SetTaskStatus(UiText.Localize(initialProgress.Message));
        try
        {
            await RunTrackedTaskAsync(T("格式转换"), BackgroundTaskKind.FormatConversion, async (context, token) =>
            {
                context.ReportBooks([card.Title]);
                context.ReportProgress(UiText.Localize(initialProgress.Message), 0, 100);
                var progress = new Progress<FormatConversionProgress>(value =>
                {
                    if (token.IsCancellationRequested) return;
                    context.ReportProgress(T("{0} → {1}：{2}", sourceFile.Format.ToUpperInvariant(),
                        target.ToUpperInvariant(), UiText.Localize(value.Message)), value.RoundedPercentage, 100);
                    SetTaskStatus(T("格式转换：{0}%", value.RoundedPercentage));
                });
                await ConvertSingleBookFileAsync(card, sourceFile, target, progress, token, sourcePath, () =>
                    context.ReportProgress(T("正在写入书库…"), 100, 100));
                await RefreshLibraryAsync();
                context.ReportProgress(T("已为《{0}》添加 {1} 格式。", card.Title, target.ToUpperInvariant()), 100, 100);
                return true;
            }, _conversionCancellation.Token, allowRetry: false);
            SetTaskStatus(T("已为《{0}》添加 {1} 格式。", card.Title, target.ToUpperInvariant()));
        }
        catch (OperationCanceledException)
        {
            SetTaskStatus(T("格式转换已取消。"));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("格式转换失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            _conversionInProgress = false;
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
        }
    }

    private async Task<string?> ResolveConversionSourcePathAsync(
        BookFile sourceFile,
        CancellationToken cancellationToken)
    {
        var sourcePath = ViewModel.GetAbsoluteFilePath(sourceFile);
        if (File.Exists(sourcePath))
            return sourcePath;

        try
        {
            sourcePath = await EnsureBookFileAvailableAsync(sourceFile, cancellationToken) ?? sourcePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("下载书籍失败：{0}", UiText.Localize(exception.Message)));
            return null;
        }

        if (!File.Exists(sourcePath))
        {
            SetTaskStatus(T("找不到转换来源：{0}", sourceFile.RelativePath));
            return null;
        }

        return sourcePath;
    }

    private async Task ConvertSingleBookFileAsync(
        BookCardViewModel card,
        BookFile sourceFile,
        string target,
        IProgress<FormatConversionProgress>? progress,
        CancellationToken token,
        string? resolvedSourcePath = null,
        Action? beforeAddToLibrary = null)
    {
        var sourcePath = resolvedSourcePath ?? await ResolveConversionSourcePathAsync(sourceFile, token)
            ?? throw new FileNotFoundException(T("找不到转换来源：{0}", sourceFile.RelativePath));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "LibzoraConversions", Guid.NewGuid().ToString("N"));
        var temporaryOutput = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(card.Title, "." + target));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await _formatConverter.ConvertAsync(
                sourcePath,
                temporaryOutput,
                progress,
                token,
                new FormatConversionMetadata(card.Book.Title, card.Book.Authors));
            beforeAddToLibrary?.Invoke();
            await _library.AddFileToBookAsync(card.Book.Id, temporaryOutput, token);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private async Task<AutomaticReaderFormatGenerationResult> AutoGenerateReaderFormatsForImportsAsync(
        ImportBatchResult importResult,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? requestedFormatsBySourcePath = null,
        BackgroundTaskContext? taskContext = null)
    {
        // The global setting gates flows without an explicit per-file choice;
        // the folder-import dialog always decides explicitly, so its toggles
        // stay authoritative even when the global preference is off.
        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport
            && requestedFormatsBySourcePath is null)
            return new AutomaticReaderFormatGenerationResult(0, []);

        var books = importResult.Items
            .Where(item => item.Succeeded && item.Added && item.Book is not null)
            .Select(item => new
            {
                Book = item.Book!,
                Formats = requestedFormatsBySourcePath is null
                    ? (IReadOnlyCollection<string>)["epub", "azw3"]
                    : requestedFormatsBySourcePath.GetValueOrDefault(Path.GetFullPath(item.SourcePath), Array.Empty<string>())
            })
            .Where(item => item.Formats.Count > 0)
            .GroupBy(item => item.Book.Id)
            .Select(group => new
            {
                Book = group.First().Book,
                Formats = group.SelectMany(item => item.Formats).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .Where(item => BookFormatConversionPolicy.GetMissingDefaultReaderFormats(item.Book.Files).Count > 0)
            .ToArray();
        if (books.Length == 0)
            return new AutomaticReaderFormatGenerationResult(0, []);
        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
            return new AutomaticReaderFormatGenerationResult(
                0,
                [T("已有格式转换正在进行，未启动 EPUB/AZW3 自动补齐。")]);

        _automaticReaderFormatGenerationInProgress = true;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Libzora", "automatic-formats", Guid.NewGuid().ToString("N"));
        var failures = new List<string>();
        var generatedCount = 0;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            foreach (var item in books)
            {
                var book = item.Book;
                foreach (var targetFormat in BookFormatConversionPolicy.GetMissingDefaultReaderFormats(book.Files)
                             .Where(item.Formats.Contains))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceFile = BookFormatConversionPolicy.SelectSource(book.Files, targetFormat);
                    if (sourceFile is null)
                    {
                        failures.Add(T("《{0}》没有可用于生成 {1} 的源格式。", book.Title, targetFormat.ToUpperInvariant()));
                        continue;
                    }

                    var bookTemporaryDirectory = Path.Combine(temporaryRoot, book.Id.ToString("N"));
                    var temporaryOutput = Path.Combine(
                        bookTemporaryDirectory,
                        KindleTransferPolicy.CreateSafeFileName(book.Title, "." + targetFormat));
                    try
                    {
                        Directory.CreateDirectory(bookTemporaryDirectory);
                        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
                        SetTaskStatus(T("正在为《{0}》生成 {1}…", book.Title, targetFormat.ToUpperInvariant()));
                        taskContext?.ReportProgress(T("正在为《{0}》生成 {1}…", book.Title, targetFormat.ToUpperInvariant()));
                        await _formatConverter.ConvertAsync(
                            sourcePath,
                            temporaryOutput,
                            new Progress<FormatConversionProgress>(value =>
                                SetTaskStatus(T("正在生成 {0}：{1}（{2}%）", targetFormat.ToUpperInvariant(), book.Title, value.RoundedPercentage))),
                            cancellationToken,
                            new FormatConversionMetadata(book.Title, book.Authors));
                        var addedFile = await _library.AddFileToBookAsync(book.Id, temporaryOutput, cancellationToken);
                        book.Files.Add(addedFile);
                        generatedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(T("《{0}》生成 {1}：{2}", book.Title, targetFormat.ToUpperInvariant(), UiText.Localize(exception.Message)));
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }

            if (generatedCount > 0)
                await RefreshLibraryDataAsync(cancellationToken);
            return new AutomaticReaderFormatGenerationResult(generatedCount, failures);
        }
        finally
        {
            _automaticReaderFormatGenerationInProgress = false;
            try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record AutomaticReaderFormatGenerationResult(
        int GeneratedCount,
        IReadOnlyList<string> Failures);

    // The library shell owns the modal overlays (confirmation, collection name,
    // import prompts, ...) while the reader is a sibling layered above that
    // shell. An overlay opened from inside the shell therefore stayed behind
    // the reader: the dialog was invisible yet still swallowed every click,
    // which made deleting a PDF highlight look like a freeze. Move them into the
    // shell grid that also hosts the reader so they always paint on top.
    private void PromoteModalOverlays()
    {
        if (LibraryRoot.Parent is not Panel shell) return;
        Control[] overlays =
        [
            DevicePromptOverlay,
            ConfirmationOverlay,
            CollectionNameOverlay,
            DeviceModelInputOverlay,
            TxtImportRulesOverlay,
            ImportFormatSelectionOverlay,
            ImportConflictOverlay
        ];
        var zIndex = 150;
        foreach (var overlay in overlays)
        {
            if (overlay.Parent is not Panel owner) continue;
            owner.Children.Remove(overlay);
            shell.Children.Add(overlay);
            overlay.ZIndex = zIndex++;
        }
    }
}
