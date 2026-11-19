using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

public sealed class Stage3ReadingMaterialGroupViewModel : ObservableObject, IDisposable
{
    private bool _isExpanded;
    private bool _suppressSelectionNotification;
    private readonly string _bookTitleSource;

    public Stage3ReadingMaterialGroupViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        IReadOnlyList<Stage3ReadingMaterialViewModel> items,
        string? coverPath,
        bool isExpanded,
        bool isMixedSource = false,
        bool isExportMode = false)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Source = source;
        _bookTitleSource = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
        // A book-level reflection is the primary note for the book and should
        // stay at the top of its expanded group. Keep the remaining entries in
        // their existing recency order.
        Items = items
            .OrderByDescending(item => item.IsBookReflection)
            .ThenByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        IsMixedSource = isMixedSource;
        IsExportMode = isExportMode;
        _isExpanded = isExpanded;
        foreach (var item in Items)
            item.PropertyChanged += OnItemPropertyChanged;
        if (!string.IsNullOrWhiteSpace(coverPath) && File.Exists(coverPath))
        {
            try
            {
                using var stream = File.OpenRead(coverPath);
                CoverImage = Bitmap.DecodeToWidth(
                    stream,
                    240,
                    BitmapInterpolationMode.MediumQuality);
            }
            catch { }
        }
    }

    public ReadingMaterialSource Source { get; }
    public bool IsMixedSource { get; }
    public bool HasDeletedBook => Items.Any(item => item.IsDeletedBook);
    public string DeletedLabel => UiText.IsEnglish ? "Deleted" : "已删除";
    public string DisplayBookTitle => HasDeletedBook
        ? UiText.IsEnglish ? $"{BookTitle} ({DeletedLabel})" : $"{BookTitle}（{DeletedLabel}）"
        : BookTitle;
    public bool IsExportMode { get; }
    public string SourceLabel => string.Join(" + ", Items.Select(item => item.Source == ReadingMaterialSource.Local
        ? UiText.Get("本地书籍") : item.SourceLabel).Distinct(StringComparer.Ordinal));
    internal string BookTitleKey => _bookTitleSource;
    public string BookTitle => UiText.Localize(_bookTitleSource);
    public IReadOnlyList<Stage3ReadingMaterialViewModel> Items { get; }
    public string CountLabel => UiText.Get("{0} 条批注", Items.Count);
    public Bitmap? CoverImage { get; }
    public bool HasCover => CoverImage is not null;
    public bool HasNoCover => CoverImage is null;
    public bool IsSelected
    {
        get => Items.Count > 0 && Items.All(item => item.IsSelected);
        set
        {
            if (IsSelected == value) return;
            _suppressSelectionNotification = true;
            try
            {
                foreach (var item in Items)
                    item.IsSelected = value;
            }
            finally
            {
                _suppressSelectionNotification = false;
            }
            OnPropertyChanged();
        }
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(IsCollapsed));
            OnPropertyChanged(nameof(ExpandLabel));
        }
    }
    public bool IsCollapsed => !IsExpanded;
    public string ExpandLabel => IsExpanded ? UiText.Get("收起") : UiText.Get("展开");
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(DeletedLabel));
        OnPropertyChanged(nameof(DisplayBookTitle));
        OnPropertyChanged(nameof(ExpandLabel));
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSelectionNotification
            && (e.PropertyName is null || e.PropertyName == nameof(Stage3ReadingMaterialViewModel.IsSelected)))
            OnPropertyChanged(nameof(IsSelected));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var item in Items)
            item.PropertyChanged -= OnItemPropertyChanged;
        CoverImage?.Dispose();
    }
}

public sealed class Stage3ReadingMaterialViewModel : ObservableObject, IDisposable
{
    private bool _isSelected;
    private readonly string _bookTitleSource;
    private readonly string _typeLabelSource;

