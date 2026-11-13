using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Mobile.Views.Sheets;

public partial class DictionarySheet : UserControl, IFullHeightSheet
{
    // A dismissed sheet may still be importing. Serialize access to the shared
    // manifest and index even if another reader opens a new sheet meanwhile.
    private static readonly SemaphoreSlim DictionaryGate = new(1, 1);
    private readonly MobileServices? _services;
    private readonly DictionaryService? _dictionary;
    private readonly WikipediaLookupService _wikipedia = new();
    private CancellationTokenSource? _lookupCancellation;
    private WikipediaArticle? _wikipediaArticle;
    private bool _busy;
    private string? _pendingRemoval;

    public DictionarySheet() => InitializeComponent();

    public DictionarySheet(MobileServices services, string term)
    {
        _services = services;
        _dictionary = new DictionaryService(services.Paths);
        InitializeComponent();
        QueryBox.Text = term;
        Loaded += OnLoaded;
        DetachedFromVisualTree += (_, _) => _lookupCancellation?.Cancel();
    }

    public bool FillsHeight => true;

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RunAsync(async () =>
        {
            await RefreshDictionariesAsync();
            await LookupAsync();
        });
    }

    private async void OnSearchClick(object? sender, RoutedEventArgs e) =>
        await RunAsync(LookupAsync);

    private async void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunAsync(LookupAsync);
    }

    private async Task LookupAsync()
    {
        var term = QueryBox.Text?.Trim();
        _lookupCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _lookupCancellation = cancellation;
        Results.Children.Clear();
        WikipediaResults.Children.Clear();
        WikipediaStatusText.Text = string.Empty;
        _wikipediaArticle = null;
        if (string.IsNullOrEmpty(term))
        {
            StatusText.Text = "请输入要查询的词语。";
            return;
        }
        StatusText.Text = "正在查询本地词典…";
        WikipediaStatusText.Text = "正在查询维基百科…";
        var localTask = _dictionary!.LookupAsync(term, cancellation.Token);
        var wikipediaTask = _wikipedia.LookupAsync(term, UiText.CurrentLanguage, cancellation.Token);
        try
        {
            try
            {
                var entries = await localTask;
                if (!ReferenceEquals(_lookupCancellation, cancellation)) return;
                StatusText.Text = entries.Count == 0 ? "没有找到本地释义。请先在词典管理中导入词典。" : string.Empty;
                foreach (var entry in entries)
                {
                    var section = new StackPanel { Spacing = 5 };
                    section.Children.Add(new TextBlock { Text = $"{entry.Term} · {entry.DictionaryName}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    section.Children.Add(new SelectableTextBlock { Text = entry.Definition, TextWrapping = TextWrapping.Wrap, LineHeight = 23 });
                    Results.Children.Add(section);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!ReferenceEquals(_lookupCancellation, cancellation)) return;
                StatusText.Text = "本地词典查询失败：" + UiText.Localize(exception.Message);
            }

            try
            {
                var article = await wikipediaTask;
                if (!ReferenceEquals(_lookupCancellation, cancellation)) return;
                if (article is null)
                {
                    WikipediaStatusText.Text = "没有找到同名维基百科条目。普通词义请查本地词典。";
                    return;
                }

                _wikipediaArticle = article;
                WikipediaStatusText.Text = string.Empty;
                WikipediaResults.Children.Add(new TextBlock
                {
                    Text = article.Title,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                WikipediaResults.Children.Add(new SelectableTextBlock
                {
                    Text = article.Extract,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 23
                });
                var open = new Button { Content = "在维基百科中打开" };
                open.Classes.Add("secondary");
                open.Click += async (_, _) =>
                {
                    if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                        await launcher.LaunchUriAsync(article.Url);
                };
                WikipediaResults.Children.Add(open);
                WikipediaResults.Children.Add(new TextBlock
                {
                    Text = "来源：维基百科 · CC BY-SA 4.0",
                    Classes = { "muted" },
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!ReferenceEquals(_lookupCancellation, cancellation)) return;
                WikipediaStatusText.Text = "维基百科查询失败：" + UiText.Localize(exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, cancellation))
            {
                _lookupCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task RefreshDictionariesAsync()
    {
        var items = await _dictionary!.ListAsync();
        Dictionaries.Children.Clear();
        if (items.Count == 0)
        {
            ManagementStatus.Text = "尚未导入词典。";
            return;
        }
        ManagementStatus.Text = $"已导入 {items.Count} 本词典";
        foreach (var item in items)
        {
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{item.Name} · {item.EntryCount} 条 · {(item.Enabled ? "已启用" : "已停用")}",
                TextWrapping = TextWrapping.Wrap
            });
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var toggle = new Button { Content = item.Enabled ? "停用" : "启用" };
            toggle.Classes.Add("secondary");
            toggle.Click += async (_, _) => await RunAsync(async () =>
            {
                using var operation = MobileDataCoordinator.TryBeginOperation();
                if (operation is null) return;
                await _dictionary.SetEnabledAsync(item.Id, !item.Enabled);
                _pendingRemoval = null;
                await RefreshDictionariesAsync();
                await LookupAsync();
            });
            actions.Children.Add(toggle);
            var remove = new Button { Content = _pendingRemoval == item.Id ? "确认移除" : "移除" };
            remove.Classes.Add("secondary");
            remove.Click += async (_, _) => await RunAsync(async () =>
            {
                if (_pendingRemoval != item.Id)
                {
                    _pendingRemoval = item.Id;
                    await RefreshDictionariesAsync();
                    ManagementStatus.Text = $"再次点击确认移除「{item.Name}」。";
                    return;
                }
                using var operation = MobileDataCoordinator.TryBeginOperation();
                if (operation is null) return;
                await _dictionary.RemoveAsync(item.Id);
                _pendingRemoval = null;
                await RefreshDictionariesAsync();
                await LookupAsync();
            });
            actions.Children.Add(remove);
            if (_pendingRemoval == item.Id)
            {
                var cancel = new Button { Content = "取消" };
                cancel.Classes.Add("secondary");
                cancel.Click += async (_, _) => await RunAsync(async () =>
                {
                    _pendingRemoval = null;
                    await RefreshDictionariesAsync();
                });
                actions.Children.Add(cancel);
            }
            panel.Children.Add(actions);
            Dictionaries.Children.Add(panel);
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _services is null) return;
        await RunAsync(async () =>
        {
            IReadOnlyList<string> files = [];
            try
            {
                files = await _services.ImportSource.PickFilesAsync();
                if (files.Count == 0) return;
                using var operation = MobileDataCoordinator.TryBeginOperation();
                if (operation is null) return;
                var failures = new List<string>();
                var imported = 0;
                foreach (var file in files)
                {
                    try
                    {
                        ManagementStatus.Text = $"正在导入 {Path.GetFileName(file)}…";
                        await Task.Run(() => _dictionary!.ImportAsync(file));
                        imported++;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        var message = exception is InvalidOperationException && exception.Message.Contains("Calibre", StringComparison.OrdinalIgnoreCase)
                            ? "此 Kindle 词典需要 Calibre 转换，手机版无法导入。"
                            : UiText.Localize(exception.Message);
                        failures.Add($"{Path.GetFileName(file)}：{message}");
                    }
                }
                _pendingRemoval = null;
                await RefreshDictionariesAsync();
                await LookupAsync();
                ManagementStatus.Text = $"已导入 {imported} 个文件。" + (failures.Count == 0 ? "" : "\n" + string.Join("\n", failures));
            }
            finally
            {
                _services.ImportSource.ReleaseFiles(files);
            }
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _dictionary is null) return;
        _busy = true;
        SearchButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        Dictionaries.IsEnabled = false;
        try
        {
            await DictionaryGate.WaitAsync();
            try { await action(); }
            finally { DictionaryGate.Release(); }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ManagementStatus.Text = "词典操作失败：" + UiText.Localize(exception.Message);
        }
        finally
        {
            SearchButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            Dictionaries.IsEnabled = true;
            _busy = false;
        }
    }
}
