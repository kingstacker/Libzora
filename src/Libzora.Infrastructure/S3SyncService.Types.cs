using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Path helpers and local sync object record.
/// </summary>
public sealed partial class S3SyncService : ISyncService
{
    private string? ResolveDataPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var dataRoot = Path.GetFullPath(_paths.Data)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(_paths.Data, relativePath));
        return fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record LocalSyncObject(string Hash, string Path, string ContentType);
}
