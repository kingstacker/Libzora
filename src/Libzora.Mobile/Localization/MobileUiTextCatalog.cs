using System.Collections.Frozen;
using System.Text.Json;
using System.Xml.Linq;

namespace Libzora.Mobile.Localization;

/// <summary>
/// Immutable, thread-safe UI catalog. Reuses both named and automatic desktop
/// entries, with mobile-specific translations taking precedence. It never
/// depends on an Avalonia resource dictionary owned by the UI thread.
/// </summary>
public static class MobileUiTextCatalog
{
    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Catalogs = Load();

    public static string? Translate(string source) => Translate(source, MobileUiLanguageService.English);

    public static string? Translate(string source, string language)
    {
        if (string.Equals(language, MobileUiLanguageService.Chinese, StringComparison.Ordinal))
            return null;
        if (Catalogs.TryGetValue(language, out var catalog) && catalog.TryGetValue(source, out var value))
            return value;
        return Catalogs.TryGetValue(MobileUiLanguageService.English, out var english)
            ? english.GetValueOrDefault(source)
            : null;
    }

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(MobileUiTextCatalog).Assembly;
        const string prefix = "Libzora.Mobile.Localization.";
        var chinese = ReadShared("zh-CN");
        var english = ReadShared("en-US");
        var sharedEnglish = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, source) in chinese.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            if (english.TryGetValue(key, out var translated)) sharedEnglish.TryAdd(source, translated);

        var perLanguage = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            [MobileUiLanguageService.English] = new Dictionary<string, string>(sharedEnglish, StringComparer.Ordinal)
        };

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        foreach (var name in resourceNames)
        {
            var matchedLanguage = MobileUiLanguageService.SupportedLanguages
                .FirstOrDefault(lang => !string.Equals(lang, MobileUiLanguageService.Chinese, StringComparison.Ordinal)
                    && name.EndsWith("." + lang + ".json", StringComparison.Ordinal));
            if (matchedLanguage is null) continue;

            if (!perLanguage.TryGetValue(matchedLanguage, out var translations))
            {
                translations = new Dictionary<string, string>(StringComparer.Ordinal);
                perLanguage[matchedLanguage] = translations;
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing mobile UI resource: {name}");
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateObject())
                if (entry.Value.GetString() is { Length: > 0 } value) translations[entry.Name] = value;
        }

        return perLanguage.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenDictionary(StringComparer.Ordinal),
            StringComparer.Ordinal);

        Dictionary<string, string> ReadShared(string language)
        {
            using var stream = assembly.GetManifestResourceStream(prefix + "Shared." + language + ".axaml")
                ?? throw new InvalidOperationException($"Missing shared UI resource: {language}");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            return XDocument.Load(stream).Root!.Elements(xaml + "String")
                .ToDictionary(element => (string)element.Attribute(xaml + "Key")!,
                    element => element.Value, StringComparer.Ordinal);
        }
    }
}
