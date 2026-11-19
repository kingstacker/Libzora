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
/// Stage 3 is intentionally kept in a separate partial file while the
/// Avalonia port is in progress. It owns page switching and presentation
/// state; device, backup, settings and network policy remain in Core and
/// Infrastructure.
/// </summary>
public partial class MainWindow
{
    private const int MaxReadingMaterials = 5_000;
    private const int DevicePageSize = 200;
    private readonly DispatcherTimer _stage3Timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private static readonly TimeSpan DeviceChangeCoalescingDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan DeviceChangeRetryDelay = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer _deviceChangeDebounceTimer = new() { Interval = DeviceChangeCoalescingDelay };
    private bool _deviceChangePending;
    private long _deviceChangeRetryUntil;
    private readonly DispatcherTimer _taskSpinnerTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private readonly DispatcherTimer _deviceStatusToastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _s3SyncTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _s3LocalChangeSyncTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan S3LocalChangeSyncDebounce = TimeSpan.FromSeconds(5);
    private IReadOnlyList<KindleDevice> _devices = [];
    private string? _lastDeviceIdentity;
    private string? _acceptedDeviceId;
    private string? _ignoredDeviceId;
    private string? _manuallyDisconnectedDeviceId;
    // Set after a send mutates the device library while the device page is
    // closed; OpenKindlePageAsync then rescans instead of showing the stale
    // list (its Count == 0 fast path would skip the rescan).
    private bool _deviceBooksDirty;
    private bool _deviceBooksLoaded;
    private bool _deviceBooksShowingCache;
    private bool _deviceBookScanFailed;
    private readonly Dictionary<KindleBookCardViewModel, (string Stamp, PinyinSearchIndex Index)> _devicePinyinIndexes = [];
    private bool _devicePinyinIndexBuilding;
    private IReadOnlyList<LibraryBookMatch> _libraryMatchRecords = [];
    private BookLibraryComparisonResult? _libraryPresenceComparison;
    private Task? _deviceWarmTask;
    private Task? _deviceBookScanTask;
    private CancellationTokenSource? _deviceBookScanCancellation;
    private long _deviceBookScanGeneration;
    private bool _isRefreshingDevices;
    private double _deviceUsedRatio;
    private Point? _deviceStatusToastPosition;
    private double? _deviceStatusToastPointerPosition;
    private TaskCompletionSource<bool>? _devicePromptCompletion;
    private bool _stage3Ready;
    private bool _deviceResourceBusy;
    private KindleResourceKind _deviceResourceKind = KindleResourceKind.Font;
    private static readonly HashSet<string> LocalDictionaryExtensions =
        new([".mdx", ".azw", ".mobi", ".prc", ".txt", ".tsv", ".csv"], StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string DeviceKey, KindleResourceKind Kind), IReadOnlyList<KindleDeviceResource>> _deviceResourceCache = [];
    private readonly Dictionary<string, IReadOnlyList<KindleClipping>> _deviceClippingCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _backupBusy;
    private bool _backupImportInProgress;
    private bool _dataRootMigrationInProgress;
    private bool _s3SyncBusy;
    private bool _s3SyncExitInProgress;
    private bool _allowWindowCloseForS3Sync;
    private TaskCompletionSource<bool>? _s3SyncCompletion;
    private CancellationTokenSource? _s3SyncCancellation;
    private CancellationTokenSource? _s3SyncExitCancellation;
    private CancellationTokenSource? _s3TestConnectionCancellation;
    private bool _s3DeletionConfirmationPending;
    private bool _s3SyncCancelledByUser;
    private volatile bool _s3RemoteSettingsChanged;
    private readonly LocalSyncChanges _localSyncChanges = new();
    private bool _suppressS3SettingsDraftTracking;
    private DateTimeOffset? _lastS3SyncAt;
    private S3SyncResult? _lastS3SyncResult;
    private ReadingPeriod _readingDashboardPeriod = ReadingPeriod.Week;
    private IReadOnlyList<ReadingDashboardDay> _readingDashboardDailyReading = [];
    private int _readingDashboardGeneration;
    private bool _readingMaterialsExportMode;
    private bool _readingMaterialsExportInProgress;
    private Popup? _readingMaterialPreviewPopup;
    private Border? _readingMaterialPreviewTarget;
    private bool _suppressMainAiProviderChange;
    private bool _suppressMainAiModelChange;
    private bool _deviceGridView = true;
    private KindleBookCardViewModel? _deviceMultiSelectAnchor;
    private bool _deviceRubberBandSelecting;
    private bool _deviceRubberBandPressedOnCard;
    private bool _deviceRubberBandPointerSequenceHandled;
    private Point _deviceRubberBandStart;
    private Point _deviceRubberBandStartContent;
    private Point _deviceRubberBandCurrent;
    private Point _deviceRubberBandPointerPosition;
    private readonly DispatcherTimer _deviceRubberBandAutoScrollTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private double _deviceRubberBandAutoScrollDelta;

