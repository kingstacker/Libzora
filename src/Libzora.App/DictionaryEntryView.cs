using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>Shares the same dictionary layout between settings and KReader lookup.</summary>
public sealed class DictionaryEntryView : Grid
{
    private static DictionaryEntryStyleMode s_preferredStyleMode = DictionaryEntryStyleMode.BuiltIn;

    private IReadOnlyList<DictionaryEntry> _entries = [];
    private NativeWebViewReaderHost? _host;
    private readonly Grid _contentHost = new();
    private readonly ScrollViewer _fallbackScroll;
    private readonly StackPanel _fallbackPanel = new() { Spacing = 10, Margin = new Thickness(12, 10, 14, 14) };
    private readonly Border _toolbar;
    private readonly TextBlock _toolbarLabel;
    private readonly Border _switcherFrame;
    private readonly Button _builtInStyleButton;
    private readonly Button _originalStyleButton;
    private DictionaryEntryStyleMode _styleMode = s_preferredStyleMode;
    private int _renderSequence;
    private bool _loadingHtml;
    public bool UseReaderTheme { get; set; }

    public DictionaryEntryStyleMode StyleMode
    {
        get => _styleMode;
        set
        {
            if (_styleMode == value) return;
            _styleMode = value;
            s_preferredStyleMode = value;
            UpdateStyleToggleVisuals();
            if (_entries.Count > 0)
            {
                if (_entries.Any(e => e.Html is not null))
                    RenderEntries();
                else
                    RenderPlainEntries();
            }
        }
    }

    public event EventHandler<string>? TermRequested;

    public DictionaryEntryView()
    {
        RowDefinitions = new RowDefinitions("Auto,*");

        _toolbarLabel = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };

        _builtInStyleButton = CreateStyleButton(UiText.Localize("内置样式"), DictionaryEntryStyleMode.BuiltIn);
        _originalStyleButton = CreateStyleButton(UiText.Localize("词典原样式"), DictionaryEntryStyleMode.Original);

        var switcherRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        switcherRow.Children.Add(_builtInStyleButton);
        switcherRow.Children.Add(_originalStyleButton);

