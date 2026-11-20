using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Opening EPUB/PDF and chapter navigation.
/// </summary>
public partial class ReaderPage
{
    private async Task OpenEpubAsync(string sourcePath, string format, CancellationToken cancellationToken)
    {
        _isPdf = false;
        LoadingDetailText.Text = UiText.Get("正在整理章节与图片…");

        _epubPath = sourcePath;
        if (format is "azw3" or "mobi")
        {
            var sourceHash = string.IsNullOrWhiteSpace(_bookFile!.Sha256)
                ? await Hashing.Sha256Async(sourcePath, cancellationToken)
                : _bookFile.Sha256;
            var converted = await _modules.Reader.FormatCache.PrepareEpubAsync(
                sourcePath, sourceHash, format, cancellationToken);
            _epubPath = converted.EpubPath;
        }

        var contentHash = string.IsNullOrWhiteSpace(_bookFile!.Sha256)
            ? await Hashing.Sha256Async(_epubPath, cancellationToken)
            : _bookFile.Sha256;
        _epubDocument = await _modules.Reader.Preparation.PrepareAsync(
            _epubPath, contentHash, cancellationToken);
        if (_epubDocument.Chapters.Count == 0)
            throw new InvalidDataException("EPUB 中没有可阅读章节。");

        var savedProgress = _jump ?? await _modules.Reader.Data.GetProgressAsync(_bookFile.Id, cancellationToken);
        var restore = ResolvePosition(savedProgress);
        _chapterIndex = Math.Clamp(restore?.ChapterIndex ?? 0, 0, _epubDocument.Chapters.Count - 1);
        _epubPosition = restore?.ScrollPosition ?? 0;

        // Keep the current text visible after reflow, without inserting a hard
        // page break at the old page's first word and leaving the preceding page empty.
        var host = new NativeReaderHost { Focusable = false, ReaderObjectsEnabled = _usesNativeFrontend };
        _readerHost = host;
        host.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        host.WebMessageReceived += OnReaderWebMessage;
        host.LayoutCompleted += OnReaderLayoutCompleted;
        ReaderHostSlot.Content = host.View;
        BuildEpubToc();

        var chapterPath = _epubDocument.Chapters[_chapterIndex];
        var target = new Uri(chapterPath);
        if (restore is { ScrollPosition: <= 0 } && !string.IsNullOrWhiteSpace(restore.Fragment))
            target = new Uri(target.AbsoluteUri + "#" + Uri.EscapeDataString(restore.Fragment));

        // A saved text offset lets the first pass stop after the page the
        // reader will actually see. Legacy pixel/page positions have no text
        // offset and still require the whole chapter before restoring exactly.
        var composeOffset = restore?.ContentPosition is { TextOffset: >= 0 } saved
            ? saved.TextOffset
            : (int?)null;
        var allowPartialCompose = restore is null || composeOffset is not null;
        if (!await NavigateEpubAsync(
                host, target, cancellationToken, composeOffset,
                allowPartialCompose: allowPartialCompose,
                startLayoutCompletion: false))
            throw new InvalidOperationException("阅读器无法加载 EPUB 章节。");

        if (restore is not null)
        {
            await host.Configure(
                _layout,
                restore.ScrollPosition,
                restore.Fragment,
                restoreFromProgress: true,
                showVerticalDebugBoxes: false,
                contentPosition: restore.ContentPosition);
        }

        _annotations.AddRange(await _modules.Reader.Data.GetAnnotationsAsync(_bookFile.Id, cancellationToken));
        host.SetAnnotations(_annotations.ToList());
        UpdateEpubLabels();
        host.CompleteLayoutInBackground();
        LoadingPanel.IsVisible = false;
        _ready = true;
        NotifyNativeState();
    }

    /// <summary>
    /// Progress travels between devices, so a chapter is stored relative to the
    /// book. Older mobile builds stored a device path; those fall back to the
    /// chapter index, which stays valid everywhere.
    /// </summary>
    private ReaderProgressRow? ResolvePosition(ReaderProgressRow? saved)
    {
        if (_epubDocument is not { } document || saved is null) return null;
        if (ReaderResourcePolicy.RestoreProgress(document, saved) is { } restored) return restored;
        if (saved.ChapterIndex < 0 || saved.ChapterIndex >= document.Chapters.Count) return null;
        return saved with
        {
            ChapterPath = ChapterPathAt(saved.ChapterIndex),
            Fragment = null,
            ContentPosition = ReaderContentPosition.Validate(saved.ContentPosition)
        };
    }

