using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Libzora.Core;
using Xunit;

namespace Libzora.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    // The former opt-in cloud test must never connect to an old deployment or
    // read local credential files. Retirement is verified on a fresh local host.
    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task RemovedCloudRoutesReturnGoneForEveryMethodWithoutTouchingHistoricalFiles(AccountRegion region)
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-retired-cloud-" + Guid.NewGuid().ToString("N"));
        var historical = Path.Combine(root, AccountRegions.Slug(region), "sync", "historical", "fictional-object.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(historical)!);
        byte[] sentinel = [11, 29, 47, 61];
        await File.WriteAllBytesAsync(historical, sentinel);
        try
        {
            await using var server = await LocalServer.CreateAsync(region: region, sharedDirectory: root);
            var account = await RegisterAsync(server.Http, "retirement_reader");
            foreach (var authorized in new[] { false, true })
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Options })
            foreach (var path in new[]
            {
                "api/sync", "api/sync/objects?key=fictional-object.bin", "api/sync/uploads",
                "api/sync/uploads/fictional-upload/complete", "api/sync/uploads/fictional-upload/cancel",
                "api/sync/downloads", "sync-blobs", "sync-blobs/fictional-old-signed-token"
            })
            {
                using var request = new HttpRequestMessage(method, path);
                if (authorized) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
                if (method == HttpMethod.Put || method == HttpMethod.Post || method == HttpMethod.Patch)
                    request.Content = new StringContent("fictional-body-that-must-not-be-stored");
                using var response = await server.Http.SendAsync(request);
                Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
                Assert.Equal("official_cloud_removed", Assert.Single(response.Headers.GetValues("X-Libzora-Error")));
                Assert.True(response.Headers.CacheControl?.NoStore);
                if (method != HttpMethod.Head)
                    Assert.Equal(MemberEntitlements.AccountCloudRequirement,
                        (await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountApiError))!.Message);
            }
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(historical));
            Assert.Equal(historical, Assert.Single(Directory.GetFiles(Path.Combine(root, AccountRegions.Slug(region), "sync"), "*", SearchOption.AllDirectories)));
            using var session = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", account.Token);
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
