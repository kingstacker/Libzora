using System.Globalization;

namespace Libzora.Core;

/// <summary>
/// Keeps the voice picker manageable. A Mac can expose close to two hundred
/// system voices, and installing both Kokoro packages adds two hundred speaker
/// entries, so the raw list is not a useful default. The concise list contains
/// a few representative voices; the full list remains one explicit opt-in away.
/// </summary>
public static class TtsVoicePicker
{
    private static readonly HashSet<string> NoveltySystemVoices =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Albert", "Bad News", "Bahh", "Bells", "Boing", "Bubbles",
            "Cellos", "Deranged", "Good News", "Hysterical", "Jester",
            "Organ", "Pipe Organ", "Superstar", "Trinoids", "Whisper",
            "Wobble", "Zarvox",
        };

    private static readonly string[] PreferredChineseSystemVoices =
        ["Tingting", "Meijia", "Sinji", "Yu-shu"];

    private static readonly string[] PreferredEnglishSystemVoices =
        ["Samantha", "Allison", "Ava", "Susan", "Alex"];

    public static bool IsNoveltySystemVoice(TtsVoiceInfo voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (voice.Id.Contains(':', StringComparison.Ordinal)) return false;
        return NoveltySystemVoices.Contains(BaseName(voice.Name));
    }

    /// <summary>
    /// Returns the voices shown in the picker. The selected voice is always
    /// preserved so changing the display mode cannot silently switch a user's
    /// saved voice. Novelty sound-effect voices are never offered as reading
    /// voices, even in the full list.
    /// </summary>
    public static IReadOnlyList<TtsVoiceInfo> Select(
        IReadOnlyList<TtsVoiceInfo> voices,
        bool showAll,
        string? selectedVoice = null)
    {
        ArgumentNullException.ThrowIfNull(voices);
        var usable = voices
            .Where(voice => voice is not null && !string.IsNullOrWhiteSpace(voice.Id))
            .Where(voice => !IsNoveltySystemVoice(voice))
            .ToArray();
        var visible = showAll
            ? SortForFullList(usable)
            : BuildRecommended(usable);

        var selected = voices.FirstOrDefault(voice => voice is not null
            && string.Equals(voice.Id, selectedVoice, StringComparison.OrdinalIgnoreCase));
        if (selected is not null
            && !visible.Any(voice =>
                string.Equals(voice.Id, selected.Id, StringComparison.OrdinalIgnoreCase)))
        {
            visible.Insert(0, selected);
        }
        return visible;
    }

    private static List<TtsVoiceInfo> BuildRecommended(
        IReadOnlyList<TtsVoiceInfo> voices)
    {
        var recommended = new List<TtsVoiceInfo>();
        // Prefer the smaller int8 package when both Kokoro variants are
        // installed; the same speaker in fp32 is noise in a short list.
        var kokoroPrefix = voices.Any(voice =>
                voice.Id.StartsWith("kokoro-int8:", StringComparison.OrdinalIgnoreCase))
            ? "kokoro-int8:"
            : voices.Any(voice =>
                voice.Id.StartsWith("kokoro-fp32:", StringComparison.OrdinalIgnoreCase))
                ? "kokoro-fp32:"
                : null;
        if (kokoroPrefix is not null)
        {
            AddVoice(recommended, voices, kokoroPrefix + "3");   // zf_001
            AddVoice(recommended, voices, kokoroPrefix + "58");  // zm_009
            AddVoice(recommended, voices, kokoroPrefix + "0");   // af_maple
        }

        var melo = voices.FirstOrDefault(voice =>
            voice.Id.StartsWith("melo-zh-en:", StringComparison.OrdinalIgnoreCase));
        if (melo is not null) recommended.Add(melo);

        // Keep the built-in system voices visible even when a downloaded pack
        // is installed: some users prefer the macOS voice, and hiding it made
        // the picker look as if system TTS had disappeared.
        AddSystemVoice(recommended, voices, "zh", PreferredChineseSystemVoices);
        AddSystemVoice(recommended, voices, "en-US", PreferredEnglishSystemVoices);
        return recommended;
    }

    private static List<TtsVoiceInfo> SortForFullList(
        IReadOnlyList<TtsVoiceInfo> voices)
    {
        var indexed = voices
            .Select((voice, index) => (Voice: voice, Index: index))
            .ToList();
        indexed.Sort((left, right) =>
        {
            var category = Category(left.Voice).CompareTo(Category(right.Voice));
            if (category != 0) return category;
            if (TrySpeakerId(left.Voice.Id, out var leftId)
                && TrySpeakerId(right.Voice.Id, out var rightId)
                && leftId != rightId)
            {
                return leftId.CompareTo(rightId);
            }
            var byName = StringComparer.CurrentCultureIgnoreCase.Compare(
                left.Voice.Name,
                right.Voice.Name);
            return byName != 0 ? byName : left.Index.CompareTo(right.Index);
        });
        return indexed.Select(item => item.Voice).ToList();
    }

    private static int Category(TtsVoiceInfo voice)
    {
        var chinese = voice.Culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;
        if (voice.Id.StartsWith("kokoro-int8:", StringComparison.OrdinalIgnoreCase))
            return chinese ? 0 : 1;
        if (voice.Id.StartsWith("kokoro-fp32:", StringComparison.OrdinalIgnoreCase))
            return chinese ? 2 : 3;
        if (voice.Id.StartsWith("melo-", StringComparison.OrdinalIgnoreCase)) return 4;
        if (chinese) return 5;
        if (voice.Culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            return 6;
        return 7;
    }

    private static bool TrySpeakerId(string id, out int speakerId)
    {
        speakerId = 0;
        var separator = id.LastIndexOf(':');
        return separator >= 0
            && int.TryParse(
                id.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out speakerId);
    }

    private static void AddVoice(
        List<TtsVoiceInfo> target,
        IReadOnlyList<TtsVoiceInfo> voices,
        string id)
    {
        var voice = voices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (voice is not null) target.Add(voice);
    }

    private static void AddSystemVoice(
        List<TtsVoiceInfo> target,
        IReadOnlyList<TtsVoiceInfo> voices,
        string culturePrefix,
        IReadOnlyList<string> preferredNames)
    {
        var candidates = voices.Where(voice =>
                !voice.Id.Contains(':', StringComparison.Ordinal)
                && voice.Culture?.StartsWith(culturePrefix, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        if (candidates.Length == 0) return;
        var preferred = preferredNames
            .Select(name => candidates.FirstOrDefault(voice =>
                string.Equals(BaseName(voice.Name), name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(voice => voice is not null);
        target.Add(preferred ?? candidates[0]);
    }

    private static string BaseName(string name)
    {
        var index = name.IndexOf(" (", StringComparison.Ordinal);
        return (index > 0 ? name[..index] : name).Trim();
    }
}