        _switcherFrame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = switcherRow
        };

        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 8
        };
        toolbarGrid.Children.Add(_toolbarLabel);
        SetColumn(_switcherFrame, 1);
        toolbarGrid.Children.Add(_switcherFrame);

        _toolbar = new Border
        {
            Padding = new Thickness(10, 5, 10, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsVisible = false,
            Child = toolbarGrid
        };

        SetRow(_toolbar, 0);
        Children.Add(_toolbar);

        _fallbackScroll = new ScrollViewer { Content = _fallbackPanel };
        _contentHost.Children.Add(_fallbackScroll);
        SetRow(_contentHost, 1);
        Children.Add(_contentHost);

        AttachedToVisualTree += (_, _) =>
        {
            _styleMode = s_preferredStyleMode;
            UpdateStyleToggleVisuals();
            RenderEntries();
        };
        DetachedFromVisualTree += (_, _) => ReleaseBrowser();
    }

    private Button CreateStyleButton(string label, DictionaryEntryStyleMode mode)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            MinWidth = 0,
            MinHeight = 22,
            Height = 22,
            Padding = new Thickness(8, 0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Focusable = false
        };
        button.Click += (_, _) => StyleMode = mode;
        return button;
    }

    public void SetEntries(IReadOnlyList<DictionaryEntry> entries)
    {
        _entries = entries;
        _styleMode = s_preferredStyleMode;
        if (_host is { } previousHost) ((Control)previousHost.View).IsVisible = false;
        _toolbar.IsVisible = entries.Count > 0;
        _toolbarLabel.Text = entries.Count switch
        {
            0 => string.Empty,
            1 => UiText.Localize("词条样式"),
            _ => string.Format(UiText.Localize("{0} 本词典"), entries.Count)
        };
        UpdateStyleToggleVisuals();
        if (entries.Any(e => e.Html is not null))
            ShowLoadingFallback();
        else
            RenderPlainEntries();
        _fallbackScroll.IsVisible = true;
        IsVisible = entries.Count > 0;
        RenderEntries();
    }

    private void ShowLoadingFallback()
    {
        _fallbackPanel.Children.Clear();
        _fallbackPanel.Children.Add(new TextBlock
        {
            Text = UiText.Localize("正在加载词条…"),
            FontSize = 12,
            Foreground = ResolveBrush("ReaderMutedBrush", "MutedInkBrush", Colors.Gray),
            TextWrapping = TextWrapping.Wrap
        });
    }

    private void RenderPlainEntries()
    {
        _fallbackPanel.Children.Clear();
        if (_entries.Count == 0) return;

        var ink = ResolveBrush("ReaderInkBrush", "InkBrush", Colors.Black);
        var muted = ResolveBrush("ReaderMutedBrush", "MutedInkBrush", Colors.Gray);
        var soft = ResolveBrush("ReaderHoverBrush", "SoftHoverBrush", Colors.WhiteSmoke);
        var border = ResolveBrush("ReaderBorderBrush", "HairlineBrush", Colors.LightGray);

        if (_styleMode == DictionaryEntryStyleMode.Original)
        {
            _fallbackPanel.Children.Add(new TextBlock
            {
                Text = PlainText(),
                FontSize = 13.5,
                LineHeight = 21,
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var entryStack = new StackPanel { Spacing = 6 };
            if (i > 0)
            {
                entryStack.Children.Add(new Border
                {
                    Height = 1,
                    Background = border,
                    Margin = new Thickness(0, 4, 0, 4)
                });
            }

            entryStack.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = soft,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                Child = new TextBlock
                {
                    Text = entry.DictionaryName,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = muted
                }
            });

            if (!string.IsNullOrWhiteSpace(entry.Term))
            {
                entryStack.Children.Add(new TextBlock
                {
                    Text = entry.Term,
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    Foreground = ink,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            entryStack.Children.Add(new TextBlock
            {
                Text = entry.Definition,
                FontSize = 14,
                LineHeight = 22,
                Foreground = ink,
                TextWrapping = TextWrapping.Wrap
            });

            _fallbackPanel.Children.Add(entryStack);
        }
    }

    private void UpdateStyleToggleVisuals()
    {
        var ink = ResolveBrush("ReaderInkBrush", "InkBrush", Colors.Black);
        var paper = ResolveBrush("ReaderPageBrush", "PaperBrush", Colors.White);
        var muted = ResolveBrush("ReaderMutedBrush", "MutedInkBrush", Colors.Gray);
        var border = ResolveBrush("ReaderBorderBrush", "HairlineBrush", Colors.LightGray);

        _toolbar.BorderBrush = border;
        _toolbarLabel.Foreground = muted;
        _switcherFrame.BorderBrush = border;

        var builtInActive = _styleMode == DictionaryEntryStyleMode.BuiltIn;
        _builtInStyleButton.Background = builtInActive ? ink : Brushes.Transparent;
        _builtInStyleButton.Foreground = builtInActive ? paper : muted;
        _originalStyleButton.Background = builtInActive ? Brushes.Transparent : ink;
        _originalStyleButton.Foreground = builtInActive ? muted : paper;
    }

    private async void RenderEntries()
    {
        var sequence = ++_renderSequence;
        if (!this.IsAttachedToVisualTree()) return;
        if (!_entries.Any(e => e.Html is not null))
        {
            if (_entries.Count > 0) ReleaseBrowser();
            RenderPlainEntries();
            return;
        }
        NativeWebViewReaderHost? host = null;
        try
        {
            if (_host is null)
            {
                host = new NativeWebViewReaderHost();
                _host = host;
                host.NavigationStarting += (_, e) =>
                {
                    if (e.Request?.Scheme == "libzora-dictionary")
                    {
                        e.Cancel = true;
                        var term = Uri.UnescapeDataString(e.Request.Query.TrimStart('?'));
                        if (term.Length is > 0 and <= 256)
                            Dispatcher.UIThread.Post(() => TermRequested?.Invoke(this, term));
                    }
                    // NativeWebView implements NavigateToString using a data URL on Windows.
                    else if (_loadingHtml && e.Request?.OriginalString.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) == true) { }
                    else if (e.Request is { } uri && uri.Scheme != "about"
                        && uri.AbsoluteUri != "https://libzora.invalid/dictionary/") e.Cancel = true;
                };
                host.NavigationCompleted += (_, e) =>
                {
                    if (!ReferenceEquals(_host, host)) return;
                    _loadingHtml = false;
                    if (e.IsSuccess)
                    {
                        _fallbackScroll.IsVisible = false;
                        ((Control)host.View).IsVisible = true;
                    }
                    else
                    {
                        ReleaseBrowser();
                        RenderPlainEntries();
                    }
                };
                _contentHost.Children.Add((Control)host.View);
            }
            else
            {
                host = _host;
            }
            var palette = ReadPalette();
            var entries = _entries;
            var styleMode = _styleMode;
            var htmlTask = Task.Run(() => DictionaryHtmlRenderer.Render(entries, palette, styleMode));
            // NativeWebView needs an attached adapter before accepting HTML navigation.
            await host.ReadyTask.WaitAsync(TimeSpan.FromSeconds(15));
            var html = await htmlTask;
            if (!ReferenceEquals(_host, host) || sequence != _renderSequence) return;
            _loadingHtml = true;
            ((Control)host.View).IsVisible = false;
            host.NavigateToString(html, new Uri("https://libzora.invalid/dictionary/"));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine("Dictionary HTML view: " + exception);
            if (ReferenceEquals(_host, host) && sequence == _renderSequence)
            {
                ReleaseBrowser();
                RenderPlainEntries();
            }
        }
    }

    private string PlainText() => string.Join("\n\n", _entries.Select(e => $"{e.Term} · {e.DictionaryName}\n{e.Definition}"));

    private IBrush ResolveBrush(string readerKey, string appKey, Color fallback)
    {
        if (UseReaderTheme && this.FindResource(readerKey.Replace("Brush", "Color")) is Color readerColor)
            return new SolidColorBrush(readerColor);
        return this.FindResource(UseReaderTheme ? readerKey : appKey) as IBrush ?? new SolidColorBrush(fallback);
    }

    private DictionaryHtmlPalette ReadPalette()
    {
        string ColorHex(string reader, string app, string fallback)
        {
            if (UseReaderTheme && this.FindResource(reader.Replace("Brush", "Color")) is Color readerColor)
                return $"#{readerColor.R:x2}{readerColor.G:x2}{readerColor.B:x2}";
            var brush = this.FindResource(UseReaderTheme ? reader : app) as ISolidColorBrush;
            return brush is null ? fallback : $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";
        }
        return new(ColorHex("ReaderPageBrush", "PaperBrush", "#ffffff"), ColorHex("ReaderInkBrush", "InkBrush", "#292d29"),
            ColorHex("ReaderMutedBrush", "MutedInkBrush", "#606a60"), ColorHex("ReaderHoverBrush", "SoftHoverBrush", "#e8ece6"),
            ColorHex("ReaderBorderBrush", "HairlineBrush", "#cdd4c9"));
    }

    private void ReleaseBrowser()
    {
        if (_host is not { } host) return;
        _host = null;
        _loadingHtml = false;
        host.Dispose();
        _contentHost.Children.Remove((Control)host.View);
    }
}
