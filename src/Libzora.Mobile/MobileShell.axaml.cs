using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;
using Libzora.Mobile.Controls;
using Libzora.Mobile.Localization;

namespace Libzora.Mobile;

/// <summary>
/// Single-view mobile shell: a content slot plus four tab roots. Book detail
/// and the reader are pushed on top of the active tab and hide the tab bar.
/// Transient choices open in one shared bottom sheet. Android's back gesture is
/// routed through <see cref="TopLevel.BackRequested"/>.
/// </summary>
public partial class MobileShell : UserControl
{
    private enum ShellTab
    {
        Library,
        Materials,
        ReadingData,
        Profile
    }

    private const string PageBackgroundKey = "PageBackgroundBrush";

    /// <summary>Quiet periods that keep routine app events from syncing constantly.</summary>
    public static class AutoSyncGap
    {
        public static readonly TimeSpan OnStart = TimeSpan.Zero;
        public static readonly TimeSpan OnResume = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan AfterReading = TimeSpan.Zero;
    }

    private static readonly TimeSpan SheetCloseDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(2);

    private readonly ApplicationModules _modules;
    private readonly MobileServices _services;
    private readonly Stack<(Control Page, ShellTab Tab)> _history = new();
    private ShellTab _tab = ShellTab.Library;
    private Task? _readerDataReady;
    private int _sheetVersion;
    private int _toastVersion;
    private Action? _sheetDismissed;
    private bool _sheetFillsHeight;
    private bool _backupBlocked;
    private DispatcherTimer? _sheetControlTimer;

    /// <summary>Design-time only: keeps the XAML previewer and runtime loader working.</summary>
    public MobileShell()
    {
        _modules = null!;
        _services = null!;
        Preferences = null!;
        Covers = null!;
        Sync = null!;
        InitializeComponent();
    }

    public MobileShell(ApplicationModules modules, MobileServices services, MobilePreferencesStore preferences)
    {
        _modules = modules;
        _services = services with { Paths = modules.Paths };
        Preferences = preferences;
        if (services.PreferredLanguages is { } preferredLanguages)
            UiLanguage = new MobileUiLanguageService(preferences, preferredLanguages);
        Covers = new CoverCache(modules.Paths);
        Sync = new MobileSync(modules);
        InitializeComponent();
        SheetPanel.SizeChanged += (_, _) =>
        {
            if (SheetScope.IsVisible && SheetPanel.Classes.Contains("open"))
                Dispatcher.UIThread.Post(() =>
                {
                    if (SheetScope.IsVisible && SheetPanel.Classes.Contains("open"))
                    {
                        SetPageNativeControlsObscured(true, checkCoverage: true);
                        UpdateSheetNativeControlClipping();
                    }
                }, DispatcherPriority.Render);
        };
        Loaded += (_, _) => StartDeviceChecks();
        if (_services.TabBar is { } tabBar)
        {
            // The platform bar is the visible one. This one only keeps the grid
            // row that reserves its space.
            tabBar.TabSelected += OnNativeTabSelected;
            TabBarGlass.Opacity = 0;
            TabBarGlass.IsHitTestVisible = false;
        }
        ActualThemeVariantChanged += (_, _) => SyncSystemBars();
        ApplyThemeMode();
        if (MobileDataCoordinator.IsRestartRequired)
            ShowRestartRequired(MobileDataCoordinator.IsRecoveryIncomplete);
        else
            ShowLibrary();
    }

    public MobilePreferencesStore Preferences { get; }

    public MobileUiLanguageService? UiLanguage { get; }

    public MobileServices Services => _services;

    /// <summary>Shared services for platform frontends that own their own view hierarchy.</summary>
    public ApplicationModules Modules => _modules;

    public Func<string, string, string, Task<bool>>? NativeConfirmation { get; set; }
    public Action<string>? NativeToast { get; set; }
    public Action? NativeRestartRequired { get; set; }
    public Action? NativeLibraryChanged { get; set; }
    private bool _deviceChecksStarted;

    public void StartDeviceChecks()
    {
        if (_deviceChecksStarted) return;
        _deviceChecksStarted = true;
        _modules.Accounts.Service.DeviceInvalidated += (_, args) => Dispatcher.UIThread.Post(async () =>
        {
            if (_modules.IsDisposed) return;
            await ConfirmAsync("设备已失效", args.Message, "知道了");
        });
        _modules.Accounts.Monitor.MembershipExpired += (_, args) => Dispatcher.UIThread.Post(async () =>
        {
            if (_modules.IsDisposed) return;
            await ConfirmAsync("会员已过期", args.Message, "知道了");
        });
        _modules.Accounts.Monitor.MembershipExpiring += (_, args) => Dispatcher.UIThread.Post(async () =>
        {
            if (_modules.IsDisposed) return;
            await ConfirmAsync("会员即将到期", args.Message, "知道了");
        });
        _modules.Accounts.Monitor.Start();
    }

