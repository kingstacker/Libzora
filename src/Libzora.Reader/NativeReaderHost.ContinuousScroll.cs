using Avalonia.Threading;
using Libzora.Core;
using Libzora.Layout;

namespace Libzora;

/// <summary>Bounded, two-sided chapter runway for finger-locked prose scrolling.</summary>
public sealed partial class NativeReaderHost
{
    private sealed record ContinuousChapterPreview(
        string Path, ChapterContent Content, ChapterLayout Layout, ReaderLayoutSettings Settings)
    {
        public bool Matches(string path, ReaderLayoutSettings settings, double width, double height) =>
            string.Equals(Path, path, StringComparison.Ordinal)
            && Settings == settings
            && Math.Abs(Layout.Options.ViewportWidth - width) < 0.01
            && Math.Abs(Layout.Options.ViewportHeight - height) < 0.01;
    }

    private ContinuousChapterPreview? _scrollNextChapterPreview;
    private ContinuousChapterPreview? _scrollPreviousChapterPreview;
    private ReaderLayoutSettings? _installedLayoutSettings;
    private string? _scrollPreviousChapterPath;
    private CancellationTokenSource? _previousChapterPreviewCts;
    private double _activeHandoffScrollDelta;
    private double _activeHandoffScrollOrigin;
    private double _partialScrollOverflow;
    private Uri? _loadedChapterSource;

    private bool HasNextContinuousPreview => IsContinuousProseScroll
        && _layout?.IsComplete == true && _scrollNextChapterLayout is { Pages.Count: > 0 };

    private bool HasPreviousContinuousPreview => IsContinuousProseScroll
        && _scrollPreviousChapterPreview is { Layout.IsComplete: true, Layout.Pages.Count: > 0 };

    private double ContinuousPreviousChapterStartY => HasPreviousContinuousPreview
        ? -(ComputeContinuousContentHeight(_scrollPreviousChapterPreview!.Layout) + ContinuousChapterGap)
        : 0;

    public bool IsContinuousScrollHandoffPending => _activeHandoffDirection != 0;
    public bool IsNavigationPending => _composePending;
    public bool IsReadingPositionInLoadedChapter => !IsContinuousProseScroll
        || (_scrollOffset + _partialScrollOverflow >= 0
            && (!HasNextContinuousPreview || _scrollOffset + _partialScrollOverflow < ContinuousNextChapterStartY));
    public string? LoadedChapterPath => _content?.ChapterPath;

    public void SetScrollPreviousChapterPath(string? chapterPath)
    {
        var next = string.IsNullOrWhiteSpace(chapterPath) ? null : chapterPath;
        if (string.Equals(next, _scrollPreviousChapterPath, StringComparison.Ordinal)) return;
        _previousChapterPreviewCts?.Cancel();
        _scrollPreviousChapterPath = next;
        if (!IsContinuousScrollHandoffPending && !_composePending)
        {
            _scrollPreviousChapterPreview = null;
            PreloadPreviousContinuousChapter();
        }
    }

    private ContinuousChapterPreview? CaptureContinuousChapter() =>
        IsContinuousProseScroll && !HasLoadedFootnoteFragment && _content is not null && _layout is { IsComplete: true }
            && _installedLayoutSettings is not null
            ? new(_content.ChapterPath, _content, _layout, _installedLayoutSettings) : null;

    private ContinuousChapterPreview? ResolveContinuousPreview(
        string path, ReaderLayoutSettings settings, double width, double height, bool fromPrevious)
    {
        var preview = fromPrevious ? _scrollPreviousChapterPreview : _scrollNextChapterPreview;
        return preview?.Matches(path, settings, width, height) == true
            && (!fromPrevious || preview.Layout.IsComplete) ? preview : null;
    }

    private void InstallContinuousScrollOffset(double requested)
    {
        _scrollOffset = ClampLiveScrollOffset(requested);
        var remaining = requested - _scrollOffset;
        _partialScrollOverflow = IsContinuousProseScroll
            && ((remaining > 0 && (_layout?.IsComplete == false || _scrollNextChapterPath is not null))
                || (remaining < 0 && _scrollPreviousChapterPath is not null)) ? remaining : 0;
    }

