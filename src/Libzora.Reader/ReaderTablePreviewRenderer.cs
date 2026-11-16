using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Renders one table block on a wide canvas for the full-screen table
/// inspector. This is deliberately a separate pass: the reading page keeps
/// its phone-width columns (or text fallback), while the inspector re-flows
/// the same block with the reader's current fonts, spacing and theme.
/// </summary>
internal static class ReaderTablePreviewRenderer
{
    private const float ViewportHeight = 1400f;
    private const float Inset = 24f;
    private const float MaxTotalHeight = 24_000f;
    private const float MaxPixels = 24_000_000f;

    internal static string? Render(
        ContentBlock source,
        ReaderLayoutSettings settings,
        float width,
        TypesetPaintTheme theme,
        TypesetFontLibrary fonts,
        Func<string, SKImage?>? imageResolver,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Composes and paints on a background task with the reader's shared
        // library, so a font switch must not release its faces mid-render.
        using var composition = fonts.BeginComposition();

        // The inspector is always horizontal: wide tables are the case this
        // exists for, and vertical writing would flatten them back to text.
        var options = new TypesetLayoutOptions
        {
            WritingMode = TypesetWritingMode.HorizontalTb,
            BaseFontSize = 16f * (float)settings.FontScale,
            LineHeight = (float)settings.LineHeight,
            ParagraphIndent = settings.ParagraphIndent,
            ParagraphSpacingEm = (float)settings.ParagraphSpacing,
            ParagraphSpacingMatchesLineHeight = settings.ParagraphSpacingMatchesLineHeight,
            OverridePublisherLineHeight = settings.OverridePublisherLineHeight,
            OverridePublisherParagraphSpacing = settings.OverridePublisherParagraphSpacing,
            AvoidWidowsAndOrphans = settings.AvoidWidowsAndOrphans,
            ViewportWidth = width,
            ViewportHeight = ViewportHeight,
            InsetHorizontal = Inset,
            InsetVertical = Inset,
        };
        var content = new ChapterContent
        {
            ChapterPath = "table-preview",
            BodyText = string.Empty,
            Blocks = new[] { CloneTableBlock(source) },
        };

        using var engine = new TypesetEngine(fonts, ownsFonts: false);
        var layout = engine.Compose(content, options, cancellationToken);

        var segments = new List<(LayoutPage Page, float Bottom)>();
        var totalHeight = 0f;
        foreach (var page in layout.Pages)
        {
            var bottom = ContentBottom(page);
            if (bottom <= page.InsetVertical + 0.5f)
            {
                continue;
            }

            segments.Add((page, bottom));
            totalHeight += bottom;
            if (totalHeight > MaxTotalHeight)
            {
                // A pathological table must not allocate an unbounded bitmap.
                break;
            }
        }

        if (segments.Count == 0)
        {
            return null;
        }

        totalHeight += 16f;
        var scale = 2f;
        if (width * totalHeight * scale * scale > MaxPixels)
        {
            scale = Math.Max(1f, MathF.Sqrt(MaxPixels / (width * totalHeight)));
        }

        var pixelWidth = Math.Max(1, (int)MathF.Ceiling(width * scale));
        var pixelHeight = Math.Max(1, (int)MathF.Ceiling(totalHeight * scale));

        var directory = Path.Combine(Path.GetTempPath(), "libzora-reader-preview");
        Directory.CreateDirectory(directory);
        CleanCache(directory);

        using var bitmap = new SKBitmap(
            pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(theme.Background);
            canvas.Scale(scale);
            var painter = new TypesetPainter(fonts, theme, imageResolver);
            var y = 0f;
            foreach (var (page, bottom) in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                canvas.Save();
                canvas.Translate(0f, y);
                painter.Paint(canvas, page);
                canvas.Restore();
                y += bottom;
            }
        }

        var path = Path.Combine(directory, $"table-{Guid.NewGuid():N}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        if (data is null)
        {
            return null;
        }

        using (var stream = File.Create(path))
        {
            data.SaveTo(stream);
        }

        return path;
    }

    /// <summary>Bottom of the visible ink on a preview page, in layout units.</summary>
    private static float ContentBottom(LayoutPage page)
    {
        var bottom = page.InsetVertical;
        foreach (var run in page.Runs)
        {
            if (run.Y.Length == 0)
            {
                continue;
            }

            var lastY = 0f;
            foreach (var y in run.Y)
            {
                lastY = Math.Max(lastY, y);
            }

            bottom = Math.Max(bottom, run.OriginY + lastY + run.Descent + 2f);
        }

        foreach (var image in page.Images)
        {
            bottom = Math.Max(bottom, image.Rect.Bottom);
        }

        foreach (var decoration in page.Decorations)
        {
            bottom = Math.Max(bottom, decoration.Rect.Bottom);
        }

        return Math.Min(page.Height, bottom);
    }

    /// <summary>
    /// Snapshot-only copy: offsets become -1 because the synthetic chapter
    /// has no body text, while styles, images and cells are preserved.
    /// </summary>
    private static ContentBlock CloneTableBlock(ContentBlock block)
    {
        var table = block.Table!;
        return new ContentBlock
        {
            Kind = BlockKind.Table,
            ElementId = block.ElementId,
            FragmentIds = Array.Empty<string>(),
            FootnoteReferenceFragmentIds = Array.Empty<string>(),
            Style = block.Style,
            Center = block.Center,
            AlignRight = block.AlignRight,
            Justify = block.Justify,
            TextIndentEm = block.TextIndentEm,
            SpaceBeforeLines = block.SpaceBeforeLines,
            SpaceAfterLines = block.SpaceAfterLines,
            LineHeightFactor = block.LineHeightFactor,
            MarginTopEm = block.MarginTopEm,
            MarginBottomEm = block.MarginBottomEm,
            MarginLeftEm = block.MarginLeftEm,
            MarginRightEm = block.MarginRightEm,
            PaddingLeftEm = block.PaddingLeftEm,
            PaddingRightEm = block.PaddingRightEm,
            Table = new ContentTable
            {
                Rows = table.Rows.Select(row => new ContentTableRow
                {
                    Header = row.Header,
                    Cells = row.Cells.Select(cell => new ContentTableCell
                    {
                        Items = cell.Items.Select(ClonePreviewItem).ToList(),
                        ColSpan = cell.ColSpan,
                        RowSpan = cell.RowSpan,
                        Header = cell.Header,
                        BorderTopEm = cell.BorderTopEm,
                        BorderRightEm = cell.BorderRightEm,
                        BorderBottomEm = cell.BorderBottomEm,
                        BorderLeftEm = cell.BorderLeftEm,
                    }).ToList(),
                }).ToList(),
            },
            Items = block.Items.Select(ClonePreviewItem).ToList(),
        };
    }

    private static InlineItem ClonePreviewItem(InlineItem item) => new()
    {
        Kind = item.Kind,
        Text = item.Text,
        TextStart = -1,
        Style = item.Style,
        ImagePath = item.ImagePath,
        ImageHeightEm = item.ImageHeightEm,
        ImageWidthFactor = item.ImageWidthFactor,
        DecorativeQuote = item.DecorativeQuote,
        LinkHref = item.LinkHref,
        FootnoteHref = item.FootnoteHref,
        FootnoteText = item.FootnoteText,
        RubyText = item.RubyText,
        Ghost = item.Ghost,
    };

    private static void CleanCache(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var file in Directory.EnumerateFiles(directory, "table-*.png"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
