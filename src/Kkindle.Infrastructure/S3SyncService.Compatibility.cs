using Kkindle.Core;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    /// <summary>
    /// Snapshots written before the rating fields were renamed keep the old
    /// names as opaque extension data. Adopt their values into the typed
    /// fields so a sync does not silently drop ratings; the opaque copy stays
    /// in place for clients that still read the old name.
    /// </summary>
    private static void AdoptLegacyRatingFields(S3SyncSnapshot snapshot)
    {
        foreach (var book in snapshot.Books.Concat(snapshot.ReadingMaterialBooks))
        {
            if (book.ExtensionData is not { Count: > 0 } fields) continue;
            foreach (var (name, value) in fields)
            {
                if (value.ValueKind != JsonValueKind.Number) continue;
                if (book.OnlineRatingCount is null
                    && name.EndsWith("RatingCount", StringComparison.OrdinalIgnoreCase)
                    && value.TryGetInt32(out var count))
                {
                    book.OnlineRatingCount = count;
                }
                else if (book.OnlineRating is null
                    && name.EndsWith("Rating", StringComparison.OrdinalIgnoreCase)
                    && value.TryGetDouble(out var rating))
                {
                    book.OnlineRating = rating;
                }
            }
        }
    }

    private static void ValidateSnapshotCompatibility(S3SyncSnapshot snapshot)
    {
        var minimum = snapshot.MinimumReaderVersion ?? snapshot.Version;
        if (snapshot.Version < 1 || minimum < 1 || minimum > snapshot.Version)
            throw new InvalidDataException("Invalid sync snapshot compatibility declaration.");
        if (minimum > SnapshotVersion)
            throw new InvalidDataException(UiText.Get(
                "同步快照版本 {0} 高于当前版本。请先升级 Kkindle。", minimum));
    }

    private static void PreserveForwardCompatibleData(S3SyncSnapshot target,
        S3SyncSnapshot? baseline, IReadOnlyList<S3SyncSnapshot> remotes)
    {
        var sources = remotes.Concat(baseline is null ? [] : new[] { baseline })
            .OrderBy(snapshot => snapshot.CreatedAt)
            .ThenBy(snapshot => snapshot.DeviceId, StringComparer.Ordinal).ToArray();
        foreach (var source in sources)
        {
            MergeOpaqueFields(target, source, source.CreatedAt);
            if (target.ReadingDataReset == source.ReadingDataReset)
                MergeOpaqueFields(target.ReadingDataReset, source.ReadingDataReset, source.CreatedAt, target, source, "ReadingDataReset/");
        }

        var namingSalts = sources.Append(target).SelectMany(snapshot => snapshot.BlobNamingSalts ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        target.BlobNamingSalts = namingSalts.Count == 0 ? null : namingSalts.ToArray();

        // Only surviving rows receive opaque fields. Never resurrect deleted
        // entities merely to carry data this reader does not understand.
        foreach (var entity in SyncEntities)
        {
            var sourceRows = sources.SelectMany(entity.Rows)
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(row => row.Version).ToArray(), StringComparer.OrdinalIgnoreCase);
            foreach (var row in entity.Rows(target))
                if (sourceRows.TryGetValue(row.Key, out var matches))
                    foreach (var source in matches)
                        MergeOpaqueFields(row.Value, source.Value, source.Version);
        }
        var tombstones = sources.SelectMany(snapshot => snapshot.Tombstones)
            .GroupBy(row => VersionKey(row.EntityType, row.Key))
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.DeletedAt).ToArray());
        foreach (var row in target.Tombstones)
            if (tombstones.TryGetValue(VersionKey(row.EntityType, row.Key), out var matches))
                foreach (var source in matches) MergeOpaqueFields(row, source, source.DeletedAt);

        foreach (var source in sources.Where(snapshot => snapshot.Settings is not null))
        {
            var settings = source.Settings!;
            MergeOpaqueFields(target.Settings, settings, settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.App, settings.App, settings.AppUpdatedAt ?? settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.Ai, settings.Ai, settings.AiUpdatedAt ?? settings.UpdatedAt);
            MergeOpaqueFields(target.Settings?.KindleEmail, settings.KindleEmail,
                settings.KindleEmailUpdatedAt ?? settings.UpdatedAt);
        }
        // This reader republishes its own wire version, not a version whose
        // features it cannot implement. Unknown fields remain explicitly opaque.
        target.Version = SnapshotVersion;
        target.MinimumReaderVersion = SnapshotVersion;
    }

    private static void MergeOpaqueFields(object? target, object? source, DateTimeOffset version,
        S3SyncExtensibleData? targetClock = null, S3SyncExtensibleData? sourceClock = null, string prefix = "")
    {
        if (target is null || source is null) return;
        var property = ExtensionProperty(source.GetType());
        if (property?.GetValue(source) is Dictionary<string, JsonElement> fields && fields.Count > 0)
        {
            var destination = property.GetValue(target) as Dictionary<string, JsonElement>;
            if (destination is null)
            {
                destination = new(StringComparer.Ordinal);
                property.SetValue(target, destination);
            }
            var targetData = targetClock ?? target as S3SyncExtensibleData;
            var sourceData = sourceClock ?? source as S3SyncExtensibleData;
            if (targetData is not null) targetData.ExtensionDataVersions ??= new(StringComparer.Ordinal);
            foreach (var (name, value) in fields)
            {
                var clockKey = prefix + name;
                var fieldVersion = sourceData?.ExtensionDataVersions?.TryGetValue(clockKey, out var stored) == true ? stored : version;
                var hasDestination = destination.TryGetValue(name, out var existing);
                DateTimeOffset current = default;
                var hasCurrent = targetData?.ExtensionDataVersions?.TryGetValue(clockKey, out current) == true;

                // A future client may add a new collection (for example
                // per-device listening time) as an opaque array. Merging the
                // array by row ID keeps rows learned from every device even
                // when an older client republishes the snapshot.
                if (hasDestination
                    && TryMergeVersionedArrays(existing, value, out var mergedArray))
                {
                    destination[name] = mergedArray;
                    if (targetData is not null)
                    {
                        targetData.ExtensionDataVersions![clockKey] =
                            hasCurrent ? Max(current, fieldVersion) : fieldVersion;
                    }
                    continue;
                }

                if (hasCurrent
                    && (current > fieldVersion || current == fieldVersion
                        && hasDestination
                        && string.CompareOrdinal(existing.GetRawText(), value.GetRawText()) >= 0)) continue;
                destination[name] = value.Clone();
                if (targetData is not null) targetData.ExtensionDataVersions![clockKey] = fieldVersion;
            }
        }
        // DefaultReaderLayout is a shared Core type, not a snapshot DTO.
        if (target is S3SyncAppSettings targetApp && source is S3SyncAppSettings sourceApp)
            MergeOpaqueFields(targetApp.DefaultReaderLayout, sourceApp.DefaultReaderLayout, version,
                targetApp, sourceApp, "DefaultReaderLayout/");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> ExtensionProperties = new();
    private static PropertyInfo? ExtensionProperty(Type type) => ExtensionProperties.GetOrAdd(type,
        static value => value.GetProperties().Where(property =>
            property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null).ToArray()).FirstOrDefault();

    private readonly record struct OpaqueRow(string Key, DateTimeOffset Version, JsonElement Row);

    /// <summary>
    /// Opaque arrays are merged row-wise when each row has a stable ID/key and
    /// a timestamp. This is what lets an older client preserve a newer
    /// client's additive collection (listening seconds, device counters, ...)
    /// instead of replacing the whole array with the older copy.
    /// </summary>
    private static bool TryMergeVersionedArrays(
        JsonElement existing,
        JsonElement incoming,
        out JsonElement merged)
    {
        merged = default;
        if (existing.ValueKind != JsonValueKind.Array || incoming.ValueKind != JsonValueKind.Array)
            return false;
        if (!TryReadOpaqueRows(existing, out var rows) || !TryReadOpaqueRows(incoming, out var sourceRows))
            return false;

        foreach (var (key, source) in sourceRows)
        {
            if (!rows.TryGetValue(key, out var current)
                || source.Version > current.Version
                || source.Version == current.Version
                && string.CompareOrdinal(source.Row.GetRawText(), current.Row.GetRawText()) > 0)
            {
                rows[key] = source;
            }
        }

        merged = JsonSerializer.SerializeToElement(rows.Values
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => row.Row)
            .ToArray());
        return true;
    }

    private static bool TryReadOpaqueRows(
        JsonElement array,
        out Dictionary<string, OpaqueRow> rows)
    {
        rows = new Dictionary<string, OpaqueRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryReadOpaqueRowKey(element, out var key)
                || !TryReadOpaqueRowVersion(element, out var version))
                return false;
            rows[key] = new OpaqueRow(key, version, element.Clone());
        }
        return true;
    }

    private static bool TryReadOpaqueRowKey(JsonElement row, out string key)
    {
        key = string.Empty;
        if (!TryGetJsonProperty(row, "id", out var value)
            && !TryGetJsonProperty(row, "key", out value))
            return false;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                key = value.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
                key = value.GetRawText();
                break;
            default:
                return false;
        }
        return key.Length > 0;
    }

    private static bool TryReadOpaqueRowVersion(JsonElement row, out DateTimeOffset version)
    {
        version = DateTimeOffset.MinValue;
        var found = false;
        foreach (var name in new[] { "updatedAt", "modifiedAt", "deletedAt", "createdAt" })
        {
            if (!TryGetJsonProperty(row, name, out var value)
                || value.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
                continue;
            if (!found || parsed > version) version = parsed;
            found = true;
        }
        return found;
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool HasForwardCompatibleData(S3SyncSnapshot snapshot)
    {
        if (snapshot.Version > SnapshotVersion || HasOpaqueFields(snapshot)
            || HasOpaqueFields(snapshot.ReadingDataReset) || HasOpaqueFields(snapshot.Settings)
            || HasOpaqueFields(snapshot.Settings?.App) || HasOpaqueFields(snapshot.Settings?.App?.DefaultReaderLayout)
            || HasOpaqueFields(snapshot.Settings?.Ai) || HasOpaqueFields(snapshot.Settings?.KindleEmail)) return true;
        return SyncEntities.Any(entity => entity.Rows(snapshot).Any(row => HasOpaqueFields(row.Value)))
            || snapshot.Tombstones.Any(HasOpaqueFields);
    }

    private static bool HasOpaqueFields(object? value) => value is not null
        && ExtensionProperty(value.GetType())?.GetValue(value) is Dictionary<string, JsonElement> { Count: > 0 };
}
