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

public sealed class KindleBookCardViewModel : ObservableObject, IDisposable
{
    private Bitmap? _coverImage;
    private bool _coverLoadAttempted;
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.KindleOnly;
    private bool _isDownloading;
    private bool _isSelected;
    private bool _isMultiSelected;
    private bool _isHovered;
    private bool _isTooltipEnabled = true;
    private double _downloadProgress;
    private string _statusMessage = string.Empty;
    private IReadOnlyList<KindleBookCardViewModel> _seriesMembers = [];
    private string? _seriesKey;
    private string? _seriesTitle;

    public KindleBookCardViewModel(KindleBook book)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
    }

    public KindleBook Book { get; private set; }

    public void UpdateBook(KindleBook book)
    {
        var coverChanged = !string.Equals(Book.CoverPath, book.CoverPath, StringComparison.OrdinalIgnoreCase);
        Book = book;
        if (coverChanged)
        {
            _coverImage?.Dispose();
            _coverImage = null;
            _coverLoadAttempted = false;
        }

        OnPropertyChanged(nameof(Book));
        OnPropertyChanged(nameof(CoverImage));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(RelativePath));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(HashLabel));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayMetaLabel));
        OnPropertyChanged(nameof(DisplayInfoLabel));
        OnPropertyChanged(nameof(DisplayFileName));
    }

    public Bitmap? CoverImage
    {
        get
        {
            if (_coverLoadAttempted) return _coverImage;
            _coverLoadAttempted = true;
            if (string.IsNullOrWhiteSpace(Book.CoverPath) || !File.Exists(Book.CoverPath)) return null;
            try
            {
                using var stream = File.OpenRead(Book.CoverPath);
                _coverImage = Bitmap.DecodeToWidth(
                    stream,
                    320,
                    BitmapInterpolationMode.MediumQuality);
            }
            catch { }
            return _coverImage;
        }
    }
    public string Title => UiText.Localize(Book.Title);
    public string Authors => UiText.Localize(Book.Authors);
    public string FormatLabel => Book.Format.ToUpperInvariant();
    public string SizeLabel => Book.SizeLabel;
    public string InfoLabel => $"{FormatLabel} · {Book.SizeLabel}";
    public string FileName => Book.FileName;
    public string RelativePath => Book.RelativePath;
    public string ModifiedLabel => Book.ModifiedAt is { } modifiedAt
        ? UiText.Get("修改于 {0:yyyy-MM-dd HH:mm}", modifiedAt.ToLocalTime())
        : UiText.Get("修改时间未知");
    public string HashLabel => string.IsNullOrWhiteSpace(Book.Sha256)
        ? UiText.Get("SHA-256 未计算")
        : UiText.Get("SHA-256 {0}…", Book.Sha256[..Math.Min(12, Book.Sha256.Length)]);
    public string PresenceLabel
    {
        get
        {
            if (IsSeriesStack)
            {
                var total = _seriesMembers.Count;
                var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
                if (both == total) return UiText.Get("全部 {0} 册在电脑和当前设备均有", total);
                if (both > 0) return UiText.Get("{0}/{1} 册在电脑已有，{2} 册仅在当前设备", both, total, total - both);
                return UiText.Get("全部 {0} 册仅在当前设备", total);
            }
            return LibraryPresence switch
            {
                BookLibraryPresence.Both => UiText.Get("电脑与当前设备都有"),
                BookLibraryPresence.ComputerOnly => UiText.Get("仅电脑书库"),
                _ => UiText.Get("仅当前设备")
            };
        }
    }

    public string SeriesSummaryLabel
    {
        get
        {
            if (!IsSeriesStack) return SeriesCountLabel;
            var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
            return both > 0
                ? $"{SeriesCountLabel} · {UiText.Get("{0}/{1} 在电脑", both, _seriesMembers.Count)}"
                : SeriesCountLabel;
        }
    }

    // Gallery mode (shared with the PC library grid): hide the title/author/
    // info text so only the cover image remains.
    private bool _galleryTextVisible = true;
    private bool _isLibraryPresenceVisible = true;

    public bool GalleryTextVisibility
    {
        get => _galleryTextVisible;
        private set => SetProperty(ref _galleryTextVisible, value);
    }
    public double CardHeight => _galleryTextVisible ? 292 : 214;

    public IReadOnlyList<KindleBookCardViewModel> SeriesMembers => _seriesMembers;
    public string? SeriesKey => _seriesKey;
    public string? SeriesTitle => _seriesTitle;
    public bool IsSeriesStack => _seriesMembers.Count > 1;
    public int SeriesCount => _seriesMembers.Count;
    public string SeriesCountLabel => UiText.Get("{0} 册", _seriesMembers.Count);
    public string SeriesBadgeTip => UiText.Get("展开系列：{0}", _seriesTitle ?? Title);
    public bool SeriesBadgeVisibility => IsSeriesStack;

    public string DisplayTitle => IsSeriesStack ? _seriesTitle ?? Title : Title;
    public string DisplayMetaLabel => IsSeriesStack ? SeriesCountLabel : FormatLabel;
    public string DisplayInfoLabel => IsSeriesStack ? SeriesCountLabel : InfoLabel;
    public string DisplayFileName => IsSeriesStack ? SeriesCountLabel : FileName;

    public void SetSeriesMembers(
        string? key,
        string? title,
        IReadOnlyList<KindleBookCardViewModel>? members)
    {
        var next = members is { Count: > 1 } ? members : Array.Empty<KindleBookCardViewModel>();
        var unchanged = string.Equals(_seriesKey, key, StringComparison.Ordinal)
            && string.Equals(_seriesTitle, title, StringComparison.Ordinal)
            && _seriesMembers.SequenceEqual(next);
        if (unchanged) return;

        foreach (var member in _seriesMembers)
            member.PropertyChanged -= SeriesMember_PropertyChanged;
        _seriesKey = next.Count > 0 ? key : null;
        _seriesTitle = next.Count > 0 ? title : null;
        _seriesMembers = next.Count > 0 ? next.ToArray() : [];
        foreach (var member in _seriesMembers)
            member.PropertyChanged += SeriesMember_PropertyChanged;
        NotifySeriesChanged();
    }

    private void SeriesMember_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPresence))
        {
            NotifyPresenceChanged();
        }
    }

    private void NotifySeriesChanged()
    {
        OnPropertyChanged(nameof(SeriesMembers));
        OnPropertyChanged(nameof(SeriesKey));
        OnPropertyChanged(nameof(SeriesTitle));
        OnPropertyChanged(nameof(IsSeriesStack));
        OnPropertyChanged(nameof(SeriesCount));
        OnPropertyChanged(nameof(SeriesCountLabel));
        OnPropertyChanged(nameof(SeriesBadgeTip));
        OnPropertyChanged(nameof(SeriesBadgeVisibility));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayMetaLabel));
        OnPropertyChanged(nameof(DisplayInfoLabel));
        OnPropertyChanged(nameof(DisplayFileName));
        NotifyPresenceChanged();
    }

    private void NotifyPresenceChanged()
    {
        OnPropertyChanged(nameof(PresenceLabel));
        OnPropertyChanged(nameof(PresenceOpacity));
        OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
        OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
        OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        OnPropertyChanged(nameof(SeriesSummaryLabel));
    }

    public void SetGalleryTextVisible(bool visible)
    {
        if (_galleryTextVisible == visible) return;
        _galleryTextVisible = visible;
        OnPropertyChanged(nameof(GalleryTextVisibility));
        OnPropertyChanged(nameof(PresenceVisibility));
        OnPropertyChanged(nameof(CardHeight));
    }

    public bool PresenceVisibility
    {
        get => _isLibraryPresenceVisible && _galleryTextVisible;
        private set
        {
            if (_isLibraryPresenceVisible == value) return;
            _isLibraryPresenceVisible = value;
            OnPropertyChanged(nameof(PresenceVisibility));
        }
    }

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }
    public BookLibraryPresence LibraryPresence
    {
        get => _libraryPresence;
        private set
        {
            if (!SetProperty(ref _libraryPresence, value)) return;
            NotifyPresenceChanged();
        }
    }

    private BookLibraryPresence EffectiveLibraryPresence
    {
        get
        {
            if (!IsSeriesStack) return LibraryPresence;
            var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
            if (both > 0) return BookLibraryPresence.Both;
            return _seriesMembers.All(member => member.LibraryPresence == BookLibraryPresence.ComputerOnly)
                ? BookLibraryPresence.ComputerOnly
                : BookLibraryPresence.KindleOnly;
        }
    }

    public double PresenceOpacity
    {
        get
        {
            if (!IsSeriesStack) return 1d;
            var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
            return both > 0 && both < _seriesMembers.Count ? 0.52d : 1d;
        }
    }

    public bool ComputerOnlyPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.Both;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => SetProperty(ref _isDownloading, value);
    }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }
    public bool IsMultiSelected
    {
        get => _isMultiSelected;
        set => SetProperty(ref _isMultiSelected, value);
    }
    public bool IsTooltipEnabled
    {
        get => _isTooltipEnabled;
        set => SetProperty(ref _isTooltipEnabled, value);
    }
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }
    // 与电脑书库一致：悬停或选中时整卡显示黑色细边框。
    public bool IsFrameVisible => IsSelected || IsHovered;
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }
    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;
    public void SetDownloadProgress(TransferProgress progress) => DownloadProgress = progress.Percentage;
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Authors));
        OnPropertyChanged(nameof(InfoLabel));
        OnPropertyChanged(nameof(ModifiedLabel));
        OnPropertyChanged(nameof(HashLabel));
        OnPropertyChanged(nameof(PresenceLabel));
        OnPropertyChanged(nameof(SeriesCountLabel));
        OnPropertyChanged(nameof(SeriesBadgeTip));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayMetaLabel));
        OnPropertyChanged(nameof(DisplayInfoLabel));
        OnPropertyChanged(nameof(DisplayFileName));
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _seriesMembers = [];
        _coverImage?.Dispose();
    }
}
