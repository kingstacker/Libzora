using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>Lists the two external feedback channels: GitHub Issues and the QQ group.</summary>
public partial class FeedbackWindow : Window
{
    private const string IssuesUrl = "https://github.com/kingstacker/Libzora/issues";
    private const string UserGroup = "1109898894";

    public FeedbackWindow() : this(null) { }

    // Keep the existing callers source-compatible; the support page never uses the account service.
    public FeedbackWindow(AccountService? accounts, Action? openAccountCenter = null)
    {
        InitializeComponent();
        ConfigureWindowChrome();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private void ConfigureWindowChrome()
    {
        if (MacTrafficLights.IsSupported)
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = MacTrafficLights.TitleBarHeight;
            WindowFrame.BorderThickness = new Thickness(0);
        }
        else
        {
            WindowDecorations = WindowDecorations.None;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
            WindowFrame.BorderThickness = new Thickness(1);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnOpenIssuesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await Launcher.LaunchUriAsync(new Uri(IssuesUrl))) return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        ShowStatus(UiText.Get("无法打开浏览器，请手动访问 GitHub Issues。"), isSuccess: false);
    }

    private async void OnCopyUserGroupClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                ShowStatus(Localized("Ui.About.UserGroupCopyFailed", "复制群号失败。"), isSuccess: false);
                return;
            }
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, UserGroup);
            ShowStatus(Localized("Ui.About.UserGroupCopied", "群号已复制到剪贴板。"), isSuccess: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus(Localized("Ui.About.UserGroupCopyFailed", "复制群号失败。"), isSuccess: false);
        }
    }

    private void ShowStatus(string text, bool isSuccess)
    {
        StatusText.Text = text;
        StatusText.Foreground = AppAppearanceResources.GetBrush(isSuccess ? "SuccessBrush" : "DangerBrush");
        StatusText.IsVisible = true;
    }

    private static string Localized(string key, string fallback) =>
        Application.Current?.FindResource(key)?.ToString() ?? fallback;
}
