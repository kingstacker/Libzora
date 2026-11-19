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

public sealed class LibraryViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 200;

    private readonly IBookLibraryService _library;
    private readonly string _dataRoot;
    private readonly Dictionary<Guid, BookCardViewModel> _bookCards = [];
    private string _searchText = string.Empty;
    private string? _authorFilter;
    private string? _tagFilter;
    private string? _formatFilter;
    private string? _categoryFilter;
    private Guid? _collectionFilterId;
    private string? _collectionFilterName;
    private LibraryReadingStatus? _readingStatusFilter;
    private bool? _favoriteFilter;
    private LibrarySortMode _sortMode;
    private int _pageIndex;
    private int _pageCount = 1;
    private int _bookCount;
    private int _filteredBookCount;
    private int _viewRequestId;
    private CancellationTokenSource? _viewCancellation;
    private bool _isBusy;
    private string _statusText = UiText.Get("准备就绪");

    public LibraryViewModel(IBookLibraryService library, string dataRoot)
    {
        _library = library;
        _dataRoot = dataRoot;
        // MainWindow owns the language-change refresh (cards still subscribe
        // individually for their cached labels); refreshing here as well ran
        // two identical database queries and cancelled one of them.
    }

    public BulkObservableCollection<BookCardViewModel> Books { get; } = [];
    public IReadOnlyList<Book> LibraryBooks { get; private set; } = [];
    public IReadOnlyList<string> AvailableAuthors { get; private set; } = [];
    public IReadOnlyList<string> AvailableTags { get; private set; } = [];
    public IReadOnlyList<string> AvailableFormats { get; private set; } = [];
    public IReadOnlyList<string> AvailableCategories { get; private set; } = [];

    public int PageIndex => _pageIndex;
    public int PageCount => _pageCount;
    public int BookCount => _bookCount;
    public int FilteredBookCount => _filteredBookCount;
    public bool CanGoToPreviousPage => _pageIndex > 0;
    public bool CanGoToNextPage => _pageIndex + 1 < _pageCount;
    public string PageStatusText => _pageCount <= 1
        ? string.Empty
        : UiText.Get("第 {0} / {1} 页", _pageIndex + 1, _pageCount);

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? AuthorFilter
    {
        get => _authorFilter;
        set => SetFilter(ref _authorFilter, value);
    }

    public string? TagFilter
    {
        get => _tagFilter;
        set => SetFilter(ref _tagFilter, value);
    }

    public string? FormatFilter
    {
        get => _formatFilter;
        set => SetFilter(ref _formatFilter, value);
    }

    public string? CategoryFilter
    {
        get => _categoryFilter;
        set => SetFilter(ref _categoryFilter, value);
    }

    public Guid? CollectionFilterId
    {
        get => _collectionFilterId;
        set => SetFilter(ref _collectionFilterId, value);
    }

    public string? CollectionFilterName
    {
        get => _collectionFilterName;
        set => SetProperty(ref _collectionFilterName, value);
    }

    public LibraryReadingStatus? ReadingStatusFilter
    {
        get => _readingStatusFilter;
        set => SetFilter(ref _readingStatusFilter, value);
    }

    public bool? FavoriteFilter
    {
        get => _favoriteFilter;
        set => SetFilter(ref _favoriteFilter, value);
    }

    public LibrarySortMode SortMode
    {
        get => _sortMode;
        set => SetFilter(ref _sortMode, value);
    }

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(AuthorFilter)
        || !string.IsNullOrWhiteSpace(TagFilter)
        || !string.IsNullOrWhiteSpace(FormatFilter)
        || !string.IsNullOrWhiteSpace(CategoryFilter)
        || ReadingStatusFilter is not null
        || FavoriteFilter is not null
        || CollectionFilterId is not null;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public event EventHandler? ViewChanged;

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        IsBusy = true;
        try
        {
            // Microsoft.Data.Sqlite executes its "async" commands synchronously,
            // so run the whole query on the thread pool instead of letting it
            // block the UI thread between awaits.
            progress?.Invoke("正在统计书籍…");
            _bookCount = await Task.Run(
                () => _library.GetBookCountAsync(cancellationToken),
                cancellationToken);
            OnPropertyChanged(nameof(BookCount));

            progress?.Invoke("正在读取筛选选项…");
            var options = await Task.Run(
                () => _library.GetFilterOptionsAsync(cancellationToken),
                cancellationToken);
            AvailableAuthors = options.Authors;
            AvailableTags = options.Tags;
            AvailableFormats = options.Formats;
            AvailableCategories = options.Categories;

            OnPropertyChanged(nameof(AvailableAuthors));
            OnPropertyChanged(nameof(AvailableTags));
            OnPropertyChanged(nameof(AvailableFormats));
            OnPropertyChanged(nameof(AvailableCategories));
            await RefreshViewAsync(resetPage: true, cancellationToken, progress);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshView() => _ = RefreshViewSafelyAsync(resetPage: true);

    public void GoToPreviousPage()
    {
        if (!CanGoToPreviousPage) return;
        _pageIndex--;
        _ = RefreshViewSafelyAsync(resetPage: false);
    }

    public void GoToNextPage()
    {
        if (!CanGoToNextPage) return;
        _pageIndex++;
        _ = RefreshViewSafelyAsync(resetPage: false);
    }

    /// <summary>
    /// Returns the collections that contain at least one book matching the
    /// current search/filter. The shelf only materializes one page, so the
    /// caller cannot infer this from <see cref="Books"/> alone.
    /// </summary>
    public async Task<HashSet<Guid>> GetMatchingCollectionIdsAsync(
        IReadOnlySet<Guid> requiredCollectionIds,
        CancellationToken cancellationToken = default)
    {
        var books = Books.ToArray();
        var matching = new HashSet<Guid>();
        foreach (var card in books)
            matching.UnionWith(card.Book.CollectionIds);
        if (requiredCollectionIds.IsSubsetOf(matching) || books.Length >= FilteredBookCount)
            return matching;

        const int pageSize = 500;
        var loaded = 0;
        for (var pageIndex = 0; loaded < FilteredBookCount; pageIndex++)
        {
            var page = await _library.SearchPageAsync(
                SearchText,
                AuthorFilter,
                TagFilter,
                FormatFilter,
                CategoryFilter,
                CollectionFilterId,
                ReadingStatusFilter,
                FavoriteFilter,
                SortMode,
                pageIndex,
                pageSize,
                cancellationToken);
            if (page.Books.Count == 0) break;
            foreach (var book in page.Books)
                matching.UnionWith(book.CollectionIds);
            loaded += page.Books.Count;
            if (requiredCollectionIds.IsSubsetOf(matching) || page.Books.Count < pageSize) break;
        }
        return matching;
    }

    public void Dispose()
    {
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
        foreach (var card in _bookCards.Values)
            card.Dispose();
        _bookCards.Clear();
        Books.Clear();
    }

    public async Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ImportBookConflict, Task<ImportConflictResolution>>? conflictResolver = null,
        IReadOnlyDictionary<string, TxtImportRules>? txtImportRules = null)
    {
        IsBusy = true;
        try
        {
            var result = await _library.ImportAsync(paths, progress, cancellationToken, conflictResolver, txtImportRules);
            await RefreshAsync(cancellationToken);
            StatusText = result.FailureCount == 0
                ? result.SkippedCount == 0
                    ? UiText.Get("已新增 {0} 个文件", result.AddedCount)
                    : UiText.Get("已新增 {0} 个文件，跳过 {1} 个重复或已有文件", result.AddedCount, result.SkippedCount)
                : result.SkippedCount == 0
                    ? UiText.Get("已新增 {0} 个文件，{1} 个失败", result.AddedCount, result.FailureCount)
                    : UiText.Get("已新增 {0} 个文件，跳过 {1} 个重复或已有文件，{2} 个失败", result.AddedCount, result.SkippedCount, result.FailureCount);
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteBookAsync(Book book, CancellationToken cancellationToken = default)
    {
        await _library.DeleteAsync(book.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = UiText.Get("已移入回收站：《{0}》", book.Title);
    }

    public async Task DeleteFileAsync(Book book, BookFile file, CancellationToken cancellationToken = default)
    {
        await _library.DeleteFileAsync(book.Id, file.Id, cancellationToken);
        await RefreshAsync(cancellationToken);
        StatusText = UiText.Get("已移入回收站：{0} 文件", file.Format.ToUpperInvariant());
    }

    public string GetAbsoluteFilePath(BookFile file) => _library.GetAbsoluteFilePath(file);

    public async Task RefreshViewAsync(
        bool resetPage = true,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        if (resetPage) _pageIndex = 0;
        var requestId = Interlocked.Increment(ref _viewRequestId);
        _viewCancellation?.Cancel();
        _viewCancellation?.Dispose();
        _viewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestToken = _viewCancellation.Token;
        IsBusy = true;
        try
        {
            progress?.Invoke("正在查询书架内容…");
            var page = await Task.Run(
                () => _library.SearchPageAsync(
                    SearchText,
                    AuthorFilter,
                    TagFilter,
                    FormatFilter,
                    CategoryFilter,
                    CollectionFilterId,
                    ReadingStatusFilter,
                    FavoriteFilter,
                    SortMode,
                    _pageIndex,
                    PageSize,
                    requestToken),
                requestToken);
            requestToken.ThrowIfCancellationRequested();
            if (requestId != _viewRequestId) return;

            progress?.Invoke("正在整理书籍卡片…");
            var lastPageIndex = Math.Max(0, (int)Math.Ceiling(page.TotalCount / (double)PageSize) - 1);
            if (_pageIndex > lastPageIndex)
            {
                _pageIndex = lastPageIndex;
                page = await Task.Run(
                    () => _library.SearchPageAsync(
                        SearchText,
                        AuthorFilter,
                        TagFilter,
                        FormatFilter,
                        CategoryFilter,
                        CollectionFilterId,
                        ReadingStatusFilter,
                        FavoriteFilter,
                        SortMode,
                        _pageIndex,
                        PageSize,
                        requestToken),
                    requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (requestId != _viewRequestId) return;
            }

            LibraryBooks = page.Books;
            _filteredBookCount = page.TotalCount;
            var pageCount = Math.Max(1, (int)Math.Ceiling(page.TotalCount / (double)PageSize));
            if (_pageIndex >= pageCount) _pageIndex = pageCount - 1;
            if (_pageCount != pageCount)
            {
                _pageCount = pageCount;
                OnPropertyChanged(nameof(PageCount));
            }
            OnPropertyChanged(nameof(LibraryBooks));
            OnPropertyChanged(nameof(FilteredBookCount));
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(PageStatusText));

            var incomingIds = page.Books.Select(book => book.Id).ToHashSet();
            foreach (var id in _bookCards.Keys.Where(id => !incomingIds.Contains(id)).ToArray())
            {
                var old = _bookCards[id];
                old.Dispose();
                _bookCards.Remove(id);
            }

            var orderedCards = new List<BookCardViewModel>(page.Books.Count);
            foreach (var book in page.Books)
            {
                if (!_bookCards.TryGetValue(book.Id, out var card))
                {
                    card = new BookCardViewModel(book, _dataRoot);
                    _bookCards.Add(book.Id, card);
                }
                else
                {
                    card.UpdateBook(book);
                }
                orderedCards.Add(card);
            }
            Books.ReplaceAll(orderedCards);
            progress?.Invoke("正在刷新书架界面…");

            var baseStatus = page.TotalCount == 0
                ? _bookCount == 0 ? UiText.Get("书库还是空的")
                    : CollectionFilterId is not null ? UiText.Get("“{0}”收藏夹还是空的", UiText.Localize(CollectionFilterName ?? string.Empty))
                    : UiText.Get("没有符合条件的书籍")
                : HasActiveFilters || !string.IsNullOrWhiteSpace(SearchText)
                    ? CollectionFilterId is not null
                        ? UiText.Get("{0} · {1} 本书", UiText.Localize(CollectionFilterName ?? string.Empty), page.TotalCount)
                        : UiText.Get("找到 {0} 本书", page.TotalCount)
                    : UiText.Get("共 {0} 本书", page.TotalCount);
            StatusText = _pageCount <= 1
                ? baseStatus
                : $"{baseStatus} · {PageStatusText}";

            OnPropertyChanged(nameof(HasActiveFilters));
            ViewChanged?.Invoke(this, EventArgs.Empty);
            progress?.Invoke("书架界面已刷新");
        }
        finally
        {
            if (requestId == _viewRequestId) IsBusy = false;
        }
    }

    private async Task RefreshViewSafelyAsync(bool resetPage)
    {
        try
        {
            await RefreshViewAsync(resetPage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = UiText.Get("刷新书库失败：{0}", UiText.Localize(exception.Message));
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetFilter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return;
        OnPropertyChanged(nameof(HasActiveFilters));
    }
}
