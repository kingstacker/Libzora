using System.Runtime.InteropServices;

namespace Libzora.Platform.Windows;

/// <summary>
/// Enumerates attached WPD (portable device) Plug and Play IDs without
/// opening a device session. The Shell path used by
/// <see cref="WpdKindleAccess"/> opens the storage object, which must be
/// avoided while the user has explicitly stopped access to the reader.
/// </summary>
internal static class WpdAttachmentProbe
{
    private static readonly Guid PortableDeviceManagerClassId = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");

    public static bool IsDeviceAttached(string deviceIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TryGetDeviceIds(out var deviceIds)
            ? deviceIds.Any(id => SameDevice(id, deviceIdentity))
            // If the manager cannot answer, keep the manual-disconnect latch:
            // reopening the stopped reader would defeat the user's request.
            : true;
    }

    public static bool HasAnyDevice(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return !TryGetDeviceIds(out var deviceIds) || deviceIds.Count > 0;
    }

    private static bool SameDevice(string pnpDeviceId, string identity)
    {
        var left = Normalize(pnpDeviceId);
        var right = Normalize(identity);
        return left.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var start = value.IndexOf(@"\\?\", StringComparison.OrdinalIgnoreCase);
        var normalized = (start >= 0 ? value[start..] : value).Trim().TrimEnd('\\');
        // The Shell item path may append the WPD interface class GUID while
        // the PnP ID may omit it, so compare the device instance part only.
        var interfaceSuffix = normalized.LastIndexOf("#{", StringComparison.Ordinal);
        return interfaceSuffix > 0 ? normalized[..interfaceSuffix] : normalized;
    }

    private static bool TryGetDeviceIds(out IReadOnlyList<string> deviceIds)
    {
        deviceIds = [];
        object? managerObject = null;
        try
        {
            var managerType = Type.GetTypeFromCLSID(PortableDeviceManagerClassId, throwOnError: true);
            managerObject = managerType is null ? null : Activator.CreateInstance(managerType);
            if (managerObject is not IPortableDeviceManager manager) return false;

            uint count = 0;
            if (manager.GetDevices(IntPtr.Zero, ref count) < 0) return false;
            if (count == 0) return true;

            var capacity = checked((int)count);
            var buffer = Marshal.AllocCoTaskMem(IntPtr.Size * capacity);
            try
            {
                if (manager.GetDevices(buffer, ref count) < 0) return false;
                if (count > capacity) return false;
                var ids = new List<string>((int)count);
                for (var index = 0; index < (int)count; index++)
                {
                    var pointer = Marshal.ReadIntPtr(buffer, index * IntPtr.Size);
                    if (pointer == IntPtr.Zero) continue;
                    try
                    {
                        if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } id) ids.Add(id);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
                deviceIds = ids;
                return true;
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException
            or TypeInitializationException or ArgumentException or NotSupportedException or OverflowException)
        {
            // Treat an unavailable WPD manager as "unknown" so the caller keeps
            // the manual-disconnect latch instead of reopening the reader.
            System.Diagnostics.Debug.WriteLine($"WPD attachment probe failed: {exception}");
            return false;
        }
        finally
        {
            if (managerObject is not null && Marshal.IsComObject(managerObject))
                Marshal.ReleaseComObject(managerObject);
        }
    }

    [ComImport]
    [Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceManager
    {
        [PreserveSig]
        int GetDevices(IntPtr deviceIds, ref uint deviceIdCount);
    }
}
