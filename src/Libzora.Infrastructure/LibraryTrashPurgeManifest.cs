using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

internal sealed class LibraryTrashPurgeManifest
{
    public int Version { get; init; } = 1;
    public Guid TrashItemId { get; init; }
    public string TrashPath { get; init; } = string.Empty;
    public string? TrashCoverPath { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
