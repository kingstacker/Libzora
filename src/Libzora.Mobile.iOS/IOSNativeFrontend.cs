#if LIBZORA_SWIFT_UI
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Avalonia.Threading;
using Foundation;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile;
using Libzora.Mobile.Views;
using Libzora.Mobile.Localization;
using UIKit;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.iOSHead;

/// <summary>
/// In-process boundary between SwiftUI and the existing application services.
/// Requests and replies contain data only; native controls never forward taps
/// to hidden Avalonia buttons or share their scrolling/clip geometry.
/// </summary>
internal sealed partial class IOSNativeFrontend
{
    private static readonly NativeRequest Callback = OnRequest;
    private static IOSNativeFrontend? _current;
    private static Uri? _pendingUrl;
    private static readonly List<string> PendingIncomingFiles = [];
    private readonly MobileShell _shell;
    private readonly WidgetSnapshotCoordinator _widgetSnapshots;
    private readonly AvaloniaView _readerSurface;
    private readonly UIWindow _window;
    private readonly Dictionary<long, CancellationTokenSource> _requests = [];
    private readonly Dictionary<long, TaskCompletionSource<string>> _prompts = [];
    private readonly List<NSObject> _observers = [];
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly AsyncLocal<CancellationToken> _requestToken = new();
    private readonly UIViewController _readerController;
    private long _promptId;
    private ReaderPage? _reader;
    private string? _readerSessionId;
    private CancellationTokenSource? _readerCancellation;
    private Thickness _nativeSurfaceInsets;
    private bool _readerSurfaceRenderingPaused;
    private readonly IOSNowPlayingController _nowPlaying;

    internal static bool IsAttached => _current is not null;

    private IOSNativeFrontend(MobileShell shell, AvaloniaView readerSurface, UIViewController readerController, UIWindow window)
    {
        _shell = shell;
        _readerSurface = readerSurface;
        _readerController = readerController;
        _window = window;
        _nowPlaying = new IOSNowPlayingController(() => _reader);
        // Notification callbacks may run as soon as they are registered.
        // Their snapshot coordinator must already be fully initialized.
        _widgetSnapshots = new WidgetSnapshotCoordinator(
            publish: PublishWidgetSnapshotAsync,
            published: ReloadWidgets);
        shell.NativeToast = message => Publish(Data(("type", "toast"), ("message", message)));
        shell.NativeConfirmation = ConfirmAsync;
        shell.NativeRestartRequired = () => Publish(Data(("type", "restart")));
        shell.NativeLibraryChanged = () => Publish(Data(("type", "libraryChanged")));
        shell.Preferences.Changed += (_, _) =>
            Publish(Data(("type", "preferences"), ("preferences", shell.Preferences.Current)));
        shell.Modules.Accounts.Service.DeviceInvalidated += (_, _) => Publish(Data(("type", "accountInvalidated")));
        shell.DeveloperModeChanged += (_, _) => Publish(Data(("type", "developer"), ("enabled", shell.IsDeveloperMode)));
        UiText.LanguageChanged += (_, _) =>
        {
            Publish(Data(("type", "language"), ("language", UiText.CurrentLanguage)));
            _reader?.NativeRefreshUiText();
        };
        _observers.Add(UIApplication.Notifications.ObserveDidBecomeActive((_, _) =>
        {
            shell.UiLanguage?.Refresh();
            ResumeReaderSurfaceRendering(_reader);
            _reader?.Resume();
            _ = shell.CheckDevicesIfDueAsync();
        }));
        _observers.Add(UIApplication.Notifications.ObserveWillResignActive((_, _) =>
        {
            PauseReaderSurfaceRendering(_reader);
            if (_reader is not { } reader)
            {
                _widgetSnapshots.Schedule();
                return;
            }
            if (shell.Services.BackgroundWork is { } background) background.Run(() => PauseReaderAndPublishWidgetsAsync(reader));
            else _ = PauseReaderAndPublishWidgetsAsync(reader);
        }));
        _observers.Add(UIApplication.Notifications.ObserveDidEnterBackground((_, _) =>
        {
            PauseReaderSurfaceRendering(_reader);
        }));

        shell.Modules.Library.Books.DataChanged += (_, _) => _widgetSnapshots.Schedule();
        shell.Modules.Reader.Data.DataChanged += (_, _) => _widgetSnapshots.Schedule();
        // The shelf is usually initialized after this constructor returns, so
        // this first pass is mostly a placeholder; library.get schedules the
        // populated snapshot once the native shelf has loaded.
        _widgetSnapshots.Schedule();
    }

    private async Task PauseReaderAndPublishWidgetsAsync(ReaderPage reader)
    {
        try
        {
            await reader.PauseAsync();
        }
        finally
        {
            _widgetSnapshots.Schedule();
        }
    }

