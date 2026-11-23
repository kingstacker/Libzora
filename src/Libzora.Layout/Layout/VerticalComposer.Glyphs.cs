using SkiaSharp;

namespace Libzora.Layout;

/// <summary>
/// Vertical offset, ruby and glyph resolution.
/// </summary>
internal sealed partial class VerticalComposer
{
    private void ShiftPlacedDown(float shift)
    {
        // Runs placed for the current column start at the page's run list
        // position recorded before the column began; shifting rewrites their
        // origins. This avoids a second pass over cell geometry.
        for (var i = _lastColumnFirstRun; i < _pages.Current.Runs.Count; i++)
        {
            var run = _pages.Current.Runs[i];
            _pages.Current.Runs[i] = run with { OriginY = run.OriginY + shift };
        }

        for (var i = _lastColumnFirstDecoration; i < _pages.Current.Decorations.Count; i++)
        {
            var decoration = _pages.Current.Decorations[i];
            _pages.Current.Decorations[i] = new PlacedRect
            {
                Kind = decoration.Kind,
                Rect = OffsetY(decoration.Rect, shift),
                TextStart = decoration.TextStart,
                TextLength = decoration.TextLength,
            };
        }

        for (var i = _lastColumnFirstHotZone; i < _pages.Current.HotZones.Count; i++)
        {
            var zone = _pages.Current.HotZones[i];
            _pages.Current.HotZones[i] = new PlacedHotZone
            {
                Kind = zone.Kind,
                Rect = OffsetY(zone.Rect, shift),
                Href = zone.Href,
                FootnoteText = zone.FootnoteText,
                Marker = zone.Marker,
            };
        }

        for (var i = _lastColumnFirstImage; i < _pages.Current.Images.Count; i++)
        {
            var image = _pages.Current.Images[i];
            _pages.Current.Images[i] = new PlacedImage
            {
                Path = image.Path,
                Rect = OffsetY(image.Rect, shift),
                RotationDegrees = image.RotationDegrees,
                IsBlock = image.IsBlock,
                LinkHref = image.LinkHref,
                FootnoteHref = image.FootnoteHref,
                FootnoteText = image.FootnoteText,
            };
        }

        for (var i = _lastColumnFirstDebugBox; i < _pages.Current.DebugBoxes.Count; i++)
        {
            var box = _pages.Current.DebugBoxes[i];
            _pages.Current.DebugBoxes[i] = box with { Rect = OffsetY(box.Rect, shift) };
        }
    }

    private static SKRect OffsetY(SKRect rect, float amount) =>
        new(rect.Left, rect.Top + amount, rect.Right, rect.Bottom + amount);

    private float GetTopHang(LayoutCell cell) =>
        TypesetText.IsProhibitedAtLineStart(cell.Text)
        && TypesetText.IsHangablePunctuation(cell.Text)
            ? Math.Min(CellPitch * 0.5f, Math.Max(1f, CellPitch - 1f))
            : 0f;

