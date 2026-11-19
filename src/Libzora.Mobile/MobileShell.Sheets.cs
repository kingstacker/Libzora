using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using Libzora.Mobile.Views.Sheets;
using Libzora.Mobile.Controls;
using Libzora.Mobile.Localization;

namespace Libzora.Mobile;

/// <summary>
/// Sheet presentation, theme and toasts.
/// </summary>
public partial class MobileShell
{
    public Control? CurrentPage => _history.Count > 0 ? _history.Peek().Page : null;

    public bool CanGoBack => SheetScope.IsVisible || _history.Count > 1;

    /// <summary>
    /// Reader tables (progress, bookmarks, annotations) are created lazily the
    /// first time a page needs them; the desktop shell does this during startup.
    /// </summary>
    public Task EnsureReaderDataAsync()
    {
        // A failed attempt (locked database, full disk) is retried on the next
        // open instead of failing every reader for the rest of the session.
        if (_readerDataReady is { IsFaulted: true } or { IsCanceled: true }) _readerDataReady = null;
        return _readerDataReady ??= _modules.Reader.Data.InitializeAsync();
    }

    public void ShowLibrary() => ShowRoot(ShellTab.Library, new LibraryPage(_modules, _services, this));

    public void ShowMaterials() => ShowRoot(ShellTab.Materials, new MaterialsPage(_modules, this));

    public void ShowProfile() => ShowRoot(ShellTab.Profile, new ProfilePage(_modules, this));

    public void OpenBook(Book book) => Push(new BookDetailPage(book, _modules, this));

    public void OpenCollection(BookCollection collection) => Push(new CollectionPage(collection, _modules, this));

    public void OpenReader(Book book, ReaderProgressRow? location = null) =>
        Push(new ReaderPage(book, _modules, this, location));

    /// <summary>The control shown in the open sheet, for pages that update it later.</summary>
    public Control? CurrentSheet => SheetScope.IsVisible ? SheetBody.Content as Control : null;

    public void OpenBookEditor(Book book) => Push(new BookEditPage(book, _modules, this));

    public void ShowReadingData() => ShowRoot(ShellTab.ReadingData, new ReadingDataPage(_modules, this));

    public void OpenSyncSettings() => Push(new SyncSettingsPage(_modules, this));

    public void OpenAccount() => Push(new AccountPage(_modules.Accounts.Service, this, _modules.Accounts.Membership,
        AccountAvailability.RegistrationAvailable));

    public void OpenAiSettings() => Push(new AiSettingsPage(_modules, this));

    public void OpenKindleSettings() => Push(new KindleSettingsPage(_modules, this));

    /// <summary>
    /// Runs a silent sync when it is due. Deletions always wait for the user on
    /// the sync page, because a background pass must never approve them.
    /// </summary>
    public async void RequestAutoSync(TimeSpan minimumGap)
    {
        if (_backupBlocked || DateTimeOffset.UtcNow - Sync.LastRunAt < minimumGap) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules.Library.Books.InitializeAsync();
            await EnsureReaderDataAsync();
            var stored = await _modules.Sync.Service.LoadSettingsAsync();
            if (stored.Settings is not { Enabled: true, AutomaticSyncEnabled: true, IsConfigured: true }) return;
            var result = await Sync.RunAsync(stored, null, (_, _) => Task.FromResult(false), CancellationToken.None, operation);
            if (result is null) ShowToast("同步发现待确认的删除，请到 S3 同步页处理");
            else
            {
                if (result.Changed || Preferences.Current.ShowBookSyncStatus) NativeLibraryChanged?.Invoke();
                if (result.Changed && CurrentPage is LibraryPage library) await library.ReloadAsync();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Offline phones are normal; the sync page reports errors on request.
        }
    }

    public void OnAppResumed()
    {
        UiLanguage?.Refresh();
        if (_backupBlocked) return;
        (CurrentPage as ReaderPage)?.Resume();
        RequestAutoSync(AutoSyncGap.OnResume);
    }

    public void OnAppPaused()
    {
        if (_backupBlocked || CurrentPage is not ReaderPage reader) return;
        if (_services.BackgroundWork is { } background)
            background.Run(reader.PauseAsync);
        else
            _ = reader.PauseAsync();
    }

