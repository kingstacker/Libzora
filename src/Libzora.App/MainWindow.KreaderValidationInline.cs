#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Native vertical inline diagnostics.
/// </summary>
public partial class MainWindow
{
    private async Task<JsonElement> ReadKreaderNativeVerticalInlineDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const body = document.body;
              if (!body) return null;
              const tcyRuns = Array.from(body.querySelectorAll(
                '.libzora-tcy[data-libzora-vertical-run="1"]'));
              const nativeDigitRuns = Array.from(body.querySelectorAll(
                '.libzora-native-vertical-digits[data-libzora-native-vertical-digits="1"]'));
              const nativeDigits = Array.from(body.querySelectorAll(
                '.libzora-native-vertical-digit[data-libzora-native-vertical-digit="1"]'));
              const nativeFootnotes = Array.from(body.querySelectorAll(
                '.libzora-native-vertical-footnote[data-libzora-native-vertical-footnote="1"]'));
              const linuxNumberRuns = Array.from(body.querySelectorAll(
                '.libzora-linux-vertical-number[data-libzora-linux-vertical-number="1"]'));
              const linuxSingles = Array.from(body.querySelectorAll(
                '.libzora-linux-vertical-single[data-libzora-linux-vertical-single="1"]'));
              const linuxTcyRuns = Array.from(body.querySelectorAll(
                '.libzora-linux-vertical-tcy[data-libzora-linux-vertical-tcy="1"]'));
              const cjkSeparatedNumbers = Array.from(body.querySelectorAll(
                '.libzora-cjk-before-number'));
              const legacySelector = [
                '.libzora-vertical-digit',
                '.libzora-vertical-number',
                '.libzora-vertical-latin',
                '.libzora-vertical-punctuation',
                '.libzora-cell-inner',
                '.libzora-tcy-inner'
              ].join(',');
              const legacy = Array.from(body.querySelectorAll(legacySelector));
              let linuxNumericStyleErrorCount = 0;
              let cjkNumberSpacingErrorCount = 0;
              let syntheticLinuxNumberLayoutCount = 0;
              for (const run of [...linuxSingles, ...linuxTcyRuns, ...linuxNumberRuns]) {
                const style = getComputedStyle(run);
                const isTcy = run.classList.contains('libzora-linux-vertical-tcy');
                const isSingle = run.classList.contains('libzora-linux-vertical-single');
                // Compatibility cells must share the parent's vertical-rl
                // flow. The historical horizontal-tb cells were orthogonal
                // atomic boxes and WebKitGTK sized their line box to the
                // 1em cell, collapsing the column pitch and clipping the
                // chapter — the geometric pitch guard in
                // RequireKreaderVerticalFlowInvariants watches for a
                // regression of that bug.
                if (style.writingMode !== 'vertical-rl')
                  linuxNumericStyleErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticLinuxNumberLayoutCount++;
                if (isSingle) {
                  const rect = run.getBoundingClientRect();
                  const fontSize = parseFloat(style.fontSize) || 0;
                  if (!(fontSize > 0)
                      || Math.abs(rect.width - fontSize) > 1.05
                      || Math.abs(rect.height - fontSize) > 1.05)
                  linuxNumericStyleErrorCount++;
                }
              }
              for (const run of cjkSeparatedNumbers) {
                const style = getComputedStyle(run);
                // Only the inline axis (top/bottom in vertical-rl) must stay
                // margin-free: a front or back gap at the Han→number boundary
                // moves the number off the shared vertical rhythm. The block
                // axis now carries the deliberate symmetric pitch margin that
                // keeps the cell's line-box extent on the paragraph grid.
                const margins = [
                  style.marginTop,
                  style.marginBottom,
                  style.marginInlineStart,
                  style.marginInlineEnd
                ].map(value => Math.abs(parseFloat(value) || 0));
                if (margins.some(margin => margin > 1.05))
                  cjkNumberSpacingErrorCount++;
              }
              let syntheticLayoutCount = 0;
              for (const node of legacy) {
                const style = getComputedStyle(node);
                if (style.display !== 'contents'
                    || style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticLayoutCount++;
              }

              let digitCellStyleErrorCount = 0;
              let digitCellSizeErrorCount = 0;
              let syntheticNativeDigitLayoutCount = 0;
              for (const digit of nativeDigits) {
                const style = getComputedStyle(digit);
                const rect = digit.getBoundingClientRect();
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.display !== 'inline-block'
                    || style.writingMode !== 'horizontal-tb')
                  digitCellStyleErrorCount++;
                if (!(fontSize > 0)
                    || Math.abs(rect.width - fontSize) > 1.05
                    || Math.abs(rect.height - fontSize) > 1.05)
                  digitCellSizeErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticNativeDigitLayoutCount++;
              }

