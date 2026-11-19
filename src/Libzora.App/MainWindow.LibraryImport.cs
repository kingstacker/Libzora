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
/// Importing files and folders, format selection and conflict handling.
/// </summary>
public partial class MainWindow
{
    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var selectedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0)
        {
            SetTaskStatus(T("没有选择可导入的文件。"));
            return;
        }

        try
        {
            var inputPaths = LibraryDropImportPolicy.ExpandImportableFiles(selectedPaths);
            if (inputPaths.Length == 0)
            {
                SetTaskStatus(T("所选位置没有 EPUB、PDF、MOBI、AZW3 或 TXT 文件。"));
                await ShowMessageAsync(T("无法导入"), T("拖入的文件或文件夹中没有 EPUB、PDF、MOBI、AZW3 或 TXT 书籍文件。"));
                return;
            }
            var requestedFormats = await ChooseImportFormatsAsync(inputPaths);
            if (requestedFormats is null)
            {
                SetTaskStatus(T("已取消导入。"));
                return;
            }
            // TXT files are imported with automatic encoding, heading and
            // volume detection. The rules preview used to gate every import
            // on an extra confirmation step; the defaults handle the common
            // case, so import proceeds without prompting.
            var txtImportRules = inputPaths
                .Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    path => path,
                    _ => TxtImportRules.Default,
                    StringComparer.OrdinalIgnoreCase);
            SetTaskStatus(T("正在导入 {0} 个位置…", inputPaths.Length));
            ShowTaskProgressPopup();
            TaskProgressPopupText.Text = T("正在导入 {0} 个位置…", inputPaths.Length);
            var importTitle = T("书库导入");
            var importOutcome = await RunTrackedTaskAsync(importTitle, BackgroundTaskKind.Import, async (context, token) =>
            {
            context.ReportBooks(inputPaths.Select(Path.GetFileNameWithoutExtension).Select(name => name ?? string.Empty).ToArray());
            using var progress = new CoalescingUiProgress<TransferProgress>(value =>
            {
                var message = string.IsNullOrWhiteSpace(value.Message)
                    ? T("正在导入：{0:0}%", value.Percentage)
                    : UiText.Localize(value.Message);
                context.ReportProgress(message, (int)value.Percentage, 100);
                SetTaskStatus(message);
                TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = message;
            });
            var imported = await ViewModel.ImportAsync(
                inputPaths,
                progress,
                token,
                ResolveImportConflictAsync,
                txtImportRules);
            progress.Dispose(); // Ignore queued import updates once format generation starts.
            var importedTitles = ViewModel.Books.Where(card => card.Book is not null)
                .ToDictionary(card => card.Book!.Id, card => card.Book!.Title);
            context.ReportBooks(imported.Items.Select(item => item.Book?.Title
                ?? (item.BookId is { } id ? importedTitles.GetValueOrDefault(id) : null)
                ?? Path.GetFileNameWithoutExtension(item.SourcePath)).ToArray());
            context.ReportProgress(T("正在补齐阅读格式"));
            var generated = await AutoGenerateReaderFormatsForImportsAsync(imported, token, requestedFormats, context);
            await RefreshLibraryMatchRecordsAsync(token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            await RefreshBookSyncStatusesAsync(token);
            var formatSummary = generated.Failures.Count > 0
                ? T("；格式补齐失败 {0} 项", generated.Failures.Count)
                : generated.GeneratedCount > 0
                    ? T("；已补齐 {0} 个 EPUB/AZW3 文件", generated.GeneratedCount)
                    : string.Empty;
            context.ReportProgress(BuildImportSummary(imported, formatSummary));
            return (Result: imported, AutomaticFormats: generated);
            }, _lifetimeCancellation.Token);
            var result = importOutcome.Result;
            var automaticFormats = importOutcome.AutomaticFormats;
            HideTaskProgressPopup();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            // ViewModel.ImportAsync rebuilds the cards before returning, so the
            // library-change notification may have refreshed the old card
            // instances. Recalculate against the cards now shown; otherwise a
            // normal import leaves every newly-created badge at its default
            // NotSynced value until the next full sync.
            await RefreshBookSyncStatusesAsync(_lifetimeCancellation.Token);
            var automaticSuffix = automaticFormats.Failures.Count > 0
                ? T("；格式补齐失败 {0} 项", automaticFormats.Failures.Count)
                : automaticFormats.GeneratedCount > 0
                    ? T("；已补齐 {0} 个 EPUB/AZW3 文件", automaticFormats.GeneratedCount)
                    : string.Empty;
            var importSummary = BuildImportSummary(result, automaticSuffix);
            SetTaskStatus(importSummary);
            ShowSettingsCapsule(importSummary, 2400, success: result.FailureCount == 0);
        }
        catch (OperationCanceledException)
        {
            HideTaskProgressPopup();
            SetTaskStatus(T("已取消导入。"));
            if (!_lifetimeCancellation.IsCancellationRequested) await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            HideTaskProgressPopup();
            SoftwareLog.ErrorWithoutMessage("library.import", exception);
            SetTaskStatus(T("导入失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("导入失败"), UiText.Localize(exception.Message));
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?> ChooseImportFormatsAsync(
        IReadOnlyList<string> files)
    {
        if (files.Count > PerFileImportFormatSelectionLimit)
        {
            var continueImport = await ConfirmAsync(
                T("导入大量书籍"),
                T("共 {0} 个文件。为避免一次创建数千个设置控件，本次将关闭逐项格式补齐并仅导入原始文件。是否继续？", files.Count));
            return continueImport
                ? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
                : null;
        }

        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        foreach (var file in files)
        {
            var toggle = new ToggleSwitch
            {
                // Start consistent with the global "导入后补齐 EPUB 与 AZW3"
                // preference; each row can still be overridden individually.
                IsChecked = _appSettings.AutoGenerateEpubAndAzw3OnImport,
                OnContent = T("补齐"),
                OffContent = T("仅导入")
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(file), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = Path.GetDirectoryName(file) ?? string.Empty, FontSize = 10, Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"), TextTrimming = TextTrimming.CharacterEllipsis }
                }
            });
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            ImportFormatSelectionList.Children.Add(new Border
            {
                Padding = new Thickness(10, 8),
                BorderBrush = AppAppearanceResources.GetBrush("HairlineBrush"),
                BorderThickness = new Thickness(1),
                Child = row
            });
            _importFormatSelectionRows.Add((file, toggle));
        }
        ImportFormatSelectionSummaryText.Text = T("共 {0} 个文件。可逐项决定是否在导入后补齐 EPUB 与 AZW3；原始文件始终保留。", files.Count);
        ShowOverlay(ImportFormatSelectionOverlay);
        ImportFormatSelectionOverlay.Focus();
        _importFormatSelectionCompletion = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return await _importFormatSelectionCompletion.Task;
    }

    private void CompleteImportFormatSelection(bool import)
    {
        var completion = _importFormatSelectionCompletion;
        if (completion is null) return;
        _importFormatSelectionCompletion = null;
        ImportFormatSelectionOverlay.IsVisible = false;
        var result = import
            ? _importFormatSelectionRows.ToDictionary(
                row => row.FilePath,
                row => (IReadOnlyCollection<string>)(row.Toggle.IsChecked == true ? new[] { "epub", "azw3" } : Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase)
            : null;
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        completion.TrySetResult(result);
    }

    private void ImportFormatSelectionPrimaryButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(true);

    private void ImportFormatSelectionCancelButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(false);

    private void ImportFormatSelectionOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteImportFormatSelection(e.Key == Key.Enter);
    }

    private async Task<ImportConflictResolution> ResolveImportConflictAsync(ImportBookConflict conflict)
    {
        if (_importConflictCompletion is not null)
            return ImportConflictResolution.Skip;

        ImportConflictTitleText.Text = T("发现同名书籍");
        ImportConflictSummaryText.Text = T(
            "导入文件《{0}》与书库中的《{1}》标题和作者相同，但文件内容不同。请选择导入方式。",
            Path.GetFileName(conflict.SourcePath),
            conflict.ExistingBook.Title);
        var existingFormats = conflict.ExistingBook.Files
            .Select(file => file.Format.Trim().TrimStart('.').ToUpperInvariant())
            .Where(format => format.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ImportConflictExistingFilesText.Text = existingFormats.Length == 0
            ? T("现有记录还没有文件格式。")
            : T("现有格式：{0}", string.Join("、", existingFormats));
        ImportConflictMergeButton.Content = T("添加为格式");
        ImportConflictSeparateButton.Content = T("作为新版本");
        ImportConflictSkipButton.Content = T("跳过");

        var completion = new TaskCompletionSource<ImportConflictResolution>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _importConflictCompletion = completion;
        ShowOverlay(ImportConflictOverlay);
        ImportConflictOverlay.Focus();
        try
        {
            return await completion.Task.WaitAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return ImportConflictResolution.Skip;
        }
        finally
        {
            if (ReferenceEquals(_importConflictCompletion, completion))
            {
                _importConflictCompletion = null;
                ImportConflictOverlay.IsVisible = false;
            }
        }
    }

    private void CompleteImportConflict(ImportConflictResolution resolution)
    {
        ImportConflictOverlay.IsVisible = false;
        _importConflictCompletion?.TrySetResult(resolution);
    }

    private void ImportConflictMergeButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportConflict(ImportConflictResolution.AddAsFormat);

    private void ImportConflictSeparateButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportConflict(ImportConflictResolution.KeepSeparate);

    private void ImportConflictSkipButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportConflict(ImportConflictResolution.Skip);

    private void ImportConflictOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CompleteImportConflict(ImportConflictResolution.Skip);
    }
}
