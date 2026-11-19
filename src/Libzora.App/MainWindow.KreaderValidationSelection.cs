#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Selection bar and hover diagnostics.
/// </summary>
public partial class MainWindow
{
    private static async Task<JsonElement> ShowAndReadKreaderSelectionBarAsync(IReaderHost host)
    {
        var selected = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const selection = window.getSelection?.();
              if (!selection || !document.body) return '';
              selection.removeAllRanges();
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                const parent = node.parentElement;
                if (!parent || parent.closest('#libzora-selection-bar, script, style, noscript')) continue;
                const text = node.nodeValue || '';
                const start = text.search(/\S/);
                if (start < 0) continue;
                const probe = document.createRange();
                probe.setStart(node, start);
                probe.setEnd(node, Math.min(text.length, start + 1));
                const rect = probe.getBoundingClientRect();
                if (rect.bottom <= 0 || rect.top >= window.innerHeight
                    || rect.right <= 0 || rect.left >= window.innerWidth) continue;
                const range = document.createRange();
                range.setStart(node, start);
                range.setEnd(node, Math.min(text.length, start + 6));
                selection.addRange(range);
                return selection.toString();
              }
              return '';
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(selected), "could not create visible reader selection");
        await Task.Delay(250);

        var result = await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('libzora-selection-bar');
              const firstButton = bar?.querySelector('button');
              if (!bar || !firstButton) return null;
              const style = getComputedStyle(bar);
              const buttonStyle = getComputedStyle(firstButton);
              const rect = bar.getBoundingClientRect();
              const buttonRect = firstButton.getBoundingClientRect();
              const signature = [
                style.display,
                style.writingMode,
                style.textOrientation,
                style.direction,
                style.paddingTop,
                style.paddingRight,
                style.paddingBottom,
                style.paddingLeft,
                rect.width.toFixed(2),
                rect.height.toFixed(2),
                bar.querySelectorAll(':scope > button').length,
                bar.querySelectorAll('.lz-sel-sep').length,
                buttonStyle.display,
                buttonStyle.fontFamily,
                buttonStyle.fontSize,
                buttonStyle.lineHeight,
                buttonRect.width.toFixed(2),
                buttonRect.height.toFixed(2)
              ].join('|');
              return JSON.stringify({
                display: style.display,
                writingMode: style.writingMode,
                textOrientation: style.textOrientation,
                direction: style.direction,
                width: rect.width,
                height: rect.height,
                buttonCount: bar.querySelectorAll('button').length,
                signature
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("selection bar metric script returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static string NormalizeKreaderSelectionBarSignature(JsonElement metrics)
    {
        var fields = (metrics.GetProperty("signature").GetString() ?? string.Empty).Split('|');
        // The bar is positioned against the selected glyph range. A vertical
        // Latin run can legitimately move that range by a few pixels, which
        // changes the auto-sized outer width/height without changing the bar
        // controls or their writing direction. Compare the stable style and
        // control dimensions, not those two placement-dependent fields.
        if (fields.Length > 9)
        {
            fields[8] = "<dynamic-width>";
            fields[9] = "<dynamic-height>";
        }
        return string.Join('|', fields);
    }

    private static async Task ClearKreaderValidationSelectionAsync(IReaderHost host)
    {
        await host.InvokeScriptAsync("window.getSelection?.()?.removeAllRanges();");
        await Task.Delay(100);
    }

    private async Task ValidateWindowsFootnoteHoverAndClickAsync(IReaderHost host, TextWriter log)
    {
        if (!OperatingSystem.IsWindows() || host.View is not Avalonia.Controls.Control view)
            return;

        await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              anchor?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
            })();
            """);
        await Task.Delay(150);
        var rawRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              if (!anchor) return null;
              const rect = anchor.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2,
                viewportWidth: document.documentElement.clientWidth || window.innerWidth || 1,
                viewportHeight: document.documentElement.clientHeight || window.innerHeight || 1
              });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawRect), "footnote marker rectangle missing");
        using var rectDocument = JsonDocument.Parse(rawRect!);
        var rect = rectDocument.RootElement;
        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        var screenX = topLeft.X + (int)Math.Round(rect.GetProperty("x").GetDouble()
            * width / Math.Max(1, rect.GetProperty("viewportWidth").GetDouble()));
        var screenY = topLeft.Y + (int)Math.Round(rect.GetProperty("y").GetDouble()
            * height / Math.Max(1, rect.GetProperty("viewportHeight").GetDouble()));
        Require(SetCursorPos(screenX - 40, screenY), "failed to move cursor away from footnote marker");
        await Task.Delay(80);
        Require(SetCursorPos(screenX, screenY), "failed to position cursor over footnote marker");

        var hoverStates = new List<bool>();
        for (var sample = 0; sample < 8; sample++)
        {
            await Task.Delay(100);
            hoverStates.Add(ReaderFootnoteHostPopup.IsOpen);
        }
        if (!ReaderFootnoteHostPopup.IsOpen)
        {
            var hit = DecodeReaderScriptString(await host.InvokeScriptAsync($$"""
                (() => {
                  const x = {{rect.GetProperty("x").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                  const y = {{rect.GetProperty("y").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                  const element = document.elementFromPoint(x, y);
                  return JSON.stringify({
                    tag: element?.tagName || '',
                    href: element?.closest?.('a')?.href || '',
                    text: element?.textContent || ''
                  });
                })();
                """));
            await log.WriteLineAsync($"DEBUG footnote hover states={string.Join(',', hoverStates)} hit={hit}");
        }
        Require(ReaderFootnoteHostPopup.IsOpen, "footnote popup closed while pointer stayed over marker");
        Require(
            ReaderFootnoteText.Text?.Contains("Footnote validation body", StringComparison.Ordinal) == true,
            "footnote popup text missing");

        await host.InvokeScriptAsync("""
            (() => {
              const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
              anchor?.click();
              return !!anchor;
            })();
            """);
        var navigated = false;
        for (var attempt = 0; attempt < 20 && !navigated; attempt++)
        {
            await Task.Delay(100);
            var result = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const target = document.getElementById('footnote-1');
                  return JSON.stringify({
                    visible: !!target && getComputedStyle(target).display !== 'none',
                    hash: window.__libzoraReaderLogicalHash || location.hash || ''
                  });
                })();
                """));
            if (string.IsNullOrWhiteSpace(result)) continue;
            using var resultDocument = JsonDocument.Parse(result);
            var root = resultDocument.RootElement;
            navigated = root.GetProperty("visible").GetBoolean()
                && root.GetProperty("hash").GetString()?.Contains("footnote-1", StringComparison.Ordinal) == true;
        }
        if (!navigated)
        {
            var diagnostic = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const anchor = document.querySelector('a[epub\\:type*="noteref"], a[role*="doc-noteref"]');
                  const target = document.getElementById('footnote-1');
                  return JSON.stringify({
                    href: anchor?.href || '',
                    targetDisplay: target ? getComputedStyle(target).display : '',
                    targetInlineDisplay: target?.style?.getPropertyValue('display') || '',
                    hash: window.__libzoraReaderLogicalHash || location.hash || ''
                  });
                })();
                """));
            await log.WriteLineAsync(
                $"DEBUG footnote click navigation: {diagnostic}; currentFragment={_readerCurrentFragment}; status={ReaderStatusText.Text}");
        }
        Require(navigated, "clicking footnote marker did not reveal and navigate to its target");
        HideReaderFootnotePopup();
        await log.WriteLineAsync("PASS Windows footnote hover stability and click navigation");
    }
}
#endif
