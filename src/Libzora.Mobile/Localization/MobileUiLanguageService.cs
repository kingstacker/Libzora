using Libzora.Core;

namespace Libzora.Mobile.Localization;

/// <summary>
/// Keeps the saved choice separate from its effective language. The platform
/// provider reads OS preferences, not a culture changed by the application.
/// </summary>
public sealed class MobileUiLanguageService
{
    public const string System = "auto";
    public const string Chinese = "zh-CN";
    public const string TraditionalChinese = "zh-Hant";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";
    public const string German = "de-DE";
    public const string French = "fr-FR";
    public const string Korean = "ko-KR";
    public const string Spanish = "es-ES";
    public const string Italian = "it-IT";
    public const string Portuguese = "pt-BR";

    public static readonly IReadOnlyList<string> SupportedLanguages =
    [
        Chinese, TraditionalChinese, English, Japanese, German,
        French, Korean, Spanish, Italian, Portuguese
    ];

    private readonly MobilePreferencesStore _preferences;
    private readonly Func<IReadOnlyList<string>> _preferredLanguages;

    public MobileUiLanguageService(MobilePreferencesStore preferences,
        Func<IReadOnlyList<string>> preferredLanguages)
    {
        _preferences = preferences;
        _preferredLanguages = preferredLanguages;
        UiText.Configure(ResolveText, ResolveLanguage(preferences.Current.UiLanguage, preferredLanguages()), normalize: false);
    }

    public string Language => UiText.CurrentLanguage;

    /// <summary>Re-evaluate on a preference change or a return to the foreground.</summary>
    public bool Refresh()
    {
        var language = ResolveLanguage(_preferences.Current.UiLanguage, _preferredLanguages());
        var changed = !string.Equals(Language, language, StringComparison.Ordinal);
        UiText.SetLanguage(language, normalize: false);
        return changed;
    }

    public static string NormalizePreference(string? preference)
    {
        var language = preference?.Trim().Replace('_', '-');
        if (string.IsNullOrEmpty(language)) return System;
        if (MatchesTraditionalChinese(language)) return TraditionalChinese;
        if (Matches(language, "zh")) return Chinese;
        if (Matches(language, "en")) return English;
        if (Matches(language, "ja")) return Japanese;
        if (Matches(language, "de")) return German;
        if (Matches(language, "fr")) return French;
        if (Matches(language, "ko")) return Korean;
        if (Matches(language, "es")) return Spanish;
        if (Matches(language, "it")) return Italian;
        if (Matches(language, "pt")) return Portuguese;
        return System;
    }

    public static string ResolveLanguage(string? preference, IEnumerable<string> preferredLanguages)
    {
        var choice = NormalizePreference(preference);
        if (choice != System) return choice;
        foreach (var language in preferredLanguages)
        {
            var supported = NormalizePreference(language);
            if (supported != System) return supported;
        }
        return English;
    }

    private static bool MatchesTraditionalChinese(string language) =>
        Matches(language, "zh-Hant")
        || Matches(language, "zh-TW")
        || Matches(language, "zh-HK")
        || Matches(language, "zh-MO");

    private static bool Matches(string? language, string prefix) =>
        string.Equals(language, prefix, StringComparison.OrdinalIgnoreCase)
        || language?.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ResolveText(string source) =>
        MobileUiTextCatalog.Translate(source, UiText.CurrentLanguage);
}
