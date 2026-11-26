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
/// Reading dashboard, platform diagnostics and the library trash.
/// </summary>
public partial class MainWindow
{
    private async void ReadingDashboardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowStage3Page(ReadingDashboardPage);
        await RefreshReadingDashboardAsync();
    }

    private void DashboardPeriodButton_Click(object? sender, RoutedEventArgs e)
    {
        _readingDashboardPeriod = _readingDashboardPeriod switch
        {
            ReadingPeriod.Week => ReadingPeriod.Month,
            ReadingPeriod.Month => ReadingPeriod.Year,
            _ => ReadingPeriod.Week
        };
        PresentDashboardDays();
    }

    private async Task RefreshReadingDashboardAsync()
    {
        var generation = ++_readingDashboardGeneration;
        try
        {
            var dashboard = await _readerData.GetReadingDashboardAsync(
                100, _lifetimeCancellation.Token, 366);
            if (generation != _readingDashboardGeneration) return;
            DashboardBooksStartedText.Text = T("{0} 本", dashboard.BooksStarted);
            DashboardBooksFinishedText.Text = T("{0} 本", dashboard.BooksFinished);
            DashboardTotalTimeText.Text = FormatReadingTime(dashboard.TotalSeconds);
            ToolTip.SetTip(DashboardTotalTimeText,
                dashboard.ListeningSeconds > 0
                    ? T("累计听书 {0}", FormatReadingTime(dashboard.ListeningSeconds))
                    : null);
            DashboardAverageProgressText.Text = T("平均进度 {0:0.#}%", dashboard.AverageProgress);
            DashboardBookmarksText.Text = $"{dashboard.BookmarkCount.ToString(CultureInfo.InvariantCulture)} / {dashboard.AnnotationCount.ToString(CultureInfo.InvariantCulture)}";

            var lastSevenDays = dashboard.DailyReading.TakeLast(7).ToArray();
            var weekSeconds = lastSevenDays.Sum(day => day.ActiveSeconds);
            var averageText = FormatReadingTime((long)Math.Round(weekSeconds / 7d));
            DashboardDailyAverageText.Text = (weekSeconds, dashboard.ListeningSeconds) switch
            {
                (> 0, > 0) => T("近 7 天日均 {0} · 含听书 {1}", averageText, FormatReadingTime(dashboard.ListeningSeconds)),
                (> 0, <= 0) => T("近 7 天日均 {0}", averageText),
                (_, > 0) => T("累计听书 {0}", FormatReadingTime(dashboard.ListeningSeconds)),
                _ => T("近 7 天日均 —")
            };
            DashboardStreakText.Text = T("{0} 天",
                Math.Max(dashboard.StreakDays, ReadingMedalCatalog.ComputeStreakDays(dashboard.DailyReading)));
            PopulateReadingMedals(dashboard);
            DashboardStatusText.IsVisible = false;

            string BookTitle(ReadingDashboardBook item)
            {
                var title = string.IsNullOrWhiteSpace(item.Title) ? T("未知书名") : item.Title;
                return item.IsInLibrary ? title : T("{0}（已移出书库）", title);
            }
            _readingDashboardItems.Clear();
            foreach (var item in dashboard.RecentBooks)
            {
                var recent = new Stage3DashboardRecentViewModel(
                    BookTitle(item),
                    item.ProgressPercent,
                    item.CumulativeSeconds,
                    item.UpdatedAt)
                {
                    BookId = item.BookId,
                    BookFileId = item.BookFileId,
                    ListeningSeconds = item.ListeningSeconds
                };
                _readingDashboardItems.Add(recent);
            }
            DashboardRecentEmptyText.IsVisible = _readingDashboardItems.Count == 0;

            _readingDashboardDailyReading = dashboard.DailyReading;
            PresentDashboardDays();

            PopulateDashboardSplitBars(DashboardBookTimes, dashboard.MostReadBooks
                .Select(item => (
                    BookTitle(item),
                    (double)item.CumulativeSeconds,
                    (double)Math.Clamp(item.ListeningSeconds, 0, Math.Max(0, item.CumulativeSeconds)),
                    item.ListeningSeconds > 0
                        ? T("{0}（含听书 {1}）", FormatReadingTime(item.CumulativeSeconds), FormatReadingTime(item.ListeningSeconds))
                        : FormatReadingTime(item.CumulativeSeconds))));

            var progressValues = dashboard.Books.Select(item => item.HasFinished ? 100d : Math.Min(item.ProgressPercent, 99.49)).ToArray();
            PopulateDashboardBars(DashboardProgressBuckets,
            [
                ("0–24%", (double)progressValues.Count(value => value < 25), T("{0} 本", progressValues.Count(value => value < 25))),
                ("25–49%", (double)progressValues.Count(value => value >= 25 && value < 50), T("{0} 本", progressValues.Count(value => value >= 25 && value < 50))),
                ("50–74%", (double)progressValues.Count(value => value >= 50 && value < 75), T("{0} 本", progressValues.Count(value => value >= 50 && value < 75))),
                ("75–99%", (double)progressValues.Count(value => value >= 75 && value < 99.5), T("{0} 本", progressValues.Count(value => value >= 75 && value < 99.5))),
                ("完成", (double)progressValues.Count(value => value >= 99.5), T("{0} 本", progressValues.Count(value => value >= 99.5)))
            ]);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DashboardStatusText.Text = T("阅读数据暂时不可用：{0}", UiText.Localize(exception.Message));
            DashboardStatusText.IsVisible = true;
        }
    }

    private void PresentDashboardDays()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var range = ReadingPeriodRange.Create(_readingDashboardPeriod, today);
        var buckets = range.BuildBuckets(_readingDashboardDailyReading, today);
        var periodLabel = T(_readingDashboardPeriod switch
        { ReadingPeriod.Week => "周", ReadingPeriod.Month => "月", _ => "年" });
        var nextPeriodLabel = T(_readingDashboardPeriod switch
        { ReadingPeriod.Week => "月", ReadingPeriod.Month => "年", _ => "周" });
        DashboardPeriodButton.MinWidth = UiText.IsEnglish ? 60 : 36;
        DashboardPeriodButton.Content = periodLabel;
        ToolTip.SetTip(DashboardPeriodButton, T("点击切换到{0}视图", nextPeriodLabel));
        Avalonia.Automation.AutomationProperties.SetName(DashboardPeriodButton,
            T("当前{0}视图，点击切换到{1}视图", periodLabel, nextPeriodLabel));
        DashboardDailyTitleText.Text = T(_readingDashboardPeriod switch
        { ReadingPeriod.Week => "本周阅读时长", ReadingPeriod.Month => "本月阅读时长", _ => "今年阅读时长" });
        var maximumSeconds = Math.Max(1, buckets.Max(bucket => bucket.ActiveSeconds));
        var labelInterval = Math.Max(1, (int)Math.Ceiling(buckets.Count / 7d));
        DashboardDays.Clear();
        for (var index = 0; index < buckets.Count; index++)
        {
            var bucket = buckets[index];
            var showLabel = index == buckets.Count - 1 || index % labelInterval == 0;
            var timeLabel = FormatReadingTime(bucket.ActiveSeconds);
            var dateLabel = _readingDashboardPeriod == ReadingPeriod.Year
                ? T("{0} 月", bucket.StartDate.Month) : bucket.StartDate.ToString("MM-dd", CultureInfo.InvariantCulture);
            var detailTime = bucket.ListeningSeconds > 0
                ? T("{0}（含听书 {1}）", timeLabel, FormatReadingTime(bucket.ListeningSeconds))
                : timeLabel;
            var barHeight = bucket.ActiveSeconds == 0 ? 4 : 10 + 170d * bucket.ActiveSeconds / maximumSeconds;
            DashboardDays.Add(new Stage3DashboardDayViewModel(showLabel ? dateLabel : "",
                showLabel && bucket.ActiveSeconds > 0 ? timeLabel : "",
                barHeight,
                bucket.Contains(today) ? 1 : 0.35)
            {
                DetailLabel = $"{bucket.StartDate:yyyy-MM-dd} — {bucket.EndDate:yyyy-MM-dd}: {detailTime}"
            });
        }
    }

    private async void DashboardRecentBook_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Stage3DashboardRecentViewModel item })
            await OpenDashboardRecentBookAsync(item);
    }

    private async Task OpenDashboardRecentBookAsync(Stage3DashboardRecentViewModel item)
    {
        if (_bookOpenInProgress > 0) return;
        BookCardViewModel? temporaryCard = null;
        try
        {
            var card = ViewModel.Books.FirstOrDefault(candidate => candidate.Book.Id == item.BookId);
            if (card is null)
            {
                var book = await _library.GetBookAsync(item.BookId, _lifetimeCancellation.Token);
                if (book is not null) card = temporaryCard = new BookCardViewModel(book, _paths.Data);
            }
            var file = card?.Book.Files.FirstOrDefault(candidate => candidate.Id == item.BookFileId);
            if (card is null || file is null)
            {
                DashboardStatusText.Text = T("找不到对应的本地书籍文件。");
                DashboardStatusText.IsVisible = true;
                return;
            }
            await OpenBookAsync(card, file, restoreProgress: true);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DashboardStatusText.Text = T("打开书籍失败：{0}", UiText.Localize(exception.Message));
            DashboardStatusText.IsVisible = true;
        }
        finally { temporaryCard?.Dispose(); }
    }

    private static void PopulateDashboardBars(
        ObservableCollection<Stage3DashboardBarViewModel> target,
        IEnumerable<(string Label, double Value, string ValueLabel)> values)
    {
        target.Clear();
        var materialized = values.ToArray();
        var maximum = Math.Max(1, materialized.Select(item => item.Value).DefaultIfEmpty().Max());
        foreach (var item in materialized)
            target.Add(new Stage3DashboardBarViewModel(
                item.Label,
                item.ValueLabel,
                item.Value <= 0 ? 0 : Math.Max(8, 210d * item.Value / maximum)));
    }

    private static void PopulateDashboardSplitBars(
        ObservableCollection<Stage3DashboardBarViewModel> target,
        IEnumerable<(string Label, double TotalValue, double ListeningValue, string ValueLabel)> values)
    {
        target.Clear();
        var materialized = values.ToArray();
        var maximum = Math.Max(1, materialized.Select(item => item.TotalValue).DefaultIfEmpty().Max());
        foreach (var item in materialized)
        {
            var totalWidth = item.TotalValue <= 0 ? 0 : Math.Max(8, 210d * item.TotalValue / maximum);
            var listeningWidth = item.TotalValue > 0 && item.ListeningValue > 0
                ? Math.Clamp(totalWidth * item.ListeningValue / item.TotalValue, 2d, totalWidth)
                : 0d;
            target.Add(new Stage3DashboardBarViewModel(item.Label, item.ValueLabel, totalWidth)
            {
                ListeningBarWidth = listeningWidth
            });
        }
    }

    private async void RefreshReadingDashboardButton_Click(object? sender, RoutedEventArgs e) =>
        await RefreshReadingDashboardAsync();

    private async void ExportReadingDashboardButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshReadingDashboardAsync();
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("导出阅读数据"),
            SuggestedFileName = T("Libzora-阅读数据-{0}.csv", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var csv = new StringBuilder(T("书名,进度,阅读秒数,听书秒数,最近阅读") + "\r\n");
        foreach (var item in _readingDashboardItems)
        {
            csv.Append('"')
                .Append(item.Title.Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append("\",")
                .Append(item.ProgressPercent.ToString("0.##", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(item.Seconds.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .Append(item.ListeningSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(',')
                .AppendLine(item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        await File.WriteAllTextAsync(path, csv.ToString(), new UTF8Encoding(true), _lifetimeCancellation.Token);
        DashboardStatusText.Text = T("已导出 {0} 条阅读数据到 {1}", _readingDashboardItems.Count, path);
        DashboardStatusText.IsVisible = true;
    }

    private static string FormatReadingTime(long seconds)
    {
        if (seconds < 60) return T("{0} 秒", seconds);
        if (seconds < 3600) return T("{0:0.#} 分", seconds / 60d);
        return T("{0:0.#} 小时", seconds / 3600d);
    }

    private void UpdateDiagnosticsTexts()
    {
        RepairDiagnosticsButton.Content = T("一键修复");
        RefreshDiagnosticsButton.Content = T("重新检测");
    }

    private void UpdateDiagnosticsRepairButtonState()
    {
        RepairDiagnosticsButton.IsEnabled = !_diagnosticsRepairBusy
            && !_diagnosticsRefreshing
            && PlatformDiagnostics.Any(item => item.CanRepair);
    }

    private async Task RefreshPlatformDiagnosticsAsync()
    {
        if (_diagnosticsRefreshing) return;
        _diagnosticsRefreshing = true;
        UpdateDiagnosticsTexts();
        RefreshDiagnosticsButton.IsEnabled = false;
        RepairDiagnosticsButton.IsEnabled = false;
        DiagnosticsStatusText.Text = T("正在检测…");
        try
        {
            var diagnostics = (await _platformDiagnostics.CollectAsync(
                _appSettings.CalibrePath,
                _lifetimeCancellation.Token)).ToList();
            try
            {
                var tts = await _readerTts.CheckEnvironmentAsync(_lifetimeCancellation.Token);
                diagnostics.Add(new PlatformDiagnostic(
                    "TTS",
                    tts.IsAvailable
                        ? PlatformDiagnosticStatus.Ready
                        : _readerTts.CanOpen
                        ? PlatformDiagnosticStatus.Warning
                        : PlatformDiagnosticStatus.Unavailable,
                    tts.Message,
                    tts.IsAvailable || !_readerTts.CanRepairEnvironment
                        ? PlatformDiagnosticRepairKind.None
                        : PlatformDiagnosticRepairKind.InstallTts));
            }
            catch (Exception exception)
            {
                diagnostics.Add(new PlatformDiagnostic(
                    "TTS",
                    PlatformDiagnosticStatus.Warning,
                    exception.Message,
                    _readerTts.CanRepairEnvironment
                        ? PlatformDiagnosticRepairKind.InstallTts
                        : PlatformDiagnosticRepairKind.None));
            }

            foreach (var item in PlatformDiagnostics)
                item.Dispose();
            PlatformDiagnostics.Clear();
            foreach (var item in diagnostics)
                PlatformDiagnostics.Add(new PlatformDiagnosticViewModel(item));
            DiagnosticsStatusText.Text = T("检测完成：{0} 项。", diagnostics.Count);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = T("检测失败：{0}", UiText.Localize(exception.Message));
        }
        finally
        {
            _diagnosticsRefreshing = false;
            RefreshDiagnosticsButton.IsEnabled = !_diagnosticsRepairBusy;
            UpdateDiagnosticsRepairButtonState();
        }
    }

    private async void RefreshDiagnosticsButton_Click(object? sender, RoutedEventArgs e) =>
        await RefreshPlatformDiagnosticsAsync();

    private async void RepairDiagnosticsButton_Click(object? sender, RoutedEventArgs e) =>
        await RepairDiagnosticsAsync();

    private async void RepairDiagnosticButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlatformDiagnosticViewModel diagnostic }) return;
        await RepairDiagnosticsAsync(diagnostic.Item.RepairKind);
    }

    private async Task RepairDiagnosticsAsync(
        PlatformDiagnosticRepairKind? requestedKind = null)
    {
        if (_diagnosticsRepairBusy || _diagnosticsRefreshing) return;

        PlatformDiagnosticRepairKind[] repairKinds;
        if (requestedKind is { } requested)
        {
            repairKinds = requested == PlatformDiagnosticRepairKind.None
                ? []
                : [requested];
        }
        else
        {
            repairKinds = PlatformDiagnostics
                .Where(item => item.CanRepair)
                .Select(item => item.Item.RepairKind)
                .Distinct()
                .ToArray();
        }

        if (repairKinds.Length == 0)
        {
            DiagnosticsStatusText.Text = T("没有可自动修复的项目。");
            return;
        }

        if (NetworkEnabledCheck.IsChecked == false)
        {
            DiagnosticsStatusText.Text = T("请先开启“允许网络功能”。");
            return;
        }

        var repairNames = repairKinds
            .Select(GetDiagnosticRepairName)
            .ToArray();
        var message = T(
            "将尝试自动安装 {0}。安装程序可能请求系统权限，是否继续？",
            string.Join("、", repairNames));
        if (!await ConfirmAsync(
                T("一键修复"),
                message,
                T("继续安装"),
                _lifetimeCancellation.Token))
        {
            return;
        }

        _diagnosticsRepairBusy = true;
        RefreshDiagnosticsButton.IsEnabled = false;
        UpdateDiagnosticsRepairButtonState();
        var repairing = PlatformDiagnostics
            .Where(item => repairKinds.Contains(item.Item.RepairKind))
            .ToArray();
        foreach (var item in repairing)
            item.IsRepairing = true;
        try
        {
            _paths.EnsureDirectories();
            DiagnosticsStatusText.Text = T("正在安装 {0}", string.Join("、", repairNames));
            foreach (var repairKind in repairKinds)
            {
                switch (repairKind)
                {
                    case PlatformDiagnosticRepairKind.InstallCalibre:
                        await RunCalibreSetupAsync(
                            installPlugin: false,
                            value => DiagnosticsStatusText.Text =
                                UiText.Localize(value.Message)
                                + (value.Percentage is { } percentage ? $" {percentage:0}%" : string.Empty));
                        break;
                    case PlatformDiagnosticRepairKind.InstallTts:
                        await RunTtsDiagnosticRepairAsync();
                        break;
                }
            }

            await RefreshPlatformDiagnosticsAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = T(
                "诊断修复失败：{0}",
                UiText.Localize(exception.Message));
        }
        finally
        {
            foreach (var item in repairing)
                item.IsRepairing = false;
            _diagnosticsRepairBusy = false;
            RefreshDiagnosticsButton.IsEnabled = !_diagnosticsRefreshing;
            UpdateDiagnosticsRepairButtonState();
        }
    }

    private async Task RunTtsDiagnosticRepairAsync()
    {
        if (!_readerTts.CanRepairEnvironment)
        {
            DiagnosticsStatusText.Text = T("当前版本没有配置 TTS 自动安装器。");
            return;
        }

        var progress = new Progress<TtsSetupProgress>(update =>
        {
            if (_diagnosticsRepairBusy && !_lifetimeCancellation.IsCancellationRequested)
                DiagnosticsStatusText.Text = UiText.Localize(update.Message);
        });
        try
        {
            var availability = await _readerTts.EnsureEnvironmentReadyAsync(
                progress,
                _lifetimeCancellation.Token);
            _readerTtsEnvironmentChecked = true;
            _readerTtsNotice = availability.IsAvailable
                ? null
                : availability.Message;
            UpdateReaderTtsUi();
            if (!availability.IsAvailable)
            {
                DiagnosticsStatusText.Text = T(
                    "安装失败：{0}",
                    UiText.Localize(availability.Message));
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            DiagnosticsStatusText.Text = T("安装已取消。");
        }
        catch (Exception exception)
        {
            DiagnosticsStatusText.Text = T(
                "安装失败：{0}",
                UiText.Localize(exception.Message));
        }
    }

    private static string GetDiagnosticRepairName(
        PlatformDiagnosticRepairKind repairKind) => repairKind switch
    {
        PlatformDiagnosticRepairKind.InstallCalibre => T("下载/安装 Calibre"),
        PlatformDiagnosticRepairKind.InstallTts => T("安装 TTS"),
        _ => T("修复")
    };

    private bool _trashPurgeAllBusy;

    private async Task RefreshTrashItemsAsync()
    {
        try
        {
            var items = await _library.GetTrashItemsAsync(_lifetimeCancellation.Token);
            foreach (var item in LibraryTrashItems)
                item.Dispose();
            LibraryTrashItems.Clear();
            foreach (var item in items)
                LibraryTrashItems.Add(new LibraryTrashItemViewModel(item));
            var hasItems = LibraryTrashItems.Count > 0;
            LibraryTrashEmptyText.IsVisible = !hasItems;
            LibraryTrashList.IsVisible = hasItems;
            PurgeAllTrashButton.IsVisible = hasItems;
            PurgeAllTrashButton.IsEnabled = !_trashPurgeAllBusy && hasItems;
            LibraryTrashStatusText.Text = !hasItems
                ? string.Empty
                : T("共 {0} 项，恢复前请确认原文件位置没有同名文件。", LibraryTrashItems.Count);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LibraryTrashStatusText.Text = T("读取回收站失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void RestoreTrashButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_trashPurgeAllBusy || sender is not Button { Tag: LibraryTrashItemViewModel item }) return;
        try
        {
            await _library.RestoreTrashItemAsync(item.Item.Id, _lifetimeCancellation.Token);
            await RefreshLibraryDataAsync(_lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            await RefreshTrashItemsAsync();
            SetTaskStatus(T("已恢复《{0}》。", item.Item.Title));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LibraryTrashStatusText.Text = T("恢复失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void PurgeTrashButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_trashPurgeAllBusy || sender is not Button { Tag: LibraryTrashItemViewModel item }) return;
        var confirmed = await ConfirmAsync(
            T("永久清理回收站项目"),
            T("《{0}》将被永久删除，之后无法恢复。继续吗？", item.Item.Title),
            T("永久删除"),
            _lifetimeCancellation.Token);
        if (!confirmed) return;
        try
        {
            await _library.PurgeTrashItemAsync(item.Item.Id, _lifetimeCancellation.Token);
            await RefreshTrashItemsAsync();
            SetTaskStatus(T("已永久清理《{0}》。", item.Item.Title));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LibraryTrashStatusText.Text = T("清理失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private async void PurgeAllTrashButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_trashPurgeAllBusy || LibraryTrashItems.Count == 0) return;
        var items = LibraryTrashItems.Select(item => item.Item).ToArray();
        var confirmed = await ConfirmAsync(
            T("清空回收站"),
            T("回收站中的 {0} 个项目将被永久删除，之后无法恢复。继续吗？", items.Length),
            T("全部永久删除"),
            _lifetimeCancellation.Token,
            allowDiagnosticAutoConfirm: false);
        if (!confirmed) return;

        _trashPurgeAllBusy = true;
        PurgeAllTrashButton.IsEnabled = false;
        var purgedCount = 0;
        Exception? failure = null;
        try
        {
            foreach (var item in items)
            {
                await _library.PurgeTrashItemAsync(item.Id, _lifetimeCancellation.Token);
                purgedCount++;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _trashPurgeAllBusy = false;
            await RefreshTrashItemsAsync();
        }

        if (failure is not null)
            LibraryTrashStatusText.Text = T("清理失败：{0}", UiText.Localize(failure.Message));
        else if (purgedCount > 0)
            SetTaskStatus(T("已清空回收站，共永久清理 {0} 项。", purgedCount));
    }
}
