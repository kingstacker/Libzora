using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile;
using Libzora.Mobile.Views;

namespace Libzora.iOSHead;

internal sealed record NativeMaterial(string Id, Guid BookId, string BookTitle, string Kind, string Quote,
    string Note, string LocationText, DateTimeOffset UpdatedAt, ReaderProgressRow Location,
    string? HighlightColor = null, string? UnderlineStyle = null)
{
    /// <summary>Location written to exports. The list shows the shorter <see cref="LocationText"/>.</summary>
    [JsonIgnore] public string ExportLocation { get; init; } = LocationText;
}

internal sealed record NativeBookReflection(
    Guid BookId,
    string Content,
    string PreviewMarkdown,
    string PreviewText,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool HasContent);

internal sealed record NativeReflectionCitation(
    Guid Id,
    Guid BookId,
    string BookTitle,
    string ChapterLabel,
    string SelectedText,
    string Note,
    DateTimeOffset UpdatedAt,
    string DisplayText);

internal sealed record ReaderFontOption(
    string Key,
    string Name,
    string Detail,
    string Kind,
    bool Installed,
    long Bytes,
    string? License,
    string PreviewName,
    string? Path);

internal sealed record ReaderFontList(string Current, ReaderFontOption[] Fonts);

internal sealed record ReaderFontInstall(string Key, string Path, string PreviewName);

internal sealed record NativeAiSource(
    string Id,
    string Label,
    string Content,
    bool IsPdf,
    int ChapterIndex,
    int Offset,
    int PdfPage);

internal sealed record NativeAiResult(
    string Text,
    string Reasoning,
    string Status,
    NativeAiSource[] Sources);

internal sealed record NativeAiInfo(
    bool Configured,
    string Provider,
    string Model);

/// <summary>
/// The native bridge uses an explicit metadata registry. Device AOT builds do
/// not need reflection to discover anonymous response types or settings fields.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(MobilePreferences))]
[JsonSerializable(typeof(LegalDocumentContent))]
[JsonSerializable(typeof(ReaderFontList))]
[JsonSerializable(typeof(ReaderFontInstall))]
[JsonSerializable(typeof(ReaderProgressRow))]
[JsonSerializable(typeof(NativeReaderState))]
[JsonSerializable(typeof(NativeReaderDialog))]
[JsonSerializable(typeof(NativeReaderMedia))]
[JsonSerializable(typeof(NativeReaderFootnote))]
[JsonSerializable(typeof(NativeReaderTocItem[]))]
[JsonSerializable(typeof(NativeReaderSearchItem[]))]
[JsonSerializable(typeof(NativeReaderSearchBatch))]
[JsonSerializable(typeof(NativeMaterial[]))]
[JsonSerializable(typeof(NativeBookReflection))]
[JsonSerializable(typeof(NativeReflectionCitation))]
[JsonSerializable(typeof(NativeReflectionCitation[]))]
[JsonSerializable(typeof(BookCollection))]
[JsonSerializable(typeof(LibraryFilterOptions))]
[JsonSerializable(typeof(ReadingDashboard))]
[JsonSerializable(typeof(ReadingPeriodBucket[]))]
[JsonSerializable(typeof(ReadingMedal[]))]
[JsonSerializable(typeof(AccountStatus))]
[JsonSerializable(typeof(DeviceFeatureStatus))]
[JsonSerializable(typeof(S3SyncSettings))]
[JsonSerializable(typeof(AiConnectionSettings))]
[JsonSerializable(typeof(List<AiConversationTurn>))]
[JsonSerializable(typeof(NativeAiSource))]
[JsonSerializable(typeof(NativeAiSource[]))]
[JsonSerializable(typeof(NativeAiResult))]
[JsonSerializable(typeof(NativeAiInfo))]
[JsonSerializable(typeof(KindleEmailSettings))]
[JsonSerializable(typeof(TtsSettings))]
[JsonSerializable(typeof(TtsVoiceInfo[]))]
[JsonSerializable(typeof(DictionaryDefinition[]))]
[JsonSerializable(typeof(DictionaryEntry[]))]
[JsonSerializable(typeof(DictionaryHtmlPalette))]
[JsonSerializable(typeof(WikipediaArticle))]
[JsonSerializable(typeof(WikipediaArticle[]))]
[JsonSerializable(typeof(TxtImportRules))]
[JsonSerializable(typeof(TxtChapterPreviewItem[]))]
internal partial class IOSNativeJsonContext : JsonSerializerContext;

internal static class IOSNativeJson
{
    internal static Dictionary<string, object?> Data(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(field => field.Key, field => field.Value);

    internal static string Serialize(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), IOSNativeJsonContext.Default);

    internal static T Read<T>(JsonElement value, T defaults, JsonTypeInfo<T> metadata) where T : class
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a native settings object.");
        // The generated constructor for init-only records supplies zero/null
        // for missing fields. Overlay the form onto the actual service defaults
        // before deserializing, including fields an older frontend doesn't send.
        var baseline = JsonSerializer.SerializeToElement(defaults, metadata);
        var supplied = value.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in baseline.EnumerateObject())
                if (!supplied.Contains(property.Name)) property.WriteTo(writer);
            foreach (var property in value.EnumerateObject()) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return JsonSerializer.Deserialize(output.ToArray(), metadata) ?? throw new JsonException("Invalid native settings object.");
    }
}
