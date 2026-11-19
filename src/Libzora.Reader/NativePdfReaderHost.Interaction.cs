using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Selection and annotation state.
/// </summary>
public sealed partial class NativePdfReaderHost
{
    private static readonly Lazy<WriteableBitmap> SelectionInversionPixel = new(() =>
    {
        var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using var frame = bitmap.Lock();
        Marshal.WriteInt32(frame.Address, -1);
        return bitmap;
    });

    private Guid? _selectedAnnotationId;
    private bool _wordSelectionActive;
    private PdfPageLink? _pointerLink;
    private IReadOnlyList<PdfTextRange>? _selectionRanges;
    public string? SelectionFragment => PdfTextRanges.Encode(_selectionRanges);

    private IReadOnlyList<Rect> GetAnnotationLocalBounds(int page, ReaderAnnotation annotation) =>
        PdfTextRanges.Read(annotation.Fragment, annotation.StartOffset, annotation.EndOffset)
            .SelectMany(range => GetLocalRangeBounds(page, range.Start, range.End)).ToArray();

    private IReadOnlyList<Rect> GetSelectionBounds() => (_selectionRanges ?? [new(_selectionStart, _selectionEnd)])
        .SelectMany(range => GetRangeBounds(_selectionPage, range.Start, range.End)).ToArray();

    public IReadOnlyList<Rect> GetAnnotationBounds(ReaderAnnotation annotation)
    {
        if (!TryGetAnnotationAnchor(annotation, out var page, out _)) return [];
        if (ReaderPdfPointAnchor.TryParse(annotation.Fragment, out _, out _)
            && GetPdfPointPosition(annotation) is { } marker)
            return [new Rect(marker.X - 10, marker.Y - 10, 20, 20)];
        var transform = GetPageTransform(GetPageBounds(page));
        return GetAnnotationLocalBounds(page, annotation)
            .Select(rect => rect.TransformToAABB(transform)).ToArray();
    }

    public void SelectAnnotation(Guid id)
    {
        var selected = _annotations.Any(item => item.Id == id) ? id : (Guid?)null;
        if (_selectedAnnotationId == selected)
        {
            Focus();
            return;
        }
        _selectedAnnotationId = selected;
        if (selected is null)
        {
            _selectionDashTimer.Stop();
        }
        else if (!_selectionDashTimer.IsEnabled)
        {
            _selectionDashOffset = 0;
            _selectionDashTimer.Start();
        }
        InvalidateVisual();
        Focus();
    }

    /// <summary>The highlight/underline/sticky-note currently picked with the left button.</summary>
    public Guid? SelectedAnnotationId => _selectedAnnotationId;

    /// <summary>Drops the highlight/underline selection without touching the text selection.</summary>
    public void ClearAnnotationSelection()
    {
        if (_selectedAnnotationId is null) return;
        _selectedAnnotationId = null;
        _selectionDashTimer.Stop();
        InvalidateVisual();
    }

    public void SetAnnotations(IReadOnlyList<ReaderAnnotation> annotations)
    {
        _annotations = annotations.ToArray();
        if (!_annotations.Any(item => item.Id == _selectedAnnotationId)) _selectedAnnotationId = null;
        ClearHover();
        InvalidateVisual();
    }

    private IEnumerable<ReaderAnnotation> AnnotationsForPage(int page) => _annotations.Where(annotation =>
        annotation.ChapterPath.StartsWith("pdf:", StringComparison.Ordinal)
        && int.TryParse(annotation.ChapterPath.AsSpan(annotation.ChapterPath.LastIndexOf(':') + 1), out var number) && number == page);

    public void ClearSelection()
    {
        _selectionRanges = null;
        _wordSelectionActive = false;
        _selectionStart = _selectionEnd = 0;
        _selectionPage = PageNumber;
        InvalidateVisual();
        EmitSelection();
    }
}