    public async Task CheckDevicesIfDueAsync()
    {
        try { await _modules.Accounts.Monitor.CheckIfDueAsync(); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { }
    }

    /// <summary>
    /// Developer mode holds features outside the normal product.
    /// Like the desktop app it lasts for this run only.
    /// </summary>
    public bool IsDeveloperMode { get; private set; }

    public event EventHandler? DeveloperModeChanged;

    public void ToggleDeveloperMode()
    {
        if (IsDeveloperMode)
        {
            SetDeveloperMode(false);
            return;
        }
        ShowSheet("开发者模式", new PromptSheet(null, "进入", password =>
        {
            if (password != BookDetailEditing.DeveloperPassword)
            {
                ShowToast("密码错误");
                return Task.CompletedTask;
            }
            HideSheet();
            SetDeveloperMode(true);
            return Task.CompletedTask;
        }, placeholder: "请输入密码", isPassword: true));
    }

    private void SetDeveloperMode(bool enabled)
    {
        IsDeveloperMode = enabled;
        DeveloperModeChanged?.Invoke(this, EventArgs.Empty);
        ShowToast(enabled ? "已进入开发者模式" : "已退出开发者模式");
    }

    public bool TryToggleDeveloperMode(string? password)
    {
        if (!IsDeveloperMode && password != BookDetailEditing.DeveloperPassword) return false;
        SetDeveloperMode(!IsDeveloperMode);
        return true;
    }

    public CoverCache Covers { get; }

    public MobileSync Sync { get; }

    public Task ExportBackupAsync(string path, CancellationToken token = default) =>
        RunBackupAsync(path, restore: false, token);

    public Task RestoreBackupAsync(string path, CancellationToken token = default) =>
        RunBackupAsync(path, restore: true, token);

    public Task EnsureBackupEncryptionAsync(CancellationToken token = default) =>
        new AppBackupService(_services.Paths, _services.SecretProtector).EnsureEncryptionConfiguredAsync(token);

    private async Task RunBackupAsync(string path, bool restore, CancellationToken token)
    {
        await EnsureBackupEncryptionAsync(token);
        if (_backupBlocked) throw new InvalidOperationException("备份或恢复正在进行，请稍候。");
        _backupBlocked = true;
        BackupBlockerTitle.Text = restore ? "正在恢复备份" : "正在导出备份";
        BackupBlockerText.Text = "正在等待进行中的数据操作完成，请保持应用打开。";
        BackupBlockerProgress.Value = 0;
        BackupBlockerProgress.IsVisible = true;
        BackupBlocker.IsVisible = true;
        SetNativeControlsBlocked(true);
        _services.TabBar?.Update(false, (int)_tab);
        // Reported from a background thread; Progress<T> hops back to the UI one.
        var progress = new Progress<AppBackupProgress>(update =>
        {
            BackupBlockerProgress.Value = Math.Clamp(update.Fraction, 0, 1);
            if (!string.IsNullOrWhiteSpace(update.Message)) BackupBlockerText.Text = update.Message;
        });
        try
        {
            using var lease = await MobileDataCoordinator.BeginRestoreAsync(token)
                ?? throw new InvalidOperationException("数据操作暂不可用，请重新启动应用。");
            var backup = new AppBackupService(_services.Paths, _services.SecretProtector);
            if (restore)
            {
                try
                {
                    await backup.ImportAsync(path, token, progress);
                }
                catch (AppBackupRollbackException)
                {
                    // A failed rollback leaves a recovery journal for startup. Do not
                    // allow this process to write into the partially restored library.
                    lease.Complete(recoveryIncomplete: true);
                    ShowRestartRequired(recoveryIncomplete: true);
                    throw;
                }
                lease.Complete();
                ShowRestartRequired();
            }
            else
                await backup.ExportAsync(path, token, progress);
        }
        finally
        {
            if (!MobileDataCoordinator.IsRestartRequired)
            {
                _backupBlocked = false;
                BackupBlocker.IsVisible = false;
                SetNativeControlsBlocked(false);
                _services.TabBar?.Update(_history.Count == 1 && !SheetScope.IsVisible, (int)_tab);
            }
        }
    }

    /// <summary>
    /// The backup blocker is an opaque Avalonia border, but a hosted UIKit
    /// segment is a platform view above the whole scene and would keep showing
    /// through it, so the native controls are swapped for the fallback while
    /// the blocker is up.
    /// </summary>
    private void SetNativeControlsBlocked(bool blocked)
    {
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeMenuButton>())
            group.SetObscured(blocked);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeActionButton>())
            group.SetObscured(blocked);
        if (blocked)
        {
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeMenuButton>())
                group.SetObscured(true);
            foreach (var group in PageHost.GetVisualDescendants().OfType<NativeActionButton>())
                group.SetObscured(true);
        }
        else
        {
            SetPageNativeControlsObscured(SheetScope.IsVisible);
        }
    }

    private void ShowRestartRequired(bool recoveryIncomplete = false)
    {
        NativeRestartRequired?.Invoke();
        _backupBlocked = true;
        BackupBlockerTitle.Text = recoveryIncomplete ? "恢复未完成" : "需要重新启动";
        BackupBlockerText.Text = recoveryIncomplete
            ? "恢复失败，自动回滚未完成。请完全关闭应用并重新打开，让应用尝试恢复原数据。重新启动前无法继续使用或同步。"
            : "请完全关闭应用后重新打开，以载入恢复的数据。重新启动前无法继续使用或同步。";
        BackupBlockerProgress.IsVisible = false;
        BackupBlocker.IsVisible = true;
        SetNativeControlsBlocked(true);
        _services.TabBar?.Update(false, (int)_tab);
    }
}
