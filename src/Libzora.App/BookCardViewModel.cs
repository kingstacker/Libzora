using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Presentation data for one local-library book. The domain model remains
/// untouched; this class only turns paths and enum values into UI labels and a
/// loadable Avalonia bitmap.
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
    private bool _isTooltipEnabled = true;
    private bool _coverLoadAttempted;
    private bool _disposed;
    private int _coverVersion;
    private CancellationTokenSource? _coverCancellation;
    public Task CoverLoadingTask { get; private set; } = Task.CompletedTask;

    // A shelf card can stand in for a whole series. Members start with the
    // visible front volume and then follow series order; other member cards
    // stay in the page list for actions but are not rendered in the shelf.
    private IReadOnlyList<BookCardViewModel> _seriesMembers = [];
    private string? _seriesKey;
    private string? _seriesTitle;

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

    public bool IsTooltipEnabled
    {
        get => _isTooltipEnabled;
        set => SetProperty(ref _isTooltipEnabled, value);
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
            return _seriesMembers.All(member => member.LibraryPresence == BookLibraryPresence.KindleOnly)
                ? BookLibraryPresence.KindleOnly
                : BookLibraryPresence.ComputerOnly;
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

    // The WinUI reference draws three distinct monochrome glyphs for the
    // library-presence badge (PC only / Kindle only / both). Avalonia keeps
    // the same three-state model so the comparison stays readable instead of
    // using a fixed icon on every card.
    public bool ComputerOnlyPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.ComputerOnly;
    public bool KindleOnlyPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.KindleOnly;
    public bool BothLibrariesPresenceVisibility => EffectiveLibraryPresence == BookLibraryPresence.Both;

    public string PresenceLabel
    {
        get
        {
            if (IsSeriesStack)
            {
                var total = _seriesMembers.Count;
                var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
                if (both == total) return UiText.Get("全部 {0} 册在电脑和当前设备均有", total);
                if (both > 0) return UiText.Get("{0}/{1} 册已在当前设备，{2} 册仅在电脑", both, total, total - both);
                return UiText.Get("全部 {0} 册仅在电脑书库", total);
            }
            return LibraryPresence switch
            {
                BookLibraryPresence.Both => UiText.Get("电脑与当前设备都有"),
                BookLibraryPresence.KindleOnly => UiText.Get("仅当前设备有"),
                _ => UiText.Get("仅电脑书库有")
            };
        }
    }

    // The presence badge sits in the card's text footer; when gallery mode
    // hides the footer the badge must go with it so only the cover remains.
    public bool PresenceVisibility => _isLibraryPresenceVisible && _isGalleryTextVisible;

    public BookSyncStatus SyncStatus
    {
        get => _syncStatus;
        private set
        {
            if (!SetProperty(ref _syncStatus, value)) return;
            NotifySyncStatusChanged();
        }
    }

    private BookSyncStatus EffectiveSyncStatus
    {
        get
        {
            if (!IsSeriesStack) return SyncStatus;
            if (_seriesMembers.Any(member => member.SyncStatus == BookSyncStatus.NotDownloaded))
                return BookSyncStatus.NotDownloaded;
            if (_seriesMembers.Any(member => member.SyncStatus == BookSyncStatus.NotSynced))
                return BookSyncStatus.NotSynced;
            return BookSyncStatus.Synced;
        }
    }

    public double SyncStatusOpacity
    {
        get
        {
            if (!IsSeriesStack) return 1d;
            var synced = _seriesMembers.Count(member => member.SyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded);
            return synced > 0 && synced < _seriesMembers.Count ? 0.58d : 1d;
        }
    }

    public bool SyncStatusVisibility => _isSyncStatusVisible;
    public bool SyncNotSyncedVisibility => EffectiveSyncStatus == BookSyncStatus.NotSynced;
    // A local copy that is already part of the remote baseline is simply
    // synced from the user's perspective; the separate enum value is kept
    // internally for service/test compatibility.
    public bool SyncSyncedVisibility => EffectiveSyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded;
    public bool SyncNotDownloadedVisibility => EffectiveSyncStatus == BookSyncStatus.NotDownloaded;
    public string SyncStatusLabel
    {
        get
        {
            if (IsSeriesStack)
            {
                var total = _seriesMembers.Count;
                var notDownloaded = _seriesMembers.Count(member => member.SyncStatus == BookSyncStatus.NotDownloaded);
                var synced = _seriesMembers.Count(member => member.SyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded);
                if (synced == total) return UiText.Get("全部 {0} 册已同步", total);
                if (notDownloaded > 0) return UiText.Get("云端同步：{0}/{1} 册已下载，{2} 册待下载", total - notDownloaded, total, notDownloaded);
                return UiText.Get("云端同步：{0}/{1} 册已同步，{2} 册未同步", synced, total, total - synced);
            }
            return SyncStatus switch
            {
                BookSyncStatus.Synced => UiText.Get("已同步"),
                BookSyncStatus.NotDownloaded => UiText.Get("已同步到云端，尚未下载；点击图标下载"),
                BookSyncStatus.Downloaded => UiText.Get("已同步"),
                _ => UiText.Get("未同步；点击图标上传")
            };
        }
    }

    public string SeriesSummaryLabel
    {
        get
        {
            if (!IsSeriesStack) return SeriesCountLabel;
            var parts = new List<string> { SeriesCountLabel };
            if (_isSyncStatusVisible)
            {
                var synced = _seriesMembers.Count(member => member.SyncStatus is BookSyncStatus.Synced or BookSyncStatus.Downloaded);
                parts.Add(UiText.Get("{0}/{1} 已同步", synced, _seriesMembers.Count));
            }
            if (_isLibraryPresenceVisible)
            {
                var both = _seriesMembers.Count(member => member.LibraryPresence == BookLibraryPresence.Both);
                if (both > 0) parts.Add(UiText.Get("{0}/{1} 在设备", both, _seriesMembers.Count));
            }
            return string.Join(" · ", parts);
        }
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

    private void NotifySyncStatusChanged()
    {
        OnPropertyChanged(nameof(SyncStatusLabel));
        OnPropertyChanged(nameof(SyncStatusOpacity));
        OnPropertyChanged(nameof(SyncNotSyncedVisibility));
        OnPropertyChanged(nameof(SyncSyncedVisibility));
        OnPropertyChanged(nameof(SyncNotDownloadedVisibility));
        OnPropertyChanged(nameof(SeriesSummaryLabel));
    }

    public bool GalleryTextVisibility => _isGalleryTextVisible;

    public IReadOnlyList<BookCardViewModel> SeriesMembers => _seriesMembers;
    public string? SeriesKey => _seriesKey;
    public string? SeriesTitle => _seriesTitle;
    public bool IsSeriesStack => _seriesMembers.Count > 1;
    public int SeriesCount => _seriesMembers.Count;
    public string SeriesCountLabel => UiText.Get("{0} 册", _seriesMembers.Count);
    public string SeriesBadgeTip => UiText.Get("展开系列：{0}", _seriesTitle ?? Title);
    public bool SeriesBadgeVisibility => IsSeriesStack;

    public string DisplayTitle => IsSeriesStack ? _seriesTitle ?? Title : Title;
    public string DisplayMetaLabel => IsSeriesStack ? SeriesCountLabel : FormatLabel;
    public string DisplayFileSummaryLabel => IsSeriesStack ? SeriesCountLabel : FileSummaryLabel;

    public void SetSeriesMembers(
        string? key,
        string? title,
        IReadOnlyList<BookCardViewModel>? members)
    {
        var next = members is { Count: > 1 } ? members : Array.Empty<BookCardViewModel>();
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
        else if (e.PropertyName == nameof(SyncStatus))
        {
            NotifySyncStatusChanged();
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
        OnPropertyChanged(nameof(DisplayFileSummaryLabel));
        NotifyPresenceChanged();
        NotifySyncStatusChanged();
    }

    /// <summary>
    /// Releases rear-cover bitmaps when the stacked card scrolls away. The
    /// front card itself is released by <see cref="UnloadCover"/>.
    /// </summary>
    public void UnloadSeriesCovers(BookCardViewModel? keep)
    {
        foreach (var member in _seriesMembers)
            if (!ReferenceEquals(member, this) && !ReferenceEquals(member, keep))
                member.UnloadCover();
    }

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
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayMetaLabel));
        OnPropertyChanged(nameof(DisplayFileSummaryLabel));
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
        OnPropertyChanged(nameof(SeriesCountLabel));
        OnPropertyChanged(nameof(SeriesBadgeTip));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayMetaLabel));
        OnPropertyChanged(nameof(DisplayFileSummaryLabel));
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var member in _seriesMembers)
            member.PropertyChanged -= SeriesMember_PropertyChanged;
        _seriesMembers = [];
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
