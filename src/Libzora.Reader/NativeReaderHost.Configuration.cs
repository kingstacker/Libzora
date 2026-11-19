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
/// Configure, presentation and visible text ranges.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    /// <summary>
    /// Text range covered by the page(s) currently filling the viewport. The
    /// native search uses this to publish visible matches before scanning the
    /// rest of the chapter.
    /// </summary>
    public (int Start, int End)? GetVisiblePageTextRange()
    {
        if (_content is null || _layout is null || _layout.Pages.Count == 0) return null;
        var first = Math.Clamp(VisibleFirstPageIndex, 0, _layout.Pages.Count - 1);
        var last = IsSpread ? Math.Min(first + 1, _layout.Pages.Count - 1) : first;
        var start = -1;
        var end = -1;
        for (var index = first; index <= last; index++)
        {
            var page = _layout.Pages[index];
            if (page.TextStartOffset < 0 || page.TextEndOffset <= page.TextStartOffset) continue;
            if (start < 0 || page.TextStartOffset < start) start = page.TextStartOffset;
            if (page.TextEndOffset > end) end = page.TextEndOffset;
        }

        if (start < 0 || end <= start) return null;
        start = Math.Clamp(start, 0, _content.BodyText.Length);
        end = Math.Clamp(end, start, _content.BodyText.Length);
        return end > start ? (start, end) : null;
    }

    /// <summary>Keep the current page's first text at a page boundary when settings change.</summary>
    public bool PreservePageStartOnSettingsChange { get; set; }

    public bool Vertical => _settings.VerticalWriting;

    /// <summary>
    /// Applies reader settings and restores a content anchor. The numeric
    /// scroll position remains a fallback for callers with legacy records.
    /// </summary>
    public async Task Configure(
        ReaderLayoutSettings settings,
        double scrollPosition,
        string? fragment,
        bool restoreFromProgress,
        bool showVerticalDebugBoxes,
        ReaderContentPosition? contentPosition = null,
        double? legacyChapterRatio = null)
    {
        if (_disposed) return;
        CancelPendingPageClick();
        var configurationVersion = ++_configurationVersion;
        // A reparse owns the pending position until it finishes. Applying a
        // second settings change to the old content would erase that position
        // and let the first load later install obsolete layout settings.
        if (_composePending && !await _navigationTask) return;
        if (_disposed || configurationVersion != _configurationVersion) return;

        var indentChanged = _content is not null
            && Source is not null
            && settings.ParagraphIndent != _loadedParagraphIndent;
        var settingsChanged = SettingsChanged(settings);
        var readingAnchor = (settingsChanged || indentChanged) && _layout is { Pages.Count: > 0 }
            ? ViewportReadingPositionMatches() ? _viewportReadingAnchor ?? CaptureViewportReadingAnchor()
                : CaptureViewportReadingAnchor()
            : null;
        var anchorPageStart = PreservePageStartOnSettingsChange && readingAnchor is not null
            && _presentation == ReaderPresentation.Single && DerivePresentation(settings) == ReaderPresentation.Single;
        if (anchorPageStart)
        {
            // Anchor the page's first text, not a saved mid-page reading
            // offset. Otherwise the lines above the saved position disappear
            // from the top of the page when typography changes.
            readingAnchor = CaptureViewportReadingAnchor();
            anchorPageStart = readingAnchor is { TextOffset: >= 0, ImageIndex: null };
        }
        _presentation = DerivePresentation(settings);
        _settings = settings;
        _showVerticalDebugBoxes = showVerticalDebugBoxes;

        Task<bool>? reload = null;
        if (indentChanged)
        {
            reload = ReloadChapterForIndentChange(readingAnchor, anchorPageStart);
        }
        else if (_content is not null && (settingsChanged || _layout is null))
        {
            _relayoutTimer?.Stop();
            await (RelayoutTask = RecomposeAsync(readingAnchor, anchorPageStart));
            await RelayoutTask;
            if (_disposed || configurationVersion != _configurationVersion) return;
        }

        // A saved progress position is authoritative when it exists. The
        // fragment is still useful for fresh link navigation, but letting it
        // win here would reopen a chapter at the link's page instead of the
        // user's last reading position.
        contentPosition = ReaderContentPosition.Validate(contentPosition);
        var hasSavedPosition = restoreFromProgress && (contentPosition is not null || scrollPosition > 0.5
            || legacyChapterRatio is > 0 and <= 1);
        _pendingFragment = hasSavedPosition || string.IsNullOrWhiteSpace(fragment)
            ? null
            : NormalizeFragment(fragment);
        if (restoreFromProgress)
        {
            _pendingReadingAnchor = null;
            _pendingRestoreOffset = null;
            _pendingRestorePage = null;
            _pendingRestoreRatio = null;
            _pendingSeekToEnd = false;
            _pendingScrollOffset = null;
            if (contentPosition is not null)
            {
                _pendingReadingAnchor = ToViewportReadingAnchor(contentPosition);
            }
            else if (legacyChapterRatio is > 0 and <= 1 && double.IsFinite(legacyChapterRatio.Value))
            {
                // Old records did not identify whether their number was a
                // pixel coordinate or a character offset. Their chapter ratio
                // is a portable, approximate fallback across layout changes.
                _pendingRestoreRatio = legacyChapterRatio;
            }
            else if (IsScroll)
            {
                // Continuous mode saves an absolute content offset.
                _pendingScrollOffset = Math.Max(0, scrollPosition);
            }
            else if (Vertical)
            {
                _pendingRestoreOffset = (int)Math.Round(Math.Max(0, scrollPosition));
            }
            else
            {
                _pendingRestorePage = PageFromPixelScroll(scrollPosition);
            }
        }
        else
        {
            if (_pendingFragment is not null) _pendingReadingAnchor = null;
            _pendingRestoreOffset = null;
            _pendingRestorePage = null;
            _pendingRestoreRatio = null;
            _pendingSeekToEnd = false;
            _pendingScrollOffset = null;
            if (!IsScroll)
            {
                _scrollOffset = 0;
            }
        }

        if (reload is not null && !await reload) return;
        if (_disposed || configurationVersion != _configurationVersion) return;
        ApplyPendingPosition();
        _bitmapDirty = true;
        InvalidateVisual();
        EmitScroll();
        // The saved position is authoritative and has now been applied. Only
        // then is it safe to finish a partial opening layout in the background.
        CompleteLayoutInBackground();
    }
}
