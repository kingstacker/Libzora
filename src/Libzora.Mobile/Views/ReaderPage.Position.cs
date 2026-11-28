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
/// Layout completion, position display and TOC building.
/// </summary>
public partial class ReaderPage
{
    private void OnReaderLayoutCompleted(object? sender, EventArgs e)
    {
        if (_disposed || !_ready) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // The completion runs on a worker; labels and the progress slider
            // must be updated on the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && _ready) UpdateEpubLabels();
            }, DispatcherPriority.Background);
            return;
        }
        UpdateEpubLabels();
    }

    /// <summary>
    /// Operations that cross the composed prefix need the whole chapter. The
    /// background pass normally finishes long before the reader turns a page,
    /// so this usually returns immediately.
    /// </summary>
    private async Task EnsureLayoutCompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new OperationCanceledException();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        request.Token.ThrowIfCancellationRequested();
        if (_readerHost is NativeReaderHost host && !host.IsLayoutComplete)
            await host.EnsureCompleteLayoutAsync().WaitAsync(request.Token);
        request.Token.ThrowIfCancellationRequested();
    }

    private string FormatPdfPageTitle(int page)
    {
        if (_readerHost is NativePdfReaderHost pdf)
        {
            var label = pdf.GetPageLabel(page);
            if (!string.IsNullOrWhiteSpace(label) && !string.Equals(label, page.ToString(), StringComparison.Ordinal))
            {
                return UiText.Get("第 {0} 页", label);
            }
        }
        return UiText.Get("第 {0} 页", page);
    }

    private void UpdatePdfLabels()
    {
        if (_pdfPageCount <= 0) return;
        _overallProgress = Math.Clamp((double)_pdfPage / _pdfPageCount, 0, 1);
        PresentPosition(FormatPdfPageTitle(_pdfPage), $"{_pdfPage} / {_pdfPageCount}");
    }

    private void PresentPosition(string title, string position)
    {
        _positionTitle = title;
        _positionPages = position;
        RefreshPositionUiText();
        if (_sliderActive) return;
        ProgressSlider.Value = _overallProgress * 100;
        // The native snapshot computes its bookmark state once; the hidden
        // Avalonia icon does not need a duplicate lookup on every scroll tick.
        if (!_usesNativeFrontend) BookmarkIcon.Filled = FindCurrentBookmark() is not null;
        NotifyNativeState();
    }

    private void RefreshPositionUiText()
    {
        var title = _isPdf ? UiText.Get("第 {0} 页", _pdfPage) : _positionTitle;
        var percent = $"{_overallProgress * 100:0}%";
        FooterChapter.Text = title;
        FooterPosition.Text = $"{ProgressDetail(_positionPages)}  ·  {percent}";
        ChapterText.Text = title;
        if (!_sliderActive) PercentText.Text = percent;
    }

    private void BuildEpubToc()
    {
        _tocEntries.Clear();
        if (_epubDocument is null) return;
        var items = _epubDocument.Navigation.Count > 0
            ? _epubDocument.Navigation
            : _epubDocument.ChapterTitles.Select((title, index) =>
                new EpubReaderNavigationItem(title, _epubDocument.Chapters[index], index));
        foreach (var item in items)
        {
            var title = string.IsNullOrWhiteSpace(item.Title)
                ? _epubDocument.ChapterTitles.ElementAtOrDefault(item.ChapterIndex)
                : item.Title;
            _tocEntries.Add(new TocEntry(
                string.IsNullOrWhiteSpace(title) ? UiText.Get("第 {0} 章", item.ChapterIndex + 1) : title,
                item.ChapterIndex,
                item.Target,
                item.Level)
            {
                UiTextTemplate = string.IsNullOrWhiteSpace(title) ? "第 {0} 章" : null
            });
        }
    }

    private void BuildPdfToc(PdfDocumentInfo? info)
    {
        _tocEntries.Clear();
        _hasPdfOutline = info?.Outline is { Count: > 0 };
        if (info?.Outline is { Count: > 0 })
        {
            foreach (var item in info.Outline)
            {
                _tocEntries.Add(new TocEntry(
                    item.Title,
                    Math.Max(0, item.PageNumber - 1),
                    null,
                    item.Level));
            }
            return;
        }

        var pdf = _readerHost as NativePdfReaderHost;
        var limit = Math.Min(_pdfPageCount, 2000);
        for (var page = 1; page <= limit; page++)
        {
            var label = pdf?.GetPageLabel(page);
            var hasCustomLabel = !string.IsNullOrWhiteSpace(label) && !string.Equals(label, page.ToString(), StringComparison.Ordinal);
            _tocEntries.Add(new TocEntry(
                hasCustomLabel ? $"{UiText.Get("第 {0} 页", label!)} ({page})" : UiText.Get("第 {0} 页", page),
                page - 1,
                null,
                0)
            {
                UiTextTemplate = hasCustomLabel ? null : "第 {0} 页"
            });
        }
    }

    private async Task JumpToAsync(TocEntry item)
    {
        BeginReaderNavigation();
        ClearFootnoteNavigation();
        ClearActiveSyncedMarker();
        _tts?.NotifyManualNavigation();
        _shell.HideSheet();
        SetChromeVisible(false);
        try
        {
            if (_isPdf)
            {
                await GoToPdfPageAsync(item.ChapterIndex + 1);
            }
            else if (_readerHost is NativeReaderHost epub && _epubDocument is not null)
            {
                var chapter = Math.Clamp(item.ChapterIndex, 0, _epubDocument.Chapters.Count - 1);
                var targetText = string.IsNullOrWhiteSpace(item.Target)
                    ? _epubDocument.Chapters[chapter]
                    : item.Target;
                if (!Uri.TryCreate(targetText, UriKind.Absolute, out var target))
                    target = new Uri(_epubDocument.Chapters[chapter]);
                if (await NavigateToChapterAsync(epub, chapter, target))
                {
                    UpdateEpubLabels();
                    _ = SaveProgressSoonAsync(force: true);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private async Task GoToPdfPageAsync(int page, double? top = null, CancellationToken cancellationToken = default)
    {
        if (_readerHost is not NativePdfReaderHost pdf) return;
        using var linked = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken)
            : null;
        var token = linked?.Token ?? _lifetime.Token;
        if (Math.Clamp(page, 1, Math.Max(1, _pdfPageCount)) != _pdfPage)
        {
            ClearActiveSyncedMarker();
        }
        // A whole-page view shows each page from its start, never a pan carried over.
        if (!IsPdfZoomed && pdf.Zoom > 1.01) await pdf.SetFitModeAsync(DesiredPdfFitMode);
        page = Math.Clamp(page, 1, _pdfPageCount);
        var path = _modules.Library.Books.GetAbsoluteFilePath(_bookFile!);
        var fragment = top is { } t && double.IsFinite(t)
            ? $"#page={page}&top={Math.Clamp(t, 0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : $"#page={page}";
        if (!await pdf.NavigateAsync(new Uri(new Uri(path).AbsoluteUri + fragment), token))
            return;
        // NavigateAsync clears hand-tool mode; restore panning when zoomed or scrolling.
        pdf.SetHandToolMode(IsPdfPannable);
        _pdfPage = pdf.PageNumber;
        UpdatePdfLabels();
        _ = SaveProgressSoonAsync(force: true);
    }
}
