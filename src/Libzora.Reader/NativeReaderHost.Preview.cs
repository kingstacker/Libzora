using Libzora.Core;
using Libzora.Layout;
using SkiaSharp;

namespace Libzora;

/// <summary>
/// Figure and table tap targets plus the wide table snapshot shown by the
/// native reader viewer. The page itself keeps its normal text-sized layout;
/// the snapshot re-composes the same block on a wider canvas so phone readers
/// can inspect every column.
/// </summary>
public sealed partial class NativeReaderHost
{
    private readonly record struct TableTextRange(int Start, int End, int BlockIndex);

    private void BuildTableTextRanges(ChapterContent content)
    {
        _tableTextRanges.Clear();
        for (var index = 0; index < content.Blocks.Count; index++)
        {
            var block = content.Blocks[index];
            if (block.Kind != BlockKind.Table || block.Table is not { Rows.Count: > 0 })
            {
                continue;
            }

            var start = int.MaxValue;
            var end = -1;
            foreach (var item in block.Items)
            {
                if (item.TextStart < 0 || item.Text.Length == 0)
                {
                    continue;
                }

                start = Math.Min(start, item.TextStart);
                end = Math.Max(end, item.TextStart + item.Text.Length);
            }

            if (end > start)
            {
                _tableTextRanges.Add(new TableTextRange(start, end, index));
            }
        }
    }

    /// <summary>
    /// Emits the tap target under a page point. Images win over tables, and a
    /// table's painted segment wins over its text range; the text range keeps
    /// fallback tables (very wide or vertical) reachable on phones.
    /// </summary>
    private bool TryEmitReaderObject(int page, SKPoint point)
    {
        if (!ReaderObjectsEnabled || _layout is null || _content is null)
        {
            return false;
        }

        if (_layout.GetBlockImageAt(page, point) is { } image && File.Exists(image.Path))
        {
            Emit(new { type = "readerObject", kind = "image", path = image.Path });
            return true;
        }

        if (_layout.GetTableAt(page, point) is { } table
            && TryGetBlockIndex(table.Block, out var rectIndex))
        {
            Emit(new { type = "readerObject", kind = "table", blockIndex = rectIndex });
            return true;
        }

        var offset = _layout.HitTest(page, point);
        if (offset >= 0)
        {
            foreach (var range in _tableTextRanges)
            {
                if (offset < range.Start || offset >= range.End)
                {
                    continue;
                }

                Emit(new { type = "readerObject", kind = "table", blockIndex = range.BlockIndex });
                return true;
            }
        }

        return false;
    }

    private bool TryGetBlockIndex(ContentBlock block, out int index)
    {
        if (_content is { } content)
        {
            for (var i = 0; i < content.Blocks.Count; i++)
            {
                if (!ReferenceEquals(content.Blocks[i], block))
                {
                    continue;
                }

                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Re-composes one table block on a canvas wide enough for its real
    /// columns and writes a PNG the native viewer can zoom. The reader page
    /// itself stays paginated at its normal width.
    /// </summary>
    public Task<string?> RenderTablePreviewAsync(
        int blockIndex,
        double minimumWidth,
        CancellationToken cancellationToken = default)
    {
        var content = _content;
        var layout = _layout;
        if (content is null || layout is null
            || blockIndex < 0 || blockIndex >= content.Blocks.Count)
        {
            return Task.FromResult<string?>(null);
        }

        var block = content.Blocks[blockIndex];
        if (block.Kind != BlockKind.Table || block.Table is not { Rows.Count: > 0 })
        {
            return Task.FromResult<string?>(null);
        }

        var settings = _settings;
        var theme = _paintTheme;
        var fonts = FontLibrary;
        var capacity = block.Table.Rows
            .Select(row => row.Cells.Sum(cell => Math.Max(1, cell.ColSpan)))
            .DefaultIfEmpty(2)
            .Max();
        var neededWidth = layout.Options.BaseFontSize
            * (2.2f * capacity + 0.4f * Math.Max(0, capacity - 1)) + 96f;
        var width = Math.Clamp(
            Math.Max(Math.Max(480f, (float)minimumWidth), neededWidth), 480f, 4096f);

        return Task.Run(
            () => ReaderTablePreviewRenderer.Render(
                block, settings, width, theme, fonts, ResolveImage, cancellationToken),
            cancellationToken);
    }
}
