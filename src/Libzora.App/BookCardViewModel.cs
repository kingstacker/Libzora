using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// ObservableCollection with a single-notification bulk replace. A library
/// refresh used to raise one Remove/Insert/Move per card, and every event made
/// the virtualizing panel tear down its realized containers.
/// </summary>

/// <summary>
/// Library book card view model.
/// </summary>
public sealed class BookCardViewModel : ObservableObject, IDisposable
{
    public BookCardViewModel(Book book, string dataRoot)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Book = book;
        DataRoot = dataRoot;
        _coverFileStamp = CoverFileStamp(book);
        Refresh();
    }

    public Book Book { get; private set; }
    public string DataRoot { get; }
    public string Title => _title;
    public string Authors => _authors;
    // A book can contain several EPUB files (for example the original plus a
    // translated or bilingual edition). The old gallery only showed the
    // distinct extension, which made a successfully imported edition look as
    // if it had never been added. Keep the list view's detailed summary
    // separate and surface the file count in the compact gallery label.
    public string FormatLabel => _formatLabel;
    public string FileCountLabel => _fileCountLabel;
    public string TotalSizeLabel => _totalSizeLabel;
    public string FileSummaryLabel => _fileSummaryLabel;
    public string ReadingStatusLabel => _readingStatusLabel;
    public string ReadingStateLabel => _readingStateLabel;
    public string OrganizationLabel => _organizationLabel;
    public string PublicationLabel => _publicationLabel;
    public string IdentifierLabel => _identifierLabel;
    public string DescriptionSummaryLabel => _descriptionSummaryLabel;
    public string UpdatedLabel => _updatedLabel;
    public string DescriptionLabel => _descriptionLabel;
    public Bitmap? CoverImage { get; private set; }

    // Every visible label is derived from the immutable Book snapshot and the
    // current language. Computing them once per Refresh keeps binding reads
    // (including Text + ToolTip.Tip on the same property) allocation-free.
    private string _title = "";
    private string _authors = "";
    private string _formatLabel = "";
    private string _fileCountLabel = "";
    private string _totalSizeLabel = "";
    private string _fileSummaryLabel = "";
    private string _readingStatusLabel = "";
    private string _readingStateLabel = "";
    private string _organizationLabel = "";
    private string _publicationLabel = "";
    private string _identifierLabel = "";
    private string _descriptionSummaryLabel = "";
    private string _updatedLabel = "";
    private string _descriptionLabel = "";

    // Keep selection on the card itself. The legacy WinUI shelf did not use
    // the list control's selection fill; it drew a black outline around the
    // exact 154-DIP card footprint instead.
    private bool _isSelected;
    private bool _isSoleSelected;
    private bool _isReadActionVisible;
    private bool _isMultiSelected;
    private bool _isHovered;
    private bool _coverLoadAttempted;
    private bool _disposed;
    private int _coverVersion;
    private CancellationTokenSource? _coverCancellation;
    public Task CoverLoadingTask { get; private set; } = Task.CompletedTask;

    // Any selection (single click or multi-select) turns the card outline
    // black; the check badge below is reserved for genuine multi-selection.
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

    // Multi-selection is for batch actions. A sole selection can also come
    // from a marquee or context menu, so it does not imply open book details.
    public bool IsSoleSelected
    {
        get => _isSoleSelected;
        set => SetProperty(ref _isSoleSelected, value);
    }

    // The cover's reading button and scrim follow the open details pane, not
    // selection alone. Closing details or returning from the reader hides them.
    public bool IsReadActionVisible
    {
        get => _isReadActionVisible;
        set => SetProperty(ref _isReadActionVisible, value);
    }

    // Hovering the whole card (cover or text) shows the same thin black
    // frame as selection, so the outline lives on the card, not on the cover
    // image alone.
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (SetProperty(ref _isHovered, value))
                OnPropertyChanged(nameof(IsFrameVisible));
        }
    }

    // The black frame around the entire card appears on hover or selection.
    public bool IsFrameVisible => IsSelected || IsHovered;

    // The original gallery always surfaced where a book is available. The
    // portable library starts with the authoritative local copy and changes
    // this value when a future device scan supplies a matching Kindle copy.
    private BookLibraryPresence _libraryPresence = BookLibraryPresence.ComputerOnly;
    private bool _isLibraryPresenceVisible = true;
    private bool _isGalleryTextVisible = true;
    private bool _isSyncStatusVisible = true;
    private BookSyncStatus _syncStatus = BookSyncStatus.NotSynced;

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

    // The WinUI reference draws three distinct monochrome glyphs for the
    // library-presence badge (PC only / Kindle only / both). Avalonia keeps
    // the same three-state model so the comparison stays readable instead of
    // using a fixed icon on every card.
    public bool ComputerOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => LibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => LibraryPresence == BookLibraryPresence.Both;

    public string PresenceLabel => LibraryPresence switch
    {
        BookLibraryPresence.Both => UiText.Get("电脑与当前设备都有"),
        BookLibraryPresence.KindleOnly => UiText.Get("仅当前设备有"),
        _ => UiText.Get("仅电脑书库有")
    };

    // The presence badge sits in the card's text footer; when gallery mode
    // hides the footer the badge must go with it so only the cover remains.
    public bool PresenceVisibility => _isLibraryPresenceVisible && _isGalleryTextVisible;

    public BookSyncStatus SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (!SetProperty(ref _syncStatus, value)) return;
            OnPropertyChanged(nameof(SyncStatusLabel));
            OnPropertyChanged(nameof(SyncNotSyncedVisibility));
            OnPropertyChanged(nameof(SyncSyncedVisibility));
            OnPropertyChanged(nameof(SyncNotDownloadedVisibility));
        }
    }

    public bool SyncStatusVisibility => _isSyncStatusVisible;
    public bool SyncNotSyncedVisibility => SyncStatus == BookSyncStatus.NotSynced;
    // A local copy that is already part of the remote baseline is simply
    // synced from the user's perspective; the separate enum value is kept
    // internally for service/test compatibility.
    public bool SyncSyncedVisibility => SyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded;
    public bool SyncNotDownloadedVisibility => SyncStatus == BookSyncStatus.NotDownloaded;
    public string SyncStatusLabel => SyncStatus switch
    {
        BookSyncStatus.Synced => UiText.Get("已同步"),
        BookSyncStatus.NotDownloaded => UiText.Get("已同步到云端，尚未下载；点击图标下载"),
        BookSyncStatus.Downloaded => UiText.Get("已同步"),
        _ => UiText.Get("未同步；点击图标上传")
    };

    public bool GalleryTextVisibility => _isGalleryTextVisible;

    // Gallery mode trims the card to the cover alone so no blank text block
    // remains under the image.
    public double CardHeight => _isGalleryTextVisible ? 292 : 214;

    public void SetLibraryPresence(BookLibraryPresence presence) => LibraryPresence = presence;

    public void SetSyncStatus(BookSyncStatus status) => SyncStatus = status;

    public void SetSyncStatusVisible(bool visible)
    {
        if (_isSyncStatusVisible == visible) return;
        _isSyncStatusVisible = visible;
        OnPropertyChanged(nameof(SyncStatusVisibility));
    }

    public void SetLibraryPresenceVisible(bool visible)
    {
        if (_isLibraryPresenceVisible == visible) return;
        _isLibraryPresenceVisible = visible;
        OnPropertyChanged(nameof(PresenceVisibility));
    }

    public void SetGalleryTextVisible(bool visible)
    {
        if (_isGalleryTextVisible == visible) return;
        _isGalleryTextVisible = visible;
        OnPropertyChanged(nameof(GalleryTextVisibility));
        OnPropertyChanged(nameof(PresenceVisibility));
        OnPropertyChanged(nameof(CardHeight));
    }

    private string _coverFileStamp = "";
    private string CoverFileStamp(Book book)
    {
        if (string.IsNullOrWhiteSpace(book.CoverPath)) return "";
        var info = new FileInfo(Path.Combine(DataRoot, book.CoverPath));
        return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
    }

    public void UpdateBook(Book book)
    {
        // The page query returns the same UpdatedAt for unchanged books, so a
        // refresh no longer stats every cover file just to rediscover that
        // nothing changed.
        var filesChanged = !Book.Files.Select(file => (file.Id, file.Sha256, file.RelativePath))
            .SequenceEqual(book.Files.Select(file => (file.Id, file.Sha256, file.RelativePath)));
        var metadataChanged = Book.UpdatedAt != book.UpdatedAt
            || Book.Title != book.Title
            || Book.CoverPath != book.CoverPath
            || filesChanged;
        var stamp = metadataChanged ? CoverFileStamp(book) : _coverFileStamp;
        var coverChanged = metadataChanged
            && (stamp != _coverFileStamp || Book.Title != book.Title
                || Book.CoverPath != book.CoverPath || filesChanged);
        var wasLoaded = _coverLoadAttempted;
        _coverFileStamp = stamp;
        Book = book;
        Refresh(coverChanged);
        if (coverChanged && wasLoaded) LoadCover();
    }

    public void Refresh(bool unloadCover = true)
    {
        if (unloadCover) UnloadCover();

        var files = Book.Files;
        var title = UiText.Localize(Book.Title);
        var authors = UiText.Localize(Book.Authors);
        var fileCountLabel = files.Count == 0 ? string.Empty : UiText.Get("{0} 个文件", files.Count);
        var totalSizeLabel = files.Count == 0 ? UiText.Get("暂无文件") : FormatSize(files.Sum(file => file.Size));
        var formatLabel = files.Count <= 1
            ? Book.FormatSummary
            : UiText.Get("{0} · {1}", Book.FormatSummary, fileCountLabel);
        var fileSummaryLabel = string.Join(" · ", new[] { Book.FormatSummary, fileCountLabel, totalSizeLabel }
            .Where(value => value.Length > 0));
        var readingStatusLabel = Book.ReadingStatus switch
        {
            LibraryReadingStatus.Reading => UiText.Get("阅读中"),
            LibraryReadingStatus.Finished => UiText.Get("已读"),
            _ => UiText.Get("待读")
        };
        var readingStateLabel = Book.IsFavorite
            ? UiText.Get("{0} · ★ 收藏", readingStatusLabel)
            : readingStatusLabel;
        var organizationLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Category) ? string.Empty : UiText.Get("分类：{0}", Book.Category),
            string.IsNullOrWhiteSpace(Book.Tags) ? string.Empty : UiText.Get("标签：{0}", Book.Tags),
            empty: UiText.Get("暂无分类或标签"));
        var publicationLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Publisher) ? string.Empty : UiText.Get("出版社：{0}", Book.Publisher),
            string.IsNullOrWhiteSpace(Book.PublishDate) ? string.Empty : UiText.Get("出版：{0}", Book.PublishDate),
            string.IsNullOrWhiteSpace(Book.PageCount) ? string.Empty : UiText.Get("页数：{0}", Book.PageCount),
            string.IsNullOrWhiteSpace(Book.Binding) ? string.Empty : UiText.Get("装帧：{0}", Book.Binding),
            empty: UiText.Get("暂无出版信息"));
        var identifierLabel = JoinLabels(
            string.IsNullOrWhiteSpace(Book.Isbn) ? string.Empty : UiText.Get("ISBN：{0}", Book.Isbn),
            Book.OnlineRating is { } rating
                ? Book.OnlineRatingCount is { } count
                    ? UiText.Get("在线评分：{0:0.0}（{1} 人评价）", rating, count)
                    : UiText.Get("在线评分：{0:0.0}", rating)
                : string.Empty,
            empty: UiText.Get("暂无 ISBN 或评分"));
        string descriptionLabel;
        string descriptionSummaryLabel;
        if (string.IsNullOrWhiteSpace(Book.Description))
        {
            descriptionLabel = descriptionSummaryLabel = UiText.Get("暂无简介");
        }
        else
        {
            descriptionLabel = Book.Description;
            var description = Regex.Replace(Book.Description, @"\s+", " ").Trim();
            descriptionSummaryLabel = description.Length <= 90 ? description : $"{description[..90]}…";
        }
        var updatedLabel = UiText.Get("更新于 {0:yyyy-MM-dd HH:mm}", Book.UpdatedAt.ToLocalTime());

        SetLabel(ref _title, title, nameof(Title));
        SetLabel(ref _authors, authors, nameof(Authors));
        SetLabel(ref _formatLabel, formatLabel, nameof(FormatLabel));
        SetLabel(ref _fileCountLabel, fileCountLabel, nameof(FileCountLabel));
        SetLabel(ref _totalSizeLabel, totalSizeLabel, nameof(TotalSizeLabel));
        SetLabel(ref _fileSummaryLabel, fileSummaryLabel, nameof(FileSummaryLabel));
        SetLabel(ref _readingStatusLabel, readingStatusLabel, nameof(ReadingStatusLabel));
        SetLabel(ref _readingStateLabel, readingStateLabel, nameof(ReadingStateLabel));
        SetLabel(ref _organizationLabel, organizationLabel, nameof(OrganizationLabel));
        SetLabel(ref _publicationLabel, publicationLabel, nameof(PublicationLabel));
        SetLabel(ref _identifierLabel, identifierLabel, nameof(IdentifierLabel));
        SetLabel(ref _descriptionSummaryLabel, descriptionSummaryLabel, nameof(DescriptionSummaryLabel));
        SetLabel(ref _updatedLabel, updatedLabel, nameof(UpdatedLabel));
        SetLabel(ref _descriptionLabel, descriptionLabel, nameof(DescriptionLabel));
        OnPropertyChanged(nameof(CoverImage));
    }

    private void SetLabel(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private static string JoinLabels(string first, string second, string empty)
    {
        if (first.Length == 0) return second.Length == 0 ? empty : second;
        return second.Length == 0 ? first : string.Concat(first, " · ", second);
    }

    private static string JoinLabels(string first, string second, string third, string fourth, string empty)
    {
        var parts = new List<string>(4);
        if (first.Length > 0) parts.Add(first);
        if (second.Length > 0) parts.Add(second);
        if (third.Length > 0) parts.Add(third);
        if (fourth.Length > 0) parts.Add(fourth);
        return parts.Count == 0 ? empty : string.Join(" · ", parts);
    }

    public void LoadCover()
    {
        if (_disposed || _coverLoadAttempted) return;
        _coverLoadAttempted = true;
        var version = ++_coverVersion;
        var cancellation = new CancellationTokenSource();
        _coverCancellation = cancellation;
        CoverLoadingTask = LoadCoverAsync(Book, version, cancellation);
    }

    private async Task LoadCoverAsync(Book book, int version, CancellationTokenSource cancellation)
    {
        Bitmap? image = null;
        try
        {
            image = await CoverThumbnailCache.LoadAsync(book, DataRoot, cancellation.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || version != _coverVersion) return;
                CoverImage = image;
                image = null;
                OnPropertyChanged(nameof(CoverImage));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (version == _coverVersion) _coverLoadAttempted = false;
        }
        finally
        {
            image?.Dispose();
            if (ReferenceEquals(_coverCancellation, cancellation)) _coverCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Releases the decoded bitmap while keeping the book metadata alive.
    /// Virtualized card roots call this when they leave the visual tree so a
    /// long scroll does not retain every cover visited by the user.
    /// </summary>
    public void UnloadCover()
    {
        ++_coverVersion;
        _coverCancellation?.Cancel();
        if (CoverImage is null && !_coverLoadAttempted) return;
        CoverImage?.Dispose();
        CoverImage = null;
        _coverLoadAttempted = false;
        OnPropertyChanged(nameof(CoverImage));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Refresh recomputes every cached label; the remaining properties are
        // not derived from Book and only need a notification.
        Refresh(unloadCover: false);
        OnPropertyChanged(nameof(PresenceLabel));
        OnPropertyChanged(nameof(SyncStatusLabel));
    }

    public void Dispose()
    {
        _disposed = true;
        UiText.LanguageChanged -= OnLanguageChanged;
        UnloadCover();
    }

    private static Bitmap? LoadCoverImage(string dataRoot, string? relativePath, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(dataRoot, relativePath));
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(
                stream,
                decodeWidth,
                BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}
