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
/// Confirmation, message and collection-management overlays.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> ConfirmAsync(string title, string message, string? primaryText = null,
        CancellationToken cancellationToken = default, bool allowDiagnosticAutoConfirm = true,
        string? cancelText = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (allowDiagnosticAutoConfirm && Environment.GetEnvironmentVariable("LIBZORA_SEND_DIAG") == "1") return true;
        if (_confirmationCompletion is not null || _bookRemovalCompletion is not null || _deviceDuplicateCompletion is not null)
        {
            PdfAnnotTrace.Write($"ConfirmAsync busy: confirmation={_confirmationCompletion is not null} bookRemoval={_bookRemovalCompletion is not null} title={title}");
            return false;
        }
        PdfAnnotTrace.Write($"ConfirmAsync open title={title}");
        ConfirmationTitleText.Text = title;
        ConfirmationMessageText.Text = message;
        var previousCancelText = ConfirmationCancelButton.Content;
        if (cancelText is not null) ConfirmationCancelButton.Content = cancelText;
        ConfirmationOkButton.Content = primaryText
            ?? (title.Contains(T("删除"), StringComparison.Ordinal) ? T("确认删除") : T("应用"));
        ShowOverlay(ConfirmationOverlay);
        _confirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!allowDiagnosticAutoConfirm) ConfirmationCancelButton.Focus();
        var completion = _confirmationCompletion;
        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (ReferenceEquals(_confirmationCompletion, completion))
            {
                _confirmationCompletion = null;
                ConfirmationOverlay.IsVisible = false;
                ConfirmationCancelButton.Content = previousCancelText;
            }
        }
    }

    private async Task<BookRemovalChoice> ConfirmBookRemovalAsync(string message)
    {
        if (_confirmationCompletion is not null || _bookRemovalCompletion is not null || _deviceDuplicateCompletion is not null)
            return BookRemovalChoice.Cancel;
        ConfirmationTitleText.Text = T("从书库移除书籍");
        ConfirmationMessageText.Text = message;
        ConfirmationOkButton.Content = T("删除文件");
        ShowOverlay(ConfirmationOverlay);
        _bookRemovalCompletion = new TaskCompletionSource<BookRemovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _bookRemovalCompletion;
        try
        {
            return await completion.Task.WaitAsync(_lifetimeCancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_bookRemovalCompletion, completion))
            {
                _bookRemovalCompletion = null;
                ConfirmationOverlay.IsVisible = false;
            }
        }
    }

    // Fade-in helpers: the overlays and pages carry an Opacity transition in
    // XAML; showing them at 0 and restoring 1 on the next frame animates the
    // entrance instead of popping it in.
    private void ShowOverlay(Control overlay)
    {
        // Flyouts render in Avalonia's popup layer, above in-window overlays.
        // Dismiss the task-history flyout before opening a modal so update and
        // other confirmation dialogs remain visible and actionable.
        DismissBackgroundTaskFlyout();
        overlay.IsVisible = true;
        overlay.Opacity = 0;
        Dispatcher.UIThread.Post(() => overlay.Opacity = 1);
    }

    private static void FadeInPage(Control page)
    {
        page.IsVisible = true;
        page.Opacity = 0;
        Dispatcher.UIThread.Post(() => page.Opacity = 1);
    }

    // Monochrome information dialog (WinUI ShowMessageAsync). Fire-and-forget
    // callers can use "_ = ShowMessageAsync(...)"; the awaited task completes
    // when the user dismisses the dialog.
    private Task ShowMessageAsync(string title, string message, string? actionText = null)
    {
        MessageTitleText.Text = UiText.Localize(title);
        MessageBodyText.Text = UiText.Localize(message);
        MessageOkButton.Content = actionText ?? T("知道了");
        ShowOverlay(MessageOverlay);
        MessageOverlay.Focus();
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _messageCompletion.Task;
    }

    private void MessageOkButton_Click(object? sender, RoutedEventArgs e) => CompleteMessage();

    private void MessageOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteMessage();
    }

    private void CompleteMessage()
    {
        MessageOverlay.IsVisible = false;
        var completion = _messageCompletion;
        _messageCompletion = null;
        completion?.TrySetResult(true);
    }

    private Task<string?> PromptCollectionNameAsync(
        string? initialName = null,
        string? promptTitle = null,
        string? placeholder = null)
    {
        if (_collectionNameCompletion is not null) return Task.FromResult<string?>(null);
        CollectionNamePromptTitleText.Text = promptTitle ?? T("新建收藏夹");
        CollectionNameBox.PlaceholderText = placeholder ?? T("请输入收藏夹名称。");
        CollectionNameBox.Text = initialName ?? string.Empty;
        ShowOverlay(CollectionNameOverlay);
        CollectionNameBox.Focus();
        _collectionNameCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _collectionNameCompletion.Task;
    }

    private void CompleteConfirmation(bool confirmed)
    {
        ConfirmationOverlay.IsVisible = false;
        if (_deviceDuplicateCompletion is { } duplicate)
        {
            duplicate.TrySetResult(confirmed ? DeviceDuplicateChoice.Skip : DeviceDuplicateChoice.Cancel);
            return;
        }
        if (_bookRemovalCompletion is { } bookRemoval)
        {
            bookRemoval.TrySetResult(confirmed ? BookRemovalChoice.MoveToTrash : BookRemovalChoice.Cancel);
            return;
        }
        _confirmationCompletion?.TrySetResult(confirmed);
    }

    private void CompleteCollectionName(string? name)
    {
        CollectionNameOverlay.IsVisible = false;
        var completion = _collectionNameCompletion;
        _collectionNameCompletion = null;
        completion?.TrySetResult(name);
    }

    private async Task CreateCollectionAsync()
    {
        var name = await PromptCollectionNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已创建收藏夹“{0}”。", name.Trim()));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("创建收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法创建收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private Task OpenCollectionAsync(BookCollectionFolderViewModel folder)
    {
        ViewModel.CollectionFilterId = folder.Collection.Id;
        ViewModel.CollectionFilterName = folder.Collection.Name;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
        return Task.CompletedTask;
    }

    private async Task RenameCollectionAsync(BookCollectionFolderViewModel folder)
    {
        var name = await PromptCollectionNameAsync(folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _library.RenameCollectionAsync(
                folder.Collection.Id,
                name,
                _lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            if (ViewModel.CollectionFilterId == folder.Collection.Id)
            {
                ViewModel.CollectionFilterName = name.Trim();
                ViewModel.RefreshView();
            }
            UpdateLibraryUi();
            SetTaskStatus(T("已重命名收藏夹为“{0}”。", name.Trim()));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("重命名收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("重命名收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task ClearCollectionAsync(BookCollectionFolderViewModel folder)
    {
        if (!await ConfirmAsync(
                T("清空收藏夹"),
                T("确定清空收藏夹“{0}”吗？收藏夹本身会保留。", folder.Name))) return;

        try
        {
            await _library.ClearCollectionAsync(folder.Collection.Id, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已清空收藏夹“{0}”。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("清空收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("清空收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task DissolveCollectionAsync(BookCollectionFolderViewModel folder)
    {
        if (!await ConfirmAsync(
                T("解散收藏夹"),
                T("确定解散收藏夹“{0}”吗？书籍将自动回到未收藏夹。", folder.Name))) return;

        try
        {
            await _library.DissolveCollectionAsync(folder.Collection.Id, _lifetimeCancellation.Token);
            if (ViewModel.CollectionFilterId == folder.Collection.Id)
            {
                ViewModel.CollectionFilterId = null;
                ViewModel.CollectionFilterName = null;
            }
            await RefreshLibraryAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已解散收藏夹“{0}”。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("解散收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("解散收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task MergeCollectionAsync(
        BookCollectionFolderViewModel source,
        BookCollectionFolderViewModel target)
    {
        var sourceName = source.Name;
        var targetName = target.Name;
        if (!await ConfirmAsync(
                T("合并收藏夹"),
                T("确定将“{0}”合并到“{1}”吗？源收藏夹将被解散。", sourceName, targetName))) return;

        try
        {
            await _library.MergeCollectionsAsync(
                source.Collection.Id,
                target.Collection.Id,
                _lifetimeCancellation.Token);
            if (ViewModel.CollectionFilterId == source.Collection.Id)
            {
                ViewModel.CollectionFilterId = target.Collection.Id;
                ViewModel.CollectionFilterName = target.Collection.Name;
            }
            await RefreshLibraryAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已将“{0}”合并到“{1}”。", sourceName, targetName));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("合并收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("合并收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task ToggleCollectionMembershipAsync()
    {
        if (_selectedCard is null) return;
        if (DetailCollectionBox.SelectedItem is not BookCollectionFolderViewModel folder)
        {
            SetTaskStatus(T("请先选择一个收藏夹。"));
            return;
        }

        try
        {
            var bookId = _selectedCard.Book.Id;
            if (_selectedCard.Book.CollectionIds.Contains(folder.Collection.Id))
            {
                await _library.RemoveBookFromCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus(T("已从“{0}”移出。 ", folder.Name));
            }
            else
            {
                await _library.AddBookToCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus(T("已加入“{0}”。", folder.Name));
            }

            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }
}
