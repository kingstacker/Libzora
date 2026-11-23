namespace Libzora.Core;

/// <summary>Public addresses of the user agreement and privacy policy pages.</summary>
public static class LegalDocumentLinks
{
    private const string Site = "https://libzora.stacker.beauty/";

    public static string UserAgreement => Site + "terms.html?lang=" + LanguageCode;

    public static string PrivacyPolicy => Site + "privacy.html?lang=" + LanguageCode;

    private static string LanguageCode => UiText.IsEnglish ? "en" : "zh";
}
