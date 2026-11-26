using System.Text.Json.Serialization;

namespace Libzora.Core;

public enum LibraryReadingStatus { Unread = 0, Reading = 1, Finished = 2 }

public enum LibrarySortMode
{
    UpdatedDescending = 0,
    TitleAscending = 1,
    AuthorAscending = 2,
    CreatedDescending = 3,
    ProgressDescending = 4
}

public sealed record LibraryPageResult(IReadOnlyList<Book> Books, int TotalCount);

public sealed record LibraryFilterOptions(
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> Categories);

public sealed record LibraryCollectionSummary(
    BookCollection Collection,
    int BookCount,
    IReadOnlyList<string> CoverPaths);

public sealed record LibraryFileMatch(string RelativePath, string Sha256);

public sealed record LibraryBookMatch(
    Guid Id,
    string Title,
    string Authors,
    IReadOnlyList<LibraryFileMatch> Files)
{
    public string? Isbn { get; init; }
}

public sealed record LibraryBookDisplayInfo(Guid Id, string Title, string? CoverPath, bool IsDeleted = false);

public sealed record AppSettings
{
    public const string DefaultEmbeddingModelId = "BAAI/bge-small-zh-v1.5";
    public const string DefaultPinyinEngineId = PinyinBookEngineCatalog.DotNetG2PId;

    public string UiLanguage { get; init; } = UiText.DetectSystemLanguage();
    public AppTheme MainTheme { get; init; } = AppTheme.Classic;
    public AppUiFont MainUiFont { get; init; } = AppUiFont.KingHwaOldSong;
    public bool OnboardingCompleted { get; init; }
    public bool DeferTtsInstallationUntilUse { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultDeviceModel { get; init; }
    public string PreferredOpenFormat { get; init; } = "epub";
    public string CalibrePath { get; init; } = string.Empty;
    public bool AutoBackupEnabled { get; init; }
    public bool DeleteAnnotationsWhenDeletingBook { get; init; }
    public bool AutoGenerateEpubAndAzw3OnImport { get; init; }
    public bool CollectionsMutuallyExclusive { get; init; } = true;
    // The library surface is restored before the first window is shown so a
    // restart does not unexpectedly fall back to the grid view.
    public string LibraryViewMode { get; init; } = "Grid";
    public string LibrarySortMode { get; init; } = nameof(Libzora.Core.LibrarySortMode.UpdatedDescending);
    public string DeviceLibraryViewMode { get; init; } = "Grid";
    public string DeviceBookSortMode { get; init; } = "title";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoGenerateAzw3OnImport { get; init; }
    public int AutoBackupRetention { get; init; } = 5;
    public bool AiEnabled { get; init; } = true;
    public string ReaderAiReasoningDepth { get; init; } = "auto";
    public bool NetworkEnabled { get; init; } = true;
    public bool SendToKindleWebEnabled { get; init; } = true;
    public string EmbeddingModelId { get; init; } = DefaultEmbeddingModelId;
    public string PinyinEngineId { get; init; } = DefaultPinyinEngineId;
    public bool AutoUpdateCheckEnabled { get; init; } = true;
    public bool DevelopmentUpdateCheckEnabled { get; init; }

    // Developer mode exposes plugin diagnostics (DevTools, page log panel,
    // reload, folder install). It is off by default and never shipped enabled.
    public bool PluginDeveloperMode { get; init; }

    // Plugins may declare a background script (contributes.background). It
    // runs headless, at startup and on its interval, only while this is on.
    public bool PluginBackgroundTasksEnabled { get; init; } = true;

    // Update checks run at most once per calendar day; the timestamp and the
    // discovered update summary persist so the title-bar badge survives restarts
    // without extra network calls.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? LastAutoUpdateCheckAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateReleaseNotes { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdateReleaseNotesEnglish { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingUpdatePackagePath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? PendingUpdateDownloadedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTimeOffset? PendingUpdateInstallStartedAt { get; init; }

    public bool AutoConnectDevice { get; init; } = true;
    public bool CompareKindleLibraryEnabled { get; init; } = true;
    public bool GridGalleryDisplay { get; init; }
    public bool StackBookSeries { get; init; } = true;
    public bool ShowSyncStatusIcon { get; init; } = true;
    public bool ShowLibraryPresenceIcon { get; init; } = true;
    public bool ShowUnearnedReadingMedals { get; init; }
    public bool ShowReadingMedals { get; init; } = true;
    public bool ReadingMaterialsCollapsedByDefault { get; init; } = true;
    // Null is reserved for settings files created before the compact reader
    // toolbar existed. Preserve their old pinned TTS action once, while new
    // profiles start with an uncluttered toolbar.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReaderPinnedTools { get; init; }
    public string LastKindleSendMethod { get; init; } = "web";
    public string ReaderAiServiceProfileId { get; init; } = "default";
    public bool PinyinContextMenuEnabled { get; init; } = true;
    public bool PinyinLocalOnly { get; init; } = true;
    public BookTranslationSettings Translation { get; init; } = new();
    public McpServerSettings McpServer { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReaderVerticalDebugBoxesEnabled { get; init; }
    public ReaderLayoutSettings DefaultReaderLayout { get; init; } = new();
    public ReaderAppearanceSettings ReaderAppearance { get; init; } = new();
    public ReaderProgressDisplayMode ReaderProgressDisplay { get; init; } = ReaderProgressDisplayMode.Chapters;
    // Reader navigation chrome stays global, including with a per-book layout.
    public double ReaderTocFontSize { get; init; } = ReaderTocDefaults.DefaultFontSize;
    /// <summary>Per-book comic/manga preferences keyed by bookFileId ("N" format).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, MangaBookSettings>? MangaBooks { get; init; }

    public bool TryGetMangaSettings(Guid? bookFileId, out MangaBookSettings settings)
    {
        if (bookFileId is { } id && MangaBooks is { Count: > 0 } map
            && map.TryGetValue(id.ToString("N"), out var saved))
        {
            settings = MangaBookSettings.Normalize(saved);
            return true;
        }

        settings = new MangaBookSettings();
        return false;
    }

    public AppSettings WithMangaSettings(Guid? bookFileId, MangaBookSettings settings)
    {
        if (bookFileId is not { } id) return this;
        var normalized = MangaBookSettings.Normalize(settings);
        var updated = MangaBooks is not null
            ? new Dictionary<string, MangaBookSettings>(MangaBooks, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, MangaBookSettings>(StringComparer.OrdinalIgnoreCase);
        var key = id.ToString("N");
        updated[key] = normalized;
        while (updated.Count > 256)
        {
            var oldest = updated.Keys.First();
            updated.Remove(oldest);
        }

        return this with { MangaBooks = updated };
    }

    private static Dictionary<string, MangaBookSettings>? NormalizeMangaBooks(
        Dictionary<string, MangaBookSettings>? books)
    {
        if (books is not { Count: > 0 }) return null;
        var normalized = new Dictionary<string, MangaBookSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in books)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value is null) continue;
            normalized[entry.Key.Trim()] = MangaBookSettings.Normalize(entry.Value);
            if (normalized.Count >= 256) break;
        }
        return normalized.Count > 0 ? normalized : null;
    }