    public Stage3ReadingMaterialViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        string typeLabel,
        string chapterLabel,
        string location,
        string quote,
        string note,
        DateTimeOffset? updatedAt,
        ReaderAnnotation? localAnnotation,
        KindleClipping? kindleClipping,
        KindleClipping? pairedKindleClipping = null,
        string? sourceDeviceId = null,
        string? sourceDeviceName = null,
        bool canDeleteDeviceNotes = true,
        ReaderBookReflection? localReflection = null)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Source = source;
        SourceDeviceId = sourceDeviceId;
        SourceDeviceName = sourceDeviceName;
        CanDelete = source == ReadingMaterialSource.Local || canDeleteDeviceNotes;
        _bookTitleSource = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
        ChapterLabel = chapterLabel;
        Location = location;
        Quote = quote;
        Note = note;
        UpdatedAt = updatedAt;
        LocalAnnotation = localAnnotation;
        LocalReflection = localReflection;
        KindleClipping = kindleClipping;
        PairedKindleClipping = pairedKindleClipping;
        _typeLabelSource = kindleClipping is { } clipping && pairedKindleClipping is null
            ? clipping.Type switch
            {
                KindleClippingType.Highlight => "划线",
                KindleClippingType.Note => "笔记",
                KindleClippingType.Bookmark => "书签",
                _ => "记录"
            }
            : typeLabel;
    }

    public ReadingMaterialSource Source { get; }
    public string? SourceDeviceId { get; }
    public string? SourceDeviceName { get; }
    public bool CanDelete { get; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? UiText.Get("本地")
        : string.IsNullOrWhiteSpace(SourceDeviceName) ? UiText.Get("设备") : SourceDeviceName;
    internal string BookTitleKey => _bookTitleSource;
    public string BookTitle => UiText.Localize(_bookTitleSource);
    public string TypeLabel => UiText.Get(_typeLabelSource);
    public string ChapterLabel { get; }
    public string Location { get; }
    public string Quote { get; }
    public string Note { get; }
    public DateTimeOffset? UpdatedAt { get; }
    public ReaderAnnotation? LocalAnnotation { get; }
    public ReaderBookReflection? LocalReflection { get; }
    public KindleClipping? KindleClipping { get; }
    public KindleClipping? PairedKindleClipping { get; }
    public string QuoteLabel => string.IsNullOrWhiteSpace(Quote) ? UiText.Get("无划线内容") : UiText.Get("“{0}”", Quote);
    public bool IsBookReflection => LocalReflection is not null;
    public string NoteLabel => IsBookReflection || string.IsNullOrWhiteSpace(Note) ? "" : UiText.Get("批注：{0}", Note);
    public string ChapterDisplayLabel => IsBookReflection
        ? UiText.Get("书籍级思考")
        : UiText.Get("章节：{0}", ChapterLabel);
    public string SelectedContentLabel => IsBookReflection
        ? string.Empty
        : string.IsNullOrWhiteSpace(Quote) ? UiText.Get("选中内容：无") : UiText.Get("选中内容：{0}", Quote);
    public bool IsNotBookReflection => !IsBookReflection;
    public string ReflectionMarkdown => IsBookReflection
        ? BookReflectionEditorSurface.NormalizeMarkdown(Note)
        : string.Empty;
    public string ReflectionPreviewMarkdown => IsBookReflection
        ? BookReflectionEditorSurface.RemoveImagesForPreview(ReflectionMarkdown)
        : string.Empty;
    public string ReflectionPreviewText => IsBookReflection
        ? GetReflectionPreviewText(ReflectionPreviewMarkdown)
        : string.Empty;
    public string? ReflectionTooltipText => IsBookReflection
        ? GetReflectionTooltipText(ReflectionPreviewMarkdown)
        : null;
    public bool HasNote => !IsBookReflection && !string.IsNullOrWhiteSpace(Note);
    public string DateLabel => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? UiText.Get("时间未知");
    public string SearchText => string.Join('\n', SourceLabel, BookTitle, TypeLabel, ChapterLabel, Location, Quote, Note);
    public bool IsDeletedBook { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ReadingMaterialRecord ToRecord() => new(Source, BookTitle, TypeLabel, Location, Quote, Note, UpdatedAt, SourceDeviceId, SourceDeviceName);

    private static string GetReflectionPreviewText(string markdown)
    {
        foreach (var line in GetReflectionTextLines(markdown))
        {
            if (line.Length > 0)
                return line;
        }

        return string.Empty;
    }

    private static string GetReflectionTooltipText(string markdown) =>
        string.Join("\n", GetReflectionTextLines(markdown)).Trim();

    private static IEnumerable<string> GetReflectionTextLines(string markdown)
    {
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var text = line.Trim();
            text = Regex.Replace(text, @"^#{1,6}\s*", string.Empty);
            text = Regex.Replace(text, @"(\*\*|__|~~|`)", string.Empty);
            text = Regex.Replace(text, @"\[(?<label>[^\]]+)\]\([^)]*\)", "${label}");
            yield return text;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(QuoteLabel));
        OnPropertyChanged(nameof(NoteLabel));
        OnPropertyChanged(nameof(ChapterDisplayLabel));
        OnPropertyChanged(nameof(SelectedContentLabel));
        OnPropertyChanged(nameof(IsNotBookReflection));
        OnPropertyChanged(nameof(ReflectionMarkdown));
        OnPropertyChanged(nameof(ReflectionPreviewMarkdown));
        OnPropertyChanged(nameof(ReflectionPreviewText));
        OnPropertyChanged(nameof(ReflectionTooltipText));
        OnPropertyChanged(nameof(DateLabel));
        OnPropertyChanged(nameof(SearchText));
    }

    public void Dispose() =>
        UiText.LanguageChanged -= OnLanguageChanged;
}
