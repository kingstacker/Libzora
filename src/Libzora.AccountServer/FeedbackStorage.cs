using Libzora.Core;

namespace Libzora.AccountServer;

/// <summary>
/// Screenshots live outside the sync object namespace so sync listing and cleanup never
/// expose or remove them. Keys are fixed-shape GUID paths; no caller-supplied path is used.
/// </summary>
internal sealed class FeedbackStorage(string directory)
{
    private readonly string _root = Path.Combine(directory, "feedback");

    public Task<string> SaveAsync(Guid feedbackId, Guid storageId, string contentType, Stream input, long length,
        CancellationToken token) =>
        SaveKeyAsync(Key(feedbackId, storageId, contentType), input, length, token);

    /// <summary>Stores the optional sanitized software log as plain UTF-8 text.</summary>
    public Task<string> SaveLogAsync(Guid feedbackId, Guid storageId, Stream input, long length,
        CancellationToken token) =>
        SaveKeyAsync(LogKey(feedbackId, storageId), input, length, token);

    private async Task<string> SaveKeyAsync(string key, Stream input, long length, CancellationToken token)
    {
        var path = PathForKey(key);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        Restrict(folder);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += count;
                    if (received > length) throw new AccountRequestException(413, "附件内容超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (received != length) throw new AccountRequestException(400, "附件上传不完整，请重试。");
                await output.FlushAsync(token);
            }
            File.Move(temporary, path);
            return key;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public FileStream OpenRead(string key)
    {
        var path = PathForKey(key);
        if (!File.Exists(path)) throw new AccountRequestException(404, "反馈附件不存在。");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
    }

    public void Delete(string key)
    {
        var path = PathForKey(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteFeedback(Guid feedbackId)
    {
        var folder = Path.Combine(_root, feedbackId.ToString("N"));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private string Key(Guid feedbackId, Guid storageId, string contentType) =>
        $"feedback/{feedbackId:N}/{storageId:N}{FeedbackValidation.ExtensionFor(contentType)}";

    private static string LogKey(Guid feedbackId, Guid storageId) =>
        $"feedback/{feedbackId:N}/{storageId:N}.txt";

    private string PathForKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 3 || parts[0] != "feedback"
            || !Guid.TryParseExact(parts[1], "N", out var feedback)
            || parts[2].Length < 33
            || !Guid.TryParseExact(parts[2][..32], "N", out _)
            || parts[2][32..] is not (".jpg" or ".png" or ".webp" or ".txt"))
            throw new AccountRequestException(400, "无效的反馈附件路径。");
        var path = Path.GetFullPath(Path.Combine(_root, feedback.ToString("N"), parts[2]));
        var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AccountRequestException(400, "无效的反馈附件路径。");
        return path;
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
