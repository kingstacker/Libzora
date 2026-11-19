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
    private double _downloadProgress;
    private string _statusMessage = string.Empty;

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
    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => UiText.Get("电脑与当前设备都有"),
        BookLibraryPresence.ComputerOnly => UiText.Get("仅电脑书库"),
        _ => UiText.Get("仅当前设备")
    };

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
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(ComputerOnlyPresenceVisibility));
            OnPropertyChanged(nameof(KindleOnlyPresenceVisibility));
            OnPropertyChanged(nameof(BothLibrariesPresenceVisibility));
        }
    }
    public bool ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both;
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
    }

    public void Dispose()
    {
        UiText.LanguageChanged -= OnLanguageChanged;
        _coverImage?.Dispose();
    }
}
