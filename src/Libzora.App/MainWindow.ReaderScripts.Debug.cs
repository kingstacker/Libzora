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
/// Debug layout and document reveal scripts.
/// </summary>
public partial class MainWindow
{
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
