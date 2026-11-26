using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Mobile.Views;

/// <summary>
/// First-launch welcome guide for Avalonia mobile shells (Android / desktop
/// mobile preview) requiring acceptance of the bundled User Agreement and
/// Privacy Policy.
/// </summary>
public partial class WelcomeOverlay : UserControl
{
    private readonly Action? _onAccepted;

    /// <summary>Design-time only.</summary>
    public WelcomeOverlay() : this(null) { }

    public WelcomeOverlay(Action? onAccepted)
    {
        _onAccepted = onAccepted;
        InitializeComponent();
        ApplyLocalizedCopy();
    }

    private void ApplyLocalizedCopy()
    {
        WelcomeTitleText.Text = UiText.Get("欢迎使用 Libzora");
        WelcomeSubtitleText.Text = UiText.Get("静心阅读，数据归己。");
        Feature1Title.Text = UiText.Get("精排离线阅读");
        Feature1Detail.Text = UiText.Get("支持 EPUB、PDF、漫画等常见电子书格式，专注舒适排版与沉浸阅读体验。");
        Feature2Title.Text = UiText.Get("批注、统计与同步");
        Feature2Detail.Text = UiText.Get(OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()
            ? "统一管理划线笔记、读后思考与阅读数据，支持 iCloud 与自备 S3 / WebDAV 端到端加密同步。"
            : "统一管理划线笔记、读后思考与阅读数据，支持自备 S3 / WebDAV 端到端加密同步。");
        Feature3Title.Text = UiText.Get("隐私优先");
        Feature3Detail.Text = UiText.Get("书库与同步密钥仅保存在你的设备中，无广告、无第三方追踪，离线即可使用。");
        AgreeText.Text = UiText.Get("我已阅读并同意《用户协议》和《隐私政策》，并确认仅导入本人拥有合法权利的电子书");
        TermsButton.Content = UiText.Get("阅读用户协议");
        PrivacyButton.Content = UiText.Get("阅读隐私政策");
        AcceptButton.Content = UiText.Get("同意并开始使用");
        ConsentHintText.Text = UiText.Get("需勾选同意用户协议与隐私政策后方可继续使用");
        LegalCloseButton.Content = UiText.Get("完成");
    }

    private void OnAgreeChanged(object? sender, RoutedEventArgs e)
    {
        var agreed = AgreeCheckBox.IsChecked == true;
        AcceptButton.IsEnabled = agreed;
        ConsentHintText.Opacity = agreed ? 0 : 1;
    }

    private void OnTermsClick(object? sender, RoutedEventArgs e) =>
        ShowLegalDocument(LegalDocumentKind.UserAgreement);

    private void OnPrivacyClick(object? sender, RoutedEventArgs e) =>
        ShowLegalDocument(LegalDocumentKind.PrivacyPolicy);

    private void ShowLegalDocument(LegalDocumentKind kind)
    {
        var document = LegalDocuments.GetDocument(kind);
        LegalTitleText.Text = document.Title;
        LegalBlocksPanel.Children.Clear();
        LegalBlocksPanel.Children.Add(new TextBlock
        {
            Text = document.EffectiveDate,
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap
        });
        LegalBlocksPanel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Background = this.FindResource("SurfaceBrush") as IBrush,
            Child = new TextBlock
            {
                Text = document.Summary,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            }
        });
        foreach (var block in document.Blocks)
        {
            LegalBlocksPanel.Children.Add(new TextBlock
            {
                Text = block.Text,
                FontSize = block.IsHeading ? 16 : 14,
                FontWeight = block.IsHeading ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = block.IsHeading ? new Thickness(0, 8, 0, 0) : default,
                TextWrapping = TextWrapping.Wrap
            });
        }
        WelcomeContent.IsVisible = false;
        LegalViewer.IsVisible = true;
    }

    private void OnCloseLegalClick(object? sender, RoutedEventArgs e)
    {
        LegalViewer.IsVisible = false;
        WelcomeContent.IsVisible = true;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (AgreeCheckBox.IsChecked != true) return;
        _onAccepted?.Invoke();
    }
}
