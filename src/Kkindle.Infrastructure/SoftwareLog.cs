using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kkindle.Infrastructure;

/// <summary>
/// Bounded, sanitized diagnostic log that can be attached to a feedback report.
/// Only explicit failure events are recorded; book titles, reading content,
/// account details, file paths and secrets are removed before anything reaches
/// disk. The logger never throws and never blocks application work.
/// </summary>
public static class SoftwareLog
{
    public const string FileName = "software.log";
    public const string ArchiveFileName = "software.1.log";
    public const int MaximumFileBytes = 512 * 1024;
    public const int MaximumFeedbackBytes = 256 * 1024;

    private const int MaximumMessageCharacters = 400;
    private const int MaximumContextValueCharacters = 120;
    private const int MaximumStackTraceCharacters = 4096;
    private const int MaximumExceptionDepth = 4;
    private const string Redacted = "<redacted>";

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlPattern = new(
        @"\b[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AuthorizationPattern = new(
        @"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SecretPattern = new(
        @"(?i)\b(api[_\-]?key|access[_\-]?key|secret[_\-]?key|password|passwd|token|authorization)\s*[=:]\s*[^\s,;&]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedPathPattern = new(
        @"(?<q>[""'])(?<v>[^""'\r\n]{1,500})\k<q>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsUserPathPattern = new(
        @"(?i)\b[A-Z]:\\Users\\[^\\\r\n""'<>]+(?:\\[^\r\n""'<>]*)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PosixHomePathPattern = new(
        @"(?<![\w:])/(?:Users|home)/[^/\r\n""'<>]+(?:/[^\r\n""'<>]*)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsAbsolutePathPattern = new(
        @"(?i)\b[A-Z]:\\(?:[^\\\r\n""'<>]+\\)*[^\r\n""'<>]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PosixAbsolutePathPattern = new(
        @"(?<![\w:/])/(?:[^\s""'<>/]+/)*[^\s""'<>/]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LongTokenPattern = new(
        @"\b[A-Za-z0-9+/_\-]{32,}={0,2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IPv4Pattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BookTitlePattern = new(
        @"《[^》\r\n]{1,200}》",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BookFilePattern = new(
        @"(?i)\b[^\s/\\:""'<>]+\.(?:epub|azw3|mobi|pdf|fb2|cbz|cbr|txt|doc|docx|rtf|html?|xhtml)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EventNamePattern = new(
        @"^[a-z][a-z0-9]*(?:\.[a-z0-9]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ContextKeyPattern = new(
        @"^[a-z][a-z0-9._\-]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? _filePath;
    private static string? _archivePath;
    private static string? _configuredDirectory;
    private static string _appRoot = "";
    private static string? _homeDirectory;
    private static string _appVersion = "unknown";
    private static string _platform = "unknown";
    private static string _platformVersion = "unknown";
    private static bool _sessionHeaderWritten;

    private const string Preamble =
        "# Kkindle software log / 软件日志\n" +
        "# Sanitized: application failures and technical context only. No accounts, emails, books,\n" +
        "# notes, reading content, file paths, or secrets are included.\n";

    /// <summary>True once <see cref="Configure"/> has selected a writable log directory.</summary>
    public static bool IsAvailable
    {
        get { lock (Gate) return _filePath is not null; }
    }

    /// <summary>
    /// Selects the log location and writes one session header. Safe to call more
    /// than once; the header is emitted once per directory and process.
    /// </summary>
    public static void Configure(AppPaths paths, string? appVersion = null, string? platform = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string directory;
        string root;
        try
        {
            directory = Path.GetFullPath(paths.Logs);
            root = Path.GetFullPath(paths.Root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _filePath = null;
                _archivePath = null;
                _configuredDirectory = null;
                return;
            }

            _filePath = Path.Combine(directory, FileName);
            _archivePath = Path.Combine(directory, ArchiveFileName);
            _appRoot = root;
            _homeDirectory = TryGetHomeDirectory();
            _appVersion = Bound(appVersion?.Trim() ?? "unknown", 32);
            _platform = Bound(string.IsNullOrWhiteSpace(platform) ? CurrentPlatformName() : platform.Trim(), 16);
            _platformVersion = Bound(Environment.OSVersion.Version.ToString(), 64);
            if (!string.Equals(_configuredDirectory, directory, StringComparison.Ordinal))
            {
                _configuredDirectory = directory;
                _sessionHeaderWritten = false;
            }

            if (_sessionHeaderWritten) return;
            _sessionHeaderWritten = true;
            AppendLineLocked(
                $"[{DateTimeOffset.UtcNow:O}] META session.start app={_appVersion} " +
                $"platform={_platform} os={_platformVersion}\n");
        }
    }

    /// <summary>Records an application failure together with a sanitized exception message.</summary>
    public static void Error(string eventName, Exception? exception = null,
        params (string Name, object? Value)[] context) =>
        Write("ERROR", eventName, exception, includeMessage: true, context);

    /// <summary>
    /// Records an application failure without the exception message. Use this
    /// when a failure can contain user content (AI responses, plugin output,
    /// book files, sync object names).
    /// </summary>
    public static void ErrorWithoutMessage(string eventName, Exception? exception = null,
        params (string Name, object? Value)[] context) =>
        Write("ERROR", eventName, exception, includeMessage: false, context);

    /// <summary>Records a recoverable problem event without an exception.</summary>
    public static void Warning(string eventName, params (string Name, object? Value)[] context) =>
        Write("WARN", eventName, null, includeMessage: false, context);

    /// <summary>
    /// Returns the sanitized tail of the current and immediately previous log
    /// files, capped at <paramref name="maximumBytes"/> of UTF-8. Returns null
    /// when logging is unavailable or no log exists yet.
    /// </summary>
    public static string? CaptureFeedbackLog(int maximumBytes = MaximumFeedbackBytes)
    {
        maximumBytes = Math.Clamp(maximumBytes, 4096, MaximumFeedbackBytes);
        string? current;
        string? previous;
        string appVersion;
        string platform;
        string platformVersion;
        lock (Gate)
        {
            if (_filePath is null) return null;
            current = TryReadAllText(_filePath);
            previous = _archivePath is null ? null : TryReadAllText(_archivePath);
            appVersion = _appVersion;
            platform = _platform;
            platformVersion = _platformVersion;
        }

        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(previous)) return null;
        var header =
            "Kkindle software log / 软件日志\n" +
            $"app={appVersion} platform={platform} os={platformVersion}\n" +
            $"captured={DateTimeOffset.UtcNow:O}\n" +
            "Sanitized: failures and technical context only; no accounts, emails, books, notes, reading content, paths, or secrets.\n\n";
        var bodyBudget = Math.Max(1024, maximumBytes - Utf8.GetByteCount(header) - 64);
        var body = TakeUtf8Tail((previous ?? "") + (current ?? ""), bodyBudget);
        return header + body;
    }

    private static void Write(string level, string eventName, Exception? exception, bool includeMessage,
        (string Name, object? Value)[] context)
    {
        if (!EventNamePattern.IsMatch(eventName)) return;

        var line = new StringBuilder(256);
        line.Append('[').Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append("] ")
            .Append(level).Append(' ').Append(eventName);
        foreach (var (name, value) in context)
        {
            if (!ContextKeyPattern.IsMatch(name)) continue;
            var text = Bound(SanitizeSingleLine(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
                MaximumContextValueCharacters);
            line.Append(' ').Append(name).Append('=').Append(text.Length == 0 ? "\"\"" : text);
        }
        line.Append('\n');
        if (exception is not null) AppendException(line, exception, includeMessage);
        AppendLine(line.ToString());
    }

    private static void AppendException(StringBuilder builder, Exception exception, bool includeMessage)
    {
        var types = new List<string>(MaximumExceptionDepth);
        for (var current = exception; current is not null && types.Count < MaximumExceptionDepth;
             current = current.InnerException)
            types.Add(current.GetType().FullName ?? current.GetType().Name);
        builder.Append("  exception=").Append(string.Join(" <- ", types))
            .Append(" hresult=0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
            .Append('\n');

        if (includeMessage && !string.IsNullOrWhiteSpace(exception.Message))
            builder.Append("  message=")
                .Append(Bound(SanitizeSingleLine(exception.Message), MaximumMessageCharacters))
                .Append('\n');

        var stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack)) return;
        var safe = Bound(Sanitize(stack), MaximumStackTraceCharacters);
        foreach (var rawLine in safe.Split('\n'))
        {
            var frame = rawLine.TrimEnd('\r');
            if (frame.Length == 0) continue;
            builder.Append("  ").Append(frame.Trim()).Append('\n');
        }
    }

    private static void AppendLine(string line)
    {
        lock (Gate) AppendLineLocked(line);
    }

    private static void AppendLineLocked(string line)
    {
        if (_filePath is null) return;
        try
        {
            EnsurePreambleLocked();
            var length = Utf8.GetByteCount(line);
            var existing = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0;
            if (existing > 0 && existing + length > MaximumFileBytes && _archivePath is not null)
            {
                File.Move(_filePath, _archivePath, overwrite: true);
                EnsurePreambleLocked();
            }
            File.AppendAllText(_filePath, line, Utf8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            // Logging must never interfere with the application.
        }
    }

    private static void EnsurePreambleLocked()
    {
        if (_filePath is null) return;
        if (File.Exists(_filePath) && new FileInfo(_filePath).Length > 0) return;
        File.AppendAllText(_filePath, Preamble, Utf8);
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Utf8);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetHomeDirectory()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) || home.Length <= 2 ? null : Path.GetFullPath(home);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string CurrentPlatformName() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsIOS() ? "ios"
        : OperatingSystem.IsAndroid() ? "android"
        : "linux";

    /// <summary>Sanitizes free-form text before it can be written to disk.</summary>
    internal static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Replace("\r\n", "\n").Replace('\r', '\n');

        // Known installation and home paths first, so they keep useful names.
        if (_appRoot.Length > 2)
            text = text.Replace(_appRoot, "<app>", PathComparison);
        if (_homeDirectory is { Length: > 2 } home
            && !string.Equals(home, _appRoot, PathComparison))
            text = text.Replace(home, "<home>", PathComparison);

        text = UrlPattern.Replace(text, SanitizeUrl);
        text = AuthorizationPattern.Replace(text, "$1 <redacted>");
        text = SecretPattern.Replace(text, "$1=<redacted>");
        text = EmailPattern.Replace(text, "<email>");
        text = QuotedPathPattern.Replace(text, RedactQuotedPath);
        text = BookTitlePattern.Replace(text, "《<title>》");
        text = BookFilePattern.Replace(text, "<book-file>");
        if (_homeDirectory is { Length: > 2 })
            text = text.Replace(_homeDirectory, "<home>", PathComparison);
        text = WindowsUserPathPattern.Replace(text, "<path>");
        text = PosixHomePathPattern.Replace(text, "<path>");
        text = WindowsAbsolutePathPattern.Replace(text, "<path>");
        text = PosixAbsolutePathPattern.Replace(text, "<path>");
        text = GuidPattern.Replace(text, "<id>");
        text = LongTokenPattern.Replace(text, Redacted);
        text = IPv4Pattern.Replace(text, "<ip>");
        return StripControlCharacters(text);
    }

    private static string StripControlCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character) && character is not ('\n' or '\t')) continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string SanitizeUrl(Match match)
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return "<url>";
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{authority}";
    }

    private static string RedactQuotedPath(Match match)
    {
        var value = match.Groups["v"].Value;
        var looksLikePath = value.Contains('/') || value.Contains('\\')
            || value.StartsWith('~') || BookFilePattern.IsMatch(value);
        return looksLikePath ? "<path>" : match.Value;
    }

    private static string SanitizeSingleLine(string value) =>
        CollapseWhitespace(Sanitize(value).Replace('\n', ' ').Replace('\t', ' '));

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;
        foreach (var character in value)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (previousWasSpace) continue;
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
            previousWasSpace = isSpace;
        }
        return builder.ToString().Trim();
    }

    private static string Bound(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        return value[..maximum] + "…";
    }

    private static string TakeUtf8Tail(string value, int maximumBytes)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length <= maximumBytes) return value;
        var start = bytes.Length - maximumBytes;
        while (start < bytes.Length && bytes[start] != (byte)'\n') start++;
        if (start < bytes.Length) start++;
        return Utf8.GetString(bytes, start, bytes.Length - start);
    }
}
