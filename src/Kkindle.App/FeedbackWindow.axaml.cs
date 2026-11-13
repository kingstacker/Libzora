using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Kkindle.Core;
using Kkindle.Infrastructure;
using SkiaSharp;

namespace Kkindle;

/// <summary>
/// Collects one feedback report for the signed-in account. Screenshots stay in
/// memory, are re-encoded to bounded JPEGs, and are dropped when the window closes.
/// </summary>
public partial class FeedbackWindow : Window
{
    private const int MaximumScreenshotEdge = 1600;
    private const int ScreenshotJpegQuality = 82;

    private readonly AccountService? _accounts;
    private readonly Action? _openAccountCenter;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<SelectedScreenshot> _screenshots = [];
    // One idempotency key per window: retrying after a network error must not
    // create a second report if the first request actually reached the server.
    private readonly Guid _requestId = Guid.NewGuid();
    private bool _submitting;
    private bool _loggedIn;

    public FeedbackWindow() : this(null) { }

    public FeedbackWindow(AccountService? accounts, Action? openAccountCenter = null)
    {
        InitializeComponent();
        _accounts = accounts;
        _openAccountCenter = openAccountCenter;
        foreach (var (value, name) in FeedbackTypes.All)
            TypeBox.Items.Add(new ComboBoxItem { Content = UiText.Get(name), Tag = value });
        TypeBox.SelectedIndex = 0;
        ContentCountText.Text = $"0/{FeedbackLimits.MaxContentLength}";
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            foreach (var screenshot in _screenshots) screenshot.Preview.Dispose();
            _screenshots.Clear();
            _lifetime.Dispose();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Opened += async (_, _) => await LoadAccountStateAsync();
    }

