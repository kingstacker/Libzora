using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

internal static partial class ReadingTimeSyncTracker
{
    private static async Task EnsureListeningCountersAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        using var inspect = Command(connection, transaction, "PRAGMA table_info(S3SyncReadingTimeCounters);");
        var exists = false;
        await using (var rows = await inspect.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) exists |= rows.GetString(1) == "ListeningSeconds";
        if (!exists)
        {
            using var alter = Command(connection, transaction,
                "ALTER TABLE S3SyncReadingTimeCounters ADD COLUMN ListeningSeconds INTEGER NOT NULL DEFAULT 0 CHECK (ListeningSeconds >= 0);");
            await alter.ExecuteNonQueryAsync(token);
        }
        // Old local totals are attached once to this device/file component, not
        // to 'legacy' (which would collapse independent listening on two devices).
        var localDevice = await ReadLocalDeviceIdAsync(connection, transaction, token);
        var legacy = new List<(Guid File, long Seconds)>();
        using (var read = Command(connection, transaction, """
            SELECT BookFileId, ListeningSeconds FROM ReaderReadingStats
            WHERE ListeningSeconds > 0 AND NOT EXISTS (
                SELECT 1 FROM S3SyncLocalMetadata WHERE Key='listening-time-v1');
            """))
        await using (var rows = await read.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token)) legacy.Add((Guid.Parse(rows.GetString(0)), rows.GetInt64(1)));
        foreach (var row in legacy)
            await SetListeningMaximumAsync(connection, transaction, row.File, LocalCounterId(localDevice, row.File), row.Seconds, token);
        using var marker = Command(connection, transaction,
            "INSERT OR IGNORE INTO S3SyncLocalMetadata (Key,Value) VALUES ('listening-time-v1','1');");
        await marker.ExecuteNonQueryAsync(token);
    }

    private static async Task<Dictionary<string, long>> ReadListeningAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid file, CancellationToken token)
    {
        using var command = Command(connection, transaction,
            "SELECT DeviceId, ListeningSeconds FROM S3SyncReadingTimeCounters WHERE BookFileId=$file AND ListeningSeconds>0;",
            ("$file", file.ToString()));
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var rows = await command.ExecuteReaderAsync(token);
        while (await rows.ReadAsync(token)) values[rows.GetString(0)] = rows.GetInt64(1);
        return values;
    }

    private static async Task SetListeningMaximumAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid file, string device, long seconds, CancellationToken token)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO S3SyncReadingTimeCounters (BookFileId,DeviceId,Seconds,ListeningSeconds)
            VALUES ($file,$device,0,$seconds)
            ON CONFLICT(BookFileId,DeviceId) DO UPDATE SET
                ListeningSeconds=MAX(ListeningSeconds,excluded.ListeningSeconds);
            """, ("$file", file.ToString()), ("$device", device), ("$seconds", seconds));
        await command.ExecuteNonQueryAsync(token);
    }

    public static async Task RecordListeningTotalAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid file, CancellationToken token)
    {
        using var read = Command(connection, transaction,
            "SELECT ListeningSeconds FROM ReaderReadingStats WHERE BookFileId=$file;", ("$file", file.ToString()));
        var local = Convert.ToInt64(await read.ExecuteScalarAsync(token));
        var values = await ReadListeningAsync(connection, transaction, file, token);
        var known = Total(values);
        if (local > known)
        {
            var device = await GetLocalCounterIdAsync(connection, transaction, file, token);
            await SetListeningMaximumAsync(connection, transaction, file, device, checked(values.GetValueOrDefault(device) + local - known), token);
            known = local;
        }
        using var update = Command(connection, transaction, """
            UPDATE ReaderReadingStats SET ListeningSeconds=MIN(CumulativeSeconds,MAX(ListeningSeconds,$total))
            WHERE BookFileId=$file;
            """, ("$file", file.ToString()), ("$total", known));
        await update.ExecuteNonQueryAsync(token);
    }

    public static async Task<long> MergeListeningAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid file, IReadOnlyDictionary<string, long>? counters, CancellationToken token)
    {
        await RecordListeningTotalAsync(connection, transaction, file, token);
        if (counters is not null)
            foreach (var (raw, seconds) in counters)
            {
                var device = NormalizeDeviceId(raw);
                if (device is null || seconds < 0) throw new InvalidDataException(UiText.Get("S3 阅读时长计数无效。"));
                await SetListeningMaximumAsync(connection, transaction, file, device, seconds, token);
            }
        return Total(await ReadListeningAsync(connection, transaction, file, token));
    }

    public static async Task MergeListeningDaysAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid file, IReadOnlyDictionary<string, Dictionary<string, long>>? counters, CancellationToken token)
    {
        if (counters is null) return;
        foreach (var (raw, dates) in counters)
        {
            var device = NormalizeDeviceId(raw);
            if (device is null) throw new InvalidDataException(UiText.Get("S3 阅读日期计数的设备标识无效。"));
            foreach (var (date, seconds) in dates)
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _) || seconds < 0)
                    throw new InvalidDataException(UiText.Get("S3 阅读日期计数无效。"));
                using var command = Command(connection, transaction, """
                    UPDATE S3SyncReadingDayCounters SET ListeningSeconds=MIN(Seconds,MAX(ListeningSeconds,$seconds))
                    WHERE BookFileId=$file AND ReadingDate=$date AND DeviceId=$device;
                    """, ("$file", file.ToString()), ("$date", date), ("$device", device), ("$seconds", seconds));
                await command.ExecuteNonQueryAsync(token);
            }
        }
    }

    private static async Task CaptureAllListeningAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<S3SyncReadingStats> stats, CancellationToken token)
    {
        var totals = new Dictionary<Guid, Dictionary<string, long>>();
        var days = new Dictionary<Guid, Dictionary<string, Dictionary<string, long>>>();
        using (var command = Command(connection, transaction,
            "SELECT BookFileId,DeviceId,ListeningSeconds FROM S3SyncReadingTimeCounters WHERE ListeningSeconds>0;"))
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                var file = Guid.Parse(rows.GetString(0));
                if (!totals.TryGetValue(file, out var values)) totals[file] = values = new(StringComparer.Ordinal);
                values[rows.GetString(1)] = rows.GetInt64(2);
            }
        using (var command = Command(connection, transaction,
            "SELECT BookFileId,DeviceId,ReadingDate,ListeningSeconds FROM S3SyncReadingDayCounters WHERE ListeningSeconds>0;"))
        await using (var rows = await command.ExecuteReaderAsync(token))
            while (await rows.ReadAsync(token))
            {
                var file = Guid.Parse(rows.GetString(0)); var device = rows.GetString(1);
                if (!days.TryGetValue(file, out var byDevice)) days[file] = byDevice = new(StringComparer.Ordinal);
                if (!byDevice.TryGetValue(device, out var dates)) byDevice[device] = dates = new(StringComparer.Ordinal);
                dates[rows.GetString(2)] = rows.GetInt64(3);
            }
        foreach (var item in stats)
        {
            item.ListeningSecondsByDevice = totals.GetValueOrDefault(item.BookFileId) ?? new(StringComparer.Ordinal);
            item.ListeningSecondsByDateByDevice = days.GetValueOrDefault(item.BookFileId) ?? new(StringComparer.Ordinal);
        }
    }
}