    private void PlaceCell(
        LayoutCell cell,
        float colCenter,
        float columnLeft,
        float y,
        float flowAdvance,
        float topHang = 0f)
    {
        var paintY = y - topHang;
        // In vertical writing a footnote reference is not raised along the
        // reading direction the way a Latin superscript is; it belongs at the
        // upper-right of the base glyph. Keep only a slight upward nudge and
        // put most of the displacement across the column so the reference
        // does not collide with the preceding character.
        var footnoteMarker = cell.FootnoteMarker && cell.Superscript;
        var superscriptShift = cell.Superscript
            ? -(footnoteMarker
                ? _context.Options.BaseFontSize * 0.10f
                : Math.Max(_context.Options.BaseFontSize * 0.38f, cell.FontSize * 0.45f))
            : 0f;
        var subscriptShift = cell.Subscript ? cell.FontSize * 0.20f : 0f;
        var flowShift = superscriptShift + subscriptShift;
        float MarkerShiftX(float freeSpace) => footnoteMarker
            ? Math.Min(
                _context.Options.BaseFontSize * 0.22f,
                Math.Max(0f, freeSpace) * 0.45f)
            : 0f;

        if (!cell.IsSpace && _context.Options.CollectDebugBoxes)
        {
            _pages.AddDebugBox(new TypesetDebugBox(
                new SKRect(columnLeft, paintY, columnLeft + ColumnPitch, y + flowAdvance),
                IsCompatibilityCell(cell)
                    ? TypesetDebugBoxKind.CompatibilityCell
                    : TypesetDebugBoxKind.HanCell));
        }

        if (cell.ImagePath is not null)
        {
            var imageWidth = cell.ImageSideways
                ? Math.Max(1f, cell.ImageHeight)
                : Math.Max(1f, cell.ImageWidth);
            var imageHeight = cell.ImageSideways
                ? Math.Max(1f, cell.ImageWidth)
                : Math.Max(1f, cell.ImageHeight);
            var availableHeight = flowAdvance;
            var imageTop = paintY + Math.Max(0f, (availableHeight - imageHeight) / 2f) + flowShift;
            var imageLeft = colCenter - imageWidth / 2f
                + MarkerShiftX(BodyColumnWidth - imageWidth);
            var imageRect = new SKRect(
                imageLeft,
                imageTop,
                imageLeft + imageWidth,
                imageTop + imageHeight);
            _pages.AddImage(new PlacedImage
            {
                Path = cell.ImagePath,
                Rect = imageRect,
                RotationDegrees = cell.ImageSideways ? 90f : 0f,
                LinkHref = cell.LinkHref,
                FootnoteHref = cell.FootnoteHref,
                FootnoteText = cell.FootnoteText,
            });

            if (cell.LinkHref is not null || cell.FootnoteHref is not null)
            {
                _pages.AddHotZone(new PlacedHotZone
                {
                    Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                    Rect = imageRect,
                    Href = cell.FootnoteHref ?? cell.LinkHref!,
                    FootnoteText = cell.FootnoteText,
                    Marker = cell.FootnoteHref is not null ? cell.Text : null,
                });
            }

            return;
        }

        if (cell.Sideways)
        {
            var ascent = cell.Ascent;
            var descent = cell.Descent;
            var originX = colCenter - (ascent - descent) / 2f + MarkerShiftX(BodyColumnWidth - cell.Advance);
            var originY = paintY + flowShift
                + (cell.RubyAnnotation is not null ? Math.Max(0f, (flowAdvance - cell.Advance) / 2f) : 0f);
            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = cell.GlyphX,
                Y = cell.GlyphY,
                OriginX = originX,
                OriginY = originY,
                FlowAdvance = cell.Advance,
                Sideways = true,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
            };
            _pages.AddRun(run);
            AddDebugGlyphBox(cell, run);
        }
        else if (cell.Combined)
        {
            var advance = Math.Max(1f, cell.Advance);
            var scale = Math.Min(1f, _context.Options.BaseFontSize / advance);
            var baseline = paintY + flowAdvance / 2f + (cell.Ascent - cell.Descent) / 2f + flowShift;
            var scaledAdvance = advance * scale;
            var originX = columnLeft + (BodyColumnWidth - scaledAdvance) / 2f
                + MarkerShiftX(BodyColumnWidth - scaledAdvance);
            var glyphX = new float[cell.GlyphX.Length];
            for (var i = 0; i < glyphX.Length; i++)
            {
                glyphX[i] = cell.GlyphX[i] * scale;
            }

            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = glyphX,
                Y = Zeroed(cell.GlyphY.Length),
                OriginX = originX,
                OriginY = baseline,
                FlowAdvance = flowAdvance,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
                Scale = scale,
                CellWidth = BodyColumnWidth,
            };
            _pages.AddRun(run);
            AddDebugGlyphBox(cell, run);
        }
        else
        {
            // Upright single-glyph cell on the fixed grid.
            var inkBounds = GetUprightGlyphInkBounds(cell);
            var baselineCorrection = 0f;
            // ASCII digits and brackets are narrower than one em. Center the
            // actual shaped advance inside the cell; centering a fixed
            // font-size box leaves those glyphs visibly offset from the CJK
            // characters around them.
            var glyphAdvance = Math.Max(0f, cell.Advance);
            var glyphOffsetX = (BodyColumnWidth - glyphAdvance) / 2f
                + MarkerShiftX(BodyColumnWidth - glyphAdvance);
            if (inkBounds is { } bounds
                && (!TypesetText.IsPunctuation(cell.Text)
                    || TypesetText.IsVerticallyCenteredMark(cell.Text)))
            {
                // A font's baseline metrics are deliberately generous. That
                // is correct for line layout, but a circular ideographic zero
                // (U+3007) can then sit visibly high inside a fixed book grid.
                // Center the actual upright ink for ordinary ideographs and
                // numeric glyphs. Mainland Chinese punctuation is excluded:
                // its right-upper placement is part of the publication style;
                // the Chinese interpunct (U+00B7) is an explicit centred mark.
                // Keep the established advance-based horizontal placement for
                // Latin/digit compatibility cells. Native CJK cells also use
                // their actual ink center so a font-sidebearing anomaly cannot
                // move an ideographic zero away from the grid center.
                if (TypesetText.IsVerticallyCenteredMark(cell.Text)
                    || TypesetText.TryGetFirstScalar(cell.Text, out var scalar)
                    && TypesetText.IsCjk(scalar))
                {
                    glyphOffsetX = BodyColumnWidth / 2f - (bounds.Left + bounds.Right) / 2f;
                }
                baselineCorrection = -(cell.Ascent - cell.Descent) / 2f
                    - (bounds.Top + bounds.Bottom) / 2f;
            }

            var baseline = paintY
                + flowAdvance / 2f
                + (cell.Ascent - cell.Descent) / 2f
                + baselineCorrection
                + flowShift;
            var originX = columnLeft;
            var glyphX = new float[cell.Glyphs.Length];
            var glyphY = new float[cell.Glyphs.Length];
            for (var i = 0; i < glyphX.Length; i++)
            {
                glyphX[i] = i < cell.GlyphX.Length
                    ? glyphOffsetX + cell.GlyphX[i]
                    : glyphOffsetX;
                glyphY[i] = i < cell.GlyphY.Length ? cell.GlyphY[i] : 0f;
            }

            var run = new PlacedRun
            {
                FontPath = cell.FontPath,
                FontSize = cell.FontSize,
                Glyphs = cell.Glyphs,
                X = glyphX,
                Y = glyphY,
                OriginX = originX,
                OriginY = baseline,
                FlowAdvance = flowAdvance,
                SyntheticBold = cell.Style.Bold,
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
                Clusters = CloneClusters(cell),
                Style = cell.Style,
                CellWidth = BodyColumnWidth,
            };
            _pages.AddRun(run);
            AddDebugGlyphBox(cell, run);
        }

        if (cell.RubyAnnotation is not null)
        {
            PlaceRubyAnnotation(cell, columnLeft, paintY, flowAdvance);
        }

        if (cell.TextStart >= 0 && cell.TextLength > 0)
        {
            _pages.TrackText(cell.TextStart, cell.TextStart + cell.TextLength);
        }

        if (cell.LinkHref is not null || cell.FootnoteHref is not null)
        {
            var top = paintY;
            var height = flowAdvance;
            _pages.AddHotZone(new PlacedHotZone
            {
                Kind = cell.FootnoteHref is not null ? HotZoneKind.FootnoteMarker : HotZoneKind.Link,
                Rect = new SKRect(columnLeft, top, columnLeft + ColumnPitch, top + height),
                Href = cell.FootnoteHref ?? cell.LinkHref!,
                FootnoteText = cell.FootnoteText,
                Marker = cell.FootnoteHref is not null ? cell.Text : null,
            });
        }

        if (cell.Style.Underline && cell.TextLength > 0)
        {
            var barX = colCenter - cell.FontSize / 2f - 1.6f;
            var height = cell.Sideways ? cell.Advance : CellPitch;
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.Underline,
                Rect = new SKRect(barX, paintY, barX + 1.2f, paintY + height),
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
            });
        }

        if (cell.Style.Strikeout && cell.TextLength > 0)
        {
            // In vertical text the strike line runs along the column centre.
            var barX = colCenter - 0.6f;
            var height = cell.Sideways ? cell.Advance : CellPitch;
            _pages.AddDecoration(new PlacedRect
            {
                Kind = DecorationKind.Strikeout,
                Rect = new SKRect(barX, paintY, barX + 1.2f, paintY + height),
                TextStart = cell.TextStart,
                TextLength = cell.TextLength,
            });
        }
    }
}