    private async Task LoadAccountStateAsync()
    {
        if (_accounts is null) return;
        try { PresentAccount(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token)); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            PresentAccount(null);
            ShowStatus(UiText.Localize(exception.Message), error: true);
        }
    }

    private void PresentAccount(AccountStatus? status)
    {
        _loggedIn = status is { IsLoggedIn: true, RequiresDataRestart: false };
        LoginPanel.IsVisible = !_loggedIn;
        FormPanel.IsVisible = _loggedIn;
        if (status is { RequiresDataRestart: true })
            LoginNoticeText.Text = UiText.Get("区域已切换，请重新打开应用后提交反馈。");
        else
            LoginNoticeText.Text = UiText.Get("登录后即可提交问题反馈。");
        AccountLabel.IsVisible = _loggedIn && status?.User is not null;
        if (_loggedIn && status?.User is not null)
            AccountLabel.Text = UiText.Get("将以 {0} 提交反馈。", status.User.Username);
    }

    private void OnOpenAccountCenterClick(object? sender, RoutedEventArgs e)
    {
        _openAccountCenter?.Invoke();
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnContentChanged(object? sender, TextChangedEventArgs e) =>
        ContentCountText.Text = $"{ContentBox.Text?.Length ?? 0}/{FeedbackLimits.MaxContentLength}";

    private async void OnAddScreenshotClick(object? sender, RoutedEventArgs e)
    {
        if (_submitting || _accounts is null) return;
        if (_screenshots.Count >= FeedbackLimits.MaxAttachments)
        {
            ShowStatus(UiText.Get("最多只能添加 {0} 张截图。", FeedbackLimits.MaxAttachments));
            return;
        }
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = ResourceText("Ui.Feedback.PickTitle", "选择截图"),
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                        MimeTypes = ["image/png", "image/jpeg", "image/webp"]
                    }
                ]
            });
            var unreadable = 0;
            var tooLarge = 0;
            var overTotal = 0;
            foreach (var file in files)
            {
                if (_screenshots.Count >= FeedbackLimits.MaxAttachments) break;
                byte[] source;
                await using (var stream = await file.OpenReadAsync())
                {
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory, _lifetime.Token);
                    source = memory.ToArray();
                }
                var compressed = CompressScreenshot(source);
                if (compressed is null)
                {
                    unreadable++;
                    continue;
                }
                if (compressed.LongLength > FeedbackLimits.MaxAttachmentBytes)
                {
                    tooLarge++;
                    continue;
                }
                var total = _screenshots.Sum(item => item.Image.Bytes.LongLength) + compressed.LongLength;
                if (total > FeedbackLimits.MaxTotalAttachmentBytes)
                {
                    overTotal++;
                    continue;
                }
                _screenshots.Add(new SelectedScreenshot(
                    new FeedbackImage("screenshot.jpg", "image/jpeg", compressed),
                    new Bitmap(new MemoryStream(compressed))));
            }
            RebuildScreenshots();
            if (unreadable > 0) ShowStatus(UiText.Get("有 {0} 张截图无法读取，请换一张图片。", unreadable), error: true);
            else if (tooLarge > 0) ShowStatus(UiText.Get("有 {0} 张截图处理后仍超过 4 MB，请换一张。", tooLarge), error: true);
            else if (overTotal > 0) ShowStatus(UiText.Get("截图总大小不能超过 10 MB。"), error: true);
            else if (_screenshots.Count >= FeedbackLimits.MaxAttachments)
                ShowStatus(UiText.Get("最多只能添加 {0} 张截图。", FeedbackLimits.MaxAttachments));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus(UiText.Localize(exception.Message), error: true);
        }
    }

    private void RebuildScreenshots()
    {
        ScreenshotPanel.Children.Clear();
        foreach (var screenshot in _screenshots)
        {
            var remove = new Button
            {
                Content = UiText.Get("移除"),
                FontSize = 11,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(8, 3),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            remove.Classes.Set("quiet", true);
            remove.Click += (_, _) =>
            {
                _screenshots.Remove(screenshot);
                screenshot.Preview.Dispose();
                RebuildScreenshots();
            };
            ScreenshotPanel.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 10, 8),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                BorderBrush = Application.Current?.FindResource("HairlineBrush") as IBrush,
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Image { Source = screenshot.Preview, Width = 112, Height = 84, Stretch = Stretch.Uniform },
                        remove
                    }
                }
            });
        }
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        if (_submitting || _accounts is null) return;
        if (!_loggedIn)
        {
            PresentAccount(null);
            return;
        }
        var draft = BuildDraft();
        if (FeedbackValidation.ValidateDraft(draft) is { } invalid)
        {
            ShowStatus(UiText.Get(invalid), error: true);
            ContentBox.Focus();
            return;
        }
        SetBusy(true);
        ShowStatus(UiText.Get("正在提交反馈…"));
        try
        {
            var softwareLog = AttachSoftwareLogCheckBox.IsChecked == true
                ? SoftwareLog.CaptureFeedbackLog()
                : null;
            var receipt = await _accounts.SubmitFeedbackAsync(draft,
                _screenshots.Select(item => item.Image).ToArray(), softwareLog, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            FormPanel.IsVisible = false;
            LoginPanel.IsVisible = false;
            SuccessPanel.IsVisible = true;
            SuccessNumberText.Text = UiText.Get("反馈编号：#{0}", receipt.Number);
            StatusText.IsVisible = false;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SoftwareLog.Error("feedback.submit", exception);
            ShowStatus(UiText.Localize(exception.Message), error: true);
            try { PresentAccount(await _accounts.GetStatusAsync(cancellationToken: _lifetime.Token)); }
            catch (Exception statusException) when (statusException is not OutOfMemoryException) { }
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private FeedbackDraft BuildDraft() => new()
    {
        RequestId = _requestId,
        Type = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "",
        Content = ContentBox.Text ?? "",
        Contact = string.IsNullOrWhiteSpace(ContactBox.Text) ? null : ContactBox.Text.Trim(),
        AppVersion = ApplicationVersion.GetDisplayVersion(typeof(FeedbackWindow).Assembly),
        Platform = PlatformName(),
        PlatformVersion = Environment.OSVersion.Version.ToString(),
        DeviceModel = DeviceModel()
    };

    private void SetBusy(bool busy)
    {
        _submitting = busy;
        SubmitButton.IsEnabled = !busy;
        AddScreenshotButton.IsEnabled = !busy;
        TypeBox.IsEnabled = !busy;
        ContentBox.IsEnabled = !busy;
        ContactBox.IsEnabled = !busy;
        AttachSoftwareLogCheckBox.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
        StatusText.Foreground = Application.Current?.FindResource(error ? "DangerBrush" : "MutedInkBrush") as IBrush;
    }

    private static string ResourceText(string key, string fallback) =>
        Application.Current?.FindResource(key)?.ToString() ?? fallback;

    private static string PlatformName() =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    private static string DeviceModel()
    {
        string? model = null;
        try
        {
            if (OperatingSystem.IsMacOS()) model = ReadSysctl("hw.model");
            else if (OperatingSystem.IsLinux() && File.Exists("/sys/devices/virtual/dmi/id/product_name"))
                model = File.ReadAllText("/sys/devices/virtual/dmi/id/product_name").Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        if (string.IsNullOrWhiteSpace(model))
            model = $"{PlatformName()} {RuntimeInformation.OSArchitecture}";
        return model.Length > FeedbackLimits.MaxDeviceModelLength
            ? model[..FeedbackLimits.MaxDeviceModelLength]
            : model;
    }

    private static string ReadSysctl(string name)
    {
        nuint length = 0;
        if (sysctlbyname(name, null, ref length, IntPtr.Zero, 0) != 0 || length is 0 or > 4096) return "";
        var buffer = new byte[(int)length];
        if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0) return "";
        return System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(length, (nuint)buffer.Length))
            .TrimEnd('\0').Trim();
    }

    /// <summary>Bounds screenshots at 1600 px and re-encodes them as opaque JPEG.</summary>
    private static byte[]? CompressScreenshot(byte[] source)
    {
        using var data = SKData.CreateCopy(source);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null) return null;
        var scale = Math.Min(1d, MaximumScreenshotEdge / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var opaque = new SKBitmap(info);
        using (var canvas = new SKCanvas(opaque))
        {
            canvas.Clear(SKColors.White);
            if (width == bitmap.Width && height == bitmap.Height)
                canvas.DrawBitmap(bitmap, 0, 0);
            else
            {
                using var scaled = bitmap.Resize(info,
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (scaled is not null) canvas.DrawBitmap(scaled, 0, 0);
            }
        }
        using var encoded = opaque.Encode(SKEncodedImageFormat.Jpeg, ScreenshotJpegQuality);
        return encoded?.ToArray();
    }

    private sealed record SelectedScreenshot(FeedbackImage Image, Bitmap Preview);

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[]? oldp,
        ref nuint oldlenp, IntPtr newp, nuint newlen);
}
