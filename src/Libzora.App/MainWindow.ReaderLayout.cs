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
/// Reader layout application to hosts, scroll state capture and
/// diagnostics.
/// </summary>
public partial class MainWindow
{
    private async Task<ReaderScrollState?> CaptureReaderScrollStateAsync(IReaderHost host)
    {
        if (_readerIsPdf) return null;
        if (CaptureLinuxReaderTextFallbackState() is { } linuxState)
            return linuxState;
        if (host is IReaderPositionHost nativeReader)
        {
            var nativeState = nativeReader.GetScrollState();
            return new ReaderScrollState(
                nativeState.Position,
                nativeState.Ratio,
                nativeState.ScrollWidth,
                nativeState.ScrollHeight,
                nativeState.ClientWidth,
                nativeState.ClientHeight);
        }
        try
        {
            var result = await host.InvokeScriptAsync(
                "(() => { const el = document.scrollingElement || document.documentElement; if (!el) return null; return JSON.stringify({ left: el.scrollLeft || 0, top: el.scrollTop || 0, scrollWidth: el.scrollWidth || 0, scrollHeight: el.scrollHeight || 0, clientWidth: el.clientWidth || 0, clientHeight: el.clientHeight || 0 }); })();");
            var raw = DecodeReaderScriptString(result);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return null;

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var horizontal = _readerLayout.FlowMode == 1 || _readerLayout.VerticalWriting;
            var position = horizontal ? ReadDouble(root, "left") : ReadDouble(root, "top");
            if (_readerLayout.VerticalWriting)
                position = Math.Abs(position);
            var scrollWidth = ReadDouble(root, "scrollWidth");
            var scrollHeight = ReadDouble(root, "scrollHeight");
            var clientWidth = ReadDouble(root, "clientWidth");
            var clientHeight = ReadDouble(root, "clientHeight");
            var maximum = horizontal
                ? Math.Max(0, scrollWidth - clientWidth)
                : Math.Max(0, scrollHeight - clientHeight);
            var ratio = maximum > 0 ? Math.Clamp(position / maximum, 0, 1) : 0;
            return new ReaderScrollState(
                Math.Max(0, position),
                ratio,
                scrollWidth,
                scrollHeight,
                clientWidth,
                clientHeight);
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateReaderScrollStateAsync(IReaderHost host)
    {
        if (TryApplyLinuxReaderTextFallbackState()) return;
        if (host is IReaderPositionHost nativeReader)
        {
            var nativeState = nativeReader.GetScrollState();
            ApplyReaderScrollState(new ReaderScrollState(
                nativeState.Position,
                nativeState.Ratio,
                nativeState.ScrollWidth,
                nativeState.ScrollHeight,
                nativeState.ClientWidth,
                nativeState.ClientHeight));
            _readerCurrentFragment = nativeReader.GetCurrentNavigationFragment();
            SyncReaderTocSelectionWithReadingPosition();
            return;
        }

        var state = await CaptureReaderScrollStateAsync(host);
        if (state is null) return;
        ApplyReaderScrollState(state);
    }

    private async Task RestoreReaderScrollStateAsync(
        IReaderHost host,
        ReaderScrollState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (host is IReaderLocationHost nativeReader)
        {
            if (nativeReader.Vertical)
                nativeReader.ScrollToOffset((int)Math.Max(0, Math.Round(state.Position)));
            else
                nativeReader.SeekToPixelScroll(state.Position);
            await UpdateReaderScrollStateAsync(host);
        }
    }

    private async Task ApplyReaderLayoutToHostsAsync(CancellationToken cancellationToken)
    {
        await _readerLayoutGate.WaitAsync(cancellationToken);
        try
        {
            ResetReaderContinuousEdgeTracking();
            var currentHost = CurrentReaderHost;
            var scrollState = currentHost is not null
                ? await CaptureReaderScrollStateAsync(currentHost)
                : null;
            var hosts = new[] { _readerActiveHost, _readerPreloadHost }
                .Where(host => host is not null)
                .Cast<IReaderHost>()
                .Distinct()
                .ToArray();
            await Task.WhenAll(hosts.Select(host => ConfigureReaderHostAsync(host, cancellationToken)));
            if (IsLinuxReaderTextFallbackActive())
            {
                // The visible Linux vertical surface is Avalonia-owned, so
                // changing the CSS on the hidden WebView is not enough. Reuse
                // the current pagination anchor and rebuild the custom page
                // with the new indent policy immediately.
                var fallbackAnchor = _readerLayout.FlowMode == 1
                    ? CaptureLinuxReaderTextFallbackPageAnchor()
                    : null;
                if (_readerLayout.FlowMode == 1)
                    RebuildLinuxReaderTextFallbackPages(fallbackAnchor);
                else
                    UpdateLinuxReaderTextFallbackMode();
            }
            // NativeReaderHost preserves a semantic text/page anchor while it
            // recomposes. Its position is a character offset in vertical page
            // mode but a pixel offset in horizontal mode; replaying the old
            // numeric value here would reinterpret one coordinate system as
            // the other and reopen the wrong page after a layout switch.
            if (scrollState is not null
                && currentHost is not null
                && currentHost is not IReaderConfiguredHost
                && ReferenceEquals(CurrentReaderHost, currentHost))
            {
                await RestoreReaderScrollStateAsync(currentHost, scrollState, cancellationToken);
            }
            if (currentHost is not null && ReferenceEquals(CurrentReaderHost, currentHost))
            {
                await UpdateReaderScrollStateAsync(currentHost);
                PrimeReaderContinuousEdgeTracking();
                ScheduleReaderBookPageCountRefresh();
            }
            if (!_readerIsPdf
                && currentHost is not null
                && ReferenceEquals(CurrentReaderHost, currentHost)
                && ReaderInPageSearchBar.IsVisible
                && !string.IsNullOrWhiteSpace(ReaderInPageSearchBox.Text))
            {
                var previousSearchIndex = _readerSearchIndex;
                var searchSequence = ++_readerSearchSequence;
                await ApplyReaderSearchAsync(
                    ReaderInPageSearchBox.Text.Trim(),
                    searchSequence,
                    navigate: false);
                if (_readerSearchCount > 0)
                {
                    _readerSearchIndex = Math.Clamp(previousSearchIndex, 0, _readerSearchCount - 1);
                    await NavigateReaderSearchAsync(_readerSearchIndex, searchSequence);
                }
            }
            UpdateReaderToolbar();
        }
        finally
        {
            _readerLayoutGate.Release();
        }
    }

    private void ReaderWebViewHost_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsLinuxReaderTextFallbackActive())
        {
            ScheduleLinuxReaderTextFallbackReflow();
            return;
        }

        ScheduleReaderRelayout();
    }

