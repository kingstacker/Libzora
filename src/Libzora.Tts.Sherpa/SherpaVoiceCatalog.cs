using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tts.Sherpa;

/// <summary>
/// Speaker metadata for the sherpa-onnx packages used by the desktop app.
/// Speaker ids are stable for Kokoro v1.1-zh; the downloader never rewrites
/// voices.bin, so the ids can be shipped as data instead of being probed.
/// </summary>
public static class SherpaVoiceCatalog
{
    private static readonly (int Sid, string Name, string Culture)[] KokoroVoices =
    [
        (0, "af_maple", "en-US"),
        (1, "af_sol", "en-US"),
        (2, "bf_vale", "en-GB"),
        (3, "zf_001", "zh-CN"),
        (4, "zf_002", "zh-CN"),
        (5, "zf_003", "zh-CN"),
        (6, "zf_004", "zh-CN"),
        (7, "zf_005", "zh-CN"),
        (8, "zf_006", "zh-CN"),
        (9, "zf_007", "zh-CN"),
        (10, "zf_008", "zh-CN"),
        (11, "zf_017", "zh-CN"),
        (12, "zf_018", "zh-CN"),
        (13, "zf_019", "zh-CN"),
        (14, "zf_021", "zh-CN"),
        (15, "zf_022", "zh-CN"),
        (16, "zf_023", "zh-CN"),
        (17, "zf_024", "zh-CN"),
        (18, "zf_026", "zh-CN"),
        (19, "zf_027", "zh-CN"),
        (20, "zf_028", "zh-CN"),
        (21, "zf_032", "zh-CN"),
        (22, "zf_036", "zh-CN"),
        (23, "zf_038", "zh-CN"),
        (24, "zf_039", "zh-CN"),
        (25, "zf_040", "zh-CN"),
        (26, "zf_042", "zh-CN"),
        (27, "zf_043", "zh-CN"),
        (28, "zf_044", "zh-CN"),
        (29, "zf_046", "zh-CN"),
        (30, "zf_047", "zh-CN"),
        (31, "zf_048", "zh-CN"),
        (32, "zf_049", "zh-CN"),
        (33, "zf_051", "zh-CN"),
        (34, "zf_059", "zh-CN"),
        (35, "zf_060", "zh-CN"),
        (36, "zf_067", "zh-CN"),
        (37, "zf_070", "zh-CN"),
        (38, "zf_071", "zh-CN"),
        (39, "zf_072", "zh-CN"),
        (40, "zf_073", "zh-CN"),
        (41, "zf_074", "zh-CN"),
        (42, "zf_075", "zh-CN"),
        (43, "zf_076", "zh-CN"),
        (44, "zf_077", "zh-CN"),
        (45, "zf_078", "zh-CN"),
        (46, "zf_079", "zh-CN"),
        (47, "zf_083", "zh-CN"),
        (48, "zf_084", "zh-CN"),
        (49, "zf_085", "zh-CN"),
        (50, "zf_086", "zh-CN"),
        (51, "zf_087", "zh-CN"),
        (52, "zf_088", "zh-CN"),
        (53, "zf_090", "zh-CN"),
        (54, "zf_092", "zh-CN"),
        (55, "zf_093", "zh-CN"),
        (56, "zf_094", "zh-CN"),
        (57, "zf_099", "zh-CN"),
        (58, "zm_009", "zh-CN"),
        (59, "zm_010", "zh-CN"),
        (60, "zm_011", "zh-CN"),
        (61, "zm_012", "zh-CN"),
        (62, "zm_013", "zh-CN"),
        (63, "zm_014", "zh-CN"),
        (64, "zm_015", "zh-CN"),
        (65, "zm_016", "zh-CN"),
        (66, "zm_020", "zh-CN"),
        (67, "zm_025", "zh-CN"),
        (68, "zm_029", "zh-CN"),
        (69, "zm_030", "zh-CN"),
        (70, "zm_031", "zh-CN"),
        (71, "zm_033", "zh-CN"),
        (72, "zm_034", "zh-CN"),
        (73, "zm_035", "zh-CN"),
        (74, "zm_037", "zh-CN"),
        (75, "zm_041", "zh-CN"),
        (76, "zm_045", "zh-CN"),
        (77, "zm_050", "zh-CN"),
        (78, "zm_052", "zh-CN"),
        (79, "zm_053", "zh-CN"),
        (80, "zm_054", "zh-CN"),
        (81, "zm_055", "zh-CN"),
        (82, "zm_056", "zh-CN"),
        (83, "zm_057", "zh-CN"),
        (84, "zm_058", "zh-CN"),
        (85, "zm_061", "zh-CN"),
        (86, "zm_062", "zh-CN"),
        (87, "zm_063", "zh-CN"),
        (88, "zm_064", "zh-CN"),
        (89, "zm_065", "zh-CN"),
        (90, "zm_066", "zh-CN"),
        (91, "zm_068", "zh-CN"),
        (92, "zm_069", "zh-CN"),
        (93, "zm_080", "zh-CN"),
        (94, "zm_081", "zh-CN"),
        (95, "zm_082", "zh-CN"),
        (96, "zm_089", "zh-CN"),
        (97, "zm_091", "zh-CN"),
        (98, "zm_095", "zh-CN"),
        (99, "zm_096", "zh-CN"),
        (100, "zm_097", "zh-CN"),
        (101, "zm_098", "zh-CN"),
        (102, "zm_100", "zh-CN"),
    ];

    public static IReadOnlyList<TtsVoiceInfo> GetVoices(TtsModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.EngineId.Equals("sherpa-kokoro", StringComparison.Ordinal))
        {
            return KokoroVoices
                .Select(voice => new TtsVoiceInfo(
                    $"{package.Id}:{voice.Sid}",
                    $"{package.DisplayName} · {voice.Name}",
                    voice.Culture))
                .ToArray();
        }
        if (package.EngineId.Equals("sherpa-melo", StringComparison.Ordinal))
        {
            return
            [
                new TtsVoiceInfo(
                    $"{package.Id}:0",
                    $"{package.DisplayName} · 中文女声",
                    "zh-CN"),
            ];
        }
        return [];
    }

    public static bool TryParseVoiceId(
        string? voiceId,
        out string packageId,
        out int speakerId) =>
        TtsModelCatalog.TryParseVoiceId(voiceId, out packageId, out speakerId);
}
