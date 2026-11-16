using System.Globalization;
using System.Text;

namespace Libzora.Core;

/// <summary>
/// A text-based reading estimate, independent of font size and pagination.
/// CJK text uses 500 characters/minute; other text uses 250 words/minute.
/// </summary>
public static class ReaderReadingTimeEstimate
{
    public static double EstimateSeconds(ReadOnlySpan<char> text)
    {
        var characters = 0L;
        var words = 0L;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) && IsCjk(rune.Value))
            {
                characters++;
                inWord = false;
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (!inWord) words++;
                inWord = true;
            }
            else if (rune.Value is not (0x27 or 0x2019)
                && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                inWord = false;
            }
        }
        return characters * (60.0 / 500) + words * (60.0 / 250);
    }

    public static string FormatDuration(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0) return "—";
        if (value is > 0 and < 60) return UiText.Get("不足 1 分钟");
        var minutes = Math.Ceiling(value / 60);
        if (minutes < 60) return UiText.Get("{0:0} 分钟", minutes);
        var hours = Math.Floor(minutes / 60);
        var remainder = minutes % 60;
        return remainder == 0 ? UiText.Get("{0:0} 小时", hours)
            : UiText.Get("{0:0} 小时 {1:0} 分钟", hours, remainder);
    }

    private static bool IsCjk(int value) => value is
        >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff
        or >= 0x20000 and <= 0x323af or >= 0x3040 and <= 0x30ff
        or >= 0xac00 and <= 0xd7af;
}
