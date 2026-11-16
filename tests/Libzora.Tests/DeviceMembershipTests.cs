using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Microsoft.Data.Sqlite;

namespace Libzora.Tests;

public sealed class DeviceMembershipTests
{
    [Theory]
    [InlineData("custom", "https://example.invalid/v1")]
    [InlineData("openai", "https://api.openai.com/proxy/v1")]
    [InlineData("deepseek", "https://api.openai.com/v1")]
    public async Task CustomAiEndpointsAreFreeWithoutAnAccount(string provider, string url)
    {
        // The membership plan makes custom AI interfaces free for every tier.
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => new()
        { Content = new StringContent("{\"data\":[{\"id\":\"fictional-model\"}]}", Encoding.UTF8, "application/json") }));
        Assert.Equal("fictional-model", Assert.Single(await client.ListModelsAsync(new()
            { Provider = provider, BaseUrl = url, ApiKey = "<FICTIONAL_API_KEY>" })));
    }

    [Theory]
    [InlineData("deepseek", "https://api.deepseek.com")]
    [InlineData("openai", "https://api.openai.com/v1")]
    public async Task FreeOfficialProviderStillMakesRequestsWithoutAnAccount(string provider, string url)
    {
        using var client = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => new()
        { Content = new StringContent("{\"data\":[{\"id\":\"fictional-model\"}]}", Encoding.UTF8, "application/json") }));
        Assert.Equal("fictional-model", Assert.Single(await client.ListModelsAsync(new()
            { Provider = provider, BaseUrl = url, ApiKey = "<FICTIONAL_API_KEY>" })));
    }

    [Theory]
    [InlineData(SyncProvider.S3, "test")]
    [InlineData(SyncProvider.S3, "sync")]
    [InlineData(SyncProvider.S3, "download")]
    [InlineData(SyncProvider.WebDav, "test")]
    [InlineData(SyncProvider.WebDav, "sync")]
    [InlineData(SyncProvider.WebDav, "download")]
    [InlineData(SyncProvider.AccountServer, "test")]
    [InlineData(SyncProvider.AccountServer, "sync")]
    [InlineData(SyncProvider.AccountServer, "download")]
    public async Task UnlicensedSyncIsRejectedBeforeCreatingTheObjectStore(SyncProvider provider, string operation)
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            await new SqliteBookLibraryService(paths, new BookMetadataService()).InitializeAsync();
            await new ReaderDataService(paths).InitializeAsync();
            var creations = 0;
            Func<S3SyncSettings, ISyncObjectStore> factory = _ =>
            {
                creations++;
                throw new InvalidOperationException("S3 client must not be created");
            };
            var sync = new S3SyncService(paths, new TestHelpers.PlaintextSecretProtector(), factory);
            var settings = new S3SyncSettings { Enabled = true, Provider = provider, Bucket = "fictional-bucket",
                Endpoint = "https://example.invalid", AccessKey = "<FICTIONAL_ACCESS_KEY>", SecretKey = "<FICTIONAL_SECRET_KEY>",
                WebDavEndpoint = "https://example.invalid/dav", AccountServerUrl = "https://example.invalid", AccountUserId = Guid.NewGuid(),
                EncryptionKey = "fictional-sync-key", Prefix = "fictional" };
            var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            {
                switch (operation)
                {
                    case "test": await sync.TestConnectionAsync(settings); break;
                    case "sync": await sync.SyncAsync(Guid.NewGuid().ToString(), settings); break;
                    case "download": await sync.EnsureBookFileDownloadedAsync(Guid.NewGuid().ToString(), settings,
                        new BookFile { Sha256 = new string('a', 64), RelativePath = "books/fictional.epub" }); break;
                }
            });
            if (provider == SyncProvider.AccountServer) Assert.Contains("官方云同步已移除", error.Message);
            else Assert.IsType<DeviceFeatureRequiredException>(error);
            Assert.Equal(0, creations);
        }
        finally { SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task CustomS3IsCheckedAgainstTheServerBeforeEveryOperation()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        Assert.Equal(1, scope.Handler.FeatureChecks);
        scope.Handler.Revoked = true;
        var notices = 0; scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(1, notices);
    }

    [Fact]
    public async Task FreeAccountHasDeviceManagementButCannotUsePaidFeatures()
    {
        using var scope = await AccountScope.CreateAsync(); scope.Handler.LifetimeFeatures = false;
        var status = await scope.Membership.RefreshAsync();
        Assert.Equal("free", status.Membership.Tier); Assert.Equal(2, status.Membership.LoggedInDeviceCount);
        Assert.False(status.CanUseFeatures);
        Assert.False(status.CanUseAccountCloudSync);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task SixActiveDevicesAreAcceptedAndSeventhIsRejected()
    {
        using var scope = await AccountScope.CreateAsync();
        for (var index = scope.Handler.Devices.Count; index < 6; index++)
            scope.Handler.Devices.Add(new(Guid.NewGuid(), "Fictional additional device", "Test", "active", scope.Clock.UtcNow, scope.Clock.UtcNow));
        var status = await scope.Membership.RefreshAsync();
        Assert.Equal(6, status.Membership.DeviceLimit);
        Assert.Equal(6, status.Membership.LoggedInDeviceCount);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        scope.Handler.Devices.Add(new(Guid.NewGuid(), "Fictional seventh device", "Test", "active", scope.Clock.UtcNow, scope.Clock.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RefreshAsync());
    }

    [Fact]
    public async Task TrialAndLifetimeUseOnlyOwnStorageAndExpiryDisablesTrialFeatures()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Handler.Now.AddDays(1);
        Assert.Equal("trial", (await scope.Membership.RefreshAsync()).Membership.Tier);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        scope.Handler.Now = scope.Handler.ProExpiresAt.Value;
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        scope.Handler.LifetimeFeatures = true;
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
    }

    [Fact]
    public async Task TrialExpiryIsAnnouncedOnceAcrossRestartsWithoutSigningTheAccountOut()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddDays(-1);
        var notices = new List<AccountMembershipExpiredEventArgs>();
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            monitor.MembershipExpired += (_, notice) => notices.Add(notice);
            Assert.True(await monitor.CheckIfDueAsync());
        }
        var notice = Assert.Single(notices);
        Assert.Equal(scope.Handler.ProExpiresAt.Value, notice.ExpiredAt);
        Assert.Contains("不会删除", notice.Message);
        Assert.DoesNotContain("销毁", notice.Message);
        Assert.DoesNotContain("云端数据保留", notice.Message);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        scope.Clock.Advance(TimeSpan.FromDays(1));
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            monitor.MembershipExpired += (_, next) => notices.Add(next);
            await monitor.CheckIfDueAsync();
        }
        Assert.Single(notices);
    }

    [Fact]
    public async Task KnownTrialExpiryIsCheckedBeforeTheNextDailyHeartbeat()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddHours(2);
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        var notices = 0; monitor.MembershipExpired += (_, _) => notices++;
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(0, notices);
        scope.Clock.Advance(TimeSpan.FromHours(2)); scope.Handler.Now = scope.Clock.UtcNow;
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(1, notices);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Theory]
    [InlineData(40, null)]
    [InlineData(30, 30)]
    [InlineData(29, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(6, 7)]
    [InlineData(2, 7)]
    [InlineData(1, 1)]
    [InlineData(0.5, 1)]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public void TrialRenewalReminderUsesThirtySevenAndOneDayThresholds(double days, int? expected)
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, MemberEntitlements.TrialRenewalReminder(now.AddDays(days), now));
    }

    [Fact]
    public async Task TrialRenewalRemindersArriveAtThirtySevenAndOneDaysOncePerExpiry()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false;
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddDays(40);
        var notices = new List<AccountMembershipExpiringEventArgs>();
        void Listen(AccountDeviceMonitor monitor) => monitor.MembershipExpiring += (_, notice) => notices.Add(notice);
        void Advance(TimeSpan delta) { scope.Clock.Advance(delta); scope.Handler.Now = scope.Clock.UtcNow; }

        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        Listen(monitor);
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Empty(notices);

        Advance(TimeSpan.FromDays(11));
        Assert.True(await monitor.CheckIfDueAsync());
        var first = Assert.Single(notices);
        Assert.Equal(29, first.DaysRemaining);
        Assert.Contains("剩余 29 天", first.Message);

        // A restart inside the same 30-day threshold stays quiet.
        Advance(TimeSpan.FromDays(1));
        using (var restarted = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            Listen(restarted);
            Assert.True(await restarted.CheckIfDueAsync());
        }
        Assert.Single(notices);

        Advance(TimeSpan.FromDays(22));
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(2, notices.Count);
        Assert.Equal(6, notices[^1].DaysRemaining);

        Advance(TimeSpan.FromDays(5.5));
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(3, notices.Count);
        Assert.Equal(1, notices[^1].DaysRemaining);

        // A buyout stops reminders; the old limited expiry remains history.
        scope.Handler.LifetimeFeatures = true;
        Advance(TimeSpan.FromDays(1));
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(3, notices.Count);
    }

    [Fact]
    public async Task LifetimeReceivesNoExpiryOrRenewalReminderEvenWithHistoricalLimitedRights()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.ProExpiresAt = scope.Clock.UtcNow.AddHours(2);
        var notices = 0;
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        monitor.MembershipExpiring += (_, _) => notices++;
        monitor.MembershipExpired += (_, _) => notices++;
        Assert.True(await monitor.CheckIfDueAsync());
        scope.Clock.Advance(TimeSpan.FromDays(1)); scope.Handler.Now = scope.Clock.UtcNow;
        Assert.True(await monitor.CheckIfDueAsync());
        Assert.Equal(0, notices);
    }

    [Fact]
    public async Task PasswordResetUsesTheEmailedCodeAndClearsTheLocalSession()
    {
        using var scope = await AccountScope.CreateAsync();
        var url = AccountRegions.DefaultServerUrl(AccountRegion.China);
        await scope.Accounts.RequestPasswordResetAsync(url, scope.Handler.User.Email!);
        Assert.Equal(1, scope.Handler.PasswordResetRequests);

        await Assert.ThrowsAsync<ArgumentException>(() => scope.Accounts.ResetPasswordAsync(
            url, scope.Handler.User.Email!, "1234", "fictional-new-password", "fictional-new-password"));
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Accounts.ResetPasswordAsync(
            url, scope.Handler.User.Email!, "00000000", "fictional-new-password", "fictional-new-password"));
        Assert.Contains("验证码无效", invalid.Message);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);

        var user = await scope.Accounts.ResetPasswordAsync(
            url, scope.Handler.User.Email!, MembershipHttpHandler.ResetCode, "fictional-new-password", "fictional-new-password");
        Assert.True(user.EmailVerified);
        Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.True((await scope.Accounts.SignInAsync(url, scope.Handler.User.Username, "fictional-new-password")).IsLoggedIn);
    }

    [Fact]
    public async Task PasswordResetRequestDoesNotRevealUnknownAccountsAndReportsMailOutages()
    {
        using var scope = await AccountScope.CreateAsync();
        var url = AccountRegions.DefaultServerUrl(AccountRegion.China);
        await scope.Accounts.SignOutAsync();
        await scope.Accounts.RequestPasswordResetAsync(url, "fictional_missing_reader");
        Assert.Equal(1, scope.Handler.PasswordResetRequests);
        scope.Handler.EmailServiceAvailable = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Accounts.RequestPasswordResetAsync(url, scope.Handler.User.Username));
        Assert.Contains("邮件服务暂不可用", error.Message);
    }

    [Fact]
    public async Task EmailVerificationSendsACodeAndMarksTheAccountVerified()
    {
        using var scope = await AccountScope.CreateAsync();
        Assert.False((await scope.Accounts.GetStatusAsync()).User!.EmailVerified);
        await scope.Accounts.RequestEmailVerificationAsync();
        Assert.Equal(1, scope.Handler.EmailVerificationRequests);

        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Accounts.ConfirmEmailVerificationAsync("00000000"));
        Assert.Contains("邮箱验证码无效", invalid.Message);
        Assert.False((await scope.Accounts.GetStatusAsync()).User!.EmailVerified);

        var status = await scope.Accounts.ConfirmEmailVerificationAsync(MembershipHttpHandler.EmailVerificationCode);
        Assert.True(status.User!.EmailVerified);
        Assert.True((await scope.Accounts.GetStatusAsync(validateSession: true)).User!.EmailVerified);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("region")]
    [InlineData("device")]
    [InlineData("limit")]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("inactive")]
    public async Task FeatureAccessRejectsWrongOrMalformedServerScope(string failure)
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.TransformStatus = status => failure switch
        {
            "account" => status with { Membership = status.Membership with { AccountId = Guid.NewGuid() } },
            "region" => status with { Membership = status.Membership with { Region = AccountRegion.International } },
            "device" => status with { CurrentDeviceId = Guid.NewGuid() },
            "limit" => status with { Membership = status.Membership with { DeviceLimit = MemberEntitlements.DeviceLimit + 1 } },
            "count" => status with { Membership = status.Membership with { LoggedInDeviceCount = MemberEntitlements.DeviceLimit + 1 } },
            "duplicate" => status with { Devices = [status.Devices[0], status.Devices[0]] },
            "inactive" => status with { Devices = status.Devices.Select(device => device with { State = "signed_out" }).ToArray(),
                Membership = status.Membership with { LoggedInDeviceCount = 0, ActivatedDeviceCount = 0 } },
            _ => status
        };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
    }

    [Fact]
    public async Task ALongDeviceHistoryRemainsWithinTheBoundedClientResponse()
    {
        using var scope = await AccountScope.CreateAsync();
        for (var i = 0; i < 98; i++) scope.Handler.Devices.Add(new(Guid.NewGuid(), new string('测', 64), "fictional-platform", "signed_out", scope.Clock.UtcNow, scope.Clock.UtcNow));
        Assert.Equal(100, (await scope.Membership.RefreshAsync()).Devices.Length);
    }

    [Fact]
    public async Task KickingAnotherDevicePreservesCurrentLoginAndKickingCurrentDeviceNotifiesOnce()
    {
        using var scope = await AccountScope.CreateAsync(); var notices = 0;
        scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        await scope.Membership.KickAsync(scope.Handler.OtherDeviceId);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.Equal(1, (await scope.Membership.RefreshAsync()).Membership.LoggedInDeviceCount);
        await scope.Membership.KickAsync(scope.Handler.CurrentDeviceId);
        Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn); Assert.Equal(1, notices);
        await scope.Accounts.GetStatusAsync(true); Assert.Equal(1, notices);
    }

    [Fact]
    public async Task SuccessfulDailyCheckSurvivesClientAndMonitorRestartsAndRunsAt24Hours()
    {
        using var scope = await AccountScope.CreateAsync();
        using (var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock))
        {
            var checks = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => monitor.CheckIfDueAsync()));
            Assert.Single(checks, checkedServer => checkedServer); Assert.Equal(1, scope.Handler.SessionChecks);
        }
        using var restarted = new AccountService(scope.Paths, scope.Protector, scope.Http, scope.Clock);
        using var next = new AccountDeviceMonitor(restarted, scope.Paths, scope.Clock);
        scope.Clock.Advance(TimeSpan.FromHours(23)); Assert.False(await next.CheckIfDueAsync());
        Assert.Equal(1, scope.Handler.SessionChecks);
        scope.Clock.Advance(TimeSpan.FromHours(1)); Assert.True(await next.CheckIfDueAsync());
        Assert.False(await next.CheckIfDueAsync()); Assert.Equal(2, scope.Handler.SessionChecks);
        var checkpoint = await File.ReadAllTextAsync(Path.Combine(scope.Paths.Data, "device-check.json"));
        Assert.DoesNotContain(scope.Handler.User.Id.ToString(), checkpoint);
        Assert.DoesNotContain(scope.Handler.CurrentDeviceId.ToString(), checkpoint);
    }

    [Fact]
    public async Task KickingAnAlreadyBlockedOtherDeviceKeepsTheCurrentSession()
    {
        using var scope = await AccountScope.CreateAsync(); var notices = 0;
        scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        var index = scope.Handler.Devices.FindIndex(device => device.DeviceId == scope.Handler.OtherDeviceId);
        scope.Handler.Devices[index] = scope.Handler.Devices[index] with { State = "blocked" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.KickAsync(scope.Handler.OtherDeviceId));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.True((await scope.Membership.RefreshAsync()).IsCurrentDeviceActive);
        Assert.Equal(0, notices);
    }

    [Fact]
    public async Task OfflineDailyCheckKeepsTheSessionAndRetriesWithoutRecordingSuccess()
    {
        using var scope = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        scope.Handler.Offline = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => monitor.CheckIfDueAsync());
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(scope.Paths.Data, "device-check.json")));
        scope.Handler.Offline = false;
        Assert.True(await monitor.CheckIfDueAsync()); Assert.Equal(1, scope.Handler.SessionChecks);
    }

    [Fact]
    public async Task DailyCheckReportsInvalidationOnceAndClearsThePersistedSession()
    {
        using var scope = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        var notices = 0; scope.Accounts.DeviceInvalidated += (_, _) => notices++;
        scope.Handler.Revoked = true;
        Assert.True(await monitor.CheckIfDueAsync()); Assert.False(await monitor.CheckIfDueAsync());
        Assert.Equal(1, notices); Assert.False((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        using var restarted = new AccountService(scope.Paths, scope.Protector, scope.Http, scope.Clock);
        Assert.False((await restarted.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task CorruptedCheckpointIsReplacedAfterASuccessfulCheck()
    {
        using var scope = await AccountScope.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(scope.Paths.Data, "device-check.json"), "broken checkpoint");
        using var monitor = new AccountDeviceMonitor(scope.Accounts, scope.Paths, scope.Clock);
        Assert.True(await monitor.CheckIfDueAsync()); Assert.False(await monitor.CheckIfDueAsync());
    }

    [Fact]
    public async Task DeviceIdentityPersistsAndKeystoreFailureNeverReplacesIt()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(Path.Combine(root, "uninitialized")); var protector = new EncryptedProtector();
            using var account = new AccountService(paths, protector);
            var identities = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => account.GetDeviceAsync()));
            var id = Assert.Single(identities.Select(item => item.DeviceId).Distinct()); Assert.NotEqual(Guid.Empty, id);
            var file = Path.Combine(paths.Data, "device-identity.bin"); var protectedBytes = await File.ReadAllBytesAsync(file);
            Assert.False(protectedBytes.AsSpan().SequenceEqual(id.ToByteArray()));
            using var restored = new AccountService(paths, protector); Assert.Equal(id, (await restored.GetDeviceAsync()).DeviceId);
            protector.Unavailable = true;
            using var unavailable = new AccountService(paths, protector);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.GetDeviceAsync());
            Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(file));
            protector.Unavailable = false; Assert.Equal(id, (await unavailable.GetDeviceAsync()).DeviceId);
        }
        finally { TestHelpers.TryDelete(root); }
    }

    [Fact]
    public async Task EncryptedBackupNeverTransfersTheDeviceIdentitySessionOrMembershipCheckpoint()
    {
        using var source = await AccountScope.CreateAsync();
        using var monitor = new AccountDeviceMonitor(source.Accounts, source.Paths, source.Clock); await monitor.CheckIfDueAsync();
        await BackupTests.SetBackupKeyAsync(source.Paths);
        await new SqliteBookLibraryService(source.Paths, new BookMetadataService()).InitializeAsync();
        var archive = Path.Combine(source.Root, "fictional.libzora");
        await new AppBackupService(source.Paths, new TestHelpers.PlaintextSecretProtector()).ExportAsync(archive);
        using (var zip = await BackupTests.OpenBackupArchiveAsync(archive))
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("device-identity") || entry.FullName.Contains("account-session") || entry.FullName.Contains("device-check"));
        var targetPaths = new AppPaths(Path.Combine(source.Root, "target"));
        await BackupTests.SetBackupKeyAsync(targetPaths);
        await new SqliteBookLibraryService(targetPaths, new BookMetadataService()).InitializeAsync();
        using var target = new AccountService(targetPaths, source.Protector, source.Http, source.Clock);
        var targetId = (await target.GetDeviceAsync()).DeviceId;
        await new AppBackupService(targetPaths, new TestHelpers.PlaintextSecretProtector()).ImportAsync(archive);
        Assert.NotEqual(source.Handler.CurrentDeviceId, targetId); Assert.Equal(targetId, (await target.GetDeviceAsync()).DeviceId);
        Assert.False((await target.GetStatusAsync()).IsLoggedIn);
        Assert.False(File.Exists(Path.Combine(targetPaths.Data, "device-check.json")));
    }

    [Fact]
    public async Task OneTimeTrialUnlocksOnlyCustomSyncAndDoesNotGrantStorageQuotas()
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.LifetimeFeatures = false; scope.Handler.TrialAvailable = true;
        var claimed = await scope.Membership.ClaimTrialAsync();
        Assert.Equal("trial", claimed.Tier);
        Assert.Null(claimed.ProExpiresAt);
        Assert.Equal(scope.Handler.Now.AddDays(7), claimed.FeatureExpiresAt);
        Assert.Equal(0, claimed.StorageQuota); Assert.Equal(0, claimed.DownloadRemainingBytes);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.AccountCloudSync));
        scope.Clock.Advance(TimeSpan.FromDays(7)); scope.Handler.Now = scope.Clock.UtcNow;
        var replay = await scope.Membership.ClaimTrialAsync();
        Assert.Equal(claimed.TrialClaimedAt, replay.TrialClaimedAt);
        Assert.Equal(claimed.TrialExpiresAt, replay.TrialExpiresAt);
        Assert.Equal("free", replay.Tier); Assert.False(replay.TrialAvailable);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Fact]
    public async Task HistoricalPaidPeriodsStayLimitedAndNeverBecomeLifetimePurchases()
    {
        using var scope = await AccountScope.CreateAsync();
        var now = scope.Handler.Now;
        var member = (await scope.Membership.RefreshAsync()).Membership with
        {
            Tier = "pro", ProExpiresAt = now.AddDays(365),
            TrialClaimed = true, TrialClaimedAt = now, TrialExpiresAt = now.AddDays(7)
        };
        Assert.Equal(now.AddDays(365), member.FeatureExpiresAt);
        Assert.Equal("trial", MemberEntitlements.EffectiveTier(member, now.AddDays(10)));
        Assert.Equal("free", MemberEntitlements.EffectiveTier(member, now.AddDays(365)));
        Assert.Equal("lifetime", MemberEntitlements.EffectiveTier(member with { Tier = "lifetime" }, now.AddYears(10)));
    }

    [Fact]
    public async Task RetiredCatalogOrdersAndPurchaseCallsNeverReachTheAccountServer()
    {
        using var scope = await AccountScope.CreateAsync();
        var requests = scope.Handler.RequestedActions.ToArray();
        Assert.Empty(await scope.Membership.ProductsAsync());
        Assert.Empty(await scope.Membership.OrdersAsync());
        foreach (var product in new[] { "fictional-lifetime", "old-pro", "old-storage", "old-download", "" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.PurchaseAsync(product, Guid.NewGuid()));
            Assert.Contains("不处理购买订单", error.Message);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.CancelOrderAsync(Guid.NewGuid()));
        Assert.Equal(requests, scope.Handler.RequestedActions);
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("sync")]
    [InlineData("download")]
    public async Task RemovedProviderIsRejectedBeforeAuthorizationEvenWithAValidCustomConnection(string operation)
    {
        using var scope = await AccountScope.CreateAsync();
        var creations = 0;
        Func<S3SyncSettings, ISyncObjectStore> factory = _ =>
        { creations++; throw new InvalidOperationException("Object store must not be created"); };
        var sync = new S3SyncService(scope.Paths, scope.Protector, factory, scope.Membership);
        var settings = new S3SyncSettings
        {
            Provider = SyncProvider.AccountServer, Enabled = true,
            Endpoint = "https://example.invalid", AccessKey = "fictional-access", SecretKey = "fictional-secret",
            Bucket = "fictional-bucket", EncryptionKey = "fictional-sync-key"
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (operation == "test") await sync.TestConnectionAsync(settings);
            else if (operation == "sync") await sync.SyncAsync(Guid.NewGuid().ToString("N"), settings);
            else await sync.EnsureBookFileDownloadedAsync(Guid.NewGuid().ToString("N"), settings,
                new BookFile { Sha256 = new string('a', 64), RelativePath = "books/fictional.epub" });
        });
        Assert.Contains("官方云同步已移除", error.Message);
        Assert.Equal(0, creations); Assert.Equal(0, scope.Handler.FeatureChecks);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private sealed class EncryptedProtector : ISecretProtector
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool Unavailable { get; set; }
        public byte[] Protect(byte[] value)
        {
            var output = new byte[value.Length + 28]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(output.AsSpan(0, 12), value, output.AsSpan(28), output.AsSpan(12, 16)); return output;
        }
        public byte[] Unprotect(byte[] value)
        {
            if (Unavailable) throw new CryptographicException("fictional locked keystore");
            var output = new byte[value.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), output); return output;
        }
    }

    [Fact]
    public async Task RightsRefreshAndTrialClaimRequireLoginBeforeSendingAnyRequest()
    {
        using var scope = await AccountScope.CreateAsync();
        await scope.Accounts.SignOutAsync();
        var requests = scope.Handler.RequestedActions.ToArray();
        var error = await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.RefreshAsync());
        Assert.Contains("登录", error.Message);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.ClaimTrialAsync());
        Assert.Equal(requests, scope.Handler.RequestedActions);
        Assert.DoesNotContain(typeof(DeviceMembershipService).GetMethods(), method => method.Name.Contains("Redeem", StringComparison.Ordinal));
        Assert.Null(typeof(AccountMembership).Assembly.GetType("Libzora.Core.ActivationCodeRedeemRequest"));
        Assert.Null(typeof(AccountMembership).Assembly.GetType("Libzora.Core.ActivationCodeRedemption"));
    }

    [Fact]
    public async Task AccountBoundLifetimeRightsFollowOnlyServerGrantsAndRevocations()
    {
        using var scope = await AccountScope.CreateAsync(); scope.Handler.LifetimeFeatures = false;
        var free = await scope.Membership.RefreshAsync();
        Assert.Equal("free", free.Membership.Tier); Assert.False(free.CanUseFeatures);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        Assert.False(scope.Handler.LifetimeFeatures);

        // A fictional protected server operation, not a client request, grants the account's rights.
        scope.Handler.LifetimeFeatures = true;
        var lifetime = await scope.Membership.RefreshAsync();
        Assert.Equal("lifetime", lifetime.Membership.Tier); Assert.True(lifetime.CanUseFeatures);
        Assert.Null(lifetime.Membership.FeatureExpiresAt);
        Assert.Null(lifetime.Membership.ProExpiresAt);
        Assert.False(lifetime.CanUseAccountCloudSync);
        Assert.Equal(scope.Handler.CurrentDeviceId, lifetime.CurrentDeviceId);
        Assert.Equal(scope.Handler.User.Id, lifetime.Membership.AccountId);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3);
        await scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav);
        Assert.Equal("lifetime", MemberEntitlements.EffectiveTier(lifetime.Membership, scope.Handler.Now.AddYears(10)));

        scope.Handler.LifetimeFeatures = false;
        Assert.False((await scope.Membership.RefreshAsync()).CanUseFeatures);
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
        await Assert.ThrowsAsync<DeviceFeatureRequiredException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomWebDav));
        Assert.True((await scope.Accounts.GetStatusAsync()).IsLoggedIn);
        Assert.DoesNotContain(scope.Handler.RequestedActions, action => action is "products" or "orders" || action.StartsWith("activation-codes/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("account")] [InlineData("region")] [InlineData("device")] [InlineData("activated")] [InlineData("claimed")] [InlineData("expiry")]
    public async Task MinimalRightsRefreshRejectsMalformedOrMismatchedServerStatus(string field)
    {
        using var scope = await AccountScope.CreateAsync();
        scope.Handler.TransformStatus = status => field switch
        {
            "account" => status with { Membership = status.Membership with { AccountId = Guid.NewGuid() } },
            "region" => status with { Membership = status.Membership with { Region = AccountRegion.International } },
            "device" => status with { CurrentDeviceId = Guid.NewGuid() },
            "activated" => status with { Membership = status.Membership with { ActivatedDeviceCount = 0 } },
            "claimed" => status with { Membership = status.Membership with { TrialClaimed = true, TrialClaimedAt = null } },
            _ => status with { Membership = status.Membership with { TrialClaimed = true, TrialClaimedAt = scope.Handler.Now, TrialExpiresAt = scope.Handler.Now.AddDays(-1) } }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.RefreshAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.EnsureFeatureAsync(DeviceFeature.CustomS3));
    }

    [Fact]
    public async Task HistoricalPaidPeriodsAreReadableButCannotBeRenewed()
    {
        using var scope = await AccountScope.CreateAsync(); var now = scope.Handler.Now;
        var member = (await scope.Membership.RefreshAsync()).Membership;
        Assert.False(MemberEntitlements.CanExtendPlus(member, 365, now));
        Assert.False(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(-10) }, 365, now));
        Assert.False(MemberEntitlements.CanExtendPlus(member with { ProExpiresAt = now.AddDays(1460), PendingPlusDays = 365 }, 365, now));
        var requests = scope.Handler.RequestedActions.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.Membership.PurchaseAsync("old-pro", Guid.NewGuid()));
        Assert.Equal(requests, scope.Handler.RequestedActions);
    }

    [Theory]
    [InlineData("free", 0)] [InlineData("trial", 0)] [InlineData("lifetime", 0)]
    [InlineData("free", 1)] [InlineData("trial", 1)] [InlineData("lifetime", 1)]
    public void MinimalTwelveFieldMembershipDeserializesWithoutRetiredConstructorFields(string tier, int region)
    {
        var account = Guid.NewGuid(); var device = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var expiry = now.AddDays(45);
        var expiryJson = tier == "trial" ? $"\"{expiry:O}\"" : "null";
        var json = $$"""
            {"accountId":"{{account}}","region":{{region}},"tier":"{{tier}}",
             "hasLifetimeFeatures":{{(tier == "free" ? "false" : "true")}},"featureExpiresAt":{{expiryJson}},
             "deviceLimit":6,"loggedInDeviceCount":1,"activatedDeviceCount":{{(tier == "free" ? 0 : 1)}},
             "trialAvailable":{{(tier == "free" ? "true" : "false")}},"trialClaimed":false,
             "trialClaimedAt":null,"trialExpiresAt":null}
            """;
        using var document = JsonDocument.Parse(json);
        Assert.Equal(12, document.RootElement.EnumerateObject().Count());
        var member = JsonSerializer.Deserialize(json, AccountJsonContext.Default.AccountMembership)!;
        Assert.Equal(account, member.AccountId); Assert.Equal((AccountRegion)region, member.Region);
        Assert.Equal(tier, MemberEntitlements.EffectiveTier(member, now));
        Assert.Equal(tier == "trial" ? expiry : (DateTimeOffset?)null, member.FeatureExpiresAt);
        Assert.Null(member.ProExpiresAt); Assert.Null(member.PlusDownloadQuotaBytes);
        Assert.Equal(0, member.UploadBytes); Assert.Equal(0, member.StorageQuota);
        Assert.Equal(0, member.DownloadRemainingBytes);
        var status = new DeviceFeatureStatus(member, [new(device, "Fictional device", "Test", "active", now, now)], device);
        Assert.Equal(tier != "free", status.CanUseFeatures);
        Assert.False(status.CanUseAccountCloudSync);
    }

    [Fact]
    public async Task NewFeatureExpiryIsAuthoritativeWhileOldJsonRetainsOriginalPaidExpiry()
    {
        using var scope = await AccountScope.CreateAsync();
        var now = scope.Handler.Now;
        var member = (await scope.Membership.RefreshAsync()).Membership with
        {
            Tier = "pro", ProExpiresAt = now.AddDays(365), TrialExpiresAt = now.AddDays(7),
            EntitlementExpiresAt = now.AddDays(90)
        };
        var json = JsonSerializer.Serialize(member, AccountJsonContext.Default.AccountMembership);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(now.AddDays(90), document.RootElement.GetProperty("featureExpiresAt").GetDateTimeOffset());
            Assert.False(document.RootElement.TryGetProperty("entitlementExpiresAt", out _));
        }
        var current = JsonSerializer.Deserialize(json, AccountJsonContext.Default.AccountMembership)!;
        Assert.Equal(now.AddDays(90), current.FeatureExpiresAt);
        Assert.Equal("free", MemberEntitlements.EffectiveTier(current, now.AddDays(90)));
        var legacyJson = JsonSerializer.Serialize(member with { EntitlementExpiresAt = null }, AccountJsonContext.Default.AccountMembership);
        var legacy = JsonSerializer.Deserialize(legacyJson, AccountJsonContext.Default.AccountMembership)!;
        Assert.Null(legacy.EntitlementExpiresAt);
        Assert.Equal(now.AddDays(365), legacy.FeatureExpiresAt);
        Assert.Equal("trial", MemberEntitlements.EffectiveTier(legacy, now.AddDays(90)));
        Assert.Equal("free", MemberEntitlements.EffectiveTier(legacy, now.AddDays(365)));
    }

    private sealed class AccountScope : IDisposable
    {
        public string Root { get; } = TestHelpers.CreateTempDirectory();
        public AppPaths Paths { get; }
        public MembershipHttpHandler Handler { get; } = new();
        public EncryptedProtector Protector { get; } = new();
        public Clock Clock { get; }
        public HttpClient Http { get; }
        public AccountService Accounts { get; }
        public DeviceMembershipService Membership { get; }
        private AccountScope()
        {
            Paths = new(Path.Combine(Root, "source")); Clock = new(Handler.Now); Http = new(Handler);
            Accounts = new(Paths, Protector, Http, Clock); Membership = new(Accounts);
        }
        public static async Task<AccountScope> CreateAsync()
        {
            var scope = new AccountScope();
            try { await scope.Accounts.SignInAsync(AccountRegions.DefaultServerUrl(AccountRegion.China), scope.Handler.User.Username, MembershipHttpHandler.Password, true, email: scope.Handler.User.Email, confirmPassword: MembershipHttpHandler.Password); return scope; }
            catch { scope.Dispose(); throw; }
        }
        public void Dispose() { Accounts.Dispose(); Http.Dispose(); SqliteConnection.ClearAllPools(); TestHelpers.TryDelete(Root); }
    }
}
