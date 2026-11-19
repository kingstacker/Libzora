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
/// Image selection, resizing and replacement.
/// </summary>
internal sealed partial class BookReflectionEditorSurface : Border
{
    private void ImageFrame_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid { Tag: BlockView view }
            || e.Source is Button
            || !e.GetCurrentPoint(view.ImageFrame).Properties.IsLeftButtonPressed)
            return;

        SelectImage(view);
        e.Handled = true;
    }

    private void ImageFrame_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is Button
            || sender is not Grid { Tag: BlockView view })
            return;

        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteImage(view);
            e.Handled = true;
        }
    }

    private async void ImageActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ImageActionRequest request })
            return;

        SelectImage(request.Block);
        switch (request.Action)
        {
            case ImageActionKind.Replace:
                await ReplaceImageAsync(request.Block);
                break;
            case ImageActionKind.EditAlt:
                await EditImageAltAsync(request.Block);
                break;
            case ImageActionKind.ZoomOut:
                ScaleImage(request.Block, 1 / ImageZoomFactor);
                break;
            case ImageActionKind.ZoomIn:
                ScaleImage(request.Block, ImageZoomFactor);
                break;
            case ImageActionKind.Delete:
                DeleteImage(request.Block);
                break;
        }
        e.Handled = true;
    }

    private void SelectImage(BlockView view)
    {
        if (_disposed || view.State.Kind != BlockKind.Image)
            return;

        if (_selectedImage is { } previous && previous != view)
            SetImageControls(previous, false);

        _selectedImage = view;
        _activeBlock = view;
        _selection = null;
        SetImageControls(view, true);
        view.ImageFrame?.Focus();
    }

    private void ClearImageSelection()
    {
        if (_selectedImage is not { } selected)
            return;

        SetImageControls(selected, false);
        _selectedImage = null;
    }

    private static void SetImageControls(BlockView view, bool visible)
    {
        view.ImageSelectionBorder?.SetCurrentValue(IsVisibleProperty, visible);
        view.ImageActionBar?.SetCurrentValue(IsVisibleProperty, visible);
        view.ResizeHandle?.SetCurrentValue(
            IsVisibleProperty,
            visible && view.ImageBitmap is not null);
    }

    private void ImageResizeHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not AvaloniaRectangle { Tag: BlockView view }
            || view.ImageFrame is null
            || view.ImageBitmap is null
            || !e.GetCurrentPoint(view.ImageFrame).Properties.IsLeftButtonPressed)
            return;

        SelectImage(view);
        var size = GetImageDisplaySize(view);
        if (size.Width <= 0 || size.Height <= 0)
            return;

        _imageResizeSession = new ImageResizeSession(
            view,
            e.GetPosition(view.ImageFrame),
            size.Width,
            size.Height);
        view.IsResizing = true;
        e.Pointer.Capture(view.ResizeHandle);
        e.Handled = true;
    }

    private void ImageResizeHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_imageResizeSession is not { } session
            || session.View.ImageFrame is null)
            return;

        var point = e.GetPosition(session.View.ImageFrame);
        var width = session.StartWidth + point.X - session.StartPoint.X;
        session.Changed |= SetImageWidth(
            session.View,
            width,
            session.StartHeight / session.StartWidth);
        e.Handled = true;
    }

    private void ImageResizeHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_imageResizeSession is not { } session)
            return;

        session.View.IsResizing = false;
        e.Pointer.Capture(null);
        _imageResizeSession = null;
        if (session.Changed)
            RaiseContentChanged();
        e.Handled = true;
    }

    private void ScaleImage(BlockView view, double factor)
    {
        if (view.ImageBitmap is null || factor <= 0)
            return;

        if (SetImageWidth(view, GetImageDisplaySize(view).Width * factor))
            RaiseContentChanged();
    }

    private Size GetImageDisplaySize(BlockView view)
    {
        var width = view.ImageControl?.Bounds.Width ?? 0;
        var height = view.ImageControl?.Bounds.Height ?? 0;
        if (width > 0 && height > 0)
            return new Size(width, height);

        width = view.State.ImageWidth ?? view.ImageBitmap?.PixelSize.Width ?? 0;
        height = view.State.ImageHeight ?? view.ImageBitmap?.PixelSize.Height ?? 0;
        if (width > 0 && height <= 0 && view.ImageBitmap is { PixelSize.Width: > 0 } widthBitmap)
            height = width * widthBitmap.PixelSize.Height / widthBitmap.PixelSize.Width;
        if (height > 0 && width <= 0 && view.ImageBitmap is { PixelSize.Height: > 0 } heightBitmap)
            width = height * heightBitmap.PixelSize.Width / heightBitmap.PixelSize.Height;

        if (width <= 0 || height <= 0)
            return new Size(0, 0);

        var maxWidth = GetImageMaxWidth();
        var maxHeight = DefaultImageMaxHeight;
        var factor = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return factor < 1
            ? new Size(width * factor, height * factor)
            : new Size(width, height);
    }

    private bool SetImageWidth(
        BlockView view,
        double width,
        double? aspectRatio = null)
    {
        if (view.ImageBitmap is not { PixelSize.Width: > 0, PixelSize.Height: > 0 })
            return false;

        var current = GetImageDisplaySize(view);
        if (current.Width <= 0 || current.Height <= 0)
            return false;

        var imageAspectRatio = aspectRatio is > 0
            ? aspectRatio.Value
            : current.Height / current.Width;
        var targetWidth = Math.Clamp(width, MinimumImageWidth, GetImageMaxWidth());
        var targetHeight = targetWidth * imageAspectRatio;
        if (targetHeight > DefaultImageMaxHeight)
        {
            targetHeight = DefaultImageMaxHeight;
            targetWidth = targetHeight / imageAspectRatio;
        }

        var imageWidth = Math.Max(1, (int)Math.Round(targetWidth));
        var imageHeight = Math.Max(1, (int)Math.Round(targetHeight));
        var changed = view.State.ImageWidth != imageWidth
            || view.State.ImageHeight != imageHeight;
        view.State.ImageWidth = imageWidth;
        view.State.ImageHeight = imageHeight;
        UpdateImageView(view);
        return changed;
    }

    private double GetImageMaxWidth()
    {
        var availableWidth = Bounds.Width;
        if (availableWidth <= 0)
            return DefaultImageMaxWidth;

        // Leave room for the vertical scrollbar and the surface border. This
        // keeps resized images inside the native editor at its minimum width.
        return Math.Max(
            MinimumImageWidth,
            Math.Min(DefaultImageMaxWidth, availableWidth - 24));
    }

    private void UpdateImageConstraints()
    {
        if (_disposed) return;
        foreach (var view in _blocks.Where(view => view.State.Kind == BlockKind.Image))
            UpdateImageView(view);
    }

    private void UpdateImageView(BlockView view)
    {
        if (view.State.Kind != BlockKind.Image)
            return;

        view.ImageControl?.SetCurrentValue(MaxWidthProperty, GetImageMaxWidth());
        view.ImageControl?.SetCurrentValue(MaxHeightProperty, DefaultImageMaxHeight);
        if (view.ImageControl is { } image)
        {
            image.Source = view.ImageBitmap;
            image.IsVisible = view.ImageBitmap is not null;
            if (view.State.ImageWidth is > 0)
            {
                image.Width = view.State.ImageWidth.Value;
                image.Height = view.State.ImageHeight is > 0
                    ? view.State.ImageHeight.Value
                    : double.NaN;
            }
            else
            {
                image.Width = double.NaN;
                image.Height = double.NaN;
            }
            ToolTip.SetTip(image, view.State.ImageAlt);
        }
        if (view.ImagePlaceholder is { } placeholder)
        {
            placeholder.Text = string.IsNullOrWhiteSpace(view.State.ImageAlt)
                ? UiText.Get("图片无法显示")
                : view.State.ImageAlt;
            placeholder.IsVisible = view.ImageBitmap is null;
        }
        SetImageControls(view, _selectedImage == view);
    }

    private async Task ReplaceImageAsync(BlockView view)
    {
        var topLevel = GetOwnerWindow();
        if (topLevel is null || view.State.Kind != BlockKind.Image)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = UiText.Get("替换图片"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(UiText.Get("图片"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"]
                }
            ]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length == 0)
            return;

        var source = $"data:{MimeTypeFor(path)};base64,{Convert.ToBase64String(bytes)}";
        var bitmap = LoadBitmap(source);
        if (bitmap is null)
            return;

        var previousBitmap = view.ImageBitmap;
        var width = view.State.ImageWidth;
        view.State.ImageSource = source;
        if (width is > 0)
        {
            view.State.ImageHeight = Math.Max(
                1,
                (int)Math.Round(width.Value * bitmap.PixelSize.Height / (double)bitmap.PixelSize.Width));
        }
        view.ImageBitmap = bitmap;
        UpdateImageView(view);
        previousBitmap?.Dispose();
        RaiseContentChanged();
    }

    private async Task EditImageAltAsync(BlockView view)
    {
        var alt = await PromptForImageAltAsync(view.State.ImageAlt);
        if (alt is null || view.State.Kind != BlockKind.Image)
            return;

        view.State.ImageAlt = alt;
        UpdateImageView(view);
        RaiseContentChanged();
    }

    private void DeleteImage(BlockView view)
    {
        var index = _blocks.IndexOf(view);
        if (index < 0 || view.State.Kind != BlockKind.Image)
            return;

        var focus = _blocks
            .Skip(index + 1)
            .Concat(_blocks.Take(index).Reverse())
            .FirstOrDefault(candidate => candidate.Editor is not null);
        ClearImageSelection();
        RemoveBlock(view);
        FocusBlock(focus ?? _blocks.LastOrDefault(candidate => candidate.Editor is not null), 0);
        RaiseContentChanged();
    }
}
