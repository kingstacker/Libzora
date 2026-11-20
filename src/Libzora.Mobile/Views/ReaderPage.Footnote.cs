using System.Globalization;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Mobile.Views;

/// <summary>
/// In-page note preview and the temporary trail a note jump leaves behind.
/// The native shell owns presentation; this partial owns text resolution,
/// navigation and the rule that browsing notes never overwrites reading
/// progress.
/// </summary>
public partial class ReaderPage
{
    private const int MaximumFootnoteReturnDepth = 5;

    private sealed record FootnoteReturnPoint(
        int ChapterIndex,
        ReaderContentPosition? ContentPosition,
        int ScrollPosition,
        string? Backlink);

    private readonly EpubFootnoteResolver _footnoteResolver = new();
    private readonly Dictionary<string, EpubFootnoteDetail?> _footnoteCache = new(StringComparer.Ordinal);
    private readonly List<FootnoteReturnPoint> _footnoteReturn = [];
    private NativeReaderFootnote? _activeFootnote;
    private string? _activeFootnoteMarker;
    private Func<string>? _activeFootnoteErrorText;
    private string? _activeFootnoteBacklink;
    private string? _footnoteJumpBacklink;
    private long _footnotePreviewId;
    private CancellationTokenSource? _footnotePreviewCancellation;
    private bool _footnoteProgressSuppressed;
    private ReaderProgressRow? _footnoteBodyProgress;
    private bool _footnoteNavigationInFlight;
    private bool _footnoteReturnHasBaseline;
    private double _footnoteReturnSettledTop;
    private double _footnoteReturnSettledLeft;
    private int _footnoteReturnSettledChapter = -1;
    private DateTime _footnoteReturnSettledAt;
    private double _lastScrollTop;
    private double _lastScrollLeft;

    public event Action<NativeReaderFootnote>? NativeFootnoteRequested;

    /// <summary>Shows a note card for a marker tap and resolves its text lazily.</summary>
    private async Task ShowFootnoteAsync(JsonElement root)
    {
        var href = ReadFootnoteString(root, "href") ?? string.Empty;
        var inlineText = ReadFootnoteString(root, "footnoteText");
        var marker = ReadFootnoteString(root, "marker");
        _activeFootnoteMarker = marker;
        var anchor = ReadFootnoteAnchor(root);
        var id = ++_footnotePreviewId;
        _footnotePreviewCancellation?.Cancel();
        _footnotePreviewCancellation?.Dispose();
        _footnotePreviewCancellation = null;

        var target = TryFootnoteTarget(href);
        var jumpable = target is { IsFile: true } && target.Fragment.Length > 1;
        var title = FootnoteTitle(marker);

        if (!string.IsNullOrWhiteSpace(inlineText))
        {
            // Synthetic inline/plain notes have no real destination; plain
            // numbered notes carry the definition offset so they can still
            // scroll to their note inside this chapter.
            var canJump = jumpable
                && !href.Contains("__libzora-inline-footnote", StringComparison.Ordinal)
                && (!href.Contains(PlainFootnoteFragmentPrefix, StringComparison.Ordinal)
                    || TryParsePlainFootnoteOffset(target!.Fragment, out _));
            PublishFootnote(new NativeReaderFootnote(
                id, true, title, href, StripFootnoteLabel(inlineText, marker), false, canJump, anchor, null));
            _activeFootnoteBacklink = null;
            return;
        }

        if (!jumpable)
        {
            PublishFootnote(new NativeReaderFootnote(
                id, true, title, href, null, false, false, anchor, UiText.Get("未找到注释内容")),
                () => UiText.Get("未找到注释内容"));
            return;
        }

        var key = EpubFootnoteResolver.NormalizeTargetKey(target!.AbsoluteUri);
        if (_footnoteCache.TryGetValue(key, out var cached))
        {
            PublishResolvedFootnote(id, title, target, cached, marker, anchor);
            return;
        }

        PublishFootnote(new NativeReaderFootnote(
            id, true, title, target.AbsoluteUri, null, true, false, anchor, null));
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _footnotePreviewCancellation = cancellation;
        try
        {
            var detail = _epubDocument is { } document
                ? await _footnoteResolver.ResolveDetailAsync(
                    document.RootPath, target.AbsoluteUri, cancellation.Token)
                : null;
            if (cancellation.IsCancellationRequested || id != _footnotePreviewId) return;
            _footnoteCache[key] = detail;
            PublishResolvedFootnote(id, title, target, detail, marker, anchor);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (cancellation.IsCancellationRequested || id != _footnotePreviewId) return;
            _footnoteCache[key] = null;
            PublishFootnote(new NativeReaderFootnote(
                id, true, title, target.AbsoluteUri, null, false, true, anchor,
                UiText.Get("无法读取注释：{0}", UiText.Localize(exception.Message))),
                () => UiText.Get("无法读取注释：{0}", UiText.Localize(exception.Message)));
        }
    }

