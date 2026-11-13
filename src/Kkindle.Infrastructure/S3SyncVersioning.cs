using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

// Snapshot row versions use a hybrid logical clock so a device that observed
// another device's change always writes a newer version, even when the two
// wall clocks disagree. The wire format stays a fixed-width string so versions
// can also be compared lexicographically by older code or diagnostic tools.
internal static class S3SyncVersion
{
    private const int PhysicalDigits = 19;
    private const int CounterDigits = 10;
    private const long MaximumCounter = 9_999_999_999;

    public static string Create(long physicalTicks, long counter, string deviceId)
    {
        physicalTicks = Math.Max(0, physicalTicks);
        counter = Math.Clamp(counter, 0, MaximumCounter);
        return string.Create(CultureInfo.InvariantCulture,
            $"{physicalTicks:D19}:{counter:D10}:{CleanDeviceId(deviceId)}");
    }

    public static bool TryParse(string? value, out long physicalTicks, out long counter, out string deviceId)
    {
        physicalTicks = 0;
        counter = 0;
        deviceId = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var first = value.IndexOf(':');
        if (first <= 0) return false;
        var second = value.IndexOf(':', first + 1);
        if (second <= first + 1) return false;
        if (!long.TryParse(value.AsSpan(0, first), NumberStyles.None, CultureInfo.InvariantCulture, out physicalTicks)
            || !long.TryParse(value.AsSpan(first + 1, second - first - 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out counter))
            return false;
        deviceId = value[(second + 1)..];
        return deviceId.Length > 0;
    }

    public static int Compare(string? left, DateTimeOffset leftTime, string? right, DateTimeOffset rightTime)
    {
        var leftParsed = TryParse(left, out var leftTicks, out _, out _);
        var rightParsed = TryParse(right, out var rightTicks, out _, out _);
        if (leftParsed && rightParsed)
        {
            var leftOrder = leftTicks;
            var rightOrder = rightTicks;
            return leftOrder != rightOrder
                ? leftOrder.CompareTo(rightOrder)
                : string.CompareOrdinal(left, right);
        }
        // At least one row predates hybrid-clock versions (or was written by
        // an older client that keeps them in unknown data). A version's
        // physical component is the capture clock, which can be later than the
        // row's own UpdatedAt, so comparing it with another row's wall clock
        // would make legacy rows lose to unrelated sync activity. Both sides
        // expose a row timestamp, so use that as the common ordering source.
        var timeOrder = leftTime.UtcTicks.CompareTo(rightTime.UtcTicks);
        if (timeOrder != 0) return timeOrder;
        // A versioned row and an old unversioned row at the same instant are
        // ordered in favour of the versioned row. This makes an upgrade
        // deterministic without inventing a device identity for old rows.
        if (leftParsed != rightParsed) return leftParsed ? 1 : -1;
        return 0;
    }

    public static bool IsNewer(string? candidateVersion, DateTimeOffset candidateTime,
        string? currentVersion, DateTimeOffset currentTime) =>
        Compare(candidateVersion, candidateTime, currentVersion, currentTime) > 0;

    public static long PhysicalTicks(string? version, DateTimeOffset fallback) =>
        TryParse(version, out var physical, out _, out _) ? physical : fallback.UtcTicks;

    private static string CleanDeviceId(string deviceId)
    {
        var value = (deviceId ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return "unknown";
        return value.Replace(':', '_');
    }
}

internal sealed class S3SyncClock
{
    private static readonly long MaximumOffsetTicks = TimeSpan.FromDays(30).Ticks;
    private readonly string _deviceId;
    private long _physicalTicks;
    private long _counter;
    private long _serverOffsetTicks;

    private S3SyncClock(string deviceId, long physicalTicks, long counter, long serverOffsetTicks)
    {
        _deviceId = deviceId;
        _physicalTicks = physicalTicks;
        _counter = counter;
        _serverOffsetTicks = serverOffsetTicks;
    }

    public string DeviceId => _deviceId;

    public static async Task<S3SyncClock> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId,
        CancellationToken cancellationToken)
    {
        using var command = S3SyncService.CreateCommand(connection, transaction, """
            SELECT DeviceId, PhysicalTicks, Counter, ServerOffsetTicks
            FROM S3SyncClock WHERE Id = 1;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new S3SyncClock(deviceId, 0, 0, 0);

        var storedDevice = reader.GetString(0);
        var physical = reader.GetInt64(1);
        var counter = reader.GetInt64(2);
        var offset = reader.GetInt64(3);
        if (!string.Equals(storedDevice, deviceId, StringComparison.Ordinal))
            counter = 0;
        return new S3SyncClock(deviceId, physical, counter, offset);
    }

    public async Task SaveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = S3SyncService.CreateCommand(connection, transaction, """
            INSERT INTO S3SyncClock (Id, DeviceId, PhysicalTicks, Counter, ServerOffsetTicks)
            VALUES (1, $deviceId, $physicalTicks, $counter, $serverOffsetTicks)
            ON CONFLICT(Id) DO UPDATE SET
                DeviceId = excluded.DeviceId,
                PhysicalTicks = excluded.PhysicalTicks,
                Counter = excluded.Counter,
                ServerOffsetTicks = excluded.ServerOffsetTicks;
            """);
        S3SyncService.AddParameter(command, "$deviceId", _deviceId);
        S3SyncService.AddParameter(command, "$physicalTicks", _physicalTicks);
        S3SyncService.AddParameter(command, "$counter", _counter);
        S3SyncService.AddParameter(command, "$serverOffsetTicks", _serverOffsetTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void ObserveServerOffset(TimeSpan offset)
    {
        _serverOffsetTicks = Math.Clamp(offset.Ticks, -MaximumOffsetTicks, MaximumOffsetTicks);
        var correctedNow = Correct(DateTimeOffset.UtcNow);
        if (correctedNow > _physicalTicks)
        {
            _physicalTicks = correctedNow;
            _counter = 0;
        }
    }

    public void ObserveVersion(string? version)
    {
        if (!S3SyncVersion.TryParse(version, out var physical, out var counter, out _)) return;
        if (physical > _physicalTicks)
        {
            _physicalTicks = physical;
            _counter = counter;
        }
        else if (physical == _physicalTicks && counter > _counter)
        {
            _counter = counter;
        }
    }

    public string Next(DateTimeOffset time)
    {
        var corrected = Correct(time == default ? DateTimeOffset.UtcNow : time);
        if (corrected > _physicalTicks)
        {
            _physicalTicks = corrected;
            _counter = 0;
        }
        else
        {
            _counter++;
            if (_counter > 9_999_999_999)
            {
                _physicalTicks++;
                _counter = 0;
            }
        }
        return S3SyncVersion.Create(_physicalTicks, _counter, _deviceId);
    }

    private long Correct(DateTimeOffset time)
    {
        var ticks = time.UtcTicks + _serverOffsetTicks;
        return Math.Max(0, ticks);
    }
}
