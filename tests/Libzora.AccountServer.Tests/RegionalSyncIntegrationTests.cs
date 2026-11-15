using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    [Fact]
    public async Task RegionalServersKeepSameUsernameAndSessionsSeparateWithoutExposingObjectStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-region-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var china = await LocalServer.CreateAsync(sharedDirectory: root);
            await using var international = await LocalServer.CreateAsync(region: AccountRegion.International, sharedDirectory: root);
            var chinese = await RegisterAsync(china.Http, "test_reader");
            var foreign = await RegisterAsync(international.Http, "test_reader");
            Assert.NotEqual(chinese.User.Id, foreign.User.Id);
            Assert.Equal(AccountRegion.China, chinese.Region);
            Assert.Equal(AccountRegion.International, foreign.Region);
            using (var cross = await AuthorizedAsync(international.Http, HttpMethod.Get, "session", chinese.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            using (var cross = await AuthorizedAsync(china.Http, HttpMethod.Get, "session", foreign.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
            using var wrongHeader = new HttpRequestMessage(HttpMethod.Get, "api/accounts/session");
            wrongHeader.Headers.Add("X-Libzora-Region", "international");
            wrongHeader.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chinese.Token);
            using var rejected = await china.Http.SendAsync(wrongHeader);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            foreach (var http in new[] { china.Http, international.Http })
            {
                using var gone = await http.GetAsync("api/sync/objects");
                Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
            }
            Assert.True(File.Exists(Path.Combine(china.RegionDirectory, "accounts.db")));
            Assert.True(File.Exists(Path.Combine(international.RegionDirectory, "accounts.db")));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task TwoLocalDevicesKeepBooksAndReadingDataWhenOfficialSyncIsRemoved(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var firstBase = new AppPaths(Path.Combine(server.Directory, "client-a"));
        var secondBase = new AppPaths(Path.Combine(server.Directory, "client-b"));
        var protector = new TestProtector();
        using var firstAccount = new AccountService(firstBase, protector, dataRegion: region);
        using var secondAccount = new AccountService(secondBase, protector, dataRegion: region);
        await firstAccount.SelectRegionAsync(region); await secondAccount.SelectRegionAsync(region);
        await firstAccount.SignInAsync(server.Url, "test_reader", TestPassword, register: true, email: "reader@example.invalid", confirmPassword: TestPassword);
        await secondAccount.SignInAsync(server.Url, "test_reader", TestPassword);
        var firstPaths = AccountRegionPaths.ForRegion(firstBase, region);
        var secondPaths = AccountRegionPaths.ForRegion(secondBase, region);
        var firstLibrary = new SqliteBookLibraryService(firstPaths, new BookMetadataService());
        var secondLibrary = new SqliteBookLibraryService(secondPaths, new BookMetadataService());
        await firstLibrary.InitializeAsync(); await secondLibrary.InitializeAsync();
        var firstReader = new ReaderDataService(firstPaths); await firstReader.InitializeAsync();
        var book = await ImportTestBookAsync(firstLibrary, server.Directory, region.ToString());
        var file = Assert.Single(book.Files);
        var original = await File.ReadAllBytesAsync(Path.Combine(firstPaths.Data, file.RelativePath));
        await firstReader.AddReadingTimeAsync(book.Id, file.Id, 120, 10, 1, 10);
        await firstReader.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(), BookId = book.Id, BookFileId = file.Id, ChapterPath = "chapter.xhtml",
            SelectedText = "fictional regional quote", Note = "fictional regional note", StartOffset = 0, EndOffset = 5
        });
        var historical = new S3SyncSettings
        {
            Provider = SyncProvider.AccountServer, Enabled = true, AccountRegion = region,
            AccountServerUrl = server.Url, AccountUserId = (await firstAccount.GetStatusAsync()).User!.Id,
            EncryptionKey = "fictional-regional-sync-test-key"
        };
        foreach (var accounts in new[] { firstAccount, secondAccount })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.CreateSyncSettingsAsync(historical));
            Assert.Equal(MemberEntitlements.AccountCloudRequirement, error.Message);
        }
        var sync = new S3SyncService(firstPaths, protector, firstAccount, region);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.TestConnectionAsync(historical));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAsync(Guid.NewGuid().ToString("N"), historical));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sync.EnsureBookFileDownloadedAsync(Guid.NewGuid().ToString("N"), historical, file));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(firstPaths.Data, file.RelativePath)));
        Assert.Single(await firstLibrary.SearchAsync());
        Assert.Empty(await secondLibrary.SearchAsync());
        Assert.Equal("fictional regional note", Assert.Single(await firstReader.GetAllAnnotationsAsync()).Note);
        Assert.Equal(120, (await firstReader.GetReadingDashboardAsync()).DailyReading.Sum(day => day.ActiveSeconds));
        Assert.True((await firstAccount.GetStatusAsync(validateSession: true)).IsLoggedIn);
        Assert.True((await secondAccount.GetStatusAsync(validateSession: true)).IsLoggedIn);
    }

    [Fact]
    public async Task SwitchingRegionsRestoresIndependentCustomConnectionsAndBlocksOldLibraryUntilReopen()
    {
        await using var china = await LocalServer.CreateAsync();
        await using var international = await LocalServer.CreateAsync(region: AccountRegion.International);
        var basePaths = new AppPaths(Path.Combine(china.Directory, "client"));
        var protector = new TestProtector();
        ApplicationModules Open() => new(new ApplicationModuleOptions
        {
            Paths = basePaths, SecretProtector = protector, FormatConverter = new UnavailableBookFormatConverter(), MetadataService = new BookMetadataService()
        });
        using var firstRun = Open();
        var chinaStatus = await firstRun.Accounts.Service.SignInAsync(china.Url, "test_reader", TestPassword, register: true, email: "reader@example.invalid", confirmPassword: TestPassword);
        await firstRun.Library.Books.InitializeAsync(); await firstRun.Reader.Data.InitializeAsync();
        await ImportTestBookAsync((SqliteBookLibraryService)firstRun.Library.Books, china.Directory, "China only");
        var chinese = await firstRun.Sync.Service.LoadSettingsAsync();
        var chineseSettings = S3SyncSettings.Normalize(chinese.Settings with
        {
            Provider = SyncProvider.S3, ProviderChosenByUser = true, Endpoint = "https://china-storage.example.invalid",
            Bucket = "fictional-china", AccessKey = "fictional-china-access", SecretKey = "fictional-china-secret",
            EncryptionKey = "fictional-china-sync-key"
        });
        await firstRun.Sync.Service.SaveSettingsAsync(chinese.DeviceId, chineseSettings);
        var switched = await firstRun.Accounts.Service.SelectRegionAsync(AccountRegion.International);
        Assert.False(switched.IsLoggedIn); Assert.True(switched.RequiresDataRestart);
        var foreignStatus = await firstRun.Accounts.Service.SignInAsync(international.Url, "test_reader", TestPassword, register: true, email: "reader@example.invalid", confirmPassword: TestPassword);
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstRun.Sync.Service.SaveSettingsAsync(chinese.DeviceId, chineseSettings));
        using var secondRun = Open();
        Assert.Equal(AccountRegion.International, secondRun.DataRegion);
        Assert.NotEqual(basePaths.Database, secondRun.Paths.Database);
        Assert.Equal(foreignStatus.User, (await secondRun.Accounts.Service.GetStatusAsync(true)).User);
        Assert.False((await secondRun.Accounts.Service.GetStatusAsync()).RequiresDataRestart);
        await secondRun.Library.Books.InitializeAsync(); Assert.Empty(await secondRun.Library.Books.SearchAsync());
        var foreign = await secondRun.Sync.Service.LoadSettingsAsync();
        var foreignSettings = S3SyncSettings.Normalize(foreign.Settings with
        {
            Provider = SyncProvider.WebDav, ProviderChosenByUser = true, AccountRegion = AccountRegion.International,
            WebDavEndpoint = "https://international-storage.example.invalid/dav", WebDavUsername = "fictional-foreign-user",
            WebDavPassword = "fictional-foreign-password", EncryptionKey = "fictional-international-sync-key"
        });
        await secondRun.Sync.Service.SaveSettingsAsync(foreign.DeviceId, foreignSettings);
        var restored = await secondRun.Accounts.Service.SelectRegionAsync(AccountRegion.China);
        Assert.Equal(chinaStatus.User, restored.User); Assert.Equal(china.Url, restored.ServerUrl); Assert.True(restored.RequiresDataRestart);
        using var thirdRun = Open();
        Assert.Equal(basePaths.Database, thirdRun.Paths.Database);
        Assert.Single(await thirdRun.Library.Books.SearchAsync());
        Assert.Equal(chinaStatus.User, (await thirdRun.Accounts.Service.GetStatusAsync(true)).User);
        Assert.Equal(chineseSettings, (await thirdRun.Sync.Service.LoadSettingsAsync()).Settings);
    }

    [Theory]
    [InlineData("C:/outside.bin")]
    [InlineData("folder\\..\\outside.bin")]
    [InlineData("folder/name.")]
    public async Task RetiredObjectApiNeverProcessesPathsOrCreatesFiles(string key)
    {
        await using var server = await LocalServer.CreateAsync();
        var account = await RegisterAsync(server.Http, "test_reader");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sync/uploads")
        { Content = JsonContent.Create(new SyncUploadRequest { Key = key, Length = 19 }, AccountJsonContext.Default.SyncUploadRequest) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("official_cloud_removed", Assert.Single(response.Headers.GetValues("X-Libzora-Error")));
    }

    private static async Task<Book> ImportTestBookAsync(SqliteBookLibraryService library, string root, string title)
    {
        var source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".epub");
        using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
        {
            Write("mimetype", "application/epub+zip");
            Write("META-INF/container.xml", """
                <?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Write("content.opf", $"""
                <?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">fictional-regional-test</dc:identifier><dc:title>{title}</dc:title><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Write("chapter.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>Fictional local sync test.</p></body></html>");
            void Write(string name, string content)
            {
                using var output = new StreamWriter(archive.CreateEntry(name, CompressionLevel.NoCompression).Open(), new UTF8Encoding(false));
                output.Write(content);
            }
        }
        await library.ImportAsync([source]);
        return Assert.Single(await library.SearchAsync());
    }
}
