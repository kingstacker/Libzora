#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Highlight hover, document waits and DOM metrics.
/// </summary>
public partial class MainWindow
{
    private async Task ValidateWindowsSelectionHighlightHoverAsync(IReaderHost host, TextWriter log)
    {
        if (!OperatingSystem.IsWindows() || host.View is not Avalonia.Controls.Control view)
            return;

        await host.InvokeScriptAsync("""
            (() => {
              const paragraph = Array.from(document.querySelectorAll('p'))
                .find(item => (item.textContent || '').trim().length >= 24);
              const textNode = paragraph?.firstChild;
              if (!textNode || textNode.nodeType !== Node.TEXT_NODE) return false;
              const range = document.createRange();
              range.setStart(textNode, 0);
              range.setEnd(textNode, Math.min(20, textNode.textContent?.length || 0));
              const selection = window.getSelection();
              selection?.removeAllRanges();
              selection?.addRange(range);
              document.dispatchEvent(new Event('selectionchange'));
              return true;
            })();
            """);
        await Task.Delay(150);
        var rawButtonRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('libzora-selection-bar');
              const button = document.getElementById('lz-sel-highlight');
              const panel = document.getElementById('lz-sel-styles');
              if (!bar || !button || !panel) return null;
              if (!window.__libzoraValidationPointerInstalled) {
                window.__libzoraValidationPointerInstalled = true;
                window.__libzoraValidationMenuTrace = [];
                const trace = (label, event) => window.__libzoraValidationMenuTrace.push({
                  at: Math.round(performance.now()),
                  label,
                  target: event?.target?.id || event?.target?.dataset?.highlight || event?.target?.tagName || '',
                  related: event?.relatedTarget?.id || event?.relatedTarget?.dataset?.highlight || event?.relatedTarget?.tagName || '',
                  classes: panel.className
                });
                for (const type of ['mouseenter', 'mouseleave', 'mouseover', 'mouseout']) {
                  button.addEventListener(type, event => trace('button:' + type, event));
                  panel.addEventListener(type, event => trace('panel:' + type, event));
                }
                document.documentElement.addEventListener('mouseleave', event => trace('html:mouseleave', event));
                new MutationObserver(() => trace('panel:class', null))
                  .observe(panel, { attributes: true, attributeFilter: ['class'] });
                document.addEventListener('mousemove', event => {
                  window.__libzoraValidationPointer = {
                    x: event.clientX,
                    y: event.clientY,
                    target: event.target?.id || event.target?.dataset?.highlight || event.target?.tagName || ''
                  };
                }, true);
              }
              panel.classList.remove('open', 'above');
              bar.style.display = 'flex';
              bar.style.left = '120px';
              bar.style.top = '120px';
              const rect = button.getBoundingClientRect();
              return JSON.stringify({
                x: rect.left + rect.width / 2,
                y: rect.top + rect.height / 2,
                viewportWidth: document.documentElement.clientWidth || window.innerWidth || 1,
                viewportHeight: document.documentElement.clientHeight || window.innerHeight || 1
              });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawButtonRect), "selection highlight button rectangle missing");
        using var buttonDocument = JsonDocument.Parse(rawButtonRect!);
        var buttonRect = buttonDocument.RootElement;
        var topLeft = view.PointToScreen(new Avalonia.Point(0, 0));
        var scaling = Avalonia.Controls.TopLevel.GetTopLevel(view)?.RenderScaling ?? 1d;
        var width = Math.Max(1, view.Bounds.Width * scaling);
        var height = Math.Max(1, view.Bounds.Height * scaling);
        int ToScreenX(double x, double viewportWidth) =>
            topLeft.X + (int)Math.Round(x * width / Math.Max(1, viewportWidth));
        int ToScreenY(double y, double viewportHeight) =>
            topLeft.Y + (int)Math.Round(y * height / Math.Max(1, viewportHeight));
        var viewportWidth = buttonRect.GetProperty("viewportWidth").GetDouble();
        var viewportHeight = buttonRect.GetProperty("viewportHeight").GetDouble();
        Require(
            SetCursorPos(
                ToScreenX(buttonRect.GetProperty("x").GetDouble(), viewportWidth),
                ToScreenY(buttonRect.GetProperty("y").GetDouble(), viewportHeight)),
            "failed to position cursor over selection highlight button");
        await Task.Delay(300);
        Require(await IsKreaderHighlightMenuOpenAsync(host), "selection highlight submenu did not open on hover");

        var rawPanelRect = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            (() => {
              const panel = document.getElementById('lz-sel-styles');
              if (!panel) return null;
              const rect = panel.getBoundingClientRect();
              return JSON.stringify({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 });
            })();
            """));
        Require(!string.IsNullOrWhiteSpace(rawPanelRect), "selection highlight submenu rectangle missing");
        using var panelDocument = JsonDocument.Parse(rawPanelRect!);
        var panelRect = panelDocument.RootElement;
        Require(
            SetCursorPos(
                ToScreenX(panelRect.GetProperty("x").GetDouble(), viewportWidth),
                ToScreenY(panelRect.GetProperty("y").GetDouble(), viewportHeight)),
            "failed to position cursor inside selection highlight submenu");
        await Task.Delay(450);
        var remainedOpen = await IsKreaderHighlightMenuOpenAsync(host);
        if (!remainedOpen)
        {
            var diagnostic = DecodeReaderScriptString(await host.InvokeScriptAsync("""
                (() => {
                  const bar = document.getElementById('libzora-selection-bar');
                  const button = document.getElementById('lz-sel-highlight');
                  const panel = document.getElementById('lz-sel-styles');
                  const pointer = window.__libzoraValidationPointer || { x: -1, y: -1, target: '' };
                  const rect = element => {
                    const value = element?.getBoundingClientRect();
                    return value ? { left: value.left, top: value.top, right: value.right, bottom: value.bottom } : null;
                  };
                  return JSON.stringify({
                    pointer,
                    hit: document.elementFromPoint(pointer.x, pointer.y)?.outerHTML || '',
                    button: rect(button),
                    panel: rect(panel),
                    barDisplay: bar ? getComputedStyle(bar).display : '',
                    panelDisplay: panel ? getComputedStyle(panel).display : '',
                    selection: window.getSelection()?.toString() || '',
                    trace: window.__libzoraValidationMenuTrace || []
                  });
                })();
                """));
            await log.WriteLineAsync("DEBUG selection submenu dismissed: " + diagnostic);
        }
        Require(remainedOpen, "selection highlight submenu closed while pointer was inside it");

        Require(
            SetCursorPos(ToScreenX(20, viewportWidth), ToScreenY(20, viewportHeight)),
            "failed to position cursor outside selection highlight controls");
        await Task.Delay(400);
        Require(!await IsKreaderHighlightMenuOpenAsync(host), "selection highlight submenu stayed open after pointer left button and menu");
        await host.InvokeScriptAsync("""
            (() => {
              const bar = document.getElementById('libzora-selection-bar');
              document.getElementById('lz-sel-styles')?.classList.remove('open', 'above');
              if (bar) bar.style.display = 'none';
              window.getSelection()?.removeAllRanges();
            })();
            """);
        await log.WriteLineAsync("PASS Windows selection highlight submenu hover open, retain, dismiss");
    }

    private static async Task<bool> IsKreaderHighlightMenuOpenAsync(IReaderHost host)
    {
        var value = DecodeReaderScriptString(await host.InvokeScriptAsync("""
            document.getElementById('lz-sel-styles')?.classList.contains('open') ? 'open' : 'closed';
            """));
        return string.Equals(value, "open", StringComparison.Ordinal);
    }

    private async Task SetKreaderValidationLayoutAsync(int flowMode, bool twoPage, bool vertical = false)
    {
        _readerLayout = NormalizeReaderLayoutForPlatform(_readerLayout with
        {
            FlowMode = flowMode,
            TwoPageMode = twoPage,
            VerticalWriting = vertical
        });
        await ApplyReaderLayoutToHostsAsync(ReaderToken);
        await Task.Delay(250, ReaderToken);
    }

    private static async Task PauseKreaderValidationAtInitialPageAsync(TextWriter log)
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_INITIAL_PAUSE_MS"),
                out var milliseconds)
            || milliseconds <= 0)
        {
            return;
        }

        milliseconds = Math.Clamp(milliseconds, 1, 30_000);
        await log.WriteLineAsync($"DEBUG pausing on initial EPUB page for {milliseconds} ms");
        await Task.Delay(milliseconds);
    }

    private static async Task PauseKreaderValidationAtVerticalPageAsync(TextWriter log)
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_VERTICAL_PAUSE_MS"),
                out var milliseconds)
            || milliseconds <= 0)
        {
            return;
        }

        milliseconds = Math.Clamp(milliseconds, 1, 30_000);
        await log.WriteLineAsync($"SCREENSHOT_READY vertical page; pausing for {milliseconds} ms");
        await log.FlushAsync();
        await Task.Delay(milliseconds);
    }

    private async Task WaitForKreaderDocumentAsync(IReaderHost host)
    {
        await host.ReadyTask;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var metrics = await ReadKreaderDomMetricsAsync(host);
            if (metrics.TryGetProperty("ready", out var ready)
                && ready.ValueKind == JsonValueKind.True)
            {
                return;
            }
            await Task.Delay(100, ReaderToken);
        }
        throw new InvalidOperationException("Kreader document did not become ready.");
    }

    private async Task<JsonElement> WaitForKreaderMetricsTextAsync(
        IReaderHost host,
        string expectedText)
    {
        JsonElement last = default;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            last = await ReadKreaderDomMetricsAsync(host);
            if (last.GetProperty("text").GetString()?.Contains(expectedText, StringComparison.Ordinal) == true)
                return last;
            await Task.Delay(100, ReaderToken);
        }
        return last;
    }

    private async Task<JsonElement> ReadKreaderDomMetricsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const root = document.documentElement;
              const body = document.body;
              const el = document.scrollingElement || root;
              const style = body ? getComputedStyle(body) : null;
              return JSON.stringify({
                ready: !!window.__libzoraReaderBridgeInstalled && !!body,
                text: (body?.textContent || body?.innerText || '').slice(0, 4000),
                flowMode: Number(window.__libzoraReaderFlowMode || 0),
                twoPage: window.__libzoraReaderTwoPage === true,
                vertical: window.__libzoraReaderVertical === true,
                bodyDisplay: style?.display || '',
                bodyVisibility: style?.visibility || '',
                bodyOpacity: style?.opacity || '',
                columnCount: style?.columnCount || '',
                columnWidth: style?.columnWidth || '',
                columnGap: style?.columnGap || '',
                writingMode: style?.writingMode || '',
                lineHeight: style?.lineHeight || '',
                verticalPageStep: getComputedStyle(root).getPropertyValue('--libzora-vertical-page-step'),
                clientWidth: el?.clientWidth || 0,
                clientHeight: el?.clientHeight || 0,
                scrollWidth: el?.scrollWidth || 0,
                scrollHeight: el?.scrollHeight || 0,
                scrollLeft: el?.scrollLeft || 0,
                scrollTop: el?.scrollTop || 0,
                selectionBar: !!document.getElementById('libzora-selection-bar'),
                bookmarkCorner: !!document.getElementById('libzora-bookmark-corner'),
                footnoteLinks: document.querySelectorAll('a[epub\\:type*="noteref"], a[role*="doc-noteref"], a[href*="footnote"]').length,
                annotationMarks: document.querySelectorAll('.libzora-saved-annotation').length
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader metric script returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
#endif
