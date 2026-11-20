using System.Security.Cryptography;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.iOSHead;
using Xunit;

namespace Libzora.Mobile.Tests;

public sealed class NativePersistenceRegressionTests
{
    [Fact]
    public void LegacyDeviceKeyMigratesWithoutChangingSecretsAndIsRemovedOnlyAfterVerification()
    {
        var legacy = RandomNumberGenerator.GetBytes(32);
        byte[]? device = null;
        var removed = false;
        var reads = 0;
        var migrated = IOSSecretKeyMigration.GetOrCreate(
            () => { reads++; return device?.ToArray(); },
            key => { Assert.False(removed); device = key.ToArray(); },
            () => legacy.ToArray(),
            () => { Assert.True(reads >= 2); Assert.Equal(legacy, device); removed = true; }, true);
        Assert.Equal(legacy, migrated);
        Assert.True(removed);
        var reopened = IOSSecretKeyMigration.GetOrCreate(() => device!.ToArray(),
            _ => throw new InvalidOperationException("Must not rotate the key"),
            () => null, () => { }, false);
        Assert.Equal(legacy, reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMigrationRetainsLegacyKey(bool mismatchedReadback)
    {
        var legacy = RandomNumberGenerator.GetBytes(32);
        var reads = 0;
        var removed = false;
        Assert.ThrowsAny<Exception>(() => IOSSecretKeyMigration.GetOrCreate(
            () => ++reads == 1 ? null : new byte[32],
            _ => { if (!mismatchedReadback) throw new IOException("Device store unavailable"); },
            () => legacy.ToArray(), () => removed = true, true));
        Assert.False(removed);
    }

    [Fact]
    public void ConflictingLegacyKeyIsRetainedRatherThanDiscarded()
    {
        var removed = false;
        Assert.Throws<CryptographicException>(() => IOSSecretKeyMigration.GetOrCreate(
            () => new byte[32], _ => throw new InvalidOperationException("Must not replace device key"),
            () => Enumerable.Repeat((byte)1, 32).ToArray(), () => removed = true, false));
        Assert.False(removed);
    }

    [Fact]
    public void MissingKeyDuringDecryptionNeverGeneratesAReplacement()
    {
        var saved = false;
        Assert.Throws<CryptographicException>(() => IOSSecretKeyMigration.GetOrCreate(
            () => null, _ => saved = true, () => null, () => { }, false));
        Assert.False(saved);
    }

    [Fact]
    public void InvalidLegacyKeyIsNotSilentlyReplaced()
    {
        var saved = false;
        var removed = false;
        Assert.Throws<CryptographicException>(() => IOSSecretKeyMigration.GetOrCreate(
            () => null, _ => saved = true, () => new byte[3], () => removed = true, true));
        Assert.False(saved);
        Assert.False(removed);
    }

    [Fact]
    public void NativeSaveFailureDoesNotChangeCurrentOrNotifyObservers()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-native-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MobilePreferencesStore(new AppPaths(root));
            store.UpdateAndSave(value => value with { PdfZoom = 2 });
            var baseline = store.Current;
            var notifications = 0;
            store.Changed += (_, _) => notifications++;
            // An occupied temporary path produces a deterministic write error.
            Directory.CreateDirectory(Path.Combine(root, "mobile-preferences.json.tmp"));
            Assert.ThrowsAny<Exception>(() => store.UpdateAndSave(value => value with { ThemeMode = MobileThemeMode.Dark }));
            Assert.Equal(baseline, store.Current);
            Assert.Equal(0, notifications);
            var restored = new MobilePreferencesStore(new AppPaths(root));
            restored.Load();
            Assert.Equal(2, restored.Current.PdfZoom);
            Assert.Equal(baseline.ThemeMode, restored.Current.ThemeMode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RendererPreferenceChangesNotifyTheNativeBridge()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-native-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MobilePreferencesStore(new AppPaths(root));
            var notifications = 0;
            store.Changed += (_, _) => notifications++;
            var file = Guid.NewGuid();
            store.Update(value => value.WithPdfZoom(file, 2.4));
            Assert.Equal(1, notifications);
            Assert.Equal(2.4, store.Current.PdfZoom);
            Assert.Equal(2.4, store.Current.GetEffectivePdfZoom(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
