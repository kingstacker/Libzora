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
    public const string English = "en-US";

    private readonly MobilePreferencesStore _preferences;
    private readonly Func<IReadOnlyList<string>> _preferredLanguages;

    public MobileUiLanguageService(MobilePreferencesStore preferences,
        Func<IReadOnlyList<string>> preferredLanguages)
    {
        _preferences = preferences;
        _preferredLanguages = preferredLanguages;
        UiText.Configure(ResolveText, ResolveLanguage(preferences.Current.UiLanguage, preferredLanguages()));
    }

    public string Language => UiText.CurrentLanguage;

    /// <summary>Re-evaluate on a preference change or a return to the foreground.</summary>
    public bool Refresh()
    {
        var language = ResolveLanguage(_preferences.Current.UiLanguage, _preferredLanguages());
        var changed = !string.Equals(Language, language, StringComparison.Ordinal);
        UiText.SetLanguage(language);
        return changed;
    }

    public static string NormalizePreference(string? preference)
    {
        var language = preference?.Trim();
        if (Matches(language, "zh")) return Chinese;
        if (Matches(language, "en")) return English;
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

    private static bool Matches(string? language, string prefix) =>
        string.Equals(language, prefix, StringComparison.OrdinalIgnoreCase)
        || language?.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) == true
        || language?.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ResolveText(string source) => UiText.IsEnglish
        ? MobileUiTextCatalog.Translate(source) : null;
}