    private string CurrentChapterPath =>
        _isPdf ? $"{PdfPageChapterPrefix}{_pdfPage}" : ChapterPathAt(_chapterIndex);

    private string ChapterPathAt(int index) =>
        _epubDocument is { } document && index >= 0 && index < document.Chapters.Count
            ? Path.GetRelativePath(document.RootPath, document.Chapters[index]).Replace('\\', '/')
            : string.Empty;

    private async Task OpenPdfAsync(string path, CancellationToken cancellationToken)
    {
        _isPdf = true;
        TypographyLabel.Text = UiText.Get("显示");
        LoadingDetailText.Text = UiText.Get("正在渲染 PDF 页面…");

        var savedProgress = _jump ?? await _modules.Reader.Data.GetProgressAsync(_bookFile!.Id, cancellationToken);
        var initialPage = Math.Max(1, (savedProgress?.ChapterIndex ?? 0) + 1);

        var host = new NativePdfReaderHost { Focusable = false };
        _readerHost = host;
        host.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        host.WebMessageReceived += OnReaderWebMessage;
        ReaderHostSlot.Content = host.View;

        _pdfPageCount = await host.PrepareDocumentAsync(path, cancellationToken);
        if (_pdfPageCount <= 0) throw new InvalidOperationException("这个 PDF 没有可显示的页面。");
        _annotations.AddRange(await _modules.Reader.Data.GetAnnotationsAsync(_bookFile!.Id, cancellationToken));
        host.SetAnnotations(_annotations.ToList());
        // Sync global PdfZoom chip with this book's remembered zoom before applying.
        var bookZoom = _shell.Preferences.Current.GetEffectivePdfZoom(_bookFile!.Id);
        if (Math.Abs(_shell.Preferences.Current.PdfZoom - bookZoom) > 0.01)
        {
            _shell.Preferences.Update(value => value with { PdfZoom = bookZoom });
        }

        await host.SetDisplayModeAsync(DesiredPdfDisplayMode);
        await host.SetFitModeAsync(DesiredPdfFitMode);
        if (_shell.Preferences.Current.PdfAutoCrop)
        {
            await host.SetAutoCropMarginsAsync(true);
        }
        initialPage = Math.Clamp(initialPage, 1, _pdfPageCount);
        var target = new Uri(new Uri(path).AbsoluteUri + $"#page={initialPage}");
        if (!await host.NavigateAsync(target, cancellationToken))
            throw new InvalidOperationException(host.LastError ?? "阅读器无法加载 PDF。");

        await ApplyPdfZoomAsync(host);
        _pdfPage = host.PageNumber;
        BuildPdfToc(host.CachedDocumentInfo);
        UpdatePdfLabels();
        LoadingPanel.IsVisible = false;
        _ready = true;
        NotifyNativeState();
    }

    /// <summary>
    /// The index changes before navigating because position messages from the
    /// new chapter arrive while it loads; a failed load puts it back so labels
    /// and saved progress keep describing the chapter still on screen.
    /// </summary>
    private async Task<bool> NavigateToChapterAsync(NativeReaderHost host, int chapter, Uri target)
    {
        var previous = _chapterIndex;
        _chapterIndex = chapter;
        var navigated = false;
        try
        {
            navigated = await NavigateEpubAsync(host, target, _lifetime.Token);
            return navigated;
        }
        finally
        {
            if (!navigated) _chapterIndex = previous;
        }
    }

