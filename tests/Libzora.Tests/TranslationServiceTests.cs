using System.Net;
using System.Text;
using System.Text.Json;
using Libzora.Infrastructure;
using Libzora.Core;

namespace Libzora.Tests;

public sealed class TranslationServiceTests
{
    [Fact]
    public void NormalizesGoogleProxyAddress()
    {
        Assert.Equal(
            "http://127.0.0.1:7890",
            TranslationProxy.NormalizeAddress("  127.0.0.1:7890/  "));
        Assert.Equal(string.Empty, TranslationProxy.NormalizeAddress("  "));
        Assert.Throws<ArgumentException>(
            () => TranslationProxy.NormalizeAddress("socks5://127.0.0.1:1080"));
    }

    [Theory]
    [InlineData(ReaderTranslationProvider.Google)]
    [InlineData(ReaderTranslationProvider.Bing)]
    public async Task RetiredWebProvidersAreRejectedBeforeAnyRequest(ReaderTranslationProvider provider)
    {
        var requests = 0;
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ =>
        {
            requests++;
            throw new InvalidOperationException("No request is allowed");
        }));
        using var service = new TranslationService(ai);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.TranslateAsync("Hello", provider, "zh-CN"));
        Assert.Contains("已停用", error.Message);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(BookTranslationProvider.BingFree)]
    [InlineData(BookTranslationProvider.GoogleFree)]
    public void RetiredSavedSettingsMigrateToModelTranslation(BookTranslationProvider provider)
        => Assert.Equal(BookTranslationProvider.Ai, BookTranslationSettings.Normalize(new() { Provider = provider }).Provider);

    [Fact]
    public async Task UnconfiguredTranslationDoesNotFallBackToAnOnlineService()
    {
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(_ => throw new InvalidOperationException("No network")));
        using var service = new TranslationService(ai);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync("Hello", ReaderTranslationProvider.Ai, "zh-CN"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("https://api.example.com/v1", false)]
    [InlineData("https://localhost.example.com/v1", false)]
    [InlineData("file:///tmp/model", false)]
    public void OnlyLoopbackModelEndpointsCanOmitAnApiKey(string endpoint, bool expected)
        => Assert.Equal(expected, new AiConnectionSettings { BaseUrl = endpoint, Model = "local-model" }.IsConfigured);

    [Fact]
    public async Task LocalModelTranslatesWithoutAnAuthorizationHeader()
    {
        using var ai = new AiChatClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return ModelReply("你好");
        }));
        using var service = new TranslationService(ai);
        Assert.Equal("你好", await service.TranslateAsync("Hello", ReaderTranslationProvider.Ai, "zh-CN",
            new AiConnectionSettings { Provider = "custom", BaseUrl = "http://127.0.0.1:11434/v1", Model = "local-model" }));
    }

    internal static HttpResponseMessage ModelReply(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } })
            + "\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
    };

    [Fact]
    public async Task TranslatesWithConfiguredAiWithoutAddingChatHistory()
    {
        using var aiClient = TestHelpers.CreateActivatedAiClient(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(payload);
            var messages = document.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Contains(
                messages.EnumerateArray(),
                message => message.GetProperty("content").GetString()!.Contains("Hello world"));
            Assert.DoesNotContain(
                messages.EnumerateArray(),
                message => message.GetProperty("content").GetString()!.Contains("旧对话"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"你好，世界\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));
        using var service = new TranslationService(aiClient);

        var result = await service.TranslateAsync(
            "Hello world",
            ReaderTranslationProvider.Ai,
            "zh-CN",
            new AiConnectionSettings
            {
                Provider = "custom",
                BaseUrl = "https://api.example.com/v1",
                Model = "translation-model",
                ApiKey = "sk-test"
            });

        Assert.Equal("你好，世界", result);
    }

}
