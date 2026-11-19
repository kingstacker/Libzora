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
/// Window icon, auto-hide scrollbars and library chrome helpers.
/// </summary>
public partial class MainWindow
{
    private void ApplyApplicationIcon()
    {
        try
        {
            using var iconStream = AssetLoader.Open(
                new Uri("avares://Libzora.App/Assets/Icons/libzora.png"));
            Icon = new WindowIcon(iconStream);
        }
        catch
        {
            // A missing icon must not prevent the app from starting.
        }
    }

    // Marks a library ListBox's ScrollViewer with the bookScroll/.scrolling
    // classes the App.axaml auto-hide styles key on. The ListBox can rebuild
    // its template ScrollViewer when its visual tree changes, so the caller
    // also retries from TemplateApplied.
    private void AttachBookGridAutoHideScrollbar()
    {
        _bookGridScrollViewer = BookGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        AttachBookAutoHideScrollbar(BookGrid);
    }

    private void AttachBookListAutoHideScrollbar() => AttachBookAutoHideScrollbar(BookList);

    private void AttachBookAutoHideScrollbar(ListBox bookList)
    {
        if (bookList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } viewer)
            return;
        if (viewer.Classes.Contains("bookScroll")) return;
        viewer.Classes.Add("bookScroll");
        viewer.Classes.Add("scrolling");
        var idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        idleTimer.Tick += (_, _) =>
        {
            idleTimer.Stop();
            viewer.Classes.Remove("scrolling");
        };
        viewer.ScrollChanged += (_, _) =>
        {
            viewer.Classes.Add("scrolling");
            idleTimer.Stop();
            idleTimer.Start();        };
        viewer.DetachedFromVisualTree += (_, _) =>
        {
            idleTimer.Stop();
            viewer.Classes.Remove("scrolling");
        };
        Dispatcher.UIThread.Post(idleTimer.Start, DispatcherPriority.Loaded);
    }

    // The settings page owns its ScrollViewer directly, so it can use the same
    // transient thumb behavior without waiting for a ListBox template rebuild.
    private void AttachSettingsAutoHideScrollbar()
    {
        if (SettingsScrollViewer.Classes.Contains("settingsScroll"))
        {
            SettingsScrollViewer.Classes.Add("scrolling");
            var idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            idleTimer.Tick += (_, _) =>
            {
                idleTimer.Stop();
                SettingsScrollViewer.Classes.Remove("scrolling");
            };
            SettingsScrollViewer.ScrollChanged += (_, e) =>
            {
                if (e.OffsetDelta.Y == 0) return;
                SettingsScrollViewer.Classes.Add("scrolling");
                idleTimer.Stop();
                idleTimer.Start();
            };
            Dispatcher.UIThread.Post(idleTimer.Start, DispatcherPriority.Loaded);
        }
    }

    private void AttachReadingDashboardAutoHideScrollbars()
    {
        foreach (var viewer in new[] { ReadingDashboardScrollViewer, DashboardRecentScrollViewer })
        {
            var idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            idleTimer.Tick += (_, _) =>
            {
                idleTimer.Stop();
                viewer.Classes.Remove("scrolling");
            };
            viewer.ScrollChanged += (_, e) =>
            {
                // Scrolling the nested recent list must not reveal the page's scrollbar.
                if (!ReferenceEquals(e.Source, viewer) || e.OffsetDelta == default) return;
                viewer.Classes.Add("scrolling");
                idleTimer.Stop();
                idleTimer.Start();
            };
            viewer.DetachedFromVisualTree += (_, _) =>
            {
                idleTimer.Stop();
                viewer.Classes.Remove("scrolling");
            };
        }
    }

    public LibraryViewModel ViewModel { get; }

    private static string T(string source, params object?[] args) => UiText.Get(source, args);

    private string BuildImportSummary(ImportBatchResult result, string suffix = "")
    {
        if (result.FailureCount == 0)
        {
            return result.SkippedCount == 0
                ? T("导入完成：新增 {0} 个文件{1}。", result.AddedCount, suffix)
                : T("导入完成：新增 {0} 个文件，跳过 {1} 个重复或已有文件{2}。", result.AddedCount, result.SkippedCount, suffix);
        }

        return result.SkippedCount == 0
            ? T("导入完成：新增 {0} 个文件，{1} 个失败{2}。", result.AddedCount, result.FailureCount, suffix)
            : T("导入完成：新增 {0} 个文件，跳过 {1} 个重复或已有文件，{2} 个失败{3}。", result.AddedCount, result.SkippedCount, result.FailureCount, suffix);
    }

    private void MainWindowLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MainWindowLanguageChanged(sender, e));
            return;
        }

        RefreshInteractiveControlToolTips();
        UpdateReaderNavigationLabels();
        UpdateS3SyncIndicator(_s3SyncIndicatorState, _s3SyncIndicatorError);
        UpdateAccountSyncPresentation();
        UpdateS3SettingsActions();
        S3SyncDeviceText.Text = T("当前设备 ID：{0}", _s3SyncStoredSettings.DeviceId[..Math.Min(12, _s3SyncStoredSettings.DeviceId.Length)]);
        RefreshOnboardingLocalizedChoices();
        UpdateSidebarWidth();
        if (_filterControlsReady)
        {
            RefreshLocalizedFilterItems();
            ViewModel.RefreshView();
        }
        RefreshDeviceShelfLanguage();
        RefreshLocalizedReadingMaterialsSourceFilter();
        UpdateLibraryUi();
        if (_selectedCard is not null)
        {
            UpdateDetailCollectionsSelector(_selectedCard);
            UpdateDetailActionIcons(_selectedCard.Book.IsFavorite, _selectedCard.Book.ReadingStatus);
            if (LibraryDetailPane.IsVisible)
                UpdateBookReflectionPreview(_selectedBookReflection);
        }
        UpdateDeviceBookSelectionUi();
        UpdateDeviceBookPaginationUi();
        UpdateDeviceBookEmptyState();
        UpdateReaderAiLanguageText();
        ApplyReaderAiSettingsToControls();
        RefreshReaderTranslationLocalizedText();
        _ = ObserveReaderTaskAsync(
            RefreshReaderEmbeddingModelStatusAsync(_lifetimeCancellation.Token));
        UpdateCalibreDetectionStatus();
        UpdateDiagnosticsTexts();
        foreach (var diagnostic in PlatformDiagnostics)
            diagnostic.RefreshLocalizedProperties();
        if (!string.IsNullOrWhiteSpace(_pendingUpdateVersion))
        {
            var packageReady = TryGetPendingUpdatePackage(out _, out _);
            ShowUpdateBadge(
                _pendingUpdateVersion,
                _appSettings.PendingUpdateReleaseNotes,
                _appSettings.PendingUpdateReleaseNotesEnglish,
                packageReady);
            AboutUpdateStatusText.Text = packageReady
                ? T("更新包已下载，退出应用后安装 {0}", _pendingUpdateVersion)
                : T("发现新版本 {0}", _pendingUpdateVersion);
        }
        if (ReaderRoot.IsVisible)
        {
            UpdateReaderToolbar();
            UpdateReaderZenTocToggle();
            UpdateReaderLayoutSliderLabels();
            UpdateReaderTtsUi();
        }
        if (_stage3Ready && ReadingMaterialsPage.IsVisible && !_readingMaterialsDirty)
            ApplyReadingMaterialsFilter();
    }

    private void RefreshLocalizedFilterItems()
    {
        var readingStatusIndex = ReadingStatusFilterBox.SelectedIndex;
        var favoriteIndex = FavoritesFilterBox.SelectedIndex;
        var sortIndex = LibrarySortBox.SelectedIndex;
        _updatingFilterControls = true;
        try
        {
            ReadingStatusFilterBox.ItemsSource = new[] { T("待读"), T("阅读中"), T("已读") };
            FavoritesFilterBox.ItemsSource = new[] { T("收藏"), T("未收藏") };
            LibrarySortBox.ItemsSource = new[] { T("最近更新"), T("标题升序"), T("作者升序"), T("创建时间"), T("进度优先") };
            ReadingStatusFilterBox.SelectedIndex = readingStatusIndex;
            FavoritesFilterBox.SelectedIndex = favoriteIndex;
            LibrarySortBox.SelectedIndex = sortIndex;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private void RefreshLocalizedReadingMaterialsSourceFilter()
    {
        var selectedIndex = ReadingMaterialsSourceBox.SelectedIndex;
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 0, T("全部来源"));
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 1, T("本地书库"));
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 2, T("当前设备"));
        RestoreComboBoxSelection(ReadingMaterialsSourceBox, selectedIndex);
    }

    private static void SetComboBoxItemContent(ComboBox comboBox, int index, string content)
    {
        if (index >= 0 && index < comboBox.Items.Count && comboBox.Items[index] is ComboBoxItem item)
            item.Content = content;
    }

    private static void RestoreComboBoxSelection(ComboBox comboBox, int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= comboBox.Items.Count)
            return;

        comboBox.SelectedIndex = -1;
        comboBox.SelectedIndex = selectedIndex;
    }

    public ObservableCollection<BookCollectionFolderViewModel> CollectionFolders { get; } = [];
    public BulkObservableCollection<BookCollectionFolderViewModel> FilteredCollectionFolders { get; } = [];

    private enum LibraryViewMode
    {
        Grid,
        List,
        Collections
    }

    private async Task RunSendDiagnosticAsync()
    {
        try
        {
            await Task.Delay(1500);
            if (CurrentDevice is not { } device) return;
            foreach (var card in ViewModel.Books)
            {
                try
                {
                    using var prepared = await PrepareKindleTransferAsync(device, card.Book, null, CancellationToken.None);
                    Console.WriteLine($"[senddiag] prepare ok 《{card.Title}》 ({prepared.File.Format})");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[senddiag] PREPARE FAILED 《{card.Title}》: {exception.Message}");
                    LogSendDiagnostic($"Prepare 《{card.Title}》", exception);
                }
            }
            Console.WriteLine("[senddiag] prepare sweep done");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[senddiag] outer failure: {exception}");
        }
    }

    private Task? _libraryInitialization;
}
