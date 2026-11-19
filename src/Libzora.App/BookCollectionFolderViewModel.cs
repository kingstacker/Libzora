using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Collection folder view model.
/// </summary>
public sealed class BookCollectionFolderViewModel : ObservableObject, IDisposable
{
    private readonly Bitmap?[] _covers = new Bitmap?[3];
    private static readonly SemaphoreSlim CoverGate = new(2, 2);
    private string _coverStamp = string.Empty;
    private int _coverVersion;
    private bool _disposed;
    public Task CoverLoadingTask { get; private set; } = Task.CompletedTask;

    public BookCollectionFolderViewModel(
        BookCollection collection,
        int bookCount,
        string dataRoot,
        IReadOnlyList<string?> coverPaths)
    {
        UiText.LanguageChanged += OnLanguageChanged;
        Update(collection, bookCount, dataRoot, coverPaths);
    }

    public BookCollection Collection { get; private set; } = null!;
    public void Update(BookCollection collection, int count, string root, IReadOnlyList<string?> paths)
    {
        Collection = collection;
        BookCount = count;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BookCountLabel));
        var stamp = string.Join("|", paths.Select(path =>
        {
            var file = new FileInfo(Path.Combine(root, path ?? "__missing__"));
            return file.Exists ? $"{file.FullName}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : path;
        }));
        if (stamp == _coverStamp) return;
        _coverStamp = stamp;
        var version = ++_coverVersion;
        CoverLoadingTask = LoadCoversAsync(root, paths.ToArray(), version);
    }

    private async Task LoadCoversAsync(string root, IReadOnlyList<string?> paths, int version)
    {
        await CoverGate.WaitAsync();
        Bitmap?[]? loaded = null;
        try
        {
            if (_disposed || version != _coverVersion) return;
            loaded = await Task.Run(() => Enumerable.Range(0, 3)
                .Select(index => LoadCoverImage(root, index < paths.Count ? paths[index] : null, 96)).ToArray());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || version != _coverVersion) return;
                for (var i = 0; i < 3; i++)
                {
                    _covers[i]?.Dispose();
                    _covers[i] = loaded[i];
                    loaded[i] = null;
                    OnPropertyChanged($"Cover{i + 1}");
                }
            });
        }
        finally
        {
            if (loaded is not null) foreach (var image in loaded) image?.Dispose();
            CoverGate.Release();
        }
    }

    public string Name => string.Equals(
        Collection.Name,
        BookLibraryDefaults.UncollectedCollectionName,
        StringComparison.Ordinal)
        ? UiText.Get("未收藏")
        : UiText.Localize(Collection.Name);
    public int BookCount { get; private set; }
    public string BookCountLabel => UiText.Get("{0} 本书", BookCount);
    public Bitmap? Cover1 => _covers[0];
    public Bitmap? Cover2 => _covers[1];
    public Bitmap? Cover3 => _covers[2];

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BookCountLabel));
    }

    public void Dispose()
    {
        _disposed = true;
        ++_coverVersion;
        UiText.LanguageChanged -= OnLanguageChanged;
        foreach (var cover in _covers) cover?.Dispose();
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
}
