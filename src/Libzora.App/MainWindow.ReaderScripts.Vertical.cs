using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Vertical text and number script.
/// </summary>
public partial class MainWindow
{
    private const string PrepareVerticalNumbersAndPunctuationScript = """
        (() => {
          const body = document.getElementById(window.__libzoraVertWrapRoot || '')
            || document.body;
          if (!body || window.__libzoraReaderVertical !== true) return false;
          // In vertical-rl the inline axis runs top-to-bottom. Each generated
          // Han/digit/punctuation box therefore advances by its own local 1em;
          // line-height belongs to the horizontal column pitch and must never
          // be reused as the cell height.
          const hasPublicationCombine = element => {
            for (let node = element; node && node !== body; node = node.parentElement) {
              const style = getComputedStyle(node);
              const value = style.textCombineUpright || style.webkitTextCombine || '';
              if (value && value !== 'none') return true;
            }
            return false;
          };
          for (const anchor of body.querySelectorAll('a')) {
            if (anchor.classList.contains('libzora-linux-vertical-footnote')) continue;
            const label = (anchor.textContent || '').replace(/\s+/g, '');
            const metadata = [
              anchor.className || '',
              anchor.getAttribute('role') || '',
              anchor.getAttribute('epub:type') || '',
              anchor.getAttribute('href') || '',
              anchor.getAttribute('data-libzora-footnote-href') || ''
            ].join(' ');
            const isFootnote = /libzora-footnote-reference|doc-noteref|noteref|footnote|endnote/i.test(metadata)
              || (!!anchor.querySelector('sup') && /^\[?\d{1,3}\]?$/.test(label));
            if (!isFootnote) continue;
            if (!label || label.length > 6 || !/[0-9０-９一二三四五六七八九十]/.test(label))
              continue;
            const inner = document.createElement('span');
            inner.className = 'libzora-linux-vertical-footnote-inner';
            while (anchor.firstChild) inner.appendChild(anchor.firstChild);
            anchor.appendChild(inner);
            anchor.classList.add('libzora-linux-vertical-footnote');
            anchor.dataset.libzoraLinuxVerticalFootnote = '1';
            const scale = label.length <= 3 ? 0.62 : label.length === 4 ? 0.50 : 0.42;
            anchor.style.setProperty('--libzora-linux-footnote-scale', scale + 'em');
          }

          const openingPunctuation = new Set([
            '（', '《', '〈', '【', '〔', '［', '｛', '“', '‘', '「', '『', '〖', '〘', '〚'
          ]);
          const closingPunctuation = new Set([
            '）', '》', '〉', '】', '〕', '］', '｝', '”', '’', '」', '』', '〗', '〙', '〛'
          ]);
          const openingQuotes = new Set(['“', '‘', '「', '『']);
          const closingQuotes = new Set(['”', '’', '」', '』']);
          const verticalPairGlyphs = new Map([
            ['（', '︵'], ['）', '︶'], ['(', '︵'], [')', '︶'],
            ['｛', '︷'], ['｝', '︸'], ['{', '︷'], ['}', '︸'],
            ['〔', '︹'], ['〕', '︺'], ['【', '︻'], ['】', '︼'],
            ['《', '︽'], ['》', '︾'], ['〈', '︿'], ['〉', '﹀'],
            ['［', '﹇'], ['］', '﹈'], ['[', '﹇'], [']', '﹈'],
            // Use the Unicode vertical quote forms instead of rotating the
            // horizontal quote glyph. The latter has different left/right
            // side bearings for open/close marks and was the source of the
            // visibly asymmetric quote/bracket edges on Linux.
            ['“', '﹁'], ['”', '﹂'], ['‘', '﹃'], ['’', '﹄'],
            ['「', '﹁'], ['」', '﹂'], ['『', '﹃'], ['』', '﹄']
          ]);
          // Horizontal punctuation glyphs are intentionally positioned toward
          // a corner of their em square. Use the Unicode vertical forms for
          // marks that have a dedicated form; the original character remains
          // in the text node for copy/search and the visible form is painted
          // by the centered cell pseudo-element below.
          const verticalSingleGlyphs = new Map([
            ['，', '︐'], ['、', '︑'], ['。', '︒'],
            ['：', '︓'], ['；', '︔'], ['！', '︕'], ['？', '︖'],
            ['｡', '︒'], ['､', '︑']
          ]);
          const addCellGlyph = (inner, value) => {
            const glyph = document.createElement('span');
            glyph.className = 'libzora-linux-vertical-glyph';
            glyph.textContent = value;
            inner.replaceChildren(glyph);
            return glyph;
          };
          const asciiOpeningPunctuation = new Set(['(', '[', '{']);
          const asciiClosingPunctuation = new Set([')', ']', '}']);
          const singlePunctuation = new Set([
            '，', '。', '！', '？', '、', '；', '：', '…', '—', '―', '–', '－',
            '·', '・', '｡', '､', '．', '‥', '〃', '※', '〽', '﹏', '～', '〜'
          ]);
          const verticalCenteredMarks = new Set(['…', '‥', '—', '―', '–', '－']);
          // Fullwidth colon/semicolon use their Unicode vertical forms below;
          // only ASCII forms still need the explicit quarter-turn fallback.
          const verticalRotatedPunctuation = new Set([':', ';']);
          const asciiPunctuation = new Set([
            '!', '"', '#', '$', '%', '&', '*', '+', ',', '-', '.', '/', ':',
            ';', '<', '=', '>', '?', '@', '\\', '^', '_', '`', '|', '~'
          ]);
          const hasAsciiWordNeighbor = (value, index) =>
            /[A-Za-z0-9]/.test(value[index - 1] || '')
            || /[A-Za-z0-9]/.test(value[index + 1] || '');
          const isPunctuationCandidate = (value, index) => {
            const character = value[index];
            return openingPunctuation.has(character)
              || closingPunctuation.has(character)
              || ((asciiOpeningPunctuation.has(character)
                   || asciiClosingPunctuation.has(character))
                  && !hasAsciiWordNeighbor(value, index))
              || singlePunctuation.has(character)
              || (asciiPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
          };
          const containsPunctuationCandidate = value => {
            for (let index = 0; index < value.length; index++) {
              if (isPunctuationCandidate(value, index)) return true;
            }
            return false;
          };
          const punctuationNodes = [];
          const punctuationWalker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = punctuationWalker.nextNode(); node; node = punctuationWalker.nextNode()) {
            const parent = node.parentElement;
            if (!parent
                || !containsPunctuationCandidate(node.nodeValue || '')
                || parent.closest('#libzora-selection-bar, script, style, noscript, '
                  + '[data-libzora-reader-chrome="1"], .libzora-linux-vertical-footnote, '
                  + '.libzora-linux-vertical-single-punctuation, '
                  + '.libzora-linux-vertical-pair-punctuation')
                || hasPublicationCombine(parent))
              continue;
            punctuationNodes.push(node);
          }
          for (const node of punctuationNodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            let cursor = 0;
            for (let index = 0; index < value.length; index++) {
              const character = value[index];
              const isOpening = openingPunctuation.has(character)
                || (asciiOpeningPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              const isClosing = closingPunctuation.has(character)
                || (asciiClosingPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              const isSingle = singlePunctuation.has(character)
                || (asciiPunctuation.has(character) && !hasAsciiWordNeighbor(value, index));
              if (!isOpening && !isClosing && !isSingle) continue;
              if (index > cursor)
                fragment.appendChild(document.createTextNode(value.slice(cursor, index)));
              const span = document.createElement('span');
              if (isOpening || isClosing) {
                span.className = 'libzora-linux-vertical-pair-punctuation '
                  + (isOpening
                    ? 'libzora-linux-vertical-pair-open'
                    : 'libzora-linux-vertical-pair-close');
                if (openingQuotes.has(character))
                  span.classList.add('libzora-linux-vertical-quote-open');
                if (closingQuotes.has(character))
                  span.classList.add('libzora-linux-vertical-quote-close');
                span.dataset.libzoraLinuxVerticalPairPunctuation = isOpening ? 'open' : 'close';
              } else {
                span.className = 'libzora-linux-vertical-single-punctuation';
                if (verticalCenteredMarks.has(character))
                  span.classList.add('libzora-linux-vertical-centered-mark');
                if (verticalRotatedPunctuation.has(character))
                  span.classList.add('libzora-linux-vertical-rotated-punctuation');
                span.dataset.libzoraLinuxVerticalSinglePunctuation = '1';
              }
              const inner = document.createElement('span');
              inner.className = 'libzora-linux-vertical-cell-inner';
              inner.textContent = character;
              if (isOpening || isClosing || verticalSingleGlyphs.has(character)) {
                inner.dataset.libzoraVerticalGlyph = (isOpening || isClosing)
                  ? (verticalPairGlyphs.get(character) || character)
                  : verticalSingleGlyphs.get(character);
              } else {
                // An unmapped single mark is rendered by a real child glyph.
                // Do not also attach the data attribute used by the pseudo
                // element for vertical presentation forms, or it would paint
                // the mark twice (and invalidate its optical centre).
                inner.removeAttribute('data-libzora-vertical-glyph');
                addCellGlyph(inner, character);
              }
              span.appendChild(inner);
              fragment.appendChild(span);
              cursor = index + 1;
            }
            if (cursor < value.length)
              fragment.appendChild(document.createTextNode(value.slice(cursor)));
            node.parentNode?.replaceChild(fragment, node);
          }
          const nodes = [];
          const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const parent = node.parentElement;
            if (!parent
                || !/[0-9]/.test(node.nodeValue || '')
                || parent.closest('#libzora-selection-bar, script, style, noscript, '
                  + '[data-libzora-reader-chrome="1"], .libzora-linux-vertical-number, '
                  + '.libzora-linux-vertical-footnote, '
                  + '.libzora-linux-vertical-single-punctuation, '
                  + '.libzora-linux-vertical-pair-punctuation')
                || hasPublicationCombine(parent))
              continue;
            nodes.push(node);
          }
          for (const node of nodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            const pattern = /[0-9]+/g;
            let cursor = 0;
            let wrapped = false;
            for (let match = pattern.exec(value); match; match = pattern.exec(value)) {
              const before = value[match.index - 1] || '';
              const after = value[match.index + match[0].length] || '';
              if (/[A-Za-z0-9]/.test(before) || /[A-Za-z0-9]/.test(after)) continue;
              if (match.index > cursor)
                fragment.appendChild(document.createTextNode(value.slice(cursor, match.index)));
              const span = document.createElement('span');
              if (match[0].length === 1) {
                span.className = 'libzora-linux-vertical-single';
                span.dataset.libzoraLinuxVerticalSingle = '1';
                const inner = document.createElement('span');
                inner.className = 'libzora-linux-vertical-cell-inner';
                inner.textContent = match[0];
                addCellGlyph(inner, match[0]);
                span.appendChild(inner);
              } else if (match[0].length === 2) {
                span.className = 'libzora-linux-vertical-tcy';
                span.dataset.libzoraLinuxVerticalTcy = '1';
                const inner = document.createElement('span');
                inner.className = 'libzora-linux-vertical-tcy-inner';
                inner.textContent = match[0];
                span.appendChild(inner);
              } else {
                span.className = 'libzora-linux-vertical-number';
                span.dataset.libzoraLinuxVerticalNumber = '1';
                for (const digit of match[0]) {
                  const cell = document.createElement('span');
                  cell.className = 'libzora-linux-vertical-digit';
                  const inner = document.createElement('span');
                  inner.className = 'libzora-linux-vertical-cell-inner';
                  inner.textContent = digit;
                  addCellGlyph(inner, digit);
                  cell.appendChild(inner);
                  span.appendChild(cell);
                }
              }
              if (/[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/.test(before))
                span.classList.add('libzora-cjk-before-number');
              fragment.appendChild(span);
              cursor = match.index + match[0].length;
              wrapped = true;
            }
            if (!wrapped) continue;
            if (cursor < value.length)
              fragment.appendChild(document.createTextNode(value.slice(cursor)));
            node.parentNode?.replaceChild(fragment, node);
          }
          // Two digits use one horizontal tate-chu-yoko cell. Fit the actual
          // loaded font's natural width into that cell, then let the grid
          // center the complete run on the surrounding vertical column.
          for (const cell of body.querySelectorAll(
              '.libzora-linux-vertical-tcy[data-libzora-linux-vertical-tcy="1"]')) {
            const inner = cell.querySelector(':scope > .libzora-linux-vertical-tcy-inner');
            if (!inner) continue;
            const em = parseFloat(getComputedStyle(cell).fontSize);
            const naturalWidth = inner.offsetWidth;
            if (!(em > 0) || !(naturalWidth > 0)) continue;
            const scale = Math.min(1, (em * 0.9) / naturalWidth);
            inner.style.setProperty('transform', 'scaleX(' + scale.toFixed(4) + ')', 'important');
          }

          // WebKitGTK keeps an upright Han glyph on the font's native
          // baseline, which is not necessarily the visual centre of the
          // one-em advance box (KingHwaOldSong has a small right-side ink
          // overhang). Give every ordinary Han character the same explicit
          // cell as the compatibility marks. The outer cell is the layout
          // unit; only the inner glyph is nudged, so the column rhythm and
          // text offsets remain unchanged.
          const cjkCharacterPattern = /[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/;
          const cjkNodes = [];
          const cjkWalker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
          for (let node = cjkWalker.nextNode(); node; node = cjkWalker.nextNode()) {
            const parent = node.parentElement;
            const value = node.nodeValue || '';
            if (!parent
                || !cjkCharacterPattern.test(value)
                || parent.closest(
                  '#libzora-selection-bar, script, style, noscript, ruby, rt, '
                  + '[data-libzora-reader-chrome="1"], '
                  + '.libzora-linux-vertical-cjk, '
                  + '.libzora-linux-vertical-single, '
                  + '.libzora-linux-vertical-single-punctuation, '
                  + '.libzora-linux-vertical-tcy, '
                  + '.libzora-linux-vertical-number, '
                  + '.libzora-linux-vertical-pair-punctuation, '
                  + '.libzora-linux-vertical-footnote')
                || hasPublicationCombine(parent))
              continue;
            cjkNodes.push(node);
          }
          for (const node of cjkNodes) {
            const value = node.nodeValue || '';
            const fragment = document.createDocumentFragment();
            let textStart = 0;
            let wrapped = false;
            for (let index = 0; index < value.length;) {
              const codePoint = value.codePointAt(index);
              const character = String.fromCodePoint(codePoint || 0);
              const length = character.length;
              if (!cjkCharacterPattern.test(character)) {
                index += length;
                continue;
              }
              if (index > textStart)
                fragment.appendChild(document.createTextNode(value.slice(textStart, index)));
              const cell = document.createElement('span');
              cell.className = 'libzora-linux-vertical-cjk';
              cell.dataset.libzoraLinuxVerticalCjk = '1';
              const ink = document.createElement('span');
              ink.className = 'libzora-linux-vertical-cjk-ink';
              ink.dataset.libzoraVerticalInkCharacter = character;
              addCellGlyph(ink, character);
              cell.appendChild(ink);
              fragment.appendChild(cell);
              index += length;
              textStart = index;
              wrapped = true;
            }
            if (!wrapped) continue;
            if (textStart < value.length)
              fragment.appendChild(document.createTextNode(value.slice(textStart)));
            node.parentNode?.replaceChild(fragment, node);
          }

          // Centre the actual ink, rather than only the browser's advance
          // width. Canvas TextMetrics uses the same loaded font and weight as
          // the DOM. The correction is per glyph and scales automatically
          // for headings and bold titles.
          const inkContext = document.createElement('canvas').getContext('2d');
          const inkShiftCache = new Map();
          const applyInkShift = inner => {
            if (!inkContext) return;
            const glyph = inner.dataset.libzoraVerticalGlyph
              || inner.dataset.libzoraVerticalInkCharacter
              || (inner.textContent || '');
            if (!glyph || inner.classList.contains('libzora-linux-vertical-tcy-inner')) return;
            const style = getComputedStyle(inner);
            const isRotatedMark = inner.parentElement?.classList.contains(
              'libzora-linux-vertical-centered-mark')
              || inner.parentElement?.classList.contains(
                'libzora-linux-vertical-rotated-punctuation');
            const key = [
              style.fontStyle,
              style.fontWeight,
              style.fontSize,
              style.fontFamily,
              glyph,
              isRotatedMark ? 'rotated' : 'upright'
            ].join('|');
            let shift = inkShiftCache.get(key);
            if (shift === undefined) {
              if (isRotatedMark) {
                // The parent cell is quarter-turned after this pass. An
                // upright optical Y correction would become a physical X
                // offset after rotation, putting a dash/ellipsis off the
                // shared centre line. Symmetric rotated marks use the grid
                // centre directly.
                shift = { x: 0, y: 0 };
                inkShiftCache.set(key, shift);
              } else {
              inkContext.font = style.fontStyle + ' ' + style.fontWeight + ' '
                + style.fontSize + ' ' + style.fontFamily;
              const metrics = inkContext.measureText(glyph);
              const advance = metrics.width;
              const left = metrics.actualBoundingBoxLeft;
              const right = metrics.actualBoundingBoxRight;
              const actualAscent = metrics.actualBoundingBoxAscent;
              const actualDescent = metrics.actualBoundingBoxDescent;
              const fontAscent = metrics.fontBoundingBoxAscent;
              const fontDescent = metrics.fontBoundingBoxDescent;
              const shiftX = Number.isFinite(advance)
                && advance > 0
                && Number.isFinite(left)
                && Number.isFinite(right)
                ? (advance - (right - left)) / 2
                : 0;
              // The browser centres the line box, not the painted ink.
              // Use the font box to recover the CSS baseline inside that
              // line box, then move the painted glyph so its actual ink
              // bounds have the same centre as the em cell. This matters
              // most for vertical presentation brackets, whose arc sits
              // toward opposite ends of the em by design.
              const shiftY = Number.isFinite(actualAscent)
                && Number.isFinite(actualDescent)
                && Number.isFinite(fontAscent)
                && Number.isFinite(fontDescent)
                ? (actualAscent + fontDescent - fontAscent - actualDescent) / 2
                : 0;
              shift = { x: shiftX, y: shiftY };
              inkShiftCache.set(key, shift);
              }
            }
            inner.style.setProperty(
              '--libzora-vertical-ink-shift',
              shift.x.toFixed(3) + 'px',
              'important');
            inner.style.setProperty(
              '--libzora-vertical-ink-shift-y',
              shift.y.toFixed(3) + 'px',
              'important');
            inner.dataset.libzoraVerticalOpticalShiftX = shift.x.toFixed(3);
            inner.dataset.libzoraVerticalOpticalShiftY = shift.y.toFixed(3);
          };
          body.querySelectorAll(
            '.libzora-linux-vertical-cjk-ink, '
              + '.libzora-linux-vertical-single > .libzora-linux-vertical-cell-inner, '
              + '.libzora-linux-vertical-single-punctuation > .libzora-linux-vertical-cell-inner, '
              + '.libzora-linux-vertical-number > .libzora-linux-vertical-digit > .libzora-linux-vertical-cell-inner, '
              + '.libzora-linux-vertical-pair-punctuation > .libzora-linux-vertical-cell-inner')
            .forEach(applyInkShift);

          // Keep the optical correction independent from the outer inline
          // span's DOMRect. WebKitGTK can report that parent rect from a stale
          // vertical line box after a font-size change; feeding its centre
          // back into the child's relative offset makes the glyph walk into a
          // neighbouring column every time the reader is zoomed. Canvas ink
          // bearings scale with the live computed font and are the stable
          // paint-only correction here.
          const restoreVerticalHanOpticalShift = () => body.querySelectorAll(
            '.libzora-linux-vertical-cjk').forEach(cell => {
            const ink = cell.querySelector(':scope > .libzora-linux-vertical-cjk-ink');
            if (!ink) return;
            const opticalX = parseFloat(ink.dataset.libzoraVerticalOpticalShiftX) || 0;
            const opticalY = parseFloat(ink.dataset.libzoraVerticalOpticalShiftY) || 0;
            ink.style.setProperty(
              '--libzora-vertical-ink-shift',
              opticalX.toFixed(3) + 'px',
              'important');
            ink.style.setProperty(
              '--libzora-vertical-ink-shift-y',
              opticalY.toFixed(3) + 'px',
              'important');
          });
          window.__libzoraVerticalRecenterHan = restoreVerticalHanOpticalShift;
          restoreVerticalHanOpticalShift();
          document.body.dataset.libzoraVerticalInlinePrepared = 'publication-native-compat-1';
          return true;
        })();
        """;

    // Draw layout boxes while investigating Linux vertical typography. Every
    // generated Han/digit/punctuation cell is outlined directly by CSS; only
    // native Latin/other publication text needs fixed Range rectangles in the
    // pointer-transparent layer. The refresh hooks remain layout-aware because
    // cover fitting and late font/image loads can move those native ranges.
}
