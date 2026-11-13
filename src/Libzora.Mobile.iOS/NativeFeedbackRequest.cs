using System.Text.Json;
using Libzora.Core;

namespace Libzora.iOSHead;

/// <summary>
/// Maps the Swift feedback form onto the shared feedback contract. The account
/// and app version are supplied by the managed side, never by the UI form.
/// </summary>
internal static class NativeFeedbackRequest
{
    // Base64 has no whitespace here; allow a small padding margin.
    private static readonly long MaximumBase64Length =
        (FeedbackLimits.MaxAttachmentBytes + 2) / 3 * 4 + 8;

    public static FeedbackDraft ReadDraft(JsonElement args, string appVersion) => new()
    {
        RequestId = Guid.TryParse(Text(args, "requestId"), out var requestId) ? requestId : Guid.Empty,
        Type = Text(args, "type").Trim(),
        Content = Text(args, "content"),
        Contact = Text(args, "contact").Trim() is { Length: > 0 } contact ? contact : null,
        AppVersion = appVersion,
        Platform = "ios",
        PlatformVersion = Text(args, "platformVersion").Trim(),
        DeviceModel = Text(args, "deviceModel").Trim()
    };

    /// <summary>
    /// The log toggle defaults to on, matching the desktop window. Older
    /// frontends that never send the field therefore keep the default.
    /// </summary>
    public static bool ReadAttachLogs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("attachLogs", out var value))
            return true;
        return value.ValueKind switch
        {
            JsonValueKind.False => false,
            _ => true
        };
    }

    /// <summary>Decodes screenshots held in memory as base64 JPEG/PNG/WebP payloads.</summary>
    public static FeedbackImage[] ReadImages(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("images", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return [];
        if (values.GetArrayLength() > FeedbackLimits.MaxAttachments)
            throw new InvalidOperationException($"最多只能添加 {FeedbackLimits.MaxAttachments} 张截图。");
        var images = new List<FeedbackImage>();
        long total = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("截图内容无效。");
            var text = value.GetString() ?? "";
            if (text.Length > MaximumBase64Length)
                throw new InvalidOperationException(
                    $"每张截图不能超过 {FeedbackLimits.MaxAttachmentBytes / 1024 / 1024} MB。");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(text); }
            catch (FormatException) { throw new InvalidOperationException("截图内容无效。"); }
            if (bytes.LongLength > FeedbackLimits.MaxAttachmentBytes)
                throw new InvalidOperationException(
                    $"每张截图不能超过 {FeedbackLimits.MaxAttachmentBytes / 1024 / 1024} MB。");
            total += bytes.LongLength;
            if (total > FeedbackLimits.MaxTotalAttachmentBytes)
                throw new InvalidOperationException(
                    $"截图总大小不能超过 {FeedbackLimits.MaxTotalAttachmentBytes / 1024 / 1024} MB。");
            var contentType = FeedbackValidation.DetectImageType(bytes)
                ?? throw new InvalidOperationException("截图格式无效，仅支持 JPEG、PNG 或 WebP。");
            images.Add(new FeedbackImage(
                $"screenshot-{images.Count + 1}{FeedbackValidation.ExtensionFor(contentType)}", contentType, bytes));
        }
        return images.ToArray();
    }

    private static string Text(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
}
