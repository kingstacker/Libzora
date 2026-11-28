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
        if (_context.Options.MangaMode)
        {
            PlaceMangaImageBlock(block, info, item);
            return;
        }
        if (block.PageBreakBefore && !ContinuousScroll)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
            _previousBlockBottomSpace = 0f;
        }

        var hasWidthConstraint = item.ImageWidthFactor is > 0f;
        var maxW = hasWidthConstraint
            ? Math.Min(
                ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                item.DecorativeQuote ? BaseFontSize * 5f : float.MaxValue)
            : ContentWidth;
        var captionReserve = !ContinuousScroll && block.KeepWithNext ? BodyLineHeight * 1.5f : 0f;
        var maxH = Math.Max(1f, _context.Options.ContentHeight - BaseFontSize * 1.4f - captionReserve);
        var scale = Math.Min(maxW / info.Width, maxH / info.Height);
        if (!hasWidthConstraint)
        {
            scale = Math.Min(1f, scale);
        }
        var w = info.Width * scale;
        var h = info.Height * scale;

        var topGap = _pages.CurrentPageHasContent || ContinuousScroll
            ? Math.Max(0f, BaseFontSize * 0.65f - _previousBlockBottomSpace)
            : 0f;
        if (ContinuousScroll)
        {
            _cursorY += topGap;
            WrapContinuousScrollPage();
        }
        else if (_cursorY + topGap + h + captionReserve > ContentBottom + 0.01f && _pages.CurrentPageHasContent)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
        }
        else
        {
            _cursorY += topGap;
        }

        var x = block.AlignRight
            ? InsetH + ContentWidth - w
            : block.Center
                ? InsetH + (ContentWidth - w) / 2f
                : InsetH;
        _pages.RecordFragment(block.ElementId, _cursorY);
        _pages.RecordFragments(block.FragmentIds, _cursorY);
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
        var bottomGap = block.KeepWithNext ? BaseFontSize * 0.35f : BaseFontSize * 0.70f;
        _cursorY += h + bottomGap;
        if (block.PageBreakAfter && !ContinuousScroll)
        {
            _pages.NextIfUsed();
            _cursorY = InsetV;
            _previousBlockBottomSpace = 0f;
        }
        else
        {
            _previousBlockBottomSpace = bottomGap;
            WrapContinuousScrollPage();
        }
    }

    private void PlaceMangaImageBlock(ContentBlock block, SKImageInfo info, InlineItem item)
    {
        var crop = _context.Options.MangaAutoCrop
            ? ImageCropDetector.GetCachedImageCropBox(item.ImagePath!)
            : null;
        var cropLeft = crop?.Left ?? 0f;
        var cropTop = crop?.Top ?? 0f;
        var cropW = crop?.Width ?? 1f;
        var cropH = crop?.Height ?? 1f;
        var srcW = Math.Max(1f, info.Width * cropW);
        var srcH = Math.Max(1f, info.Height * cropH);
        var vpW = Math.Max(1f, _context.Options.ViewportWidth);
        var vpH = Math.Max(1f, _context.Options.ViewportHeight);

        if (_context.Options.SeamlessImageScroll)
        {
            var scale = vpW / srcW;
            var totalH = Math.Max(1f, srcH * scale);
            var remainingH = totalH;
            var sliceTopFraction = 0f;
            while (remainingH > 0.5f)
            {
                if (_cursorY >= vpH - 0.5f)
                {
                    _pages.Next();
                    _cursorY = 0f;
                }

                var space = vpH - _cursorY;
                var sliceH = Math.Min(remainingH, space);
                var sliceFrac = sliceH / totalH;
                var sliceCrop = new SKRect(
                    cropLeft,
                    cropTop + cropH * sliceTopFraction,
                    cropLeft + cropW,
                    cropTop + cropH * Math.Min(1f, sliceTopFraction + sliceFrac));
                _pages.RecordFragment(block.ElementId, _cursorY);
                _pages.RecordFragments(block.FragmentIds, _cursorY);
                _pages.AddImage(new PlacedImage
                {
                    Path = item.ImagePath!,
                    Rect = new SKRect(0f, _cursorY, vpW, _cursorY + sliceH),
                    SourceCrop = sliceCrop,
                    IsBlock = true,
                    LinkHref = item.LinkHref,
                    FootnoteHref = item.FootnoteHref,
                });
                _cursorY += sliceH;
                remainingH -= sliceH;
                sliceTopFraction += sliceFrac;
            }

            return;
        }

        _pages.NextIfUsed();
        _cursorY = 0f;
        var pageScale = Math.Min(vpW / srcW, vpH / srcH);
        var pw = srcW * pageScale;
        var ph = srcH * pageScale;
        var px = (vpW - pw) / 2f;
        var py = (vpH - ph) / 2f;
        _pages.RecordFragment(block.ElementId, _cursorY);
        _pages.RecordFragments(block.FragmentIds, _cursorY);
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath!,
            Rect = new SKRect(px, py, px + pw, py + ph),
            SourceCrop = crop,
            IsBlock = true,
            LinkHref = item.LinkHref,
            FootnoteHref = item.FootnoteHref,
        });
        _cursorY = vpH;
    }

    private void PlaceRule(ContentBlock block)
    {
        if (ContinuousScroll)
        {
            _cursorY += BaseFontSize * 1.2f;
            WrapContinuousScrollPage();
        }
        else
        {
            _cursorY += BaseFontSize * 2f;
            if (_cursorY + 1f > ContentBottom + 0.01f)
            {
                _pages.NextIfUsed();
                _cursorY = InsetV + BaseFontSize;
            }
        }

        _pages.RecordFragment(block.ElementId, _cursorY);
        _pages.RecordFragments(block.FragmentIds, _cursorY);
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(InsetH, _cursorY, InsetH + ContentWidth, _cursorY + 1.2f),
        });
        _cursorY += ContinuousScroll ? BaseFontSize * 1.2f : BaseFontSize * 2f;
        WrapContinuousScrollPage();
    }
}
