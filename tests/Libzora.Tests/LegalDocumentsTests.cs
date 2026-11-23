using Libzora.Infrastructure;
using Xunit;

namespace Libzora.Tests;

public sealed class LegalDocumentsTests
{
    [Theory]
    [InlineData(LegalDocumentKind.UserAgreement, false, "用户协议", "一、协议的接受", "十三、联系我们")]
    [InlineData(LegalDocumentKind.UserAgreement, true, "Terms of Service", "1. Acceptance", "13. Contact")]
    [InlineData(LegalDocumentKind.PrivacyPolicy, false, "隐私政策", "一、适用范围", "十二、联系我们")]
    [InlineData(LegalDocumentKind.PrivacyPolicy, true, "Privacy Policy", "1. Scope", "12. Contact")]
    public void BundledArticleHasSummaryAndReadableBody(
        LegalDocumentKind kind, bool english, string title, string firstHeading, string lastHeading)
    {
        var document = LegalDocuments.GetDocument(kind, english);

        Assert.Equal(title, document.Title);
        Assert.False(string.IsNullOrWhiteSpace(document.EffectiveDate));
        Assert.Contains("Libzora", document.Summary);
        Assert.Equal(new LegalDocumentBlock(firstHeading, IsHeading: true), document.Blocks[0]);
        Assert.Equal(lastHeading, document.Blocks.Last(block => block.IsHeading).Text);
        Assert.Contains(document.Blocks, block => block.Text.StartsWith("• ", StringComparison.Ordinal));
        Assert.Contains(document.Blocks, block => block.Text.Contains("GitHub Issues", StringComparison.Ordinal));
    }
}
