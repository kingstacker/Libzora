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
    private static readonly FrozenDictionary<string, string> English = Load();

    public static string? Translate(string source) => English.GetValueOrDefault(source);

    private static FrozenDictionary<string, string> Load()
    {
        var assembly = typeof(MobileUiTextCatalog).Assembly;
        const string prefix = "Libzora.Mobile.Localization.";
        var chinese = ReadShared("zh-CN");
        var english = ReadShared("en-US");
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, source) in chinese.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            if (english.TryGetValue(key, out var translated)) translations.TryAdd(source, translated);

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                         && name.EndsWith(".en-US.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing mobile UI resource: {name}");
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateObject())
                if (entry.Value.GetString() is { Length: > 0 } value) translations[entry.Name] = value;
        }
        return translations.ToFrozenDictionary(StringComparer.Ordinal);

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
