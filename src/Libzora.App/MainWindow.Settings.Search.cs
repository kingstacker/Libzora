using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using System.Xml.Linq;

namespace Libzora;

/// <summary>
/// Settings section navigation and search.
/// </summary>
public partial class MainWindow
{
    private void SystemBackupNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Data", SettingsBackupExpander);
    }

    private void SystemS3SyncNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Data", SettingsS3Expander);
    }

    private void KindleEmailSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Kindle", SettingsSendToKindleExpander);
        FocusSettingsControl(KindleEmailRecipientBox);
    }

    private async void ReaderAiSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (ReaderRoot.IsVisible) await CloseReaderAsync();
        if (ReaderRoot.IsVisible) return;
        OpenSettingsExpander("Reading", SettingsAiExpander);
        await EnsureMainReaderAiSettingsLoadedAsync();
        if (SettingsReadingSection.IsVisible && SettingsAiExpander.IsExpanded)
            FocusSettingsControl(MainReaderAiBaseUrlBox);
    }

    private async void ReaderAiVectorStatusButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (ReaderRoot.IsVisible) await CloseReaderAsync();
        if (ReaderRoot.IsVisible) return;
        OpenSettingsExpander("Reading", SettingsAiExpander);
        await EnsureMainReaderAiSettingsLoadedAsync();
        Dispatcher.UIThread.Post(() =>
        {
            if (!MainReaderEmbeddingModelSelectorBox.IsEffectivelyVisible) return;
            MainReaderEmbeddingModelSelectorBox.BringIntoView();
            MainReaderEmbeddingModelSelectorBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void SettingsCategoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ShowSettingsSection(tag);
    }

    private void ShowSettingsSection(string tag)
    {
        var sections = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = SettingsGeneralSection,
            ["Library"] = SettingsLibrarySection,
            ["Reading"] = SettingsReadingSection,
            ["Kindle"] = SettingsKindleSection,
            ["Data"] = SettingsDataSection,
            ["Plugins"] = SettingsPluginsSection,
            ["About"] = SettingsAboutSection,
            ["Advanced"] = SettingsAdvancedSection
        };
        if (!sections.TryGetValue(tag, out var activeSection))
        {
            tag = "General";
            activeSection = SettingsGeneralSection;
        }

        var focused = FocusManager?.GetFocusedElement() as Control;
        var focusWasInContent = focused is not null && focused.GetVisualAncestors()
            .Any(ancestor => ReferenceEquals(ancestor, SettingsScrollViewer) || ReferenceEquals(ancestor, SettingsS3ActionBar));
        foreach (var section in sections.Values)
            section.IsVisible = ReferenceEquals(section, activeSection);
        if (!string.Equals(_activeSettingsCategory, tag, StringComparison.OrdinalIgnoreCase))
            SettingsScrollViewer.Offset = default;
        _activeSettingsCategory = tag;
        UpdateS3SettingsActions();
        Button[] buttons = [SettingsGeneralButton, SettingsLibraryButton, SettingsReadingButton, SettingsKindleButton, SettingsDataButton, SettingsPluginsButton, SettingsAdvancedButton, SettingsAboutButton];
        foreach (var button in buttons)
        {
            var active = string.Equals(button.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
            if (active && focusWasInContent && focused?.IsEffectivelyVisible == false) button.Focus();
        }

    }

    private async void SettingsSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _settingsSearchCancellation?.Cancel();
        _settingsSearchCancellation = null;
        var query = SettingsSearchBox.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;
        SettingsCategoryButtons.IsVisible = !searching;
        SettingsScrollViewer.IsVisible = !searching;
        SettingsSearchNoResultsText.IsVisible = false;
        SettingsSearchResultsPanel.IsVisible = false;
        SettingsSearchResults.ItemsSource = null;

        if (!searching) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _settingsSearchCancellation = cancellation;
        try
        {
        await Task.Delay(180, cancellation.Token);
        var key = UiText.Get("常规") + "|" + query;
        if (!_settingsSearchCache.TryGetValue(key, out var results))
        {
            results = BuildSettingsSearchResults(query);
            if (_settingsSearchCache.Count >= 64) _settingsSearchCache.Clear();
            _settingsSearchCache[key] = results;
        }
        _settingsSearchSelectedIndex = 0;
        SettingsSearchResults.ItemsSource = results;
        SettingsSearchResultsPanel.IsVisible = results.Count > 0;
        SettingsSearchNoResultsText.IsVisible = results.Count == 0;
        Dispatcher.UIThread.Post(UpdateSettingsSearchSelection, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_settingsSearchCancellation, cancellation)) _settingsSearchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SettingsSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
        {
            SettingsSearchBox.Text = string.Empty;
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && SettingsSearchResults.Items.Count > 0)
        {
            _settingsSearchSelectedIndex = Math.Clamp(
                _settingsSearchSelectedIndex + (e.Key == Key.Down ? 1 : -1),
                0, SettingsSearchResults.Items.Count - 1);
            UpdateSettingsSearchSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SettingsSearchResults.Items
                     .ElementAtOrDefault(_settingsSearchSelectedIndex) is SettingsSearchResult result)
        {
            NavigateToSettingsSearchResult(result);
            e.Handled = true;
        }
    }

    private void UpdateSettingsSearchSelection()
    {
        var buttons = SettingsSearchResults.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is SettingsSearchResult).ToArray();
        for (var index = 0; index < buttons.Length; index++)
        {
            var selected = index == _settingsSearchSelectedIndex;
            buttons[index].Classes.Set("selected", selected);
            if (selected) buttons[index].BringIntoView();
        }
    }

    private void SettingsSearchResultButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SettingsSearchResult result })
            NavigateToSettingsSearchResult(result);
    }

    private void NavigateToSettingsSearchResult(SettingsSearchResult result)
    {
        if (BlockNavigationWhileTransferring()) return;

        SettingsSearchBox.Text = string.Empty;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        ShowSettingsSection(result.Category);

        foreach (var expander in result.Target.GetSelfAndLogicalAncestors()
                     .OfType<Expander>()
                     .Reverse())
            expander.IsExpanded = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (!result.Target.IsEffectivelyVisible) return;
            result.Target.BringIntoView();

            var focusTarget = result.Target.GetSelfAndLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(control =>
                    control.IsEffectivelyVisible && control.IsEnabled && control.Focusable &&
                    control is Button or TextBox or ComboBox or ToggleSwitch or CheckBox or RadioButton or NumericUpDown);
            focusTarget?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private List<SettingsSearchResult> BuildSettingsSearchResults(string query)
    {
        var sections = new[]
        {
            ("General", SettingsGeneralButton.Content?.ToString() ?? T("常规"), SettingsGeneralSection),
            ("Library", SettingsLibraryButton.Content?.ToString() ?? T("书库"), SettingsLibrarySection),
            ("Reading", SettingsReadingButton.Content?.ToString() ?? T("阅读"), SettingsReadingSection),
            ("Kindle", SettingsKindleButton.Content?.ToString() ?? T("Kindle"), SettingsKindleSection),
            ("Data", SettingsDataButton.Content?.ToString() ?? T("数据"), SettingsDataSection),
            ("Plugins", SettingsPluginsButton.Content?.ToString() ?? T("插件"), SettingsPluginsSection),
            ("Advanced", SettingsAdvancedButton.Content?.ToString() ?? T("高级"), SettingsAdvancedSection),
            ("About", SettingsAboutButton.Content?.ToString() ?? T("关于"), SettingsAboutSection)
        };
        var results = new List<SettingsSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (category, categoryLabel, section) in sections)
        {
            var descendants = section.GetSelfAndLogicalDescendants().OfType<Control>()
                .Where(IsSettingsSearchControlAvailable).ToArray();
            foreach (var expander in descendants.OfType<Expander>())
            {
                var title = DisplayValue(expander.Header) ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;
                var detail = DisplayValue(expander.Tag) ?? string.Empty;
                var corpus = CollectSettingsText(expander.GetSelfAndLogicalDescendants().OfType<Control>());
                AddResult(title, detail, categoryLabel, category, expander, expander, corpus);
            }

            // Settings outside an expander still need a useful destination.
            // Index each direct section child as one destination so a matching
            // label or description can focus its associated input without
            // exposing hundreds of individual controls in the result list.
            foreach (var child in section.GetLogicalChildren().OfType<Control>())
            {
                if (child is Expander) continue;
                var controls = child.GetSelfAndLogicalDescendants().OfType<Control>()
                    .Where(IsSettingsSearchControlAvailable).ToArray();
                var corpus = CollectSettingsText(controls);
                var title = controls.Select(GetSettingsControlTitle).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(title)) continue;
                var detail = controls.Select(GetSettingsControlDescription).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                AddResult(title, detail, categoryLabel, category, child, null, corpus);
            }

            // The category title and explanatory copy are searchable too,
            // including sections currently hidden by the category switcher.
            var sectionCorpus = CollectSettingsText(descendants);
            var sectionTitle = descendants.OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("settingsSectionTitle"))?.Text;
            if (!string.IsNullOrWhiteSpace(sectionTitle))
                AddResult(DisplayValue(sectionTitle)!, string.Empty, categoryLabel, category, section, null, sectionCorpus);
        }

        var terms = ExpandSettingsSearchTerms(query);
        return results
            .Select(result => (Result: result, Score: GetSearchScore(result, terms)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Result)
            .Take(18)
            .ToList();

        void AddResult(string title, string detail, string categoryLabel, string category, Control target, Expander? expander, string corpus)
        {
            var expandedCorpus = ExpandCorpus(corpus + " " + title + " " + detail + " " + categoryLabel);
            if (GetSearchScore(new SettingsSearchResult(title, detail, categoryLabel, "", category, target, expander, expandedCorpus), ExpandSettingsSearchTerms(query)) <= 0)
                return;

            var key = $"{category}|{target.GetType().FullName}|{title}|{detail}";
            if (!seen.Add(key)) return;
            var accessibleName = string.Join(", ", new[] { title, detail, categoryLabel }.Where(value => !string.IsNullOrWhiteSpace(value)));
            results.Add(new SettingsSearchResult(title, detail, categoryLabel, accessibleName, category, target, expander, expandedCorpus));
        }
    }

    private static bool IsSettingsSearchControlAvailable(Control control) =>
        !control.GetSelfAndLogicalAncestors().OfType<Control>()
            .Any(ancestor => ancestor.Classes.Contains("syncProviderHidden"));

    private static string CollectSettingsText(IEnumerable<Control> controls)
    {
        var values = new List<string>();
        foreach (var control in controls.Where(IsSettingsSearchControlAvailable))
        {
            switch (control)
            {
                case TextBlock { Text: { Length: > 0 } text }:
                    Add(text);
                    break;
                case TextBox { PlaceholderText: { Length: > 0 } placeholder }:
                    Add(placeholder);
                    break;
            }

            if (control is Expander expander)
            {
                Add(DisplayValue(expander.Header));
                Add(DisplayValue(expander.Tag));
            }
            if (control is ContentControl contentControl)
                Add(DisplayValue(contentControl.Content));
            Add(DisplayValue(control.Tag));
            Add(DisplayValue(ToolTip.GetTip(control)));
            Add(AutomationProperties.GetName(control));
        }

        return string.Join(" ", values);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            values.Add(value);
            var normalized = NormalizeSettingsSearchText(value);
            if (SettingsLocalizedSearchTerms.Value.TryGetValue(normalized, out var translations))
                values.AddRange(translations);
        }
    }

    private static string? GetSettingsControlTitle(Control control)
    {
        if (control is Expander expander && DisplayValue(expander.Header) is { } header) return header;
        if (AutomationProperties.GetName(control) is { Length: > 0 } accessibleName) return accessibleName;
        if (control is ContentControl { Content: { } content } && DisplayValue(content) is { } displayContent) return displayContent;
        if (control is TextBlock { Text: { Length: > 0 } text }) return DisplayValue(text);
        return null;
    }

    private static string? GetSettingsControlDescription(Control control)
    {
        if (control is Expander expander && DisplayValue(expander.Tag) is { Length: > 0 } description) return description;
        if (DisplayValue(ToolTip.GetTip(control)) is { Length: > 0 } tip) return tip;
        return null;
    }

    private static string? DisplayValue(object? value)
    {
        var text = value switch
        {
            string valueText => valueText,
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.PlaceholderText,
            ContentControl { Content: string content } => content,
            Control control => AutomationProperties.GetName(control),
            _ => null
        };
        text = text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : UiText.Localize(text);
    }

    private static string ExpandCorpus(string corpus)
    {
        var normalized = NormalizeSettingsSearchText(corpus);
        var variants = new List<string> { corpus };
        foreach (var group in SettingsSearchSynonyms)
            if (group.Any(term => MatchesSettingsTerm(normalized, NormalizeSettingsSearchText(term))))
                variants.Add(string.Join(" ", group));
        return string.Join(" ", variants);
    }

    private static string[] ExpandSettingsSearchTerms(string query)
    {
        var normalized = NormalizeSettingsSearchText(query);
        var variants = new HashSet<string>(StringComparer.Ordinal) { normalized };
        if (SettingsLocalizedSearchTerms.Value.TryGetValue(normalized, out var translations))
            foreach (var translation in translations)
                variants.Add(NormalizeSettingsSearchText(translation));
        foreach (var group in SettingsSearchSynonyms)
            if (group.Any(term => MatchesSettingsTerm(normalized, NormalizeSettingsSearchText(term))))
                foreach (var term in group)
                    variants.Add(NormalizeSettingsSearchText(term));
        return variants.Where(term => term.Length > 0).ToArray();
    }

    private static int GetSearchScore(SettingsSearchResult result, IReadOnlyCollection<string> terms)
    {
        var title = NormalizeSettingsSearchText(result.Title);
        var corpus = NormalizeSettingsSearchText(result.SearchCorpus);
        var score = 0;
        foreach (var term in terms)
        {
            if (MatchesSettingsTerm(title, term)) score = Math.Max(score, term.Length >= 3 ? 100 : 80);
            else if (MatchesSettingsTerm(corpus, term)) score = Math.Max(score, 50);
        }
        return score;
    }

    private static bool MatchesSettingsTerm(string source, string term)
    {
        if (source.Length == 0 || term.Length == 0) return false;
        var isLatinWord = term.Any(char.IsLetter) && term.All(character => character <= 0x7F && (char.IsLetterOrDigit(character) || character == ' '));
        if (!isLatinWord) return source.Contains(term, StringComparison.Ordinal);

        var pattern = $"(?<![A-Za-z0-9]){Regex.Escape(term).Replace("\\ ", "\\s+")}(?![A-Za-z0-9])";
        return Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant);
    }

    private static IReadOnlyDictionary<string, string[]> BuildSettingsLocalizedSearchTerms()
    {
        const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        const string appAssembly = "Libzora.App";
        try
        {
            var dictionaries = new[] { "zh-CN", "en-US" }
                .Select(language => LoadUiStringDictionary(appAssembly, language, xamlNamespace))
                .ToArray();
            if (dictionaries[0].Count == 0 || dictionaries[1].Count == 0) return new Dictionary<string, string[]>();

            var variants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (key, chinese) in dictionaries[0])
            {
                if (!dictionaries[1].TryGetValue(key, out var english)) continue;
                Add(chinese, english);
                Add(english, chinese);
            }

            return variants.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal);

            void Add(string source, string translation)
            {
                var normalized = NormalizeSettingsSearchText(source);
                if (normalized.Length < 2 || normalized.Length > 220) return;
                if (!variants.TryGetValue(normalized, out var values))
                    variants[normalized] = values = new HashSet<string>(StringComparer.Ordinal);
                values.Add(translation);
            }
        }
        catch
        {
            // The explicit settings vocabulary below remains available if an
            // app host does not expose compiled Avalonia assets (for example,
            // a very early startup or a stripped-down test host).
            return new Dictionary<string, string[]>();
        }

        static Dictionary<string, string> LoadUiStringDictionary(string assembly, string language, string xamlNamespace)
        {
            var uri = new Uri($"avares://{assembly}/Resources/Strings.{language}.axaml");
            using var stream = AssetLoader.Open(uri);
            var document = XDocument.Load(stream, LoadOptions.None);
            var keyName = XName.Get("Key", xamlNamespace);
            return document.Root?.Elements()
                       .Where(element => element.Name.LocalName == "String")
                       .Select(element => (Key: element.Attribute(keyName)?.Value, Value: element.Value.Trim()))
                       .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                       .GroupBy(item => item.Key!, StringComparer.Ordinal)
                       .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeSettingsSearchText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    private static void FocusSettingsControl(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) is not null) control.Focus();
    }, DispatcherPriority.Loaded);
}
