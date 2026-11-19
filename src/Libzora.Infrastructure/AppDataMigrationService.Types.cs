using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Payload file record.
/// </summary>
public sealed partial class AppDataMigrationService
{
    private sealed record PayloadFile(
        string SourcePath,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);

    internal enum MigrationPhase
    {
        Preparing,
        Copying,
        Verifying,
        Committing,
        Committed
    }

    internal sealed class MigrationJournal
    {
        public int Version { get; set; } = JournalVersion;
        public MigrationPhase Phase { get; set; }
        public string SourceRoot { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;
        public string StageDirectory { get; set; } = string.Empty;
        public string ActiveDataRelativePath { get; set; } = "data";
        public List<string> PayloadEntries { get; set; } = [];
        public List<string> CopiedDatabasePaths { get; set; } = [];
        public string SourceFingerprint { get; set; } = string.Empty;
        public int BookCount { get; set; }
        public int FileCount { get; set; }
        public long CopiedBytes { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