    private Task PublishWidgetSnapshotAsync(CancellationToken cancellationToken)
    {
        var colorfulMode = _shell.Preferences.Current.ColorfulMode;
        var themeMode = (int)_shell.Preferences.Current.ThemeMode;
        var uiFont = (int)_shell.Preferences.Current.UiFont;
        return Task.Run(() => WidgetSnapshotPublisher.PublishAsync(
            _shell.Modules.Paths,
            _shell.Modules.Library.Books,
            _shell.Modules.Reader.Data,
            colorfulMode,
            themeMode,
            IOSWidgetPaths.Resolve(_shell.Modules.Paths),
            cancellationToken,
            uiFont: uiFont), cancellationToken);
    }

    private static void ReloadWidgets()
    {
        try
        {
            lz_ios_reload_widgets();
        }
        catch (Exception)
        {
            // The extension may not be installed, or the app may be shutting
            // down; WidgetKit will still pick up the snapshot on its next pass.
        }
    }

    [DllImport("__Internal")]
    private static extern void lz_ios_reload_widgets();

    /// <summary>Routes a libzora:// deep link or an external book file URL delivered by iOS.</summary>
    internal static bool HandleIncomingUrl(NSUrl? url)
    {
        if (url is null) return false;
        if (url.IsFileUrl)
        {
            var copied = IOSFileImportSource.CopyExternalUrlToCache(url);
            if (copied is null) return false;
            if (_current is { } frontend)
                Dispatcher.UIThread.Post(() => _ = frontend.ImportExternalFilesAsync([copied]));
            else
                lock (PendingIncomingFiles) { PendingIncomingFiles.Add(copied); }
            return true;
        }
        return HandleUrl(url.AbsoluteString);
    }

