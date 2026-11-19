using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Platform.Common;

/// <summary>
/// Path roots and file operations.
/// </summary>
public sealed partial class MassStorageKindleDeviceService : IKindleDeviceService
{
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(root, "*", options);
    }

    private static string GetDocumentsRoot(KindleDevice device)
    {
        return ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.BooksDirectory);
    }

    private static string GetResourceRoot(KindleDevice device, KindleResourceKind kind)
    {
        var resourceRoot = ReaderDevicePaths.ResolveDirectory(device.RootPath, device.Profile.ResourceDirectory(kind));
        if (Directory.Exists(resourceRoot)) RejectLink(resourceRoot);
        return resourceRoot;
    }

    private static string GetClippingsPath(KindleDevice device) =>
        ResolveWithinRoot(GetDocumentsRoot(device), "My Clippings.txt", GetDocumentsRoot(device));

    private static string ResolveWithinRoot(string basePath, string relativePath, string allowedRoot)
    {
        var path = Path.GetFullPath(Path.Combine(basePath, relativePath));
        EnsureUnderRoot(path, allowedRoot);
        RejectLinksInPath(path, allowedRoot);
        return path;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("设备路径不在允许的目录范围内。");
    }

    private static void RejectLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不允许操作设备目录中的链接。");
    }

    private static void RejectLinksInPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root);
        RejectLink(normalizedRoot);
        var relative = Path.GetRelativePath(normalizedRoot, Path.GetFullPath(path));
        if (relative == ".") return;
        var current = normalizedRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
    }

    private static async Task CopyAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash: null);
    }

    private static async Task<string> CopyAndHashAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action = "正在发送")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await CopyCoreAsync(source, target, total, progress, cancellationToken, action, hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task CopyCoreAsync(
        string source,
        string target,
        long total,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken,
        string action,
        IncrementalHash? hash)
    {
        await using var input = await OpenFileStreamAsync(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        await using var output = await OpenFileStreamAsync(
            target,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash?.AppendData(buffer, 0, read);
            copied += read;
            progress?.Report(new TransferProgress(copied, total, $"{action} {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = await OpenFileStreamAsync(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            cancellationToken);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<FileStream> OpenFileStreamAsync(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options,
        CancellationToken cancellationToken)
    {
        IOException? lastSharingException = null;
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, mode, access, share, 128 * 1024, options);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                lastSharingException = exception;
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }

        throw lastSharingException ?? new IOException($"无法打开传输文件：{path}");
    }

    private static async Task MoveFileAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < FileOperationRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (attempt + 1 >= FileOperationRetryAttempts) throw;
                await Task.Delay(FileOperationRetryDelayMilliseconds, cancellationToken);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is 32 or 33;
    }

    private static string GetUniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string SafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "book.bin" : value;
    }

    private static void SetFallbackMetadata(KindleBook book)
    {
        var fileName = Path.GetFileNameWithoutExtension(book.RelativePath);
        var separator = fileName.LastIndexOf('_');
        if (separator > 0)
        {
            var suffix = fileName[(separator + 1)..];
            if (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)) fileName = fileName[..separator];
        }
        book.Title = fileName.Replace('_', ' ').Trim();
        book.Authors = "未知作者";
    }

    private static KindleBook CloneBook(KindleBook book) => new()
    {
        RelativePath = book.RelativePath,
        Title = book.Title,
        Authors = book.Authors,
        Format = book.Format,
        Size = book.Size,
        Sha256 = book.Sha256,
        CoverPath = book.CoverPath,
        ModifiedAt = book.ModifiedAt,
        IsManagedByLibzora = book.IsManagedByLibzora
    };

    private static bool IsDictionaryPath(string relativePath)
    {
        var separator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var first = separator < 0 ? relativePath : relativePath[..separator];
        return first.Equals("dictionaries", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureMassStorage(KindleDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Transport != KindleTransport.MassStorage)
            throw new NotSupportedException("This service supports mounted USB storage only.");
    }

    private static bool IsWithinOrEqual(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> DefaultMountRoots()
    {
        var user = Environment.UserName;
        if (OperatingSystem.IsMacOS()) return ["/Volumes"];
        if (OperatingSystem.IsLinux()) return [$"/media/{user}", $"/run/media/{user}"];
        return [];
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
