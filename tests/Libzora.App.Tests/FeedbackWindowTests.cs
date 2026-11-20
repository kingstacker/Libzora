using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
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
            Assert.Null(window.FindControl<TextBlock>("IssuesUrlText"));
            Assert.Null(window.FindControl<TextBlock>("FeedbackServiceNote"));
            Assert.Null(window.FindControl<Button>("OpenAccountCenterButton"));
            Assert.True(window.FindControl<Button>("OpenIssuesButton")!.IsEnabled);
            var group = window.FindControl<Button>("CopyUserGroupButton")!;
            Assert.True(group.IsEnabled);
            Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("1109898894") == true);
            foreach (var name in new[] { "LoginPanel", "FormPanel", "ContentBox", "ContactBox",
                         "AddScreenshotButton", "ScreenshotPanel", "AttachSoftwareLogCheckBox", "SubmitButton", "SuccessPanel" })
                Assert.Null(window.FindControl<Control>(name));
            Assert.Equal(requests, handler.RequestCount);
            Assert.Equal(signedIn, (await service.GetStatusAsync()).IsLoggedIn);
            group.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<TextBlock>("StatusText")!.IsVisible);
            Assert.Equal("1109898894", await window.Clipboard!.TryGetTextAsync());
            Assert.Equal(language == "en-US" ? "Group number copied to clipboard." : "群号已复制到剪贴板。",
                window.FindControl<TextBlock>("StatusText")!.Text);
            Capture(window, $"{language}-{(signedIn ? "signed-in" : "signed-out")}-feedback");
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