    public static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        var preferred = (settings.PreferredOpenFormat ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (preferred is not ("epub" or "pdf" or "azw3" or "mobi")) preferred = "epub";
        var libraryViewMode = (settings.LibraryViewMode ?? string.Empty).Trim();
        var reasoningDepth = (settings.ReaderAiReasoningDepth ?? string.Empty).Trim().ToLowerInvariant();
        if (reasoningDepth is not ("auto" or "low" or "medium" or "high" or "max")) reasoningDepth = "auto";
        if (!libraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase)
            && !libraryViewMode.Equals("Collections", StringComparison.OrdinalIgnoreCase))
            libraryViewMode = "Grid";
        else
            libraryViewMode = libraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase)
                ? "List"
                : "Collections";
        var librarySortMode = Enum.GetNames<global::Libzora.Core.LibrarySortMode>()
            .FirstOrDefault(name => name.Equals(
                (settings.LibrarySortMode ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase))
            ?? nameof(global::Libzora.Core.LibrarySortMode.UpdatedDescending);
        var deviceLibraryViewMode = (settings.DeviceLibraryViewMode ?? string.Empty).Trim();
        if (!deviceLibraryViewMode.Equals("Grid", StringComparison.OrdinalIgnoreCase)
            && !deviceLibraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase))
            deviceLibraryViewMode = "Grid";
        else
            deviceLibraryViewMode = deviceLibraryViewMode.Equals("List", StringComparison.OrdinalIgnoreCase)
                ? "List"
                : "Grid";
        var deviceBookSortMode = (settings.DeviceBookSortMode ?? string.Empty).Trim().ToLowerInvariant();
        if (deviceBookSortMode is not ("title" or "author" or "modified" or "size"))
            deviceBookSortMode = "title";
        return settings with
        {
            UiLanguage = UiText.NormalizeLanguage(settings.UiLanguage),
            MainTheme = Enum.IsDefined(settings.MainTheme) ? settings.MainTheme : AppTheme.Classic,
            MainUiFont = Enum.IsDefined(settings.MainUiFont) ? settings.MainUiFont : AppUiFont.KingHwaOldSong,
            ReaderPinnedTools = NormalizeReaderPinnedTools(settings.ReaderPinnedTools, settings.OnboardingCompleted),
            LastKindleSendMethod = NormalizeKindleSendMethod(settings.LastKindleSendMethod),
            ReaderAiServiceProfileId = NormalizeProfileId(settings.ReaderAiServiceProfileId),
            DefaultDeviceModel = string.IsNullOrWhiteSpace(settings.DefaultDeviceModel)
                ? null
                : settings.DefaultDeviceModel.Trim(),
            PreferredOpenFormat = preferred,
            LibraryViewMode = libraryViewMode,
            LibrarySortMode = librarySortMode,
            DeviceLibraryViewMode = deviceLibraryViewMode,
            DeviceBookSortMode = deviceBookSortMode,
            ReaderAiReasoningDepth = reasoningDepth,
            CalibrePath = (settings.CalibrePath ?? string.Empty).Trim(),
            EmbeddingModelId = string.IsNullOrWhiteSpace(settings.EmbeddingModelId)
                ? DefaultEmbeddingModelId
                : settings.EmbeddingModelId.Trim(),
            PinyinEngineId = PinyinBookEngineCatalog.NormalizeId(settings.PinyinEngineId),
            AutoGenerateEpubAndAzw3OnImport = settings.AutoGenerateEpubAndAzw3OnImport
                || settings.AutoGenerateAzw3OnImport,
            AutoGenerateAzw3OnImport = false,
            AutoBackupRetention = Math.Clamp(settings.AutoBackupRetention, 1, 30),
            DefaultReaderLayout = ReaderLayoutDefaults.Normalize(settings.DefaultReaderLayout ?? new ReaderLayoutSettings()),
            ReaderAppearance = ReaderAppearanceSettings.Normalize(settings.ReaderAppearance),
            ReaderProgressDisplay = Enum.IsDefined(settings.ReaderProgressDisplay)
                ? settings.ReaderProgressDisplay
                : ReaderProgressDisplayMode.Chapters,
            ReaderTocFontSize = ReaderTocDefaults.NormalizeFontSize(settings.ReaderTocFontSize),
            MangaBooks = NormalizeMangaBooks(settings.MangaBooks),
            Translation = BookTranslationSettings.Normalize(settings.Translation),
            McpServer = McpServerSettings.Normalize(settings.McpServer)
        };
    }

    private static string NormalizeReaderPinnedTools(string? value, bool onboardingCompleted)
    {
        if (value is null) return onboardingCompleted ? "tts" : string.Empty;
        var requested = value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        string[] allowed = ["tts", "translation", "pinyin", "ai"];
        return string.Join(',', allowed.Where(requested.Contains));
    }

    private static string NormalizeKindleSendMethod(string? value)
    {
        var method = (value ?? string.Empty).Trim().ToLowerInvariant();
        return method is "usb" or "web" or "email" ? method : "web";
    }

    private static string NormalizeProfileId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
}

