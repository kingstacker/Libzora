using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Libzora.Layout;

/// <summary>
/// Paragraph flushing and table blocks.
/// </summary>
public sealed partial class XhtmlChapterLoader
{
    private int AppendBodyText(string value)
    {
        var start = _body.Length;
        _body.Append(value);
        return start;
    }

    private void FlushParagraph(FlowContext ctx)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var hasVisible = _pending.Any(i =>
            (!i.Ghost && i.Kind == InlineKind.Image) ||
            (!i.Ghost && i.Text.Length > 0 && !string.IsNullOrWhiteSpace(i.Text)) ||
            i.Kind == InlineKind.LineBreak ||
            i.Kind == InlineKind.FootnoteMarker);

        if (!hasVisible)
        {
            _pending.Clear();
            return;
        }

        var items = _pending.ToList();
        _pending.Clear();

        _blocks.Add(new ContentBlock
        {
            Kind = ctx.Kind == BlockKind.Paragraph && ctx.IsQuote
                ? BlockKind.Blockquote
                : ctx.Kind,
            ElementId = ctx.ElementId,
            Style = ctx.BaseStyle,
            Center = ctx.Center,
            AlignRight = ctx.AlignRight,
            Justify = ctx.Justify,
            TextIndentEm = ctx.TextIndentEm,
            ExplicitTextIndent = ctx.ExplicitTextIndent,
            SpaceBeforeLines = ctx.SpaceBeforeLines,
            SpaceAfterLines = ctx.SpaceAfterLines,
            ListMarker = ctx.ListMarker,
            ListLevel = ctx.ListLevel,
            LineHeightFactor = ctx.LineHeightFactor,
            MarginTopEm = ctx.MarginTopEm,
            MarginBottomEm = ctx.MarginBottomEm,
            MarginLeftEm = ctx.MarginLeftEm,
            MarginRightEm = ctx.MarginRightEm,
            PaddingLeftEm = ctx.PaddingLeftEm,
            PaddingRightEm = ctx.PaddingRightEm,
            BorderLeftEm = ctx.BorderLeftEm,
            HeadingLevel = ctx.HeadingLevel,
            PageBreakBefore = ctx.PageBreakBefore,
            PageBreakAfter = ctx.PageBreakAfter,
            KeepWithNext = ctx.KeepWithNext,
            IsCaption = ctx.IsCaption,
            FootnoteReferenceFragmentIds = _pendingFragmentIds.Where(_footnoteReferenceFragmentIds.Contains).ToArray(),
            FragmentIds = TakePendingFragmentIds(),
            Items = items,
        });
    }

    private void ApplyContainerPageBreaks(int startBlockIndex, CssHints hints)
    {
        if (startBlockIndex >= _blocks.Count)
        {
            return;
        }

        if (hints.PageBreakBefore)
        {
            var first = _blocks[startBlockIndex];
            _blocks[startBlockIndex] = CloneWithBreakFlags(first, pageBreakBefore: true);
        }

        if (hints.PageBreakAfter)
        {
            var last = _blocks[^1];
            _blocks[^1] = CloneWithBreakFlags(last, pageBreakAfter: true);
        }
    }

    private void LinkFigureCaptions(int startBlockIndex)
    {
        for (var i = startBlockIndex; i < _blocks.Count - 1; i++)
        {
            if (_blocks[i].Kind == BlockKind.Image && _blocks[i + 1].IsCaption)
            {
                _blocks[i] = CloneWithBreakFlags(_blocks[i], keepWithNext: true);
            }
            else if (_blocks[i].IsCaption && _blocks[i + 1].Kind is BlockKind.Image or BlockKind.Table)
            {
                _blocks[i] = CloneWithBreakFlags(_blocks[i], keepWithNext: true);
            }
        }
    }

    private static ContentBlock CloneWithBreakFlags(
        ContentBlock source,
        bool? pageBreakBefore = null,
        bool? pageBreakAfter = null,
        bool? keepWithNext = null) => new()
    {
        Kind = source.Kind,
        ElementId = source.ElementId,
        FragmentIds = source.FragmentIds,
        FootnoteReferenceFragmentIds = source.FootnoteReferenceFragmentIds,
        Style = source.Style,
        Center = source.Center,
        AlignRight = source.AlignRight,
        Justify = source.Justify,
        TextIndentEm = source.TextIndentEm,
        ExplicitTextIndent = source.ExplicitTextIndent,
        SpaceBeforeLines = source.SpaceBeforeLines,
        SpaceAfterLines = source.SpaceAfterLines,
        ListMarker = source.ListMarker,
        ListLevel = source.ListLevel,
        LineHeightFactor = source.LineHeightFactor,
        MarginTopEm = source.MarginTopEm,
        MarginBottomEm = source.MarginBottomEm,
        MarginLeftEm = source.MarginLeftEm,
        MarginRightEm = source.MarginRightEm,
        PaddingLeftEm = source.PaddingLeftEm,
        PaddingRightEm = source.PaddingRightEm,
        BorderLeftEm = source.BorderLeftEm,
        HeadingLevel = source.HeadingLevel,
        PageBreakBefore = pageBreakBefore ?? source.PageBreakBefore,
        PageBreakAfter = pageBreakAfter ?? source.PageBreakAfter,
        KeepWithNext = keepWithNext ?? source.KeepWithNext,
        IsCaption = source.IsCaption,
        Table = source.Table,
        Items = source.Items,
    };

    private bool _buildingTableCells;

    /// <summary>
    /// Reads a table into rows and cells while keeping the body text stream in
    /// document order. Inline content still flows through the normal walker,
    /// so character offsets, links, ruby and fragment anchors keep working.
    /// </summary>
    private void AddTableBlock(XElement table, FlowContext ctx)
    {
        var rows = new List<ContentTableRow>();
        var defaultBorder = ParseTableBorderAttribute(table);
        var wasBuildingCells = _buildingTableCells;
        _buildingTableCells = true;
        try
        {
            foreach (var rowElement in TableRowElements(table))
            {
                var cellElements = rowElement.Elements()
                    .Where(child => (child.Name.LocalName is "td" or "th") && !IsDisplayNone(child))
                    .ToList();
                if (cellElements.Count == 0)
                {
                    continue;
                }

                var cells = new List<ContentTableCell>(cellElements.Count);
                foreach (var cellElement in cellElements)
                {
                    var isHeader = cellElement.Name.LocalName == "th";
                    var hints = ResolveHints(cellElement);
                    var cellContext = ctx.ForTableCell(hints, isHeader);
                    TrackId(cellElement);
                    WalkInlineContent(cellElement, cellContext);
                    var items = _pending.ToList();
                    _pending.Clear();
                    var anyDeclaredBorder = !float.IsNaN(hints.BorderTopEm)
                        || !float.IsNaN(hints.BorderRightEm)
                        || !float.IsNaN(hints.BorderBottomEm)
                        || !float.IsNaN(hints.BorderLeftEm);
                    cells.Add(new ContentTableCell
                    {
                        Items = items,
                        ColSpan = ParseColumnSpan(cellElement),
                        RowSpan = ParseRowSpan(cellElement),
                        Header = isHeader,
                        BorderTopEm = CellBorder(hints.BorderTopEm, anyDeclaredBorder, defaultBorder),
                        BorderRightEm = CellBorder(hints.BorderRightEm, anyDeclaredBorder, defaultBorder),
                        BorderBottomEm = CellBorder(hints.BorderBottomEm, anyDeclaredBorder, defaultBorder),
                        BorderLeftEm = CellBorder(hints.BorderLeftEm, anyDeclaredBorder, defaultBorder),
                    });
                }

                rows.Add(new ContentTableRow
                {
                    Cells = cells,
                    Header = rowElement.Ancestors()
                        .Any(ancestor => ancestor.Name.LocalName == "thead"),
                });
            }
        }
        finally
        {
            _buildingTableCells = wasBuildingCells;
        }

        if (rows.Count == 0)
        {
            return;
        }

        // Vertical typesetting and table-less hosts read this flattened copy.
        // Breaks keep cells from running together; offsets stay untouched.
        var fallback = new List<InlineItem>();
        foreach (var row in rows)
        {
            if (fallback.Count > 0)
            {
                fallback.Add(new InlineItem
                {
                    Kind = InlineKind.LineBreak,
                    TextStart = -1,
                    Style = ctx.BaseStyle,
                });
            }

            var firstCell = true;
            foreach (var cell in row.Cells)
            {
                if (!firstCell)
                {
                    // A forced break keeps each cell's text offsets in its own
                    // run; a synthetic space would start a run at offset -1
                    // and make the following real text unselectable.
                    fallback.Add(new InlineItem
                    {
                        Kind = InlineKind.LineBreak,
                        TextStart = -1,
                        Style = ctx.BaseStyle,
                    });
                }

                firstCell = false;
                fallback.AddRange(cell.Items);
            }
        }

        _blocks.Add(new ContentBlock
        {
            Kind = BlockKind.Table,
            ElementId = Id(table),
            Style = ctx.BaseStyle,
            Justify = ctx.Justify,
            SpaceBeforeLines = 0.5f,
            SpaceAfterLines = 0.5f,
            Table = new ContentTable { Rows = rows },
            Items = fallback,
            FragmentIds = TakePendingFragmentIds(),
        });
    }

    private IEnumerable<XElement> TableRowElements(XElement table)
    {
        foreach (var child in table.Elements())
        {
            if (IsDisplayNone(child)) continue;
            var local = child.Name.LocalName;
            if (local == "tr")
            {
                yield return child;
            }
            else if (local is "thead" or "tbody" or "tfoot")
            {
                foreach (var row in child.Elements())
                {
                    if (row.Name.LocalName == "tr" && !IsDisplayNone(row))
                    {
                        yield return row;
                    }
                }
            }
        }
    }

    private static int ParseColumnSpan(XElement cell) =>
        int.TryParse((string?)cell.Attribute("colspan"), out var span)
            ? Math.Clamp(span, 1, 12)
            : 1;

    private static int ParseRowSpan(XElement cell)
    {
        if (!int.TryParse((string?)cell.Attribute("rowspan"), out var span) || span < 1)
        {
            return 1;
        }

        return Math.Clamp(span, 1, 64);
    }

    private static float ParseTableBorderAttribute(XElement table)
    {
        var attribute = table.Attribute("border");
        if (attribute is null)
        {
            return 0f;
        }

        var value = attribute.Value.Trim();
        if (value.Length == 0)
        {
            return 1f / 16f;
        }

        return int.TryParse(value, out var width) && width > 0 ? 1f / 16f : 0f;
    }

    private static float CellBorder(float hint, bool anyDeclared, float defaultBorder)
    {
        if (!float.IsNaN(hint))
        {
            return hint;
        }

        return anyDeclared ? 0f : defaultBorder;
    }
}
