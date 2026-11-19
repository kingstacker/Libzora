using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Content positions, text and speech snapshots.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private int PageFromPixelScroll(double scrollPosition)
    {
        var stride = Math.Max(1, ComposePageWidth(Math.Max(1, Bounds.Width)));
        var page = (int)Math.Clamp(Math.Round(scrollPosition / stride), 0, Math.Max(0, PageCount - 1));
        if (IsSpread)
        {
            page -= page % 2;
        }

        return page;
    }

    public string? BodyText => _content?.BodyText;

    public IReadOnlyList<(int Start, int Length)> RubyAnnotationRanges =>
        _content?.RubyAnnotationRanges ?? [];

    private ReaderSearchTextPolicy.PreparedSearchText? _preparedSearchText;
    private ChapterContent? _preparedSearchContent;

    /// <summary>
    /// Page count of the chapter currently composed in this host, used by the
    /// book-wide estimate so the visible chapter is not parsed and laid out
    /// a second time.
    /// </summary>
    internal (string ChapterPath, int PageCount)? CurrentChapterPageCount
    {
        get
        {
            var layout = _layout;
            var content = _content;
            return layout is null || content is null
                ? null
                : (content.ChapterPath, layout.Pages.Count);
        }
    }

    /// <summary>
    /// Returns the current chapter's page count only when the live layout was
    /// composed with the same settings and viewport as the requested estimate.
    /// Otherwise a settings change could report a stale count.
    /// </summary>
    public (string ChapterPath, int PageCount)? GetCurrentChapterPageCountFor(
        ReaderLayoutSettings settings,
        double width,
        double height)
    {
        var layout = _layout;
        var content = _content;
        var options = _options;
        if (layout is null || content is null || options is null || !layout.IsComplete) return null;
        var expected = BuildOptions(settings, width, height);
        return options.WritingMode == expected.WritingMode
            && Math.Abs(options.BaseFontSize - expected.BaseFontSize) < 0.01f
            && Math.Abs(options.LineHeight - expected.LineHeight) < 0.001f
            && options.ParagraphIndent == expected.ParagraphIndent
            && Math.Abs(options.ParagraphSpacingEm - expected.ParagraphSpacingEm) < 0.001f
            && options.ParagraphSpacingMatchesLineHeight == expected.ParagraphSpacingMatchesLineHeight
            && options.OverridePublisherLineHeight == expected.OverridePublisherLineHeight
            && options.OverridePublisherParagraphSpacing == expected.OverridePublisherParagraphSpacing
            && options.NaturalLatinTypography == expected.NaturalLatinTypography
            && options.HyphenateEnglish == expected.HyphenateEnglish
            && options.AvoidWidowsAndOrphans == expected.AvoidWidowsAndOrphans
            && options.AlignLeft == expected.AlignLeft
            && options.CjkLatinSpacing == expected.CjkLatinSpacing
            && string.Equals(settings.FontFamily, _settings.FontFamily, StringComparison.OrdinalIgnoreCase)
            && string.Equals(settings.LatinFontFamily, _settings.LatinFontFamily, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(options.ViewportWidth - expected.ViewportWidth) < 0.5f
            && Math.Abs(options.ViewportHeight - expected.ViewportHeight) < 0.5f
            && Math.Abs(options.InsetHorizontal - expected.InsetHorizontal) < 0.5f
            && Math.Abs(options.InsetVertical - expected.InsetVertical) < 0.5f
            && Math.Abs(options.EffectiveInsetBottom - expected.EffectiveInsetBottom) < 0.5f
                ? (content.ChapterPath, layout.Pages.Count)
                : null;
    }

    public IReadOnlyList<ReaderSearchMatch> FindSearchMatches(string query)
    {
        var content = _content;
        if (_preparedSearchText is null || !ReferenceEquals(_preparedSearchContent, content))
        {
            _preparedSearchText = ReaderSearchTextPolicy.Prepare(
                content?.BodyText ?? string.Empty,
                content?.RubyAnnotationRanges);
            _preparedSearchContent = content;
        }
        return ReaderSearchTextPolicy.FindMatches(_preparedSearchText, query);
    }

    /// <summary>
    /// Returns the text that is actually represented by visible content
    /// blocks. The raw chapter stream also contains ruby/footnote text that
    /// the native painter intentionally omits, so using it directly for TTS
    /// makes the voice advance through text that has no corresponding glyph
    /// on screen.
    /// </summary>
    public ReaderTtsTextSnapshot? GetSpeechText()
    {
        if (_content is null || _layout is null || _content.BodyText.Length == 0)
        {
            return null;
        }

        var speechStartOffset = GetSpeechStartOffset();
        if (speechStartOffset < 0)
        {
            return null;
        }

        if (ReferenceEquals(_speechTextSnapshotContent, _content)
            && ReferenceEquals(_speechTextSnapshotLayout, _layout)
            && _speechTextSnapshot is { StartOffset: var cachedStartOffset }
            && cachedStartOffset == speechStartOffset)
        {
            return _speechTextSnapshot;
        }

        _speechTextSnapshotContent = _content;
        _speechTextSnapshotLayout = _layout;
        _speechTextSnapshot = ReaderSpeechTextBuilder.Build(
            _content,
            speechStartOffset,
            _layout);
        return _speechTextSnapshot;
    }

    private void InvalidateSpeechTextSnapshot()
    {
        _speechTextSnapshot = null;
        _speechTextSnapshotContent = null;
        _speechTextSnapshotLayout = null;
    }

    /// <summary>
    /// Character offset of the first visible text at or after the current page,
    /// for starting TTS. Returns -1 when the current chapter has no readable
    /// text from the current page onward (for example, an image-only page at
    /// the end of a chapter).
    /// </summary>
    public int GetSpeechStartOffset()
    {
        if (_layout is null || _layout.Pages.Count == 0) return -1;
        var pageIndex = Math.Clamp(VisibleFirstPageIndex, 0, _layout.Pages.Count - 1);
        for (var index = pageIndex; index < _layout.Pages.Count; index++)
        {
            var page = _layout.Pages[index];
            if (page.TextStartOffset >= 0
                && page.TextEndOffset > page.TextStartOffset)
            {
                return page.TextStartOffset;
            }
        }

        return -1;
    }

    /// <summary>Paints and scrolls the currently spoken text range.</summary>
    public void SetSpeechHighlight(int start, int length)
    {
        if (_content is null || _layout is null || length <= 0)
        {
            ClearSpeechHighlight();
            return;
        }

        var maximum = Math.Max(0, _content.BodyText.Length);
        var normalizedStart = Math.Clamp(start, 0, maximum);
        var normalizedLength = Math.Clamp(length, 0, maximum - normalizedStart);
        if (normalizedLength <= 0)
        {
            ClearSpeechHighlight();
            return;
        }

        _speechHighlight = (normalizedStart, normalizedLength);
        ScrollToOffset(normalizedStart);
        _bitmapDirty = true;
        InvalidateVisual();
    }

    public void NavigateToSpeechOffset(int offset)
    {
        if (_content is null || _layout is null || offset < 0 || offset >= _content.BodyText.Length) return;
        ScrollToOffset(offset);
    }

    public void ClearSpeechHighlight()
    {
        if (_speechHighlight is null) return;
        _speechHighlight = null;
        _bitmapDirty = true;
        InvalidateVisual();
    }

    /// <summary>Returns a short quote from the first visible composed page.</summary>
    public string? GetCurrentPageQuote(int maxLength = 72)
    {
        var pageIndex = VisibleFirstPageIndex;
        if (_content is null
            || _layout is null
            || pageIndex < 0
            || pageIndex >= _layout.Pages.Count
            || maxLength <= 0)
        {
            return null;
        }

        var page = _layout.Pages[pageIndex];
        if (page.TextStartOffset < 0 || page.TextEndOffset <= page.TextStartOffset)
        {
            return null;
        }

        var start = Math.Clamp(page.TextStartOffset, 0, _content.BodyText.Length);
        var end = Math.Clamp(page.TextEndOffset, start, _content.BodyText.Length);
        var normalized = string.Join(
            " ",
            _content.BodyText[start..end].Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        var length = maxLength;
        if (length > 0 && length < normalized.Length && char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..Math.Max(1, length)];
    }

    // ---- chapter pipeline -------------------------------------------------
}
