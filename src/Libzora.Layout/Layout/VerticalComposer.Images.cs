using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Image blocks and rules.
/// </summary>
internal sealed partial class VerticalComposer
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
                _context.Options.ContentWidth * Math.Clamp(item.ImageWidthFactor!.Value, 0.01f, 1f),
                item.DecorativeQuote ? _context.Options.BaseFontSize * 5f : float.MaxValue)
            : _context.Options.ContentWidth;
        var maxH = Math.Max(1f, ContentHeight - _context.Options.BaseFontSize * 3.6f);
        var scale = Math.Min(maxW / info.Width, maxH / info.Height);
        if (!hasWidthConstraint)
        {
            scale = Math.Min(1f, scale);
        }
        var w = info.Width * scale;
        var h = info.Height * scale;

        // Images occupy their own page in vertical writing. The composer
        // keeps an empty current page after every forced image break; consume
        // it only for a later image so the first image does not get a blank
        // page before it, while consecutive images remain isolated.
        if (_pages.Current.Images.Count > 0)
        {
            _pages.Next();
        }
        else
        {
            _pages.NextIfUsed();
        }
        var x = block.AlignRight
            ? ContentRight - w
            : block.Center
                ? ContentLeft + (_context.Options.ContentWidth - w) / 2f
                : ContentLeft;
        var y = ContentTop + (ContentHeight - h) / 2f;
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddImage(new PlacedImage
        {
            Path = item.ImagePath,
            Rect = new SKRect(x, y, x + w, y + h),
            IsBlock = true,
            LinkHref = item.LinkHref,
            FootnoteHref = item.FootnoteHref,
        });
        if (item.LinkHref is not null || item.FootnoteHref is not null)
        {
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = item.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(x, y, x + w, y + h),
                Href = item.FootnoteHref ?? item.LinkHref!,
                FootnoteText = item.FootnoteText,
                Marker = item.FootnoteHref is not null ? item.Text : null,
            });
        }
        _pages.Next();
        _cursorX = ContentRight;
    }

    private void PlaceRule(ContentBlock block)
    {
        EnsureColumnSpace();
        _pages.RecordFragment(block.ElementId);
        _pages.RecordFragments(block.FragmentIds);
        _pages.AddDecoration(new PlacedRect
        {
            Kind = DecorationKind.Rule,
            Rect = new SKRect(ContentLeft, ContentTop + ContentHeight / 2f, ContentRight, ContentTop + ContentHeight / 2f + 1.2f),
        });
        _pages.MarkUsed();
        _cursorX -= ColumnPitch;
    }
}
