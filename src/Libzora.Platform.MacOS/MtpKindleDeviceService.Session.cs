using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Platform.MacOS.Mtp;

namespace Libzora.Platform.MacOS;

/// <summary>
/// Kindle devices that expose MTP instead of USB mass storage. macOS has no
/// MTP stack of its own, so this service talks to libmtp (the same library
/// calibre uses on macOS).
/// </summary>

/// <summary>
/// MTP session helpers, covers and file utilities.
/// </summary>
public sealed partial class MtpKindleDeviceService : IKindleDeviceService, IDeviceAttachmentProbe
{
    private async Task<T> WithSessionAsync<T>(
        KindleDevice device,
        Func<MtpDeviceSession, T> action,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var session = OpenSession(device);
                return action(session);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private MtpDeviceSession OpenSession(KindleDevice device)
    {
        var candidates = MtpDeviceSession.Detect()
            .Where(raw => raw.VendorId == AmazonVendorId)
            .ToArray();
        var exact = candidates.FirstOrDefault(candidate =>
            string.Equals(
                MtpDeviceIdentity.RootPath(candidate),
                device.RootPath.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            var session = MtpDeviceSession.Open(exact);
            if (MtpDeviceIdentity.Matches(
                    device,
                    session.SerialNumber,
                    session.Model,
                    MtpDeviceIdentity.RootPath(exact)))
                return session;
            session.Dispose();
            throw new IOException("Kindle 设备身份与上次连接不一致，已停止操作。请断开设备后重新连接并刷新。");
        }

        // Bus/device numbers change across replugs. Re-identify the reader by
        // serial before touching anything; a model fallback is not unique and
        // must fail closed rather than operate on another Kindle.
        if (!MtpDeviceIdentity.CanReconnectByIdentity(device))
            throw new IOException("Kindle 设备已重新连接，但无法可靠确认设备身份。请重新连接并刷新设备列表后重试。");

        MtpDeviceSession? matched = null;
        try
        {
            foreach (var candidate in candidates)
            {
                MtpDeviceSession session;
                try
                {
                    session = MtpDeviceSession.Open(candidate);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    throw new IOException(
                        "存在无法读取的 Kindle 候选设备，无法可靠确认操作目标。请断开其他 Kindle 后重试。",
                        exception);
                }

                if (MtpDeviceIdentity.Matches(device, session.SerialNumber, session.Model))
                {
                    if (matched is not null)
                    {
                        session.Dispose();
                        throw new IOException("检测到多台身份相同的 Kindle 设备，无法安全确定操作目标。请仅连接一台后重试。");
                    }
                    matched = session;
                }
                else
                {
                    session.Dispose();
                }
            }
        }
        catch
        {
            matched?.Dispose();
            throw;
        }

        if (matched is null)
            throw new IOException("原 Kindle 设备未找到或身份不匹配，请重新连接并刷新设备列表后重试。");
        return matched;
    }

    private async Task<string?> ReadClippingsTextAsync(KindleDevice device, CancellationToken cancellationToken)
    {
        var relativePath = $"{device.Profile.BooksDirectory}/My Clippings.txt";
        var temporary = Path.Combine(Path.GetTempPath(), "Libzora", $"clippings-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            var found = await WithSessionAsync(device, session =>
            {
                var entry = session.Find(relativePath);
                if (entry is null) return false;
                session.Download(entry, temporary, cancellationToken: cancellationToken);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            if (!found) return null;
            return await File.ReadAllTextAsync(temporary, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private string? WriteCover(string sha256, BookMetadata metadata)
    {
        var coverBytes = metadata.CoverBytes;
        if (coverBytes is not { Length: > 0 }) return null;
        var extension = string.IsNullOrWhiteSpace(metadata.CoverExtension) ? ".jpg" : metadata.CoverExtension;
        var name = string.IsNullOrWhiteSpace(sha256) ? Guid.NewGuid().ToString("N") : sha256;
        var path = Path.Combine(_coverCacheDirectory, $"{name}{extension}");
        try
        {
            if (!File.Exists(path)) File.WriteAllBytes(path, coverBytes);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? TryReadDeviceThumbnail(
        MtpDeviceSession session,
        KindleBook book,
        string bookPath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var thumbnailName = KindleThumbnailService
                .ReadThumbnailFileNameAsync(bookPath, cancellationToken)
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(thumbnailName)) return null;
            var remote = session.Find($"{ThumbnailDirectory}/{thumbnailName}");
            if (remote is null) return null;
            var local = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.jpg");
            try
            {
                session.Download(remote, local, cancellationToken: cancellationToken);
                var name = string.IsNullOrWhiteSpace(book.Sha256)
                    ? Guid.NewGuid().ToString("N")
                    : book.Sha256;
                var path = Path.Combine(_coverCacheDirectory, $"{name}.jpg");
                File.Copy(local, path, overwrite: true);
                return path;
            }
            finally
            {
                TryDelete(local);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
        var title = fileName.Replace('_', ' ').Trim();
        book.Title = title.Length == 0 ? "未命名书籍" : title;
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

    private static string SafeFileName(string name) =>
        new(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());

    private static string UniqueDestination(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("目标目录中同名文件过多。");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
