using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Libzora.AccountServer;
using Libzora.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    private static readonly HttpMethod[] RetiredFeedbackMethods =
        [HttpMethod.Get, HttpMethod.Head, HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch,
            HttpMethod.Delete, HttpMethod.Options, HttpMethod.Trace, new("PROPFIND")];

    private static readonly string[] RetiredFeedbackPaths =
        ["api/v1/feedback", "api/v1/feedback/", "api/v1/feedback/11111111-1111-1111-1111-111111111111",
            "api/v1/feedback/not-a-guid/attachments/fictional.png", "API/V1/FEEDBACK?content=fictional-query"];

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task FreshAccountSchemaContainsOnlyAuthenticationTables(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var account = await RegisterAsync(server.Http, "retirement_reader");
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(server.RegionDirectory, "accounts.db")}");
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var tables = new List<string>();
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        Assert.Equal(new[] { "AccountSessions", "Accounts" }, tables);
        Assert.Empty(Directory.GetDirectories(server.RegionDirectory));
        using var session = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", account.Token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task RemovedFeedbackRoutesReturnGoneForEveryMethodRegardlessOfAuthenticationOrRegion(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var account = await RegisterAsync(server.Http, "retirement_reader");
        using var http = new HttpClient { BaseAddress = server.Http.BaseAddress };
        var wrongRegion = region == AccountRegion.China ? "international" : "china";
        foreach (var bearer in new string?[] { null, account.Token, "malformed-token" })
        foreach (var header in new string?[] { null, AccountRegions.Slug(region), wrongRegion })
        foreach (var method in RetiredFeedbackMethods)
        foreach (var path in RetiredFeedbackPaths)
        {
            using var request = new HttpRequestMessage(method, path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (header is not null) request.Headers.Add("X-Libzora-Region", header);
            if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
                request.Content = new StringContent("fictional-content-that-must-not-be-stored");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            Assert.Equal("feedback_removed", Assert.Single(response.Headers.GetValues("X-Libzora-Error")));
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (method != HttpMethod.Head)
                Assert.Equal("账号服务不再接收反馈、截图或软件日志。",
                    (await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.AccountApiError))!.Message);
        }
        Assert.Empty(Directory.GetDirectories(server.RegionDirectory));
        using var session = await AuthorizedAsync(server.Http, HttpMethod.Get, "session", account.Token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task RetiredFeedbackMiddlewareNeverReadsEvenMalformedOrOversizedRequestBodies(AccountRegion region)
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-unread-feedback-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = await AccountServerHost.BuildAsync([
                "--urls", "http://127.0.0.1:0", "--AccountServer:DataDirectory", root,
                "--AccountServer:Region", region.ToString(), "--Logging:LogLevel:Default", "None"]);
            // Exercise the actual middleware with a stream that throws on every
            // read, instead of inferring body handling from a network response.
            var pipeline = ((IApplicationBuilder)app).Build();
            foreach (var method in RetiredFeedbackMethods)
            foreach (var path in RetiredFeedbackPaths)
            {
                using var input = new ThrowOnReadStream();
                using var output = new MemoryStream();
                var context = new DefaultHttpContext();
                context.RequestServices = app.Services;
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                context.Request.Scheme = "http";
                context.Request.Method = method.Method;
                context.Request.Path = "/" + path.Split('?')[0];
                context.Request.Headers.Authorization = "Bearer malformed-token";
                context.Request.ContentType = "multipart/form-data; boundary=never-read";
                context.Request.ContentLength = long.MaxValue;
                context.Request.Body = input;
                context.Response.Body = output;
                await pipeline(context);
                Assert.Equal(StatusCodes.Status410Gone, context.Response.StatusCode);
                Assert.Equal("feedback_removed", context.Response.Headers["X-Libzora-Error"].ToString());
                Assert.False(input.ReadAttempted);
            }
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, AccountRegions.Slug(region))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task RetiredFeedbackDoesNotPersistContentScreenshotsSoftwareLogsOrQueryStrings(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var account = await RegisterAsync(server.Http, "retirement_reader");
        var before = await SnapshotFilesAsync(server.RegionDirectory);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(Guid.NewGuid().ToString()), "requestId");
        form.Add(new StringContent("bug"), "type");
        form.Add(new StringContent("fictional feedback content that must never be persisted"), "content");
        form.Add(new StringContent("fictional-contact@example.invalid"), "contact");
        form.Add(new StringContent("fictional software log that must never be persisted"), "softwareLog");
        form.Add(new StringContent("1.0.0"), "appVersion");
        form.Add(new StringContent("macos"), "platform");
        form.Add(new StringContent("15.0"), "platformVersion");
        form.Add(new StringContent("fictional-device"), "deviceModel");
        var image = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "attachments", "../../fictional-screenshot.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/feedback?content=fictional-private-query")
            { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        using var response = await server.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var after = await SnapshotFilesAsync(server.RegionDirectory);
        Assert.Equal(before.Keys.Order().ToArray(), after.Keys.Order().ToArray());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        Assert.Empty(Directory.GetDirectories(server.RegionDirectory));
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task RestartLeavesHistoricalFeedbackTablesAttachmentsCloudFilesAndLedgerUntouched(AccountRegion region)
    {
        await using var server = await LocalServer.CreateAsync(region: region);
        var account = await RegisterAsync(server.Http, "retirement_reader");
        var feedbackId = Guid.NewGuid();
        await CreateHistoricalFeedbackAsync(server.RegionDirectory, account.User.Id, feedbackId);
        var historical = new[]
        {
            Path.Combine(server.RegionDirectory, "feedback", feedbackId.ToString("N"), "fictional-shot.png"),
            Path.Combine(server.RegionDirectory, "feedback", feedbackId.ToString("N"), "fictional-log.txt"),
            Path.Combine(server.RegionDirectory, "feedback", feedbackId.ToString("N"), "fictional-upload.part"),
            Path.Combine(server.RegionDirectory, "sync", "historical", "fictional-cloud.bin"),
            Path.Combine(server.RegionDirectory, "historical-license-ledger.json")
        };
        foreach (var path in historical)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "fictional retained historical data: " + Path.GetFileName(path));
        }
        var before = await SnapshotFilesAsync(server.RegionDirectory);
        var modified = historical.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var oldRows = await ReadHistoricalFeedbackAsync(server.RegionDirectory);
        await server.Application.StopAsync();
        await using var restarted = await LocalServer.CreateAsync(region: region, sharedDirectory: server.Directory);
        foreach (var method in RetiredFeedbackMethods)
        {
            using var request = new HttpRequestMessage(method, "api/v1/feedback/" + feedbackId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
            using var response = await restarted.Http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        }
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent("fictional new feedback must not overwrite retained data"), "content");
            using var response = await restarted.Http.PostAsync("api/v1/feedback", form);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        }
        Assert.Equal(oldRows, await ReadHistoricalFeedbackAsync(server.RegionDirectory));
        Assert.Equal(before.Keys.Order().ToArray(), Directory.GetFiles(server.RegionDirectory, "*", SearchOption.AllDirectories).Order().ToArray());
        foreach (var path in historical)
        {
            Assert.Equal(before[path], await File.ReadAllBytesAsync(path));
            Assert.Equal(modified[path], File.GetLastWriteTimeUtc(path));
        }
        using var session = await AuthorizedAsync(restarted.Http, HttpMethod.Get, "session", account.Token);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var login = await CredentialsAsync(restarted.Http, "login", "retirement_reader", TestPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Theory]
    [InlineData(AccountRegion.China)]
    [InlineData(AccountRegion.International)]
    public async Task AuthenticationStillRejectsRemoteHttpAndMissingOrIncorrectRegionHeaders(AccountRegion region)
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-account-guards-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var app = await AccountServerHost.BuildAsync([
                "--urls", "http://127.0.0.1:0", "--AccountServer:DataDirectory", root,
                "--AccountServer:Region", region.ToString(), "--Logging:LogLevel:Default", "None"]);
            var pipeline = ((IApplicationBuilder)app).Build();
            foreach (var scheme in new[] { "http", "https" })
            foreach (var header in new string?[] { null, region == AccountRegion.China ? "international" : "china" })
            {
                using var output = new MemoryStream();
                var context = new DefaultHttpContext();
                context.RequestServices = app.Services;
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                context.Request.Scheme = scheme;
                context.Request.Path = "/api/accounts/session";
                context.Request.Method = "GET";
                // An untrusted caller cannot turn remote HTTP into trusted HTTPS.
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
                if (header is not null) context.Request.Headers["X-Libzora-Region"] = header;
                context.Response.Body = output;
                await pipeline(context);
                Assert.Equal(scheme == "http" ? StatusCodes.Status400BadRequest : StatusCodes.Status403Forbidden,
                    context.Response.StatusCode);
                Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Dictionary<string, byte[]>> SnapshotFilesAsync(string directory)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            files.Add(path, await File.ReadAllBytesAsync(path));
        return files;
    }

    private static async Task CreateHistoricalFeedbackAsync(string directory, Guid accountId, Guid feedbackId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "accounts.db")};Foreign Keys=True");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        // This obsolete schema exists only in the test's newly created database.
        command.CommandText = """
            CREATE TABLE Feedback (
                Id TEXT PRIMARY KEY, Number INTEGER NOT NULL UNIQUE,
                UserId TEXT NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                RequestId TEXT NOT NULL, Type TEXT NOT NULL, Content TEXT NOT NULL, Contact TEXT,
                AppVersion TEXT NOT NULL, Platform TEXT NOT NULL, PlatformVersion TEXT NOT NULL,
                DeviceModel TEXT NOT NULL, AttachmentUrls TEXT NOT NULL, Status TEXT NOT NULL,
                AdminReply TEXT, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
                AdminRepliedAt INTEGER, UNIQUE(UserId, RequestId));
            CREATE INDEX IX_Feedback_User ON Feedback(UserId, CreatedAt DESC);
            CREATE INDEX IX_Feedback_Status ON Feedback(Status, CreatedAt DESC);
            CREATE INDEX IX_Feedback_Type ON Feedback(Type, CreatedAt DESC);
            INSERT INTO Feedback VALUES ($id, 1, $account, $request, 'bug', 'fictional retained feedback',
                'fictional@example.invalid', '1.0.0', 'macos', '15.0', 'fictional-device',
                '["feedback/fictional-shot.png","feedback/fictional-log.txt"]', 'pending',
                'fictional retained reply', 1, 2, 2);
            """;
        command.Parameters.AddWithValue("$id", feedbackId.ToString());
        command.Parameters.AddWithValue("$account", accountId.ToString());
        command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ReadHistoricalFeedbackAsync(string directory)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "accounts.db")}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Feedback;";
        var values = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                for (var column = 0; column < reader.FieldCount; column++) values.Add(reader.GetValue(column).ToString()!);
        command.CommandText = "SELECT name, COALESCE(sql, '') FROM sqlite_schema WHERE name LIKE '%Feedback%' ORDER BY name;";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) { values.Add(reader.GetString(0)); values.Add(reader.GetString(1)); }
        return values.ToArray();
    }

    private sealed class ThrowOnReadStream : MemoryStream
    {
        public bool ReadAttempted { get; private set; }
        private Exception ReadFailure()
        {
            ReadAttempted = true;
            return new InvalidOperationException("Retired feedback middleware must not read request bodies.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw ReadFailure();
        public override int Read(Span<byte> buffer) => throw ReadFailure();
        public override int ReadByte() => throw ReadFailure();
        public override void CopyTo(Stream destination, int bufferSize) => throw ReadFailure();
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => throw ReadFailure();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw ReadFailure();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw ReadFailure();
    }
}