    public void ApplyThemeMode()
    {
        if (Application.Current is not { } application) return;
        application.RequestedThemeVariant = Preferences.Current.ThemeMode switch
        {
            MobileThemeMode.Light => ThemeVariant.Light,
            MobileThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        Dispatcher.UIThread.Post(SyncSystemBars);
    }

    /// <summary>
    /// Applies an appearance reported by the platform while the user's choice is
    /// "follow the system". Platforms whose SDK no longer notifies Avalonia
    /// directly call this from their own trait observer. Must run on the UI thread.
    /// </summary>
    public void ApplySystemThemeVariant(ThemeVariant variant)
    {
        if (Preferences.Current.ThemeMode != MobileThemeMode.System) return;
        if (Application.Current is not { } application) return;
        if (application.RequestedThemeVariant == variant) return;
        application.RequestedThemeVariant = variant;
    }

    public void ShowSheet(
        string title,
        Control content,
        ThemeVariant? variant = null,
        Action? onDismissed = null)
    {
        if (_backupBlocked)
        {
            onDismissed?.Invoke();
            return;
        }
        NotifySheetDismissed();
        _sheetDismissed = onDismissed;
        var version = ++_sheetVersion;
        SheetTitle.Text = title;
        SheetScrim.Classes.Set("reader-preview", content is ReaderSettingsSheet);
        SheetBody.Content = content;
        SheetScope.RequestedThemeVariant = variant ?? ThemeVariant.Default;
        SheetPanel.MaxHeight = content is ReaderSettingsSheet
            ? Math.Max(200, Bounds.Height * 0.45)
            : Math.Max(320, Bounds.Height * 0.82);
        _sheetFillsHeight = content is IFullHeightSheet { FillsHeight: true };
        SheetPanel.Height = _sheetFillsHeight ? SheetPanel.MaxHeight : double.NaN;
        // The shell is inset by the safe area; the scrim and sheet reach the screen edges.
        var safe = Padding;
        SheetScope.Margin = new Thickness(-safe.Left, -safe.Top, -safe.Right, -safe.Bottom);
        SheetPanel.Padding = new Thickness(0, 0, 0, safe.Bottom);
        if (_services.TabBar is not ISheetOccludingTabBar || _history.Count != 1)
            _services.TabBar?.Update(false, (int)_tab);
        SetPageNativeControlsObscured(true);
        SheetScope.IsVisible = true;
        StartSheetControlTracking();
        AnimateSheetOpenSoon(version);
        RevealSheetCoverageSoon(version);
        RefreshVirtualizedListsAfterOpen(version);
    }

    /// <summary>
    /// Adds the open classes only after the closed state has painted once.
    /// Applying them in the same frame the scope becomes visible leaves the
    /// transition with no starting value, so the sheet snapped into place.
    /// </summary>
    private async void AnimateSheetOpenSoon(int version)
    {
        await Task.Delay(24);
        if (version != _sheetVersion || !SheetScope.IsVisible) return;
        SheetScrim.Classes.Add("open");
        SheetPanel.Classes.Add("open");
        SetPageNativeControlsScrim(true);
    }

    /// <summary>
    /// The sheet panel slides over native page controls, which are drawn above
    /// everything Avalonia paints. Hide exactly the ones the settled panel
    /// covers as soon as its layout exists, before the slide reaches them.
    /// </summary>
    private async void RevealSheetCoverageSoon(int version)
    {
        await Task.Delay(60);
        if (version != _sheetVersion || !SheetScope.IsVisible) return;
        SetPageNativeControlsObscured(true, checkCoverage: true);
    }

    /// <summary>Updates a live sheet's theme without recreating its contents.</summary>
    public void SetSheetTheme(ThemeVariant? variant) =>
        SheetScope.RequestedThemeVariant = variant ?? ThemeVariant.Default;

    /// <summary>
    /// A virtualized list measures what is visible while the sheet is still
    /// sliding up from off-screen, realizes only a row or two, and does not
    /// re-measure when the slide ends. Measure it again once the sheet is in place.
    /// </summary>
    private async void RefreshVirtualizedListsAfterOpen(int version)
    {
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        SetPageNativeControlsObscured(true, checkCoverage: true);
        UpdateSheetNativeControlClipping();
        _sheetControlTimer?.Stop();
        foreach (var panel in SheetBody.GetVisualDescendants().OfType<VirtualizingStackPanel>())
            panel.InvalidateMeasure();
    }

    /// <summary>Resolves false when the sheet is closed any way other than confirming.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (NativeConfirmation is { } native)
            return native(UiText.Localize(title), UiText.Localize(message), UiText.Localize(confirmText));
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowSheet(title, new PromptSheet(message, confirmText, _ =>
        {
            decision.TrySetResult(true);
            HideSheet();
            return Task.CompletedTask;
        }), onDismissed: () => decision.TrySetResult(false));
        return decision.Task;
    }

    private void NotifySheetDismissed()
    {
        var dismissed = _sheetDismissed;
        _sheetDismissed = null;
        dismissed?.Invoke();
    }

