using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Persists AI connection settings with a protected API key.
/// </summary>
public sealed class AiSettingsStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AiSettingsStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "ai-settings.json");

    public async Task<AiConnectionSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new AiConnectionSettings();
        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedAiSettings>(stream, _jsonOptions, cancellationToken);
            if (persisted is null) return new AiConnectionSettings();
            var provider = persisted.Provider?.Trim().ToLowerInvariant() ?? "deepseek";
            if (provider is not ("deepseek" or "openai" or "custom")) provider = "custom";
            var defaults = AiConnectionSettings.GetDefaults(provider);
            return new AiConnectionSettings
            {
                Provider = provider,
                BaseUrl = string.IsNullOrWhiteSpace(persisted.BaseUrl) ? defaults.BaseUrl : persisted.BaseUrl.Trim(),
                Model = AiConnectionSettings.NormalizeModel(
                    provider,
                    string.IsNullOrWhiteSpace(persisted.Model) ? defaults.Model : persisted.Model),
                ApiKey = UnprotectApiKey(persisted.ProtectedApiKey)
            };
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException or System.ComponentModel.Win32Exception)
        {
            return new AiConnectionSettings();
        }
        catch (SecretStoreUnavailableException)
        {
            // Keychain locked: keep the stored (still valid) API key untouched.
            SecretStoreGuard.MarkUnreadable(SettingsPath);
            return new AiConnectionSettings();
        }
    }

    public async Task SaveAsync(AiConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        await SaveUnderLockAsync(settings, cancellationToken);
    }

    // A quick model selection must preserve the stored endpoint and encrypted
    // key, including keys that cannot be decrypted on the current machine.
    public async Task SaveModelAsync(string model, CancellationToken cancellationToken = default)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        var persisted = new PersistedAiSettings();
        if (File.Exists(SettingsPath))
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, true);
            persisted = await JsonSerializer.DeserializeAsync<PersistedAiSettings>(stream, _jsonOptions, cancellationToken)
                ?? persisted;
        }
        persisted.Model = AiConnectionSettings.NormalizeModel(persisted.Provider ?? "deepseek", model);
        await PublishAsync(persisted, cancellationToken);
    }

    private string UnprotectApiKey(string? protectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(protectedApiKey)));
        }
        catch (Exception exception) when (exception is FormatException
            or System.Security.Cryptography.CryptographicException
            or System.ComponentModel.Win32Exception)
        {
            // Keep the endpoint/model usable when the machine-bound key is
            // unavailable, so the user only needs to re-enter the API key.
            return string.Empty;
        }
    }

    internal async Task SaveUnderLockAsync(AiConnectionSettings settings, CancellationToken cancellationToken, DateTimeOffset? syncedAt = null)
    {
        SecretStoreGuard.ThrowIfUnreadable(SettingsPath);
        _paths.EnsureDirectories();
        var persisted = new PersistedAiSettings
        {
            Provider = settings.Provider.Trim().ToLowerInvariant(),
            BaseUrl = settings.BaseUrl.Trim(),
            Model = AiConnectionSettings.NormalizeModel(settings.Provider, settings.Model),
            ProtectedApiKey = string.IsNullOrWhiteSpace(settings.ApiKey)
                ? string.Empty
                : Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(settings.ApiKey.Trim())))
        };
        await PublishAsync(persisted, cancellationToken, syncedAt);
    }

    private async Task PublishAsync(PersistedAiSettings persisted, CancellationToken cancellationToken, DateTimeOffset? syncedAt = null)
    {
        _paths.EnsureDirectories();
        var temporaryPath = SettingsPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, persisted, _jsonOptions, cancellationToken);
        if (syncedAt is { } timestamp) File.SetLastWriteTimeUtc(temporaryPath, timestamp.UtcDateTime);
        SettingsFile.Publish(temporaryPath, SettingsPath);
    }

    private sealed class PersistedAiSettings
    {
        public string? Provider { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ProtectedApiKey { get; set; }
    }
}
