using Avalonia.Controls;
using Avalonia.Interactivity;
using Libzora.Core;

namespace Libzora.Mobile.Views.Sheets;

public partial class BookReflectionCitationSheet : UserControl, IFullHeightSheet
{
    private readonly IReadOnlyList<BookReflectionCitationRecord> _citations;
    private readonly string _currentBookTitle;
    private readonly Action<BookReflectionCitationRecord>? _onPicked;

    public bool FillsHeight => true;

    public BookReflectionCitationSheet()
    {
        _citations = [];
        _currentBookTitle = string.Empty;
        InitializeComponent();
    }

    public BookReflectionCitationSheet(
        IReadOnlyList<BookReflectionCitationRecord> citations,
        string currentBookTitle,
        Action<BookReflectionCitationRecord> onPicked)
    {
        _citations = citations;
        _currentBookTitle = currentBookTitle;
        _onPicked = onPicked;
        InitializeComponent();
        ApplyFilter();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filtered = BookReflectionMarkdown.FilterCitations(
            _citations,
            SearchBox.Text,
            _currentBookTitle);
        CitationList.ItemsSource = filtered;
        EmptyText.IsVisible = filtered.Count == 0;
    }

    private void OnCitationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BookReflectionCitationRecord citation })
            _onPicked?.Invoke(citation);
    }
}
