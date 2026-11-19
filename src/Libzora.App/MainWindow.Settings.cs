using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Libzora;

public sealed record SettingsSearchResult(
    string Title,
    string Detail,
    string CategoryLabel,
    string AccessibleName,
    string Category,
    Control Target,
    Expander? Expander,
    string SearchCorpus);
public partial class MainWindow
{
    private async void AboutUserGroupButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                ShowSettingsCapsule(T("剪贴板不可用。"), 4000);
                return;
            }
            await clipboard.SetTextAsync("1109898894");
            ShowSettingsCapsule(Application.Current?.FindResource("Ui.About.UserGroupCopied")?.ToString() ?? "群号已复制到剪贴板。", 2000, success: true);
        }
        catch
        {
            ShowSettingsCapsule(Application.Current?.FindResource("Ui.About.UserGroupCopyFailed")?.ToString() ?? "复制群号失败。", 4000);
        }
    }

    private string _activeSettingsCategory = "General";
    private bool _mainReaderAiSettingsLoaded;
    private Task? _mainReaderAiSettingsLoadTask;
    private readonly SemaphoreSlim _appSettingsSaveGate = new(1, 1);
    private long _appSettingsEditVersion;
    private long _appSettingsSavedVersion;
    private bool _skipS3SyncOnExit;
    private bool _allowWindowCloseForDataRootSwitch;
    private bool _s3SettingsDirty;
    private bool _s3SettingsSaving;
    private long _s3SettingsEditVersion;
    private Task<bool>? _s3SettingsSaveTask;
    private bool _suppressKindleEmailProviderChange;
    private bool _kindleEmailTestBusy;
    private CancellationTokenSource? _settingsSearchCancellation;
    private readonly Dictionary<string, List<SettingsSearchResult>> _settingsSearchCache = new();
    private int _settingsSearchSelectedIndex;

    private static readonly string[][] SettingsSearchSynonyms =
    [
        ["同步", "sync", "synchronization", "cloud", "云端", "设备同步"],
        ["字体", "font", "typeface", "字号", "字形"],
        ["邮箱", "email", "mail", "邮件", "smtp", "收件地址"],
        ["人工智能", "ai", "模型", "问答", "密钥", "api"],
        ["外观", "theme", "主题"],
        ["kindle", "推送", "发送", "send to kindle", "usb"],
        ["备份", "backup", "恢复", "restore", "导出备份"],
        ["更新", "update", "upgrade", "检查更新", "软件版本"],
        ["代理", "proxy", "代理服务器", "google proxy"],
        ["快捷键", "shortcut", "keyboard shortcut", "热键"],
        ["calibre", "格式转换", "电子书转换", "converter"],
        ["目录", "directory", "folder", "存储路径", "数据路径"],
        ["诊断", "diagnostics", "日志", "log export"],
        ["高级", "advanced", "mcp", "集成", "integration"],
        ["账户", "account", "账号", "登录"]
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> SettingsLocalizedSearchTerms =
        new(BuildSettingsLocalizedSearchTerms);

    private sealed record KindleEmailProviderPreset(string Id, string SmtpHost, int SmtpPort, bool EnableSsl);

    private static readonly KindleEmailProviderPreset[] KindleEmailProviderPresets =
    [
        new("gmail", "smtp.gmail.com", 587, true),
        new("qq", "smtp.qq.com", 587, true),
        // 163 exposes STARTTLS on port 25. Its 587 endpoint is not
        // consistently available and closes the connection before the SMTP
        // greeting on some networks.
        new("163", "smtp.163.com", 25, true),
        new("outlook", "smtp-mail.outlook.com", 587, true)
    ];

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
        => await OpenApplicationSettingsAsync();

    private async Task OpenApplicationSettingsAsync()
    {
        if (BlockNavigationWhileTransferring()) return;
        if (ReaderRoot.IsVisible) await CloseReaderAsync();
        if (ReaderRoot.IsVisible) return;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        SettingsDataPathText.Text = _paths.Data;
        InitializeMcpSettings();
        // Re-scan the plugins directory: a plugin updated on disk (or unpacked
        // by hand) should show its new version and pages without restarting.
        RefreshPluginContributions();
        ShowSettingsSection(_activeSettingsCategory);
    }
}
