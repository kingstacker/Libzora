using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>Shows external support guidance without collecting or uploading account data, screenshots or logs.</summary>
public partial class FeedbackWindow : Window
{
    public FeedbackWindow() : this(null) { }

    // Keep the existing callers source-compatible; the support page never uses the account service.
    public FeedbackWindow(AccountService? accounts, Action? openAccountCenter = null)
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnOpenIssuesClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (await Launcher.LaunchUriAsync(new Uri("https://github.com/kingstacker/Libzora/issues"))) return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        StatusText.Text = UiText.Get("无法打开浏览器，请手动访问 GitHub Issues。");
        StatusText.IsVisible = true;
    }
}