    /// <summary>A reversed drag can finish back in the original chapter.</summary>
    private bool CancelReversedContinuousHandoff(double? down, double? up)
    {
        var canceled = down is < 0 || up is <= 0;
        if (!canceled || !IsContinuousProseScroll || _content is null) return false;
        var originalOffset = _activeHandoffScrollOrigin + _activeHandoffScrollDelta;
        _activeHandoffDirection = 0;
        _activeHandoffScrollDelta = 0;
        _activeHandoffScrollOrigin = 0;
        _composePending = false;
        Source = _loadedChapterSource ?? new Uri(_content.ChapterPath);
        SetNavigationFragments(_layout!.Options.NavigationFragments is { } fragments ? fragments : Array.Empty<string>());
        InstallContinuousScrollOffset(originalOffset);
        _bitmapDirty = true;
        InvalidateVisual();
        return true;
    }

    /// <summary>Replay residual motion after the mobile navigation lock releases.</summary>
    public void ContinueContinuousScrollAfterNavigation()
    {
        if (_disposed || !IsContinuousProseScroll || _composePending || IsContinuousScrollHandoffPending) return;
        var requested = _scrollOffset + _partialScrollOverflow;
        if (requested < 0 && _scrollPreviousChapterPath is not null)
        {
            Emit(new { type = "continuousEdge", direction = -1, carryOffset = 0d, carryUpDelta = -requested });
        }
        else if (HasNextContinuousPreview && requested >= ContinuousNextChapterStartY)
        {
            Emit(new { type = "continuousEdge", direction = 1,
                carryOffset = requested - ContinuousNextChapterStartY, carryUpDelta = 0d });
        }
        else if (_partialScrollOverflow != 0 && _layout?.IsComplete == true)
        {
            var remaining = _partialScrollOverflow;
            _partialScrollOverflow = 0;
            ScrollByPixel(remaining);
        }
    }

    /// <summary>Warm the preceding tail without delaying the visible chapter.</summary>
    private void PreloadPreviousContinuousChapter()
    {
        _previousChapterPreviewCts?.Cancel();
        if (!IsContinuousProseScroll || _scrollPreviousChapterPath is not { } path
            || _content is null || _options is null) return;
        var settings = _settings;
        var options = _options;
        if (_scrollPreviousChapterPreview?.Matches(path, settings, options.ViewportWidth, options.ViewportHeight) == true)
            return;
        var currentContent = _content;
        var request = new CancellationTokenSource();
        _previousChapterPreviewCts = request;
        var token = request.Token;
        _ = LoadPreviousAsync();

        async Task LoadPreviousAsync()
        {
            IDisposable? fontUse = null;
            try
            {
                var preview = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var content = NativeReaderChapterContentCache.Shared.Load(path, settings.ParagraphIndent, null, token);
                    var fonts = FontLibrary;
                    fontUse = fonts.BeginComposition();
                    using var engine = new TypesetEngine(fonts, ownsFonts: false);
                    SelectReaderMainFont(engine, settings.FontFamily);
                    SelectReaderLatinFont(engine, settings.LatinFontFamily);
                    return new ContinuousChapterPreview(path, content, engine.Compose(content, options, token), settings);
                }, token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_content, currentContent)
                        || _settings != settings || !ReferenceEquals(_options, options)
                        || !string.Equals(path, _scrollPreviousChapterPath, StringComparison.Ordinal)) return;
                    _scrollPreviousChapterPreview = preview;
                    RetainInstalledLayoutFonts();
                    _bitmapDirty = true;
                    InvalidateVisual();
                    ContinueContinuousScrollAfterNavigation();
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { /* Missing/unavailable neighbors must not break the current page. */ }
            finally
            {
                fontUse?.Dispose();
                if (ReferenceEquals(_previousChapterPreviewCts, request)) _previousChapterPreviewCts = null;
                request.Dispose();
            }
        }
    }
}
