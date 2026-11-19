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
/// JavaScript injected into the reader document for vertical text,
/// ruby and debug layout.
/// </summary>
public partial class MainWindow
{
    internal const char ReaderLinuxTextFallbackFootnoteMarker = '\uE000';
    private const string ReaderWebBundledFontFamily = "LibzoraKingHwaOldSong";
    private const string ReaderBundledFontTargetFileName = ".KingHwaOldSong-v3.0.ttf";
    private const int ReaderAnimationNone = 0;
    private const int ReaderAnimationFade = 1;
    private const int ReaderAnimationSlide = 2;
    private const int ReaderAnimationWave = 3;
    private const double ReaderZenActivationWidth = 380;
    private const double ReaderZenActivationHeight = 64;
    // The Linux reader must render the real EPUB in WebKitGTK, exactly like
    // the Windows reader does in WebView2: covers and images, the selection
    // action bar, footnote popovers and the injected page-turn/keyboard
    // handling all live in that document. Keep the old Avalonia text surface
    // in the source for diagnostics and regression tests, but never select it
    // as a production reader surface.
    private static readonly bool UseLinuxPlainTextRecoveryFallback = false;

    private const string PrepareVerticalInlineRunsScript = """
        (() => {
          // Publication 縦中横: a two-digit run sits horizontally inside one
          // one-em square, filling it like two half-width digits side by side
          // while keeping full text height. The base CSS uses a fixed
          // compression that suits common Linux fonts; here each cell is fit
          // against its real untransformed layout width (offsetWidth ignores
          // transforms), so DejaVu, Noto and embedded EPUB fonts all land on
          // the standard square instead of a squeezed sliver.
          const fitVerticalTcyRuns = () => {
            if (window.__libzoraReaderVertical !== true) return;
            const cells = document.querySelectorAll(
              '.libzora-tcy[data-libzora-vertical-run="1"], .libzora-tcy-all[data-libzora-vertical-run="1"]');
            for (const cell of cells) {
              const inner = cell.querySelector(':scope > .libzora-tcy-inner');
              if (!inner) continue;
              const em = parseFloat(getComputedStyle(inner).fontSize);
              const natural = inner.offsetWidth;
              if (!(em > 0) || !(natural > 0)) continue;
              const scale = Math.min(1, (em * 0.96) / natural);
              inner.style.setProperty(
                'transform',
                'translate(-50%, -50%) scaleX(' + scale.toFixed(4) + ')',
                'important');
            }
          };

          // Sideways runs are atomic by default so a word or a number is never
          // torn apart mid-glyph. A run that is longer than the column it must
          // live in cannot honor that: kept nowrap it overflows past the page
          // bottom and the margin mask cuts it. Measure each run against the
          // usable column extent and let the ones that cannot fit wrap.
          // In vertical-rl the inline axis is vertical, so a run's border-box
          // height is its inline extent.
          const fitVerticalSidewaysRuns = () => {
            if (window.__libzoraReaderVertical !== true) return;
            const body = document.body;
            const el = document.scrollingElement || document.documentElement;
            if (!body || !el) return;
            const runs = body.querySelectorAll(
              '.libzora-vertical-latin[data-libzora-vertical-run="1"], '
              + '.libzora-vertical-number[data-libzora-vertical-run="1"]');
            for (const run of runs) run.classList.remove('libzora-vertical-run-wrap');
            const style = getComputedStyle(body);
            const available = (el.clientHeight || 0)
              - (parseFloat(style.paddingTop) || 0)
              - (parseFloat(style.paddingBottom) || 0);
            if (!(available > 0)) return;
            for (const run of runs) {
              if (run.getBoundingClientRect().height > available + 0.5)
                run.classList.add('libzora-vertical-run-wrap');
            }
          };

          const fitVerticalRuns = () => {
            fitVerticalTcyRuns();
            fitVerticalSidewaysRuns();
          };
          window.__libzoraFitVerticalTcyRuns = fitVerticalTcyRuns;
          window.__libzoraFitVerticalRuns = fitVerticalRuns;
          // The usable column extent changes with the viewport, so the wrap
          // decision has to be revisited on resize. Configuration passes only
          // rebind the closure; the listener itself is installed once.
          if (window.__libzoraVerticalFitResizeBound !== true) {
            window.__libzoraVerticalFitResizeBound = true;
            let pending = 0;
            window.addEventListener('resize', () => {
              if (pending) clearTimeout(pending);
              pending = setTimeout(() => {
                pending = 0;
                if (typeof window.__libzoraFitVerticalRuns === 'function')
                  window.__libzoraFitVerticalRuns();
              }, 120);
            });
          }

          if (typeof window.__libzoraPrepareVerticalInlineRuns === 'function')
            window.__libzoraPrepareVerticalInlineRuns();

          const prepare = () => {
            const body = document.body;
            if (!body || window.__libzoraReaderVertical !== true) return false;
            if (body.dataset.libzoraVerticalInlinePrepared === '65') return true;

            body.querySelectorAll('span[data-libzora-vertical-run="1"]')
              .forEach(span => span.replaceWith(document.createTextNode(span.textContent || '')));

            // Keep these rules identical to MarkVerticalInlineRuns in
            // EpubReaderPreparationService and to the injected bridge copy.
            // A token carries its own internal connectors, so "don't", "AT&T"
            // and "well-known" survive whole; adjacent tokens separated by a
            // short ASCII gap merge into one phrase when either side has a
            // letter, so an English sentence becomes one sideways run instead
            // of one rotated box per word. Digit-only neighbours never merge.
            const verticalInlineTokenPattern = /[A-Za-z0-9]+(?:['’&.,:/+\-–—][A-Za-z0-9]+|%|°[CF])*/g;
            const numericTokenPattern = /^[0-9]+(?:[.,:/+\-–—][0-9]+|%|°[CF])*$/;
            const verticalPunctuationCharacters = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
            const maxPhraseGap = 3;
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
            const textNodes = [];
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (!parent
                  || parent.closest('#libzora-selection-bar, script, style, noscript, ruby, rt, [data-libzora-vertical-run="1"]')
                  || !/[0-9A-Za-z!\"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~]/.test(node.nodeValue || ''))
                continue;
              textNodes.push(node);
            }

            const canMerge = (value, left, right) => {
              if (!/[A-Za-z]/.test(left.token) && !/[A-Za-z]/.test(right.token)) return false;
              const gapStart = left.start + left.token.length;
              const gapLength = right.start - gapStart;
              if (gapLength < 1 || gapLength > maxPhraseGap) return false;
              for (let index = gapStart; index < right.start; index++) {
                const character = value[index];
                if (character !== ' ' && character !== '\t'
                    && !verticalPunctuationCharacters.includes(character))
                  return false;
              }
              return true;
            };

            const classify = (token, merged) => {
              const hasLatin = /[A-Za-z]/.test(token);
              if (merged) return hasLatin ? 'libzora-vertical-latin' : null;
              const isNumericToken = numericTokenPattern.test(token);
              const digitCount = (token.match(/[0-9]/g) || []).length;
              if (!hasLatin && (!isNumericToken || digitCount === 0)) return null;
              if (!isNumericToken) return 'libzora-vertical-latin';
              if (!/^[0-9]+$/.test(token)) return 'libzora-vertical-number';
              return token.length === 1
                ? 'libzora-vertical-digit'
                : token.length === 2
                ? 'libzora-tcy'
                : 'libzora-vertical-number';
            };

            for (const node of textNodes) {
              const value = node.nodeValue || '';
              const fragment = document.createDocumentFragment();
              let cursor = 0;
              let wrapped = false;
              const matches = [];
              verticalInlineTokenPattern.lastIndex = 0;
              for (let match = verticalInlineTokenPattern.exec(value);
                   match;
                   match = verticalInlineTokenPattern.exec(value)) {
                matches.push({ token: match[0], start: match.index });
              }

              const runs = [];
              for (let index = 0; index < matches.length;) {
                let last = index;
                while (last + 1 < matches.length
                    && canMerge(value, matches[last], matches[last + 1]))
                  last++;
                const start = matches[index].start;
                const end = matches[last].start + matches[last].token.length;
                const token = value.slice(start, end);
                const merged = last > index;
                index = last + 1;
                const className = classify(token, merged);
                if (!className) continue;
                runs.push({ start, length: end - start, token, className });
              }

              for (let index = 0; index < value.length; index++) {
                if (!verticalPunctuationCharacters.includes(value[index])
                    || runs.some(run => index >= run.start
                      && index < run.start + run.length))
                  continue;
                runs.push({
                  start: index,
                  length: 1,
                  token: value[index],
                  className: 'libzora-vertical-punctuation'
                });
              }

              runs.sort((left, right) => left.start - right.start);
              for (const run of runs) {
                const { start, token, className } = run;

                if (start > cursor)
                  fragment.appendChild(document.createTextNode(value.slice(cursor, start)));
                const span = document.createElement('span');
                span.className = className;
                span.dataset.libzoraVerticalRun = '1';
                if (className === 'libzora-tcy' || className === 'libzora-tcy-all') {
                  span.dataset.libzoraTcyLength = String(token.length);
                  const inner = document.createElement('span');
                  inner.className = 'libzora-tcy-inner';
                  inner.textContent = token;
                  span.appendChild(inner);
                } else if (
                    className === 'libzora-vertical-digit'
                    || className === 'libzora-vertical-punctuation') {
                  // Same centered-cell structure as tate-chu-yoko so digit
                  // and punctuation ink stays inside its one-em cell no
                  // matter which Linux font supplies the baseline metrics.
                  const inner = document.createElement('span');
                  inner.className = 'libzora-cell-inner';
                  inner.textContent = token;
                  span.appendChild(inner);
                } else {
                  span.textContent = token;
                }
                fragment.appendChild(span);
                cursor = start + run.length;
                wrapped = true;
              }
              if (!wrapped) continue;
              if (cursor < value.length)
                fragment.appendChild(document.createTextNode(value.slice(cursor)));
              node.parentNode?.replaceChild(fragment, node);
            }
            body.dataset.libzoraVerticalInlinePrepared = '65';
            return true;
          };

          if (!document.body) {
            document.addEventListener('DOMContentLoaded', () => {
              prepare();
              fitVerticalRuns();
            }, { once: true });
            return true;
          }
          prepare();
          fitVerticalRuns();
          return true;
        })();
        """;