              let digitOverlapCount = 0;
              let maxDigitOverlap = 0;
              for (const run of nativeDigitRuns) {
                const digits = Array.from(run.querySelectorAll(
                  ':scope > .libzora-native-vertical-digit'));
                for (let index = 1; index < digits.length; index++) {
                  const previous = digits[index - 1].getBoundingClientRect();
                  const current = digits[index].getBoundingClientRect();
                  const previousCenter = previous.left + previous.width / 2;
                  const currentCenter = current.left + current.width / 2;
                  const sameColumn = Math.abs(previousCenter - currentCenter)
                    <= Math.max(previous.width, current.width) * 0.75;
                  if (!sameColumn) continue;
                  const overlap = previous.bottom - current.top;
                  if (overlap <= 1.05) continue;
                  digitOverlapCount++;
                  maxDigitOverlap = Math.max(maxDigitOverlap, overlap);
                }
              }

              let footnoteCombineStyleErrorCount = 0;
              let syntheticFootnoteLayoutCount = 0;
              for (const footnote of nativeFootnotes) {
                const style = getComputedStyle(footnote);
                if (style.textCombineUpright !== 'all')
                  footnoteCombineStyleErrorCount++;
                if (style.position === 'absolute'
                    || (style.transform && style.transform !== 'none'))
                  syntheticFootnoteLayoutCount++;
              }
              syntheticNativeDigitLayoutCount += syntheticFootnoteLayoutCount;

