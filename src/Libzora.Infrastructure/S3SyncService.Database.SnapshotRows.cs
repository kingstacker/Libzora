using System.Globalization;
using System.Security.Cryptography;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Row mapping, cloning and snapshot finishing.
/// </summary>
public sealed partial class S3SyncService
{
    private static S3SyncAnnotation ReadAnnotationRow(SqliteDataReader reader) => new()
    {
        Id = ParseGuid(reader.GetString(0), "ReaderAnnotations.Id"),
        BookId = ParseGuid(reader.GetString(1), "ReaderAnnotations.BookId"),
        BookFileId = ParseGuid(reader.GetString(2), "ReaderAnnotations.BookFileId"),
        ChapterPath = reader.GetString(3),
        Fragment = NullableString(reader, 4),
        StartOffset = reader.GetInt32(5),
        EndOffset = reader.GetInt32(6),
        SelectedText = reader.GetString(7),
        Prefix = reader.GetString(8),
        Suffix = reader.GetString(9),
        Color = reader.GetString(10),
        UnderlineStyle = reader.GetString(11),
        Note = reader.GetString(12),
        CreatedAt = ParseTimestamp(reader.GetString(13)),
        UpdatedAt = ParseTimestamp(reader.GetString(14))
    };

    private static void OverlayAnnotation(
        S3SyncAnnotation annotation,
        List<S3SyncAnnotation> annotations,
        Dictionary<Guid, int> annotationIndex)
    {
        if (annotationIndex.TryGetValue(annotation.Id, out var index))
            annotations[index] = annotation;
        else
        {
            annotationIndex[annotation.Id] = annotations.Count;
            annotations.Add(annotation);
        }
    }

    private static S3SyncBook ReadBookRow(SqliteDataReader reader) => new()
    {
        Id = ParseGuid(reader.GetString(0), "Books.Id"),
        Title = reader.GetString(1),
        Authors = reader.GetString(2),
        Series = NullableString(reader, 3),
        SeriesIndex = NullableDouble(reader, 4),
        Description = NullableString(reader, 5),
        Publisher = NullableString(reader, 6),
        PublishDate = NullableString(reader, 7),
        Isbn = NullableString(reader, 8),
        PageCount = NullableString(reader, 9),
        Binding = NullableString(reader, 10),
        OnlineRating = NullableDouble(reader, 11),
        OnlineRatingCount = NullableInt(reader, 12),
        Tags = reader.GetString(13),
        Category = reader.GetString(14),
        IsFavorite = reader.GetInt32(15) != 0,
        ReadingStatus = (LibraryReadingStatus)reader.GetInt32(16),
        CreatedAt = ParseTimestamp(reader.GetString(17)),
        UpdatedAt = ParseTimestamp(reader.GetString(18)),
        LocalCoverPath = NullableString(reader, 19)
    };

    private static S3SyncBook CloneBook(S3SyncBook source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        Authors = source.Authors,
        Series = source.Series,
        SeriesIndex = source.SeriesIndex,
        Description = source.Description,
        Publisher = source.Publisher,
        PublishDate = source.PublishDate,
        Isbn = source.Isbn,
        PageCount = source.PageCount,
        Binding = source.Binding,
        OnlineRating = source.OnlineRating,
        OnlineRatingCount = source.OnlineRatingCount,
        Tags = source.Tags,
        Category = source.Category,
        IsFavorite = source.IsFavorite,
        ReadingStatus = source.ReadingStatus,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        CoverHash = source.CoverHash,
        CoverFileName = source.CoverFileName,
        LocalCoverPath = source.LocalCoverPath,
        SyncVersion = source.SyncVersion
    };

    private static List<S3SyncTombstone> CloneTombstones(
        IReadOnlyCollection<S3SyncTombstone> source) =>
        source.Select(tombstone => new S3SyncTombstone
        {
            EntityType = tombstone.EntityType,
            Key = tombstone.Key,
            DeletedAt = tombstone.DeletedAt,
            ReadingDataResetId = tombstone.ReadingDataResetId,
            SyncVersion = tombstone.SyncVersion
        }).ToList();

    private Task<S3SyncSnapshot> CaptureSnapshotAsync(
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        CancellationToken cancellationToken) =>
        CaptureSnapshotWithBaselineAsync(deviceId, tombstones, cancellationToken, null, null);

    private async Task<S3SyncSnapshot> CaptureSnapshotWithBaselineAsync(
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        CancellationToken cancellationToken,
        S3SyncSnapshot? incrementalBase = null,
        DateTimeOffset? since = null)
    {
        var captureStartedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = incrementalBase is not null && since is not null
                ? await CaptureIncrementalDatabaseSnapshotAsync(
                    connection, transaction, deviceId, tombstones, incrementalBase,
                    since.Value, captureStartedAt, cancellationToken)
                : await CaptureDatabaseSnapshotAsync(
                    connection, transaction, deviceId, tombstones,
                    cancellationToken, captureStartedAt);
        await transaction.CommitAsync(cancellationToken);

        return await FinishSnapshotAsync(snapshot, cancellationToken);
    }