    // Device-operation coordination (WinUI reference): every Kindle session
    // operation is tracked so eject can drain active work before requesting
    // the removal, and no new session is opened while eject is in progress.
    private readonly object _deviceOperationSync = new();
    private readonly HashSet<Task> _activeDeviceOperations = [];
    private bool _deviceEjectInProgress;
    private bool _isTransferring;
    // Held from the first check until the transfer's finally block. The
    // visible _isTransferring flag is set only after device refresh and
    // confirmation dialogs, which left a window for two sends to enter.
    private int _transferGate;
    private readonly SemaphoreSlim _deviceBookExportGate = new(1, 1);
    private CancellationTokenSource? _transferCancellation;
    private CancellationTokenSource? _transferPrefetchCancellation;
    private bool _transferStopRequested;
    // The generic progress popup uses this tick. Transfer progress lives in
    // the task-history flyout instead of a second floating surface.
    private bool _taskProgressPopupSpinnerActive;
    private double _taskProgressPopupSpinnerAngle;
    private RotateTransform? _taskProgressPopupSpinnerTransform;

    private sealed class CoalescingUiProgress<T> : IProgress<T>, IDisposable
    {
        private readonly Action<T> _apply;
        private readonly object _gate = new();
        private T? _latest;
        private bool _hasLatest;
        private bool _scheduled;
        private bool _disposed;

        public CoalescingUiProgress(Action<T> apply)
        {
            _apply = apply;
        }

        public void Report(T value)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _latest = value;
                _hasLatest = true;
                if (_scheduled) return;
                _scheduled = true;
            }