    private async Task<bool> NavigateEpubAsync(
        NativeReaderHost host,
        Uri target,
        CancellationToken cancellationToken,
        int? composeTextOffset = null,
        bool allowPartialCompose = true,
        bool startLayoutCompletion = true)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ReaderNavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (!UriEquals(args.Request, target)) return;
            completion.TrySetResult(args.IsSuccess);
        };
        host.NavigationCompleted += handler;
        try
        {
            host.Navigate(
                target,
                _layout,
                showVerticalDebugBoxes: false,
                composeTextOffset,
                allowPartialCompose);
            var navigated = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (navigated && startLayoutCompletion) host.CompleteLayoutInBackground();
            return navigated;
        }
        finally
        {
            host.NavigationCompleted -= handler;
        }
    }

    private static bool UriEquals(Uri? left, Uri right) =>
        left is not null
        && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private void OnReaderWebMessage(object? sender, ReaderWebMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Body)) return;
        try
        {
            using var json = JsonDocument.Parse(e.Body);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            if (type.GetString() == "scroll")
            {
                var top = root.TryGetProperty("top", out var topElement) ? topElement.GetDouble() : 0;
                var left = root.TryGetProperty("left", out var leftElement) ? leftElement.GetDouble() : 0;
                _lastScrollTop = top;
                _lastScrollLeft = left;
                _epubPosition = Math.Max(0, left);
                DismissFootnoteReturnIfReaderMoved(top, left);
                UpdateEpubLabels();
                _ = SaveProgressSoonAsync(force: false);
            }
            else if (type.GetString() == "link")
            {
                _tapConsumed = true;
                if (_usesNativeFrontend
                    && root.TryGetProperty("footnote", out var footnote)
                    && footnote.ValueKind == JsonValueKind.True)
                    _ = ShowFootnoteAsync(root);
                else
                    _ = HandleLinkAsync(
                        root.TryGetProperty("href", out var href) ? href.GetString() : null,
                        root.TryGetProperty("footnoteText", out var note) && note.ValueKind == JsonValueKind.String
                            ? note.GetString()
                            : null);
            }
            // The PDF surface reports a first tap on a highlight as a selection.
            else if (type.GetString() is "annotationClick" or "annotationSelect")
            {
                _tapConsumed = true;
                if (root.TryGetProperty("id", out var id) && id.TryGetGuid(out var annotationId))
                    ShowAnnotation(annotationId);
            }
            else if (type.GetString() == "readerObject" && _usesNativeFrontend)
            {
                _tapConsumed = true;
                OpenReaderObject(root);
            }
            else if (type.GetString() == "selection" && _expectSelection)
            {
                _expectSelection = false;
                if (root.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } selected)
                {
                    ShowSelection(new ReaderSelection(
                        selected,
                        root.GetProperty("startOffset").GetInt32(),
                        root.GetProperty("endOffset").GetInt32(),
                        root.GetProperty("prefix").GetString() ?? string.Empty,
                        root.GetProperty("suffix").GetString() ?? string.Empty,
                        ReadDouble(root, "x"),
                        ReadDouble(root, "y"),
                        ReadDouble(root, "bottom")));
                }
            }
            else if (type.GetString() == "pdfPage")
            {
                _pdfPage = root.TryGetProperty("page", out var pageElement)
                    ? pageElement.GetInt32()
                    : _pdfPage;
                UpdatePdfLabels();
                _ = SaveProgressSoonAsync(force: false);
            }
            else if (type.GetString() == "pdfLink")
            {
                _tapConsumed = true;
                var targetPage = root.TryGetProperty("page", out var pageProp) && pageProp.ValueKind == JsonValueKind.Number
                    ? pageProp.GetInt32()
                    : 0;
                var targetTop = root.TryGetProperty("top", out var topProp) && topProp.ValueKind == JsonValueKind.Number
                    ? topProp.GetDouble()
                    : (double?)null;
                var uriText = root.TryGetProperty("uri", out var uriProp) && uriProp.ValueKind == JsonValueKind.String
                    ? uriProp.GetString()
                    : null;
                if (targetPage >= 1 && targetPage <= _pdfPageCount)
                {
                    _ = GoToPdfPageAsync(targetPage, targetTop);
                }
                else if (!string.IsNullOrWhiteSpace(uriText))
                {
                    _ = HandleLinkAsync(uriText, null);
                }
            }
        }
        catch (JsonException)
        {
            // Reader bridge messages are advisory; malformed state must not
            // interrupt page rendering.
        }
    }

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;

    private void UpdateEpubLabels()
    {
        if (_epubDocument is null || _readerHost is not NativeReaderHost host) return;
        var title = _epubDocument.ChapterTitles.ElementAtOrDefault(_chapterIndex)
            ?? Path.GetFileNameWithoutExtension(_epubDocument.Chapters[_chapterIndex]);
        if (!host.IsLayoutComplete)
        {
            // Only the opening prefix is composed. A page-index ratio would
            // show the end of that prefix as the end of the chapter, so use
            // the visible text position until the full layout arrives.
            var ratio = Math.Clamp(
                host.CurrentTextOffset / (double)Math.Max(1, host.BodyTextLength), 0, 1);
            _overallProgress = Math.Clamp(
                (_chapterIndex + ratio) / _epubDocument.Chapters.Count, 0, 1);
            PresentPosition(title, UiText.Get("排版中"));
            return;
        }

        var pages = Math.Max(1, host.PageCount);
        var page = Math.Clamp(host.CurrentPage, 0, pages - 1);
        var chapterRatio = pages > 1 ? (double)page / (pages - 1) : 0;
        _overallProgress = Math.Clamp((_chapterIndex + chapterRatio) / _epubDocument.Chapters.Count, 0, 1);
        PresentPosition(title, BookPagePosition(page, pages));
    }
}