    // Match Apple Books' publication model: WebKit receives the original text
    // runs and the EPUB alone decides where tate-chu-yoko is appropriate via
    // text-combine. Kreader only unwraps private boxes from older caches.
    private const string PreparePublicationVerticalTextScript = """
        (() => {
          const prepare = () => {
            const body = document.body;
            if (!body || window.__libzoraReaderVertical !== true) return false;

            body.querySelectorAll('span[data-libzora-vertical-run="1"]')
              .forEach(span => span.replaceWith(
                document.createTextNode(span.textContent || '')));
            body.querySelectorAll(
              '.libzora-native-vertical-digits, .libzora-native-vertical-footnote, '
              + '.libzora-linux-vertical-single, .libzora-linux-vertical-tcy, '
              + '.libzora-linux-vertical-number, '
              + '.libzora-linux-vertical-single-punctuation, '
              + '.libzora-linux-vertical-cjk, '
              + '.libzora-linux-vertical-pair-punctuation')
              .forEach(span => span.replaceWith(
                document.createTextNode(span.textContent || '')));
            body.querySelectorAll('.libzora-linux-vertical-footnote-inner')
              .forEach(span => span.replaceWith(...span.childNodes));
            body.querySelectorAll('.libzora-linux-vertical-footnote')
              .forEach(anchor => {
                anchor.classList.remove('libzora-linux-vertical-footnote');
                anchor.removeAttribute('data-libzora-linux-vertical-footnote');
                anchor.style.removeProperty('--libzora-linux-footnote-scale');
              });
            body.normalize();
            body.dataset.libzoraVerticalInlinePrepared = 'publication-native-1';
            window.__libzoraFitVerticalTcyRuns = null;
            window.__libzoraFitVerticalRuns = null;
            return true;
          };

          if (!document.body) {
            document.addEventListener('DOMContentLoaded', prepare, { once: true });
            return true;
          }
          return prepare();
        })();
        """;