public sealed record McpServerSettings
{
    public bool Enabled { get; init; } = true;
    public string Transport { get; init; } = "stdio";
    public int HttpPort { get; init; } = 8765;
    public string AccessToken { get; init; } = string.Empty;
    public bool BookLibraryEnabled { get; init; } = true;
    public bool SearchBooksEnabled { get; init; } = true;
    public bool BookMetadataEnabled { get; init; } = true;
    public bool ReadingProgressEnabled { get; init; } = true;
    public bool RecentBooksEnabled { get; init; } = true;
    public bool TagsEnabled { get; init; } = true;
    public bool CollectionsEnabled { get; init; } = true;
    public bool BookFileEnabled { get; init; } = true;
    public bool DeviceListEnabled { get; init; } = true;
    public bool DeviceStatusEnabled { get; init; } = true;
    public bool DeviceLibraryEnabled { get; init; } = true;
    public bool EjectDeviceEnabled { get; init; } = true;
    public bool SendToKindleEnabled { get; init; } = true;
    public bool ConvertBookEnabled { get; init; } = true;
    public bool ImportBookEnabled { get; init; } = true;
    public bool DeleteBookEnabled { get; init; }
    public bool DeleteDeviceBookEnabled { get; init; }
    public bool CollectionManageEnabled { get; init; }
    public bool SearchBookContentEnabled { get; init; } = true;
    public bool AnnotationsEnabled { get; init; } = true;
    public bool BookmarksEnabled { get; init; } = true;
    public bool ReadingDashboardEnabled { get; init; } = true;

