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
}