    /// <summary>Routes a libzora:// link delivered by a widget or another app.</summary>
    internal static bool HandleUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)
            || !string.Equals(url.Scheme, "libzora", StringComparison.OrdinalIgnoreCase))
            return false;
        if (_current is { } frontend)
        {
            if (Dispatcher.UIThread.CheckAccess()) frontend.OpenUrl(url);
            else Dispatcher.UIThread.Post(() => frontend.OpenUrl(url));
        }
        else
            _pendingUrl = url;
        return true;
    }

    private void OpenUrl(Uri url)
    {
        switch (url.Host.ToLowerInvariant())
        {
#if DEBUG
            case "test-txt-import" when NSBundle.MainBundle.BundleIdentifier == "com.kingstacker.libzora.iossafetycheck":
                var fixtureDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var fixtures = new[] { "TXT规则样例.txt", "TXT确认样例.txt" }
                    .Select(name => Path.Combine(fixtureDirectory, name)).Where(File.Exists).ToArray();
                if (fixtures.Length > 0) _ = ImportExternalFilesAsync(fixtures);
                break;
#endif
            case "read":
                var id = url.AbsolutePath.Trim('/');
                if (Guid.TryParse(id, out var bookId))
                    Publish(Data(("type", "openWidgetBook"), ("bookId", bookId.ToString())));
                break;
            case "dashboard":
                ShowTab(2);
                break;
            case "materials":
                ShowTab(1);
                break;
            case "profile":
                ShowTab(3);
                break;
            case "library":
                ShowTab(0);
                break;
        }
    }

    private void ShowTab(int tab)
    {
        // A tab link must land on the tab even if a book was open when the
        // widget was tapped. Swift clears its saved-session restore, cancels
        // an in-flight reader open, dismisses the reader cover and selects
        // the tab, so no separate close event can race ahead of it.
        Publish(Data(("type", "openTab"), ("tab", tab)));
    }

    internal static void Attach(UIWindow window)
    {
        if (_current is not null || Application.Current is not MobileApp { Shell: { } shell,
                ApplicationLifetime: ISingleViewApplicationLifetime lifetime }) return;

        // This surface is only a child of the native reader. It is never the
        // window root, so opening a book doesn't transfer a visible controller
        // between hierarchies or leave an appearance transition unfinished.
        var surface = new AvaloniaView();
        var readerController = new DefaultAvaloniaViewController { View = surface };
        surface.InitWithController(readerController);
        var frontend = new IOSNativeFrontend(shell, surface, readerController, window);
        NativeLibrary.SetDllImportResolver(typeof(IOSNativeFrontend).Assembly, (library, _, _) =>
            library == "__Internal" ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero);
        _current = frontend;
        // Set the effective locale and interface font before SwiftUI constructs its first frame.
        SetUiLanguage(UiText.CurrentLanguage);
        SetUiFont((int)shell.Preferences.Current.UiFont);
        var pointer = CreateShell(Marshal.GetFunctionPointerForDelegate(Callback), readerController.Handle);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException(UiText.Get("无法创建 SwiftUI 界面。"));
        var controller = ObjCRuntime.Runtime.GetNSObject<UIViewController>(pointer)
            ?? throw new InvalidOperationException(UiText.Get("无法绑定 SwiftUI 界面。"));
        window.RootViewController = controller;
        window.BackgroundColor = UIColor.SystemBackground;
        // The managed preferences are already loaded. Publish them before the
        // first frame, so an early native tap never saves Swift's placeholders
        // over the user's existing reading and appearance settings.
        Publish(Data(("type", "preferences"), ("preferences", shell.Preferences.Current)));
        Publish(Data(("type", "developer"), ("enabled", shell.IsDeveloperMode)));
        Publish(Data(("type", "syncEntitlements"), ("enabled", AccountAvailability.EnforceSyncEntitlements)));
#if DEBUG
        Console.WriteLine("Libzora native: SwiftUI root created.");
#endif
        ReleaseShell(pointer);
        if (_pendingUrl is { } pendingUrl)
        {
            _pendingUrl = null;
            frontend.OpenUrl(pendingUrl);
        }
        string[]? pendingFiles = null;
        lock (PendingIncomingFiles)
        {
            if (PendingIncomingFiles.Count > 0)
            {
                pendingFiles = PendingIncomingFiles.ToArray();
                PendingIncomingFiles.Clear();
            }
        }
        if (pendingFiles is not null)
            Dispatcher.UIThread.Post(() => _ = frontend.ImportExternalFilesAsync(pendingFiles));
        shell.StartDeviceChecks();
        lifetime.MainView = null;
        if (MobileDataCoordinator.IsRestartRequired) Publish(Data(("type", "restart")));
        else shell.RequestAutoSync(MobileShell.AutoSyncGap.OnStart);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeRequest(long id, IntPtr json);

    [ObjCRuntime.MonoPInvokeCallback(typeof(NativeRequest))]
    private static void OnRequest(long id, IntPtr json)
    {
        // Swift's UTF-8 buffer is borrowed only for the duration of this call.
        var request = Marshal.PtrToStringUTF8(json);
        if (request is null || _current is not { } frontend) return;
        Dispatcher.UIThread.Post(() => frontend.HandleRequest(id, request));
    }

    private async void HandleRequest(long id, string request)
    {
        CancellationTokenSource? cancellation = null;
        try
        {
            using var document = JsonDocument.Parse(request);
            var command = document.RootElement.GetProperty("command").GetString() ?? string.Empty;
            if (command == "cancel")
            {
                if (_requests.TryGetValue(id, out var pending)) pending.Cancel();
                return;
            }
            cancellation = new CancellationTokenSource();
            _requests.Add(id, cancellation);
            _requestToken.Value = cancellation.Token;
            var args = document.RootElement.TryGetProperty("args", out var value) ? value : default;
            var result = await DispatchAsync(command, args, cancellation.Token);
            ResolveRequest(id, Serialize(Data(("ok", true), ("result", result))));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ResolveRequest(id, Serialize(Data(("ok", false), ("error", UiText.Localize(exception.Message)))));
        }
        finally
        {
            if (cancellation is not null)
            {
                _requests.Remove(id);
                cancellation.Dispose();
            }
        }
    }

    private async Task<object> DispatchAsync(string command, JsonElement args, CancellationToken token)
    {
        if (command == "ui.confirm")
        {
            if (_prompts.Remove(args.GetProperty("id").GetInt64(), out var decision))
                decision.TrySetResult(args.TryGetProperty("choice", out _) ? Text(args, "choice")
                    : Flag(args, "accepted") ? "confirm" : "cancel");
            return Data();
        }
        if (MobileDataCoordinator.IsRestartRequired)
            throw new InvalidOperationException(UiText.Get("请完全关闭并重新打开应用后继续使用。"));
        switch (command)
        {
            case "library.get":
                return await GetLibraryAsync(token);
            case "library.filterOptions":
                using (BeginOperation())
                {
                    await _shell.Modules.Library.Books.InitializeAsync(token);
                    return await _shell.Modules.Library.Books.GetFilterOptionsAsync(token);
                }
            case "library.filter":
                return await FilterLibraryAsync(args, token);
            case "library.import":
                await ImportBooksAsync(token);
                return Data();
            case "reader.fonts":
                return await GetReaderFontsAsync(token);
            case "reader.fontDownload":
                return await InstallReaderFontAsync(Text(args, "id"), token);
            case "preferences.save":
                await _readerGate.WaitAsync(token);
                try
                {
                    var previous = _shell.Preferences.Current;
                    var preferences = Read(args.GetProperty("preferences"), previous, IOSNativeJsonContext.Default.MobilePreferences);
                    _shell.Preferences.UpdateAndSave(_ => NormalizePreferences(preferences));
                    _shell.UiLanguage?.Refresh();
                    _shell.ApplyThemeMode();
                    _widgetSnapshots.Schedule();
                    // Language and interface-font changes refresh chrome through the event;
                    // they must not reflow or reopen the current book.
                    if (_reader is { } currentReader
                        && (previous with
                        {
                            UiLanguage = _shell.Preferences.Current.UiLanguage,
                            UiFont = _shell.Preferences.Current.UiFont,
                            OnboardingAccepted = _shell.Preferences.Current.OnboardingAccepted
                        }) != _shell.Preferences.Current)
                        await currentReader.NativeApplySettingsAsync();
                }
                finally { _readerGate.Release(); }
                return _shell.Preferences.Current;
            case "reader.open":
                RequireOnboardingAcceptance();
                _readerCancellation?.Cancel();
                await _readerGate.WaitAsync(token);
                try
                {
                    await CloseReaderAsync();
                    var book = await _shell.Modules.Library.Books.GetBookAsync(Id(args, "bookId"), token)
                        ?? throw new InvalidOperationException(UiText.Get("书籍已不存在。"));
                    var jump = args.TryGetProperty("location", out var location)
                        ? location.Deserialize(IOSNativeJsonContext.Default.ReaderProgressRow) : null;
                    var preferredFileId = await ResolveReaderFileAsync(book, args, jump, token);
                    _readerCancellation = new CancellationTokenSource();
                    _readerSessionId = Text(args, "sessionId");
                    _reader = new ReaderPage(book, _shell.Modules, _shell, jump, preferredFileId);
                    var sessionId = _readerSessionId;
                    _reader.NativeStateChanged += state =>
                    {
                        if (_reader is { } active && string.Equals(_readerSessionId, sessionId, StringComparison.Ordinal))
                            _nowPlaying.Update(active, state, _shell.Modules.Paths);
                        Publish(Data(("type", "reader"), ("sessionId", sessionId), ("reader", state)));
                    };
                    _reader.NativeDialogRequested += dialog => Publish(Data(("type", "readerDialog"), ("sessionId", sessionId), ("dialog", dialog)));
                    _reader.NativeReaderMediaRequested += media => Publish(Data(("type", "readerMedia"), ("sessionId", sessionId), ("media", media)));
                    _reader.NativeFootnoteRequested += footnote => Publish(Data(("type", "readerFootnote"), ("sessionId", sessionId), ("footnote", footnote)));
                    _reader.NativeSearchBatchChanged += search => Publish(Data(("type", "readerSearch"), ("sessionId", sessionId), ("search", search)));
                    _reader.UseNativeFrontend();
                    var surfaceBounds = _readerSurface.Bounds.Width >= 2 && _readerSurface.Bounds.Height >= 2
                        ? _readerSurface.Bounds
                        : _window.Bounds;
                    var viewportLeft = 0d;
                    if (args.TryGetProperty("insets", out var initialInsets) && initialInsets.ValueKind == JsonValueKind.Object)
                    {
                        _nativeSurfaceInsets = new Thickness(
                            ReadDouble(initialInsets, "left"),
                            ReadDouble(initialInsets, "top"),
                            ReadDouble(initialInsets, "right"),
                            ReadDouble(initialInsets, "bottom"));
                        var hintWidth = ReadDouble(initialInsets, "viewportWidth");
                        var hintHeight = ReadDouble(initialInsets, "viewportHeight");
                        viewportLeft = Math.Max(0, ReadDouble(initialInsets, "viewportLeft"));
                        if (hintWidth >= 2 && hintHeight >= 2)
                            surfaceBounds = new CoreGraphics.CGRect(0, 0, hintWidth, hintHeight);
                    }
                    else if (_nativeSurfaceInsets == default)
                    {
                        var safe = _window.SafeAreaInsets;
                        _nativeSurfaceInsets = new Thickness(safe.Left, safe.Top, safe.Right, safe.Bottom);
                        viewportLeft = Math.Max(0, safe.Left);
                    }
                    _reader.NativeSetViewportHint(surfaceBounds.Width, surfaceBounds.Height);
                    _reader.NativeSetSurfaceInsets(_nativeSurfaceInsets, surfaceBounds.Width, surfaceBounds.Height, viewportLeft);
                    var darkAppearance = _shell.Preferences.Current.ThemeMode switch
                    {
                        MobileThemeMode.Light => false,
                        MobileThemeMode.Dark => true,
                        _ => _window.TraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark
                    };
                    _reader.NativeSetAppAppearance(darkAppearance, 0);
                    // SwiftUI already places this surface inside the window's
                    // safe area and the reader page applies its own margins.
                    // Avalonia's automatic safe-area padding would otherwise
                    // subtract the bottom inset a second time, shortening the
                    // text column and leaving a blank band above the footer.
                    TopLevel.SetAutoSafeAreaPadding(_reader, false);
                    _readerSurface.Content = _reader;
                    if (UIApplication.SharedApplication.ApplicationState == UIApplicationState.Background)
                        PauseReaderSurfaceRendering(_reader);
                    else
                        ResumeReaderSurfaceRendering(_reader);
                    // The reader lives outside the shell's navigation stack, so
                    // shell services (automatic sync) need it reported here.
                    _shell.SetActiveReaderPage(_reader);
                }
                finally { _readerGate.Release(); }
                return Data();
            case "reader.appearance":
                await _readerGate.WaitAsync(token);
                try
                {
                    if (_reader is { } current && string.Equals(_readerSessionId, Text(args, "sessionId"), StringComparison.Ordinal))
                        current.NativeSetAppAppearance(Flag(args, "dark"), Number(args, "revision"));
                }
                finally { _readerGate.Release(); }
                return Data();
            case "reader.insets":
                await _readerGate.WaitAsync(token);
                try
                {
                    var insetSession = Text(args, "sessionId");
                    if (_reader is not null && !string.Equals(_readerSessionId, insetSession, StringComparison.Ordinal))
                        return Data(("accepted", false), ("revision", 0L));
                    _nativeSurfaceInsets = new Thickness(
                        ReadDouble(args, "left"),
                        ReadDouble(args, "top"),
                        ReadDouble(args, "right"),
                        ReadDouble(args, "bottom"));
                    var viewportWidth = ReadDouble(args, "viewportWidth");
                    var viewportHeight = ReadDouble(args, "viewportHeight");
                    var viewportLeft = Math.Max(0, ReadDouble(args, "viewportLeft"));
                    _reader?.NativeSetSurfaceInsets(
                        _nativeSurfaceInsets,
                        viewportWidth >= 2 ? viewportWidth : null,
                        viewportHeight >= 2 ? viewportHeight : null,
                        viewportLeft);
                    return Data(("accepted", _reader is not null),
                        ("revision", _reader?.NativePresentationFrameRevision ?? 0L));
                }
                finally { _readerGate.Release(); }
            case "reader.close":
                _readerCancellation?.Cancel();
                await _readerGate.WaitAsync(token);
                try { await CloseReaderAsync(); }
                finally { _readerGate.Release(); }
                return Data();
            default:
                if (command.StartsWith("reader.", StringComparison.Ordinal))
                    return await DispatchReaderAsync(command, args, token);
                if (command.StartsWith("book.", StringComparison.Ordinal)
                    || command.StartsWith("collection.", StringComparison.Ordinal)
                    || command.StartsWith("materials.", StringComparison.Ordinal)
                    || command.StartsWith("reflection.", StringComparison.Ordinal)
                    || command.StartsWith("trash.", StringComparison.Ordinal)
                    || command.StartsWith("backup.", StringComparison.Ordinal)
                    || command.StartsWith("txt.", StringComparison.Ordinal)
                    || command == "dashboard.get")
                    return await DispatchLibraryAsync(command, args, token);
                if (command.StartsWith("settings.", StringComparison.Ordinal)
                    || command.StartsWith("dictionary.", StringComparison.Ordinal))
                    return await DispatchSettingsAsync(command, args, token);
                throw new InvalidOperationException(UiText.Get("未知操作：{0}", command));
        }
    }

    private void RequireOnboardingAcceptance()
    {
#if DEBUG
        if (NSProcessInfo.ProcessInfo.Arguments.Contains("--libzora-skip-onboarding")) return;
#endif
        if (!_shell.Preferences.Current.OnboardingAccepted)
            throw new InvalidOperationException(UiText.Get("请先阅读并同意用户协议与隐私政策。"));
    }

    private void PauseReaderSurfaceRendering(ReaderPage? reader)
    {
        if (_readerSurfaceRenderingPaused) return;
        var target = reader is null ? null : TopLevel.GetTopLevel(reader) as Avalonia.Controls.Embedding.EmbeddableControlRoot;
        if (target is null) return;
        target.StopRendering();
        _readerSurfaceRenderingPaused = true;
    }

    private void ResumeReaderSurfaceRendering(ReaderPage? reader)
    {
        if (reader is null) return;
        var target = TopLevel.GetTopLevel(reader) as Avalonia.Controls.Embedding.EmbeddableControlRoot;
        if (target is null) return;
        if (_readerSurfaceRenderingPaused)
        {
            target.StartRendering();
            _readerSurfaceRenderingPaused = false;
        }
        reader.InvalidateVisual();
    }

    private async Task CloseReaderAsync()
    {
        _nowPlaying.Clear();
        if (_reader is not { } reader) return;
        reader.NativeCancelSearch();
        await reader.PauseAsync();
        PauseReaderSurfaceRendering(reader);
        _readerSurface.Content = null;
        _reader = null;
        _shell.SetActiveReaderPage(null);
        _readerSessionId = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        _shell.RequestAutoSync(MobileShell.AutoSyncGap.AfterReading);
        _widgetSnapshots.Schedule();
    }

    private async Task<object> GetLibraryAsync(CancellationToken token)
    {
        using var operation = BeginOperation();
        await _shell.Modules.Library.Books.InitializeAsync(token);
        await _shell.EnsureReaderDataAsync();
        var books = await _shell.Modules.Library.Books.SearchAsync(cancellationToken: token);
        var collections = await _shell.Modules.Library.Books.GetCollectionSummariesAsync(token);
        var dashboard = await _shell.Modules.Reader.Data.GetReadingDashboardAsync(dayCount: 1, cancellationToken: token);
        var progress = dashboard.Books.GroupBy(item => item.BookId)
            .ToDictionary(group => group.Key, group => group.Max(item => item.ProgressPercent));
        var readingStates = books.ToDictionary(book => book.Id, book => book.ReadingStatus);
        var continueBookId = dashboard.Books.Where(item => item.IsInLibrary && item.ProgressPercent < 99.5
                && readingStates.GetValueOrDefault(item.BookId) != LibraryReadingStatus.Finished
                && !(readingStates.GetValueOrDefault(item.BookId) == LibraryReadingStatus.Unread
                    && _shell.Preferences.Current.HasManualReadingStatus(item.BookId, LibraryReadingStatus.Unread)))
            .OrderByDescending(item => item.UpdatedAt).Select(item => (Guid?)item.BookId).FirstOrDefault();
        IReadOnlyDictionary<Guid, BookSyncStatus>? syncStatuses = null;
        if (_shell.Preferences.Current.ShowBookSyncStatus)
        {
            syncStatuses = await IOSNativeBookLibraryPolicy.OptionalSyncStatusesAsync(async badgeToken =>
            {
                var service = _shell.Modules.Sync.Service;
                var settings = await service.LoadSettingsAsync(badgeToken);
                return await service.GetBookSyncStatusesAsync(settings.DeviceId, settings.Settings, books, badgeToken);
            }, token);
        }
        _widgetSnapshots.Schedule();
        return Data(
            ("books", books.Select(book => BookData(book, progress.GetValueOrDefault(book.Id),
                syncStatuses != null && syncStatuses.TryGetValue(book.Id, out var status) ? status : null)).ToArray()),
            ("collections", collections.Select(item => Data(
                ("id", item.Collection.Id), ("name", item.Collection.Name), ("count", item.BookCount),
                ("automatic", MobileCollections.IsUncollected(item.Collection)))).ToArray()),
            ("preferences", _shell.Preferences.Current), ("continueBookId", continueBookId));
    }

    // The native shelf retains its existing book snapshot; return matching IDs
    // using the same option and token semantics as the desktop library.
    private async Task<Guid[]> FilterLibraryAsync(JsonElement args, CancellationToken token)
    {
        using var operation = BeginOperation();
        var books = _shell.Modules.Library.Books;
        await books.InitializeAsync(token);
        const int pageSize = 500;
        var ids = new List<Guid>();
        for (var pageIndex = 0; ; pageIndex++)
        {
            var page = await books.SearchPageAsync(
                author: Text(args, "author"), tag: Text(args, "tag"), format: Text(args, "format"),
                category: Text(args, "category"), pageIndex: pageIndex, pageSize: pageSize, cancellationToken: token);
            ids.AddRange(page.Books.Select(book => book.Id));
            if (page.Books.Count == 0 || ids.Count >= page.TotalCount) break;
        }
        return ids.ToArray();
    }

    private object BookData(Book book, double progress, BookSyncStatus? syncStatus) => Data(
        ("id", book.Id), ("title", book.Title), ("authors", book.Authors), ("formats", book.FormatSummary),
        ("size", MobileFormat.Size(book.Files.Sum(file => file.Size))),
        ("coverPath", string.IsNullOrWhiteSpace(book.CoverPath) ? null
            : Path.IsPathRooted(book.CoverPath) ? book.CoverPath : Path.Combine(_shell.Modules.Paths.Data, book.CoverPath)),
        ("progress", progress), ("favorite", book.IsFavorite), ("readingStatus", (int)book.ReadingStatus),
        ("readingStatusExplicit", _shell.Preferences.Current.HasManualReadingStatus(book.Id, book.ReadingStatus)),
        ("syncStatus", syncStatus is { } status ? (int?)status : null),
        ("collectionIds", book.CollectionIds.ToArray()), ("updatedAt", book.UpdatedAt), ("createdAt", book.CreatedAt),
        ("description", book.Description), ("series", book.Series),
        ("seriesIndex", IOSNativeBookLibraryPolicy.FiniteSeriesIndex(book.SeriesIndex)),
        ("fileTitles", book.Files.Select(file => Path.GetFileNameWithoutExtension(file.RelativePath)).ToArray()),
        ("publisher", book.Publisher),
        ("publishDate", book.PublishDate), ("isbn", book.Isbn), ("pageCount", book.PageCount),
        ("binding", book.Binding), ("tags", book.Tags), ("category", book.Category), ("onlineRating", book.OnlineRating));

    private async Task<ImportConflictResolution> ChooseImportConflictAsync(ImportBookConflict conflict, CancellationToken token)
    {
        long id = 0;
        var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Import runs on a worker. Prompt state and the Swift C ABI belong to
        // the UI thread, including dismissal after cancellation.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            id = ++_promptId;
            _prompts.Add(id, decision);
            var choices = new List<Dictionary<string, object?>>
            {
                Data(("id", "keepSeparate"), ("title", UiText.Get("独立保存")), ("cancel", false))
            };
            // Without a per-file reader picker, attaching a second copy of an
            // already-present format would make it inaccessible on iOS.
            if (IOSNativeBookLibraryPolicy.CanAddFormat(conflict))
                choices.Add(Data(("id", "addFormat"), ("title", UiText.Get("添加格式")), ("cancel", false)));
            choices.Add(Data(("id", "skip"), ("title", UiText.Get("跳过")), ("cancel", true)));
            Publish(Data(("type", "confirm"), ("prompt", Data(("id", id),
                ("title", UiText.Get("同名书籍")),
                ("message", UiText.Get("书库中已有《{0}》（{1}）。请选择此文件的保存方式：{2}",
                    conflict.Title, conflict.Authors, Path.GetFileName(conflict.SourcePath))),
                ("confirm", UiText.Get("独立保存")), ("choices", choices.ToArray())))));
        });
        try
        {
            var choice = await decision.Task.WaitAsync(token);
            return choice switch
            {
                "keepSeparate" => ImportConflictResolution.KeepSeparate,
                "addFormat" when IOSNativeBookLibraryPolicy.CanAddFormat(conflict) => ImportConflictResolution.AddAsFormat,
                _ => ImportConflictResolution.Skip
            };
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _prompts.Remove(id);
                Publish(Data(("type", "confirmDismissed"), ("id", id)));
            });
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var id = ++_promptId;
        var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompts.Add(id, decision);
        Publish(Data(("type", "confirm"), ("prompt", Data(("id", id),
            ("title", UiText.Localize(title)), ("message", UiText.Localize(message)), ("confirm", UiText.Localize(confirm))))));
        try { return await decision.Task.WaitAsync(_requestToken.Value) == "confirm"; }
        finally
        {
            _prompts.Remove(id);
            Publish(Data(("type", "confirmDismissed"), ("id", id)));
        }
    }

    private static MobileDataCoordinator.OperationLease BeginOperation() =>
        MobileDataCoordinator.TryBeginOperation() ?? throw new InvalidOperationException(UiText.Get("正在备份或恢复数据，请稍后重试。"));

    private static Guid Id(JsonElement args, string name) => args.GetProperty(name).GetGuid();
    private static string Text(JsonElement args, string name, string fallback = "") =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool Flag(JsonElement args, string name, bool fallback = false) =>
        args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static int Number(JsonElement args, string name, int fallback = 0) =>
        args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;
    private static double ReadDouble(JsonElement args, string name, double fallback = 0) =>
        args.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
    private static MobilePreferences NormalizePreferences(MobilePreferences value) => value with
    {
        UiLanguage = MobileUiLanguageService.NormalizePreference(value.UiLanguage),
        UiFont = Enum.IsDefined(value.UiFont) ? value.UiFont : AppUiFont.KingHwaOldSong,
        ThemeMode = Enum.IsDefined(value.ThemeMode) ? value.ThemeMode : MobileThemeMode.System,
        Sort = Enum.IsDefined(value.Sort) ? value.Sort : MobileBookSort.Recent,
        ReaderTheme = Enum.IsDefined(value.ReaderTheme) ? value.ReaderTheme : ReaderTheme.Classic,
        ReaderFlowMode = MobilePreferences.NormalizeFlowMode(value.ReaderFlowMode),
        ReaderPageAnimation = MobilePreferences.NormalizePageAnimation(value.ReaderPageAnimation),
        ReaderProgressDisplay = MobilePreferences.NormalizeProgressDisplay(value.ReaderProgressDisplay),
        ReaderPowerSavingMode = Enum.IsDefined(value.ReaderPowerSavingMode) ? value.ReaderPowerSavingMode : MobileReaderPowerSavingMode.System,
        PdfZoom = double.IsFinite(value.PdfZoom) ? Math.Clamp(value.PdfZoom, 1, 4) : 1,
        PdfDisplayMode = value.PdfDisplayMode is >= 0 and <= 2 ? value.PdfDisplayMode : 0,
        PdfFitMode = value.PdfFitMode is 0 or 1 ? value.PdfFitMode : 0,
        MangaDirection = value.MangaDirection is >= 0 and <= 2 ? value.MangaDirection : 0,
        ReaderFontScale = double.IsFinite(value.ReaderFontScale)
            ? Math.Clamp(value.ReaderFontScale, ReaderLayoutDefaults.MinFontScale, ReaderLayoutDefaults.MaxFontScale) : 1.35,
        ReaderFontFamily = NormalizeReaderFontFamily(value.ReaderFontFamily),
        ReaderLatinFontFamily = NormalizeReaderLatinFontFamily(value.ReaderLatinFontFamily),
        ReaderLineHeight = double.IsFinite(value.ReaderLineHeight) ? Math.Clamp(value.ReaderLineHeight, 1.2, 3) : 1.75,
        ReaderParagraphSpacing = double.IsFinite(value.ReaderParagraphSpacing)
            ? Math.Clamp(value.ReaderParagraphSpacing, 0, ReaderLayoutDefaults.MaxParagraphSpacing) : ReaderLayoutDefaults.DefaultParagraphSpacing,
        ReaderBodyPadding = double.IsFinite(value.ReaderBodyPadding) ? Math.Clamp(value.ReaderBodyPadding, 16, 64) : 24,
        ReaderHighlightStyle = MobilePreferences.NormalizeReaderHighlightStyle(value.ReaderHighlightStyle),
        ReaderHighlightColor = MobilePreferences.NormalizeReaderHighlightColor(value.ReaderHighlightColor),
        PreviewTxtImport = value.PreviewTxtImport,
        ManualBookReadingStatuses = MobilePreferences.NormalizeManualReadingStatuses(value.ManualBookReadingStatuses),
        OnboardingAccepted = value.OnboardingAccepted
    };

    private static string NormalizeReaderLatinFontFamily(string? value)
    {
        var family = value?.Trim();
        if (string.IsNullOrEmpty(family)
            || string.Equals(family, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (family.StartsWith(ReaderFontDefaults.SystemFamilyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = family[ReaderFontDefaults.SystemFamilyPrefix.Length..].Trim();
            return name.Length == 0 ? "auto" : ReaderFontDefaults.SystemFamilyPrefix + name;
        }

        return "auto";
    }

    private static string NormalizeReaderFontFamily(string? value)
    {
        var family = value?.Trim();
        if (string.IsNullOrEmpty(family)) return ReaderFontDefaults.DefaultFamily;

        if (string.Equals(family, ReaderFontDefaults.BundledFamily, StringComparison.OrdinalIgnoreCase))
        {
            return ReaderFontDefaults.DefaultFamily;
        }

        if (family.StartsWith(ReaderFontDefaults.SystemFamilyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = family[ReaderFontDefaults.SystemFamilyPrefix.Length..].Trim();
            return IsSafeReaderFontName(name)
                ? ReaderFontDefaults.SystemFamilyPrefix + name
                : ReaderFontDefaults.DefaultFamily;
        }

        if (family.StartsWith(ReaderFontDefaults.CatalogFamilyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = family[ReaderFontDefaults.CatalogFamilyPrefix.Length..].Trim();
            return ReaderFontCatalog.Find(id) is { } entry
                ? ReaderFontDefaults.CatalogFamilyPrefix + entry.Id
                : ReaderFontDefaults.DefaultFamily;
        }

        return IsSafeReaderFontName(family) ? family : ReaderFontDefaults.DefaultFamily;
    }

    private static bool IsSafeReaderFontName(string value) =>
        value.Length is > 0 and <= 160
        && value.All(character => char.IsLetterOrDigit(character)
            || character is ' ' or '-' or '.' or '_' or '+' or ',');

    private static void Publish(object value)
    {
        var json = Serialize(value);
        if (Dispatcher.UIThread.CheckAccess()) PublishEvent(json);
        else Dispatcher.UIThread.Post(() => PublishEvent(json));
    }

    [DllImport("__Internal", EntryPoint = "lz_ios_set_ui_language")]
    private static extern void SetUiLanguage([MarshalAs(UnmanagedType.LPUTF8Str)] string language);
    [DllImport("__Internal", EntryPoint = "lz_ios_set_ui_font")]
    private static extern void SetUiFont(int font);
    [DllImport("__Internal", EntryPoint = "lz_ios_create_shell")]
    private static extern IntPtr CreateShell(IntPtr handler, IntPtr readerController);
    [DllImport("__Internal", EntryPoint = "lz_ios_release_shell")]
    private static extern void ReleaseShell(IntPtr pointer);
    [DllImport("__Internal", EntryPoint = "lz_ios_resolve_request")]
    private static extern void ResolveRequest(long id, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport("__Internal", EntryPoint = "lz_ios_publish_event")]
    private static extern void PublishEvent([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
}
#endif