              let tcyStyleErrorCount = 0;
              let minTcyInlineRatio = Number.POSITIVE_INFINITY;
              let maxTcyInlineRatio = 0;
              for (const run of tcyRuns) {
                const style = getComputedStyle(run);
                const rect = run.getBoundingClientRect();
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.textCombineUpright !== 'all') tcyStyleErrorCount++;
                if (fontSize > 0) {
                  const ratio = rect.height / fontSize;
                  minTcyInlineRatio = Math.min(minTcyInlineRatio, ratio);
                  maxTcyInlineRatio = Math.max(maxTcyInlineRatio, ratio);
                }
              }

              const units = [];
              const seenTcy = new Set();
              const generatedSelector = [
                '.libzora-linux-vertical-single',
                '.libzora-linux-vertical-single-punctuation',
                '.libzora-linux-vertical-tcy',
                '.libzora-linux-vertical-number',
                '.libzora-linux-vertical-cjk',
                '.libzora-linux-vertical-pair-punctuation'
              ].join(',');
              const walker = document.createTreeWalker(
                body,
                NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
              const classify = character => {
                if (/[A-Za-z]/.test(character)) return 'latin';
                if (/[0-9]/.test(character)) return 'digit';
                if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】…—]/.test(character))
                  return 'punctuation';
                if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
                return 'other';
              };
              let latinCount = 0;
              let digitCount = 0;
              let punctuationCount = 0;
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                  if (node.matches?.('.libzora-tcy[data-libzora-vertical-run="1"]')
                      && !seenTcy.has(node)) {
                    seenTcy.add(node);
                    const rect = node.getBoundingClientRect();
                    units.push({ category: 'digit', text: node.textContent || '', rect });
                    digitCount += (node.textContent || '').length;
                    continue;
                  }
                  if (node.matches?.(generatedSelector)) {
                    const rect = node.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) {
                      const text = node.textContent || '';
                      const category = node.matches(
                        '.libzora-linux-vertical-single-punctuation, '
                          + '.libzora-linux-vertical-pair-punctuation')
                        ? 'punctuation'
                        : node.matches('.libzora-linux-vertical-cjk')
                          ? 'cjk'
                        : 'digit';
                      units.push({ category, text, rect });
                      if (category === 'punctuation') punctuationCount++;
                      else digitCount += text.replace(/[^0-9]/g, '').length;
                    }
                    continue;
                  }
                  continue;
                }
                const parent = node.parentElement;
                if (!parent
                    || parent.closest('#libzora-selection-bar, script, style, noscript')
                    || parent.closest('.libzora-tcy[data-libzora-vertical-run="1"], '
                      + '.libzora-linux-vertical-footnote, ' + generatedSelector))
                  continue;
                const text = node.nodeValue || '';
                for (let index = 0; index < text.length; index++) {
                  const character = text[index];
                  if (/\s/.test(character)) continue;
                  const range = document.createRange();
                  range.setStart(node, index);
                  range.setEnd(node, index + 1);
                  const rect = range.getBoundingClientRect();
                  if (!(rect.width > 0) || !(rect.height > 0)) continue;
                  const category = classify(character);
                  if (category === 'latin') latinCount++;
                  else if (category === 'digit') digitCount++;
                  else if (category === 'punctuation') punctuationCount++;
                  units.push({ category, text: character, rect });
                }
              }

              let boundaryOverlapCount = 0;
              let maxBoundaryOverlap = 0;
              const overlapItems = [];
              for (let index = 1; index < units.length; index++) {
                const previous = units[index - 1];
                const current = units[index];
                if (previous.category === current.category) continue;
                if (!['latin', 'digit', 'punctuation', 'cjk'].includes(previous.category)
                    && !['latin', 'digit', 'punctuation', 'cjk'].includes(current.category))
                  continue;
                const previousCenter = previous.rect.left + previous.rect.width / 2;
                const currentCenter = current.rect.left + current.rect.width / 2;
                const sameColumn = Math.abs(previousCenter - currentCenter)
                  <= Math.max(previous.rect.width, current.rect.width) * 0.75;
                if (!sameColumn) continue;
                // DOM order advances to the next vertical column after the
                // current column reaches its bottom. Its range therefore
                // starts near the top again; that is a column transition, not
                // two glyphs sharing the same inline space.
                if (current.rect.top + 1.05 < previous.rect.top) continue;
                const overlap = previous.rect.bottom - current.rect.top;
                // WebKitGTK exposes adjacent Range advance boxes with up to
                // one device pixel of shared edge after fractional layout is
                // rounded. Treat only overlap beyond that rounding allowance
                // as a real inline collision.
                if (overlap <= 1.05) continue;
                boundaryOverlapCount++;
                maxBoundaryOverlap = Math.max(maxBoundaryOverlap, overlap);
                if (overlapItems.length < 12) {
                  overlapItems.push({
                    previous: previous.text,
                    current: current.text,
                    overlap: +overlap.toFixed(2),
                    previousRect: [previous.rect.left, previous.rect.top, previous.rect.width, previous.rect.height].map(v => +v.toFixed(2)),
                    currentRect: [current.rect.left, current.rect.top, current.rect.width, current.rect.height].map(v => +v.toFixed(2))
                  });
                }
              }
              return JSON.stringify({
                preparedVersion: body.dataset.libzoraVerticalInlinePrepared || '',
                writingMode: getComputedStyle(body).writingMode,
                textOrientation: getComputedStyle(body).textOrientation,
                tcyCount: tcyRuns.length,
                nativeDigitRunCount: nativeDigitRuns.length,
                nativeDigitCount: nativeDigits.length,
                digitCellStyleErrorCount,
                digitCellSizeErrorCount,
                digitOverlapCount,
                maxDigitOverlap,
                nativeFootnoteCount: nativeFootnotes.length,
                linuxNumberRunCount: linuxNumberRuns.length,
                linuxSingleCount: linuxSingles.length,
                linuxTcyCount: linuxTcyRuns.length,
                cjkSeparatedNumberCount: cjkSeparatedNumbers.length,
                cjkNumberSpacingErrorCount,
                linuxNumericStyleErrorCount,
                syntheticLinuxNumberLayoutCount,
                footnoteCombineStyleErrorCount,
                syntheticNativeDigitLayoutCount,
                tcyStyleErrorCount,
                minTcyInlineRatio: Number.isFinite(minTcyInlineRatio) ? minTcyInlineRatio : 0,
                maxTcyInlineRatio,
                legacyWrapperCount: legacy.length,
                syntheticLayoutCount,
                latinCount,
                digitCount,
                punctuationCount,
                boundaryOverlapCount,
                maxBoundaryOverlap,
                overlapItems
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader native vertical inline diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
#endif
