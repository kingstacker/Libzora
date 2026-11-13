using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Libzora.Core;
using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderTranslationTests(SettingsUiSession session)
{
    [Theory]
    [InlineData("empty", "无法识别", false)]
    [InlineData("network", "无法连接", false)]
    [InlineData("timeout", "超时", false)]
    [InlineData("success", "你好", true)]
    public Task TranslationShowsAReadableResultOrFailure(string response, string expected, bool succeeded) =>
        session.Session.Dispatch<bool>(async () =>
        {
            await using var scope = await ReaderTestWindow.Create();
            using var ai = new AiChatClient(new Handler(response));
            using var service = new TranslationService(ai);
            Configure(scope, service, local: true);
            await ReaderTests.Render();
            await scope.Call<Task>("TranslateReaderSelectionAsync", ReaderTranslationProvider.Ai, 0, "");
            await ReaderTests.Render();
            Assert.Contains(expected, scope.Get<TextBlock>("ReaderTranslationResultText").Text);
            Assert.Equal(succeeded, scope.Get<Button>("ReaderTranslationCopyButton").IsEnabled);
            Assert.Equal(succeeded, scope.Get<Button>("ReaderTranslationAnnotateButton").IsEnabled);
            Assert.Contains(succeeded ? "完成" : "重试", scope.Get<TextBlock>("ReaderTranslationStatusText").Text);
            scope.Call("HideReaderTranslationPopup", false);
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemoteTranslationSendsNothingUntilTheUserApproves(bool approve) =>
        session.Session.Dispatch<bool>(async () =>
        {
            await using var scope = await ReaderTestWindow.Create();
            var handler = new Handler("success");
            using var ai = new AiChatClient(handler);
            using var service = new TranslationService(ai);
            Configure(scope, service, local: false);
            var pending = scope.Call<Task>("TranslateReaderSelectionAsync", ReaderTranslationProvider.Ai, 0, "");
            Assert.Equal(0, handler.Requests);
            Assert.Contains("https://translation.example.test", scope.Get<TextBlock>("ConfirmationMessageText").Text);
            Assert.Contains("所选正文", scope.Get<TextBlock>("ConfirmationMessageText").Text);
            scope.Field<TaskCompletionSource<bool>>("_confirmationCompletion").TrySetResult(approve);
            await pending;
            Assert.Equal(approve ? 1 : 0, handler.Requests);
            scope.Call("HideReaderTranslationPopup", false);
            return true;
        }, CancellationToken.None);

    private static void Configure(ReaderTestWindow scope, TranslationService service, bool local)
    {
        scope.Field<TranslationService>("_translationService").Dispose();
        scope.Set("_translationService", service);
        scope.Set("_readerAiSettings", new AiConnectionSettings
        {
            Provider = "custom", BaseUrl = local ? "http://127.0.0.1:11434/v1" : "https://translation.example.test/v1",
            Model = "translation-model", ApiKey = local ? "" : "test-key"
        });
        scope.Set("_appSettings", scope.Field<AppSettings>("_appSettings") with { NetworkEnabled = !local, AiEnabled = true });
        scope.Set("_readerPendingSelection", "Hello");
        scope.Get<Control>("LibraryRoot").IsVisible = false;
        scope.Get<Control>("ReaderRoot").IsVisible = true;
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (response == "network") throw new HttpRequestException("无法连接模型服务");
            if (response == "timeout") throw new TimeoutException("模型服务请求超时");
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var text = response == "success" ? "你好" : "";
            var data = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + data + "\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
