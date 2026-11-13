using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kkindle.AccountServer;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kkindle.AccountServer.Tests;

public sealed partial class AccountIntegrationTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    [Fact]
    public async Task FeedbackRequiresLoginAndOnlyExposesTheSignedInOwnersItems()
    {
        await using var server = await LocalServer.CreateAsync();
        using (var anonymous = await server.Http.PostAsync("api/v1/feedback", new MultipartFormDataContent()))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var owner = await RegisterAsync(server.Http, "test_reader");
        var other = await RegisterAsync(server.Http, "another_reader");
        var receipt = await SubmitAsync(server.Http, owner.Token, FeedbackForm(Guid.NewGuid(),
            content: "阅读页切换章节时出现闪退。", extra: [("userId", other.User.Id.ToString())]));
        Assert.Equal(FeedbackStatuses.Pending, receipt.Status);
        Assert.Equal(1, receipt.Number);

        using (var own = await GetFeedbackAsync(server.Http, owner.Token, ""))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var item = Assert.Single((await own.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItemArray))!);
            Assert.Equal(receipt.Id, item.Id);
            Assert.Equal(FeedbackTypes.Bug, item.Type);
            Assert.Equal("阅读页切换章节时出现闪退。", item.Content);
            Assert.Equal("12345678", item.Contact);
            Assert.Equal("macos", item.Platform);
            Assert.Equal("15.0", item.PlatformVersion);
            Assert.Equal("MacBookPro18,3", item.DeviceModel);
            Assert.Equal(0, item.AttachmentCount);
            Assert.Null(item.AdminReply);
        }
        using (var otherList = await GetFeedbackAsync(server.Http, other.Token, ""))
            Assert.Empty((await otherList.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItemArray))!);
        using (var otherDetail = await GetFeedbackAsync(server.Http, other.Token, receipt.Id.ToString("N")))
            Assert.Equal(HttpStatusCode.NotFound, otherDetail.StatusCode);
        using (var ownDetail = await GetFeedbackAsync(server.Http, owner.Token, receipt.Id.ToString("N")))
        {
            Assert.Equal(HttpStatusCode.OK, ownDetail.StatusCode);
            Assert.Equal(receipt.Id,
                (await ownDetail.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItem))!.Id);
        }

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(server.RegionDirectory, "accounts.db")}");
        await connection.OpenAsync();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT UserId FROM Feedback WHERE Id=$id;";
        query.Parameters.AddWithValue("$id", receipt.Id.ToString());
        Assert.Equal(owner.User.Id.ToString(), (await query.ExecuteScalarAsync()) as string);
    }

    [Fact]
    public async Task FeedbackStoresScreenshotsOutsideTheSyncNamespace()
    {
        await using var server = await LocalServer.CreateAsync();
        var session = await RegisterAsync(server.Http, "test_reader");
        var form = FeedbackForm(Guid.NewGuid(), content: "同步完成后封面没有刷新。", contact: "reader@example.invalid");
        AddImage(form, PngBytes, "image/png", "screenshot-one.png");
        AddImage(form, JpegBytes, "image/jpeg", "screenshot-two.jpg");
        var receipt = await SubmitAsync(server.Http, session.Token, form);

        var folder = Path.Combine(server.RegionDirectory, "feedback", receipt.Id.ToString("N"));
        var files = Directory.GetFiles(folder);
        Assert.Equal(2, files.Length);
        Assert.Equal(new[] { ".jpg", ".png" }, files.Select(Path.GetExtension).Order().ToArray());
        Assert.False(Directory.Exists(Path.Combine(server.RegionDirectory, "sync", "feedback")));

        using var list = await GetFeedbackAsync(server.Http, session.Token, "");
        var item = Assert.Single((await list.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItemArray))!);
        Assert.Equal(2, item.AttachmentCount);
        Assert.Equal("reader@example.invalid", item.Contact);
        Assert.Equal("同步完成后封面没有刷新。", item.Content);
    }

    [Fact]
    public async Task FeedbackStoresSanitizedSoftwareLogOutsideTheSyncNamespace()
    {
        await using var server = await LocalServer.CreateAsync();
        var session = await RegisterAsync(server.Http, "test_reader");
        var form = FeedbackForm(Guid.NewGuid(), content: "打开书籍时出现异常。");
        form.Add(new StringContent("Kkindle software log\nERROR reader.open exception=System.TimeoutException"),
            "softwareLog");
        var receipt = await SubmitAsync(server.Http, session.Token, form);

        var folder = Path.Combine(server.RegionDirectory, "feedback", receipt.Id.ToString("N"));
        var logPath = Assert.Single(Directory.GetFiles(folder));
        Assert.EndsWith(".txt", logPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reader.open", await File.ReadAllTextAsync(logPath), StringComparison.Ordinal);

        using var list = await GetFeedbackAsync(server.Http, session.Token, "");
        var item = Assert.Single((await list.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItemArray))!);
        Assert.True(item.HasSoftwareLog);
        Assert.Equal(0, item.AttachmentCount);
    }

    [Fact]
    public async Task FeedbackRejectsInvalidFieldsAndScreenshots()
    {
        await using var server = await LocalServer.CreateAsync();
        var session = await RegisterAsync(server.Http, "test_reader");

        using (var invalidType = FeedbackForm(Guid.NewGuid(), type: "crash"))
            await AssertRejected(server.Http, session.Token, invalidType, HttpStatusCode.BadRequest);
        using (var shortContent = FeedbackForm(Guid.NewGuid(), content: "闪退"))
            await AssertRejected(server.Http, session.Token, shortContent, HttpStatusCode.BadRequest);
        using (var longContent = FeedbackForm(Guid.NewGuid(),
                   content: new string('字', FeedbackLimits.MaxContentLength + 1)))
            await AssertRejected(server.Http, session.Token, longContent, HttpStatusCode.BadRequest);
        using (var invalidContact = FeedbackForm(Guid.NewGuid(), contact: "not-a-contact"))
            await AssertRejected(server.Http, session.Token, invalidContact, HttpStatusCode.BadRequest);
        using (var oversizeLog = FeedbackForm(Guid.NewGuid(), content: "软件日志过大时拒绝提交。"))
        {
            oversizeLog.Add(new StringContent(new string('x', FeedbackLimits.MaxSoftwareLogBytes + 1)),
                "softwareLog");
            await AssertRejected(server.Http, session.Token, oversizeLog, HttpStatusCode.BadRequest);
        }
        using (var invalidLog = FeedbackForm(Guid.NewGuid(), content: "软件日志包含控制字符时拒绝提交。"))
        {
            invalidLog.Add(new StringContent("bad\0log"), "softwareLog");
            await AssertRejected(server.Http, session.Token, invalidLog, HttpStatusCode.BadRequest);
        }

        var fakeImage = FeedbackForm(Guid.NewGuid(), content: "截图格式无效的测试反馈。");
        AddImage(fakeImage, "not an image"u8.ToArray(), "image/png", "fake.png");
        using (fakeImage) await AssertRejected(server.Http, session.Token, fakeImage, HttpStatusCode.BadRequest);

        var tooMany = FeedbackForm(Guid.NewGuid(), content: "截图数量超限的测试反馈。");
        for (var index = 0; index < FeedbackLimits.MaxAttachments + 1; index++)
            AddImage(tooMany, PngBytes, "image/png", $"shot-{index}.png");
        using (tooMany) await AssertRejected(server.Http, session.Token, tooMany, HttpStatusCode.BadRequest);

        var oversize = FeedbackForm(Guid.NewGuid(), content: "截图体积超限的测试反馈。");
        var bigImage = new byte[FeedbackLimits.MaxAttachmentBytes + 1];
        PngBytes.CopyTo(bigImage, 0);
        AddImage(oversize, bigImage, "image/png", "big.png");
        using (oversize) await AssertRejected(server.Http, session.Token, oversize, HttpStatusCode.RequestEntityTooLarge);

        using var empty = await GetFeedbackAsync(server.Http, session.Token, "");
        Assert.Empty((await empty.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackItemArray))!);
    }

    [Fact]
    public async Task FeedbackDuplicateRequestReturnsTheOriginalReceipt()
    {
        await using var server = await LocalServer.CreateAsync();
        var session = await RegisterAsync(server.Http, "test_reader");
        var requestId = Guid.NewGuid();
        var form = FeedbackForm(requestId, content: "重复提交只会生成一条反馈。");
        AddImage(form, PngBytes, "image/png", "shot.png");
        var first = await SubmitAsync(server.Http, session.Token, form);
        using (var duplicate = await SendFormAsync(server.Http, session.Token,
                   FeedbackForm(requestId, content: "重复提交只会生成一条反馈。")))
        {
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
            var receipt = (await duplicate.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackReceipt))!;
            Assert.Equal(first.Id, receipt.Id);
            Assert.Equal(first.Number, receipt.Number);
        }

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(server.RegionDirectory, "accounts.db")}");
        await connection.OpenAsync();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*), COUNT(AttachmentUrls) FROM Feedback;";
        await using var reader = await count.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Single(Directory.GetFiles(Path.Combine(server.RegionDirectory, "feedback", first.Id.ToString("N"))));
    }

    [Fact]
    public async Task FeedbackRateLimitRejectsRapidResubmission()
    {
        await using var server = await LocalServer.CreateAsync();
        var session = await RegisterAsync(server.Http, "test_reader");
        for (var index = 0; index < FeedbackLimits.PerUserWindowLimit; index++)
            await SubmitAsync(server.Http, session.Token,
                FeedbackForm(Guid.NewGuid(), content: $"高频提交测试反馈 {index}。"));
        using var limited = await SendFormAsync(server.Http, session.Token,
            FeedbackForm(Guid.NewGuid(), content: "这条反馈应当被频率限制拒绝。"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task FeedbackDailyLimitAppliesAcrossTimeWindows()
    {
        var clock = new TestClock();
        await using var server = await LocalServer.CreateAsync(clock);
        var session = await RegisterAsync(server.Http, "test_reader");
        var store = server.Application.Services.GetRequiredService<AccountDatabase>();
        for (var index = 0; index < FeedbackLimits.PerUserDayLimit; index++)
        {
            await store.CreateFeedbackAsync(Guid.NewGuid(), session.User, new FeedbackDraft
            {
                RequestId = Guid.NewGuid(),
                Type = FeedbackTypes.Other,
                Content = $"每日上限测试反馈 {index}。",
                AppVersion = "1.0.0",
                Platform = "macos",
                PlatformVersion = "15.0",
                DeviceModel = "Mac14,2"
            }, [], default);
            clock.Advance(TimeSpan.FromMinutes(FeedbackLimits.PerUserWindowMinutes + 1));
        }
        var exception = await Assert.ThrowsAsync<AccountRequestException>(() => store.CreateFeedbackAsync(Guid.NewGuid(), session.User,
            new FeedbackDraft
            {
                RequestId = Guid.NewGuid(),
                Type = FeedbackTypes.Other,
                Content = "超过每日上限的测试反馈。",
                AppVersion = "1.0.0",
                Platform = "macos",
                PlatformVersion = "15.0",
                DeviceModel = "Mac14,2"
            }, [], default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, exception.StatusCode);
    }

    [Fact]
    public async Task ClientSubmitsFeedbackAndReadsItsOwnHistory()
    {
        await using var server = await LocalServer.CreateAsync();
        var paths = new AppPaths(Path.Combine(server.Directory, "client"));
        using var client = new AccountService(paths, new TestProtector());
        await client.SignInAsync(server.Url, "test_reader", TestPassword, register: true,
            email: "reader@example.invalid", confirmPassword: TestPassword);
        var receipt = await client.SubmitFeedbackAsync(new FeedbackDraft
        {
            RequestId = Guid.NewGuid(),
            Type = FeedbackTypes.Feature,
            Content = "希望支持自定义字体。",
            Contact = "reader@example.invalid",
            AppVersion = "1.0.0",
            Platform = "macos",
            PlatformVersion = "15.0",
            DeviceModel = "Mac14,2"
        }, [new FeedbackImage("shot.png", "image/png", PngBytes)],
            "Kkindle software log\nERROR reader.open exception=System.TimeoutException");
        var item = Assert.Single(await client.GetFeedbackAsync());
        Assert.Equal(receipt.Id, item.Id);
        Assert.Equal(FeedbackTypes.Feature, item.Type);
        Assert.Equal(1, item.AttachmentCount);
        Assert.True(item.HasSoftwareLog);
        Assert.Equal("希望支持自定义字体。", (await client.GetFeedbackAsync(receipt.Id)).Content);
    }

    private static MultipartFormDataContent FeedbackForm(Guid requestId, string type = FeedbackTypes.Bug,
        string content = "阅读页切换章节时出现闪退。", string? contact = "12345678",
        string appVersion = "1.0.0", string platform = "macos", string platformVersion = "15.0",
        string deviceModel = "MacBookPro18,3", (string Name, string Value)[]? extra = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(requestId.ToString()), "requestId");
        form.Add(new StringContent(type), "type");
        form.Add(new StringContent(content), "content");
        form.Add(new StringContent(contact ?? ""), "contact");
        form.Add(new StringContent(appVersion), "appVersion");
        form.Add(new StringContent(platform), "platform");
        form.Add(new StringContent(platformVersion), "platformVersion");
        form.Add(new StringContent(deviceModel), "deviceModel");
        foreach (var (name, value) in extra ?? []) form.Add(new StringContent(value), name);
        return form;
    }

    private static void AddImage(MultipartFormDataContent form, byte[] bytes, string contentType, string fileName)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, "attachments", fileName);
    }

    private static async Task<FeedbackReceipt> SubmitAsync(HttpClient http, string token, MultipartFormDataContent form)
    {
        using var response = await SendFormAsync(http, token, form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync(AccountJsonContext.Default.FeedbackReceipt))!;
    }

    private static async Task<HttpResponseMessage> SendFormAsync(HttpClient http, string token,
        MultipartFormDataContent form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/feedback");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = form;
        return await http.SendAsync(request);
    }

    private static Task<HttpResponseMessage> GetFeedbackAsync(HttpClient http, string token, string suffix)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            "api/v1/feedback" + (suffix.Length == 0 ? "" : "/" + suffix));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(request);
    }

    private static async Task AssertRejected(HttpClient http, string token, MultipartFormDataContent form,
        HttpStatusCode expected)
    {
        using var response = await SendFormAsync(http, token, form);
        Assert.Equal(expected, response.StatusCode);
    }
}
