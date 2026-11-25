namespace Libzora.Mobile;

/// <summary>A byte-for-byte temporary export whose lifetime includes the native share sheet.</summary>
public sealed class MobileBookFileExport : IDisposable
{
    private readonly string _directory;
    public string Path { get; }
    private MobileBookFileExport(string directory, string path) { _directory = directory; Path = path; }

    public static async Task<MobileBookFileExport> CreateAsync(string source, string fileName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName) || System.IO.Path.GetFileName(fileName) != fileName
            || fileName is "." or "..")
            throw new ArgumentException("Invalid export file name.", nameof(fileName));
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "libzora-book-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = new MobileBookFileExport(directory, System.IO.Path.Combine(directory, fileName));
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(result.Path))
                await input.CopyToAsync(output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
