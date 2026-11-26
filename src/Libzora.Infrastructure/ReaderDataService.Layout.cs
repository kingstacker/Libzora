using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Layout columns and per-book layout profiles.
/// </summary>
public sealed partial class ReaderDataService
{
    private static async Task EnsureReaderLayoutTwoPageColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
        var hasTwoPageColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "TwoPageMode", StringComparison.OrdinalIgnoreCase))
                {
                    hasTwoPageColumn = true;
                    break;
                }
            }
        }

        if (hasTwoPageColumn) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderLayoutSettings ADD COLUMN TwoPageMode INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderLayoutParagraphIndentColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
        var hasParagraphIndentColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "ParagraphIndent", StringComparison.OrdinalIgnoreCase))
                {
                    hasParagraphIndentColumn = true;
                    break;
                }
            }
        }

        if (hasParagraphIndentColumn) return;

        // Existing readers have always kept the publisher/default paragraph
        // indent, so migrate old rows to the enabled state.
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderLayoutSettings ADD COLUMN ParagraphIndent INTEGER NOT NULL DEFAULT 1;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderLayoutProfileColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existingColumns.Add(reader.GetString(1));
        }

        var additions = new (string Name, string Definition)[]
        {
            ("UseBookOverride", "INTEGER NOT NULL DEFAULT 0"),
            ("AppearanceTheme", "INTEGER NOT NULL DEFAULT 0"),
            ("AppearancePaperEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("AppearancePaperStrength", "REAL NOT NULL DEFAULT 0.45"),
            ("AppearanceFibersEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("ProfileUpdatedAt", "TEXT NULL"),
            ("LayoutUpdatedAt", "TEXT NULL")
        };

        foreach (var (name, definition) in additions)
        {
            if (existingColumns.Contains(name)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE ReaderLayoutSettings ADD COLUMN {name} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = "UPDATE ReaderLayoutSettings SET LayoutUpdatedAt = UpdatedAt WHERE LayoutUpdatedAt IS NULL;";
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }

        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_ReaderLayoutSettings_BookProfile ON ReaderLayoutSettings(BookId, ProfileUpdatedAt DESC);";
        await index.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTextExtractionVersionColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(BookContentChunks);";
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "TextExtractionVersion", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE BookContentChunks ADD COLUMN TextExtractionVersion INTEGER NOT NULL DEFAULT 1;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderAnnotationStyleColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderAnnotations);";
        var hasUnderlineStyleColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "UnderlineStyle", StringComparison.OrdinalIgnoreCase))
                {
                    hasUnderlineStyleColumn = true;
                    break;
                }
            }
        }

        if (hasUnderlineStyleColumn) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderAnnotations ADD COLUMN UnderlineStyle TEXT NOT NULL DEFAULT 'solid';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureReaderBookmarkPositionColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderBookmarks);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("ScrollPosition"))
        {
            using var addPosition = connection.CreateCommand();
            addPosition.CommandText = "ALTER TABLE ReaderBookmarks ADD COLUMN ScrollPosition INTEGER NULL;";
            await addPosition.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("FlowMode"))
        {
            using var addFlowMode = connection.CreateCommand();
            addFlowMode.CommandText = "ALTER TABLE ReaderBookmarks ADD COLUMN FlowMode INTEGER NOT NULL DEFAULT 0;";
            await addFlowMode.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnsureReaderContentPositionColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "ReaderProgress", "ReaderBookmarks" })
        {
            using var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info({table});";
            var hasPosition = false;
            await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    hasPosition |= string.Equals(reader.GetString(1), "ContentPositionJson", StringComparison.OrdinalIgnoreCase);
            }
            if (hasPosition) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN ContentPositionJson TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task EnsureListeningTimeColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "ReaderReadingStats", "ReaderReadingSessions", "S3SyncReadingDayCounters" })
        {
            using var inspect = connection.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info({table});";
            var hasListening = false;
            var hasTable = false;
            await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    hasTable = true;
                    hasListening |= string.Equals(reader.GetString(1), "ListeningSeconds", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (!hasTable || hasListening) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN ListeningSeconds INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<ReaderLayoutSettings?> GetLayoutSettingsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent
            FROM ReaderLayoutSettings
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderLayoutSettings(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6) != 0,
            reader.GetInt32(7) != 0)
        {
            ParagraphIndent = reader.GetInt32(8) != 0
        };
    }

    public async Task SaveLayoutSettingsAsync(
        Guid bookId,
        Guid bookFileId,
        ReaderLayoutSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderLayoutSettings (
                    BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                    FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent, LayoutUpdatedAt, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                    $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $paragraphIndent, $updatedAt, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, FontScale=$fontScale, LineHeight=$lineHeight,
                    MaxWidth=$maxWidth, BodyPadding=$bodyPadding, FontFamily=$fontFamily,
                    FlowMode=$flowMode, VerticalWriting=$verticalWriting,
                    TwoPageMode=$twoPageMode, ParagraphIndent=$paragraphIndent,
                    LayoutUpdatedAt=$updatedAt, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$fontScale", settings.FontScale);
            command.Parameters.AddWithValue("$lineHeight", settings.LineHeight);
            command.Parameters.AddWithValue("$maxWidth", settings.MaxWidth);
            command.Parameters.AddWithValue("$bodyPadding", settings.BodyPadding);
            command.Parameters.AddWithValue("$fontFamily", string.IsNullOrWhiteSpace(settings.FontFamily) ? DBNull.Value : settings.FontFamily);
            command.Parameters.AddWithValue("$flowMode", settings.FlowMode);
            command.Parameters.AddWithValue("$verticalWriting", settings.VerticalWriting ? 1 : 0);
            command.Parameters.AddWithValue("$twoPageMode", settings.TwoPageMode ? 1 : 0);
            command.Parameters.AddWithValue("$paragraphIndent", settings.ParagraphIndent ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingLayout);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<ReaderBookLayoutProfile?> GetBookLayoutProfileAsync(
        Guid bookId,
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        ReaderLayoutSettings? layout = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode,
                       VerticalWriting, TwoPageMode, ParagraphIndent
                FROM ReaderLayoutSettings
                WHERE BookId = $bookId
                ORDER BY CASE WHEN BookFileId = $bookFileId THEN 0 ELSE 1 END,
                         COALESCE(LayoutUpdatedAt, UpdatedAt) DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                layout = ReadLayoutSettings(reader);
        }

        if (layout is null) return null;

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                       AppearancePaperStrength, AppearanceFibersEnabled
                FROM ReaderLayoutSettings
                WHERE BookId = $bookId
                ORDER BY CASE WHEN ProfileUpdatedAt IS NULL THEN 1 ELSE 0 END,
                         ProfileUpdatedAt DESC, UpdatedAt DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var appearance = ReaderAppearanceSettings.Normalize(new ReaderAppearanceSettings
            {
                Theme = Enum.IsDefined((ReaderTheme)reader.GetInt32(1))
                    ? (ReaderTheme)reader.GetInt32(1)
                    : ReaderTheme.Classic,
                PaperEnabled = reader.GetInt32(2) != 0,
                PaperStrength = reader.GetDouble(3),
                FibersEnabled = reader.GetInt32(4) != 0
            });
            return new ReaderBookLayoutProfile(
                bookId,
                bookFileId,
                reader.GetInt32(0) != 0,
                ReaderLayoutDefaults.Normalize(layout),
                appearance);
        }
    }

    public async Task SaveBookLayoutProfileAsync(
        ReaderBookLayoutProfile profile,
        CancellationToken cancellationToken = default)
    {
        profile = ReaderBookLayoutProfile.Normalize(profile);
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var appearance = profile.Appearance;
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE ReaderLayoutSettings
                    SET UseBookOverride = $useBookOverride,
                        AppearanceTheme = $theme,
                        AppearancePaperEnabled = $paperEnabled,
                        AppearancePaperStrength = $paperStrength,
                        AppearanceFibersEnabled = $fibersEnabled,
                        ProfileUpdatedAt = $profileUpdatedAt,
                        UpdatedAt = CASE WHEN julianday(UpdatedAt) < julianday($updatedAt)
                            THEN $updatedAt ELSE UpdatedAt END
                    WHERE BookId = $bookId;
                    """;
                AddBookProfileParameters(update, profile, appearance, timestamp);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = (SqliteTransaction)transaction;
                upsert.CommandText = """
                    INSERT INTO ReaderLayoutSettings (
                        BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                        FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent,
                        UseBookOverride, AppearanceTheme, AppearancePaperEnabled,
                        AppearancePaperStrength, AppearanceFibersEnabled, ProfileUpdatedAt, LayoutUpdatedAt, UpdatedAt)
                    VALUES (
                        $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                        $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $paragraphIndent,
                        $useBookOverride, $theme, $paperEnabled, $paperStrength, $fibersEnabled,
                        $profileUpdatedAt, $layoutUpdatedAt, $updatedAt)
                    ON CONFLICT(BookFileId) DO UPDATE SET
                        BookId = excluded.BookId,
                        FontScale = excluded.FontScale,
                        LineHeight = excluded.LineHeight,
                        MaxWidth = excluded.MaxWidth,
                        BodyPadding = excluded.BodyPadding,
                        FontFamily = excluded.FontFamily,
                        FlowMode = excluded.FlowMode,
                        VerticalWriting = excluded.VerticalWriting,
                        TwoPageMode = excluded.TwoPageMode,
                        ParagraphIndent = excluded.ParagraphIndent,
                        UseBookOverride = excluded.UseBookOverride,
                        AppearanceTheme = excluded.AppearanceTheme,
                        AppearancePaperEnabled = excluded.AppearancePaperEnabled,
                        AppearancePaperStrength = excluded.AppearancePaperStrength,
                        AppearanceFibersEnabled = excluded.AppearanceFibersEnabled,
                        ProfileUpdatedAt = excluded.ProfileUpdatedAt,
                        LayoutUpdatedAt = CASE WHEN $layoutChanged = 1 THEN excluded.LayoutUpdatedAt ELSE ReaderLayoutSettings.LayoutUpdatedAt END,
                        UpdatedAt = CASE WHEN $layoutChanged = 1 THEN excluded.UpdatedAt ELSE ReaderLayoutSettings.UpdatedAt END;
                    """;
                AddBookProfileParameters(upsert, profile, appearance, timestamp);
                AddLayoutParameters(upsert, profile.Layout);
                var existingLayout = await ReadLayoutSettingsForBookFileAsync(
                    connection, (SqliteTransaction)transaction, profile.BookFileId, cancellationToken);
                var layoutChanged = existingLayout is null || !LayoutsEqual(existingLayout, profile.Layout);
                upsert.Parameters.AddWithValue("$layoutUpdatedAt", layoutChanged ? timestamp : DBNull.Value);
                upsert.Parameters.AddWithValue("$layoutChanged", layoutChanged ? 1 : 0);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            NotifyDataChanged(LocalDataChangeKind.ReadingLayout);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static ReaderLayoutSettings ReadLayoutSettings(SqliteDataReader reader) => new(
        reader.GetDouble(0),
        reader.GetDouble(1),
        reader.GetDouble(2),
        reader.GetDouble(3),
        reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt32(6) != 0,
        reader.GetInt32(7) != 0)
    {
        ParagraphIndent = reader.GetInt32(8) != 0
    };

    private static bool LayoutsEqual(ReaderLayoutSettings left, ReaderLayoutSettings right) =>
        Math.Abs(left.FontScale - right.FontScale) < 0.0001
        && Math.Abs(left.LineHeight - right.LineHeight) < 0.0001
        && Math.Abs(left.MaxWidth - right.MaxWidth) < 0.0001
        && Math.Abs(left.BodyPadding - right.BodyPadding) < 0.0001
        && string.Equals(left.FontFamily, right.FontFamily, StringComparison.Ordinal)
        && left.FlowMode == right.FlowMode
        && left.VerticalWriting == right.VerticalWriting
        && left.TwoPageMode == right.TwoPageMode
        && left.ParagraphIndent == right.ParagraphIndent;

    private static async Task<ReaderLayoutSettings?> ReadLayoutSettingsForBookFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid bookFileId,
        CancellationToken cancellationToken)
    {
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode, VerticalWriting, TwoPageMode, ParagraphIndent FROM ReaderLayoutSettings WHERE BookFileId = $bookFileId;";
        select.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLayoutSettings(reader) : null;
    }

    private static void AddBookProfileParameters(
        SqliteCommand command,
        ReaderBookLayoutProfile profile,
        ReaderAppearanceSettings appearance,
        string timestamp)
    {
        command.Parameters.AddWithValue("$bookId", profile.BookId.ToString());
        command.Parameters.AddWithValue("$bookFileId", profile.BookFileId.ToString());
        command.Parameters.AddWithValue("$useBookOverride", profile.UseBookOverride ? 1 : 0);
        command.Parameters.AddWithValue("$theme", (int)appearance.Theme);
        command.Parameters.AddWithValue("$paperEnabled", appearance.PaperEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$paperStrength", appearance.PaperStrength);
        command.Parameters.AddWithValue("$fibersEnabled", appearance.FibersEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$profileUpdatedAt", timestamp);
        command.Parameters.AddWithValue("$updatedAt", timestamp);
    }

    private static void AddLayoutParameters(SqliteCommand command, ReaderLayoutSettings layout)
    {
        command.Parameters.AddWithValue("$fontScale", layout.FontScale);
        command.Parameters.AddWithValue("$lineHeight", layout.LineHeight);
        command.Parameters.AddWithValue("$maxWidth", layout.MaxWidth);
        command.Parameters.AddWithValue("$bodyPadding", layout.BodyPadding);
        command.Parameters.AddWithValue("$fontFamily", layout.FontFamily);
        command.Parameters.AddWithValue("$flowMode", layout.FlowMode);
        command.Parameters.AddWithValue("$verticalWriting", layout.VerticalWriting ? 1 : 0);
        command.Parameters.AddWithValue("$twoPageMode", layout.TwoPageMode ? 1 : 0);
        command.Parameters.AddWithValue("$paragraphIndent", layout.ParagraphIndent ? 1 : 0);
    }

    // ------------------------------------------------------------------
    // Reading stats (cumulative active reading time + progress snapshot).
    // ------------------------------------------------------------------
}
