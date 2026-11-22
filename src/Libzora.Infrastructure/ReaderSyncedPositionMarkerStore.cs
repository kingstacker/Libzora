using System.Globalization;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Persists local-only markers for reading positions received from another
/// device during sync so the reader highlights the resume point on first open
/// and clears it when the user turns or leaves the page.
/// </summary>
internal static class ReaderSyncedPositionMarkerStore
{
    public static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ReaderSyncedPositionMarkers (
                BookFileId TEXT PRIMARY KEY,
                BookId TEXT NOT NULL,
                ChapterPath TEXT NOT NULL,
                Fragment TEXT NULL,
                ChapterIndex INTEGER NOT NULL DEFAULT 0,
                ScrollPosition INTEGER NOT NULL DEFAULT 0,
                ProgressPercent REAL NOT NULL DEFAULT 0,
                FlowMode INTEGER NOT NULL DEFAULT 0,
                ContentPositionJson TEXT NULL,
                SourceDeviceId TEXT NOT NULL DEFAULT '',
                SyncedAt TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<ReaderSyncedPositionMarker?> GetAsync(
        SqliteConnection connection,
        Guid bookFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, SourceDeviceId, SyncedAt, ContentPositionJson
            FROM ReaderSyncedPositionMarkers
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderSyncedPositionMarker(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetDouble(6),
            reader.GetInt32(7),
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture))
        {
            ContentPosition = ReaderContentPositionJson.Deserialize(reader.IsDBNull(10) ? null : reader.GetString(10))
        };
    }

    public static async Task UpsertInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid localBookId,
        Guid localFileId,
        S3SyncProgress item,
        string sourceDeviceId,
        DateTimeOffset syncedAt,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ReaderSyncedPositionMarkers (
                BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                ProgressPercent, FlowMode, ContentPositionJson, SourceDeviceId, SyncedAt)
            VALUES (
                $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                $progressPercent, $flowMode, $contentPosition, $sourceDeviceId, $syncedAt)
            ON CONFLICT(BookFileId) DO UPDATE SET
                BookId = excluded.BookId, ChapterPath = excluded.ChapterPath,
                Fragment = excluded.Fragment, ChapterIndex = excluded.ChapterIndex,
                ScrollPosition = excluded.ScrollPosition, ProgressPercent = excluded.ProgressPercent,
                FlowMode = excluded.FlowMode, ContentPositionJson = excluded.ContentPositionJson,
                SourceDeviceId = excluded.SourceDeviceId, SyncedAt = excluded.SyncedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", localFileId.ToString());
        command.Parameters.AddWithValue("$bookId", localBookId.ToString());
        command.Parameters.AddWithValue("$chapterPath", item.ChapterPath);
        command.Parameters.AddWithValue("$fragment", (object?)item.Fragment ?? DBNull.Value);
        command.Parameters.AddWithValue("$chapterIndex", item.ChapterIndex);
        command.Parameters.AddWithValue("$scrollPosition", item.ScrollPosition);
        command.Parameters.AddWithValue("$progressPercent", item.ProgressPercent);
        command.Parameters.AddWithValue("$flowMode", item.FlowMode);
        command.Parameters.AddWithValue("$contentPosition", (object?)item.ContentPositionJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceDeviceId", sourceDeviceId);
        command.Parameters.AddWithValue("$syncedAt", syncedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task DeleteInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid localFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM ReaderSyncedPositionMarkers WHERE BookFileId = $bookFileId;";
        command.Parameters.AddWithValue("$bookFileId", localFileId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task ClearAsync(
        SqliteConnection connection,
        Guid bookFileId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ReaderSyncedPositionMarkers WHERE BookFileId = $bookFileId;";
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static bool AreSameReadingPosition(S3SyncProgress? local, S3SyncProgress remote)
    {
        if (local is null) return false;
        if (local.ChapterIndex != remote.ChapterIndex) return false;

        var isPdf = remote.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)
            || local.ChapterPath.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase);
        if (isPdf)
        {
            if (TryParsePdfPan(local.Fragment, out var lx, out var ly)
                && TryParsePdfPan(remote.Fragment, out var rx, out var ry))
            {
                return Math.Abs(lx - rx) <= 0.02 && Math.Abs(ly - ry) <= 0.02;
            }
            return true;
        }

        if (!string.Equals(
                NormalizeChapterPath(local.ChapterPath),
                NormalizeChapterPath(remote.ChapterPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var localPos = ReaderContentPositionJson.Deserialize(local.ContentPositionJson);
        var remotePos = ReaderContentPositionJson.Deserialize(remote.ContentPositionJson);
        if (localPos is not null && remotePos is not null)
        {
            if (localPos.ImageIndex != remotePos.ImageIndex) return false;
            if (localPos.TextOffset != remotePos.TextOffset) return false;
            if (localPos.TextOffset >= 0 || localPos.ImageIndex is not null)
            {
                return local.FlowMode != 0 && remote.FlowMode != 0
                    || (Math.Abs(localPos.RunY - remotePos.RunY) <= 4
                        && Math.Abs(localPos.SliceFraction - remotePos.SliceFraction) <= 0.02);
            }
        }
        else if (localPos is not null || remotePos is not null)
        {
            return false;
        }

        return local.ScrollPosition == remote.ScrollPosition
            && string.Equals(local.Fragment ?? string.Empty, remote.Fragment ?? string.Empty, StringComparison.Ordinal);
    }

    private static string NormalizeChapterPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static bool TryParsePdfPan(string? fragment, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(fragment) || !fragment.StartsWith("pdf-view:", StringComparison.Ordinal))
            return false;
        var parts = fragment[9..].Split(';');
        return parts.Length >= 3
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }
}