    private void PublishResolvedFootnote(
        long id,
        string title,
        Uri target,
        EpubFootnoteDetail? detail,
        string? marker,
        NativeReaderFootnoteAnchor anchor)
    {
        var text = detail?.Text is { } raw ? StripFootnoteLabel(raw, marker) : null;
        var resolved = !string.IsNullOrWhiteSpace(text);
        // The preview is a convenience, never a replacement for the source:
        // keep "定位原注" reachable even when the whole note fits in the card.
        PublishFootnote(new NativeReaderFootnote(
            id, true, title, target.AbsoluteUri, text, false, true, anchor,
            resolved ? null : UiText.Get("未找到注释内容")),
            resolved ? null : () => UiText.Get("未找到注释内容"));
        _activeFootnoteBacklink = detail?.Backlink;
    }

    private void PublishFootnote(NativeReaderFootnote footnote, Func<string>? errorText = null)
    {
        // A language change may occur while an asynchronous note resolves.
        if (footnote.Visible) footnote = footnote with { Title = FootnoteTitle(_activeFootnoteMarker) };
        _activeFootnote = footnote.Visible ? footnote : null;
        _activeFootnoteErrorText = footnote.Visible ? errorText : null;
        NativeFootnoteRequested?.Invoke(footnote);
    }

    private void RefreshFootnoteUiText()
    {
        if (_activeFootnote is not { } footnote) return;
        var updated = footnote with
        {
            Title = FootnoteTitle(_activeFootnoteMarker),
            Error = _activeFootnoteErrorText?.Invoke()
        };
        _activeFootnote = updated;
        NativeFootnoteRequested?.Invoke(updated);
    }

    public void NativeDismissFootnote()
    {
        _footnotePreviewId++;
        _footnotePreviewCancellation?.Cancel();
        _footnotePreviewCancellation?.Dispose();
        _footnotePreviewCancellation = null;
        if (_activeFootnote is null) return;
        _activeFootnote = null;
        _activeFootnoteMarker = null;
        _activeFootnoteErrorText = null;
        _activeFootnoteBacklink = null;
        NativeFootnoteRequested?.Invoke(new NativeReaderFootnote(
            _footnotePreviewId, false, string.Empty, string.Empty, null, false, false,
            NativeReaderFootnoteAnchor.Empty, null));
    }

