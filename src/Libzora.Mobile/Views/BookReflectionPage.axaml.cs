using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Mobile.Views.Sheets;

namespace Libzora.Mobile.Views;

/// <summary>
/// Mobile editor for book-level reading reflections with Markdown formatting,
/// annotation citations, image insertion and live preview.
/// </summary>
public partial class BookReflectionPage : UserControl, IShellPage
{
    private static readonly Regex ImageLineRegex = new(
        @"^\s*!\[(?<alt>[^\]]*)\]\((?<source>[^)\s\r\n]+)(?:\s*=\s*\d+(?:x\d+)?)?\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex HeadingLineRegex = new(
        @"^\s*(?<marks>#{1,6})\s+(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex UnorderedLineRegex = new(
        @"^\s*[-*+]\s+(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex OrderedLineRegex = new(
        @"^\s*(?<number>\d+)[.)]\s+(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex InlineRegex = new(
        @"(\*\*(?<bold>[^*\r\n]+)\*\*|\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\))",
        RegexOptions.Compiled);

    private readonly Book _book;
    private readonly ApplicationModules _modules;
    private readonly MobileShell _shell;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Bitmap> _previewBitmaps = [];
    private bool _isPreview;
    private bool _busy;
    private bool _loaded;

    /// <summary>Design-time only.</summary>
    public BookReflectionPage()
    {
        _book = null!;
        _modules = null!;
        _shell = null!;
        InitializeComponent();
    }