            Dispatcher.UIThread.Post(Deliver, DispatcherPriority.Background);
        }

        private void Deliver()
        {
            T latest;
            lock (_gate)
            {
                if (_disposed || !_hasLatest)
                {
                    _scheduled = false;
                    return;
                }

                latest = _latest!;
                _latest = default;
                _hasLatest = false;
                _scheduled = false;
            }

            _apply(latest);

            lock (_gate)
            {
                if (_disposed || !_hasLatest || _scheduled) return;
                _scheduled = true;
            }

            Dispatcher.UIThread.Post(Deliver, DispatcherPriority.Background);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _latest = default;
                _hasLatest = false;
                _scheduled = false;
            }
        }
    }

    // Application-settings real-time auto-save (600 ms debounce).
    private bool _suppressAppSettingsAutoSave;
    private bool _appSettingsAutoSaveConfigured;
    private bool _appSettingsAutoSaveShowStatus = true;
    private CancellationTokenSource? _appSettingsAutoSaveCancellation;
    private int _settingsCapsuleSequence;
    // Template application and first layout can make NumericUpDown/ComboBox
    // re-fire change events right after the auto-save handlers are attached.
    // Until startup has fully settled, those saves persist silently instead of
    // popping the “设置已保存” capsule.
    private bool _appSettingsStartupSettled;
    private bool _calibreSetupBusy;
    private CancellationTokenSource? _calibreDetectionCancellation;
    private bool _diagnosticsRepairBusy;
    private bool _diagnosticsRefreshing;

    public ObservableCollection<KindleBookCardViewModel> DeviceBooks { get; } = [];
    private readonly Dictionary<string, KindleBookCardViewModel> _deviceCardsByPath = new(StringComparer.OrdinalIgnoreCase);
    public BulkObservableCollection<KindleBookCardViewModel> VisibleDeviceBooks { get; } = [];
    private IReadOnlyList<KindleBookCardViewModel> _filteredDeviceBooks = [];
    private int _devicePageIndex;
    private int _devicePageCount = 1;
    public ObservableCollection<KindleDeviceResource> DeviceResources { get; } = [];
    public ObservableCollection<Stage3ReadingMaterialViewModel> ReadingMaterials { get; } = [];
    public ObservableCollection<Stage3ReadingMaterialGroupViewModel> ReadingMaterialGroups { get; } = [];
    public ObservableCollection<Stage3DashboardDayViewModel> DashboardDays { get; } = [];
    public ObservableCollection<Stage3DashboardBarViewModel> DashboardBookTimes { get; } = [];
    public ObservableCollection<Stage3DashboardBarViewModel> DashboardProgressBuckets { get; } = [];
    public ObservableCollection<Stage3DashboardRecentViewModel> DashboardRecentItems { get; } = [];
    public ObservableCollection<ManagedFont> ManagedFonts { get; } = [];
    public ObservableCollection<DictionaryDefinition> ManagedDictionaries { get; } = [];
    public ObservableCollection<LibraryTrashItemViewModel> LibraryTrashItems { get; } = [];
    public ObservableCollection<PlatformDiagnosticViewModel> PlatformDiagnostics { get; } = [];

    private readonly List<Stage3ReadingMaterialViewModel> _allStage3ReadingMaterials = [];
    private readonly Dictionary<string, string> _readingMaterialCoverPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(ReadingMaterialSource Source, string BookTitle), bool> _readingMaterialGroupStates =
        new(new ReadingMaterialGroupKeyComparer());
    private ObservableCollection<Stage3DashboardRecentViewModel> _readingDashboardItems => DashboardRecentItems;
    private bool _readingMaterialsDirty;
    private int _stage3IdleTicks;

    private KindleDevice? CurrentDevice => _devices.FirstOrDefault();

    private void ConfigureStage3Timer()
    {
        ConfigureDevicePollingInterval();
        _deviceChangeDebounceTimer.Tick += DeviceChangeDebounceTimer_Tick;
        _stage3Timer.Tick += async (_, _) =>
        {
            if (!_stage3Ready || _kindle is null || _deviceEjectInProgress) return;
            // Native notifications use a fast coalesced/retry path; retain the
            // quiet 6s/15s fallback rather than opening COM/MTP every 500ms.
            // Without a notifier (Linux, or failed native registration), use
            // the 500ms timer on every page so a ready mount need not wait 15s.
            if (_deviceChangeNotifier is not null && !DevicePage.IsVisible)
            {
                var idleTickDivisor = OperatingSystem.IsMacOS() ? 2 : 5;
                if (++_stage3IdleTicks % idleTickDivisor != 0) return;
            }
            else
            {
                _stage3IdleTicks = 0;
            }
            await RefreshDevicesAsync(scanBooks: DevicePage.IsVisible);
        };
        _stage3Timer.Start();
        _taskSpinnerTimer.Tick += (_, _) => TickTaskSpinner();
        _deviceStatusToastTimer.Tick += (_, _) =>
        {
            _deviceStatusToastTimer.Stop();
            HideDeviceStatusToast();
        };
        _s3SyncTimer.Tick += async (_, _) => await RunAutomaticS3SyncIfDueAsync();
        _s3LocalChangeSyncTimer.Tick += async (_, _) => await RunPendingS3LocalChangeSyncAsync();
        _s3SyncTimer.Start();
    }

    // Keep existing progress producers synchronized with title-bar task history.
    private void ShowTaskProgressPopup(bool recordTask = true)
    {
        if (recordTask) BeginGenericTaskRecord();
        // Transfer progress is rendered in the task-history flyout. Do not
        // introduce a second floating progress surface while it is active.
        if (_transferTaskCompletion is not null) return;

        StartTaskSpinner(TaskProgressPopupSpinnerIcon);
        TaskProgressPopup.IsVisible = false;
        TaskProgressPopup.Opacity = 1;
    }

    private void HideTaskProgressPopup()
    {
        EndGenericTaskRecord();
        StopTaskSpinnerFor(TaskProgressPopupSpinnerIcon);
        FadeOutPopup(TaskProgressPopup);
    }

    private void ShowTransferToast(
        string title,
        string message,
        double? progress = null,
        bool isIndeterminate = false,
        bool autoHide = false,
        bool completed = false)
    {
        var transferActive = !completed && !autoHide;
        UpdateTransferTaskRecord(title, message, progress, isIndeterminate, autoHide, completed);
        if (transferActive)
        {
            StopTaskSpinnerFor(TaskProgressPopupSpinnerIcon);
            TaskProgressPopup.IsVisible = false;
            TaskProgressPopup.Opacity = 0;
            return;
        }

        var capsuleMessage = completed ? T("任务完成：{0}", message) : message;
        ShowSettingsCapsule(capsuleMessage, completed ? 2400 : 3600, success: completed);
    }

    private void HideTransferToast()
    {
        EndTransferTaskRecord();
    }

    private void StartTaskSpinner(Control icon)
    {
        if (ReferenceEquals(icon, TaskProgressPopupSpinnerIcon))
        {
            _taskProgressPopupSpinnerTransform ??= new RotateTransform(0);
            if (!_taskProgressPopupSpinnerActive)
            {
                _taskProgressPopupSpinnerActive = true;
                _taskProgressPopupSpinnerAngle = 0;
                _taskProgressPopupSpinnerTransform.Angle = 0;
            }
            icon.RenderTransform = _taskProgressPopupSpinnerTransform;
        }
        else
        {
            return;
        }

        // Reassert visibility when a toast/popup transition temporarily hid
        // the icon. This does not reset its angle or task ownership.
        icon.IsVisible = true;
        _taskSpinnerTimer.Start();
    }

    private void StopTaskSpinnerFor(Control icon)
    {
        if (ReferenceEquals(icon, TaskProgressPopupSpinnerIcon))
        {
            _taskProgressPopupSpinnerActive = false;
            _taskProgressPopupSpinnerAngle = 0;
        }
        else
        {
            return;
        }

        _taskProgressPopupSpinnerTransform ??= new RotateTransform(0);
        _taskProgressPopupSpinnerTransform.Angle = 0;
        icon.RenderTransform = _taskProgressPopupSpinnerTransform;
        icon.IsVisible = false;
        if (!_taskProgressPopupSpinnerActive)
            _taskSpinnerTimer.Stop();
    }

    private void StopTaskSpinner()
    {
        _taskSpinnerTimer.Stop();
        _taskProgressPopupSpinnerActive = false;
        _taskProgressPopupSpinnerAngle = 0;
        _taskProgressPopupSpinnerTransform ??= new RotateTransform(0);
        _taskProgressPopupSpinnerTransform.Angle = 0;
        TaskProgressPopupSpinnerIcon.RenderTransform = _taskProgressPopupSpinnerTransform;
        TaskProgressPopupSpinnerIcon.IsVisible = false;
    }

    private void TickTaskSpinner()
    {
        if (!_taskProgressPopupSpinnerActive)
        {
            _taskSpinnerTimer.Stop();
            return;
        }

        if (_taskProgressPopupSpinnerActive)
        {
            TaskProgressPopupSpinnerIcon.IsVisible = true;
            _taskProgressPopupSpinnerAngle = (_taskProgressPopupSpinnerAngle + 18) % 360;
            _taskProgressPopupSpinnerTransform ??= new RotateTransform(0);
            _taskProgressPopupSpinnerTransform.Angle = _taskProgressPopupSpinnerAngle;
            TaskProgressPopupSpinnerIcon.RenderTransform = _taskProgressPopupSpinnerTransform;
        }
    }

    private void RequestTransferStop()
    {
        if (!_isTransferring) return;
        _transferStopRequested = true;
        ReportTransferTaskStage(T("当前书籍发送完成后将停止…"));
        SetTaskStatus(T("当前书籍发送完成后将停止发送。"));
        _transferPrefetchCancellation?.Cancel();
    }

    private async void FadeOutPopup(Control popup)
    {
        popup.Opacity = 0;
        await Task.Delay(250);
        if (popup.Opacity < 0.5) popup.IsVisible = false;
    }

    // Device feedback bubble uses the task-history flyout's shape and spacing.
    // It floats in window coordinates and follows the eject button.
    private void ShowDeviceStatusToast(string message)
    {
        DeviceStatusToastText.Text = message;
        DeviceStatusToast.IsVisible = true;
        PositionDeviceStatusToast();
        DeviceStatusToast.Opacity = 1;
        _deviceStatusToastTimer.Stop();
        _deviceStatusToastTimer.Start();
    }

    private void PositionDeviceStatusToast()
    {
        if (DeviceStatusEjectButton is not { } anchor) return;
        DeviceStatusToast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var popupSize = DeviceStatusToast.DesiredSize;
        PositionDeviceStatusToast(popupSize);
    }

    private void PositionDeviceStatusToast(Size popupSize)
    {
        if (DeviceStatusEjectButton is not { } anchor
            || popupSize.Width <= 0
            || popupSize.Height <= 0)
            return;
        // Keep the pointer centered on the eject icon, like the task-history
        // pointer is centered on its 24-DIP button.
        var apex = anchor.TranslatePoint(
            new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2 - 4),
            this);
        var anchorOrigin = anchor.TranslatePoint(new Point(0, 0), this);
        if (apex is not { } apexPoint || anchorOrigin is not { } anchorOriginPoint) return;

        const double edgeMargin = 8;
        const double pointerCenterFromLeft = 12.5;
        const double pointerWidth = 16;
        const double taskHistoryVerticalOffset = 4;
        const double pointerTipInset = 0.5;
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
        var maxLeft = Math.Max(edgeMargin, windowWidth - popupSize.Width - edgeMargin);
        var popupLeft = Math.Clamp(apexPoint.X - pointerCenterFromLeft, edgeMargin, maxLeft);
        var popupTop = Math.Max(
            edgeMargin,
            anchorOriginPoint.Y + taskHistoryVerticalOffset - popupSize.Height + pointerTipInset);
        var pointerLeft = Math.Clamp(
            apexPoint.X - popupLeft - pointerWidth / 2,
            0,
            Math.Max(0, popupSize.Width - pointerWidth));

        if (_deviceStatusToastPosition != new Point(popupLeft, popupTop))
        {
            _deviceStatusToastPosition = new Point(popupLeft, popupTop);
            DeviceStatusToast.RenderTransform = new TranslateTransform(popupLeft, popupTop);
        }
        // Draw the rectangle and pointer as one contour, with no seam across the base.
        var right = popupSize.Width - 0.5;
        var bottom = popupSize.Height - 8.5;
        var pointerTip = popupSize.Height - 0.5;
        var pointerStart = Math.Clamp(pointerLeft, 1, Math.Max(1, right - pointerWidth - 1));
        var outline = FormattableString.Invariant(
            $"M 0.5,0.5 H {right} V {bottom} H {pointerStart + pointerWidth} L {pointerStart + pointerWidth / 2},{pointerTip} L {pointerStart},{bottom} H 0.5 Z");
        if (!string.Equals(DeviceStatusToastOutline.Tag as string, outline, StringComparison.Ordinal))
        {
            DeviceStatusToastOutline.Tag = outline;
            DeviceStatusToastOutline.Data = Geometry.Parse(outline);
        }
        _deviceStatusToastPointerPosition = pointerLeft;
    }

    // Re-anchor the bubble while it is visible so it follows the eject button
    // when the window moves or is resized (WinUI LayoutUpdated behaviour).
    //
    // Layout callbacks must NOT call Measure: measuring during a layout pass
    // invalidates layout again, so LayoutUpdated fires again, and the toast
    // loops forever ("Infinite layout loop detected" — the device-connect
    // bubble crashed the app exactly this way). Use the already-laid-out
    // Bounds instead, and move the toast with RenderTransform so its position
    // never participates in the layout pass.
    private void DeviceStatusEjectButton_LayoutUpdated(object? sender, EventArgs e)
    {
        if (!DeviceStatusToast.IsVisible) return;
        var popupSize = DeviceStatusToast.Bounds.Size;
        PositionDeviceStatusToast(popupSize);
    }

    private void HideDeviceStatusToast()
    {
        _deviceStatusToastTimer.Stop();
        DeviceStatusToast.Opacity = 0;
        _ = Task.Delay(350).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                if (DeviceStatusToast.Opacity < 0.5) DeviceStatusToast.IsVisible = false;
            }),
            TaskScheduler.Default);
    }

    // Device-operation coordination (WinUI reference MainWindow.xaml.cs):
    // eject drains active operations and refuses new ones while in progress.
    private bool HasActiveDeviceOperations
    {
        get
        {
            lock (_deviceOperationSync) return _activeDeviceOperations.Count > 0;
        }
    }

    private async Task TrackDeviceOperationAsync(Func<Task> operation)
    {
        if (_deviceEjectInProgress) return;
        var task = operation();
        lock (_deviceOperationSync) _activeDeviceOperations.Add(task);
        UpdateKindleAnnotationSyncButtonVisibility();
        try
        {
            await task;
        }
        finally
        {
            lock (_deviceOperationSync) _activeDeviceOperations.Remove(task);
            UpdateKindleAnnotationSyncButtonVisibility();
        }
    }

    private async Task WaitForActiveDeviceOperationsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_deviceOperationSync) tasks = _activeDeviceOperations.ToArray();
            if (tasks.Length == 0) return;
            await Task.WhenAll(tasks);
        }
    }

    private async Task InitializeStage3Async(CancellationToken cancellationToken)
    {
        if (_stage3Ready) return;

        SetTaskStatus(T("正在加载云端同步设置…"));
        _s3SyncStoredSettings = await _s3SyncService.LoadSettingsAsync(cancellationToken);
        SetTaskStatus(T("正在初始化阅读数据…"));
        await _readerData.InitializeAsync(cancellationToken);
        await _readerProgressRecovery.InitializeAsync(cancellationToken);
        await _readerReadingTimeRecovery.InitializeAsync(cancellationToken);
        SetTaskStatus(T("正在初始化同步记录…"));
        await _s3SyncService.InitializeDeletionTrackingAsync(cancellationToken, _s3SyncStoredSettings.DeviceId);
        SetTaskStatus(T("正在初始化设备设置…"));
        await _deviceModelStore.InitializeAsync(cancellationToken);
        SetTaskStatus(T("正在读取应用设置…"));
        _appSettings = await _appSettingsStore.LoadAsync(cancellationToken);
        ViewModel.SortMode = ParseLibrarySortMode(_appSettings.LibrarySortMode);
        SetLibraryViewMode(ParseLibraryViewMode(_appSettings.LibraryViewMode), persist: false);
        RestoreDeviceShelfState();
        ApplyMainAppearance();
        ApplyReaderAppearance();
        SyncReaderAppearanceControls();
        if (Application.Current is App app)
            app.ApplyLanguage(_appSettings.UiLanguage);
        RefreshLocalizedReadingMaterialsSourceFilter();
        LoadReaderVerticalDebugBoxesSetting();
        SetTaskStatus(T("正在检查 Calibre…"));
        await DetectCalibreAtStartupAsync(cancellationToken);
        SetTaskStatus(T("正在加载发送设置…"));
        _kindleEmailSettings = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        RefreshS3SyncIndicatorFromSettings();
        PopulateSettingsControls();
        ConfigureAppSettingsAutoSave();
        SetTaskStatus(T("正在加载本地资源…"));
        await RefreshManagedResourcesAsync(cancellationToken);
        SetTaskStatus(T("正在检查设备…"));
        DeviceStorageText.Text = T("正在读取存储信息…");
        DevicePageDeviceText.Text = T("正在检查设备…");
        DevicePageStatusText.Text = T("正在读取设备连接状态。");
        _stage3Ready = true;
        if (_deviceChangePending) _deviceChangeDebounceTimer.Start();
        // Device discovery can proceed while an automatic backup is prepared.
        var initialDeviceRefresh = RefreshDevicesAsync(scanBooks: false, cancellationToken);
        await RunAutoBackupIfNeededAsync(cancellationToken);
        if (_s3SyncStoredSettings.Settings.Enabled
            && _s3SyncStoredSettings.Settings.AutomaticSyncEnabled
            && _s3SyncStoredSettings.Settings.IsConfigured
            && _appSettings.NetworkEnabled)
        {
            _ = Dispatcher.UIThread.InvokeAsync(() => RunLifecycleS3SyncAsync());
        }
        await initialDeviceRefresh;

        // Startup layout can raise harmless change events. Equality checks in
        // the scheduler filter those without cancelling real user edits.
        await Dispatcher.UIThread.InvokeAsync(() => { });
        _appSettingsStartupSettled = true;
    }

    private void ShowLibraryPage()
    {
        if (BlockNavigationWhileTransferring()) return;
        if (_readingMaterialsExportMode) SetReadingMaterialsExportMode(false);
        ClearBookMultiSelection();
        if (DevicePage.IsVisible)
            ClearDeviceBookSelection();
        WindowBrandText.IsVisible = false;
        SetSidebarActive(AllBooksButton);
        FadeInPage(LibraryWorkspace);
        LibraryDetailPane.IsVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        DevicePage.IsVisible = false;
        DeviceResourcePage.IsVisible = false;
        ReadingMaterialsPage.IsVisible = false;
        ReadingDashboardPage.IsVisible = false;
        SettingsPage.IsVisible = false;
        AccountCenterHost.IsVisible = false;
        _accountCenterView?.Deactivate();    }

    private void ShowStage3Page(Control page, Button? activeButton = null)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (_readingMaterialsExportMode && !ReferenceEquals(page, ReadingMaterialsPage))
            SetReadingMaterialsExportMode(false);
        ClearBookMultiSelection();
        if (DevicePage.IsVisible && !ReferenceEquals(page, DevicePage))
            ClearDeviceBookSelection();
        WindowBrandText.IsVisible = false;
        activeButton ??= page switch
        {
            _ when ReferenceEquals(page, DevicePage) => KindleBooksButton,
            _ when ReferenceEquals(page, DeviceResourcePage) =>
                _deviceResourceKind == KindleResourceKind.Font ? FontManagementButton : DictionaryManagementButton,
            _ when ReferenceEquals(page, ReadingMaterialsPage) =>
                ReaderNotesNavigationButton,
            _ when ReferenceEquals(page, ReadingDashboardPage) => ReadingDashboardButton,
            _ when ReferenceEquals(page, AccountCenterHost) => AccountNavigationButton,
            _ => SettingsNavigationButton
        };
        SetSidebarActive(activeButton);
        LibraryWorkspace.IsVisible = false;
        LibraryDetailPane.IsVisible = false;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        FadeInPage(page);
        DevicePage.IsVisible = ReferenceEquals(page, DevicePage);
        DeviceResourcePage.IsVisible = ReferenceEquals(page, DeviceResourcePage);
        ReadingMaterialsPage.IsVisible = ReferenceEquals(page, ReadingMaterialsPage);
        ReadingDashboardPage.IsVisible = ReferenceEquals(page, ReadingDashboardPage);
        SettingsPage.IsVisible = ReferenceEquals(page, SettingsPage);
        AccountCenterHost.IsVisible = ReferenceEquals(page, AccountCenterHost);
        if (!AccountCenterHost.IsVisible) _accountCenterView?.Deactivate();    }

    private bool BlockNavigationWhileTransferring()
    {
        if (!_isTransferring) return false;
        SetTaskStatus(T("正在发送书籍，完成或停止后才能切换功能区。"));
        ReportTransferTaskStage(T("正在发送，请完成或停止任务后再切换功能区。"));
        return true;
    }

    private void SetTransferNavigationEnabled(bool enabled)
    {
        Button[] navigationButtons =
        [
            AllBooksButton,
            KindleBooksButton,
            FontManagementButton,
            DictionaryManagementButton,
            ReaderNotesNavigationButton,
            ReadingDashboardButton,
            AccountNavigationButton,
            SettingsNavigationButton
        ];
        foreach (var button in navigationButtons)
            button.IsEnabled = enabled;
    }

    private void SetSidebarActive(Button activeButton)
    {
        Button[] buttons =
        [
            AllBooksButton,
            KindleBooksButton,
            FontManagementButton,
            DictionaryManagementButton,
            ReaderNotesNavigationButton,
            ReadingDashboardButton,
            AccountNavigationButton,
            SettingsNavigationButton
        ];
        foreach (var button in buttons)
            button.Classes.Remove("active");
        activeButton.Classes.Add("active");

        _activeNavigationSectionButton = activeButton switch
        {
            _ when ReferenceEquals(activeButton, FontManagementButton)
                || ReferenceEquals(activeButton, DictionaryManagementButton) => DeviceManagementSectionButton,
            _ when ReferenceEquals(activeButton, ReaderNotesNavigationButton)
                || ReferenceEquals(activeButton, ReadingDashboardButton) => ReadingSectionButton,
            _ when ReferenceEquals(activeButton, SettingsNavigationButton)
                || ReferenceEquals(activeButton, AccountNavigationButton) => null,
            _ => BookManagementSectionButton
        };
        switch (_activeNavigationSectionButton)
        {
            case var section when ReferenceEquals(section, BookManagementSectionButton):
                BookManagementChildren.IsVisible = true;
                break;
            case var section when ReferenceEquals(section, DeviceManagementSectionButton):
                DeviceManagementChildren.IsVisible = true;
                break;
            case var section when ReferenceEquals(section, ReadingSectionButton):
                ReadingChildren.IsVisible = true;
                break;
        }
        UpdateSidebarSectionVisuals();
    }
}
