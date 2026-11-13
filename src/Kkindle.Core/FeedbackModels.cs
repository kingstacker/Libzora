using System.Text.Json;

namespace Kkindle.Core;

/// <summary>Wire values shared by every client. The server accepts only these values.</summary>
public static class FeedbackTypes
{
    public const string Bug = "bug";
    public const string Feature = "feature";
    public const string Sync = "sync";
    public const string Other = "other";

    public static bool IsValid(string? value) => value is Bug or Feature or Sync or Other;

    public static string DisplayName(string value) => value switch
    {
        Bug => "问题 Bug",
        Feature => "功能建议",
        Sync => "同步问题",
        Other => "其他问题",
        _ => "问题反馈"
    };

    public static IReadOnlyList<(string Value, string Name)> All { get; } =
        [(Bug, "问题 Bug"), (Feature, "功能建议"), (Sync, "同步问题"), (Other, "其他问题")];
}

public static class FeedbackStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Resolved = "resolved";

    public static bool IsValid(string? value) => value is Pending or Processing or Resolved;

    public static string DisplayName(string value) => value switch
    {
        Pending => "待处理",
        Processing => "处理中",
        Resolved => "已解决",
        _ => value
    };
}

public static class FeedbackLimits
{
    public const int MaxAttachments = 3;
    public const long MaxAttachmentBytes = 4L * 1024 * 1024;
    public const long MaxTotalAttachmentBytes = 10L * 1024 * 1024;
    public const int MinContentLength = 5;
    public const int MaxContentLength = 4000;
    public const int MaxContactLength = 100;
    public const int MaxAppVersionLength = 32;
    public const int MaxPlatformLength = 16;
    public const int MaxPlatformVersionLength = 64;
    public const int MaxDeviceModelLength = 128;
    public const int MaxSoftwareLogBytes = 256 * 1024;
    public const int MaxAdminReplyLength = 4000;
    public const int PerUserWindowMinutes = 10;
    public const int PerUserWindowLimit = 5;
    public const int PerUserDayLimit = 20;
}

/// <summary>The form values sent with a feedback submission. The server always derives the owner from the session.</summary>
public sealed class FeedbackDraft
{
    public Guid RequestId { get; init; }
    public string Type { get; init; } = "";
    public string Content { get; init; } = "";
    public string? Contact { get; init; }
    public string AppVersion { get; init; } = "";
    public string Platform { get; init; } = "";
    public string PlatformVersion { get; init; } = "";
    public string DeviceModel { get; init; } = "";
}

/// <summary>One screenshot held only in memory until the multipart request completes.</summary>
public sealed record FeedbackImage(string FileName, string ContentType, byte[] Bytes);

public sealed record FeedbackReceipt(Guid Id, long Number, string Status, DateTimeOffset CreatedAt);

public sealed record FeedbackItem(Guid Id, long Number, string Type, string Content, string? Contact,
    string AppVersion, string Platform, string PlatformVersion, string DeviceModel, string Status,
    string? AdminReply, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int AttachmentCount,
    bool HasSoftwareLog = false);

public static class FeedbackValidation
{
    public static string? ValidateDraft(FeedbackDraft draft)
    {
        if (draft.RequestId == Guid.Empty) return "反馈请求标识无效。";
        if (!FeedbackTypes.IsValid(draft.Type)) return "请选择有效的反馈类型。";
        var content = draft.Content?.Trim() ?? "";
        if (content.Length < FeedbackLimits.MinContentLength) return $"问题描述至少需要 {FeedbackLimits.MinContentLength} 个字。";
        if (content.Length > FeedbackLimits.MaxContentLength) return $"问题描述不能超过 {FeedbackLimits.MaxContentLength} 个字。";
        if (draft.Contact?.Trim() is { Length: > 0 } contact && !IsContact(contact)) return "联系方式请填写 QQ 号或有效邮箱。";
        if (!IsBounded(draft.AppVersion, FeedbackLimits.MaxAppVersionLength, required: true)) return "App 版本信息无效。";
        if (!IsBounded(draft.Platform, FeedbackLimits.MaxPlatformLength, required: true)) return "客户端平台信息无效。";
        if (!IsBounded(draft.PlatformVersion, FeedbackLimits.MaxPlatformVersionLength, required: true)) return "系统版本信息无效。";
        if (!IsBounded(draft.DeviceModel, FeedbackLimits.MaxDeviceModelLength, required: true)) return "设备型号信息无效。";
        return null;
    }

    public static string? ValidateImages(IReadOnlyList<FeedbackImage> images)
    {
        if (images.Count > FeedbackLimits.MaxAttachments) return $"最多只能添加 {FeedbackLimits.MaxAttachments} 张截图。";
        long total = 0;
        foreach (var image in images)
        {
            if (image.Bytes.Length == 0) return "截图内容为空。";
            if (image.Bytes.LongLength > FeedbackLimits.MaxAttachmentBytes)
                return $"每张截图不能超过 {FeedbackLimits.MaxAttachmentBytes / 1024 / 1024} MB。";
            if (DetectImageType(image.Bytes) is null) return "截图格式无效，仅支持 JPEG、PNG 或 WebP。";
            total += image.Bytes.LongLength;
        }
        if (total > FeedbackLimits.MaxTotalAttachmentBytes)
            return $"截图总大小不能超过 {FeedbackLimits.MaxTotalAttachmentBytes / 1024 / 1024} MB。";
        return null;
    }

    /// <summary>
    /// The optional software log is plain, client-sanitized UTF-8 text. The
    /// server re-validates only its shape and size; it never interprets it.
    /// </summary>
    public static string? ValidateSoftwareLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('\0')) return "软件日志内容无效。";
        foreach (var character in value)
        {
            if (char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
                return "软件日志内容无效。";
        }
        if (System.Text.Encoding.UTF8.GetByteCount(value) > FeedbackLimits.MaxSoftwareLogBytes)
            return $"软件日志不能超过 {FeedbackLimits.MaxSoftwareLogBytes / 1024} KB。";
        return null;
    }

    public static bool IsContact(string value) =>
        AccountValidation.IsEmail(value) || value.Length is >= 5 and <= 12 && value.All(char.IsAsciiDigit);

    /// <summary>Returns the canonical content type when the bytes start with a supported image signature.</summary>
    public static string? DetectImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    public static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType))
    };

    private static bool IsBounded(string? value, int maximum, bool required)
    {
        var text = value?.Trim() ?? "";
        return (!required || text.Length > 0) && text.Length <= maximum && !text.Any(char.IsControl);
    }
}

public static class FeedbackJson
{
    public static string SerializeKeys(IEnumerable<string> keys) => JsonSerializer.Serialize(keys.ToArray());

    public static string[] DeserializeKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
