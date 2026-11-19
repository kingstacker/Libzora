using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>Apple Foundation-backed access to the entitled iCloud Drive container.</summary>
public sealed class AppleICloudSyncFileSystem : IICloudSyncFileSystem
{
    private static readonly NativeAccessor AccessorCallback = InvokeAccessor;
    private static readonly TimeSpan DownloadPollInterval = TimeSpan.FromMilliseconds(250);

    public string ContainerIdentifier => ICloudSyncContainer.Identifier;

    public string? AccountIdentity
    {
        get
        {
            RequireApple();
            var archive = Native.AccountArchive(out var error);
            var message = TakeString(error);
            var encoded = TakeString(archive);
            if (message is not null) throw FileOperationFailure(message);
            if (encoded is null) return null;
            try
            {
                return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(encoded)));
            }
            catch (FormatException exception)
            {
                throw new IOException(UiText.Get("iCloud 文件操作失败，请稍后重试。"), exception);
            }
        }
    }

    public Task<string> GetContainerDirectoryAsync(CancellationToken cancellationToken)
    {
        RequireApple();
        cancellationToken.ThrowIfCancellationRequested();
        // URLForUbiquityContainerIdentifier can block. Cancellation stops waiting;
        // the worker still owns and frees any result from that Foundation call.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = AccountIdentity ?? throw new InvalidOperationException(
                UiText.Get("请在系统设置中登录 Apple 账户并开启 iCloud 云盘。"));
            var directory = Native.Documents(ContainerIdentifier, out var error);
            var message = TakeString(error);
            var path = TakeString(directory);
            cancellationToken.ThrowIfCancellationRequested();
            if (message is not null || path is null)
                throw new IOException(UiText.Get("当前应用无法访问 iCloud 云盘，请检查 iCloud 设置和应用权限。"),
                    message is null ? null : new IOException(message));
            if (!string.Equals(identity, AccountIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException(UiText.Get("系统 iCloud 账户已更改。请确认当前 Apple 账户后重新保存同步设置。"));
            cancellationToken.ThrowIfCancellationRequested();
            return NormalizePath(path);
        }, cancellationToken).WaitAsync(cancellationToken);
    }

    public bool ItemExists(string path)
    {
        RequireApple();
        path = NormalizePath(path);
        var result = Native.ItemExists(path, out var error);
        var message = TakeString(error);
        if (result < 0 || message is not null)
            throw FileOperationFailure(message);
        return result == 1;
    }

    public Task EnsureDownloadedAsync(string path, CancellationToken cancellationToken)
    {
        RequireApple();
        path = NormalizePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(async () =>
        {
            var requestDownload = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only metadata is queried. The .name.icloud bytes are never read.
                var result = Native.DownloadStatus(path, requestDownload ? 1 : 0, out var error);
                var message = TakeString(error);
                cancellationToken.ThrowIfCancellationRequested();
                ThrowFailure(result, message, path);
                if (result == 0) return;
                if (result != 3) throw FileOperationFailure("The iCloud download returned an invalid status.");
                requestDownload = false;
                await Task.Delay(DownloadPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).WaitAsync(cancellationToken);
    }

    public Task EnsureUploadedAsync(string path, CancellationToken cancellationToken)
    {
        RequireApple();
        path = NormalizePath(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(async () =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Native.UploadStatus(path, out var error);
                var message = TakeString(error);
                cancellationToken.ThrowIfCancellationRequested();
                if (result == 2 || message is not null)
                    throw new IOException(UiText.Get("iCloud 文件上传失败，请检查网络连接和 iCloud 存储空间后重试。"),
                        message is null ? null : new IOException(message));
                ThrowFailure(result, message, path);
                if (result == 0) return;
                await Task.Delay(DownloadPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).WaitAsync(cancellationToken);
    }

    public T Coordinate<T>(string path, ICloudSyncFileAccess access, Func<string, T> accessor,
        CancellationToken cancellationToken)
    {
        RequireApple();
        ArgumentNullException.ThrowIfNull(accessor);
        path = NormalizePath(path);
        if (access is not (ICloudSyncFileAccess.Read or ICloudSyncFileAccess.Write or ICloudSyncFileAccess.Delete))
            throw new ArgumentOutOfRangeException(nameof(access));
        cancellationToken.ThrowIfCancellationRequested();
        var coordinator = Native.CoordinatorCreate(out var createError);
        var createMessage = TakeString(createError);
        if (coordinator == IntPtr.Zero || createMessage is not null)
        {
            if (coordinator != IntPtr.Zero) Native.CoordinatorRelease(coordinator);
            throw FileOperationFailure(createMessage);
        }

        GCHandle contextHandle = default;
        try
        {
            var context = new AccessorContext(relocatedPath => accessor(relocatedPath), cancellationToken);
            contextHandle = GCHandle.Alloc(context);
            // Dispose registration before releasing the coordinator: an in-flight
            // cancellation callback must never use a released Objective-C object.
            using var registration = cancellationToken.Register(static state =>
                Native.CoordinatorCancel((IntPtr)state!), coordinator);
            cancellationToken.ThrowIfCancellationRequested();
            var result = Native.Coordinate(coordinator, path, (int)access, AccessorCallback,
                GCHandle.ToIntPtr(contextHandle), out var error);
            var message = TakeString(error);
            context.Exception?.Throw();
            cancellationToken.ThrowIfCancellationRequested();
            ThrowFailure(result, message, path);
            if (!context.Invoked) throw new IOException("The file coordinator did not invoke its accessor.");
            return (T)context.Result!;
        }
        finally
        {
            if (contextHandle.IsAllocated) contextHandle.Free();
            Native.CoordinatorRelease(coordinator);
        }
    }

    public void DeleteItem(string path)
    {
        RequireApple();
        path = NormalizePath(path);
        var result = Native.Delete(path, out var error);
        ThrowFailure(result, TakeString(error), path);
    }

    private static IOException FileOperationFailure(string? detail) => new(
        UiText.Get("iCloud 文件操作失败，请稍后重试。"), detail is null ? null : new IOException(detail));

    private static void ThrowFailure(int result, string? message, string path)
    {
        if (result == 1) throw new SyncObjectNotFoundException(UiText.Get("iCloud 同步文件尚不可用，请稍后重试。"),
            message is null ? null : new IOException(message));
        if (result == 2 || message is not null) throw FileOperationFailure(message);
        if (result is not (0 or 3)) throw FileOperationFailure("The iCloud operation returned an invalid result.");
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new ArgumentException("An absolute iCloud path is required.", nameof(path));
        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        if (name.Length > 8 && name.StartsWith('.') && name.EndsWith(".icloud", StringComparison.Ordinal))
            path = Path.Combine(Path.GetDirectoryName(path)!, name[1..^7]);
        return path;
    }

    private static void RequireApple()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            throw new PlatformNotSupportedException("iCloud Drive requires macOS or iOS.");
    }

    private static string? TakeString(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(value); }
        finally { Native.Free(value); }
    }

    // Mono's AOT compiler recognizes MonoPInvokeCallbackAttribute by name, even
    // in portable assemblies that intentionally do not reference ObjCRuntime.
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class MonoPInvokeCallbackAttribute(Type delegateType) : Attribute
    {
        public Type DelegateType { get; } = delegateType;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeAccessor(IntPtr context, IntPtr coordinatedPath);

    private sealed class AccessorContext(Func<string, object?> accessor, CancellationToken cancellationToken)
    {
        public Func<string, object?> Accessor { get; } = accessor;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public object? Result { get; set; }
        public bool Invoked { get; set; }
        public ExceptionDispatchInfo? Exception { get; set; }
    }

    [MonoPInvokeCallback(typeof(NativeAccessor))]
    private static int InvokeAccessor(IntPtr contextPointer, IntPtr coordinatedPath)
    {
        AccessorContext? context = null;
        try
        {
            context = (AccessorContext?)GCHandle.FromIntPtr(contextPointer).Target
                ?? throw new IOException("The file coordinator has no managed accessor.");
            context.CancellationToken.ThrowIfCancellationRequested();
            var path = Marshal.PtrToStringUTF8(coordinatedPath)
                ?? throw new IOException("The file coordinator returned no path.");
            context.Invoked = true;
            context.Result = context.Accessor(NormalizePath(path));
            return 0;
        }
        catch (Exception exception)
        {
            // Preserve the original managed exception and throw only after the
            // native call returns. No exception may unwind through Objective-C.
            if (context is not null) context.Exception = ExceptionDispatchInfo.Capture(exception);
            return 1;
        }
    }

    private static class Native
    {
        // iOS must see literal __Internal imports when linking its static
        // archive, otherwise dead-stripping removes unreferenced C entrypoints
        // even when the archive was force-loaded. macOS resolves the same
        // imports to the helper dylib in its application payload.
        private const string Library = "__Internal";

        static Native()
        {
            if (OperatingSystem.IsMacOS())
                NativeLibrary.SetDllImportResolver(typeof(AppleICloudSyncFileSystem).Assembly,
                    static (library, _, _) => library == Library
                        ? NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "libLibzoraICloud.dylib"))
                        : IntPtr.Zero);
        }

        [DllImport(Library, EntryPoint = "lz_icloud_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr value);
        [DllImport(Library, EntryPoint = "lz_icloud_account_archive", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr AccountArchive(out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_documents", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Documents([MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_item_exists", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ItemExists([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_download_status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DownloadStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int requestDownload, out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_upload_status", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UploadStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_coordinator_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr CoordinatorCreate(out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_coordinator_cancel", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CoordinatorCancel(IntPtr coordinator);
        [DllImport(Library, EntryPoint = "lz_icloud_coordinator_release", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CoordinatorRelease(IntPtr coordinator);
        [DllImport(Library, EntryPoint = "lz_icloud_coordinate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Coordinate(IntPtr coordinator, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int access, NativeAccessor accessor, IntPtr context, out IntPtr error);
        [DllImport(Library, EntryPoint = "lz_icloud_delete", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Delete([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr error);
    }
}
