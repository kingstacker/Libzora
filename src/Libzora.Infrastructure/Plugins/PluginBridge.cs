using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Libzora.Core;

namespace Libzora.Infrastructure.Plugins;

/// <summary>Window-level services a plugin page may ask the host for.</summary>
public interface IPluginUiHost
{
    bool NetworkEnabled { get; }
    void Close();
    void Notify(string message);
    void SetTitle(string title);
    void OpenExternal(Uri uri);
    void BooksChanged(IReadOnlyCollection<Guid> bookIds);

    /// <summary>Asks the user whether the plugin may call <paramref name="host"/>.</summary>
    Task<bool> RequestNetworkAccessAsync(string host);

    /// <summary>Asks the user to confirm importing a downloaded file.</summary>
    Task<bool> ConfirmImportAsync(string fileName, long sizeBytes);

    /// <summary>Pushes a named event to the page (progress reporting).</summary>
    void RaiseEvent(string name, JsonNode payload);

    /// <summary>Receives one page diagnostic line; ignored outside developer mode.</summary>
    void Log(string level, string message);

    /// <summary>Shows the host file picker; returns user-approved local files.</summary>
    Task<IReadOnlyList<PluginPickedFile>> PickFilesAsync(
        bool allowMultiple,
        IReadOnlyList<string> extensions);

    /// <summary>Shows the host save dialog and writes the bytes.</summary>
    Task<PluginPickedFile?> SaveFileAsync(string suggestedName, byte[] content);

    Task CopyTextAsync(string text);

    Task<string?> ReadTextAsync();
}

/// <summary>A file the user approved through a plugin window's picker.</summary>
public sealed record PluginPickedFile(string Name, string? Path);

/// <summary>
/// Executes the <c>window.libzora</c> calls of one plugin page. Every call is
/// checked against the plugin's manifest permissions, and library access is
/// limited to the books the user opened the page with.
/// </summary>

public sealed partial class PluginBridge : IDisposable
{
    public const int MaximumResponseBytes = 10 * 1024 * 1024;
    public const int MaximumCoverBytes = 10 * 1024 * 1024;
    public const long MaximumImportBytes = 500L * 1024 * 1024;
    private static readonly HashSet<string> ImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".fb2", ".txt", ".djvu", ".cbz", ".cbr",
        ".zip", ".rtf", ".doc", ".docx", ".html", ".htm", ".md", ".lit", ".pdb", ".kfx", ".tcr"
    };
    private const int MaximumRedirects = 5;
    private const int MaximumTextLength = 20_000;
    private const int MaximumFieldLength = 1_000;
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36 Libzora/1.0";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Cookie2", "Content-Length", "Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Expect", "Origin", "Proxy-Authorization", "Proxy-Connection"
    };

    private readonly InstalledPlugin _plugin;
    private readonly PluginPageContext _context;
    private readonly IBookLibraryService _library;
    private readonly AppPaths _paths;
    private readonly PluginStorage _storage;
    private readonly IPluginUiHost _ui;
    private readonly PluginSecrets? _secrets;
    private readonly PluginNetworkGrants _grants;
    private readonly IPluginBookTaskHost? _bookTasks;
    private readonly ReaderDataService? _readerData;
    private readonly IPluginContentHost? _content;
    private readonly IPluginAiHost? _ai;
    private readonly IPluginReaderHost? _reader;
    private readonly HashSet<string> _pickedFiles = new(StringComparer.Ordinal);
    // One prompt per host at a time: a page fetching many images can ask for
    // the same origin concurrently, and each naive request would open its own
    // dialog even after the first one was allowed.
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _networkApprovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _taskGate = new(1, 1);
    private CancellationTokenSource? _taskCancellation;
    private readonly HashSet<Guid> _allowedBooks;
    private bool _disposed;

    /// <summary>
    /// True while a book task (translation, pinyin or model download) runs.
    /// The window asks before closing so a task is not cancelled by accident.
    /// </summary>
    public bool HasRunningTask => _taskGate.CurrentCount == 0;

    public PluginBridge(
        InstalledPlugin plugin,
        PluginPageContext context,
        IBookLibraryService library,
        AppPaths paths,
        PluginStorage storage,
        IPluginUiHost ui,
        PluginSecrets? secrets = null,
        HttpMessageHandler? handler = null,
        IPluginBookTaskHost? bookTasks = null,
        ReaderDataService? readerData = null,
        IPluginContentHost? content = null,
        IPluginAiHost? ai = null,
        IPluginReaderHost? reader = null)
    {
        _plugin = plugin;
        _context = context;
        _library = library;
        _paths = paths;
        _storage = storage;
        _ui = ui;
        _secrets = secrets;
        _bookTasks = bookTasks;
        _readerData = readerData;
        _content = content;
        _ai = ai;
        _reader = reader;
        _grants = new PluginNetworkGrants(Path.Combine(paths.PluginData, plugin.Id));
        _allowedBooks = context.BookIds.ToHashSet();
        // Cookies persist for the lifetime of the page so sites that set a
        // visitor cookie on the first response accept follow-up requests.
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All
        }, disposeHandler: true)
        {
            // Large book downloads can take minutes; the plugin window's
            // lifetime token still cancels the transfer when it closes.
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Handles one raw message from the page. Returns the request id and the
    /// reply payload, or null when the message is not a bridge request.
    /// </summary>
}
