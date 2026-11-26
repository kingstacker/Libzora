namespace Libzora.Core;

public sealed partial record ReaderContentPosition
{
    /// <summary>Capture the source text at this exact logical point, not the page's display quote.</summary>
    public ReaderContentPosition WithTextAnchor(string? bodyText)
    {
        if (ImageIndex is not null || string.IsNullOrEmpty(bodyText) || TextOffset < 0 || TextOffset >= bodyText.Length)
            return this with { AnchorQuote = null, AnchorPrefix = null, AnchorSuffix = null };
        var start = TextOffset;
        if (start > 0 && char.IsLowSurrogate(bodyText[start]) && char.IsHighSurrogate(bodyText[start - 1])) start--;
        var end = SafeEnd(bodyText, Math.Min(bodyText.Length, start + 48));
        var prefixStart = Math.Max(0, start - 40);
        if (prefixStart > 0 && char.IsLowSurrogate(bodyText[prefixStart]) && char.IsHighSurrogate(bodyText[prefixStart - 1])) prefixStart++;
        var suffixEnd = SafeEnd(bodyText, Math.Min(bodyText.Length, end + 40));
        return this with
        {
            AnchorQuote = bodyText[start..end],
            AnchorPrefix = bodyText[prefixStart..start],
            AnchorSuffix = bodyText[end..suffixEnd]
        };
    }

    private static int SafeEnd(string text, int end) => end > 0 && end < text.Length
        && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end - 1 : end;
}
