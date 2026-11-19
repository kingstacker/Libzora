#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Vertical edge diagnostics.
/// </summary>
public partial class MainWindow
{
    private async Task<JsonElement> ReadKreaderVerticalEdgeDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const root = document.documentElement;
              const body = document.body;
              const el = document.scrollingElement || root;
              if (!root || !body || !el) return null;
              const bodyStyle = getComputedStyle(body);
              const rootStyle = getComputedStyle(root);
              const viewport = el.clientWidth || root.clientWidth || window.innerWidth || 0;
              const pageStep = parseFloat(rootStyle.getPropertyValue('--libzora-vertical-page-step')) || 0;
              const originShift = parseFloat(rootStyle.getPropertyValue('--libzora-vertical-origin-shift')) || 0;
              const contentShift = parseFloat(rootStyle.getPropertyValue('--libzora-vertical-content-shift')) || 0;
              const trailingExtent = parseFloat(
                rootStyle.getPropertyValue('--libzora-vertical-trailing-extent')) || 0;
              const leftSide = Math.max(0, (parseFloat(bodyStyle.paddingLeft) || 0) - trailingExtent);
              const rightSide = parseFloat(bodyStyle.paddingRight) || 0;
              const baseSide = (leftSide + rightSide) / 2;
              const baseLeft = viewport - pageStep - baseSide;
              const baseRight = viewport - baseSide;
              const nominalSafeLeft = baseLeft + originShift;
              const nominalSafeRight = baseRight + originShift;
              const parsedSafeLeft = parseFloat(
                rootStyle.getPropertyValue('--libzora-vertical-safe-left'));
              const parsedSafeRight = parseFloat(
                rootStyle.getPropertyValue('--libzora-vertical-safe-right'));
              const safeLeft = Number.isFinite(parsedSafeLeft)
                ? Math.max(nominalSafeLeft, parsedSafeLeft)
                : nominalSafeLeft;
              const safeRight = Number.isFinite(parsedSafeRight)
                ? Math.min(nominalSafeRight, parsedSafeRight)
                : nominalSafeRight;
              const tolerance = 0.75;
              // The usable column band along the inline (vertical) axis. In
              // vertical-rl every column starts at the top of this band and
              // grows downward; anything painted outside it either overflows
              // an atomic run past the page or has been pushed to the bottom
              // by an rtl inline direction.
              const contentTop = parseFloat(bodyStyle.paddingTop) || 0;
              const contentBottom = (el.clientHeight || 0) - (parseFloat(bodyStyle.paddingBottom) || 0);
              const bodyFontSize = parseFloat(bodyStyle.fontSize) || 16;
              const bandTolerance = 2;
              const partialGlyphs = [];
              const visibleMedia = [];
              const overflowGlyphs = [];
              const columnBands = [];
              // Glyphs are grouped into columns by proximity, not by exact
              // left: an upright digit or ASCII punctuation cell centers its
              // ink geometrically (`libzora-cell-inner`), shifting the glyph
              // rect a few pixels off the host column edge. Grouping by raw
              // rect.left turned every such cell into a phantom one-glyph
              // "column", and a full column that simply ended on such a cell
              // was misread as a bottom-aligned line.
              const columnMergeTolerance = Math.min(bodyFontSize * 0.75, 24);
              let glyphCount = 0;
              let overflowTopCount = 0;
              let overflowBottomCount = 0;
              let hangingPunctuationCount = 0;
              const hangingGlyphs = [];
              let inspectedCharacters = 0;
              let minGlyphLeft = Number.POSITIVE_INFINITY;
              let maxGlyphRight = Number.NEGATIVE_INFINITY;
              const columnLefts = [];
              const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const parent = node.parentElement;
                if (!parent || parent.closest('#libzora-selection-bar, script, style, noscript')) continue;
                const text = node.nodeValue || '';
                const nodeRange = document.createRange();
                nodeRange.selectNodeContents(node);
                const nodeIsVisible = Array.from(nodeRange.getClientRects()).some(rect =>
                  rect.bottom > 0 && rect.top < el.clientHeight
                  && rect.right > safeLeft - 48 && rect.left < safeRight + 48);
                if (!nodeIsVisible) continue;
                for (let index = 0; index < text.length && inspectedCharacters < 12000; index++) {
                  inspectedCharacters++;
                  if (/\s/.test(text[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  for (const rect of range.getClientRects()) {
                    if (rect.bottom <= 0 || rect.top >= el.clientHeight) continue;
                    if (rect.right <= safeLeft + tolerance || rect.left >= safeRight - tolerance) continue;
                    glyphCount++;
                    minGlyphLeft = Math.min(minGlyphLeft, rect.left);
                    maxGlyphRight = Math.max(maxGlyphRight, rect.right);
                    let band = null;
                    let bestDistance = Number.POSITIVE_INFINITY;
                    for (const candidate of columnBands) {
                      const distance = Math.abs(candidate.left - rect.left);
                      if (distance < bestDistance) {
                        bestDistance = distance;
                        band = candidate;
                      }
                    }
                    if (!band || bestDistance > columnMergeTolerance) {
                      band = { left: rect.left, top: rect.top, bottom: rect.bottom };
                      columnBands.push(band);
                      columnLefts.push(band.left);
                    } else {
                      band.top = Math.min(band.top, rect.top);
                      band.bottom = Math.max(band.bottom, rect.bottom);
                    }
                    // Kinsoku lets a column's final closing mark (or an
                    // interval dot at the top edge) hang into the page margin
                    // instead of starting the next column. That overhang is
                    // correct vertical typography — the continuous-flow model
                    // deliberately kept those glyphs visible — so classify a
                    // small punctuation overhang as a hang, not an overflow.
                    // Letters, digits and deep non-punctuation intrusions are
                    // still real overflows: an atomic sideways run escaped
                    // the page band.
                    const hangAllowance = Math.max(6, bodyFontSize * 1.3);
                    const isHangPunctuation = character =>
                      /[.,:;!?、。，．：；？！…—―·‥〉》」』）〕］｝〟’”~～﹀﹂﹄〕〗〙〛@#%&*_+=]/.test(character);
                    if (rect.top < contentTop - bandTolerance
                        || rect.bottom > contentBottom + bandTolerance) {
                      const hangsTop = rect.top < contentTop - bandTolerance
                        && contentTop - rect.top <= hangAllowance
                        && isHangPunctuation(text[index]);
                      const hangsBottom = rect.bottom > contentBottom + bandTolerance
                        && rect.bottom - contentBottom <= hangAllowance
                        && isHangPunctuation(text[index]);
                      if (rect.top < contentTop - bandTolerance) {
                        if (hangsTop) hangingPunctuationCount++; else overflowTopCount++;
                      }
                      if (rect.bottom > contentBottom + bandTolerance) {
                        if (hangsBottom) hangingPunctuationCount++; else overflowBottomCount++;
                      }
                      if (!hangsTop && !hangsBottom && overflowGlyphs.length < 12) {
                        overflowGlyphs.push({
                          glyph: text[index],
                          left: rect.left,
                          right: rect.right,
                          top: rect.top,
                          bottom: rect.bottom
                        });
                      }
                      if ((hangsTop || hangsBottom) && hangingGlyphs.length < 12) {
                        hangingGlyphs.push({
                          glyph: text[index],
                          top: rect.top,
                          bottom: rect.bottom
                        });
                      }
                    }
                    if (rect.left < safeLeft - tolerance || rect.right > safeRight + tolerance) {
                      partialGlyphs.push({
                        glyph: text[index],
                        left: rect.left,
                        right: rect.right,
                        top: rect.top,
                        bottom: rect.bottom
                      });
                    }
                  }
                }
                if (inspectedCharacters >= 12000) break;
              }
              for (const media of body.querySelectorAll('img, svg, canvas, video, table')) {
                const rect = media.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) continue;
                if (rect.bottom <= 0 || rect.top >= el.clientHeight) continue;
                if (rect.right <= safeLeft + tolerance || rect.left >= safeRight - tolerance) continue;
                visibleMedia.push({
                  tag: media.tagName,
                  left: rect.left,
                  right: rect.right,
                  top: rect.top,
                  bottom: rect.bottom
                });
              }
              // A column whose text starts well below the top of the band and
              // ends flush against its bottom is the signature of a bottom
              // aligned inline direction (`direction: rtl` under vertical-rl).
              // Columns that merely share space with an image are excluded:
              // there the text legitimately starts below the picture.
              let bottomAlignedColumnCount = 0;
              const bottomAlignedColumns = [];
              for (const band of columnBands) {
                if (band.top <= contentTop + bodyFontSize * 3) continue;
                if (band.bottom < contentBottom - bodyFontSize * 0.5) continue;
                if (visibleMedia.some(item =>
                    item.right > band.left - 1 && item.left < band.left + bodyFontSize * 2)) continue;
                bottomAlignedColumnCount++;
                if (bottomAlignedColumns.length < 8)
                  bottomAlignedColumns.push({ left: band.left, top: band.top, bottom: band.bottom });
              }
              // Reading direction is not a geometric detail: under vertical-rl
              // `direction` selects the inline axis, so rtl silently bottom
              // aligns every partially filled line. Report the computed value
              // of the body and of a real text block rather than trusting the
              // stylesheet to still say ltr.
              const textBlock = Array.from(body.querySelectorAll('p, li, blockquote, div'))
                .find(node => (node.textContent || '').trim().length > 0);
              const blockDirection = textBlock ? getComputedStyle(textBlock).direction : '';
              return JSON.stringify({
                viewport,
                scrollLeft: el.scrollLeft || 0,
                scrollWidth: el.scrollWidth || 0,
                pageStep,
                originShift,
                contentShift,
                safeLeft,
                safeRight,
                nominalSafeLeft,
                nominalSafeRight,
                leftMargin: safeLeft,
                rightMargin: viewport - safeRight,
                marginDelta: Math.abs(nominalSafeLeft - (viewport - nominalSafeRight)),
                maskMarginDelta: Math.abs(safeLeft - (viewport - safeRight)),
                lineHeight: parseFloat(bodyStyle.lineHeight) || 0,
                paddingLeft: parseFloat(bodyStyle.paddingLeft) || 0,
                paddingRight: parseFloat(bodyStyle.paddingRight) || 0,
                pageIndex: pageStep > 0 ? Math.round(Math.abs(el.scrollLeft || 0) / pageStep) : 0,
                minGlyphLeft: Number.isFinite(minGlyphLeft) ? minGlyphLeft : null,
                maxGlyphRight: Number.isFinite(maxGlyphRight) ? maxGlyphRight : null,
                columnCount: columnLefts.length,
                columnLefts: columnLefts.sort((a, b) => a - b).slice(0, 80),
                glyphCount,
                visibleMediaCount: visibleMedia.length,
                visibleMedia: visibleMedia.slice(0, 12),
                contentTop,
                contentBottom,
                direction: bodyStyle.direction || '',
                blockDirection,
                overflowTopCount,
                overflowBottomCount,
                hangingPunctuationCount,
                hangingGlyphs,
                overflowGlyphs,
                bottomAlignedColumnCount,
                bottomAlignedColumns,
                inspectedCharacters,
                partialGlyphCount: partialGlyphs.length,
                partialGlyphs: partialGlyphs.slice(0, 12)
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader vertical edge diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
#endif
