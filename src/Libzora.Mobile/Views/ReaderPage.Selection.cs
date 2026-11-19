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
/// Selection bar, annotations and links.
/// </summary>
public partial class ReaderPage
{
    private async Task HandleLinkAsync(string? href, string? footnoteText)
    {
        if (string.IsNullOrWhiteSpace(href)) return;
        if (_footnoteJumpBacklink is { } backlink
            && string.Equals(NormalizeFootnoteHref(href), backlink, StringComparison.Ordinal))
        {
            _footnoteJumpBacklink = null;
            await NativeReturnFromFootnoteAsync();
            return;
        }
        if (!string.IsNullOrWhiteSpace(footnoteText))
        {
            if (_usesNativeFrontend)
            {
                NativeDialogRequested?.Invoke(new NativeReaderDialog("info", UiText.Get("注释"), footnoteText));
                return;
            }
            _shell.ShowSheet(UiText.Get("注释"), new InfoSheet(footnoteText), ThemeScope.RequestedThemeVariant);
            return;
        }

        try
        {
            if (Uri.TryCreate(href, UriKind.Absolute, out var external) && external.Scheme is "http" or "https")
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(external);
                return;
            }

            if (_readerHost is not NativeReaderHost host || _epubDocument is not { } document) return;
            _tts?.NotifyManualNavigation();
            ClearFootnoteNavigation();
            var target = new Uri(new Uri(document.Chapters[_chapterIndex]), href);
            var path = Path.GetFullPath(target.LocalPath);
            var chapter = document.Chapters.ToList().FindIndex(item =>
                string.Equals(Path.GetFullPath(item), path, StringComparison.Ordinal));
            if (chapter < 0) return;
            if (await NavigateToChapterAsync(host, chapter, target))
            {
                UpdateEpubLabels();
                _ = SaveProgressSoonAsync(force: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is UriFormatException or InvalidOperationException or TimeoutException)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private void ClearSelection()
    {
        HideSelectionBar();
        (_readerHost as IReaderSelectionHost)?.ClearSelection();
    }

    /// <summary>
    /// Shows the icon toolbar beside the selection instead of a sheet, so the
    /// selected text stays visible while choosing an action.
    /// </summary>
    private void ShowSelection(ReaderSelection selection)
    {
        var chapterPath = CurrentChapterPath;
        var overlaps = _annotations.Any(item => item.ChapterPath == chapterPath
            && selection.Start < item.EndOffset && selection.End > item.StartOffset);
        _selection = selection;
        if (_usesNativeFrontend)
        {
            NotifyNativeState();
            return;
        }
        SelectionHighlightButton.IsVisible = !overlaps;
        SelectionNoteButton.IsVisible = !overlaps;
        SelectionLayer.IsVisible = true;
        PositionSelectionBar(selection);
    }

    private void HideSelectionBar()
    {
        _selection = null;
        SelectionLayer.IsVisible = false;
        NotifyNativeState();
    }

    private void PositionSelectionBar(ReaderSelection selection)
    {
        SelectionBar.Measure(Size.Infinity);
        var size = SelectionBar.DesiredSize;
        var width = ReaderPaper.Bounds.Width;
        var height = ReaderPaper.Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // Anchor to the selection's left edge and prefer the space above it;
        // fall below when the first line is too close to the top edge.
        const double inset = 8;
        const double gap = 10;
        var left = selection.X ?? (width - size.Width) / 2;
        left = Math.Clamp(left, inset, Math.Max(inset, width - inset - size.Width));
        var top = (selection.Y ?? height / 2) - size.Height - gap;
        if (top < inset)
            top = (selection.Bottom ?? selection.Y ?? height / 2) + gap;
        top = Math.Clamp(top, inset, Math.Max(inset, height - inset - size.Height));
        Canvas.SetLeft(SelectionBar, left);
        Canvas.SetTop(SelectionBar, top);
    }

    private void OnSelectionHighlightClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        HideSelectionBar();
        _ = SaveAnnotationAsync(NewAnnotation(selection));
    }

    private void OnSelectionNoteClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        HideSelectionBar();
        PromptNote(NewAnnotation(selection));
    }

    private void OnSelectionTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        ClearSelection();
        _ = TranslateAsync(selection.Text);
    }

    private void OnSelectionAskClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        ClearSelection();
        ShowAssistant(selection.Text);
    }

    private void OnSelectionCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection) return;
        _ = CopyAsync(selection.Text);
    }

    private ReaderAnnotation NewAnnotation(ReaderSelection selection) => new()
    {
        BookId = _book.Id,
        BookFileId = _bookFile!.Id,
        ChapterPath = CurrentChapterPath,
        Fragment = _isPdf ? (_readerHost as IPdfTextHost)?.SelectionFragment : null,
        StartOffset = selection.Start,
        EndOffset = selection.End,
        SelectedText = selection.Text,
        Prefix = selection.Prefix,
        Suffix = selection.Suffix
    };

    private void PromptNote(ReaderAnnotation annotation)
    {
        _shell.ShowSheet(UiText.Get("笔记"), new PromptSheet(annotation.SelectedText, UiText.Get("保存"), async note =>
        {
            annotation.Note = note;
            _shell.HideSheet();
            await SaveAnnotationAsync(annotation);
        }, initialText: annotation.Note, placeholder: UiText.Get("写下你的想法")), ThemeScope.RequestedThemeVariant);
    }

    private async Task SaveAnnotationAsync(ReaderAnnotation annotation)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            annotation.UpdatedAt = DateTimeOffset.UtcNow;
            await _modules.Reader.Data.SaveAnnotationAsync(annotation, _lifetime.Token);
            _annotations.RemoveAll(item => item.Id == annotation.Id);
            _annotations.Add(annotation);
            (_readerHost as IReaderAnnotationHost)?.SetAnnotations(_annotations.ToList());
            ClearSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Get("保存失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private void ShowAnnotation(Guid id)
    {
        if (_annotations.Find(item => item.Id == id) is not { } annotation) return;
        if (_usesNativeFrontend)
        {
            NativeDialogRequested?.Invoke(new NativeReaderDialog("annotation", UiText.Get("批注"), annotation.DisplayQuote,
                annotation.Id, annotation.Note));
            return;
        }
        var title = annotation.DisplayQuote.Length > 24
            ? annotation.DisplayQuote[..24] + "…"
            : annotation.DisplayQuote;
        _shell.ShowSheet(title, new ChoiceSheet(
            [
                new ChoiceItem(annotation.Note.Length > 0 ? UiText.Get("编辑笔记") : UiText.Get("写笔记"), Tag: true),
                new ChoiceItem(UiText.Get("删除"), Tag: false)
            ],
            choice =>
            {
                if (choice.Tag is true)
                {
                    PromptNote(annotation);
                    return;
                }
                _shell.HideSheet();
                _ = DeleteAnnotationAsync(annotation);
            }), ThemeScope.RequestedThemeVariant);
    }

    private async Task DeleteAnnotationAsync(ReaderAnnotation annotation)
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        try
        {
            await _modules.Reader.Data.DeleteAnnotationAsync(annotation.Id, _lifetime.Token);
            _annotations.Remove(annotation);
            (_readerHost as IReaderAnnotationHost)?.SetAnnotations(_annotations.ToList());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Get("删除失败：{0}", UiText.Localize(exception.Message)));
        }
    }
}
