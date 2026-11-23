using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Libzora.Core;

namespace Libzora;

public sealed class ReaderAnnotationAccentBrushConverter : IValueConverter
{
    public static readonly ReaderAnnotationAccentBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var annotation = value switch
        {
            ReaderAnnotation a => a,
            Stage3ReadingMaterialViewModel m => m.LocalAnnotation,
            _ => null
        };
        var isReaderTheme = string.Equals(parameter as string, "reader", StringComparison.OrdinalIgnoreCase);
        if (TryGetHighlightBrush(annotation, isReaderTheme, out var brush))
            return brush;
        return AppAppearanceResources.GetBrush(isReaderTheme ? "ReaderBorderBrush" : "SubtleInkBrush");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static bool TryGetHighlightBrush(ReaderAnnotation? annotation, bool isReaderTheme, out IBrush brush)
    {
        brush = Brushes.Transparent;
        if (annotation is null) return false;
        var style = annotation.UnderlineStyle?.Trim().ToLowerInvariant();
        var hex = annotation.Color?.Trim();
        var isBlack = string.IsNullOrWhiteSpace(hex) || string.Equals(hex, "#000000", StringComparison.OrdinalIgnoreCase);
        if (isBlack)
        {
            if (style != "marker") return false;
            brush = AppAppearanceResources.GetBrush(isReaderTheme ? "ReaderInkBrush" : "InkBrush");
            return true;
        }
        if (Color.TryParse(hex, out var color))
        {
            brush = new SolidColorBrush(color);
            return true;
        }
        return false;
    }
}

public sealed class ReaderAnnotationDateConverter : IValueConverter
{
    public static readonly ReaderAnnotationDateConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset date
            ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

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

    private string _readerAnnotationInputStyle = "solid";
    private string _readerAnnotationInputColor = "#000000";

    private PopupFlyoutBase? ReaderAnnotationInputStyleFlyout => ReaderAnnotationInputStyleButton is { } button
        ? FlyoutBase.GetAttachedFlyout(button) as PopupFlyoutBase
        : null;

    private void RefreshReaderAnnotationInputStyleControls()
    {
        if (ReaderAnnotationInputStyleButton is null) return;
        var style = NormalizeReaderAnnotationStyle(_readerAnnotationInputStyle);
        var color = NormalizeReaderAnnotationColor(_readerAnnotationInputColor);
        _readerAnnotationInputStyle = style;
        _readerAnnotationInputColor = color;

        ReaderAnnotationInputStyleLabel.Text = style switch
        {
            "double" => T("双线"),
            "dashed" => T("虚线"),
            "dotted" => T("点线"),
            "wavy" => T("波浪线"),
            "marker" => T("荧光标记"),
            _ => T("直线")
        };

        ReaderAnnotationInputStyleSolidLine.IsVisible = style == "solid";
        ReaderAnnotationInputStyleDoubleLine.IsVisible = style == "double";
        ReaderAnnotationInputStyleDashedLine.IsVisible = style == "dashed";
        ReaderAnnotationInputStyleDottedLine.IsVisible = style == "dotted";
        ReaderAnnotationInputStyleWavyLine.IsVisible = style == "wavy";
        ReaderAnnotationInputStyleMarkerBar.IsVisible = style == "marker";
        if (style == "marker")
        {
            ReaderAnnotationInputStyleMarkerBar.Background = string.Equals(color, "#000000", StringComparison.OrdinalIgnoreCase)
                ? AppAppearanceResources.GetBrush("ReaderInkBrush")
                : new SolidColorBrush(Color.Parse(color));
        }

        if (ReaderAnnotationInputStyleFlyout is MenuFlyout flyout)
        {
            foreach (var item in flyout.Items.OfType<MenuItem>())
                item.Classes.Set("active", string.Equals(item.Tag as string, style, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var option in ReaderAnnotationInputPalette.Children.OfType<Button>())
        {
            var optionHex = option.Tag as string ?? string.Empty;
            var isSelected = style == "marker"
                ? string.Equals(optionHex, color, StringComparison.OrdinalIgnoreCase)
                : string.Equals(optionHex, color, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(optionHex, "#000000", StringComparison.OrdinalIgnoreCase);
            option.Classes.Set("selected", isSelected);
        }

        if (ReaderAnnotationAccentBrushConverter.TryGetHighlightBrush(
                new ReaderAnnotation { UnderlineStyle = style, Color = color },
                isReaderTheme: true,
                out var accentBrush))
        {
            ReaderAnnotationInputQuoteBar.Background = accentBrush;
        }
        else
        {
            ReaderAnnotationInputQuoteBar.Background = AppAppearanceResources.GetBrush("ReaderBorderBrush");
        }
    }

    private void ReaderAnnotationInputStyleItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string style }) return;
        _readerAnnotationInputStyle = NormalizeReaderAnnotationStyle(style);
        RefreshReaderAnnotationInputStyleControls();
    }

    private void ReaderAnnotationInputColorItem_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: string color }) return;
        _readerAnnotationInputColor = NormalizeReaderAnnotationColor(color);
        if (_readerAnnotationInputStyle != "marker")
            _readerAnnotationInputStyle = "marker";
        RefreshReaderAnnotationInputStyleControls();
    }
}
