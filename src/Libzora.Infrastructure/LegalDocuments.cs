using System.Net;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

public enum LegalDocumentKind
{
    UserAgreement,
    PrivacyPolicy,
}

public sealed record LegalDocumentBlock(string Text, bool IsHeading = false);

public sealed record LegalDocumentContent(
    string Title,
    string EffectiveDate,
    string Summary,
    IReadOnlyList<LegalDocumentBlock> Blocks);

/// <summary>Reads the same legal articles the website serves, from local assembly resources.</summary>
public static class LegalDocuments
{
    public static LegalDocumentContent GetDocument(LegalDocumentKind kind, bool? isEnglish = null)
    {
        var page = kind switch
        {
            LegalDocumentKind.UserAgreement => "terms",
            LegalDocumentKind.PrivacyPolicy => "privacy",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        var language = (isEnglish ?? !UiText.IsChinese) ? "en" : "zh";
        using var stream = typeof(LegalDocuments).Assembly.GetManifestResourceStream($"Libzora.Legal.{page}.html")
            ?? throw new InvalidOperationException($"Missing bundled legal page: {page}.");
        var html = new HtmlDocument();
        html.Load(stream);
        var article = html.DocumentNode.SelectSingleNode($"//article[@data-legal='{language}']")
            ?? throw new InvalidOperationException($"Missing legal article: {page}/{language}.");
        var blocks = new List<LegalDocumentBlock>();
        foreach (var node in article.SelectNodes("./h2 | ./p[not(@class='legal-meta')] | ./ul/li"))
        {
            var text = ReadText(node);
            blocks.Add(new LegalDocumentBlock(node.Name == "li" ? "• " + text : text, node.Name == "h2"));
        }

        return new LegalDocumentContent(
            ReadText(article.SelectSingleNode("./h1")),
            ReadText(article.SelectSingleNode("./p[@class='legal-meta']")),
            ReadText(article.SelectSingleNode("./div[@class='legal-summary']")),
            blocks.AsReadOnly());
    }

    private static string ReadText(HtmlNode node) => WebUtility.HtmlDecode(node.InnerText).Trim();
}
