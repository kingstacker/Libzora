using Libzora.Core;

namespace Libzora.Platform.MacOS.Mtp;

/// <summary>
/// Stable identity helpers for MTP readers. The USB bus/device address changes
/// on every replug, so it must never be used on its own to decide which reader
/// an operation targets. The serial number is the only identity libmtp exposes
/// that survives a reconnect; devices without one can only be addressed while
/// the original topology is still present.
/// </summary>
internal static class MtpDeviceIdentity
{
    internal const string Prefix = "mtp:";
    internal const string ModelPrefix = "mtp:model:";
    internal const string RootPrefix = "mtp://";

    public static string RootPath(MtpRawDevice raw) => $"{RootPrefix}{raw.BusLocation}/{raw.DevNum}";

    public static string Describe(string? serialNumber, string? model) =>
        !string.IsNullOrWhiteSpace(serialNumber)
            ? Prefix + serialNumber.Trim()
            : ModelPrefix + (string.IsNullOrWhiteSpace(model) ? "Kindle" : model.Trim());

    public static bool IsMtpIdentity(string? identity) =>
        !string.IsNullOrWhiteSpace(identity)
        && identity.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Only a serial-backed identity is safe to use after the bus/device
    /// address has changed. A model fallback cannot distinguish two identical
    /// Kindles and therefore fails closed.
    /// </summary>
    public static bool CanReconnectByIdentity(KindleDevice device) =>
        IsMtpIdentity(device.VolumeSerial)
        && !device.VolumeSerial.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks an opened session against the identity recorded when the reader
    /// was detected. The caller must dispose the session when this returns
    /// false; it belongs to a different device.
    /// </summary>
    public static bool Matches(
        KindleDevice device,
        string? serialNumber,
        string? model,
        string? openedRootPath = null)
    {
        var expected = (device.VolumeSerial ?? string.Empty).Trim();
        if (expected.Length == 0)
        {
            // Older callers may only have stored the USB root. It is weak, but
            // it still describes the exact reader that was opened.
            return !string.IsNullOrWhiteSpace(openedRootPath)
                && string.Equals(
                    device.RootPath.Trim(),
                    openedRootPath.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }
        if (!IsMtpIdentity(expected)) return false;
        return string.Equals(expected, Describe(serialNumber, model), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Remembers the last observed USB root for each MTP identity. The attachment
/// probe must not open a session on a reader the user just stopped, so it can
/// only compare identities against raw USB topology. An identity that cannot
/// be resolved to exactly one remembered root fails closed (reported as still
/// attached) so the latch is never released for the wrong reader.
/// </summary>
internal sealed class MtpAttachmentTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<string>> _rootsByIdentity =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the readers seen by a successful detection pass. Identities
    /// that were not seen keep their previous root so their disappearance can
    /// still release the latch.
    /// </summary>
    public void Observe(IEnumerable<(string Identity, string RootPath)> detected)
    {
        lock (_sync)
        {
            foreach (var group in detected
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.Identity))
                         .GroupBy(entry => entry.Identity, StringComparer.OrdinalIgnoreCase))
            {
                var roots = group
                    .Select(entry => entry.RootPath)
                    .Where(root => !string.IsNullOrWhiteSpace(root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (roots.Count > 0) _rootsByIdentity[group.Key] = roots;
            }
        }
    }

    public bool IsAttached(string? identity, IReadOnlyCollection<string> attachedRootPaths)
    {
        var attached = attachedRootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (attached.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(identity)) return true;

        var normalized = identity.Trim();
        if (normalized.StartsWith(MtpDeviceIdentity.RootPrefix, StringComparison.OrdinalIgnoreCase))
            return attached.Contains(normalized);

        lock (_sync)
        {
            if (!_rootsByIdentity.TryGetValue(normalized, out var roots) || roots.Count != 1)
            {
                // Either this identity was never detected or it is ambiguous
                // (for example two serial-less Kindles of the same model).
                // Keeping the latch is safer than releasing it for another
                // reader.
                return true;
            }
            return attached.Contains(roots[0]);
        }
    }
}