    // Vertical compatibility cells. Publication-authored text-combine always
    // wins; isolated punctuation and otherwise-unmarked pure numeric runs get
    // fixed one-em cells so their physical alignment is stable in vertical-rl
    // on both WebKitGTK and WebView2.
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
    private const string PrepareVerticalDebugLayoutScript = """
        (() => {
          const body = document.body;
          const root = document.documentElement;
          if (!body || !root) return false;
          body.dataset.libzoraVerticalDebugBoxes = '1';

          let layer = document.getElementById('libzora-vertical-debug-boxes');
          if (!layer) {
            layer = document.createElement('div');
            layer.id = 'libzora-vertical-debug-boxes';
            layer.setAttribute('aria-hidden', 'true');
            layer.style.cssText = [
              'position:fixed',
              'left:0',
              'top:0',
              'width:0',
              'height:0',
              'margin:0',
              'padding:0',
              'border:0',
              'pointer-events:none',
              'z-index:2147483647',
              'writing-mode:horizontal-tb',
              'font-family:sans-serif'
            ].join(';');
            root.appendChild(layer);
          }

          const colors = {
            cjk: 'rgba(22, 163, 74, 0.92)',
            latin: 'rgba(124, 58, 237, 0.92)',
            digit: 'rgba(234, 88, 12, 0.92)',
            punctuation: 'rgba(219, 39, 119, 0.92)',
            other: 'rgba(107, 114, 128, 0.82)'
          };
          const classify = character => {
            if (/[A-Za-z]/.test(character)) return 'latin';
            if (/[0-9]/.test(character)) return 'digit';
            if (/[!"#$%&'()*+,\-./:;<=>?@[\\\]^_`{|}~，。！？、；：“”‘’（）《》〈〉【】〔〕［］｛｝…—]/.test(character))
              return 'punctuation';
            if (/[⺀-鿿豈-﫿]/.test(character)) return 'cjk';
            return 'other';
          };
          const isGenerated = node => !!node.closest?.(
            '.libzora-linux-vertical-single, '
              + '.libzora-linux-vertical-single-punctuation, '
              + '.libzora-linux-vertical-cjk, '
              + '.libzora-linux-vertical-tcy, '
              + '.libzora-linux-vertical-number, '
              + '.libzora-linux-vertical-pair-punctuation, '
              + '.libzora-linux-vertical-footnote, '
              + '#libzora-selection-bar, script, style, noscript');
          const isVisibleRect = rect => rect
            && rect.width > 0
            && rect.height > 0
            && Number.isFinite(rect.left)
            && Number.isFinite(rect.top);
          const appendFixedFrame = (fragment, rect, color, style = 'solid', kind = '') => {
            if (!isVisibleRect(rect)) return;
            const box = document.createElement('div');
            if (kind) box.dataset.libzoraVerticalDebugKind = kind;
            box.style.cssText = [
              'position:fixed',
              'left:' + rect.left.toFixed(2) + 'px',
              'top:' + rect.top.toFixed(2) + 'px',
              'width:' + rect.width.toFixed(2) + 'px',
              'height:' + rect.height.toFixed(2) + 'px',
              'box-sizing:border-box',
              'margin:0',
              'padding:0',
              'border:1px ' + style + ' ' + color,
              'pointer-events:none',
              'writing-mode:horizontal-tb'
            ].join(';');
            fragment.appendChild(box);
          };
          const queueRefresh = () => {
            if (queueRefresh.pending) return;
            queueRefresh.pending = true;
            requestAnimationFrame(() => {
              queueRefresh.pending = false;
              refresh();
            });
          };
          const refreshAfterLayout = () => {
            queueRefresh();
            requestAnimationFrame(() => {
              queueRefresh();
              requestAnimationFrame(queueRefresh);
            });
            // Image decode and the cover-fit pass can settle after two
            // animation frames on WebKitGTK. This delayed pass is still
            // bounded and only runs while the debug overlay is enabled.
            setTimeout(queueRefresh, 120);
          };
          const refresh = () => {
            const fragment = document.createDocumentFragment();
            const legend = document.createElement('div');
            legend.style.cssText = [
              'position:fixed',
              'left:8px',
              'top:8px',
              'padding:3px 5px',
              'border:1px solid rgba(17,24,39,.65)',
              'background:rgba(255,255,255,.88)',
              'color:#111',
              'font:11px/1.2 sans-serif',
              'white-space:nowrap',
              'writing-mode:horizontal-tb'
            ].join(';');
            legend.textContent = '红=兼容字格 蓝=内部字形框 绿=汉字 紫=英文 橙=数字 粉=标点';
            fragment.appendChild(legend);

            // Han/digit/punctuation frames are CSS outlines on their real
            // one-em layout cells. This fixed layer is only for native text
            // ranges (Latin and other unwrapped publication content).

            const viewportWidth = window.innerWidth || root.clientWidth || 0;
            const viewportHeight = window.innerHeight || root.clientHeight || 0;
            const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT);
            let drawn = 0;
            for (let node = walker.nextNode(); node && drawn < 1400; node = walker.nextNode()) {
              const parent = node.parentElement;
              if (!parent || isGenerated(parent)) continue;
              const text = node.nodeValue || '';
              for (let index = 0; index < text.length && drawn < 1400; index++) {
                if (/\s/.test(text[index])) continue;
                const range = document.createRange();
                range.setStart(node, index);
                range.setEnd(node, index + 1);
                for (const rect of range.getClientRects()) {
                  if (!(rect.width > 0) || !(rect.height > 0)
                      || rect.right < 0 || rect.bottom < 0
                      || rect.left > viewportWidth || rect.top > viewportHeight)
                    continue;
                  appendFixedFrame(
                    fragment,
                    rect,
                    colors[classify(text[index])],
                    'solid',
                    classify(text[index]));
                  drawn++;
                }
              }
            }
            layer.replaceChildren(fragment);
          };
          queueRefresh.pending = false;
          window.__libzoraVerticalDebugQueueRefresh = queueRefresh;
          window.__libzoraVerticalDebugRefresh = refreshAfterLayout;
          if (window.__libzoraVerticalDebugListenersBound !== true) {
            window.__libzoraVerticalDebugListenersBound = true;
            const refreshLiveLayout = () =>
              window.__libzoraVerticalDebugQueueRefresh?.();
            const refreshLoadedLayout = () =>
              window.__libzoraVerticalDebugRefresh?.();
            window.__libzoraVerticalDebugLiveLayoutHandler = refreshLiveLayout;
            window.__libzoraVerticalDebugLoadHandler = refreshLoadedLayout;
            window.addEventListener('scroll', refreshLiveLayout, true);
            window.addEventListener('resize', refreshLiveLayout, true);
            window.addEventListener('load', refreshLoadedLayout, true);
            document.fonts?.ready?.then(() =>
              window.__libzoraVerticalDebugRefresh?.()).catch(() => {});
          }
          window.__libzoraVerticalDebugResizeObserver?.disconnect?.();
          if (typeof ResizeObserver === 'function') {
            const observer = new ResizeObserver(() =>
              window.__libzoraVerticalDebugQueueRefresh?.());
            observer.observe(root);
            observer.observe(body);
            window.__libzoraVerticalDebugResizeObserver = observer;
          }
          window.__libzoraVerticalDebugMutationObserver?.disconnect?.();
          if (typeof MutationObserver === 'function') {
            const observer = new MutationObserver(() =>
              window.__libzoraVerticalDebugQueueRefresh?.());
            observer.observe(body, {
              subtree: true,
              attributes: true,
              attributeFilter: ['class', 'style']
            });
            window.__libzoraVerticalDebugMutationObserver = observer;
          }
          refreshAfterLayout();
          return true;
        })();
        """;

