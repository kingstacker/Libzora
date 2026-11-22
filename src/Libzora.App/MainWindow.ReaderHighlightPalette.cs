using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace Libzora;

public partial class MainWindow
{
    private PopupFlyoutBase? ReaderHighlightStyleFlyout => ReaderSelectionHighlightMenuButton is { } button
        ? FlyoutBase.GetAttachedFlyout(button) as PopupFlyoutBase
        : null;

    private PopupFlyoutBase? ReaderMarkerPaletteFlyout => ReaderSelectionMarkerColorButton is { } button
        ? FlyoutBase.GetAttachedFlyout(button) as PopupFlyoutBase
        : null;

    private void ReaderSelectionHighlightFlyout_Opened(object? sender, EventArgs e)
    {
        ReaderSelectionHighlightMenuButton.Classes.Set("active", true);
        RefreshReaderMarkerColorPreview();
        RefreshReaderHighlightMenuSelection(NormalizeReaderAnnotationStyle(_readerLastHighlightStyle));
    }

    private void ReaderSelectionHighlightFlyout_Closed(object? sender, EventArgs e)
    {
        ReaderSelectionHighlightMenuButton.Classes.Set("active", false);
        ReaderMarkerPaletteFlyout?.Hide();
    }

    private void ReaderSelectionMarkerColorButton_Click(object? sender, RoutedEventArgs e)
    {
        // Opening the palette must not invoke the containing highlight row.
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        RefreshReaderMarkerColorPreview();
        if (ReaderMarkerPaletteFlyout is { } flyout)
        {
            if (flyout.IsOpen) flyout.Hide();
            else flyout.ShowAt(ReaderSelectionMarkerItem);
        }
    }

    private string ReaderMarkerColorForSelection()
    {
        var chapterPath = _readerIsPdf ? $"pdf:page:{_readerPdfPage}" : GetReaderChapterPath();
        var hasRange = !string.IsNullOrWhiteSpace(_readerPendingSelection)
            && _readerPendingSelectionEndOffset > _readerPendingSelectionStartOffset;
        var annotation = _selectedReaderAnnotation ?? (hasRange
            ? ReaderAnnotations.FirstOrDefault(item =>
                string.Equals(item.ChapterPath, chapterPath, StringComparison.OrdinalIgnoreCase)
                && item.StartOffset == _readerPendingSelectionStartOffset
                && item.EndOffset == _readerPendingSelectionEndOffset)
            : null);
        return NormalizeReaderAnnotationColor(annotation?.UnderlineStyle == "marker"
            ? annotation.Color
            : _readerLastMarkerColor);
    }

    private void RefreshReaderMarkerColorPreview()
    {
        var color = ReaderMarkerColorForSelection();
        ReaderSelectionMarkerColorPreview.Background = new SolidColorBrush(Color.Parse(color));
        foreach (var option in ReaderSelectionMarkerPalette.Children.OfType<Button>())
            option.Classes.Set("selected", string.Equals(option.Tag as string, color, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshReaderHighlightMenuSelection(string activeStyle)
    {
        if (ReaderHighlightStyleFlyout is not MenuFlyout flyout) return;
        foreach (var item in flyout.Items.OfType<MenuItem>())
            item.Classes.Set("active", string.Equals(item.Tag as string, activeStyle, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshReaderSelectionHighlightButton()
    {
        if (ReaderSelectionHighlightSolidLine is null) return;
        var style = NormalizeReaderAnnotationStyle(_readerLastHighlightStyle);
        ReaderSelectionHighlightSolidLine.IsVisible = style == "solid";
        ReaderSelectionHighlightDoubleLine.IsVisible = style == "double";
        ReaderSelectionHighlightDashedLine.IsVisible = style == "dashed";
        ReaderSelectionHighlightDottedLine.IsVisible = style == "dotted";
        ReaderSelectionHighlightWavyLine.IsVisible = style == "wavy";
        ReaderSelectionHighlightMarkerBar.IsVisible = style == "marker";
        if (style == "marker")
        {
            var color = ReaderMarkerColorForSelection();
            _readerSelectionHighlightMarkerBinding?.Dispose();
            _readerSelectionHighlightMarkerBinding = null;
            if (string.Equals(color, "#000000", StringComparison.OrdinalIgnoreCase))
            {
                _readerSelectionHighlightMarkerBinding = ReaderSelectionHighlightMarkerBar.Bind(
                    Border.BackgroundProperty,
                    new DynamicResourceExtension("ReaderInkBrush"));
                ReaderSelectionHighlightMarkerBar.BorderThickness = new Thickness(0);
            }
            else
            {
                ReaderSelectionHighlightMarkerBar.Background = new SolidColorBrush(Color.Parse(color));
                ReaderSelectionHighlightMarkerBar.BorderThickness = new Thickness(0.5);
            }
        }
        RefreshReaderHighlightMenuSelection(style);
    }
}
