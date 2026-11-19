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
/// Chapter navigation core, fonts and layout options.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    private bool SettingsChanged(ReaderLayoutSettings settings) =>
        settings.FontScale != _settings.FontScale
        || Math.Abs(settings.LineHeight - _settings.LineHeight) > 0.001
        || Math.Abs(settings.MaxWidth - _settings.MaxWidth) > 0.001
        || settings.VerticalWriting != _settings.VerticalWriting
        || settings.ParagraphIndent != _settings.ParagraphIndent
        || Math.Abs(settings.ParagraphSpacing - _settings.ParagraphSpacing) > 0.001
        || settings.ParagraphSpacingMatchesLineHeight != _settings.ParagraphSpacingMatchesLineHeight
        || settings.OverridePublisherLineHeight != _settings.OverridePublisherLineHeight
        || settings.OverridePublisherParagraphSpacing != _settings.OverridePublisherParagraphSpacing
        || settings.NaturalLatinTypography != _settings.NaturalLatinTypography
        || settings.HyphenateEnglish != _settings.HyphenateEnglish
        || settings.AvoidWidowsAndOrphans != _settings.AvoidWidowsAndOrphans
        || !string.Equals(settings.FontFamily, _settings.FontFamily, StringComparison.OrdinalIgnoreCase)
        || settings.FlowMode != _settings.FlowMode
        || settings.TwoPageMode != _settings.TwoPageMode
        || Math.Abs(settings.BodyPadding - _settings.BodyPadding) > 0.001
        || settings.BodyBottomPadding != _settings.BodyBottomPadding;

    private static int? ResolveInitialComposeStop(
        ChapterContent content,
        string? fragment,
        ViewportReadingAnchor? readingAnchor,
        int? composeTextOffset)
    {
        if (content.BodyText.Length <= InitialComposeAheadCharacters / 2) return null;
        var anchor = composeTextOffset
            ?? (readingAnchor?.TextOffset is >= 0 ? readingAnchor.TextOffset : (int?)null)
            ?? (string.IsNullOrWhiteSpace(fragment)
                ? (int?)null
                : content.FragmentTextOffsets.TryGetValue(NormalizeFragment(fragment), out var fragmentOffset)
                    ? fragmentOffset
                    : (int?)null)
            ?? 0;
        var stop = (long)Math.Max(0, anchor) + InitialComposeAheadCharacters;
        if (stop >= content.BodyText.Length) return null;
        return (int)Math.Max(1, stop);
    }

    private Task<bool> NavigateCore(string chapterPath, string? fragment, ViewportReadingAnchor? readingAnchor = null,
        bool anchorPageStart = false, int? composeTextOffset = null, bool allowPartialCompose = false)
    {
        CancelPendingPageClick();
        var initialPage = _pageIndex;
        var initialScroll = _scrollOffset;
        _navigationTarget = null;
        _composePending = true;
        _relayoutTimer?.Stop();
        _viewportReadingAnchor = null;
        _viewportReadingState = null;
        _preserveViewportPosition = false;
        _relayoutCts?.Cancel();
        _navigationCts?.Cancel();
        _navigationCts?.Dispose();
        var navigationCts = new CancellationTokenSource();
        _navigationCts = navigationCts;
        var navigationToken = navigationCts.Token;
        var version = Interlocked.Increment(ref _navigationVersion);
        var navigationSource = Source;

        _pendingFragment = readingAnchor is not null || string.IsNullOrWhiteSpace(fragment) ? null : NormalizeFragment(fragment);
        _pendingRestoreOffset = null;
        _pendingRestorePage = null;
        _pendingRestoreRatio = null;
        _pendingSeekToEnd = false;
        _pendingReadingAnchor = readingAnchor;
        StopSelectionAutoPageTurn();
        _selectionActiveOffset = _selectionStart = _selectionEnd = _selectionAnchor = -1;
        _hasLastPointerPosition = false;
        _searchHits = null;
        _speechHighlight = null;
        InvalidateSpeechTextSnapshot();
        if (readingAnchor is null)
        {
            _pageIndex = 0;
            _scrollOffset = 0;
        }
        _pendingScrollOffset = null;

        // Compose off the UI thread; layout and shaping are pure CPU work on
        // engine-owned state, and the result is applied back on the UI thread.
        var settings = _settings;
        var width = Math.Max(1, ComposePageWidth(Bounds.Width));
        var height = Math.Max(1, Bounds.Height);
        var startingArgs = new ReaderNavigationStartingEventArgs(navigationSource);
        NavigationStarting?.Invoke(this, startingArgs);
        if (startingArgs.Cancel)
        {
            navigationCts.Cancel();
            _composePending = false;
            NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
            return _navigationTask = Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigationTask = completion.Task;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            TypesetEngine? navigationEngine = null;
            IDisposable? fontUse = null;
            try
            {
                var composed = await Task.Run(() =>
                {
                    navigationToken.ThrowIfCancellationRequested();
                    var loader = new XhtmlChapterLoader(settings.ParagraphIndent,
                        string.IsNullOrWhiteSpace(fragment) ? null : NormalizeFragment(fragment));
                    var content = loader.Load(chapterPath, navigationToken);
                    var options = BuildOptions(settings, width, height, _showVerticalDebugBoxes);
                    var fonts = FontLibrary;
                    fontUse = fonts.BeginComposition();
                    navigationEngine = new TypesetEngine(fonts, ownsFonts: false);
                    SelectReaderMainFont(navigationEngine, settings.FontFamily);
                    var stopAfter = allowPartialCompose
                        ? ResolveInitialComposeStop(content, fragment, readingAnchor, composeTextOffset)
                        : null;
                    var layout = navigationEngine.Compose(content, options, navigationToken,
                        anchorPageStart ? readingAnchor?.TextOffset : null, stopAfter);
                    return (content, options, layout);
                }, navigationToken);

                navigationToken.ThrowIfCancellationRequested();
                if (_disposed || version != Volatile.Read(ref _navigationVersion))
                {
                    return;
                }

                if (readingAnchor is not null && _layout is { Pages.Count: > 0 }
                    && (_pageIndex != initialPage || Math.Abs(_scrollOffset - initialScroll) > 0.001))
                    _pendingReadingAnchor = CaptureViewportReadingAnchor();

                lock (_engineGate)
                {
                    var previous = _engine;
                    _engine = navigationEngine;
                    navigationEngine = null;
                    previous?.Dispose();
                }
                _content = composed.content;
                BuildTableTextRanges(composed.content);
                _loadedFootnoteFragment = string.IsNullOrWhiteSpace(fragment) ? null : NormalizeFragment(fragment);
                _options = composed.options;
                _layout = composed.layout;
                RetainInstalledLayoutFonts();
                InvalidateSpeechTextSnapshot();
                _loadedParagraphIndent = settings.ParagraphIndent;
                _composePending = false;
                _bitmapDirty = true;
                _pageIndex = 0;
                if (Math.Abs(width - ComposePageWidth(Bounds.Width)) > 0.01
                    || Math.Abs(height - Math.Max(1, Bounds.Height)) > 0.01)
                    await (RelayoutTask = RecomposeAsync());
                navigationToken.ThrowIfCancellationRequested();
                if (_disposed || version != Volatile.Read(ref _navigationVersion)) return;
                ApplyPendingPosition();
                PrefetchVisibleImages();
                InvalidateVisual();
                EmitScroll();
                NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, true));
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException) when (navigationToken.IsCancellationRequested)
            {
                // A superseded request must not complete a newer waiter for
                // the same URI or report a spurious chapter-load failure.
                if (!_disposed && version == Volatile.Read(ref _navigationVersion))
                    _composePending = false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Libzora] navigation failed {chapterPath}: {exception.Message}");
                if (!_disposed && version == Volatile.Read(ref _navigationVersion))
                {
                    _composePending = false;
                    NavigationCompleted?.Invoke(this, new ReaderNavigationCompletedEventArgs(navigationSource, false));
                }
            }
            finally
            {
                navigationEngine?.Dispose();
                fontUse?.Dispose();
                completion.TrySetResult(false);
            }
        });
        return completion.Task;
    }

    private TypesetEngine Engine
    {
        get
        {
            lock (_engineGate)
            {
                if (_engine is null)
                {
                    _engine = new TypesetEngine(FontLibrary, ownsFonts: false);
                }

                return _engine;
            }
        }
    }

    internal TypesetFontLibrary FontLibrary
    {
        get
        {
            lock (_fontLibraryGate)
            {
                return _fontLibrary ??= CreateFontLibrary();
            }
        }
    }

    private ChapterLayout Compose(
        ChapterContent content,
        TypesetLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_engineGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Engine.Compose(content, options, cancellationToken);
        }
    }

    internal static string GetBundledFontPath() => OperatingSystem.IsAndroid()
        ? "/system/fonts/NotoSerifCJK-Regular.ttc"
        : Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", BundledFontFileName);

    /// <summary>
    /// Faces bundled next to the reading font for glyphs the CJK face does not
    /// cover. iOS cannot read the system font directory, so these are the only
    /// fallbacks available on device.
    /// </summary>
    private static readonly string[] BundledFallbackFontFileNames =
    [
        "NotoEmoji.ttf",
        "NotoSansSymbols2-Regular.ttf",
    ];

    private static IEnumerable<string> GetBundledFallbackFontPaths()
    {
        var fontDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (var fileName in BundledFallbackFontFileNames)
        {
            yield return Path.Combine(fontDirectory, fileName);
        }
    }

    private static IEnumerable<string> GetReaderFontCandidatesForFamily(string family)
    {
        var normalized = family.Trim();
        if (string.Equals(normalized, ReaderFontDefaults.DefaultFamily, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, ReaderFontDefaults.BundledFamily, StringComparison.OrdinalIgnoreCase))
        {
            yield return GetBundledFontPath();
            yield break;
        }

        var windowsFontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var windowsFileNames = normalized switch
        {
            "Microsoft YaHei UI" => new[] { "msyh.ttc", "msyhl.ttc", "msyh.ttf" },
            "SimSun" => new[] { "simsun.ttc", "simsun.ttf" },
            "SimHei" => new[] { "simhei.ttf", "simhei.ttc" },
            "KaiTi" => new[] { "simkai.ttf", "simkai.ttc" },
            "DengXian" => new[] { "Deng.ttf", "Dengl.ttf", "Dengb.ttf" },
            _ => Array.Empty<string>(),
        };
        if (!string.IsNullOrWhiteSpace(windowsFontDirectory))
        {
            foreach (var fileName in windowsFileNames)
                yield return Path.Combine(windowsFontDirectory, fileName);
        }

        var platformPaths = normalized switch
        {
            "Source Han Serif SC" => new[]
            {
                "/usr/share/fonts/opentype/source-han-serif/SourceHanSerifSC-Regular.otf",
                "/usr/share/fonts/opentype/source-han-serif/SourceHanSerifCN-Regular.otf",
                "/usr/share/fonts/truetype/adobe-source-han-serif/SourceHanSerifSC-Regular.otf",
                "/System/Library/Fonts/SourceHanSerifSC-Regular.otf",
            },
            "Noto Serif CJK SC" => new[]
            {
                "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc",
                "/usr/share/fonts/truetype/noto/NotoSerifCJK-Regular.ttc",
                "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.otf",
            },
            _ => Array.Empty<string>(),
        };
        foreach (var path in platformPaths)
            yield return path;
    }

    private static string? ResolveReaderFontPath(string? family)
    {
        if (string.IsNullOrWhiteSpace(family))
            return GetBundledFontPath();

        // Platform faces have no file path; SelectReaderMainFont asks the
        // font library to load them through the platform font manager.
        if (TypesetFontLibrary.IsSystemFontKey(family))
            return null;

        // Catalog and imported fonts live in the app container. Their
        // absolute paths are rebuilt at launch because iOS changes the
        // container path on reinstall and update.
        if (ReaderFontRegistry.TryResolve(family, out var userFontPath))
            return userFontPath;

        foreach (var part in family.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in GetReaderFontCandidatesForFamily(part))
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetReaderFontCandidates()
    {
        var paths = new List<string>(GetBundledFallbackFontPaths());
        var windowsFontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrWhiteSpace(windowsFontDirectory))
        {
            paths.AddRange(new[]
            {
                "segoeui.ttf",
                "msyh.ttc",
                "msyhl.ttc",
                "msyh.ttf",
                "simsun.ttc",
                "simsun.ttf",
                "simhei.ttf",
                "simhei.ttc",
                "simkai.ttf",
                "simkai.ttc",
                "Deng.ttf",
                "Dengl.ttf",
                "Dengb.ttf",
                "seguiemj.ttf",
                "seguisym.ttf",
                "malgun.ttf",
            }.Select(fileName => Path.Combine(windowsFontDirectory, fileName)));
        }

        paths.AddRange(new[]
        {
            // Android
            "/system/fonts/NotoSansCJK-Regular.ttc",
            "/system/fonts/Roboto-Regular.ttf",
            "/system/fonts/NotoColorEmoji.ttf",
            "/system/fonts/NotoSansSymbols-Regular.ttf",
            "/system/fonts/NotoSansSymbols2-Regular.ttf",
            // Linux
            "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
            "/usr/share/fonts/truetype/noto/NotoSansSymbols2-Regular.ttf",
            "/usr/share/fonts/opentype/noto/NotoSerifCJK-Regular.ttc",
            "/usr/share/fonts/truetype/noto/NotoSerifCJK-Regular.otf",
            "/usr/share/fonts/opentype/source-han-serif/SourceHanSerifSC-Regular.otf",
            "/usr/share/fonts/opentype/source-han-serif/SourceHanSerifCN-Regular.otf",
            "/usr/share/fonts/truetype/adobe-source-han-serif/SourceHanSerifSC-Regular.otf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            // macOS. Apple Color Emoji is intentionally not listed: the face is
            // ~190 MB and the bundled Noto Emoji covers the same characters.
            "/System/Library/Fonts/PingFang.ttc",
            "/System/Library/Fonts/Hiragino Sans GB.ttc",
            "/System/Library/Fonts/Apple Symbols.ttf",
            "/System/Library/Fonts/AppleSDGothicNeo.ttc",
            "/System/Library/Fonts/SourceHanSerifSC-Regular.otf",
        });

        return paths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TypesetFontLibrary CreateFontLibrary()
    {
        var main = GetBundledFontPath();
        if (!File.Exists(main))
        {
            throw new InvalidOperationException($"Bundled reader font is missing: {main}");
        }

        var fonts = new List<string>();
        foreach (var candidate in GetReaderFontCandidates())
        {
            if (!string.Equals(candidate, main, StringComparison.OrdinalIgnoreCase)
                && !fonts.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                fonts.Add(candidate);
            }
        }

        return new TypesetFontLibrary(main, fonts);
    }

    private void SelectReaderMainFont(string? family)
    {
        lock (_engineGate)
        {
            SelectReaderMainFont(Engine, family);
        }
    }

    private static void SelectReaderMainFont(TypesetEngine engine, string? family)
    {
        if (!string.IsNullOrWhiteSpace(family)
            && string.Equals(
                family.Trim(),
                ReaderFontDefaults.SystemDefaultFamily,
                StringComparison.OrdinalIgnoreCase))
        {
            // "System default" is resolved per platform so the same synced
            // preference picks PingFang SC on Apple platforms, Microsoft
            // YaHei UI on Windows and Noto CJK on Linux.
            foreach (var defaultFamily in ReaderFontDefaults.GetSystemDefaultFamilies())
            {
                if (engine.Fonts.TryEnsureSystemFont(defaultFamily, out var defaultKey)
                    && engine.Fonts.TrySetMainFont(defaultKey))
                {
                    return;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(family) && TypesetFontLibrary.IsSystemFontKey(family))
        {
            var systemFamily = family[ReaderFontDefaults.SystemFamilyPrefix.Length..].Trim();
            if (engine.Fonts.TryEnsureSystemFont(systemFamily, out var systemKey)
                && engine.Fonts.TrySetMainFont(systemKey))
            {
                return;
            }
        }

        var selected = ResolveReaderFontPath(family) ?? GetBundledFontPath();
        // Imported/user fonts are not part of the fallback candidate list, so
        // register the chosen face before making it the main one.
        engine.Fonts.TryEnsureFont(selected);
        engine.Fonts.TrySetMainFont(selected);
    }

    private static TypesetLayoutOptions BuildOptions(
        ReaderLayoutSettings settings,
        double width,
        double height,
        bool collectDebugBoxes = false)
    {
        var vertical = settings.VerticalWriting;
        var horizontalInset = Math.Max(0, settings.BodyPadding);
        var verticalInset = settings.BodyPadding;
        if (vertical)
        {
            (horizontalInset, verticalInset) = ReaderPlatformLayoutPolicy.GetVerticalPageInsets(
                width,
                height,
                settings.BodyPadding,
                settings.MaxWidth);
        }
        else
        {
            // MaxWidth is the readable body width, not the bitmap width. Keep
            // it centered in the viewport while retaining the requested
            // padding on narrow windows.
            var available = Math.Max(1, width - horizontalInset * 2);
            var contentWidth = Math.Min(available, Math.Max(1, settings.MaxWidth));
            horizontalInset = Math.Max(horizontalInset, (width - contentWidth) / 2);
        }

        return new TypesetLayoutOptions
        {
            WritingMode = vertical ? TypesetWritingMode.VerticalRl : TypesetWritingMode.HorizontalTb,
            BaseFontSize = 16f * (float)settings.FontScale,
            LineHeight = (float)settings.LineHeight,
            ParagraphIndent = settings.ParagraphIndent,
            ParagraphSpacingEm = (float)settings.ParagraphSpacing,
            ParagraphSpacingMatchesLineHeight = settings.ParagraphSpacingMatchesLineHeight,
            OverridePublisherLineHeight = settings.OverridePublisherLineHeight,
            OverridePublisherParagraphSpacing = settings.OverridePublisherParagraphSpacing,
            NaturalLatinTypography = settings.NaturalLatinTypography,
            HyphenateEnglish = settings.HyphenateEnglish,
            AvoidWidowsAndOrphans = settings.AvoidWidowsAndOrphans,
            ViewportWidth = (float)width,
            ViewportHeight = (float)height,
            InsetHorizontal = (float)horizontalInset,
            InsetVertical = (float)verticalInset,
            InsetBottom = (float?)settings.BodyBottomPadding,
            CollectDebugBoxes = collectDebugBoxes,
        };
    }

    /// <summary>
    /// Measures every EPUB chapter with the same loader, font library and
    /// viewport geometry as the visible native reader. The toolbar must not
    /// use the old plain-text estimate: images, footnotes, headings and
    /// vertical Latin runs all affect the real page count.
    /// </summary>
    public int[] EstimateChapterPageCounts(IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings, double width, double height,
        CancellationToken cancellationToken, (string ChapterPath, int PageCount)? knownChapter) =>
        EstimatePageCounts(chapterPaths, settings, width, height, cancellationToken, FontLibrary, knownChapter);

    internal static int[] EstimatePageCounts(
        IReadOnlyList<string> chapterPaths,
        ReaderLayoutSettings settings,
        double width,
        double height,
        CancellationToken cancellationToken,
        TypesetFontLibrary? sharedFonts = null,
        (string ChapterPath, int PageCount)? knownChapter = null)
    {
        var cacheKey = BuildPageCountCacheKey(
            chapterPaths, settings, width, height, knownChapter);
        lock (PageCountCacheLock)
        {
            if (PageCountCache.TryGetValue(cacheKey, out var cached))
            {
                cached.LastUse = ++_pageCountCacheTick;
                return cached.Counts.ToArray();
            }
        }

        var counts = EstimatePageCountsCore(
            chapterPaths, settings, width, height, cancellationToken, sharedFonts, knownChapter);
        cancellationToken.ThrowIfCancellationRequested();

        lock (PageCountCacheLock)
        {
            PageCountCache[cacheKey] = (counts.ToArray(), ++_pageCountCacheTick);
            while (PageCountCache.Count > 4)
            {
                long oldestUse = long.MaxValue;
                long? oldestKey = null;
                foreach (var entry in PageCountCache)
                {
                    if (entry.Value.LastUse >= oldestUse) continue;
                    oldestUse = entry.Value.LastUse;
                    oldestKey = entry.Key;
                }
                if (oldestKey is not { } key) break;
                PageCountCache.Remove(key);
            }
        }
        return counts;
    }
}
