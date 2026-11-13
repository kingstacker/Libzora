using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.TestFixtures;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task FeedbackWindowRequiresSignInBeforeShowingTheForm() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http);
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Until(() => window.FindControl<Border>("LoginPanel")!.IsVisible);
            Assert.False(window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            Assert.Equal(0, handler.CredentialRequests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FeedbackWindowSubmitsThroughTheSignedInAccount() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Avalonia.Application.Current!).ApplyLanguage("zh-CN");
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http, transferHttp: http);
        await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword,
            register: true, email: "reader@example.invalid", confirmPassword: AccountHttpHandler.TestPassword);
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Until(() => window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            var type = window.FindControl<ComboBox>("TypeBox")!;
            Assert.Equal(FeedbackTypes.Bug, ((ComboBoxItem)type.SelectedItem!).Tag);
            Assert.True(window.FindControl<CheckBox>("AttachSoftwareLogCheckBox")!.IsChecked);
            window.FindControl<TextBox>("ContentBox")!.Text = "点击同步后书籍封面没有刷新。";
            window.FindControl<TextBox>("ContactBox")!.Text = "reader@example.invalid";
            window.FindControl<Button>("SubmitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<Border>("SuccessPanel")!.IsVisible);
            Assert.Contains("#7", window.FindControl<TextBlock>("SuccessNumberText")!.Text!);
            Assert.False(window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            Assert.NotNull(handler.LastFeedbackFields);
            Assert.Equal(FeedbackTypes.Bug, handler.LastFeedbackFields!["type"]);
            Assert.Equal("点击同步后书籍封面没有刷新。", handler.LastFeedbackFields["content"]);
            Assert.Equal("reader@example.invalid", handler.LastFeedbackFields["contact"]);
            Assert.Equal("macos", handler.LastFeedbackFields["platform"]);
            Assert.Equal(0, handler.LastFeedbackImageCount);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FeedbackWindowAttachesASanitizedSoftwareLog() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Avalonia.Application.Current!).ApplyLanguage("zh-CN");
        SoftwareLog.Configure(scope.Paths, "test-version", "macos");
        SoftwareLog.Error("reader.open",
            new InvalidOperationException("无法打开 /Users/test-reader/Books/三体.epub，reader@example.invalid"),
            ("format", "epub"));
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http, transferHttp: http);
        await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword,
            register: true, email: "reader@example.invalid", confirmPassword: AccountHttpHandler.TestPassword);
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Until(() => window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            window.FindControl<TextBox>("ContentBox")!.Text = "打开书籍时出现异常。";
            window.FindControl<Button>("SubmitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<Border>("SuccessPanel")!.IsVisible);

            var log = handler.LastFeedbackLog;
            Assert.NotNull(log);
            Assert.Contains("session.start", log, StringComparison.Ordinal);
            Assert.Contains("reader.open", log, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
            Assert.Contains("<path>", log, StringComparison.Ordinal);
            Assert.DoesNotContain("三体", log, StringComparison.Ordinal);
            Assert.DoesNotContain("reader@example.invalid", log, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/test-reader", log, StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FeedbackWindowOmitsSoftwareLogsWhenUnchecked() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Avalonia.Application.Current!).ApplyLanguage("zh-CN");
        SoftwareLog.Configure(scope.Paths, "test-version", "macos");
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http, transferHttp: http);
        await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword,
            register: true, email: "reader@example.invalid", confirmPassword: AccountHttpHandler.TestPassword);
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Until(() => window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            window.FindControl<CheckBox>("AttachSoftwareLogCheckBox")!.IsChecked = false;
            window.FindControl<TextBox>("ContentBox")!.Text = "不需要附加日志的反馈。";
            window.FindControl<Button>("SubmitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<Border>("SuccessPanel")!.IsVisible);
            Assert.Null(handler.LastFeedbackLog);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FeedbackRetryReusesTheRequestId() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Avalonia.Application.Current!).ApplyLanguage("zh-CN");
        using var handler = new AccountHttpHandler();
        using var http = new HttpClient(handler);
        using var service = new AccountService(scope.Paths, new AccountTestProtector(), http, transferHttp: http);
        await service.SignInAsync(AccountService.DefaultServerUrl, "test_reader", AccountHttpHandler.TestPassword,
            register: true, email: "reader@example.invalid", confirmPassword: AccountHttpHandler.TestPassword);
        handler.FailNextFeedback = true;
        var window = new FeedbackWindow(service);
        try
        {
            window.Show();
            await Until(() => window.FindControl<StackPanel>("FormPanel")!.IsVisible);
            window.FindControl<TextBox>("ContentBox")!.Text = "网络失败后重试不应生成重复反馈。";
            var submit = window.FindControl<Button>("SubmitButton")!;
            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => submit.IsEnabled);
            Assert.False(window.FindControl<Border>("SuccessPanel")!.IsVisible);
            var status = window.FindControl<TextBlock>("StatusText")!;
            Assert.True(status.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(status.Text));
            var requestId = handler.LastFeedbackFields!["requestId"];

            submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.FindControl<Border>("SuccessPanel")!.IsVisible);
            Assert.Equal(requestId, handler.LastFeedbackFields!["requestId"]);
        }
        finally { window.Close(); }
    });
}
