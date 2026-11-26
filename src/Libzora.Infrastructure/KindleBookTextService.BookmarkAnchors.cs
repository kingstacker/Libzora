namespace Libzora.Infrastructure;

internal sealed partial record KindleRawBookText
{
    /// <summary>Extract only a text anchor belonging to this block; never hop across an image or chapter.</summary>
    public bool ExtractBookmarkAnchorAt(long position, out string quote, out string prefix, out string suffix) =>
        ExtractBookmarkAnchorAt(position, out quote, out prefix, out suffix, out _);

    public bool ExtractBookmarkAnchorAt(long position, out string quote, out string prefix, out string suffix, out long canonicalPosition)
    {
        canonicalPosition = 0;
        quote = prefix = suffix = string.Empty;
        if (position < 0 || position >= Text.LongLength || GetVisibleIndex() is not { } visible) return false;
        var index = FindFirstVisibleAtOrAfter(visible.Starts, position);
        if (index > 0 && visible.Starts[index - 1] <= position && position < visible.Ends[index - 1])
        {
            index--;
            while (index > 0 && visible.Starts[index - 1] == visible.Starts[index]) index--;
        }
        if (index >= visible.Text.Length) return false;
        var block = visible.Blocks.FirstOrDefault(block => block.Start <= index && index < block.End);
        if (block is null || position < block.SourceStart || position >= block.SourceEnd) return false;
        if (visible.Images.Any(image => image.Start <= position && position < image.End
            || position <= image.Start && image.Start < visible.Starts[index])) return false;
        var end = SafeBoundary(visible.Text, Math.Min(block.End, index + 32));
        if (end <= index) return false;
        var prefixStart = Math.Max(block.Start, index - 24);
        if (prefixStart > 0 && char.IsLowSurrogate(visible.Text[prefixStart]) && char.IsHighSurrogate(visible.Text[prefixStart - 1])) prefixStart++;
        var suffixEnd = SafeBoundary(visible.Text, Math.Min(block.End, end + 24));
        canonicalPosition = visible.Starts[index];
        quote = visible.Text[index..end];
        prefix = visible.Text[prefixStart..index];
        suffix = visible.Text[end..suffixEnd];
        return true;
    }

    /// <summary>Find every occurrence and require the complete saved context, never a shortened fallback.</summary>
    public bool TryFindBookmarkPosition(string quote, string? prefix, string? suffix, out long position)
    {
        position = 0;
        if (GetVisibleIndex() is not { } visible) return false;
        quote = StripWhitespace(quote);
        prefix = StripWhitespace(prefix ?? string.Empty);
        suffix = StripWhitespace(suffix ?? string.Empty);
        if (quote.Length == 0) return false;
        var found = -1;
        for (var offset = 0; offset <= visible.Text.Length - quote.Length;)
        {
            var index = visible.Text.IndexOf(quote, offset, StringComparison.Ordinal);
            if (index < 0) break;
            offset = index + 1;
            if (!visible.Text.AsSpan(0, index).EndsWith(prefix, StringComparison.Ordinal)
                || !visible.Text.AsSpan(index + quote.Length).StartsWith(suffix, StringComparison.Ordinal)) continue;
            if (found >= 0) return false;
            found = index;
        }
        if (found < 0) return false;
        position = visible.Starts[found];
        return true;
    }

    private static int SafeBoundary(string text, int end) => end > 0 && end < text.Length
        && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end - 1 : end;
}
