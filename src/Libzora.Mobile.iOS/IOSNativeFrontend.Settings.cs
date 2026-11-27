#if LIBZORA_SWIFT_UI
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile;
using static Libzora.iOSHead.IOSNativeJson;

namespace Libzora.iOSHead;

internal sealed partial class IOSNativeFrontend
{
    private S3SyncSettings? _syncDraft;
    private static readonly SemaphoreSlim DictionaryGate = new(1, 1);
    private static readonly WikipediaLookupService WikipediaLookup = new();

    private async Task<object> DispatchSettingsAsync(string command, JsonElement args, CancellationToken token)
    {
        using var operation = BeginOperation();
        var modules = _shell.Modules;
        if (command is "dictionary.wikipedia" or "dictionary.ai")
            return await DispatchDictionaryAsync(command, args, token);
        if (command.StartsWith("dictionary.", StringComparison.Ordinal))
        {
            await DictionaryGate.WaitAsync(token);
            try { return await DispatchDictionaryAsync(command, args, token); }
            finally { DictionaryGate.Release(); }
        }
        switch (command)
        {
            case "settings.account.get":
                return await modules.Accounts.Service.GetStatusAsync(Flag(args, "validateSession"), token);
            case "settings.account.select":
                return await modules.Accounts.Service.SelectRegionAsync((AccountRegion)args.GetProperty("region").GetInt32(), token);
            case "settings.account.login":
            case "settings.account.register":
                if (args.TryGetProperty("region", out var loginRegion))
                    await modules.Accounts.Service.SelectRegionAsync((AccountRegion)loginRegion.GetInt32(), token);
                return await modules.Accounts.Service.SignInAsync(Text(args, "serverUrl"), Text(args, "username"),
                    Text(args, "password"), command == "settings.account.register", token,
                    command == "settings.account.register" ? Text(args, "email") : null,
                    command == "settings.account.register" ? Text(args, "confirmPassword") : null);
            case "settings.account.apple.login":
                if (args.TryGetProperty("region", out var appleRegion))
                    await modules.Accounts.Service.SelectRegionAsync((AccountRegion)appleRegion.GetInt32(), token);
                return await modules.Accounts.Service.SignInWithAppleAsync(
                    Text(args, "serverUrl"),
                    new AppleSignInCredential
                    {
                        IdentityToken = Text(args, "identityToken"),
                        AuthorizationCode = OptionalText(args, "authorizationCode"),
                        Nonce = OptionalText(args, "nonce"),
                        Email = OptionalText(args, "email"),
                        DisplayName = OptionalText(args, "displayName")
                    },
                    token);
            case "settings.account.apple.bind":
                return await modules.Accounts.Service.BindAppleAsync(
                    new AppleSignInCredential
                    {
                        IdentityToken = Text(args, "identityToken"),
                        AuthorizationCode = OptionalText(args, "authorizationCode"),
                        Nonce = OptionalText(args, "nonce")
                    },
                    token);
            case "settings.account.apple.unbind":
                return await modules.Accounts.Service.UnbindAppleAsync(token);
            case "settings.account.logout":
                var revoked = await modules.Accounts.Service.SignOutAsync(token);
                return Data(("message", revoked ? UiText.Get("已退出登录。") : UiText.Get("已在本机退出；服务器暂时不可用，远端会话将在到期后失效。")));
            case "settings.account.close":
                var confirmation = Text(args, "confirmation").Trim();
                if (string.Equals(confirmation, "DELETE", StringComparison.OrdinalIgnoreCase))
                    confirmation = "停用账号";
                await modules.Accounts.Service.CloseAccountAsync(
                    Text(args, "currentPassword"),
                    confirmation,
                    token);
                return Data(("message", UiText.Get("账号已注销并退出登录。")));
            case "settings.account.reset.request":
                if (args.TryGetProperty("region", out var recoveryRegion))
                    await modules.Accounts.Service.SelectRegionAsync((AccountRegion)recoveryRegion.GetInt32(), token);
                await modules.Accounts.Service.RequestPasswordResetAsync(Text(args, "serverUrl"), Text(args, "username"), token);
                return Data(("message", UiText.Get("若该账号已绑定邮箱，验证码已发送（15 分钟内有效）。")));
            case "settings.account.reset.confirm":
                await modules.Accounts.Service.ResetPasswordAsync(Text(args, "serverUrl"), Text(args, "username"),
                    Text(args, "code"), Text(args, "newPassword"), Text(args, "confirmPassword"), token);
                return Data(("message", UiText.Get("密码已重置，请使用新密码登录。")));
            case "settings.account.email.request":
                await modules.Accounts.Service.RequestEmailVerificationAsync(token);
                return Data(("message", UiText.Get("验证码已发送至注册邮箱（15 分钟内有效）。")));
            case "settings.account.email.verify":
                return await modules.Accounts.Service.ConfirmEmailVerificationAsync(Text(args, "code"), token);
            case "settings.membership.get": return await modules.Accounts.Membership.RefreshAsync(token);
            case "settings.membership.trialClaim":
                await modules.Accounts.Membership.ClaimTrialAsync(token);
                return await modules.Accounts.Membership.RefreshAsync(token);
            case "settings.membership.kick":
                await modules.Accounts.Membership.KickAsync(Guid.Parse(Text(args, "deviceId")), token);
                return Data(("message", UiText.Get("设备已退出登录。")));
            case "settings.developer.toggle":
                if (!_shell.TryToggleDeveloperMode(Text(args, "password"))) throw new InvalidOperationException(UiText.Get("密码错误"));
                break;
            case "settings.legal.get":
                return LegalDocuments.GetDocument(Text(args, "kind") switch
                {
                    "terms" => LegalDocumentKind.UserAgreement,
                    "privacy" => LegalDocumentKind.PrivacyPolicy,
                    _ => throw new ArgumentException("Unknown legal document."),
                });
            case "settings.feedback.submit":
                // Reject legacy calls before reading content, images or local logs.
                throw new InvalidOperationException(UiText.Get("账号服务不接收反馈、截图或日志，请使用 GitHub Issues。"));
            case "settings.profile":
                await modules.Library.Books.InitializeAsync(token);
                var syncProfile = await modules.Sync.Service.LoadSettingsAsync(token);
                var aiProfile = await modules.Ai.Settings.LoadAsync(token);
                var emailProfile = await modules.Integrations.KindleEmailSettings.LoadAsync(token);
                var profileStorage = await Task.Run(() => ComputeStorageBreakdown(modules.Paths), token);
                return Data(("version", ApplicationVersion.GetDisplayVersion(typeof(MobileApp).Assembly)),
                    ("sync", syncProfile.Settings is { Enabled: true, IsConfigured: true } ? UiText.Get("{0} · 已开启", syncProfile.Settings.ProviderName) : UiText.Get("未开启")),
                    ("ai", aiProfile.IsConfigured ? aiProfile.ProviderDisplayName : UiText.Get("未设置")),
                    ("kindle", emailProfile.IsConfigured ? UiText.Get("已设置") : UiText.Get("未设置")),
                    ("storage", profileStorage.TotalText),
                    ("account", await modules.Accounts.Service.GetStatusAsync(cancellationToken: token)));
            case "settings.storage.get":
                return await Task.Run(() => ComputeStorageBreakdown(modules.Paths), token);
            case "settings.storage.clearCache":
                return await Task.Run(() =>
                {
                    ClearDirectoryContents(modules.Paths.ReaderCache);
                    ClearDirectoryContents(modules.Paths.Logs);
                    return ComputeStorageBreakdown(modules.Paths);
                }, token);
            case "settings.sync.get":
                var stored = await modules.Sync.Service.LoadSettingsAsync(token);
                _syncDraft = stored.Settings;
                return Data(("settings", stored.Settings), ("summary", await SyncSummaryAsync(stored, token)),
                    ("providers", modules.Sync.Service.AvailableProviders.Select(provider =>
                        Data(("provider", (int)provider.Provider), ("displayName", provider.DisplayName))).ToArray()));
            case "settings.sync.save":
            case "settings.sync.test":
            case "settings.sync.run":
                var current = await modules.Sync.Service.LoadSettingsAsync(token);
                var settings = ReadSyncSettings(args, _syncDraft ?? current.Settings);
                // Saving this form records the user's explicit provider choice.
                settings = settings with { AccountRegion = modules.DataRegion, ProviderChosenByUser = true };
                if (command == "settings.sync.run") settings = settings with { Enabled = true };
                if ((settings.Enabled || command != "settings.sync.save") && settings.Validate() is { } invalid)
                    throw new InvalidOperationException(invalid);
                if (command == "settings.sync.test")
                {
                    await modules.Sync.Service.TestConnectionAsync(settings, token);
                    return Data(("message", UiText.Get("连接成功")));
                }
                if (!string.Equals(settings.EncryptionKey, current.Settings.EncryptionKey, StringComparison.Ordinal)
                    && !Flag(args, "encryptionKeyConfirmed"))
                    throw new InvalidOperationException(UiText.Get("请先确认同步密钥。"));
                await modules.Sync.Service.SaveSettingsAsync(current.DeviceId, settings, token);
                // Platform account bindings are added by the service on save.
                settings = (await modules.Sync.Service.LoadSettingsAsync(token)).Settings;
                _syncDraft = settings;
                if (command == "settings.sync.save") return Data();
                await modules.Library.Books.InitializeAsync(token);
                await _shell.EnsureReaderDataAsync();
                var result = await _shell.Sync.RunAsync(new S3SyncStoredSettings(current.DeviceId, settings),
                    new Progress<string>(message => Publish(Data(("type", "status"), ("channel", "sync"), ("message", UiText.Localize(message))))),
                    (exception, _) => ConfirmAsync(UiText.Get("确认同步删除"), UiText.Get("{0}\n\n继续后，这些删除会同步到其他设备。", UiText.Localize(exception.Message)), UiText.Get("同步这些删除")), token, operation);
                return Data(("message", result is null ? UiText.Get("已取消，未同步删除。") : result.IsPartial
                    ? UiText.Get("同步未完成：新增 {0} 本书，下载 {1} 个文件；请重试。", result.BooksAdded, result.FilesDownloaded)
                    : result.Changed ? UiText.Get("同步完成：新增 {0} 本，下载 {1} 个文件，应用 {2} 条批注。", result.BooksAdded, result.FilesDownloaded, result.AnnotationsApplied)
                    : UiText.Get("同步完成，当前已是最新。")), ("warning", result?.Warning is { } warning ? UiText.Localize(warning) : null));
            case "settings.sync.import":
                var syncCurrent = await modules.Sync.Service.LoadSettingsAsync(token);
                var importDraft = ReadSyncSettings(args, _syncDraft ?? syncCurrent.Settings);
                var files = await _shell.Services.ImportSource.PickFilesAsync(token);
                try
                {
                    if (files.Count == 0) return importDraft;
                    if (files.Count != 1) throw new InvalidOperationException(UiText.Get("请选择一个同步参数文件。"));
                    _syncDraft = await SyncConnectionProfileService.ImportAsync(files[0], importDraft, token);
                    return _syncDraft;
                }
                finally { _shell.Services.ImportSource.ReleaseFiles(files); }
            case "settings.sync.export":
                var exportCurrent = await modules.Sync.Service.LoadSettingsAsync(token);
                await ExportSyncProfileAsync(ReadSyncSettings(args, _syncDraft ?? exportCurrent.Settings), token);
                break;
            case "settings.ai.get": return await modules.Ai.Settings.LoadAsync(token);
            case "settings.ai.defaults":
                var defaults = AiConnectionSettings.GetDefaults(Text(args, "provider", "deepseek"));
                return Data(("baseUrl", defaults.BaseUrl), ("model", defaults.Model));
            case "settings.ai.save":
            case "settings.ai.test":
            case "settings.ai.models":
                var ai = args.GetProperty("settings").Deserialize(IOSNativeJsonContext.Default.AiConnectionSettings)
                    ?? throw new InvalidDataException(UiText.Get("无效的 AI 设置。"));
                if (!Uri.TryCreate(ai.BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException(UiText.Get("请输入有效的接口地址。"));
                if (command == "settings.ai.models")
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var models = await modules.Ai.ChatClient.ListModelsAsync(ai, timeout.Token);
                    return Data(("models", models.ToArray()));
                }
                if (command == "settings.ai.test")
                {
                    await modules.Ai.ChatClient.TestConnectionAsync(ai, token);
                    return Data(("message", UiText.Get("连接成功")));
                }
                await modules.Ai.Settings.SaveAsync(ai, token);
                break;
            case "settings.kindle.get": return await modules.Integrations.KindleEmailSettings.LoadAsync(token);
            case "settings.kindle.save":
                var email = args.GetProperty("settings").Deserialize(IOSNativeJsonContext.Default.KindleEmailSettings)
                    ?? throw new InvalidDataException(UiText.Get("无效的邮箱设置。"));
                if (email.Validate() is { } emailInvalid) throw new InvalidOperationException(emailInvalid);
                await modules.Integrations.KindleEmailSettings.SaveAsync(KindleEmailSettings.Normalize(email), token);
                break;
            case "settings.speech.get":
                var detectedLanguage = _reader?.NativeGetSpeechLanguage() ?? "zh";
                using (var engine = _shell.Services.CreateTtsEngine?.Invoke())
                    return Data(("settings", await modules.Reader.TtsSettings.LoadAsync(token)),
                        ("voices", engine is null ? Array.Empty<TtsVoiceInfo>() : (await engine.GetVoicesAsync(token)).ToArray()),
                        ("detectedLanguage", detectedLanguage));
            case "settings.speech.save":
                var speech = await modules.Reader.TtsSettings.LoadAsync(token);
                speech.Voice = Text(args, "voice", speech.Voice);
                speech.Speed = args.GetProperty("speed").GetDouble();
                speech.AutoAdvance = Flag(args, "autoAdvance", speech.AutoAdvance);
                await modules.Reader.TtsSettings.SaveAsync(speech, token);
                if (_reader is { } activeReader)
                    await activeReader.NativeReloadSpeechSettingsAsync();
                break;
            default: throw new InvalidOperationException(UiText.Get("未知设置操作：{0}", command));
        }
        return Data();
    }

    private async Task<string> SyncSummaryAsync(S3SyncStoredSettings stored, CancellationToken token)
    {
        if (!stored.Settings.IsConfigured) return UiText.Get("选择同步方式并保存后可与其他设备同步");
        var status = await _shell.Modules.Sync.Service.GetStatusAsync(stored.DeviceId, stored.Settings, token);
        return status.LastSyncAt is { } last
            ? UiText.Get("上次同步 {0:yyyy/MM/dd HH:mm} · {1} 本", last.LocalDateTime, status.SnapshotBookCount)
                + (status.PendingFileDownloadCount > 0 ? UiText.Get(" · {0} 个文件待下载", status.PendingFileDownloadCount) : "")
            : UiText.Get("尚未同步");
    }

    private S3SyncSettings ReadSyncSettings(JsonElement args, S3SyncSettings current)
    {
        var draft = Read(args.GetProperty("settings"), current, IOSNativeJsonContext.Default.S3SyncSettings);
        if (draft.Provider == SyncProvider.AccountServer)
            throw new InvalidOperationException(MemberEntitlements.AccountCloudRequirement);
        if (!_shell.Modules.Sync.Service.AvailableProviders.Any(provider => provider.Provider == draft.Provider))
            throw new InvalidOperationException(UiText.Get("当前平台不支持此同步方式。"));
        // Preserve service settings the phone doesn't edit, including fields
        // loaded by an imported connection profile.
        return S3SyncSettings.Normalize(current with
        {
            Provider = draft.Provider, Enabled = draft.Enabled, AutomaticSyncEnabled = draft.AutomaticSyncEnabled,
            DownloadBookFilesOnSync = draft.DownloadBookFilesOnSync, Endpoint = draft.Endpoint,
            AccessKey = draft.AccessKey, SecretKey = draft.SecretKey, WebDavEndpoint = draft.WebDavEndpoint,
            WebDavUsername = draft.WebDavUsername, WebDavPassword = draft.WebDavPassword,
            Bucket = draft.Bucket, Region = draft.Region, PathStyle = draft.PathStyle,
            SkipTlsVerify = draft.SkipTlsVerify, Prefix = draft.Prefix, EncryptionKey = draft.EncryptionKey,
            AccountRegion = current.AccountRegion, AccountServerUrl = current.AccountServerUrl, AccountUserId = current.AccountUserId
        });
    }

    private async Task ExportSyncProfileAsync(S3SyncSettings settings, CancellationToken token)
    {
        if (!await ConfirmAsync(UiText.Get("导出连接参数"), UiText.Get("参数文件会明文包含当前服务的账号和密码，请只保存或分享给信任的位置。同步加密密钥不会导出。"), UiText.Get("继续导出"))) return;
        var directory = Path.Combine(Path.GetTempPath(), "libzora-native-sync-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, $"Libzora-{settings.ProviderName}-{DateTime.Now:yyyyMMdd-HHmmss}"
                + SyncConnectionProfileService.FileExtension(settings.Provider));
            await SyncConnectionProfileService.ExportAsync(path, settings, token);
            if (_shell.Services.FileShare is { } share && await share.ShareFileAsync(path))
                _shell.ShowToast(UiText.Get("参数文件已导出。"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private async Task<object> DispatchDictionaryAsync(string command, JsonElement args, CancellationToken token)
    {
        var dictionary = _shell.Modules.Library.Dictionary;
        switch (command)
        {
            case "dictionary.get": return (await dictionary.ListAsync(token)).ToArray();
            case "dictionary.lookup": return (await dictionary.LookupAsync(Text(args, "term").Trim(), token)).ToArray();
            case "dictionary.lookupRendered":
                var lookupEntries = (await dictionary.LookupAsync(Text(args, "term").Trim(), token)).ToArray();
                DictionaryHtmlPalette? palette = null;
                if (args.TryGetProperty("palette", out var paletteElement) && paletteElement.ValueKind == JsonValueKind.Object)
                    palette = paletteElement.Deserialize(IOSNativeJsonContext.Default.DictionaryHtmlPalette);
                var renderedHtml = lookupEntries.Any(entry => entry.Html is not null)
                    ? DictionaryHtmlRenderer.Render(lookupEntries, palette)
                    : null;
                return Data(
                    ("entries", renderedHtml is null ? lookupEntries : Array.Empty<DictionaryEntry>()),
                    ("renderedHtml", renderedHtml),
                    ("entryCount", lookupEntries.Length));
            case "dictionary.wikipedia":
                var wikipediaArticle = await WikipediaLookup.LookupAsync(
                    Text(args, "term").Trim(),
                    UiText.CurrentLanguage,
                    token);
                return wikipediaArticle is null
                    ? Array.Empty<WikipediaArticle>()
                    : new[] { wikipediaArticle };
            case "dictionary.ai":
            {
                var term = Text(args, "term").Trim();
                if (term.Length == 0) return Data(("text", string.Empty));
                var ai = await _shell.Modules.Ai.Settings.LoadAsync(token);
                if (!ai.IsConfigured) throw new InvalidOperationException(UiText.Get("请先在“我的”中设置 AI 接口。"));
                if (!await EnsureAiConsentAsync(ai, token))
                    throw new InvalidOperationException(UiText.Get("已取消 AI 授权，未发送任何内容。"));
                var instructions = UiText.CurrentLanguage == "zh-CN"
                    ? "你是简洁准确的词典助手。请用中文解释用户查询的词语或短语：给出音标/词性（如适用）、核心释义与 1–2 个简短例句，直接输出，不要寒暄。"
                    : $"You are a concise dictionary assistant. Explain the queried word or phrase in {UiText.CurrentLanguage}: provide pronunciation/part of speech if applicable, core meanings, and 1-2 brief examples directly without preamble.";
                var answer = await _shell.Modules.Ai.ChatClient.CompleteAsync(
                    ai, instructions, term, Array.Empty<AiConversationTurn>(), token);
                return Data(("text", answer));
            }
            case "dictionary.toggle": await dictionary.SetEnabledAsync(Text(args, "id"), Flag(args, "enabled"), token); break;
            case "dictionary.remove":
                if (await ConfirmAsync(UiText.Get("移除词典"), UiText.Get("移除后可以重新导入。"), UiText.Get("移除"))) await dictionary.RemoveAsync(Text(args, "id"), token);
                break;
            case "dictionary.import":
                var files = await _shell.Services.ImportSource.PickFilesAsync(token);
                try
                {
                    var imported = await ImportDictionaryFilesAsync(dictionary, files, token);
                    return Data(("importedCount", imported.Count), ("names", imported.ToArray()));
                }
                finally { _shell.Services.ImportSource.ReleaseFiles(files); }
            default: throw new InvalidOperationException(UiText.Get("未知词典操作：{0}", command));
        }
        return Data();
    }

    internal static async Task<List<string>> ImportDictionaryFilesAsync(
        DictionaryService dictionary,
        IReadOnlyList<string> files,
        CancellationToken token)
    {
        if (files.Count == 0) return [];
        var cssFiles = files
            .Where(path => Path.GetExtension(path).Equals(".css", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dictionaryFiles = files
            .Where(path => !Path.GetExtension(path).Equals(".css", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (dictionaryFiles.Length == 0)
            throw new InvalidOperationException(UiText.Get("请选择 MDX、MOBI、AZW、PRC、TXT、TSV 或 CSV 词典文件。"));

        foreach (var cssFile in cssFiles)
        {
            foreach (var mdxDirectory in dictionaryFiles
                .Where(path => Path.GetExtension(path).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetDirectoryName)
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var targetCss = Path.Combine(mdxDirectory!, Path.GetFileName(cssFile));
                if (!string.Equals(Path.GetFullPath(cssFile), Path.GetFullPath(targetCss), StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(targetCss))
                    File.Copy(cssFile, targetCss);
            }
        }

        var imported = new List<string>(dictionaryFiles.Length);
        foreach (var file in dictionaryFiles)
        {
            try
            {
                var definition = await Task.Run(() => dictionary.ImportAsync(file, cancellationToken: token), token);
                imported.Add(definition.Name);
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("Calibre", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(UiText.Get("此 Kindle 词典需要 Calibre 转换，手机版无法导入。"), exception);
            }
        }
        return imported;
    }

    private static NativeStorageBreakdown ComputeStorageBreakdown(AppPaths paths)
    {
        var books = MeasureDirectory(paths.Library);
        var dictionaries = MeasureDirectory(paths.Dictionaries);
        var fonts = MeasureDirectory(paths.Fonts);

        var readerCache = MeasureDirectory(paths.ReaderCache);
        var logs = MeasureDirectory(paths.Logs);
        var covers = MeasureDirectory(paths.Covers);
        var materialCovers = MeasureDirectory(Path.Combine(paths.Data, "reading-material-covers"));
        var cacheBytes = readerCache.Bytes + logs.Bytes + covers.Bytes + materialCovers.Bytes;
        var cacheFiles = readerCache.Files + logs.Files + covers.Files + materialCovers.Files;
        var clearableCacheBytes = readerCache.Bytes + logs.Bytes;

        var backups = MeasureDirectory(paths.Backups);
        var trash = MeasureDirectory(paths.Trash);
        var backupsTrashBytes = backups.Bytes + trash.Bytes;
        var backupsTrashFiles = backups.Files + trash.Files;

        var excludedRoots = new[]
        {
            paths.Library,
            paths.Dictionaries,
            paths.Fonts,
            paths.ReaderCache,
            paths.Logs,
            paths.Covers,
            Path.Combine(paths.Data, "reading-material-covers"),
            paths.Backups,
            paths.Trash,
        }
        .Select(NormalizePrefix)
        .ToArray();

        var other = MeasureOtherFiles(paths.Root, excludedRoots);

        var categories = new[]
        {
            new NativeStorageCategory("books", books.Bytes, MobileFormat.Size(books.Bytes), books.Files),
            new NativeStorageCategory("dictionaries", dictionaries.Bytes, MobileFormat.Size(dictionaries.Bytes), dictionaries.Files),
            new NativeStorageCategory("fonts", fonts.Bytes, MobileFormat.Size(fonts.Bytes), fonts.Files),
            new NativeStorageCategory("cache", cacheBytes, MobileFormat.Size(cacheBytes), cacheFiles),
            new NativeStorageCategory("backupsTrash", backupsTrashBytes, MobileFormat.Size(backupsTrashBytes), backupsTrashFiles),
            new NativeStorageCategory("other", other.Bytes, MobileFormat.Size(other.Bytes), other.Files),
        };

        var totalBytes = categories.Sum(category => category.Bytes);
        return new NativeStorageBreakdown(
            TotalBytes: totalBytes,
            TotalText: MobileFormat.Size(totalBytes),
            ClearableCacheBytes: clearableCacheBytes,
            ClearableCacheText: MobileFormat.Size(clearableCacheBytes),
            Categories: categories);
    }

    private static string NormalizePrefix(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + Path.DirectorySeparatorChar;
    }

    private static (long Bytes, int Files) MeasureDirectory(string path)
    {
        if (!Directory.Exists(path)) return (0, 0);
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch { }
            }
        }
        catch { }
        return (bytes, files);
    }

    private static (long Bytes, int Files) MeasureOtherFiles(string root, string[] excludedPrefixes)
    {
        if (!Directory.Exists(root)) return (0, 0);
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(file);
                var excluded = false;
                foreach (var prefix in excludedPrefixes)
                {
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch { }
            }
        }
        catch { }
        return (bytes, files);
    }

    private static void ClearDirectoryContents(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path))
        {
            try { File.Delete(file); } catch { }
        }
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
#endif
