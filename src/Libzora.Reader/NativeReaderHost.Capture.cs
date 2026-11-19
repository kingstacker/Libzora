using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Visible page capture.
/// </summary>
public sealed partial class NativeReaderHost : Control, IReaderHost, IReaderPageSnapshotProvider, IReaderAppearanceHost, IReaderConfiguredHost, IReaderLocationHost, IReaderLayoutNavigationHost, IReaderPaginationHost, IReaderAnnotationHost, IReaderSpeechHost, IReaderSelectionHost, IReaderAnnotationBoundsHost, IReaderFootnoteVisualHost, IReaderPageCountHost, IReaderVisualTransitionHost
{
    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        var bitmap = _bitmap;
        if (bitmap is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        if (_bitmapDirty)
        {
            PaintPageIntoBitmap(_bitmapPixelWidth, _bitmapPixelHeight, CurrentScaling);
            _bitmapDirty = false;
        }

        try
        {
            using var frame = bitmap.Lock();
            var contentInfo = new SKImageInfo(
                _bitmapPixelWidth,
                _bitmapPixelHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);
            using var content = SKImage.FromPixelCopy(contentInfo, frame.Address, frame.RowBytes);
            var snapshotInfo = new SKImageInfo(
                _bitmapPixelWidth,
                _bitmapPixelHeight,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque);
            using var surface = SKSurface.Create(snapshotInfo);
            if (surface is null) return Task.FromResult<byte[]?>(null);

            var canvas = surface.Canvas;
            canvas.Clear(ReaderPalette.ToSkia(_palette.Page));
            canvas.Save();
            canvas.Scale((float)CurrentScaling);
            var bounds = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            ReaderPaperTexture.Paint(canvas, bounds, _palette.Page, _appearance);
            canvas.DrawImage(content, bounds);
            canvas.Restore();
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }
}