    // The settings switch must remove more than the visible layer: leaving
    // observers and capture listeners alive would keep rebuilding detached
    // debug rectangles after the user turns the feature off.
    private const string RemoveVerticalDebugLayoutScript = """
        (() => {
          document.body?.removeAttribute('data-libzora-vertical-debug-boxes');
          document.getElementById('libzora-vertical-debug-boxes')?.remove();
          window.__libzoraVerticalDebugResizeObserver?.disconnect?.();
          window.__libzoraVerticalDebugMutationObserver?.disconnect?.();
          const liveHandler = window.__libzoraVerticalDebugLiveLayoutHandler;
          const loadHandler = window.__libzoraVerticalDebugLoadHandler;
          if (liveHandler) {
            window.removeEventListener('scroll', liveHandler, true);
            window.removeEventListener('resize', liveHandler, true);
          }
          if (loadHandler)
            window.removeEventListener('load', loadHandler, true);
          window.__libzoraVerticalDebugResizeObserver = null;
          window.__libzoraVerticalDebugMutationObserver = null;
          window.__libzoraVerticalDebugLiveLayoutHandler = null;
          window.__libzoraVerticalDebugLoadHandler = null;
          window.__libzoraVerticalDebugQueueRefresh = null;
          window.__libzoraVerticalDebugRefresh = null;
          window.__libzoraVerticalDebugListenersBound = false;
          return true;
        })();
        """;

    private const string RevealReaderDocumentScript = """
        (() => {
          const root = document.documentElement;
          const body = document.body;
          if (!root || !body) return false;
          root.style.setProperty('visibility', 'visible', 'important');
          root.style.setProperty('opacity', '1', 'important');
          body.style.setProperty('visibility', 'visible', 'important');
          body.style.setProperty('opacity', '1', 'important');
          window.__libzoraReaderConfigured = true;
          return true;
        })();
        """;
}