    public async void HideSheet()
    {
        if (!SheetScope.IsVisible) return;
        NotifySheetDismissed();
        var version = ++_sheetVersion;
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetObscured(true);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetObscured(true);
        foreach (var group in SheetBody.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetObscured(true);
        StartSheetControlTracking();
        SetPageNativeControlsScrim(false);
        SheetScrim.Classes.Remove("open");
        SheetPanel.Classes.Remove("open");
        await Task.Delay(SheetCloseDelay);
        if (version != _sheetVersion) return;
        SheetScope.IsVisible = false;
        SheetBody.Content = null;
        SetPageNativeControlsObscured(false);
        _sheetControlTimer?.Stop();
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetSheetClipRect(null);
        if (_services.TabBar is ISheetOccludingTabBar occludingTabBar)
        {
            occludingTabBar.SetSheetOcclusion(null);
            _services.TabBar.Update(!_backupBlocked && _history.Count == 1, (int)_tab);
            return;
        }
        // Let the rendered page replace the sheet before showing the native
        // glass bar, whose backdrop samples the last composited frame.
        await Task.Delay(50);
        if (version != _sheetVersion) return;
        _services.TabBar?.Update(!_backupBlocked && _history.Count == 1, (int)_tab);
    }

    private void StartSheetControlTracking()
    {
        var trackTabBar = _services.TabBar is ISheetOccludingTabBar && _history.Count == 1;
        if (!trackTabBar && !PageHost.GetVisualDescendants().OfType<NativeSliderGroup>()
                .Any(group => group.SupportsSheetClipping)) return;
        _sheetControlTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _sheetControlTimer.Tick -= OnSheetControlTick;
        _sheetControlTimer.Tick += OnSheetControlTick;
        _sheetControlTimer.Start();
    }

    private void OnSheetControlTick(object? sender, EventArgs e)
    {
        if (!SheetScope.IsVisible) return;
        UpdateSheetNativeControlClipping();
    }

    private void UpdateSheetNativeControlClipping()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || SheetPanel.TranslatePoint(default, topLevel) is not { } origin) return;
        if (_history.Count == 1 && _services.TabBar is ISheetOccludingTabBar tabBar)
            tabBar.SetSheetOcclusion(new Rect(origin, SheetPanel.Bounds.Size));
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
        {
            if (!group.SupportsSheetClipping || SheetPanel.TranslatePoint(default, group) is not { } relative)
                continue;
            var height = Math.Clamp(relative.Y, 0, group.Bounds.Height);
            group.SetSheetClipRect(height >= group.Bounds.Height
                ? null
                : new Rect(0, 0, group.Bounds.Width, height));
        }
    }

    /// <summary>
    /// Hosted native controls sit above the Avalonia sheet. Controls above the
    /// panel keep their native look and get a matching dimming overlay; only
    /// controls the settled panel actually covers are hidden.
    /// </summary>
    private void SetPageNativeControlsObscured(bool obscured, bool checkCoverage = false)
    {
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeMenuButton>())
            group.SetObscured(obscured);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeActionButton>())
            group.SetObscured(obscured);
        Rect? sheetBounds = null;
        if (checkCoverage) sheetBounds = SettledSheetBounds();

        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
        {
            var covered = sheetBounds is { } sheet
                && group.TranslatePoint(default, this) is { } groupOrigin
                && sheet.Intersects(new Rect(groupOrigin, group.Bounds.Size));
            group.SetSheetObscured(obscured, covered);
        }
    }

    /// <summary>
    /// The sheet's resting rectangle. During the slide the render transform
    /// still points off-screen, but coverage must use where it will land so a
    /// hosted native control never floats over the rising panel.
    /// </summary>
    private Rect? SettledSheetBounds()
    {
        var height = SheetPanel.Bounds.Height;
        if (height <= 0)
            return SheetPanel.TranslatePoint(default, this) is { } origin
                ? new Rect(origin, SheetPanel.Bounds.Size)
                : null;
        var bottom = Bounds.Height + Padding.Bottom;
        return new Rect(0, bottom - height, Bounds.Width, height);
    }

    private void SetPageNativeControlsScrim(bool visible)
    {
        var brush = SheetScrim.Background as ISolidColorBrush;
        var color = brush?.Color ?? Color.FromArgb(0x59, 0, 0, 0);
        if (brush is not null && brush.Opacity < 1)
            color = Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B);
        // The scrim border fades as a whole and is lighter behind the reader
        // settings sheet; the hosted overlay must match it exactly.
        var elementOpacity = SheetScrim.Classes.Contains("open")
            ? (SheetScrim.Classes.Contains("reader-preview") ? 0.2 : 1)
            : 0;
        color = Color.FromArgb((byte)Math.Round(color.A * elementOpacity), color.R, color.G, color.B);
        // A single overlay above the hosted view dims its opaque parts (the
        // selected pill); the backdrop keeps the glass sample undimmed so the
        // same overlay does not darken the translucent track a second time.
        var backdrop = PageBackdropColor();
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSegmentGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSwitchGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
        foreach (var group in PageHost.GetVisualDescendants().OfType<NativeSliderGroup>())
            group.SetSheetScrim(visible ? color : null, backdrop);
    }

    private Color PageBackdropColor()
    {
        if (this.TryFindResource(PageBackgroundKey, ActualThemeVariant, out var value)
            && value is ISolidColorBrush brush && brush.Color.A > 0)
            return brush.Color;
        return ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromRgb(0x12, 0x12, 0x12)
            : Colors.White;
    }
}
