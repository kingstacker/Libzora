using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN", false)]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    public Task FeedbackGuideNeedsNoAccountAndHasNoUploadControls(string language, bool signedIn) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        if (signedIn) await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword);
        var requests = handler.RequestCount;
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Render();
            Assert.Equal("https://github.com/kingstacker/Libzora/issues", window.FindControl<TextBlock>("IssuesUrlText")!.Text);
            var explanation = window.FindControl<TextBlock>("FeedbackServiceNote")!.Text!;
            Assert.Contains("GitHub Issues", explanation);
            Assert.Contains(language == "en-US" ? "does not accept" : "不接收", explanation);
            Assert.True(window.FindControl<Button>("OpenIssuesButton")!.IsEnabled);
            foreach (var name in new[] { "LoginPanel", "OpenAccountCenterButton", "FormPanel", "ContentBox", "ContactBox",
                         "AddScreenshotButton", "ScreenshotPanel", "AttachSoftwareLogCheckBox", "SubmitButton", "SuccessPanel" })
                Assert.Null(window.FindControl<Control>(name));
            Assert.Equal(requests, handler.RequestCount);
            Assert.Equal(signedIn, (await service.GetStatusAsync()).IsLoggedIn);
            window.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
            ((Libzora.App)Application.Current!).ApplyLanguage("zh-CN");
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RetiredFeedbackServiceCallsNeverUploadOrInvalidateTheSession(bool signedIn) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        if (signedIn) await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword);
        var requests = handler.RequestCount;
        var draft = new FeedbackDraft { RequestId = Guid.NewGuid(), Type = FeedbackTypes.Bug, Content = "Fictional retired feedback report.",
            AppVersion = "test", Platform = "macos", PlatformVersion = "test", DeviceModel = "fictional" };
        var image = new FeedbackImage("fictional.png", "image/png", [0x89, 0x50, 0x4E, 0x47]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitFeedbackAsync(draft, [image]));
        Assert.Contains("不接收反馈", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitFeedbackAsync(draft, [image], "fictional software log"));
        Assert.Empty(await service.GetFeedbackAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetFeedbackAsync(Guid.NewGuid()));
        Assert.Equal(requests, handler.RequestCount);
        Assert.Null(handler.LastFeedbackFields);
        Assert.Null(handler.LastFeedbackLog);
        Assert.Equal(0, handler.LastFeedbackImageCount);
        Assert.Equal(signedIn, (await service.GetStatusAsync()).IsLoggedIn);
    });

    [Fact]
    public Task OpeningFeedbackGuideDoesNotCaptureOrUploadLocalLogs() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        SoftwareLog.Configure(scope.Paths, "test-version", "macos");
        SoftwareLog.Error("reader.open", new InvalidOperationException("fictional reader failure"));
        var logPath = Path.Combine(scope.Paths.Data, "logs", "software.log");
        var before = await File.ReadAllBytesAsync(logPath);
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Render();
            Assert.Equal(before, await File.ReadAllBytesAsync(logPath));
            Assert.Equal(0, handler.RequestCount);
            Assert.Null(handler.LastFeedbackLog);
            Assert.Null(window.FindControl<CheckBox>("AttachSoftwareLogCheckBox"));
        }
        finally { window.Close(); }
    });
}
