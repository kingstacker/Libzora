using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Libzora;

public partial class MainWindow
{
    private FeedbackWindow? _feedbackWindow;

    private void OpenFeedbackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_feedbackWindow is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new FeedbackWindow(_modules.Accounts.Service,
            () => AccountNavigationButton_Click(null, new RoutedEventArgs()));
        _feedbackWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_feedbackWindow, window)) _feedbackWindow = null;
        };
        window.Show(this);
    }
}
