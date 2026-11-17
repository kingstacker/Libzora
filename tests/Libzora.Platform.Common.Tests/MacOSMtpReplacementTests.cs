using Libzora.Platform.MacOS.Mtp;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class MacOSMtpReplacementTests
{
    [Fact]
    public void RenameFailureRestoresTheOriginalName()
    {
        using var device = new FakeReplacementDevice("old")
        {
            FailInstallingStaged = true
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains("原文件已恢复", exception.Message);
        Assert.Equal("old", device.ContentOf("book.epub"));
        Assert.Null(device.Find("book.epub.libzora-part-test"));
        Assert.Null(device.FindByPrefix("book.epub.libzora-old-"));
        Assert.False(device.RecoveryCopyCreated);
    }

    [Fact]
    public void CompatibilityUploadFailureRestoresTheDownloadedCopy()
    {
        using var device = new FakeReplacementDevice("old")
        {
            RenameSupported = false,
            FailFinalUpload = true
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains("原文件已恢复", exception.Message);
        Assert.Equal("old", device.ContentOf("book.epub"));
        Assert.True(device.RecoveryCopyCreated);
        Assert.True(device.RecoveryCopyDeleted);
        Assert.Null(device.Find("book.epub.libzora-part-test"));
    }

    [Fact]
    public void RestoreFailureKeepsTheLocalRecoveryCopyAndNamesIt()
    {
        using var device = new FakeReplacementDevice("old")
        {
            RenameSupported = false,
            FailFinalUpload = true,
            FailRestoreUpload = true
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains(device.RecoveryPath!, exception.Message);
        Assert.Contains("book.epub.libzora-part-test", exception.Message);
        Assert.True(File.Exists(device.RecoveryPath));
        Assert.False(device.RecoveryCopyDeleted);
        Assert.Null(device.Find("book.epub"));
        Assert.Equal("new-bytes", device.ContentOf("book.epub.libzora-part-test"));
        Assert.DoesNotContain("全部丢失", exception.Message);
    }

    [Fact]
    public void FailedFinalUploadPartialObjectIsRemovedBeforeRestore()
    {
        using var device = new FakeReplacementDevice("old")
        {
            RenameSupported = false,
            FailFinalUpload = true,
            LeavePartialOnFinalFailure = true
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains("原文件已恢复", exception.Message);
        Assert.Equal("old", device.ContentOf("book.epub"));
        Assert.Equal(1, device.CountNamed("book.epub"));
    }

    [Fact]
    public void MissingRecoveryCopyLeavesOriginalUntouched()
    {
        using var device = new FakeReplacementDevice("old")
        {
            RenameSupported = false,
            CanCreateRecoveryCopy = false
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains("保留原文件", exception.Message);
        Assert.Equal("old", device.ContentOf("book.epub"));
        Assert.Null(device.Find("book.epub.libzora-part-test"));
        Assert.Equal(0, device.UploadCount);
    }

    [Fact]
    public void CompatibleFirmwareReplacesViaRenameWithoutDownloadingOldBytes()
    {
        using var device = new FakeReplacementDevice("old");
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var result = Commit(device, existing, staged);

        Assert.Equal(result.ItemId, Assert.IsType<MtpEntry>(device.Find("book.epub")).ItemId);
        Assert.Equal("new-bytes", device.ContentOf("book.epub"));
        Assert.Null(device.FindByPrefix("book.epub.libzora-old-"));
        Assert.False(device.RecoveryCopyCreated);
        Assert.Equal(0, device.UploadCount);
    }

    [Fact]
    public void UnverifiableBackupRenameFailsClosedWithoutTouchingTheOriginal()
    {
        using var device = new FakeReplacementDevice("old")
        {
            RenameReportsSuccessWithoutMoving = true
        };
        var existing = Assert.IsType<MtpEntry>(device.Find("book.epub"));
        var staged = device.AddStaged("new-bytes", "book.epub.libzora-part-test");

        var exception = Assert.Throws<IOException>(() => Commit(device, existing, staged));

        Assert.Contains("已停止替换", exception.Message);
        Assert.Equal("old", device.ContentOf("book.epub"));
    }

    private static MtpEntry Commit(
        FakeReplacementDevice device,
        MtpEntry existing,
        MtpEntry staged) =>
        MtpFileReplacement.Commit(
            existing,
            staged,
            staged.Name,
            "new-bytes",
            "book.epub",
            CancellationToken.None,
            device);

    private sealed class FakeReplacementDevice : IMtpFileReplacementOperations, IDisposable
    {
        private readonly List<FakeEntry> _entries = [];
        private readonly string _originalContent;
        private uint _nextId = 1;

        public FakeReplacementDevice(string originalContent)
        {
            _originalContent = originalContent;
            _entries.Add(new FakeEntry
            {
                ItemId = _nextId++,
                Name = "book.epub",
                Content = originalContent
            });
        }

        public bool RenameSupported { get; init; } = true;
        public bool RenameReportsSuccessWithoutMoving { get; init; }
        public bool FailInstallingStaged { get; init; }
        public bool FailFinalUpload { get; init; }
        public bool FailRestoreUpload { get; init; }
        public bool LeavePartialOnFinalFailure { get; init; }
        public bool CanCreateRecoveryCopy { get; init; } = true;
        public bool RecoveryCopyCreated { get; private set; }
        public bool RecoveryCopyDeleted { get; private set; }
        public string? RecoveryPath { get; private set; }
        public int UploadCount { get; private set; }
        public List<string> Logs { get; } = [];

        public MtpEntry AddStaged(string content, string name)
        {
            var entry = new FakeEntry { ItemId = _nextId++, Name = name, Content = content };
            _entries.Add(entry);
            return entry.ToEntry();
        }

        public MtpEntry? Find(string fileName) =>
            _entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase))?.ToEntry();

        public MtpEntry? FindByPrefix(string prefix) =>
            _entries.FirstOrDefault(entry =>
                entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.ToEntry();

        public string? ContentOf(string fileName) =>
            _entries.FirstOrDefault(entry =>
                string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase))?.Content;

        public int CountNamed(string fileName) =>
            _entries.Count(entry =>
                string.Equals(entry.Name, fileName, StringComparison.OrdinalIgnoreCase));

        public bool Rename(MtpEntry entry, string newFileName)
        {
            if (RenameReportsSuccessWithoutMoving) return true;
            if (!RenameSupported) return false;
            if (FailInstallingStaged
                && entry.Name.Contains(".libzora-part-", StringComparison.Ordinal)
                && string.Equals(newFileName, "book.epub", StringComparison.OrdinalIgnoreCase))
                return false;

            var fake = _entries.FirstOrDefault(candidate => candidate.ItemId == entry.ItemId);
            if (fake is null) return false;
            fake.Name = newFileName;
            return true;
        }

        public void Delete(MtpEntry entry) =>
            _entries.RemoveAll(candidate => candidate.ItemId == entry.ItemId);

        public MtpEntry Upload(
            string sourcePath,
            string fileName,
            Action<long, long>? progress,
            CancellationToken cancellationToken)
        {
            UploadCount++;
            if (RecoveryPath is not null && string.Equals(sourcePath, RecoveryPath, StringComparison.Ordinal))
            {
                if (FailRestoreUpload) throw new IOException("simulated restore failure");
            }
            else if (FailFinalUpload)
            {
                if (LeavePartialOnFinalFailure)
                {
                    _entries.Add(new FakeEntry
                    {
                        ItemId = _nextId++,
                        Name = fileName,
                        Content = "partial"
                    });
                }
                throw new IOException("simulated final upload failure");
            }

            var entry = new FakeEntry
            {
                ItemId = _nextId++,
                Name = fileName,
                Content = string.Equals(sourcePath, RecoveryPath, StringComparison.Ordinal)
                    ? _originalContent
                    : sourcePath
            };
            _entries.Add(entry);
            return entry.ToEntry();
        }

        public string? CreateRecoveryCopy(MtpEntry entry)
        {
            if (!CanCreateRecoveryCopy) return null;
            var directory = Path.Combine(
                Path.GetTempPath(),
                "LibzoraTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            RecoveryPath = Path.Combine(directory, "book.epub.recovery");
            File.WriteAllText(RecoveryPath, _originalContent);
            RecoveryCopyCreated = true;
            return RecoveryPath;
        }

        public void DeleteRecoveryCopy(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            RecoveryCopyDeleted = !File.Exists(path);
        }

        public void Log(string message) => Logs.Add(message);

        public void Dispose()
        {
            if (RecoveryPath is null) return;
            var directory = Path.GetDirectoryName(RecoveryPath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        private sealed class FakeEntry
        {
            public required uint ItemId { get; init; }
            public required string Name { get; set; }
            public required string Content { get; init; }

            public MtpEntry ToEntry() => new()
            {
                ItemId = ItemId,
                ParentId = 1,
                StorageId = 1,
                Name = Name,
                RelativePath = $"documents/{Name}",
                IsFolder = false
            };
        }
    }
}