    public BookReflectionPage(Book book, ApplicationModules modules, MobileShell shell)
    {
        _book = book;
        _modules = modules;
        _shell = shell;
        InitializeComponent();
        KeyboardAvoidance.Attach(this, EditorScroller, ActionBar);
        BookTitleText.Text = $"《{book.Title}》";
        UpdateCharacterCount();
        SetBusy(true);
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        };
    }

    public void OnClosed()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        ClearPreviewBitmaps();
    }

    private async Task LoadAsync()
    {
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _shell.EnsureReaderDataAsync();
            var existing = await _modules.Reader.Reflections.GetAsync(_book.Id, _lifetime.Token);
            EditorBox.Text = existing is null
                ? string.Empty
                : BookReflectionMarkdown.NormalizeMarkdown(existing.Content);
            UpdateCharacterCount();
            if (_isPreview) RebuildPreview();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ShowStatus(UiText.Get("读后思考暂时不可用：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => _shell.GoBack();

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e) => UpdateCharacterCount();

    private void UpdateCharacterCount()
    {
        var count = BookReflectionMarkdown.CountCharacters(EditorBox.Text);
        CharacterCountText.Text = UiText.Get("{0} 字", count);
    }

    private void OnPreviewToggleClick(object? sender, RoutedEventArgs e)
    {
        _isPreview = !_isPreview;
        PreviewToggleButton.Content = _isPreview ? UiText.Get("编辑") : UiText.Get("预览");
        FormattingBar.IsVisible = !_isPreview;
        EditorScroller.IsVisible = !_isPreview;
        PreviewScroller.IsVisible = _isPreview;
        if (_isPreview) RebuildPreview();
    }

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        switch (tag)
        {
            case "h1": ToggleLinePrefix("# ", isHeading: true); break;
            case "h2": ToggleLinePrefix("## ", isHeading: true); break;
            case "h3": ToggleLinePrefix("### ", isHeading: true); break;
            case "bold": ToggleBold(); break;
            case "quote": ToggleLinePrefix("> ", isHeading: false); break;
            case "unordered": ToggleLinePrefix("- ", isHeading: false); break;
            case "ordered": ToggleLinePrefix("1. ", isHeading: false); break;
        }
    }

    private async void OnCiteClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            await _shell.EnsureReaderDataAsync();
            var citations = await _modules.Reader.Reflections.LoadCitationsAsync(_book.Title, _lifetime.Token);
            if (citations.Count == 0)
            {
                _shell.ShowToast(UiText.Get("暂无可引用的批注。"));
                return;
            }
            _shell.ShowSheet(UiText.Get("引用批注"), new BookReflectionCitationSheet(
                citations,
                _book.Title,
                citation =>
                {
                    _shell.HideSheet();
                    var block = BookReflectionMarkdown.FormatCitationQuoteBlock(
                        citation.DisplayText,
                        citation.BookTitle);
                    if (!string.IsNullOrEmpty(block))
                        InsertBlockText(block);
                }));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _shell.ShowToast(UiText.Localize(exception.Message));
        }
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        var (start, end, selected) = GetSelection();
        _shell.ShowSheet(UiText.Get("链接"), new PromptSheet(null, UiText.Get("确定"), url =>
        {
            var normalized = url.Trim();
            if (!normalized.Contains(':', StringComparison.Ordinal))
                normalized = "https://" + normalized;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
            {
                _shell.ShowToast(UiText.Get("请输入有效链接。"));
                return Task.CompletedTask;
            }
            var label = string.IsNullOrWhiteSpace(selected) ? uri.ToString() : selected;
            ReplaceRange(start, end, $"[{label}]({uri})");
            _shell.HideSheet();
            return Task.CompletedTask;
        }, initialText: "https://", placeholder: "https://"));
    }

    private async void OnImageClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        IReadOnlyList<string> files = [];
        try
        {
            files = await _shell.Services.ImportSource.PickFilesAsync(_lifetime.Token);
            if (files.Count == 0) return;
            var bytes = await File.ReadAllBytesAsync(files[0], _lifetime.Token);
            using (var verify = new MemoryStream(bytes, writable: false))
            {
                using var _ = new Bitmap(verify);
            }
            var mime = MimeTypeFor(files[0]);
            var source = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            InsertBlockText($"![{UiText.Get("图片")}]({source})");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _shell.ShowToast(UiText.Get("无法插入图片：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            _shell.Services.ImportSource.ReleaseFiles(files);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _shell.EnsureReaderDataAsync();
            var saved = await _modules.Reader.Reflections.SaveAsync(
                _book.Id,
                EditorBox.Text ?? string.Empty,
                _lifetime.Token);
            _shell.ShowToast(string.IsNullOrWhiteSpace(saved.Content)
                ? UiText.Get("读后思考已清空。")
                : UiText.Get("读后思考已保存。"));
            _shell.GoBack();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus(UiText.Get("保存读后思考失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var confirmed = await _shell.ConfirmAsync(
            UiText.Get("删除读后思考"),
            UiText.Get("确定删除《{0}》的读后思考吗？", _book.Title),
            UiText.Get("删除"));
        if (!confirmed) return;

        using var operation = MobileDataCoordinator.TryBeginOperation();
        if (operation is null) return;
        SetBusy(true);
        try
        {
            await _shell.EnsureReaderDataAsync();
            await _modules.Reader.Reflections.DeleteAsync(_book.Id, _lifetime.Token);
            _shell.ShowToast(UiText.Get("读后思考已清空。"));
            _shell.GoBack();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus(UiText.Get("删除失败：{0}", UiText.Localize(exception.Message)));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private (int Start, int End, string Selected) GetSelection()
    {
        var text = EditorBox.Text ?? string.Empty;
        var a = Math.Clamp(EditorBox.SelectionStart, 0, text.Length);
        var b = Math.Clamp(EditorBox.SelectionEnd, 0, text.Length);
        var start = Math.Min(a, b);
        var end = Math.Max(a, b);
        return (start, end, text[start..end]);
    }

    private void ReplaceRange(int start, int end, string replacement, int? caretAfter = null)
    {
        var text = EditorBox.Text ?? string.Empty;
        var next = string.Concat(text[..start], replacement, text[end..]);
        EditorBox.Text = next;
        var caret = Math.Clamp(caretAfter ?? (start + replacement.Length), 0, next.Length);
        EditorBox.Focus();
        EditorBox.SelectionStart = caret;
        EditorBox.SelectionEnd = caret;
        EditorBox.CaretIndex = caret;
    }

    private void ToggleBold()
    {
        var (start, end, selected) = GetSelection();
        if (start == end)
        {
            ReplaceRange(start, end, "****", start + 2);
            return;
        }
        if (selected.Length >= 4 && selected.StartsWith("**", StringComparison.Ordinal) && selected.EndsWith("**", StringComparison.Ordinal))
            ReplaceRange(start, end, selected[2..^2]);
        else
            ReplaceRange(start, end, $"**{selected}**");
    }

    private void ToggleLinePrefix(string prefix, bool isHeading)
    {
        var text = EditorBox.Text ?? string.Empty;
        var (start, _, _) = GetSelection();
        var lineStart = start > 0 ? text.LastIndexOf('\n', start - 1) + 1 : 0;
        var lineEnd = text.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[lineStart..lineEnd];

        string updated;
        if (isHeading)
        {
            var trimmed = Regex.Replace(line, @"^#{1,6}\s+", string.Empty);
            updated = line.StartsWith(prefix, StringComparison.Ordinal) ? trimmed : prefix + trimmed;
        }
        else if (line.StartsWith(prefix, StringComparison.Ordinal))
        {
            updated = line[prefix.Length..];
        }
        else
        {
            updated = prefix + line;
        }
        ReplaceRange(lineStart, lineEnd, updated, lineStart + updated.Length);
    }

    private void InsertBlockText(string block)
    {
        var (start, end, _) = GetSelection();
        var text = EditorBox.Text ?? string.Empty;
        var needsLeadingNewline = start > 0 && text[start - 1] != '\n';
        var needsTrailingNewline = end < text.Length && text[end] != '\n';
        var insertion = (needsLeadingNewline ? "\n\n" : string.Empty)
            + block
            + (needsTrailingNewline ? "\n\n" : "\n");
        ReplaceRange(start, end, insertion);
    }

    private void RebuildPreview()
    {
        ClearPreviewBitmaps();
        PreviewStack.Children.Clear();
        var normalized = BookReflectionMarkdown.NormalizeMarkdown(EditorBox.Text)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            PreviewStack.Children.Add(new TextBlock
            {
                Text = UiText.Get("还没有写下读后思考。"),
                Classes = { "muted" }
            });
            return;
        }

        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    quoteLines.Add(Regex.Replace(lines[i], @"^\s*>\s?", string.Empty));
                    i++;
                }
                PreviewStack.Children.Add(BuildQuoteView(quoteLines));
                continue;
            }

            if (ImageLineRegex.Match(line) is { Success: true } imageMatch)
            {
                PreviewStack.Children.Add(BuildImageView(
                    imageMatch.Groups["source"].Value,
                    imageMatch.Groups["alt"].Value));
                i++;
                continue;
            }

            if (HeadingLineRegex.Match(line) is { Success: true } heading)
            {
                var level = heading.Groups["marks"].Value.Length;
                var size = level switch { 1 => 21, 2 => 18, 3 => 16.5, _ => 15.5 };
                PreviewStack.Children.Add(BuildInlineTextBlock(
                    heading.Groups["text"].Value,
                    size,
                    FontWeight.SemiBold));
                i++;
                continue;
            }

            if (UnorderedLineRegex.Match(line) is { Success: true } unordered)
            {
                PreviewStack.Children.Add(BuildListItemView("•", unordered.Groups["text"].Value));
                i++;
                continue;
            }

            if (OrderedLineRegex.Match(line) is { Success: true } ordered)
            {
                PreviewStack.Children.Add(BuildListItemView(
                    $"{ordered.Groups["number"].Value}.",
                    ordered.Groups["text"].Value));
                i++;
                continue;
            }

            PreviewStack.Children.Add(BuildInlineTextBlock(line, 15, FontWeight.Normal));
            i++;
        }
    }

    private Control BuildQuoteView(List<string> quoteLines)
    {
        string? sourceTitle = null;
        if (quoteLines.Count >= 2
            && string.IsNullOrWhiteSpace(quoteLines[^2])
            && quoteLines[^1].Trim().StartsWith("--《", StringComparison.Ordinal))
        {
            sourceTitle = quoteLines[^1].Trim();
            quoteLines.RemoveRange(quoteLines.Count - 2, 2);
        }

        var stack = new StackPanel { Spacing = 6 };
        var body = string.Join("\n", quoteLines).Trim();
        if (body.Length > 0)
        {
            var tb = BuildInlineTextBlock(body, 14.5, FontWeight.Normal);
            tb.Classes.Add("secondary");
            stack.Children.Add(tb);
        }
        if (!string.IsNullOrEmpty(sourceTitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = sourceTitle,
                FontSize = 12,
                Classes = { "muted" }
            });
        }
        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = this.FindResource("SunkStrongBrush") as IBrush ?? Brushes.Gray,
            Padding = new Thickness(12, 4, 0, 4),
            Child = stack
        };
    }

    private Control BuildListItemView(string marker, string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8
        };
        grid.Children.Add(new TextBlock
        {
            Text = marker,
            FontSize = 15,
            Classes = { "muted" },
            MinWidth = 18
        });
        var body = BuildInlineTextBlock(text, 15, FontWeight.Normal);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private Control BuildImageView(string source, string alt)
    {
        var bitmap = DecodeImage(source);
        if (bitmap is not null)
        {
            _previewBitmaps.Add(bitmap);
            return new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxHeight = 320,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }
        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(alt) ? $"[{UiText.Get("图片")}]" : $"[{UiText.Get("图片")}: {alt}]",
            Classes = { "muted" }
        };
    }

    private static Bitmap? DecodeImage(string source)
    {
        try
        {
            if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var payload = source[(comma + 1)..];
                var bytes = source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(payload)
                    : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
                using var stream = new MemoryStream(bytes, writable: false);
                return new Bitmap(stream);
            }
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile && File.Exists(uri.LocalPath))
                return new Bitmap(uri.LocalPath);
        }
        catch { }
        return null;
    }

    private static TextBlock BuildInlineTextBlock(string text, double fontSize, FontWeight weight)
    {
        var block = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(fontSize * 1.48),
            Inlines = new InlineCollection()
        };
        var pos = 0;
        foreach (Match match in InlineRegex.Matches(text))
        {
            if (match.Index > pos)
                block.Inlines.Add(new Run(text[pos..match.Index]));
            if (match.Groups["bold"].Success)
            {
                block.Inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeight.Bold });
            }
            else
            {
                block.Inlines.Add(new Run(match.Groups["label"].Value)
                {
                    TextDecorations = TextDecorations.Underline
                });
            }
            pos = match.Index + match.Length;
        }
        if (pos < text.Length)
            block.Inlines.Add(new Run(text[pos..]));
        return block;
    }

    private static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png"
    };

    private void ClearPreviewBitmaps()
    {
        foreach (var bitmap in _previewBitmaps)
            bitmap.Dispose();
        _previewBitmaps.Clear();
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = !string.IsNullOrEmpty(message);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        MoreButton.IsEnabled = !busy;
    }
}
