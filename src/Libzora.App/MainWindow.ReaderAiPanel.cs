using System.Text;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Assistant panel tabs, sizing, scrolling and message actions.
/// </summary>
public partial class MainWindow
{
    private void PopulateReaderAiReasoningMenu(
        IReadOnlyList<(string, string)> options,
        string selectedDepth)
    {
        if (ReaderAiReasoningMenuItem is null) return;

        ReaderAiReasoningMenuItem.Items.Clear();
        foreach (var option in options)
        {
            var item = new MenuItem
            {
                Header = option.Item2,
                Tag = option.Item1,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = string.Equals(option.Item1, selectedDepth, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += ReaderAiReasoningMenuItem_Click;
            ReaderAiReasoningMenuItem.Items.Add(item);
        }
    }

    private void SelectReaderAiProvider(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        var item = ReaderAiProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, normalized, StringComparison.OrdinalIgnoreCase));
        ReaderAiProviderBox.SelectedItem = item ?? ReaderAiProviderBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private string GetSelectedReaderAiProvider() =>
        (ReaderAiProviderBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepseek";

    private void ReaderAiTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderAiTab();

    private void ReaderNotesTabButton_Click(object? sender, RoutedEventArgs e) => ShowReaderNotesTab();

    private void ShowReaderAiTab()
    {
        if (_readerZenMode) ExitReaderZenMode();
        ReaderPdfThumbnailsView.IsVisible = false;
        ReaderAssistantPanel.IsVisible = true;
        SetReaderAiPanelWidth(_readerAiPanelWidth);
        UpdateReaderAiScope();
        ReaderAiView.IsVisible = true;
        _ = ObserveReaderTaskAsync(RefreshReaderEmbeddingModelStatusAsync(_lifetimeCancellation.Token));
        ReaderNotesView.IsVisible = false;
        ReaderAiComposer.IsVisible = true;
        ReaderNotesExportBar.IsVisible = false;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: true);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: false);
        SetReaderAssistantTabState(ReaderPdfThumbnailsTabButton, selected: false);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

    private void ShowReaderNotesTab()
    {
        ReaderPdfThumbnailsView.IsVisible = false;
        if (!_readerZenMode)
        {
            ReaderAssistantPanel.IsVisible = true;
            SetReaderAiPanelWidth(_readerAiPanelWidth);
        }
        ReaderAiView.IsVisible = false;
        ReaderNotesView.IsVisible = true;
        ReaderAiComposer.IsVisible = false;
        ReaderNotesExportBar.IsVisible = true;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: false);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: true);
        SetReaderAssistantTabState(ReaderPdfThumbnailsTabButton, selected: false);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
    }

    private static void SetReaderAssistantTabState(Button button, bool selected)
    {
        button.Classes.Set("active", selected);
    }

    private double GetReaderAssistantPanelWidth() => ReaderPdfThumbnailsView.IsVisible
        ? _readerPdfThumbnailsPanelWidth : _readerAiPanelWidth;

    private double GetReaderAssistantPanelMinimumWidth() => ReaderPdfThumbnailsView.IsVisible
        ? ReaderPdfThumbnailsPanelMinimumWidth : ReaderAiPanelMinimumWidth;

    private double GetReaderAiPanelMaximumWidth()
    {
        if (ReaderRoot.Bounds.Width <= 0)
            return ReaderAiPanelDefaultWidth;

        var tocWidth = ReaderTocPanel.IsVisible
            ? 286d
            : ReaderTocCompactPanel.IsVisible ? 52d : 0d;
        var minimumReadingWidth = 360d;
        return Math.Max(
            GetReaderAssistantPanelMinimumWidth(),
            ReaderRoot.Bounds.Width - tocWidth - minimumReadingWidth);
    }

    private void ReaderRoot_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ReaderAssistantPanel?.IsVisible == true && !_readerZenMode)
            SetReaderAiPanelWidth(GetReaderAssistantPanelWidth());
    }

    private void SetReaderAiPanelWidth(double width)
    {
        if (ReaderRoot.ColumnDefinitions.Count < 3) return;
        var panelWidth = Math.Clamp(
            width,
            GetReaderAssistantPanelMinimumWidth(),
            GetReaderAiPanelMaximumWidth());
        if (ReaderPdfThumbnailsView.IsVisible)
            _readerPdfThumbnailsPanelWidth = panelWidth;
        else
            _readerAiPanelWidth = panelWidth;
        ReaderRoot.ColumnDefinitions[2].Width = new GridLength(panelWidth);
    }

    private void ReaderAssistantResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_readerZenMode || !ReaderAssistantPanel.IsVisible) return;

        var point = e.GetCurrentPoint(ReaderRoot);
        if (!point.Properties.IsLeftButtonPressed) return;

        _readerAiPanelResizing = true;
        _readerAiPanelResizeStartX = point.Position.X;
        _readerAiPanelResizeStartWidth = GetReaderAssistantPanelWidth();
        e.Pointer.Capture(ReaderAssistantResizeHandle);
        e.Handled = true;
    }

    private void ReaderAssistantResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_readerAiPanelResizing) return;

        // The handle is the assistant's left edge. Moving it left expands the
        // rail; moving it right gives more room back to the reading surface.
        var position = e.GetPosition(ReaderRoot);
        SetReaderAiPanelWidth(_readerAiPanelResizeStartWidth + _readerAiPanelResizeStartX - position.X);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
        e.Handled = true;
    }

    private void ReaderAssistantResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_readerAiPanelResizing) return;

        _readerAiPanelResizing = false;
        e.Pointer.Capture(null);
        ScheduleLinuxReaderTextFallbackReflow();
        ScheduleReaderRelayout();
        e.Handled = true;
    }

    private void QueueReaderAiScrollToEnd(bool force = false)
    {
        if (force) _readerAiFollowOutput = true;
        if (!_readerAiFollowOutput)
        {
            ReaderAiNewContentButton.IsVisible = true;
            return;
        }
        if (_readerAiScrollToEndQueued) return;
        _readerAiScrollToEndQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _readerAiScrollToEndQueued = false;
            if (_readerAiFollowOutput) ReaderAiScrollViewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void ReaderAiScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateReaderAiTurnNavigationSelection();
        // Extent/viewport changes are layout-only and must not flip follow mode.
        // If layout also changes the offset (for example, while the user scrolls
        // away as a streamed answer grows), the offset delta is still meaningful.
        if (e.OffsetDelta.Y == 0) return;
        _readerAiFollowOutput = ReaderAiScrollViewer.Extent.Height
            - ReaderAiScrollViewer.Viewport.Height - ReaderAiScrollViewer.Offset.Y < 40;
        if (_readerAiFollowOutput) ReaderAiNewContentButton.IsVisible = false;
    }

    private void ReaderAiNewContentButton_Click(object? sender, RoutedEventArgs e)
    {
        ReaderAiNewContentButton.IsVisible = false;
        QueueReaderAiScrollToEnd(force: true);
    }

    private void UpdateReaderAiScope()
    {
        ReaderAiScopeText.Text = _readerIsPdf
            ? T("当前 PDF · 第 {0} 页", _readerPdfPage)
            : T("当前书籍 · 引用原文回答");
    }

    private void ReaderAiText_FollowUpRequested(object? sender, ReaderAiFollowUpEventArgs e)
    {
        e.Handled = true;
        var quote = string.Join("\n", e.Text.Trim().ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line));
        var draft = ReaderAiQuestionBox.Text ?? string.Empty;
        ReaderAiQuestionBox.Text = T("针对这段对话：\n{0}\n\n{1}", quote, draft);
        ReaderAiQuestionBox.CaretIndex = ReaderAiQuestionBox.Text.Length;
        ReaderAiQuestionBox.Focus();
    }

    private async void ReaderAiCopyButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ReaderAiMessageViewModel message) return;
        try
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(message.Content);
                ReaderAiStatusText.Text = T("回答已复制。");
            }
        }
        catch (Exception exception) { ReaderAiStatusText.Text = T("复制失败：{0}", exception.Message); }
    }

    private void ReaderAiRetryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_readerAiBusy && (sender as Control)?.DataContext is ReaderAiMessageViewModel message)
            message.RetryAction?.Invoke();
    }

    private async void ReaderAiSaveAnswerButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ReaderAiMessageViewModel message) return;
        var content = message.Content;
        var references = message.Sources.Select(source => $"- {source.Label}\n\n  {source.Content.ReplaceLineEndings(" ")}").ToArray();
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("保存 AI 回答"), SuggestedFileName = "Kreader-AI.md",
                FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content + (references.Length > 0 ? "\n\n---\n\n" + string.Join("\n\n", references) : string.Empty));
            ReaderAiStatusText.Text = T("回答与来源已保存。");
        }
        catch (Exception exception) { ReaderAiStatusText.Text = T("保存失败：{0}", exception.Message); }
    }
}