    /// <summary>
    /// Builds the post-merge converged view from the in-transaction baseline.
    /// The per-book tables are small and scanned in full (merged rows can carry
    /// old timestamps); annotations, the large table, are overlaid by the local
    /// changes since the transaction start plus the remote IDs that were
    /// actually downloaded.
    /// </summary>
    private async Task<S3SyncSnapshot> CaptureConvergedSnapshotAsync(
        string deviceId,
        IReadOnlyCollection<S3SyncTombstone> tombstones,
        S3SyncSnapshot baseline,
        IReadOnlyCollection<Guid> annotationIds,
        bool fullScanAnnotations,
        CancellationToken cancellationToken)
    {
        var captureStartedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenDatabaseConnectionAsync(cancellationToken);
        await EnsureDeletionTrackingSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var snapshot = await CaptureIncrementalDatabaseSnapshotAsync(
            connection, transaction, deviceId, tombstones, baseline,
            baseline.CreatedAt, captureStartedAt, cancellationToken,
            fullScanNonAnnotationTables: true,
            annotationIds: annotationIds,
            fullScanAnnotations: fullScanAnnotations);
        await transaction.CommitAsync(cancellationToken);
        return await FinishSnapshotAsync(snapshot, cancellationToken);
    }

    private async Task<S3SyncSnapshot> FinishSnapshotAsync(
        S3SyncSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var book in snapshot.Books.Concat(snapshot.ReadingMaterialBooks))
        {
            var coverPath = book.LocalCoverPath;
            if (string.IsNullOrWhiteSpace(coverPath)) continue;
            var absolute = ResolveDataPath(coverPath);
            if (absolute is null || !File.Exists(absolute)) continue;
            try
            {
                book.CoverHash = await GetCachedFileHashAsync(absolute, cancellationToken);
                book.CoverFileName = Path.GetFileName(coverPath.Replace('\\', '/'));
            }
            catch (IOException)
            {
                // A cover can be in the middle of being replaced; metadata is
                // still useful and the next sync will retry its binary object.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an inaccessible cover like a transiently missing file.
            }
        }

        snapshot.Settings = await CaptureSettingsAsync(cancellationToken);
        return snapshot;
    }

    private async Task<S3SyncSettingsSnapshot> CaptureSettingsAsync(CancellationToken cancellationToken)
    {
        using var lease = await SettingsWriteLock.AcquireAsync(_paths, cancellationToken);
        return await CaptureSettingsUnderLockAsync(cancellationToken);
    }

    private async Task<S3SyncSettingsSnapshot> CaptureSettingsUnderLockAsync(CancellationToken cancellationToken)
    {
        var app = AppSettings.Normalize(await _appSettingsStore.LoadAsync(cancellationToken));
        var ai = await _aiSettingsStore.LoadAsync(cancellationToken);
        var kindleEmail = await _kindleEmailSettingsStore.LoadAsync(cancellationToken);
        var appUpdatedAt = GetSettingsUpdatedAt(_paths.Settings);
        var aiUpdatedAt = GetSettingsUpdatedAt(Path.Combine(_paths.Data, "ai-settings.json"));
        var emailUpdatedAt = GetSettingsUpdatedAt(Path.Combine(_paths.Data, "kindle-email-settings.json"));
        return new S3SyncSettingsSnapshot
        {
            UpdatedAt = new[] { appUpdatedAt, aiUpdatedAt, emailUpdatedAt }.Max(),
            AppUpdatedAt = appUpdatedAt,
            AiUpdatedAt = aiUpdatedAt,
            KindleEmailUpdatedAt = emailUpdatedAt,
            App = new S3SyncAppSettings
            {
                UiLanguage = app.UiLanguage,
                PreferredOpenFormat = app.PreferredOpenFormat,
                AutoBackupEnabled = app.AutoBackupEnabled,
                AutoGenerateEpubAndAzw3OnImport = app.AutoGenerateEpubAndAzw3OnImport,
                CollectionsMutuallyExclusive = app.CollectionsMutuallyExclusive,
                AutoBackupRetention = app.AutoBackupRetention,
                AiEnabled = app.AiEnabled,
                AutoUpdateCheckEnabled = app.AutoUpdateCheckEnabled,
                DevelopmentUpdateCheckEnabled = app.DevelopmentUpdateCheckEnabled,
                CompareKindleLibraryEnabled = app.CompareKindleLibraryEnabled,
                GridGalleryDisplay = app.GridGalleryDisplay,
                ShowSyncStatusIcon = app.ShowSyncStatusIcon,
                ShowLibraryPresenceIcon = app.ShowLibraryPresenceIcon,
                ShowUnearnedReadingMedals = app.ShowUnearnedReadingMedals,
                ReadingMaterialsCollapsedByDefault = app.ReadingMaterialsCollapsedByDefault,
                PinyinContextMenuEnabled = app.PinyinContextMenuEnabled,
                PinyinLocalOnly = app.PinyinLocalOnly,
                PinyinEngineId = app.PinyinEngineId,
                DefaultReaderLayout = app.DefaultReaderLayout
            },
            Ai = new S3SyncAiSettings
            {
                Provider = ai.Provider,
                BaseUrl = ai.BaseUrl,
                Model = ai.Model
            },
            KindleEmail = new S3SyncKindleEmailSettings
            {
                KindleEmailAddress = kindleEmail.KindleEmailAddress,
                SenderEmailAddress = kindleEmail.SenderEmailAddress,
                SmtpHost = kindleEmail.SmtpHost,
                SmtpPort = kindleEmail.SmtpPort,
                SmtpUsername = kindleEmail.SmtpUsername,
                EnableSsl = kindleEmail.EnableSsl
            }
        };
    }

    private static DateTimeOffset GetSettingsUpdatedAt(string path)
    {
        try
        {
            if (File.Exists(path)) return new DateTimeOffset(File.GetLastWriteTimeUtc(path));
        }
        catch (IOException)
        {
        }
        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset LayoutSyncVersion(S3SyncLayout layout)
    {
        var version = Max(layout.UpdatedAt, layout.ProfileUpdatedAt ?? DateTimeOffset.MinValue);
        return Max(version, layout.LayoutUpdatedAt ?? DateTimeOffset.MinValue);
    }
}
