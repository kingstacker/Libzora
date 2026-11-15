using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System.Text.Json.Nodes;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Infrastructure.Plugins;

namespace Libzora;

/// <summary>
/// Plugin capabilities that run without a plugin window: dictionary and
/// translator providers in the reader popups, background tasks, and the shared
/// content/AI/reader hosts lent to plugin pages.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan PluginProviderTimeout = TimeSpan.FromSeconds(20);
    private const long MaximumHeadlessLogBytes = 1024 * 1024;
    private PluginBookContentService? _pluginContent;
    private PluginAiHost? _pluginAi;
    private PluginScriptRuntime? _pluginScripts;
    private PluginBackgroundScheduler? _pluginBackground;
    private CancellationTokenSource? _readerDictionaryPluginCancellation;
    private string? _readerTranslationPluginTitle;
    private bool _pluginBackgroundStarted;

    private PluginBookContentService PluginContent =>
        _pluginContent ??= new PluginBookContentService(_paths, _library, EnsureBookFileAvailableAsync);

    private PluginAiHost PluginAi =>
        _pluginAi ??= new PluginAiHost(_aiSettingsStore, _aiChatClient, () => _appSettings);

    private PluginScriptRuntime PluginScripts => _pluginScripts ??= new PluginScriptRuntime(new PluginScriptServices(
        _library,
        _paths,
        _secretProtector,
        new HeadlessPluginUiHost(
            () => _appSettings.NetworkEnabled,
            message => Dispatcher.UIThread.Post(() => SetTaskStatus(message)),
            bookIds => Dispatcher.UIThread.Post(() => PluginWindow_BooksUpdated(null, bookIds)),
            WriteHeadlessPluginLog),
        _readerData,
        PluginContent,
        PluginAi));

    /// <summary>Reader navigation for one plugin window; closes it before opening the book.</summary>
    private IPluginReaderHost CreatePluginReaderHost(Func<PluginWindow?> window) =>
        new DelegatePluginReaderHost((bookId, fileId, chapterPath, offset, _) =>
        {
            // Reply to the page at once; the window closes and the book opens
            // on the UI thread afterwards, outside the page's lifetime.
            var owner = window();
            Dispatcher.UIThread.Post(async () =>
                await OpenBookFromPluginAsync(owner, bookId, fileId, chapterPath, offset));
            return Task.CompletedTask;
        });

    private async Task OpenBookFromPluginAsync(
        PluginWindow? owner,
        Guid bookId,
        Guid? fileId,
        string? chapterPath,
        int? offset)
    {
        owner?.Close();
        BookCardViewModel? card = ViewModel.Books.FirstOrDefault(candidate => candidate.Book.Id == bookId);
        var temporary = false;
        try
        {
            if (card is null)
            {
                var book = await _library.GetBookAsync(bookId, _lifetimeCancellation.Token);
                if (book is null)
                {
                    SetTaskStatus(T("插件要打开的书籍已不在书库中。"));
                    return;
                }
                card = new BookCardViewModel(book, _paths.Data);
                temporary = true;
            }
            var file = fileId is { } id ? card.Book.Files.FirstOrDefault(candidate => candidate.Id == id) : null;
            await OpenBookAsync(card, file);
            if (!ReaderRoot.IsVisible || string.IsNullOrWhiteSpace(chapterPath) || _readerBookFile is null) return;
            // A zero-length annotation is the reader's existing "go to this
            // chapter and offset" path.
            await NavigateToReaderAnnotationAsync(new ReaderAnnotation
            {
                BookId = bookId,
                BookFileId = _readerBookFile.Id,
                ChapterPath = chapterPath,
                StartOffset = Math.Max(0, offset ?? 0),
                EndOffset = Math.Max(0, offset ?? 0)
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
        }
        finally
        {
            if (temporary) card?.Dispose();
        }
    }

    private void WriteHeadlessPluginLog(string level, string message)
    {
        // Headless console output is only kept while developing plugins.
        if (!_appSettings.PluginDeveloperMode) return;
        try
        {
            var path = Path.Combine(_paths.PluginData, "headless-console.log");
            Directory.CreateDirectory(_paths.PluginData);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumHeadlessLogBytes)
                File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string PluginTitle(InstalledPlugin plugin, string key, string fallback) =>
        plugin.Manifest.GetTitle(UiText.CurrentLanguage, key, fallback);

    // ---- dictionary providers -----------------------------------------

    /// <summary>Starts every enabled dictionary provider for <paramref name="term"/>.</summary>
    private void StartReaderPluginDictionaryLookup(string term)
    {
        CancelReaderPluginDictionaryLookup();
        ReaderDictionaryPluginPanel.Children.Clear();
        var providers = _pluginSnapshot
            .SelectMany(plugin => plugin.Manifest.Dictionaries.Select(provider => (Plugin: plugin, Provider: provider)))
            .ToArray();
        ReaderDictionaryPluginTab.IsVisible = providers.Length > 0;
        if (providers.Length == 0) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerDictionaryPluginCancellation = cancellation;
        foreach (var (plugin, provider) in providers)
        {
            var section = new StackPanel { Spacing = 6 };
            section.Children.Add(new TextBlock
            {
                Text = PluginTitle(plugin, provider.Id, provider.Title),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = AppAppearanceResources.GetBrush("ReaderMutedBrush")
            });
            var status = new TextBlock
            {
                Text = T("正在查询…"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppAppearanceResources.GetBrush("ReaderMutedBrush")
            };
            section.Children.Add(status);
            ReaderDictionaryPluginPanel.Children.Add(section);
            _ = RunReaderPluginDictionaryAsync(plugin, provider, term, section, status, cancellation);
        }
    }

    private void CancelReaderPluginDictionaryLookup()
    {
        _readerDictionaryPluginCancellation?.Cancel();
        _readerDictionaryPluginCancellation?.Dispose();
        _readerDictionaryPluginCancellation = null;
    }

    private async Task RunReaderPluginDictionaryAsync(
        InstalledPlugin plugin,
        PluginDictionaryProvider provider,
        string term,
        StackPanel section,
        TextBlock status,
        CancellationTokenSource cancellation)
    {
        PluginScriptResult result;
        try
        {
            result = await PluginScripts.InvokeAsync(
                plugin,
                provider.Script,
                "dictionary",
                provider.Id,
                new JsonArray(term, new JsonObject { ["language"] = UiText.CurrentLanguage }),
                PluginProviderTimeout,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (!ReferenceEquals(_readerDictionaryPluginCancellation, cancellation)) return;

        if (!result.Succeeded)
        {
            status.Text = T("查询失败：{0}", UiText.Localize(result.Error ?? string.Empty));
            return;
        }
        var entries = PluginProviderResults.ParseDictionary(result.Value, term);
        if (entries.Count == 0)
        {
            status.Text = T("没有找到释义。");
            return;
        }
        section.Children.Remove(status);
        foreach (var entry in entries)
            section.Children.Add(BuildPluginDictionaryEntry(entry));
    }

    private static Control BuildPluginDictionaryEntry(PluginDictionaryEntry entry)
    {
        var ink = AppAppearanceResources.GetBrush("ReaderInkBrush");
        var muted = AppAppearanceResources.GetBrush("ReaderMutedBrush");
        var panel = new StackPanel { Spacing = 4 };
        var heading = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = ink,
            TextWrapping = TextWrapping.Wrap,
            Text = string.IsNullOrWhiteSpace(entry.Phonetic) ? entry.Headword : $"{entry.Headword}  {entry.Phonetic}"
        };
        panel.Children.Add(heading);
        for (var index = 0; index < entry.Senses.Count; index++)
        {
            var sense = entry.Senses[index];
            var prefix = entry.Senses.Count > 1 ? $"{index + 1}. " : string.Empty;
            var pos = string.IsNullOrWhiteSpace(sense.PartOfSpeech) ? string.Empty : $"{sense.PartOfSpeech} ";
            panel.Children.Add(new SelectableTextBlock
            {
                Text = prefix + pos + sense.Text,
                FontSize = 14,
                LineHeight = 21,
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var example in sense.Examples)
            {
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = "· " + example,
                    FontSize = 12,
                    Margin = new Thickness(12, 0, 0, 0),
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }
        if (!string.IsNullOrWhiteSpace(entry.Note))
        {
            panel.Children.Add(new SelectableTextBlock
            {
                Text = entry.Note,
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return panel;
    }

    // ---- translator providers -----------------------------------------

    /// <summary>Rebuilds the translation popup's provider strip.</summary>
    private void RefreshReaderTranslationPluginProviders()
    {
        if (ReaderTranslationPluginProviders is null) return;
        ReaderTranslationPluginProviders.Children.Clear();
        var providers = _pluginSnapshot
            .SelectMany(plugin => plugin.Manifest.Translators.Select(provider => (Plugin: plugin, Provider: provider)))
            .ToArray();
        ReaderTranslationPluginProviders.IsVisible = providers.Length > 0;
        if (providers.Length == 0) return;

        ReaderTranslationPluginProviders.Children.Add(CreateReaderTranslationProviderButton(
            T("内置翻译"),
            () => TranslateReaderSelectionWithBuiltInAsync()));
        foreach (var (plugin, provider) in providers)
        {
            ReaderTranslationPluginProviders.Children.Add(CreateReaderTranslationProviderButton(
                PluginTitle(plugin, provider.Id, provider.Title),
                () => TranslateReaderSelectionWithPluginAsync(plugin, provider)));
        }
    }

    private static Button CreateReaderTranslationProviderButton(string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = title,
                FontSize = 12,
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(8, 3)
        };
        button.Classes.Add("readerSelectionAction");
        ToolTip.SetTip(button, title);
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task TranslateReaderSelectionWithBuiltInAsync()
    {
        if (string.IsNullOrWhiteSpace(_readerPendingSelection)) return;
        var settings = BookTranslationSettings.Normalize(_appSettings.Translation);
        _readerTranslationProvider = ReaderTranslationProvider.Ai;
        await TranslateReaderSelectionAsync(
            _readerTranslationProvider,
            settings.AiRequestsPerMinute,
            settings.GoogleProxyAddress);
    }

    private async Task TranslateReaderSelectionWithPluginAsync(InstalledPlugin plugin, PluginTranslatorProvider provider)
    {
        var source = _readerTranslationSource;
        if (string.IsNullOrWhiteSpace(source)) return;

        CancelReaderTranslationRequest();
        _readerTranslationPluginTitle = PluginTitle(plugin, provider.Id, provider.Title);
        ReaderTranslationProviderText.Text = _readerTranslationPluginTitle;
        ReaderTranslationResultText.Text = string.Empty;
        ReaderTranslationCopyButton.IsEnabled = false;
        ReaderTranslationAnnotateButton.IsEnabled = false;
        _readerTranslationBusy = true;
        ReaderTranslationStatusText.Text = T("正在翻译…");
        var requestSequence = ++_readerTranslationRequestSequence;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ReaderToken);
        _readerTranslationCancellation = cancellation;
        try
        {
            var result = await PluginScripts.InvokeAsync(
                plugin,
                provider.Script,
                "translator",
                provider.Id,
                new JsonArray(source, new JsonObject
                {
                    ["target"] = _readerTranslationTargetLanguage,
                    ["language"] = UiText.CurrentLanguage
                }),
                PluginProviderTimeout,
                cancellation.Token);
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Error ?? "插件翻译失败。");
            var text = PluginProviderResults.ParseTranslation(result.Value)
                ?? throw new InvalidDataException("翻译返回了空结果，请重试或切换翻译引擎。");
            ReaderTranslationResultText.Text = text;
            ReaderTranslationStatusText.Text = T("翻译已完成");
            ReaderTranslationCopyButton.IsEnabled = true;
            ReaderTranslationAnnotateButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (requestSequence != _readerTranslationRequestSequence
                || !ReferenceEquals(_readerTranslationCancellation, cancellation))
                return;
            ReaderTranslationResultText.Text = T("翻译失败：{0}", UiText.Localize(exception.Message));
            ReaderTranslationStatusText.Text = T("请重试或切换翻译引擎。");
        }
        finally
        {
            if (ReferenceEquals(_readerTranslationCancellation, cancellation))
            {
                _readerTranslationCancellation = null;
                _readerTranslationBusy = false;
            }
            cancellation.Dispose();
        }
    }

    // ---- background tasks ---------------------------------------------

    private void StartPluginBackgroundTasks()
    {
        if (_pluginBackgroundStarted) return;
        _pluginBackgroundStarted = true;
        _pluginBackground = new PluginBackgroundScheduler(
            Path.Combine(_paths.PluginData, "background-state.json"),
            () => _pluginSnapshot,
            (plugin, task, argument, cancellationToken) => PluginScripts.InvokeAsync(
                plugin,
                task.Script,
                "background",
                string.Empty,
                new JsonArray(argument),
                PluginBackgroundScheduler.RunTimeout,
                cancellationToken));
        _pluginBackground.Completed += (plugin, result) =>
        {
            if (result.Succeeded) return;
            WriteHeadlessPluginLog("error", $"{plugin.Id} background: {result.Error}");
            Dispatcher.UIThread.Post(() => SetTaskStatus(T(
                "插件“{0}”的后台任务失败：{1}",
                plugin.Manifest.GetName(UiText.CurrentLanguage),
                UiText.Localize(result.Error ?? string.Empty))));
        };
        _ = RunPluginBackgroundLoopAsync(_lifetimeCancellation.Token);
    }

    private async Task RunPluginBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let startup work (library load, sync) settle first.
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_appSettings.PluginBackgroundTasksEnabled && _pluginBackground is { } scheduler)
                    await scheduler.RunDueAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

}
