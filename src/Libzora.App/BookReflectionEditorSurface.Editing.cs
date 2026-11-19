using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Libzora.Core;

namespace Libzora;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

/// <summary>
/// Text editing events, keyboard handling, paste and block splitting.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void BlockEditor_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged
            || sender is not TextBox { Tag: BlockView view } editor)
            return;

        var nextText = editor.Text ?? string.Empty;
        var previousText = view.LastPlainText;
        if (string.Equals(previousText, nextText, StringComparison.Ordinal))
            return;

        var commonStart = 0;
        while (commonStart < previousText.Length
            && commonStart < nextText.Length
            && previousText[commonStart] == nextText[commonStart])
            commonStart++;

        var commonSuffix = 0;
        while (commonSuffix < previousText.Length - commonStart
            && commonSuffix < nextText.Length - commonStart
            && previousText[previousText.Length - 1 - commonSuffix]
                == nextText[nextText.Length - 1 - commonSuffix])
            commonSuffix++;

        var oldEnd = previousText.Length - commonSuffix;
        var newEnd = nextText.Length - commonSuffix;
        var inserted = nextText[commonStart..newEnd];
        var style = GetStyleAt(view.State.Inlines, commonStart);

        var before = SliceRuns(view.State.Inlines, 0, commonStart);
        var after = SliceRuns(view.State.Inlines, oldEnd, previousText.Length);
        if (inserted.Length > 0)
            before.Add(new InlineRun(inserted, style.Style, style.LinkUrl));

        before.AddRange(after);
        view.State.Inlines = MergeRuns(before);
        view.LastPlainText = nextText;
        UpdateBlockView(view);
        RaiseContentChanged();
        UpdateSelectionToolbar(view);
    }

    private void BlockEditor_PropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: BlockView view } editor)
            return;

        if (e.Property == TextBox.SelectionStartProperty
            || e.Property == TextBox.SelectionEndProperty
            || e.Property == TextBox.CaretIndexProperty)
        {
            _activeBlock = view;
            UpdateSelectionToolbar(view);
        }
    }

    private async void BlockEditor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: BlockView view } editor)
            return;

        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        if (view.State.IsCitation)
        {
            if (e.Key is Key.Back or Key.Delete)
            {
                DeleteCitationBlock(view);
                e.Handled = true;
            }
            else if (e.Key is Key.V or Key.Insert or Key.Space or Key.Enter)
            {
                e.Handled = true;
            }
            return;
        }

        var text = editor.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), 0, text.Length);

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var pasteShortcut = (e.Key == Key.V && command)
            || (e.Key == Key.Insert && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (pasteShortcut)
        {
            e.Handled = true;
            await PasteClipboardAsync(
                view,
                editor,
                new SelectionSnapshot(view, start, end));
            return;
        }

        if (e.Key == Key.Space
            && start == end
            && end == text.Length
            && Regex.Match(text, @"^#{1,6}$") is { Success: true } headingShortcut)
        {
            view.State.Kind = (BlockKind)headingShortcut.Value.Length;
            view.State.Inlines = [];
            SetEditorText(view, string.Empty, 0);
            UpdateBlockView(view);
            e.Handled = true;
            RaiseContentChanged();
            return;
        }

        if (e.Key == Key.Enter)
        {
            SplitBlock(view, index, start, end);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back
            && start == end
            && start == 0)
        {
            MergeWithPrevious(view, index);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete
            && start == end
            && end == text.Length)
        {
            MergeWithNext(view, index);
            e.Handled = true;
        }
    }

    private MenuFlyout CreateReflectionEditorContextFlyout(BlockView view)
    {
        var editor = view.Editor!;
        var cut = new MenuItem { Header = UiText.Get("剪切") };
        var copy = new MenuItem { Header = UiText.Get("复制") };
        var paste = new MenuItem { Header = UiText.Get("粘贴") };
        var selectAll = new MenuItem { Header = UiText.Get("全选") };
        var deleteCitation = new MenuItem { Header = UiText.Get("删除引用") };

        cut.Click += (_, _) =>
        {
            editor.Focus();
            editor.Cut();
        };
        copy.Click += (_, _) =>
        {
            editor.Focus();
            editor.Copy();
        };
        paste.Click += async (_, _) =>
        {
            editor.Focus();
            await PasteClipboardAsync(view, editor);
        };
        selectAll.Click += (_, _) =>
        {
            editor.Focus();
            editor.SelectAll();
        };
        deleteCitation.Click += (_, _) => DeleteCitationBlock(view);

        var menu = new MenuFlyout();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteCitation);
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = editor.CanCut;
            copy.IsEnabled = editor.CanCopy;
            paste.IsEnabled = !editor.IsReadOnly;
            deleteCitation.IsVisible = view.State.IsCitation;
            deleteCitation.IsEnabled = view.State.IsCitation;
        };
        return menu;
    }

    private async Task PasteClipboardAsync(
        BlockView view,
        TextBox editor,
        SelectionSnapshot? selection = null)
    {
        var clipboard = TopLevel.GetTopLevel(editor)?.Clipboard;
        if (clipboard is null)
        {
            editor.Paste();
            return;
        }

        var clipboardImage = await TryGetClipboardImageAsync(clipboard);
        if (clipboardImage is null)
        {
            editor.Paste();
            return;
        }

        var text = editor.Text ?? string.Empty;
        var currentSelection = selection ?? new SelectionSnapshot(
            view,
            Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, text.Length),
            Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), 0, text.Length));
        InsertImageBlock(
            currentSelection,
            clipboardImage.Value.Source,
            clipboardImage.Value.Alt);
    }

    private static async Task<(string Source, string Alt)?> TryGetClipboardImageAsync(
        IClipboard clipboard)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await clipboard.TryGetBitmapAsync();
        }
        catch
        {
            // Some clipboard providers do not expose CF_DIB/bitmap data. A
            // copied image file is handled by the file-list fallback below.
        }

        if (bitmap is not null)
        {
            try
            {
                using (bitmap)
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                    return (
                        $"data:image/png;base64,{Convert.ToBase64String(stream.ToArray())}",
                        UiText.Get("粘贴的图片"));
                }
            }
            catch
            {
                // Fall through to copied image files, then to normal text.
            }
        }

        IReadOnlyList<IStorageItem>? files = null;
        try
        {
            files = await clipboard.TryGetFilesAsync();
        }
        catch
        {
        }

        if (files is null)
            return null;

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)
                || !File.Exists(path)
                || !IsSupportedImagePath(path))
                continue;

            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length == 0)
                    continue;

                var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
                using var validationBitmap = LoadBitmap(source);
                if (validationBitmap is null)
                    continue;

                return (
                    source,
                    Path.GetFileNameWithoutExtension(path));
            }
            catch
            {
                // Ignore an unsupported or unreadable clipboard file and
                // continue looking for another image item.
            }
        }

        return null;
    }

    private void SplitBlock(BlockView view, int index, int start, int end)
    {
        var state = view.State;
        var isList = state.Kind is BlockKind.UnorderedList or BlockKind.OrderedList;
        var beforeRuns = SliceRuns(state.Inlines, 0, start);
        var afterRuns = SliceRuns(state.Inlines, end, view.LastPlainText.Length);

        if (isList
            && beforeRuns.Count == 0
            && afterRuns.Count == 0)
        {
            state.Kind = BlockKind.Paragraph;
            state.Inlines = [];
            SetEditorText(view, string.Empty, 0);
            UpdateBlockView(view);
            AddBlock(new BlockState(), index + 1);
            FocusBlock(_blocks[index + 1], 0);
            RaiseContentChanged();
            return;
        }

        state.Inlines = beforeRuns;
        SetEditorText(view, state.PlainText, state.PlainText.Length);
        UpdateBlockView(view);

        var nextState = new BlockState
        {
            Kind = isList ? state.Kind : BlockKind.Paragraph,
            Inlines = afterRuns,
            OrderedNumber = state.Kind == BlockKind.OrderedList
                ? NextOrderedNumber(state.OrderedNumber)
                : 1
        };
        AddBlock(nextState, index + 1);
        FocusBlock(_blocks[index + 1], 0);
        RaiseContentChanged();
    }

    private void MergeWithPrevious(BlockView view, int index)
    {
        if (index == 0) return;

        if (view.State.Kind is BlockKind.UnorderedList or BlockKind.OrderedList
            && _blocks[index - 1].State.Kind is not (BlockKind.UnorderedList or BlockKind.OrderedList))
        {
            view.State.Kind = BlockKind.Paragraph;
            UpdateBlockView(view);
            FocusBlock(view, 0);
            RaiseContentChanged();
            return;
        }

        var previous = _blocks[index - 1];
        if (previous.State.Kind == BlockKind.Image)
            return;

        var caret = previous.State.PlainText.Length;
        previous.State.Inlines.AddRange(view.State.Inlines);
        previous.State.Inlines = MergeRuns(previous.State.Inlines);
        SetEditorText(previous, previous.State.PlainText, caret);
        RemoveBlock(view);
        FocusBlock(previous, caret);
        RaiseContentChanged();
    }

    private void MergeWithNext(BlockView view, int index)
    {
        if (index >= _blocks.Count - 1) return;

        var next = _blocks[index + 1];
        if (next.State.Kind == BlockKind.Image)
            return;

        var caret = view.State.PlainText.Length;
        view.State.Inlines.AddRange(next.State.Inlines);
        view.State.Inlines = MergeRuns(view.State.Inlines);
        SetEditorText(view, view.State.PlainText, caret);
        RemoveBlock(next);
        FocusBlock(view, caret);
        RaiseContentChanged();
    }

    private void SetEditorText(BlockView view, string text, int caret)
    {
        if (view.Editor is null) return;

        _suppressTextChanged = true;
        try
        {
            view.State.Inlines = view.State.Inlines.Count == 0
                ? []
                : MergeRuns(view.State.Inlines);
            view.LastPlainText = text;
            view.Editor.Text = text;
            view.Editor.CaretIndex = Math.Clamp(caret, 0, text.Length);
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void RemoveBlock(BlockView view, bool ensurePlaceholder = true)
    {
        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        _blocks.RemoveAt(index);
        _blockStack.Children.RemoveAt(index);
        view.Dispose();

        if (ensurePlaceholder && _blocks.Count == 0)
            AddBlock(new BlockState(), 0);
    }

    private void DeleteCitationBlock(BlockView view)
    {
        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        var focus = _blocks
            .Skip(index + 1)
            .Concat(_blocks.Take(index).Reverse())
            .FirstOrDefault(candidate => candidate.Editor is not null);
        RemoveBlock(view);
        FocusBlock(focus ?? _blocks.LastOrDefault(candidate => candidate.Editor is not null), 0);
        RaiseContentChanged();
    }
}