    /// <summary>Jumps to the note's definition without moving the reading progress.</summary>
    public async Task NativeJumpFootnoteAsync()
    {
        if (!_ready || _isPdf
            || _activeFootnote is not { CanJump: true } footnote
            || _readerHost is not NativeReaderHost host
            || _epubDocument is not { } document)
            return;
        if (!Uri.TryCreate(footnote.Href, UriKind.Absolute, out var target)
            || !target.IsFile
            || string.IsNullOrEmpty(target.Fragment))
            return;
        var path = Path.GetFullPath(target.LocalPath);
        var chapter = document.Chapters.ToList().FindIndex(candidate =>
            string.Equals(Path.GetFullPath(candidate), path, StringComparison.OrdinalIgnoreCase));
        if (chapter < 0) return;

        var backlink = _activeFootnoteBacklink;
        var fragment = Uri.UnescapeDataString(target.Fragment.TrimStart('#'));
        var plainFootnoteOffset = TryParsePlainFootnoteOffset(fragment, out var noteOffset) ? noteOffset : (int?)null;
        var returnPoint = new FootnoteReturnPoint(
            _chapterIndex,
            host.CaptureContentPosition(),
            (int)Math.Clamp(_epubPosition, 0, int.MaxValue),
            _footnoteJumpBacklink);
        // A detour must start from a durable body position, including a page
        // turn that the ordinary progress throttle has not saved yet. Nested
        // note jumps keep the original body baseline.
        if (!_footnoteProgressSuppressed)
        {
            if (!await SaveProgressSoonAsync(force: true) || _lastSavedProgress is null)
            {
                _shell.ShowToast(UiText.Get("无法保存阅读进度，请稍后重试。"));
                return;
            }
            _footnoteBodyProgress = _lastSavedProgress;
        }
        if (_disposed || !ReferenceEquals(_readerHost, host)) return;
        NativeDismissFootnote();
        _footnoteReturn.Add(returnPoint);
        // Keep the outermost body return point when limiting nested notes.
        if (_footnoteReturn.Count > MaximumFootnoteReturnDepth) _footnoteReturn.RemoveAt(1);
        _footnoteProgressSuppressed = true;
        _footnoteJumpBacklink = backlink;
        _tts?.NotifyManualNavigation();
        _footnoteNavigationInFlight = true;
        try
        {
            var navigated = plainFootnoteOffset is { } definitionOffset && chapter == _chapterIndex
                ? await RevealPlainFootnoteAsync(host, definitionOffset)
                : chapter == _chapterIndex
                    ? await RevealFootnoteInChapterAsync(host, fragment)
                    : await NavigateToChapterAsync(host, chapter, new Uri(target.AbsoluteUri));
            if (!navigated)
            {
                _footnoteReturn.RemoveAt(_footnoteReturn.Count - 1);
                _footnoteProgressSuppressed = _footnoteReturn.Count > 0;
                if (!_footnoteProgressSuppressed) _footnoteBodyProgress = null;
                _footnoteJumpBacklink = returnPoint.Backlink;
                _shell.ShowToast(UiText.Get("无法定位注释"));
                NotifyNativeState();
                return;
            }
            UpdateEpubLabels();
            MarkFootnoteReturnSettled();
            NotifyNativeState();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _footnoteReturn.RemoveAt(_footnoteReturn.Count - 1);
            _footnoteProgressSuppressed = _footnoteReturn.Count > 0;
            if (!_footnoteProgressSuppressed) _footnoteBodyProgress = null;
            _footnoteJumpBacklink = returnPoint.Backlink;
            NotifyNativeState();
        }
        catch (Exception exception)
        {
            _footnoteReturn.RemoveAt(_footnoteReturn.Count - 1);
            _footnoteProgressSuppressed = _footnoteReturn.Count > 0;
            if (!_footnoteProgressSuppressed) _footnoteBodyProgress = null;
            _footnoteJumpBacklink = returnPoint.Backlink;
            _shell.ShowToast(UiText.Localize(exception.Message));
            NotifyNativeState();
        }
        finally
        {
            _footnoteNavigationInFlight = false;
        }
    }

    private static async Task<bool> RevealFootnoteInChapterAsync(NativeReaderHost host, string fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        await host.RevealFootnoteAsync(fragment);
        return true;
    }

    /// <summary>
    /// Plain numbered notes live in the same chapter with no element anchor.
    /// The layout encodes the definition's body-text offset in the synthetic
    /// fragment, so the jump can scroll straight to the note paragraph.
    /// </summary>
    private static async Task<bool> RevealPlainFootnoteAsync(NativeReaderHost host, int offset)
    {
        // A partially composed chapter would clamp the offset to its last
        // page, so finish the layout before resolving the definition page.
        await host.EnsureCompleteLayoutAsync();
        host.ScrollToOffset(offset);
        return true;
    }

    private const string PlainFootnoteFragmentPrefix = "__libzora-plain-footnote-";

