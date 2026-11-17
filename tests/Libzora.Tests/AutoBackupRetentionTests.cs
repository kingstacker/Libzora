using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Tests;

public sealed class AutoBackupRetentionTests
{
    [Theory]
    [InlineData("Libzora-auto-20260101-000000.libzora", true)]
    [InlineData("libzora-AUTO-20260101-000000.LIBZORA", true)]
    [InlineData("Libzora-备份-20260101-000000.libzora", false)]
    [InlineData("Libzora-auto-20260101-000000.zip", false)]
    [InlineData("", false)]
    public void OnlyAutomaticBackupNamesAreEligible(string fileName, bool expected) =>
        Assert.Equal(expected, AutoBackupRetentionPolicy.IsAutomaticBackup(fileName));

    [Fact]
    public void RetentionSelectsExpiredAutomaticBackupsOnly()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var root = Directory.CreateTempSubdirectory("libzora-retention-");
        try
        {
            var automatic = new[]
            {
                CreateFile(root.FullName, "Libzora-auto-20261117-110000.libzora", now.AddHours(-1)),
                CreateFile(root.FullName, "Libzora-auto-20261116-110000.libzora", now.AddHours(-25)),
                CreateFile(root.FullName, "Libzora-auto-20261115-110000.libzora", now.AddHours(-49)),
                CreateFile(root.FullName, "Libzora-auto-20261114-110000.libzora", now.AddHours(-73))
            };
            var manual = new[]
            {
                CreateFile(root.FullName, "Libzora-备份-20250101.libzora", now.AddYears(-1)),
                CreateFile(root.FullName, "my-books.libzora", now.AddYears(-2))
            };

            var expired = AutoBackupRetentionPolicy.SelectExpired([.. automatic, .. manual], retention: 2);

            Assert.Equal(
                [automatic[2].FullName, automatic[3].FullName],
                expired.Select(file => file.FullName).ToArray());
            Assert.DoesNotContain(expired, file => file.FullName.Contains("备份", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ManualBackupsDoNotCountTowardsAutomaticFreshness()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var root = Directory.CreateTempSubdirectory("libzora-retention-");
        try
        {
            var manual = CreateFile(root.FullName, "Libzora-备份-20261117.libzora", now);
            var oldAutomatic = CreateFile(root.FullName, "Libzora-auto-20261116-000000.libzora", now.AddHours(-24));

            var automatic = AutoBackupRetentionPolicy.SelectAutomatic([manual, oldAutomatic]);

            var latest = Assert.Single(automatic);
            Assert.Equal(oldAutomatic.FullName, latest.FullName);
            Assert.False(AutoBackupRetentionPolicy.IsFresh(latest.LastWriteTimeUtc, now));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void FreshnessUsesTheTwentyHourWindow()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(AutoBackupRetentionPolicy.IsFresh(now.AddHours(-19), now));
        Assert.False(AutoBackupRetentionPolicy.IsFresh(now.AddHours(-20), now));
    }

    [Fact]
    public void RetentionAlwaysKeepsAtLeastOneAutomaticBackup()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var root = Directory.CreateTempSubdirectory("libzora-retention-");
        try
        {
            var files = new[]
            {
                CreateFile(root.FullName, "Libzora-auto-20261117-110000.libzora", now.AddHours(-1)),
                CreateFile(root.FullName, "Libzora-auto-20261116-110000.libzora", now.AddHours(-25))
            };

            var expired = AutoBackupRetentionPolicy.SelectExpired(files, retention: 0);

            Assert.Single(expired);
            Assert.Equal(files[1].FullName, expired[0].FullName);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    private static FileInfo CreateFile(string directory, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "backup");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return new FileInfo(path);
    }
}
