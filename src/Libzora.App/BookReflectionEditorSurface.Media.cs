using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Libzora.Core;

namespace Libzora;

using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using AvaloniaRectangle = Avalonia.Controls.Shapes.Rectangle;

/// <summary>
/// Image block insertion, link prompts and alt-text prompts.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void InsertImageBlock(
        SelectionSnapshot selection,
        string source,
        string alt)
    {
        var view = selection.Block;
        var index = _blocks.IndexOf(view);
        if (index < 0) return;

        var start = Math.Clamp(selection.Start, 0, view.LastPlainText.Length);
        var end = Math.Clamp(selection.End, start, view.LastPlainText.Length);
        var before = SliceRuns(view.State.Inlines, 0, start);
        var after = SliceRuns(view.State.Inlines, end, view.LastPlainText.Length);
        var imageState = new BlockState
        {
            Kind = BlockKind.Image,
            ImageSource = source,
            ImageAlt = alt
        };

        if (before.Count == 0 && after.Count == 0)
        {
            // RemoveBlock normally keeps one editable paragraph alive. The
            // image insertion path creates that paragraph itself so it does
            // not leave two empty paragraphs behind.
            RemoveBlock(view, ensurePlaceholder: false);
            AddBlock(imageState, Math.Min(index, _blocks.Count));
            AddBlock(new BlockState(), Math.Min(index + 1, _blocks.Count));
            FocusBlock(_blocks[Math.Min(index + 1, _blocks.Count - 1)], 0);
        }
        else
        {
            view.State.Inlines = before;
            SetEditorText(view, view.State.PlainText, view.State.PlainText.Length);
            UpdateBlockView(view);
            AddBlock(imageState, index + 1);
            AddBlock(
                new BlockState { Inlines = after },
                index + 2);
            FocusBlock(_blocks[index + 2], 0);
        }

        _selection = null;
        RaiseContentChanged();
    }

    private async Task<string?> PromptForLinkAsync()
    {
        var owner = GetOwnerWindow();
        if (owner is null) return null;

        var urlBox = new TextBox
        {
            Text = "https://",
            MinHeight = 34,
            FontSize = 14
        };
        var errorText = new TextBlock
        {
            Foreground = AppAppearanceResources.GetBrush("DangerBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = UiText.Get("链接"),
            Width = 460,
            Height = 220,
            MinWidth = 400,
            MinHeight = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };

        void Complete(string? value)
        {
            if (value is not null)
                completion.TrySetResult(value);
            else
                completion.TrySetResult(null);
            dialog.Close();
        }

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var saveButton = new Button
        {
            Content = UiText.Get("确定"),
            Padding = new Thickness(14, 7),
            MinWidth = 78
        };
        saveButton.Click += (_, _) =>
        {
            var value = urlBox.Text?.Trim() ?? string.Empty;
            if (value.Length == 0) return;
            if (!value.Contains(':', StringComparison.Ordinal))
                value = "https://" + value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
            {
                errorText.Text = UiText.Get("请输入有效链接。");
                return;
            }
            Complete(uri.ToString());
        };

        urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
                RowSpacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = UiText.Get("链接地址"),
                        FontSize = 13
                    },
                    urlBox,
                    errorText,
                    actions
                }
            }
        };
        ApplyCustomDialogChrome(dialog, UiText.Get("链接"), () => Complete(null));
        Grid.SetRow(urlBox, 1);
        Grid.SetRow(errorText, 2);
        Grid.SetRow(actions, 3);

        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            urlBox.Focus();
            urlBox.SelectAll();
        };
        dialog.Show(owner);
        return await completion.Task;
    }

    private async Task<string?> PromptForImageAltAsync(string currentAlt)
    {
        var owner = GetOwnerWindow();
        if (owner is null) return null;

        var altBox = new TextBox
        {
            Text = currentAlt,
            MinHeight = 34,
            FontSize = 14,
            PlaceholderText = UiText.Get("可选")
        };
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = UiText.Get("编辑图片描述"),
            Width = 460,
            Height = 180,
            MinWidth = 400,
            MinHeight = 170,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = AppAppearanceResources.GetBrush("PaperBrush")
        };

        void Complete(string? value)
        {
            completion.TrySetResult(value);
            dialog.Close();
        }

        var cancelButton = new Button
        {
            Content = UiText.Get("取消"),
            Padding = new Thickness(14, 7)
        };
        cancelButton.Classes.Add("quiet");
        cancelButton.Click += (_, _) => Complete(null);

        var saveButton = new Button
        {
            Content = UiText.Get("确定"),
            Padding = new Thickness(14, 7),
            MinWidth = 78
        };
        saveButton.Click += (_, _) => Complete(altBox.Text?.Trim() ?? string.Empty);

        altBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        var fields = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*"),
            RowSpacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = UiText.Get("图片描述（可选）"),
                    FontSize = 13
                },
                altBox,
                actions
            }
        };
        Grid.SetRow(altBox, 1);
        Grid.SetRow(actions, 2);
        dialog.Content = new Border
        {
            Padding = new Thickness(22),
            Child = fields
        };

        dialog.Closed += (_, _) => completion.TrySetResult(null);
        dialog.Opened += (_, _) =>
        {
            altBox.Focus();
            altBox.SelectAll();
        };
        dialog.Show(owner);
        return await completion.Task;
    }
}
