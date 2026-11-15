using System.Globalization;
using System.Text;

namespace Libzora.Core;

/// <summary>
/// Resolves what a long press on reader text should select: one CJK text
/// element, one Latin-style word, or the single character under the finger.
/// The word rules mirror the inline typesetting units so "don't",
/// "well-known" and "20°C" stay whole.
/// </summary>
public static class ReaderWordSelection
{
    /// <summary>
    /// Returns the UTF-16 range surrounding <paramref name="offset"/>.
    /// <paramref name="offset"/> may be a caret boundary at the end of the text.
    /// </summary>
    public static (int Start, int End) GetRange(string? text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);

        offset = Math.Clamp(offset, 0, text.Length - 1);
        if (offset > 0 && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1]))
        {
            offset--;
        }

        var elementEnd = TextElementEnd(text, offset);
        var rune = Rune.GetRuneAt(text, offset);
        if (IsCjk(rune.Value) || !IsWordRune(rune))
        {
            return (offset, elementEnd);
        }

        var start = offset;
        while (start > 0)
        {
            var previousStart = PreviousTextElementStart(text, start);
            if (!IsWordRune(Rune.GetRuneAt(text, previousStart)))
            {
                break;
            }

            start = previousStart;
        }

        var end = elementEnd;
        while (end < text.Length)
        {
            var next = Rune.GetRuneAt(text, end);
            if (!IsWordRune(next))
            {
                break;
            }

            end += next.Utf16SequenceLength;
        }

        // Internal connectors join the neighbouring word runs. Walk in both
        // directions so pressing either half of "well-known" selects all of it.
        while (start > 0)
        {
            var connectorStart = PreviousTextElementStart(text, start);
            var connector = Rune.GetRuneAt(text, connectorStart);
            if (!IsConnector(connector))
            {
                break;
            }

            var prefixStart = connectorStart;
            while (prefixStart > 0)
            {
                var candidateStart = PreviousTextElementStart(text, prefixStart);
                if (!IsWordRune(Rune.GetRuneAt(text, candidateStart)))
                {
                    break;
                }

                prefixStart = candidateStart;
            }

            if (prefixStart == connectorStart)
            {
                break;
            }

            start = prefixStart;
        }

        while (end < text.Length)
        {
            var connector = Rune.GetRuneAt(text, end);
            var connectorEnd = end + connector.Utf16SequenceLength;

            // A percent sign or degree unit is a suffix, not a bridge to
            // another word, and still belongs to the number before it.
            if (connector.Value == '%')
            {
                end = connectorEnd;
                continue;
            }

            if (connector.Value == '°'
                && connectorEnd < text.Length
                && text[connectorEnd] is 'C' or 'F')
            {
                end = connectorEnd + 1;
                continue;
            }

            if (!IsConnector(connector) || connectorEnd >= text.Length)
            {
                break;
            }

            var next = Rune.GetRuneAt(text, connectorEnd);
            if (!IsWordRune(next))
            {
                break;
            }

            end = connectorEnd;
            while (end < text.Length)
            {
                next = Rune.GetRuneAt(text, end);
                if (!IsWordRune(next))
                {
                    break;
                }

                end += next.Utf16SequenceLength;
            }
        }

        return (start, end);
    }

    private static int TextElementEnd(string text, int start)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text, start);
        return enumerator.MoveNext()
            ? Math.Min(text.Length, start + Math.Max(1, enumerator.GetTextElement().Length))
            : Math.Min(text.Length, start + 1);
    }

    private static int PreviousTextElementStart(string text, int index)
    {
        index--;
        if (index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static bool IsWordRune(Rune rune) =>
        !IsCjk(rune.Value)
        && (Rune.IsLetterOrDigit(rune)
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark);

    private static bool IsConnector(Rune rune) =>
        rune.Value is '\'' or '’' or '&' or '.' or ',' or ':' or '/' or '+' or '-' or '–' or '—';

    // Matches the CJK ranges used by typesetting and reading-time estimates.
    private static bool IsCjk(int value) => value is
        >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff
        or >= 0x20000 and <= 0x323af or >= 0x3040 and <= 0x30ff
        or >= 0xac00 and <= 0xd7af;
}
