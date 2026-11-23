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

    internal static bool IsAttached => _current is not null;

    private IOSNativeFrontend(MobileShell shell, AvaloniaView readerSurface, UIViewController readerController, UIWindow window)
    {
        _shell = shell;
        _readerSurface = readerSurface;
        _readerController = readerController;
        _window = window;
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
            _reader?.Resume();
            _ = shell.CheckDevicesIfDueAsync();
        }));
        _observers.Add(UIApplication.Notifications.ObserveWillResignActive((_, _) =>
        {
            if (_reader is not { } reader)
            {
                _widgetSnapshots.Schedule();
                return;
            }
            if (shell.Services.BackgroundWork is { } background) background.Run(() => PauseReaderAndPublishWidgetsAsync(reader));
            else _ = PauseReaderAndPublishWidgetsAsync(reader);
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

    private Task PublishWidgetSnapshotAsync(CancellationToken cancellationToken) =>
        WidgetSnapshotPublisher.PublishAsync(
            _shell.Modules.Paths,
            _shell.Modules.Library.Books,
            _shell.Modules.Reader.Data,
            _shell.Preferences.Current.ColorfulMode,
            (int)_shell.Preferences.Current.ThemeMode,
            IOSWidgetPaths.Resolve(_shell.Modules.Paths),
            cancellationToken,
            uiFont: (int)_shell.Preferences.Current.UiFont);

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
            Dispatcher.UIThread.Post(() => frontend.OpenUrl(url));
        else
            _pendingUrl = url;
        return true;
    }

    private void OpenUrl(Uri url)
    {
        switch (url.Host.ToLowerInvariant())
        {
            case "read":
                var id = url.AbsolutePath.Trim('/');
                if (Guid.TryParse(id, out var bookId))
                    Publish(Data(("type", "openBook"), ("bookId", bookId.ToString())));
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
                var preferences = Read(args.GetProperty("preferences"), _shell.Preferences.Current, IOSNativeJsonContext.Default.MobilePreferences);
                await _readerGate.WaitAsync(token);
                try
                {
                    var previous = _shell.Preferences.Current;
                    _shell.Preferences.UpdateAndSave(_ => NormalizePreferences(preferences));
                    _shell.UiLanguage?.Refresh();
                    _shell.ApplyThemeMode();
                    _widgetSnapshots.Schedule();
                    // Language and interface-font changes refresh chrome through the event;
                    // they must not reflow or reopen the current book.
                    if (_reader is { } currentReader
                        && (previous with { UiLanguage = _shell.Preferences.Current.UiLanguage, UiFont = _shell.Preferences.Current.UiFont }) != _shell.Preferences.Current)
                        await currentReader.NativeApplySettingsAsync();
                }
                finally { _readerGate.Release(); }
                return _shell.Preferences.Current;
            case "reader.open":
                _readerCancellation?.Cancel();
                await _readerGate.WaitAsync(token);
                try
                {
                    await CloseReaderAsync();
                    var book = await _shell.Modules.Library.Books.GetBookAsync(Id(args, "bookId"), token)
                        ?? throw new InvalidOperationException(UiText.Get("书籍已不存在。"));
                    var jump = args.TryGetProperty("location", out var location)
                        ? location.Deserialize(IOSNativeJsonContext.Default.ReaderProgressRow) : null;
                    _readerCancellation = new CancellationTokenSource();
                    _readerSessionId = Text(args, "sessionId");
                    _reader = new ReaderPage(book, _shell.Modules, _shell, jump);
                    var sessionId = _readerSessionId;
                    _reader.NativeStateChanged += state => Publish(Data(("type", "reader"), ("sessionId", sessionId), ("reader", state)));
                    _reader.NativeDialogRequested += dialog => Publish(Data(("type", "readerDialog"), ("sessionId", sessionId), ("dialog", dialog)));
                    _reader.NativeReaderMediaRequested += media => Publish(Data(("type", "readerMedia"), ("sessionId", sessionId), ("media", media)));
                    _reader.NativeFootnoteRequested += footnote => Publish(Data(("type", "readerFootnote"), ("sessionId", sessionId), ("footnote", footnote)));
                    _reader.NativeSearchBatchChanged += search => Publish(Data(("type", "readerSearch"), ("sessionId", sessionId), ("search", search)));
                    _reader.UseNativeFrontend();
                    _reader.NativeSetSurfaceInsets(_nativeSurfaceInsets);
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
                    _nativeSurfaceInsets = new Thickness(
                        ReadDouble(args, "left"),
                        ReadDouble(args, "top"),
                        ReadDouble(args, "right"),
                        ReadDouble(args, "bottom"));
                    _reader?.NativeSetSurfaceInsets(_nativeSurfaceInsets);
                }
                finally { _readerGate.Release(); }
                return Data();
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

    private async Task CloseReaderAsync()
    {
        if (_reader is not { } reader) return;
        reader.NativeCancelSearch();
        await reader.PauseAsync();
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
        var continueBookId = dashboard.Books.Where(item => item.IsInLibrary && item.ProgressPercent < 99.5)
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
        ("syncStatus", syncStatus is { } status ? (int?)status : null),
        ("collectionIds", book.CollectionIds.ToArray()), ("updatedAt", book.UpdatedAt), ("createdAt", book.CreatedAt),
        ("description", book.Description), ("series", book.Series),
        ("seriesIndex", IOSNativeBookLibraryPolicy.FiniteSeriesIndex(book.SeriesIndex)),
        ("fileTitles", book.Files.Select(file => Path.GetFileNameWithoutExtension(file.RelativePath)).ToArray()),
        ("publisher", book.Publisher),
        ("publishDate", book.PublishDate), ("isbn", book.Isbn), ("pageCount", book.PageCount),
        ("binding", book.Binding), ("tags", book.Tags), ("category", book.Category), ("onlineRating", book.OnlineRating));

    private async Task ImportBooksAsync(CancellationToken token)
    {
        var files = await _shell.Services.ImportSource.PickFilesAsync(token);
        try
        {
            await ImportFileBatchAsync(files, fromExternalOpen: false, token);
        }
        finally { _shell.Services.ImportSource.ReleaseFiles(files); }
    }

    private async Task ImportExternalFilesAsync(IReadOnlyList<string> files)
    {
        try
        {
            await ImportFileBatchAsync(files, fromExternalOpen: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally { IOSFileImportSource.DeleteFiles(files); }
    }

    private async Task ImportFileBatchAsync(IReadOnlyList<string> files, bool fromExternalOpen, CancellationToken token)
    {
        if (files.Count == 0) return;
        if (fromExternalOpen && files.All(path => Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase)))
        {
            await DictionaryGate.WaitAsync(token);
            try
            {
                var imported = await ImportDictionaryFilesAsync(_shell.Modules.Library.Dictionary, files, token);
                if (imported.Count == 1)
                    _shell.ShowToast(UiText.Get("已导入词典：{0}", imported[0]));
                else if (imported.Count > 1)
                    _shell.ShowToast(UiText.Get("已导入 {0} 本词典", imported.Count));
            }
            finally { DictionaryGate.Release(); }
            return;
        }
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".epub", ".pdf", ".mobi", ".azw3", ".txt" };
        if (files.Any(path => !supported.Contains(Path.GetExtension(path))))
            throw new InvalidOperationException(UiText.Get("请选择 EPUB、PDF、MOBI、AZW3 或 TXT 文件。"));
        // TXT files are imported directly with automatic encoding, heading and
        // volume detection so TOC chapters are ready to read without an extra confirmation gate.
        var rules = files
            .Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, _ => TxtImportRules.Default, StringComparer.OrdinalIgnoreCase);
        using var operation = BeginOperation();
        await _shell.Modules.Library.Books.InitializeAsync(token);
        var result = await Task.Run(() => _shell.Modules.Library.Books.ImportAsync(files,
            cancellationToken: token, txtImportRules: rules,
            conflictResolver: conflict => ChooseImportConflictAsync(conflict, token)), token);
        _shell.ShowToast(result.FailureCount > 0
            ? UiText.Get("已导入 {0} 本，{1} 个文件失败", result.AddedCount, result.FailureCount)
            : result.AddedCount == 0 ? UiText.Get("所选书籍已在书库中") : UiText.Get("已导入 {0} 本", result.AddedCount));
        if (fromExternalOpen)
        {
            Publish(Data(("type", "libraryChanged")));
            if (result.Items.FirstOrDefault(item => item.Succeeded && item.BookId is not null)?.BookId is { } bookId
                && files.Count == 1)
                Publish(Data(("type", "openBook"), ("bookId", bookId.ToString())));
        }
        // Successful items are already committed. A partial failure must
        // not abort the native button's subsequent library refresh.
        if (IOSNativeBookLibraryPolicy.ImportFailedCompletely(result)
            && result.Items.FirstOrDefault(item => !item.Succeeded) is { } failed)
            throw new InvalidOperationException(failed.Message);
    }

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
        ReaderHighlightColor = MobilePreferences.NormalizeReaderHighlightColor(value.ReaderHighlightColor)
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
