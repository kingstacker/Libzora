using System.Runtime.InteropServices;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Minimal Metal query used to pick a rendering mode before Avalonia starts.
/// </summary>
public static class MacOSGraphicsInfo
{
    private const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    /// Returns the system default Metal device name, or <c>null</c> when Metal
    /// is unavailable. Must be called before Avalonia initializes its renderer.
    /// </summary>
    public static string? TryGetMetalDeviceName()
    {
        if (!OperatingSystem.IsMacOS()) return null;

        var library = IntPtr.Zero;
        var pool = IntPtr.Zero;
        try
        {
            library = NativeLibrary.Load(MetalFramework);
            var symbol = NativeLibrary.GetExport(library, "MTLCreateSystemDefaultDevice");
            var createDevice = Marshal.GetDelegateForFunctionPointer<CreateDevice>(symbol);
            var device = createDevice();
            if (device == IntPtr.Zero) return null;

            pool = objc_autoreleasePoolPush();
            try
            {
                var name = Send(device, Sel("name"));
                var utf8 = name == IntPtr.Zero ? IntPtr.Zero : Send(name, Sel("UTF8String"));
                return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
            }
            finally
            {
                // MTLCreateSystemDefaultDevice follows the Core Foundation
                // create rule, so the caller owns this reference.
                SendVoid(device, Sel("release"));
            }
        }
        catch (Exception)
        {
            // GPU detection is best effort and must never block startup.
            return null;
        }
        finally
        {
            if (pool != IntPtr.Zero) objc_autoreleasePoolPop(pool);
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateDevice();

    [DllImport(ObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);
}