    private void ScheduleLinuxReaderTextFallbackReflow()
    {
        if (!IsLinuxReaderTextFallbackActive()) return;
        if (_readerLayout.FlowMode == 1
            && _readerLinuxTextFallbackPendingReflowAnchor is null)
        {
            _readerLinuxTextFallbackPendingReflowAnchor =
                CaptureLinuxReaderTextFallbackPageAnchor();
        }

        var sequence = ++_readerLinuxTextFallbackReflowSequence;
        _ = ObserveReaderTaskAsync(RunLinuxReaderTextFallbackReflowAsync(sequence));
    }

    private async Task RunLinuxReaderTextFallbackReflowAsync(int sequence)
    {
        // Column visibility changes can raise SizeChanged before Avalonia has
        // committed the new reader bounds. Waiting through that layout burst
        // prevents a 932px page from being centered inside a 572px slot and
        // painting underneath the TOC.
        await Task.Delay(80);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (sequence != _readerLinuxTextFallbackReflowSequence
                || !IsLinuxReaderTextFallbackActive())
            {
                return;
            }

            UpdateLinuxReaderTextFallbackImageSizes();
            // Selection must remain paint-only; never change wrap points
            // while the pointer is dragging a selection boundary.
            var selectionInProgress = _readerLinuxTextFallbackPointerPressed
                || !string.IsNullOrWhiteSpace(_readerPendingSelection);
            if (selectionInProgress)
            {
                _readerLinuxTextFallbackPendingReflowAnchor = null;
                return;
            }

            UpdateLinuxReaderTextFallbackBlockWidths();
            if (_readerLayout.FlowMode == 1)
            {
                var anchor = _readerLinuxTextFallbackPendingReflowAnchor;
                _readerLinuxTextFallbackPendingReflowAnchor = null;
                RebuildLinuxReaderTextFallbackPages(anchor);
            }
            SyncLinuxReaderTextFallbackState(saveProgress: false);
        });
    }

    // A native webview can trail Avalonia's Grid by several compositor frames
    // while a TOC/assistant column or zen mode changes size. Reflow only after
    // Chromium reports the same viewport as its host, then restore the reading
    // position captured before the resize reports can overwrite it.
    private void ScheduleReaderRelayout()
    {
        if (_readerIsPdf
            || !ReaderRoot.IsVisible
            || _readerNavigationCancellation is not null
            || CurrentReaderHost is not { } currentHost)
            return;

        if (!ReferenceEquals(_readerPendingRelayoutHost, currentHost))
        {
            _readerPendingRelayoutHost = currentHost;
            _readerPendingRelayoutState = CreateTrackedReaderScrollState();
        }
        else if (_readerPendingRelayoutState is null)
        {
            _readerPendingRelayoutState = CreateTrackedReaderScrollState();
        }

        _readerRelayoutCancellation?.Cancel();
        _readerRelayoutCancellation?.Dispose();
        _readerRelayoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _readerSession.GetTokenOr(CancellationToken.None));
        var cancellation = _readerRelayoutCancellation;
        var token = cancellation.Token;
        _ = ObserveReaderTaskAsync(RunScheduledReaderRelayoutAsync(cancellation, token));
    }

    private ReaderScrollState? CreateTrackedReaderScrollState()
    {
        if (_readerClientWidth <= 0 || _readerClientHeight <= 0) return null;
        return new ReaderScrollState(
            Math.Max(0, _readerScrollPosition),
            Math.Clamp(_readerScrollRatio, 0, 1),
            Math.Max(0, _readerScrollWidth),
            Math.Max(0, _readerScrollHeight),
            _readerClientWidth,
            _readerClientHeight);
    }

    private async Task RunScheduledReaderRelayoutAsync(
        CancellationTokenSource cancellation,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
            var host = _readerPendingRelayoutHost;
            var state = _readerPendingRelayoutState;
            if (host is null || !ReferenceEquals(CurrentReaderHost, host)) return;

            var converged = await WaitForReaderViewportToMatchHostAsync(host, token);
            token.ThrowIfCancellationRequested();
            await ApplyReaderViewportToHostsAsync(token, host, state);

            // Some WebView2 builds settle after the bounded first wait. A
            // second pass is required only when the native viewport lagged.
            if (!converged)
            {
                await Task.Delay(200, token);
                if (await WaitForReaderViewportToMatchHostAsync(host, token))
                {
                    token.ThrowIfCancellationRequested();
                    await ApplyReaderViewportToHostsAsync(token, host, state);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_readerRelayoutCancellation, cancellation))
            {
                _readerPendingRelayoutHost = null;
                _readerPendingRelayoutState = null;
            }
        }
    }

    private async Task ApplyReaderViewportToHostsAsync(
        CancellationToken cancellationToken,
        IReaderHost capturedHost,
        ReaderScrollState? capturedState)
    {
        // The self-drawn surface recomposes on resize by itself; the PDF
        // webview viewer scales its own page.
        await Task.CompletedTask;
    }

    private async Task<bool> WaitForReaderViewportToMatchHostAsync(
        IReaderHost host,
        CancellationToken token)
    {
        if (host is IReaderConfiguredHost)
        {
            // The self-drawn surface sizes from Avalonia bounds directly; the
            // relayout timer handles any late resize.
            return true;
        }

        const int maximumAttempts = 10;
        const double tolerance = 2;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(CurrentReaderHost, host)) return false;
            var expectedWidth = ReaderWebViewHost.Bounds.Width;
            var expectedHeight = ReaderWebViewHost.Bounds.Height;
            if (expectedWidth <= 0 || expectedHeight <= 0) return false;

            try
            {
                var result = await host.InvokeScriptAsync(
                    "(() => JSON.stringify({ width: window.innerWidth || document.documentElement.clientWidth || 0, height: window.innerHeight || document.documentElement.clientHeight || 0 }))();");
                var raw = DecodeReaderScriptString(result);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    using var document = JsonDocument.Parse(raw);
                    var root = document.RootElement;
                    var viewportWidth = ReadDouble(root, "width");
                    var viewportHeight = ReadDouble(root, "height");
                    if (Math.Abs(viewportWidth - expectedWidth) <= tolerance
                        && Math.Abs(viewportHeight - expectedHeight) <= tolerance)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Navigation can briefly make the document unavailable.
            }

            await Task.Delay(40, token);
        }

        return false;
    }

    private async Task WriteReaderLayoutDiagnosticsAsync(string stage, IReaderHost host)
    {
#if DEBUG
        if (_readerIsPdf) return;
        try
        {
            var result = await host.InvokeScriptAsync(
                $$"""
                (() => {
                  const root = document.documentElement;
                  const body = document.body;
                  const el = document.scrollingElement || root;
                  if (!root || !body || !el) return null;
                  const rootStyle = getComputedStyle(root);
                  const bodyStyle = getComputedStyle(body);
                  const textNode = Array.from(body.querySelectorAll('h1,h2,h3,h4,h5,h6,p,li,blockquote,td,th,div,span'))
                    .find(node => (node.textContent || '').trim().length > 0);
                  const textStyle = textNode ? getComputedStyle(textNode) : null;
                  const textRect = textNode ? textNode.getBoundingClientRect() : null;
                  const fontProbe = document.createElement('span');
                  fontProbe.textContent = '祖母的退化论有人挠';
                  fontProbe.style.cssText = 'position: fixed; left: -10000px; top: -10000px; white-space: nowrap; font-size: 48px; font-weight: 400; letter-spacing: 0;';
                  body.appendChild(fontProbe);
                  const measureFont = family => {
                    fontProbe.style.setProperty('font-family', family, 'important');
                    return fontProbe.getBoundingClientRect().width || 0;
                  };
                  const fontProbeWidths = {
                    bundled: measureFont('"LibzoraKingHwaOldSong"'),
                    notoSans: measureFont('"Noto Sans CJK SC"'),
                    notoSerif: measureFont('"Noto Serif CJK SC"'),
                    sans: measureFont('sans-serif')
                  };
                  fontProbe.remove();
                  const vertical = bodyStyle.writingMode === 'vertical-rl';
                  let verticalGeometry = null;
                  if (vertical) {
                    const viewport = el.clientWidth || root.clientWidth || window.innerWidth || 0;
                    const verticalPageStep = parseFloat(
                      rootStyle.getPropertyValue('--libzora-vertical-page-step')) || 0;
                    const originShift = parseFloat(
                      rootStyle.getPropertyValue('--libzora-vertical-origin-shift')) || 0;
                    const trailingExtent = parseFloat(
                      rootStyle.getPropertyValue('--libzora-vertical-trailing-extent')) || 0;
                    const leftSide = Math.max(0, (parseFloat(bodyStyle.paddingLeft) || 0) - trailingExtent);
                    const rightSide = parseFloat(bodyStyle.paddingRight) || 0;
                    const baseSide = (leftSide + rightSide) / 2;
                    const safeLeft = viewport - verticalPageStep - baseSide + originShift;
                    const safeRight = viewport - baseSide + originShift;
                    const tolerance = 0.75;
                    const partialGlyphs = [];
                    let glyphCount = 0;
                    let inspectedCharacters = 0;
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
                    verticalGeometry = {
                      viewport,
                      pageStep: verticalPageStep,
                      originShift,
                      safeLeft,
                      safeRight,
                      leftMargin: safeLeft,
                      rightMargin: viewport - safeRight,
                      marginDelta: Math.abs(safeLeft - (viewport - safeRight)),
                      lineHeight: parseFloat(bodyStyle.lineHeight) || 0,
                      glyphCount,
                      inspectedCharacters,
                      partialGlyphCount: partialGlyphs.length,
                      partialGlyphs: partialGlyphs.slice(0, 12)
                    };
                  }
                  return JSON.stringify({
                    innerWidth: window.innerWidth || 0,
                    visualWidth: window.visualViewport?.width || 0,
                    rootFontSize: rootStyle.fontSize,
                    bodyFontSize: bodyStyle.fontSize,
                    blockFontSize: textStyle ? textStyle.fontSize : '',
                    blockLineHeight: textStyle ? textStyle.lineHeight : '',
                    blockTag: textNode ? (textNode.closest('p,div,li,blockquote,td,th')?.tagName || textNode.tagName) : '',
                    paragraphCount: body.querySelectorAll('p').length,
                    firstParagraphFontSize: (() => {
                      const p = body.querySelector('p');
                      return p ? getComputedStyle(p).fontSize + '/' + getComputedStyle(p).lineHeight : '';
                    })(),
                    compatCellCount: body.querySelectorAll(
                      '.libzora-linux-vertical-single, .libzora-linux-vertical-tcy, '
                        + '.libzora-linux-vertical-number, .libzora-linux-vertical-cjk, '
                        + '.libzora-linux-vertical-pair-punctuation, .libzora-linux-vertical-single-punctuation, '
                        + '.libzora-linux-vertical-footnote').length,
                    firstCellBox: (() => {
                      const cell = body.querySelector(
                        '.libzora-linux-vertical-single, .libzora-linux-vertical-pair-punctuation, '
                          + '.libzora-linux-vertical-cjk');
                      if (!cell) return null;
                      const style = getComputedStyle(cell);
                      const rect = cell.getBoundingClientRect();
                      return style.writingMode + ' ' + style.display + ' w' + rect.width.toFixed(1)
                        + ' h' + rect.height.toFixed(1) + ' lh' + style.lineHeight;
                    })(),
                    rootClientWidth: root.clientWidth || 0,
                    clientWidth: el.clientWidth || 0,
                    clientHeight: el.clientHeight || 0,
                    scrollWidth: el.scrollWidth || 0,
                    scrollHeight: el.scrollHeight || 0,
                    scrollLeft: el.scrollLeft || 0,
                    scrollTop: el.scrollTop || 0,
                    pageStep: el.clientWidth || root.clientWidth || 0,
                    vertical,
                    verticalGeometry,
                    columnCount: bodyStyle.columnCount,
                    columnWidth: bodyStyle.columnWidth,
                    columnGap: bodyStyle.columnGap,
                    paddingLeft: bodyStyle.paddingLeft,
                    paddingRight: bodyStyle.paddingRight,
                    bodyTextLength: (body.innerText || body.textContent || '').trim().length,
                    bodyDisplay: bodyStyle.display,
                    bodyVisibility: bodyStyle.visibility,
                    bodyOpacity: bodyStyle.opacity,
                    bodyColor: bodyStyle.color,
                    bodyFontFamily: bodyStyle.fontFamily,
                    textFontFamily: textStyle?.fontFamily || '',
                    bundledFontLoaded: document.fonts?.check('1em "LibzoraKingHwaOldSong"', '祖母的退化论有人挠') === true,
                    fontStatus: document.fonts?.status || '',
                    fontProbeWidths,
                    textTag: textNode?.tagName || '',
                    textClass: textNode?.className || '',
                    textSample: (textNode?.textContent || '').trim().slice(0, 80),
                    textDisplay: textStyle?.display || '',
                    textVisibility: textStyle?.visibility || '',
                    textOpacity: textStyle?.opacity || '',
                    textColor: textStyle?.color || '',
                    textFill: textStyle?.webkitTextFillColor || '',
                    textRect: textRect
                      ? {
                          x: textRect.x,
                          y: textRect.y,
                          width: textRect.width,
                          height: textRect.height
                        }
                      : null
                  });
                })();
                """);
            var raw = DecodeReaderScriptString(result);
            Directory.CreateDirectory(_paths.Logs);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now,
                stage,
                chapter = _readerChapterIndex,
                flowMode = _readerLayout.FlowMode,
                twoPage = _readerLayout.TwoPageMode,
                hostWidth = ReaderWebViewHost.Bounds.Width,
                hostHeight = ReaderWebViewHost.Bounds.Height,
                renderScaling = TopLevel.GetTopLevel(ReaderWebViewHost)?.RenderScaling ?? 1d,
                dom = raw
            });
            await File.AppendAllTextAsync(
                Path.Combine(_paths.Logs, "reader-layout-debug.log"),
                entry + Environment.NewLine);
        }
        catch
        {
            // Diagnostics must never affect reading or navigation.
        }
#else
        await Task.CompletedTask;
#endif
    }
}
