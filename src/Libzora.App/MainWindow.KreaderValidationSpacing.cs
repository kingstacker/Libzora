#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Vertical fixture spacing diagnostics.
/// </summary>
public partial class MainWindow
{
    private async Task<JsonElement> ReadVerticalFixtureSpacingAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync("""
            (() => {
              const sample = document.querySelector('.vertical-fixture-sample');
              if (!sample) return null;
              const items = [];
              const walker = document.createTreeWalker(sample, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const text = node.nodeValue || '';
                if (node.parentElement?.closest(
                    '.libzora-linux-vertical-single, '
                      + '.libzora-linux-vertical-single-punctuation, '
                      + '.libzora-linux-vertical-tcy, '
                      + '.libzora-linux-vertical-number, '
                      + '.libzora-linux-vertical-cjk, '
                      + '.libzora-linux-vertical-pair-punctuation, '
                      + '.libzora-linux-vertical-footnote'))
                  continue;
                for (let index = 0; index < text.length; index++) {
                  if (/\s/.test(text[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (rect.width <= 0 || rect.height <= 0) continue;
                  items.push({ text: text[index], top: rect.top, bottom: rect.bottom,
                    left: rect.left, right: rect.right });
                }
              }
              let adjacentOverlapCount = 0;
              let maxOverlap = 0;
              for (let index = 1; index < items.length; index++) {
                const previous = items[index - 1];
                const current = items[index];
                const previousCenter = (previous.left + previous.right) / 2;
                const currentCenter = (current.left + current.right) / 2;
                if (Math.abs(previousCenter - currentCenter) > 2) continue;
                const overlap = previous.bottom - current.top;
                if (overlap <= 1.05) continue;
                adjacentOverlapCount++;
                maxOverlap = Math.max(maxOverlap, overlap);
              }
              const alignmentItems = [];
              const alignmentWalker = document.createTreeWalker(
                sample,
                NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
              const generatedAlignmentSelector = [
                '.libzora-linux-vertical-single',
                '.libzora-linux-vertical-single-punctuation',
                '.libzora-linux-vertical-tcy',
                '.libzora-linux-vertical-number',
                '.libzora-linux-vertical-cjk',
                '.libzora-linux-vertical-pair-punctuation'
              ].join(',');
              const alignmentCategory = character => {
                if (/[A-Za-z]/.test(character)) return 'latin';
                if (/[0-9]/.test(character)) return 'digit';
                if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】〔〕［］｛｝…—]/.test(character))
                  return 'punctuation';
                if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
                return 'other';
              };
              for (let node = alignmentWalker.nextNode(); node; node = alignmentWalker.nextNode()) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  if (!node.matches?.(generatedAlignmentSelector)) continue;
                  const rect = node.getBoundingClientRect();
                  if (rect.width > 0 && rect.height > 0)
                    alignmentItems.push({
                      category: node.matches(
                        '.libzora-linux-vertical-single-punctuation, '
                          + '.libzora-linux-vertical-pair-punctuation')
                        ? 'punctuation'
                        : node.matches('.libzora-linux-vertical-cjk')
                          ? 'cjk' : 'digit',
                      text: node.textContent || '',
                      rect
                    });
                  continue;
                }
                const parent = node.parentElement;
                if (!parent || parent.closest(
                    generatedAlignmentSelector + ', .libzora-linux-vertical-footnote'))
                  continue;
                const value = node.nodeValue || '';
                for (let index = 0; index < value.length; index++) {
                  if (/\s/.test(value[index])) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (rect.width > 0 && rect.height > 0)
                    alignmentItems.push({
                      category: alignmentCategory(value[index]),
                      text: value[index],
                      rect
                    });
                }
              }
              let verticalCenterErrorCount = 0;
              let maxVerticalCenterDelta = 0;
              const verticalCenterSamples = [];
              for (let index = 1; index < alignmentItems.length; index++) {
                const previous = alignmentItems[index - 1];
                const current = alignmentItems[index];
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                const sameColumn = Math.abs(previousCenter - currentCenter)
                  <= Math.max(8, previous.rect.width, current.rect.width) * 0.75;
                if (!sameColumn) continue;
                const delta = Math.abs(previousCenter - currentCenter);
                maxVerticalCenterDelta = Math.max(maxVerticalCenterDelta, delta);
                if (delta <= 1.1) continue;
                verticalCenterErrorCount++;
                if (verticalCenterSamples.length < 12) {
                  verticalCenterSamples.push({
                    previous: previous.text,
                    current: current.text,
                    delta: +delta.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              let cellOverlapCount = 0;
              let maxCellOverlap = 0;
              const cellOverlapItems = [];
              for (let index = 1; index < alignmentItems.length; index++) {
                const previous = alignmentItems[index - 1];
                const current = alignmentItems[index];
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                if (Math.abs(previousCenter - currentCenter) > 1.1) continue;
                const overlap = previous.rect.bottom - current.rect.top;
                if (overlap <= 1.05) continue;
                cellOverlapCount++;
                maxCellOverlap = Math.max(maxCellOverlap, overlap);
                if (cellOverlapItems.length < 12) {
                  cellOverlapItems.push({
                    previous: previous.text,
                    current: current.text,
                    overlap: +overlap.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              const numberBoundaryGaps = [];
              for (const run of sample.querySelectorAll('.libzora-cjk-before-number')) {
                const previous = run.previousSibling;
                if (!previous) continue;
                let previousText = '';
                let previousRect = null;
                if (previous.nodeType === Node.TEXT_NODE) {
                  previousText = previous.nodeValue || '';
                  let index = previousText.length - 1;
                  while (index >= 0 && /\s/.test(previousText[index])) index--;
                  if (index < 0) continue;
                  const range = document.createRange();
                  range.setStart(previous, index);
                  range.setEnd(previous, index + 1);
                  previousRect = range.getBoundingClientRect();
                  previousText = previousText[index];
                } else if (previous.matches?.('.libzora-linux-vertical-cjk')) {
                  previousText = previous.textContent || '';
                  previousRect = previous.getBoundingClientRect();
                }
                if (!previousRect || !previousText) continue;
                const runRect = run.getBoundingClientRect();
                const previousCenter = (previousRect.left + previousRect.right) / 2;
                const runCenter = (runRect.left + runRect.right) / 2;
                // An orthogonal horizontal-tb compatibility cell can have a
                // different physical X origin from the native Han Range even
                // when it occupies the same vertical column. Compare using a
                // fraction of the cell width, not a hard two-pixel cutoff.
                const sameColumnTolerance = Math.max(
                  2,
                  parseFloat(getComputedStyle(run).fontSize || '0') * 0.75,
                  Math.max(previousRect.width, runRect.width) * 0.75);
                if (Math.abs(previousCenter - runCenter) > sameColumnTolerance) continue;
                numberBoundaryGaps.push({
                  boundary: previousText + '->' + (run.textContent || ''),
                  gap: +(runRect.top - previousRect.bottom).toFixed(2)
                });
              }
              const text = sample.textContent || '';
              const footnote = sample.querySelector('.libzora-linux-vertical-footnote');
              const footnoteStyle = footnote ? getComputedStyle(footnote) : null;
              const bodyStyle = getComputedStyle(document.body);
              const bodyFontSize = parseFloat(bodyStyle.fontSize) || 0;
              const footnoteRect = footnote?.getBoundingClientRect();
              const footnoteInner = footnote?.querySelector(
                ':scope > .libzora-linux-vertical-footnote-inner');
              let footnoteInkRect = null;
              if (footnoteInner) {
                const textWalker = document.createTreeWalker(
                  footnoteInner, NodeFilter.SHOW_TEXT);
                for (let node = textWalker.nextNode(); node; node = textWalker.nextNode()) {
                  if (!(node.nodeValue || '').trim()) continue;
                  const range = document.createRange();
                  range.selectNodeContents(node);
                  footnoteInkRect = range.getBoundingClientRect();
                  break;
                }
              }
              const footnoteCenterDelta = footnoteRect && footnoteInkRect
                ? Math.max(
                    Math.abs((footnoteRect.left + footnoteRect.width / 2)
                      - (footnoteInkRect.left + footnoteInkRect.width / 2)),
                    Math.abs((footnoteRect.top + footnoteRect.height / 2)
                      - (footnoteInkRect.top + footnoteInkRect.height / 2)))
                : Number.POSITIVE_INFINITY;
              const pairOpen = Array.from(sample.querySelectorAll('.libzora-linux-vertical-pair-open'));
              const pairClose = Array.from(sample.querySelectorAll('.libzora-linux-vertical-pair-close'));
              const readCellGeometry = span => {
                const outer = span.getBoundingClientRect();
                const inner = span.querySelector(':scope > .libzora-linux-vertical-cell-inner');
                const glyph = inner?.querySelector(':scope > .libzora-linux-vertical-glyph');
                // Mapped punctuation is painted by ::before. The original
                // text node remains transparent and WebKit reports its
                // untransformed range, so using that range would measure a
                // phantom glyph outside the visible centred cell.
                if (inner?.dataset.libzoraVerticalGlyph)
                  return { outer, inner: inner.getBoundingClientRect(), glyph: inner.getBoundingClientRect() };
                // For a real generated glyph, measure the element rather than
                // a Range over its text node. WebKitGTK can leave a Range in
                // the pre-transform coordinate space when the parent cell is
                // rotated (vertical ellipsis/dash) or relatively shifted.
                if (inner && glyph)
                  return { outer, inner: inner.getBoundingClientRect(), glyph: glyph.getBoundingClientRect() };
                const node = glyph?.firstChild || inner?.firstChild || span.firstChild;
                if (!node || node.nodeType !== Node.TEXT_NODE)
                  return { outer, inner: inner?.getBoundingClientRect() || null, glyph: null };
                const range = document.createRange();
                range.selectNodeContents(node);
                return {
                  outer,
                  inner: inner?.getBoundingClientRect() || null,
                  glyph: range.getBoundingClientRect()
                };
              };
              const openGeometry = pairOpen[0] ? readCellGeometry(pairOpen[0]) : null;
              const closeGeometry = pairClose[0] ? readCellGeometry(pairClose[0]) : null;
              const pairOpenStyle = pairOpen[0] ? getComputedStyle(pairOpen[0]) : null;
              const pairCloseStyle = pairClose[0] ? getComputedStyle(pairClose[0]) : null;
              const pairOpenLeftInset = openGeometry?.glyph
                ? openGeometry.glyph.left - openGeometry.outer.left : Number.POSITIVE_INFINITY;
              const pairCloseRightInset = closeGeometry?.glyph
                ? closeGeometry.outer.right - closeGeometry.glyph.right : Number.POSITIVE_INFINITY;
              const pairOpenTopInset = openGeometry?.glyph
                ? openGeometry.glyph.top - openGeometry.outer.top : Number.NEGATIVE_INFINITY;
              const pairOpenBottomInset = openGeometry?.glyph
                ? openGeometry.outer.bottom - openGeometry.glyph.bottom : Number.NEGATIVE_INFINITY;
              const cellCenterDelta = geometry => geometry?.inner
                ? Math.max(
                    Math.abs((geometry.outer.left + geometry.outer.width / 2)
                      - (geometry.inner.left + geometry.inner.width / 2)),
                    Math.abs((geometry.outer.top + geometry.outer.height / 2)
                      - (geometry.inner.top + geometry.inner.height / 2)))
                : Number.POSITIVE_INFINITY;
              const singlePunctuation = Array.from(
                sample.querySelectorAll('.libzora-linux-vertical-single-punctuation'));
              let singlePunctuationStyleErrorCount = 0;
              let singlePunctuationCenterErrorCount = 0;
              for (const span of singlePunctuation) {
                const style = getComputedStyle(span);
                // The cell shares the parent's vertical-rl flow (the
                // orthogonal horizontal-tb cells collapsed WebKitGTK line
                // boxes); center alignment along both axes is what matters.
                if (style.display !== 'inline-grid'
                    || style.writingMode !== 'vertical-rl'
                    || style.alignItems !== 'center'
                    || style.justifyItems !== 'center')
                  singlePunctuationStyleErrorCount++;
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  singlePunctuationCenterErrorCount++;
                  continue;
                }
                const cellCenterX = geometry.outer.left + geometry.outer.width / 2;
                const cellCenterY = geometry.outer.top + geometry.outer.height / 2;
                const glyphCenterX = centeredBox.left + centeredBox.width / 2;
                const glyphCenterY = centeredBox.top + centeredBox.height / 2;
                if (Math.max(
                    Math.abs(cellCenterX - glyphCenterX),
                    Math.abs(cellCenterY - glyphCenterY)) > 1.1)
                  singlePunctuationCenterErrorCount++;
              }
              const verticalCenteredMarks = Array.from(
                sample.querySelectorAll('.libzora-linux-vertical-centered-mark'));
              let verticalCenteredMarkRotationErrorCount = 0;
              let verticalCenteredMarkCenterErrorCount = 0;
              for (const span of verticalCenteredMarks) {
                const inner = span.querySelector(':scope > .libzora-linux-vertical-cell-inner');
                const matrix = new DOMMatrix(getComputedStyle(inner).transform);
                if (Math.abs(matrix.b) < 0.8 || Math.abs(matrix.c) < 0.8)
                  verticalCenteredMarkRotationErrorCount++;
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  verticalCenteredMarkCenterErrorCount++;
                  continue;
                }
                const dx = Math.abs(
                  geometry.outer.left + geometry.outer.width / 2
                  - centeredBox.left - centeredBox.width / 2);
                const dy = Math.abs(
                  geometry.outer.top + geometry.outer.height / 2
                  - centeredBox.top - centeredBox.height / 2);
                if (Math.max(dx, dy) > 1.1)
                  verticalCenteredMarkCenterErrorCount++;
              }
              const singleDigits = Array.from(
                sample.querySelectorAll('.libzora-linux-vertical-single'));
              let singleDigitCenterErrorCount = 0;
              for (const span of singleDigits) {
                const geometry = readCellGeometry(span);
                const centeredBox = geometry.inner || geometry.glyph;
                if (!centeredBox) {
                  singleDigitCenterErrorCount++;
                  continue;
                }
                const cellCenterX = geometry.outer.left + geometry.outer.width / 2;
                const cellCenterY = geometry.outer.top + geometry.outer.height / 2;
                const glyphCenterX = centeredBox.left + centeredBox.width / 2;
                const glyphCenterY = centeredBox.top + centeredBox.height / 2;
                if (Math.max(
                    Math.abs(cellCenterX - glyphCenterX),
                    Math.abs(cellCenterY - glyphCenterY)) > 1.1)
                  singleDigitCenterErrorCount++;
              }
              const cjkCells = Array.from(
                sample.querySelectorAll('.libzora-linux-vertical-cjk'));
              let cjkCellSizeErrorCount = 0;
              let cjkGlyphCenterErrorCount = 0;
              let cjkMaxGlyphCenterDelta = 0;
              for (const cell of cjkCells) {
                const outer = cell.getBoundingClientRect();
                const style = getComputedStyle(cell);
                const fontSize = parseFloat(style.fontSize) || 0;
                if (!(fontSize > 0)
                    || Math.abs(outer.width - fontSize) > 1.1
                    || Math.abs(outer.height - fontSize) > 1.1)
                  cjkCellSizeErrorCount++;
                const glyph = cell.querySelector(
                  ':scope > .libzora-linux-vertical-cjk-ink > .libzora-linux-vertical-glyph');
                if (!glyph) {
                  cjkGlyphCenterErrorCount++;
                  continue;
                }
                const ink = glyph.getBoundingClientRect();
                const delta = Math.max(
                  Math.abs(outer.left + outer.width / 2 - ink.left - ink.width / 2),
                  Math.abs(outer.top + outer.height / 2 - ink.top - ink.height / 2));
                cjkMaxGlyphCenterDelta = Math.max(cjkMaxGlyphCenterDelta, delta);
                if (delta > 1.1) cjkGlyphCenterErrorCount++;
              }
              const singlePunctuationSample = singlePunctuation[0]
                ? readCellGeometry(singlePunctuation[0]) : null;
              const singleDigitSample = singleDigits[0]
                ? readCellGeometry(singleDigits[0]) : null;
              return JSON.stringify({
                adjacentOverlapCount,
                maxOverlap: +maxOverlap.toFixed(2),
                verticalCenterErrorCount,
                maxVerticalCenterDelta: +maxVerticalCenterDelta.toFixed(2),
                verticalCenterSamples,
                cellOverlapCount,
                maxCellOverlap: +maxCellOverlap.toFixed(2),
                cellOverlapItems,
                numberBoundaryGaps,
                footnoteFound: !!footnote,
                footnoteText: footnote?.textContent || '',
                footnoteWidth: +(footnoteRect?.width || 0).toFixed(2),
                footnoteHeight: +(footnoteRect?.height || 0).toFixed(2),
                footnoteFontSize: +(parseFloat(footnoteStyle?.fontSize || '0') || 0).toFixed(2),
                footnoteInlineAdvance: +bodyFontSize.toFixed(2),
                footnoteCenterDelta: +footnoteCenterDelta.toFixed(2),
                footnoteOuter: footnoteRect
                  ? [footnoteRect.left, footnoteRect.top, footnoteRect.width, footnoteRect.height].map(v => +v.toFixed(2)) : null,
                footnoteInk: footnoteInkRect
                  ? [footnoteInkRect.left, footnoteInkRect.top, footnoteInkRect.width, footnoteInkRect.height].map(v => +v.toFixed(2)) : null,
                pairOpenCount: pairOpen.length,
                pairCloseCount: pairClose.length,
                pairOpenJustifyItems: pairOpenStyle?.justifyItems || '',
                pairCloseJustifyItems: pairCloseStyle?.justifyItems || '',
                pairOpenLeftInset: +pairOpenLeftInset.toFixed(2),
                pairCloseRightInset: +pairCloseRightInset.toFixed(2),
                pairOpenTopInset: +pairOpenTopInset.toFixed(2),
                pairOpenBottomInset: +pairOpenBottomInset.toFixed(2),
                pairOpenCenterDelta: +cellCenterDelta(openGeometry).toFixed(2),
                pairCloseCenterDelta: +cellCenterDelta(closeGeometry).toFixed(2),
                pairOpenOuter: openGeometry?.outer
                  ? [openGeometry.outer.left, openGeometry.outer.top, openGeometry.outer.width, openGeometry.outer.height].map(v => +v.toFixed(2)) : null,
                pairOpenGlyph: openGeometry?.glyph
                  ? [openGeometry.glyph.left, openGeometry.glyph.top, openGeometry.glyph.width, openGeometry.glyph.height].map(v => +v.toFixed(2)) : null,
                pairOpenWritingMode: pairOpenStyle?.writingMode || '',
                singlePunctuationOuter: singlePunctuationSample?.outer
                  ? [singlePunctuationSample.outer.left, singlePunctuationSample.outer.top, singlePunctuationSample.outer.width, singlePunctuationSample.outer.height].map(v => +v.toFixed(2)) : null,
                singlePunctuationGlyph: singlePunctuationSample?.glyph
                  ? [singlePunctuationSample.glyph.left, singlePunctuationSample.glyph.top, singlePunctuationSample.glyph.width, singlePunctuationSample.glyph.height].map(v => +v.toFixed(2)) : null,
                singleDigitOuter: singleDigitSample?.outer
                  ? [singleDigitSample.outer.left, singleDigitSample.outer.top, singleDigitSample.outer.width, singleDigitSample.outer.height].map(v => +v.toFixed(2)) : null,
                singleDigitGlyph: singleDigitSample?.glyph
                  ? [singleDigitSample.glyph.left, singleDigitSample.glyph.top, singleDigitSample.glyph.width, singleDigitSample.glyph.height].map(v => +v.toFixed(2)) : null,
                singlePunctuationCount: singlePunctuation.length,
                singlePunctuationStyleErrorCount,
                singlePunctuationCenterErrorCount,
                verticalCenteredMarkCount: verticalCenteredMarks.length,
                verticalCenteredMarkRotationErrorCount,
                verticalCenteredMarkCenterErrorCount,
                singleDigitCenterErrorCount,
                cjkCellCount: cjkCells.length,
                cjkCellSizeErrorCount,
                cjkGlyphCenterErrorCount,
                cjkMaxGlyphCenterDelta: +cjkMaxGlyphCenterDelta.toFixed(2),
                dnaFound: text.includes('DNA'),
                fpgaFound: text.includes('FPGA')
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        Require(!string.IsNullOrWhiteSpace(raw), "vertical fixture spacing probe returned no data");
        using var document = JsonDocument.Parse(raw!);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Walks consecutive real-book chapters in vertical mode and asserts the
    /// same no-clipping/margin invariants page by page, while tallying which
    /// vertical run classes (digits, tate-chu-yoko pairs, long numeric runs,
    /// Latin words, ASCII punctuation) and how much CJK text the sweep
    /// exercised. Real books open on arbitrary front matter, so this phase is
    /// what proves mixed-script body text renders on the standard grid.
    /// </summary>
}
#endif
