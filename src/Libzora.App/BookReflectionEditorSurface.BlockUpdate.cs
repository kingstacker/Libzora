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
/// Block view refresh and selection toolbar state.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void UpdateBlockView(BlockView view)
    {
        var state = view.State;
        if (state.Kind == BlockKind.Image)
        {
            view.Marker.IsVisible = false;
            view.Row.ColumnDefinitions[0].Width = new GridLength(0);
            UpdateImageView(view);
            return;
        }

        var isQuote = state.Kind == BlockKind.Quote;
        var isCitation = isQuote && state.IsCitation;
        view.Marker.IsVisible = !isQuote;
        var markerText = state.Kind switch
        {
            BlockKind.UnorderedList => "•",
            BlockKind.OrderedList => $"{state.OrderedNumber}.",
            _ => string.Empty
        };
        var markerWidth = state.Kind switch
        {
            BlockKind.UnorderedList or BlockKind.OrderedList => 28,
            _ => 0
        };
        view.Row.ColumnDefinitions[0].Width = new GridLength(markerWidth);
        view.Marker.Width = markerWidth;
        view.Row.ColumnSpacing = isQuote ? 0 : 8;
        view.Row.Background = Brushes.Transparent;
        view.Marker.Margin = new Thickness(0);
        view.Marker.Background = Brushes.Transparent;
        view.ContentHost.HorizontalAlignment = isQuote
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
        view.ContentHost.VerticalAlignment = VerticalAlignment.Top;
        view.ContentHost.ColumnDefinitions[0].Width = isQuote
            ? GridLength.Auto
            : new GridLength(0);
        view.ContentHost.ColumnDefinitions[1].Width = isQuote
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        view.ContentHost.ColumnDefinitions[2].Width = isQuote
            ? GridLength.Auto
            : new GridLength(0);
        view.ContentHost.Background = isQuote
            ? AppAppearanceResources.GetBrush("PressedBrush")
            : Brushes.Transparent;
        view.QuoteOpening?.SetCurrentValue(IsVisibleProperty, isQuote);
        view.QuoteClosing?.SetCurrentValue(IsVisibleProperty, isQuote);
        var hasCitationSource = isCitation && !string.IsNullOrWhiteSpace(state.CitationBookTitle);
        if (view.CitationSource is { } citationSource)
        {
            citationSource.Text = hasCitationSource
                ? FormatCitationSource(state.CitationBookTitle!)
                : string.Empty;
            citationSource.SetCurrentValue(IsVisibleProperty, hasCitationSource);
        }
        view.Marker.Child = state.Kind is BlockKind.UnorderedList or BlockKind.OrderedList
            ? new TextBlock
            {
                Text = markerText,
                FontSize = 15,
                Foreground = AppAppearanceResources.GetBrush("MutedInkBrush"),
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 0, 0)
            }
            : null;

        if (view.RichText is not null)
        {
            view.RichText.Foreground = isQuote
                ? AppAppearanceResources.GetBrush("InkBrush")
                : AppAppearanceResources.GetBrush("InkBrush");
            view.RichText.Margin = isQuote
                ? new Thickness(
                    QuoteTextHorizontalSpacing,
                    10,
                    QuoteTextHorizontalSpacing,
                    10)
                : new Thickness(0, 5, 0, 5);
            view.RichText.FontStyle = isQuote
                ? FontStyle.Italic
                : FontStyle.Normal;
            view.RichText.FontSize = state.Kind switch
            {
                BlockKind.Heading1 => 28,
                BlockKind.Heading2 => 24,
                BlockKind.Heading3 => 21,
                BlockKind.Heading4 => 19,
                BlockKind.Heading5 => 17,
                BlockKind.Heading6 => 16,
                _ => 15
            };
            view.RichText.FontWeight = IsHeading(state.Kind)
                ? FontWeight.SemiBold
                : FontWeight.Normal;
            view.RichText.SetRuns(state.Inlines, isQuote);
        }

        if (view.Editor is not null)
        {
            view.Editor.FontSize = state.Kind switch
            {
                BlockKind.Heading1 => 28,
                BlockKind.Heading2 => 24,
                BlockKind.Heading3 => 21,
                BlockKind.Heading4 => 19,
                BlockKind.Heading5 => 17,
                BlockKind.Heading6 => 16,
                _ => 15
            };
            view.Editor.Padding = isQuote
                ? new Thickness(
                    QuoteTextHorizontalSpacing,
                    10,
                    QuoteTextHorizontalSpacing,
                    10)
                : new Thickness(0, 5, 0, 5);
            view.Editor.FontStyle = isQuote
                ? FontStyle.Italic
                : FontStyle.Normal;
            view.Editor.IsReadOnly = isCitation;
            view.Editor.MinHeight = state.Kind switch
            {
                BlockKind.Heading1 => 48,
                BlockKind.Heading2 => 43,
                BlockKind.Heading3 => 38,
                BlockKind.Heading4 => 35,
                BlockKind.Quote => 46,
                _ => 30
            };
        }
    }

    private void UpdateSelectionToolbar(BlockView view)
    {
        if (_disposed || view.Editor is null) return;

        var start = Math.Min(view.Editor.SelectionStart, view.Editor.SelectionEnd);
        var end = Math.Max(view.Editor.SelectionStart, view.Editor.SelectionEnd);
        if (start == end)
        {
            if (_selection?.Block == view)
                _selection = null;
            return;
        }

        _activeBlock = view;
        _selection = new SelectionSnapshot(view, start, end);
    }


    private void CollapseTextSelection(
        SelectionSnapshot? selection,
        int? caret = null,
        bool focusEditor = false)
    {
        _selection = null;
        if (selection?.Block.Editor is not { } editor)
            return;

        var textLength = editor.Text?.Length ?? 0;
        var position = Math.Clamp(caret ?? editor.CaretIndex, 0, textLength);
        editor.SelectionStart = position;
        editor.SelectionEnd = position;
        if (focusEditor)
            editor.Focus();
    }

    private SelectionSnapshot? GetToolbarSelection(bool allowCaret)
    {
        if (_selection is { } selection && _blocks.Contains(selection.Block))
            return selection;

        if (!allowCaret)
            return null;

        var view = _activeBlock ?? _blocks.LastOrDefault(block => block.Editor is not null);
        if (view?.Editor is not { } editor)
            return null;

        var textLength = editor.Text?.Length ?? 0;
        var start = Math.Clamp(Math.Min(editor.SelectionStart, editor.SelectionEnd), 0, textLength);
        var end = Math.Clamp(Math.Max(editor.SelectionStart, editor.SelectionEnd), start, textLength);
        return new SelectionSnapshot(view, start, end);
    }

    private async void ToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action })
            return;

        e.Handled = true;
        var selection = GetToolbarSelection(allowCaret: true);
        switch (action)
        {
            case "bold":
                ApplyBold(selection);
                break;
            case "link":
                await ApplyLinkAsync(selection);
                break;
            case "citation":
                await InsertCitationAsync(selection);
                break;
            case "image":
                await InsertImageAsync(selection);
                break;
            default:
                ApplyBlockAction(action, selection);
                break;
        }
    }
}