    private static bool TryParsePlainFootnoteOffset(string? fragment, out int offset)
    {
        offset = -1;
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        var value = Uri.UnescapeDataString(fragment.TrimStart('#'));
        if (!value.StartsWith(PlainFootnoteFragmentPrefix, StringComparison.Ordinal)) return false;
        // Format: <section>-<key>-<definition offset>. Older synthetic
        // fragments without the offset have only two parts and cannot jump.
        var parts = value[PlainFootnoteFragmentPrefix.Length..].Split('-');
        return parts.Length >= 3
            && int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            && offset >= 0;
    }

    /// <summary>Returns from a note jump, exactly restoring the pre-jump view.</summary>
    public async Task NativeReturnFromFootnoteAsync()
    {
        if (_isPdf
            || _footnoteReturn.Count == 0
            || _readerHost is not NativeReaderHost host
            || _epubDocument is not { } document)
            return;
        var point = _footnoteReturn[^1];
        _footnoteReturn.RemoveAt(_footnoteReturn.Count - 1);
        _tts?.NotifyManualNavigation();
        _footnoteNavigationInFlight = true;
        try
        {
            if (point.ChapterIndex != _chapterIndex
                && !await NavigateToChapterAsync(host, point.ChapterIndex, new Uri(document.Chapters[point.ChapterIndex])))
                throw new InvalidOperationException(UiText.Get("无法定位注释"));
            var restored = point.ContentPosition is { } contentPosition
                && host.RestoreContentPosition(contentPosition);
            if (!restored) host.SeekToPixelScroll(point.ScrollPosition);
            UpdateEpubLabels();
            _footnoteJumpBacklink = point.Backlink;
            if (_footnoteReturn.Count == 0)
            {
                _footnoteProgressSuppressed = false;
                _footnoteBodyProgress = null;
            }
            MarkFootnoteReturnSettled();
            NotifyNativeState();
            if (_footnoteReturn.Count == 0) _ = SaveProgressSoonAsync(force: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _footnoteReturn.Add(point);
        }
        catch (Exception exception)
        {
            _footnoteReturn.Add(point);
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
        finally
        {
            _footnoteNavigationInFlight = false;
        }
    }

    /// <summary>
    /// Reading away from the note ends the detour: the return pill disappears
    /// and the position the reader moved to becomes the saved progress.
    /// </summary>
    private bool DismissFootnoteReturnIfReaderMoved(double top, double left)
    {
        if (_footnoteNavigationInFlight
            || !_footnoteReturnHasBaseline
            || _footnoteReturn.Count == 0)
        {
            return false;
        }
        // The jump and restore settle their own scroll messages first.
        if (DateTime.UtcNow - _footnoteReturnSettledAt < TimeSpan.FromMilliseconds(500)) return false;
        if (_chapterIndex == _footnoteReturnSettledChapter
            && Math.Abs(top - _footnoteReturnSettledTop) <= 2
            && Math.Abs(left - _footnoteReturnSettledLeft) <= 2)
        {
            return false;
        }
        ClearFootnoteNavigation();
        return true;
    }

    /// <summary>Remembers the note location a reader must leave to end the detour.</summary>
    private void MarkFootnoteReturnSettled()
    {
        if (_footnoteReturn.Count == 0) return;
        _footnoteReturnSettledChapter = _chapterIndex;
        _footnoteReturnSettledTop = _lastScrollTop;
        _footnoteReturnSettledLeft = _lastScrollLeft;
        _footnoteReturnSettledAt = DateTime.UtcNow;
        _footnoteReturnHasBaseline = true;
    }

    /// <summary>Leaves footnote browsing for an explicit TOC, slider or link jump.</summary>
    private void ClearFootnoteNavigation()
    {
        _footnoteBodyProgress = null;
        if (_footnoteReturn.Count == 0 && !_footnoteProgressSuppressed) return;
        _footnoteReturn.Clear();
        _footnoteJumpBacklink = null;
        _footnoteReturnHasBaseline = false;
        _footnoteProgressSuppressed = false;
        NotifyNativeState();
    }

    private static NativeReaderFootnoteAnchor ReadFootnoteAnchor(JsonElement root)
    {
        if (root.TryGetProperty("anchor", out var anchor) && anchor.ValueKind == JsonValueKind.Object)
        {
            return new NativeReaderFootnoteAnchor(
                ReadFootnoteDouble(anchor, "x"),
                ReadFootnoteDouble(anchor, "y"),
                ReadFootnoteDouble(anchor, "width"),
                ReadFootnoteDouble(anchor, "height"),
                ReadFootnoteDouble(anchor, "viewportWidth"),
                ReadFootnoteDouble(anchor, "viewportHeight"));
        }
        return new NativeReaderFootnoteAnchor(
            ReadFootnoteDouble(root, "x"),
            ReadFootnoteDouble(root, "y"),
            0,
            0,
            0,
            0);
    }

    private static string? ReadFootnoteString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadFootnoteDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
        && double.IsFinite(number)
            ? number
            : 0;

    private static Uri? TryFootnoteTarget(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var target) && target.IsFile ? target : null;

    private static string NormalizeFootnoteHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var target)
            ? EpubFootnoteResolver.NormalizeTargetKey(target.AbsoluteUri)
            : href;

    private static string FootnoteTitle(string? marker)
    {
        var value = (marker ?? string.Empty).Trim();
        if (value.Length == 0 || value is "注" or "註") return UiText.Get("注释");
        var trimmed = FootnoteLabelCore(value, normalizeEnclosedNumber: false);
        if (trimmed.Length == 0) return UiText.Get("注释");
        if (trimmed.Length > 12) trimmed = trimmed[..12];
        return UiText.Get("注 {0}", trimmed);
    }

    private static string FootnoteLabelCore(string label, bool normalizeEnclosedNumber = true)
    {
        var core = label.Trim('[', ']', '［', '］', '〔', '〕', '【', '】', '(', ')', '（', '）').Trim();
        if (core.Length > 0 && core[0] is '注' or '註') core = core[1..].Trim();
        if (normalizeEnclosedNumber && core.Length == 1
            && char.GetUnicodeCategory(core[0]) == UnicodeCategory.OtherNumber)
        {
            var number = char.GetNumericValue(core[0]);
            if (number >= 0 && number <= 50 && number == Math.Truncate(number))
                core = ((int)number).ToString(CultureInfo.InvariantCulture);
        }
        return core;
    }

    /// <summary>
    /// Drops a note label ("3：" or "注3.") that repeats the marker already
    /// shown in the card title. A label only counts when punctuation or a
    /// space follows it, so genuine text such as "3月…" is left alone.
    /// </summary>
    private static string StripFootnoteLabel(string text, string? marker)
    {
        var label = (marker ?? string.Empty).Trim();
        if (label.Length == 0 || text.Length == 0) return text;
        var core = FootnoteLabelCore(label);
        if (core.Length == 0) core = label;
        var candidates = new[]
        {
            label, core, "注" + core, "注 " + core, "註" + core, "註 " + core,
            "[" + core + "]", "［" + core + "］", "〔" + core + "〕", "【" + core + "】",
            "(" + core + ")", "（" + core + "）",
        };
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (!text.StartsWith(candidate, StringComparison.Ordinal)) continue;
            var rest = text[candidate.Length..];
            if (rest.Length == 0) continue;
            // Publishers may use a bare "1" in the body and "[1]" in the
            // definition. Brackets identify the label even without whitespace;
            // bare digits still require a separator so "1月…" stays intact.
            var bracketed = candidate.Length > 2 && "[(［〔【（".Contains(candidate[0])
                && "])］〕】）".Contains(candidate[^1]);
            var enclosedNumber = candidate.Length == 1
                && char.GetUnicodeCategory(candidate[0]) == UnicodeCategory.OtherNumber;
            var separator = rest[0];
            var dotSeparator = separator is '.' or '．' or '，' or ','
                && (rest.Length == 1 || char.IsWhiteSpace(rest[1]));
            if (!bracketed && !enclosedNumber && !char.IsWhiteSpace(separator)
                && !"：:、。）)】]〕］".Contains(separator) && !dotSeparator) continue;
            rest = rest.TrimStart(' ', '\t', '\r', '\n', '　', '：', ':', '、', '。', '，', ',', '.', '．',
                '）', ')', '】', ']', '〕', '］');
            if (rest.Length > 0) return rest;
        }
        return text;
    }
}
