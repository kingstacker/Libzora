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
/// TOC, typography, theme and settings.
/// </summary>
public partial class ReaderPage
{
    private async Task CopyAsync(string text)
    {
        ClearSelection();
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        _shell.ShowToast(UiText.Get("已复制"));
    }

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => _sliderActive = true;

    private void OnSliderValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_sliderActive) PercentText.Text = $"{e.NewValue:0}%";
    }

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The slider applies the final thumb position after this tunnel handler.
        Dispatcher.UIThread.Post(() =>
        {
            _sliderActive = false;
            _ = SeekAsync(ProgressSlider.Value);
        });
    }

    private async Task SeekAsync(double percent)
    {
        _tts?.NotifyManualNavigation();
        try
        {
            if (_isPdf)
            {
                await GoToPdfPageAsync((int)Math.Round(percent / 100 * _pdfPageCount));
                return;
            }

            if (_readerHost is not NativeReaderHost host || _epubDocument is null) return;
            var position = percent / 100 * _epubDocument.Chapters.Count;
            var chapter = Math.Min((int)position, _epubDocument.Chapters.Count - 1);
            var chapterRatio = Math.Clamp(position - chapter, 0, 1);
            if (chapter != _chapterIndex)
                await GoToChapterAsync(host, chapter, fromEnd: false, chapter - _chapterIndex);
            // A different chapter starts at page zero, which is exact even in
            // a partially composed layout. Larger ratios need the full page
            // table, but only for the destination chapter: completing the
            // chapter being left first would double the wait.
            if (!host.IsLayoutComplete && chapterRatio > 0)
                await EnsureLayoutCompleteAsync();
            host.SeekToRatio(chapterRatio);
            UpdateEpubLabels();
            _ = SaveProgressSoonAsync(force: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    private void OnTocClick(object? sender, RoutedEventArgs e) => ShowToc();

    public void ShowToc()
    {
        HideSelectionBar();
        var selected = CurrentTocIndex();
        var items = _tocEntries
            .Select((entry, index) => new ChoiceItem(entry.DisplayTitle, null, index == selected, entry.Level, entry))
            .ToList();
        _shell.ShowSheet(_book.Title, new ChoiceSheet(items, choice =>
        {
            if (choice.Tag is TocEntry entry) _ = JumpToAsync(entry);
        }), ThemeScope.RequestedThemeVariant);
    }

    /// <summary>
    /// The entry for the current chapter or PDF page. Pages without their own
    /// entry, such as illustrations, belong to the nearest preceding entry.
    /// </summary>
    private int CurrentTocIndex()
    {
        var current = _isPdf ? _pdfPage - 1 : _chapterIndex;
        var selected = _tocEntries.FindIndex(entry => entry.ChapterIndex == current);
        return selected >= 0 ? selected : _tocEntries.FindLastIndex(entry => entry.ChapterIndex < current);
    }

    private async void OnPreviousClick(object? sender, RoutedEventArgs e) => await MoveChapterAsync(-1);

    private async void OnNextClick(object? sender, RoutedEventArgs e) => await MoveChapterAsync(1);

    private void OnTypographyClick(object? sender, RoutedEventArgs e) => ShowTypography();

    public void ShowTypography()
    {
        HideSelectionBar();
        _shell.ShowSheet(_isPdf ? UiText.Get("显示") : UiText.Get("排版"), new ReaderSettingsSheet(_shell.Preferences, !_isPdf, OnReaderSettingsChanged),
            ThemeScope.RequestedThemeVariant);
    }

    private ReaderTheme CurrentTheme => _shell.Preferences.Current.ResolveReaderTheme(
        _nativeAppAppearanceDark ?? (_shell.ActualThemeVariant == ThemeVariant.Dark));

    public bool IsPowerSavingActive => _shell.Preferences.Current.ResolvePowerSaving(
        _shell.Services.IsSystemLowPowerMode?.Invoke() == true);

    private void ApplyPowerSavingToHost()
    {
        var powerSaving = IsPowerSavingActive;
        if (_readerHost is NativeReaderHost epub) epub.SetPowerSavingMode(powerSaving);
        else if (_readerHost is NativePdfReaderHost pdf) pdf.SetPowerSavingMode(powerSaving);
        if (powerSaving)
        {
            _bookPageCountCancellation?.Cancel();
            StopReadingStatsTimer();
        }
    }

    public void RefreshPowerSavingState()
    {
        if (_disposed) return;
        ApplyPowerSavingToHost();
        RefreshReaderAppearance();
    }

    private double PdfZoom => _shell.Preferences.Current.GetEffectivePdfZoom(_bookFile?.Id);

    private bool IsPdfZoomed => _isPdf && PdfZoom > 1.01;

    private bool IsPdfContinuous => _isPdf && _shell.Preferences.Current.PdfDisplayMode == 1;

    private bool IsPdfPannable => _isPdf && (IsPdfZoomed || IsPdfContinuous || _shell.Preferences.Current.PdfFitMode == 1);

    private PdfReaderDisplayMode DesiredPdfDisplayMode => _shell.Preferences.Current.PdfDisplayMode switch
    {
        1 => PdfReaderDisplayMode.Continuous,
        2 => PdfReaderDisplayMode.TwoPage,
        _ => PdfReaderDisplayMode.SinglePage,
    };

    private PdfReaderFitMode DesiredPdfFitMode => _shell.Preferences.Current.PdfFitMode == 1
        ? PdfReaderFitMode.Width
        : PdfReaderFitMode.Page;

    /// <summary>A zoomed or continuous page needs finger panning instead of swipe page turns.</summary>
    private async Task ApplyPdfZoomAsync(NativePdfReaderHost host, bool preserveZoomAnchor = false)
    {
        var desiredDisplay = DesiredPdfDisplayMode;
        if (host.DisplayMode != desiredDisplay)
        {
            await host.SetDisplayModeAsync(desiredDisplay);
        }

        var desiredAutoCrop = _shell.Preferences.Current.PdfAutoCrop;
        if (host.AutoCropMargins != desiredAutoCrop)
        {
            await host.SetAutoCropMarginsAsync(desiredAutoCrop);
        }

        var desiredFit = DesiredPdfFitMode;
        var targetZoom = PdfZoom;
        if (host.FitMode != desiredFit)
        {
            await host.SetFitModeAsync(desiredFit);
        }

        host.SetHandToolMode(IsPdfPannable);
        if (targetZoom > 1.01)
        {
            if (!preserveZoomAnchor && Math.Abs(host.Zoom - targetZoom) > 0.01)
            {
                await host.SetZoomAsync(targetZoom);
            }
        }
        else if (Math.Abs(host.Zoom - 1) > 0.01)
        {
            await host.SetFitModeAsync(desiredFit);
        }
    }

    private void ApplyTheme()
    {
        var dark = CurrentTheme == ReaderTheme.Ink;
        var paper = ReaderPalette.For(CurrentTheme, IsPowerSavingActive).Page;
        ThemeScope.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Surface.Background = new SolidColorBrush(paper);
        _shell.SetPaperSystemBars(paper, dark);
    }

    private void OnAppThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!_usesNativeFrontend && !_disposed) RefreshReaderAppearance();
    }

    private void RefreshReaderAppearance()
    {
        ApplyPowerSavingToHost();
        ApplyTheme();
        ReaderFooter.IsVisible = !_usesNativeFrontend
            && _shell.Preferences.Current.ReaderProgressDisplay != MobileReaderProgressDisplay.Off;
        (_readerHost as IReaderAppearanceHost)?.SetAppearance(new ReaderAppearanceSettings { Theme = CurrentTheme });
        // Preserve the sheet's scroll position while the page updates live.
        _shell.SetSheetTheme(ThemeScope.RequestedThemeVariant);
    }

    private async void OnReaderSettingsChanged() => await ApplyReaderSettingsAsync();

    private async Task ApplyReaderSettingsAsync()
    {
        RefreshReaderAppearance();
        if (_readerHost is NativePdfReaderHost pdf)
        {
            // Keep per-book zoom synced if user picked a preset in Display Settings.
            if (Math.Abs(pdf.Zoom - _shell.Preferences.Current.PdfZoom) > 0.01 && _bookFile is not null)
            {
                _shell.Preferences.Update(value => value.WithPdfZoom(_bookFile.Id, value.PdfZoom));
            }
            await ApplyPdfZoomAsync(pdf);
        }

        var layout = BuildLayout(_shell.Preferences.Current);
        if (layout == _layout)
        {
            RefreshPositionLabels();
            return;
        }
        _tts?.NotifyManualNavigation();
        _layout = layout;
        if (_readerHost is not IReaderConfiguredHost configured)
        {
            RefreshPositionLabels();
            return;
        }
        try
        {
            // The host owns the live position, including while an earlier reflow is pending.
            await configured.Configure(
                _layout,
                _epubPosition,
                fragment: null,
                restoreFromProgress: false,
                showVerticalDebugBoxes: false);
            UpdateEpubLabels();
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private ReaderBookmark? FindCurrentBookmark()
    {
        var chapter = _isPdf ? _pdfPage - 1 : _chapterIndex;
        var position = _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue);
        return _bookmarks.Find(mark => mark.ChapterIndex == chapter && mark.ScrollPosition == position);
    }

    private async void OnBookmarkClick(object? sender, RoutedEventArgs e) => await ToggleBookmarkAsync();

    private bool _bookmarkBusy;

    public async Task ToggleBookmarkAsync()
    {
        // A second tap while the first save runs would add the same mark twice.
        if (_bookmarkBusy || !_ready) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null || _bookFile is null) return;
        _bookmarkBusy = true;
        try
        {
            if (FindCurrentBookmark() is { } existing)
            {
                await _modules.Reader.Data.DeleteBookmarkAsync(existing.Id, _lifetime.Token);
                _bookmarks.Remove(existing);
                BookmarkIcon.Filled = false;
                NotifyNativeState();
                _shell.ShowToast(UiText.Get("已移除书签"));
                return;
            }

            var title = _isPdf
                ? UiText.Get("第 {0} 页", _pdfPage)
                : _epubDocument?.ChapterTitles.ElementAtOrDefault(_chapterIndex) ?? UiText.Get("当前章节");
            var bookmark = new ReaderBookmark
            {
                BookId = _book.Id,
                BookFileId = _bookFile.Id,
                ChapterPath = _isPdf ? $"{PdfPageChapterPrefix}{_pdfPage}" : ChapterPathAt(_chapterIndex),
                ChapterIndex = _isPdf ? _pdfPage - 1 : _chapterIndex,
                ScrollPosition = _isPdf ? 0 : (int)Math.Clamp(_epubPosition, 0, int.MaxValue),
                FlowMode = 1,
                Title = title,
                Quote = CurrentPageQuote() ?? UiText.Get("进度 {0:0}%", _overallProgress * 100)
            };
            await _modules.Reader.Data.SaveBookmarkAsync(bookmark, _lifetime.Token);
            _bookmarks.Add(bookmark);
            BookmarkIcon.Filled = true;
            NotifyNativeState();
            _shell.ShowToast(UiText.Get("已添加书签"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }        finally
        {
            _bookmarkBusy = false;
        }
    }
}
