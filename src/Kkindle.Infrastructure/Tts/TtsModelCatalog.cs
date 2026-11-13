namespace Kkindle.Infrastructure;

/// <summary>
/// One optional offline TTS model. Downloads intentionally have a single
/// GitHub Release source: there is no mirror fallback, and the SHA-256 value
/// is the pin that makes a mutable release asset safe to install.
/// </summary>
public sealed record TtsModelPackage(
    string Id,
    string EngineId,
    string DisplayName,
    string Description,
    string ArchiveFileName,
    string DownloadUrl,
    long ArchiveBytes,
    string Sha256,
    string LicenseName,
    string LicenseUrl,
    IReadOnlyList<string> RequiredEntries)
{
    public Uri DownloadUri => new(DownloadUrl);

    public string EstimatedSizeText => TtsModelCatalog.FormatBytes(ArchiveBytes);
}

public static class TtsModelCatalog
{
    private const string ReleaseBase =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/";

    // Assets live under the rolling `tts-models` release tag. The tag may gain
    // new assets, so every package pins its file name, byte count and SHA-256.
    public static IReadOnlyList<TtsModelPackage> Packages { get; } =
    [
        new(
            Id: "kokoro-int8",
            EngineId: "sherpa-kokoro",
            DisplayName: "Kokoro v1.1-zh · int8",
            Description: "推荐。中英混读，103 个音色，下载约 140 MB。",
            ArchiveFileName: "kokoro-int8-multi-lang-v1_1.tar.bz2",
            DownloadUrl: ReleaseBase + "kokoro-int8-multi-lang-v1_1.tar.bz2",
            ArchiveBytes: 147_052_523,
            Sha256: "259C9C8BC906D6078E4E0570421885DABB68B539C2F970FCBB890A58EEAF3E9C",
            LicenseName: "Apache-2.0",
            LicenseUrl: "https://github.com/hexgrad/kokoro/blob/main/LICENSE",
            RequiredEntries:
            [
                "model.int8.onnx",
                "voices.bin",
                "tokens.txt",
                "lexicon-us-en.txt",
                "lexicon-zh.txt",
                "espeak-ng-data",
            ]),
        new(
            Id: "kokoro-fp32",
            EngineId: "sherpa-kokoro",
            DisplayName: "Kokoro v1.1-zh · fp32",
            Description: "中英混读，103 个音色，下载约 348 MB；音质优先。",
            ArchiveFileName: "kokoro-multi-lang-v1_1.tar.bz2",
            DownloadUrl: ReleaseBase + "kokoro-multi-lang-v1_1.tar.bz2",
            ArchiveBytes: 364_855_331,
            Sha256: "B78E1AC42B852E839CCEAAFBE88F66806BF5860992D1D7B9A3F2AD1198427AED",
            LicenseName: "Apache-2.0",
            LicenseUrl: "https://github.com/hexgrad/kokoro/blob/main/LICENSE",
            RequiredEntries:
            [
                "model.onnx",
                "voices.bin",
                "tokens.txt",
                "lexicon-us-en.txt",
                "lexicon-zh.txt",
                "espeak-ng-data",
            ]),
        new(
            Id: "melo-zh-en",
            EngineId: "sherpa-melo",
            DisplayName: "MeloTTS 中文/英文",
            Description: "中英混读，单音色，下载约 159 MB。",
            ArchiveFileName: "vits-melo-tts-zh_en.tar.bz2",
            DownloadUrl: ReleaseBase + "vits-melo-tts-zh_en.tar.bz2",
            ArchiveBytes: 167_006_755,
            Sha256: "E58351ED7149F290A54534538BADD4077CDBE6FDDC964B24D0BEE870415D1514",
            LicenseName: "MIT",
            LicenseUrl: "https://github.com/myshell-ai/MeloTTS/blob/main/LICENSE",
            RequiredEntries:
            [
                "model.onnx",
                "tokens.txt",
                "lexicon.txt",
            ]),
    ];

    public static TtsModelPackage? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var normalized = id.Trim();
        return Packages.FirstOrDefault(package =>
            package.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Voice ids are stored as "&lt;package-id&gt;:&lt;speaker-id&gt;".</summary>
    public static bool TryParseVoiceId(
        string? voiceId,
        out string packageId,
        out int speakerId)
    {
        packageId = string.Empty;
        speakerId = -1;
        if (string.IsNullOrWhiteSpace(voiceId)) return false;
        var separator = voiceId.LastIndexOf(':');
        if (separator <= 0 || separator == voiceId.Length - 1) return false;
        var candidatePackage = voiceId[..separator];
        if (Find(candidatePackage) is null) return false;
        if (!int.TryParse(voiceId[(separator + 1)..], out var sid) || sid < 0)
            return false;
        packageId = candidatePackage;
        speakerId = sid;
        return true;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        const double megabyte = 1024d * 1024d;
        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (megabyte * 1024):0.0} GB"
            : $"{bytes / megabyte:0} MB";
    }
}
