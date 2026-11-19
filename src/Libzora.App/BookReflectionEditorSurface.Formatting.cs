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
/// Bold, links and citation insertion.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void ApplyBlockAction(string action, SelectionSnapshot? selection)
    {
        var view = selection?.Block ?? _activeBlock;
        if (view is null || view.State.IsCitation) return;

        var kind = action switch
        {
            "heading1" => BlockKind.Heading1,
            "heading2" => BlockKind.Heading2,
            "heading3" => BlockKind.Heading3,
            "heading4" => BlockKind.Heading4,
            "heading5" => BlockKind.Heading5,
            "heading6" => BlockKind.Heading6,
            "quote" => BlockKind.Quote,
            "unordered" => BlockKind.UnorderedList,
            "ordered" => BlockKind.OrderedList,
            _ => BlockKind.Paragraph
        };

        if (view.State.Kind == kind && kind != BlockKind.Paragraph)
            kind = BlockKind.Paragraph;

        view.State.Kind = kind;
        if (kind != BlockKind.Quote)
        {
            view.State.IsCitation = false;
            view.State.CitationBookTitle = null;
        }
        UpdateBlockView(view);
        CollapseTextSelection(selection, selection?.End, focusEditor: true);
        RaiseContentChanged();
    }

    private void ApplyBold(SelectionSnapshot? selection)
    {
        if (selection is null || selection.Block.State.IsCitation) return;

        var allBold = SliceRuns(
                selection.Block.State.Inlines,
                selection.Start,
                selection.End)
            .All(run => run.Style == InlineStyle.Bold);
        var style = allBold ? InlineStyle.Plain : InlineStyle.Bold;
        selection.Block.State.Inlines = ApplyInlineStyle(
            selection.Block.State.Inlines,
            selection.Start,
            selection.End,
            style);
        RefreshTextBlock(selection.Block);
        CollapseTextSelection(selection, selection.End, focusEditor: true);
        RaiseContentChanged();
    }

    private async Task ApplyLinkAsync(SelectionSnapshot? selection)
    {
        var target = selection ?? GetToolbarSelection(allowCaret: true);
        if (target is null || !_blocks.Contains(target.Block) || target.Block.State.IsCitation) return;

        var url = await PromptForLinkAsync();
        if (string.IsNullOrWhiteSpace(url)) return;

        var textLength = target.Block.LastPlainText.Length;
        var start = Math.Clamp(target.Start, 0, textLength);
        var end = Math.Clamp(target.End, start, textLength);
        if (start == end)
        {
            var before = SliceRuns(target.Block.State.Inlines, 0, start);
            before.Add(new InlineRun(url, InlineStyle.Link, url));
            before.AddRange(SliceRuns(target.Block.State.Inlines, end, textLength));
            target.Block.State.Inlines = MergeRuns(before);
            SetEditorText(target.Block, target.Block.State.PlainText, start + url.Length);
            UpdateBlockView(target.Block);
            CollapseTextSelection(target, start + url.Length, focusEditor: true);
        }
        else
        {
            target.Block.State.Inlines = ApplyInlineStyle(
                target.Block.State.Inlines,
                start,
                end,
                InlineStyle.Link,
                url);
            RefreshTextBlock(target.Block);
            CollapseTextSelection(target, end, focusEditor: true);
        }
        RaiseContentChanged();
    }

    private async Task InsertCitationAsync(SelectionSnapshot? selection)
    {
        var target = selection ?? GetToolbarSelection(allowCaret: true);
        if (target is null || !_blocks.Contains(target.Block) || target.Block.State.IsCitation)
            return;

        var citation = await PickCitationAsync();
        if (citation is null || string.IsNullOrWhiteSpace(citation.DisplayText))
            return;

        InsertCitationBlock(
            target,
            citation.DisplayText.Trim(),
            citation.BookTitle);
    }

    private void InsertCitationBlock(
        SelectionSnapshot selection,
        string text,
        string? citationBookTitle = null)
    {
        var view = selection.Block;
        var index = _blocks.IndexOf(view);
        if (index < 0 || text.Length == 0)
            return;

        citationBookTitle = NormalizeCitationBookTitle(citationBookTitle);

        var length = view.LastPlainText.Length;
        var start = Math.Clamp(selection.Start, 0, length);
        var end = Math.Clamp(selection.End, start, length);
        var before = SliceRuns(view.State.Inlines, 0, start);
        var after = SliceRuns(view.State.Inlines, end, length);
        var inserted = ParseInline(text.ReplaceLineEndings("\n"));

        // In an existing quote field, replace the selected text in place. This
        // keeps the TextBox's character offsets identical to the rendered text,
        // so the caret lands immediately after the inserted citation.
        if (view.State.Kind == BlockKind.Quote)
        {
            before.AddRange(inserted);
            before.AddRange(after);
            view.State.Inlines = MergeRuns(before);
            view.State.IsCitation = true;
            view.State.CitationBookTitle = citationBookTitle.Length > 0 ? citationBookTitle : null;
            var caret = start + text.Length;
            SetEditorText(view, view.State.PlainText, caret);
            UpdateBlockView(view);
            FocusBlock(view, caret);
            RaiseContentChanged();
            return;
        }

        var quote = new BlockState
        {
            Kind = BlockKind.Quote,
            Inlines = inserted,
            IsCitation = true,
            CitationBookTitle = citationBookTitle.Length == 0 ? null : citationBookTitle
        };

        if (before.Count == 0 && after.Count == 0)
        {
            view.State.Kind = BlockKind.Quote;
            view.State.Inlines = inserted;
            view.State.IsCitation = true;
            view.State.CitationBookTitle = citationBookTitle.Length == 0 ? null : citationBookTitle;
            SetEditorText(view, view.State.PlainText, view.State.PlainText.Length);
            UpdateBlockView(view);
            AddBlock(new BlockState(), index + 1);
            FocusBlock(view, view.State.PlainText.Length);
        }
        else if (before.Count == 0)
        {
            view.State.Kind = BlockKind.Quote;
            view.State.Inlines = inserted;
            view.State.IsCitation = true;
            view.State.CitationBookTitle = citationBookTitle.Length == 0 ? null : citationBookTitle;
            SetEditorText(view, view.State.PlainText, view.State.PlainText.Length);
            UpdateBlockView(view);
            AddBlock(new BlockState { Inlines = after }, index + 1);
            FocusBlock(view, view.State.PlainText.Length);
        }
        else
        {
            view.State.Inlines = before;
            SetEditorText(view, view.State.PlainText, view.State.PlainText.Length);
            UpdateBlockView(view);
            AddBlock(quote, index + 1);
            AddBlock(new BlockState { Inlines = after }, index + 2);
            FocusBlock(_blocks[index + 1], text.Length);
        }

        _selection = null;
        RaiseContentChanged();
    }

    internal static IReadOnlyList<BookReflectionCitation> FilterCitations(
        IEnumerable<BookReflectionCitation> citations,
        string? query,
        string? currentBookTitle = null)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        return citations
            .Where(citation => normalizedQuery.Length == 0
                || citation.SearchText.Contains(normalizedQuery, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(citation => IsSameBook(citation.BookTitle, currentBookTitle))
            .ThenByDescending(citation => citation.UpdatedAt)
            .ToArray();
    }

    private static bool IsSameBook(string? citationBookTitle, string? currentBookTitle)
    {
        var citationTitle = NormalizeBookTitleForMatch(citationBookTitle);
        var currentTitle = NormalizeBookTitleForMatch(currentBookTitle);
        return currentTitle.Length > 0
            && string.Equals(citationTitle, currentTitle, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string NormalizeBookTitleForMatch(string? title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        return normalized.Length >= 2
            && normalized[0] == '《'
            && normalized[^1] == '》'
            ? normalized[1..^1].Trim()
            : normalized;
    }

    internal static string FormatCitationSource(string? bookTitle)
    {
        var title = NormalizeCitationBookTitle(bookTitle);
        if (title.Length == 0)
            return string.Empty;

        var decoratedTitle = title.StartsWith("《", StringComparison.Ordinal)
            && title.EndsWith("》", StringComparison.Ordinal)
            ? title
            : $"《{title}》";
        return $"--{decoratedTitle}";
    }

    private static string NormalizeCitationBookTitle(string? value) =>
        (value ?? string.Empty).Trim().ReplaceLineEndings(" ");

    private async Task<BookReflectionCitation?> PickCitationAsync()
    {
        var owner = GetOwnerWindow();
        if (owner is null || _citations.Count == 0)
            return null;

        var searchBox = new TextBox
        {
            MinHeight = 34,
            PlaceholderText = UiText.Get("搜索书名、章节、划线或批注")
        };
        var list = new ListBox
        {
            MinHeight = 260,
            MaxHeight = 420,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            SelectionMode = SelectionMode.Single
        };
        list.Classes.Add("citationPickerList");
        var emptyText = new TextBlock
        {
            Text = UiText.Get("没有找到匹配的批注。"),
            Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16),
            IsVisible = false
        };
        list.ItemTemplate = new FuncDataTemplate<BookReflectionCitation>((citation, _) =>
        {
            // Avalonia may invoke the recycled item template with a null item
            // while ItemsSource is replaced during search. Keep that transient
            // container harmless instead of dereferencing it below.
            if (citation is null)
            {
                return new Border { IsVisible = false };
            }

            var quote = new TextBlock
            {
                Text = citation.DisplayText,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3
            };
            var meta = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(citation.ChapterLabel)
                    ? citation.BookTitle
                    : $"{citation.BookTitle} · {citation.ChapterLabel}",
                FontSize = 10,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            var row = new Border
            {
                Padding = new Thickness(10, 8),
                Child = new StackPanel { Spacing = 3, Children = { quote, meta } }
            };
            row.Classes.Add("citationPickerRow");
            return row;
        });

        void RefreshList()
        {
            var query = searchBox.Text?.Trim() ?? string.Empty;
            var filtered = FilterCitations(_citations, query, _currentBookTitle);
            list.ItemsSource = filtered;
            list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
            emptyText.IsVisible = filtered.Count == 0;
        }

        var completion = new TaskCompletionSource<BookReflectionCitation?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = UiText.Get("引用批注"),
            Width = 600,
            Height = 560,
            MinWidth = 480,
            MinHeight = 420,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };

        void Complete(BookReflectionCitation? result)
        {
            completion.TrySetResult(result);
            dialog.Close();
        }

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var insertButton = new Button
        {
            Content = UiText.Get("插入引用"),
            Padding = new Thickness(14, 7),
            MinWidth = 92
        };
        insertButton.Click += (_, _) =>
            Complete(list.SelectedItem as BookReflectionCitation);
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is BookReflectionCitation citation)
                Complete(citation);
        };
        searchBox.TextChanged += (_, _) => RefreshList();
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && list.SelectedItem is BookReflectionCitation citation)
            {
                e.Handled = true;
                Complete(citation);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, insertButton }
        };
        var fields = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10,
            Children =
            {
                searchBox,
                new Grid { Children = { list, emptyText } },
                actions
            }
        };
        Grid.SetRow(searchBox, 0);
        Grid.SetRow(fields.Children[1], 1);
        Grid.SetRow(actions, 2);
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = fields
        };
        ApplyCustomDialogChrome(
            dialog,
            UiText.Get("引用批注"),
            () => Complete(null),
            canResize: true);
        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            RefreshList();
            searchBox.Focus();
        };
        dialog.Show(owner);
        return await completion.Task;
    }
}
