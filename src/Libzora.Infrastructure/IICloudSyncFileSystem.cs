namespace Libzora.Infrastructure;

/// <summary>
/// Apple-owned iCloud Drive operations. The platform implementation must use
/// the entitled ubiquity container, never an ordinary local-directory fallback.
/// All accessors are synchronous and run inside NSFileCoordinator's accessor.
/// </summary>
public interface IICloudSyncFileSystem
{
    string ContainerIdentifier { get; }

    /// <summary>
    /// A stable, opaque fingerprint of the current ubiquity identity token, or
    /// null when signed out. It must change when the system iCloud account does.
    /// </summary>
    string? AccountIdentity { get; }

    /// <summary>Resolves the container's Documents directory off the UI thread.</summary>
    Task<string> GetContainerDirectoryAsync(CancellationToken cancellationToken);

    /// <summary>Includes files whose iCloud contents are not downloaded yet.</summary>
    bool ItemExists(string path);

    /// <summary>
    /// Requests materialization and waits for readable contents. Missing items,
    /// download failures and cancellation must throw, not return empty bytes.
    /// </summary>
    Task EnsureDownloadedAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until iCloud confirms the current file version is uploaded. Local
    /// publication alone must not be reported as a successful remote upload.
    /// Upload errors (including exhausted iCloud storage) and cancellation throw.
    /// </summary>
    Task EnsureUploadedAsync(string path, CancellationToken cancellationToken);

    T Coordinate<T>(string path, ICloudSyncFileAccess access, Func<string, T> accessor,
        CancellationToken cancellationToken);

    /// <summary>Removes a coordinated item; a missing item is a success.</summary>
    void DeleteItem(string path);

    /// <summary>
    /// Prompts iCloud Drive to refresh remote directory metadata (e.g. via
    /// NSMetadataQuery) so newly installed devices discover remote placeholders
    /// before listing. Non-fatal when unsupported or timed out.
    /// </summary>
    Task RefreshDirectoryAsync(string directoryPath, CancellationToken cancellationToken) => Task.CompletedTask;
}

public enum ICloudSyncFileAccess
{
    Read,
    Write,
    Delete
}

public static class ICloudSyncContainer
{
    // Both Apple heads must be provisioned for this exact shared container.
    public const string Identifier = "iCloud.com.kingstacker.libzora";
}