    public static McpServerSettings Normalize(McpServerSettings? settings)
    {
        settings ??= new McpServerSettings();
        var transport = (settings.Transport ?? string.Empty).Trim().ToLowerInvariant();
        if (transport is not ("stdio" or "http")) transport = "stdio";

        return settings with
        {
            Transport = transport,
            HttpPort = Math.Clamp(settings.HttpPort, 1, 65535),
            AccessToken = (settings.AccessToken ?? string.Empty).Trim(),
            BookLibraryEnabled = settings.BookLibraryEnabled,
            SearchBooksEnabled = settings.SearchBooksEnabled,
            BookMetadataEnabled = settings.BookMetadataEnabled,
            ReadingProgressEnabled = settings.ReadingProgressEnabled,
            RecentBooksEnabled = settings.RecentBooksEnabled,
            TagsEnabled = settings.TagsEnabled,
            CollectionsEnabled = settings.CollectionsEnabled,
            BookFileEnabled = settings.BookFileEnabled,
            DeviceListEnabled = settings.DeviceListEnabled,
            DeviceStatusEnabled = settings.DeviceStatusEnabled,
            DeviceLibraryEnabled = settings.DeviceLibraryEnabled,
            EjectDeviceEnabled = settings.EjectDeviceEnabled,
            SendToKindleEnabled = settings.SendToKindleEnabled,
            ConvertBookEnabled = settings.ConvertBookEnabled,
            ImportBookEnabled = settings.ImportBookEnabled,
            DeleteBookEnabled = settings.DeleteBookEnabled,
            DeleteDeviceBookEnabled = settings.DeleteDeviceBookEnabled,
            CollectionManageEnabled = settings.CollectionManageEnabled,
            SearchBookContentEnabled = settings.SearchBookContentEnabled,
            AnnotationsEnabled = settings.AnnotationsEnabled,
            BookmarksEnabled = settings.BookmarksEnabled,
            ReadingDashboardEnabled = settings.ReadingDashboardEnabled
        };
    }
}

public sealed record ManagedFont(string Id, string DisplayName, string CssFamily, string RelativePath, DateTimeOffset ImportedAt);
public sealed record DictionaryDefinition(string Id, string Name, string RelativePath, int EntryCount, DateTimeOffset ImportedAt, bool Enabled = true, string? SourceFormat = null);
public sealed record DictionaryEntry(string Term, string Definition, string DictionaryName, string? Html = null, string? Stylesheet = null);
public sealed record PdfPageText(int PageNumber, string Text);
public sealed record PdfSearchResult(int PageNumber, string Excerpt, int MatchIndex);

public enum PlatformDiagnosticStatus
{
    Ready,
    Warning,
    Unavailable
}

public enum PlatformDiagnosticRepairKind
{
    None,
    InstallCalibre,
    InstallTts
}

public sealed record PlatformDiagnostic(
    string Name,
    PlatformDiagnosticStatus Status,
    string Detail,
    PlatformDiagnosticRepairKind RepairKind = PlatformDiagnosticRepairKind.None);

public sealed record ReadingDashboard(
    int BooksStarted,
    int BooksFinished,
    long TotalSeconds,
    double AverageProgress,
    int BookmarkCount,
    int AnnotationCount,
    IReadOnlyList<ReadingDashboardBook> RecentBooks,
    IReadOnlyList<ReadingDashboardDay> DailyReading)
{
    public long ListeningSeconds { get; init; }
    public int StreakDays { get; init; }
    public IReadOnlyList<ReadingDashboardBook> Books { get; init; } = RecentBooks;
    public IReadOnlyList<ReadingDashboardBook> MostReadBooks { get; init; } = RecentBooks;
}

public sealed record ReadingDashboardBook(Guid BookId, Guid BookFileId, double ProgressPercent, long CumulativeSeconds, DateTimeOffset UpdatedAt)
{
    public long ListeningSeconds { get; init; }
    public bool HasFinished { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsInLibrary { get; init; }
}
public sealed record ReadingDashboardDay(DateOnly Date, long ActiveSeconds)
{
    public long ListeningSeconds { get; init; }
}

public enum ReadingMaterialSource { Local, Device, Kindle = Device }

public sealed record ReadingMaterialRecord(
    ReadingMaterialSource Source,
    string BookTitle,
    string Type,
    string Location,
    string Quote,
    string Note,
    DateTimeOffset? UpdatedAt,
    string? SourceDeviceId = null,
    string? SourceDeviceName = null)
{
    public string SourceLabel => Source == ReadingMaterialSource.Local ? UiText.Get("本地书籍")
        : string.IsNullOrWhiteSpace(SourceDeviceName) ? UiText.Get("设备") : SourceDeviceName;
}
