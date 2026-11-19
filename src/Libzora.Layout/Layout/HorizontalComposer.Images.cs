using System.Text;
using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Image blocks and horizontal rules.
/// </summary>
internal sealed partial class HorizontalComposer
{
    private void PlaceImageBlock(ContentBlock block)
    {
        var item = block.Items.FirstOrDefault(i => i.Kind == InlineKind.Image);
        if (item?.ImagePath is null || !File.Exists(item.ImagePath))
        {
            return;
        }

        using var codec = SKCodec.Create(item.ImagePath);
        if (codec is null)
        {
            return;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return;
        }
        var hasWidthConstraint = item.ImageWidthFactor is > 0f;
        var maxW = hasWidthConstraint
            ? Math.Min(
                ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                item.DecorativeQuote ? BaseFontSize * 5f : float.MaxValue)
            : ContentWidth;
        var maxH = Math.Max(1f, _context.Options.ContentHeight - BaseFontSize * 3.6f);
        var scale = Math.Min(maxW / info.Width, maxH / info.Height);
        if (!hasWidthConstraint)
        {
            scale = Math.Min(1f, scale);
        }
        var w = info.Width * scale;
        var h = info.Height * scale;

        _cursorY += BaseFontSize * 1.8f;
        if (_cursorY + h > ContentBottom + 0.01f)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV + BaseFontSize * 0.5f;
        }

        var x = block.AlignRight
            ? InsetH + ContentWidth - w
            : block.Center
                ? InsetH + (ContentWidth - w) / 2f
                : InsetH;
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath,
            Rect = new SKRect(x, _cursorY, x + w, _cursorY + h),
            IsBlock = true,
            LinkHref = item.LinkHref,
            FootnoteHref = item.FootnoteHref,
        });
        if (item.LinkHref is not null || item.FootnoteHref is not null)
        {
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = item.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(x, _cursorY, x + w, _cursorY + h),
                Href = item.FootnoteHref ?? item.LinkHref!,
                FootnoteText = item.FootnoteText,
                Marker = item.FootnoteHref is not null ? item.Text : null,
            });
        }
        _cursorY += h + BaseFontSize * 1.8f;
    }

    private void PlaceRule(ContentBlock block)
    {
        _cursorY += BaseFontSize * 2f;
        if (_cursorY + 1f > ContentBottom + 0.01f)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV + BaseFontSize;
        }

        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(InsetH, _cursorY, InsetH + ContentWidth, _cursorY + 1.2f),
        });
        _cursorY += BaseFontSize * 2f;
    }
}
