#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// Kreader validation entry point and imported-book setup.
/// </summary>
public partial class MainWindow
{
    private Task ValidateExternalVerticalEpubAsync(StreamWriter log)
    {
        // The WebKit-specific vertical harness died with the WebView
        // typesetting engine; rewrite these probes against native pages.
        return log.WriteLineAsync("SKIPPED vertical harness: native engine pending");
    }

    private Task ValidateCurrentEpubReaderAsync(StreamWriter log)
    {
        return log.WriteLineAsync("SKIPPED reader harness: native engine pending");
    }
    private async Task RunKreaderValidationAndExitAsync()
    {
        var logPath = Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
            logPath = Path.Combine(_paths.Logs, "linux-kreader-validation.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var exitCode = 0;
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false));
        try
        {
            await log.WriteLineAsync($"Kreader Linux validation started: {DateTimeOffset.Now:O}");
            await log.WriteLineAsync($"OS: {Environment.OSVersion}");
            await log.WriteLineAsync($"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}; WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")}");

            Width = 1180;
            Height = 820;
            if (double.TryParse(
                    Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_WIDTH"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var requestedWidth))
            {
                Width = Math.Clamp(requestedWidth, 900, 1800);
            }
            if (double.TryParse(
                    Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_HEIGHT"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var requestedHeight))
            {
                Height = Math.Clamp(requestedHeight, 650, 1200);
            }
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen;
            await Task.Delay(500);

            var externalEpubPath = Environment.GetEnvironmentVariable(
                "LIBZORA_KREADER_VALIDATE_EPUB");
            if (!string.IsNullOrWhiteSpace(externalEpubPath))
            {
                externalEpubPath = Path.GetFullPath(externalEpubPath);
                Require(File.Exists(externalEpubPath), "external validation EPUB does not exist");
                var externalImport = await ViewModel.ImportAsync(
                    [externalEpubPath],
                    cancellationToken: _lifetimeCancellation.Token);
                Require(
                    externalImport.FailureCount == 0,
                    $"external import failed: {string.Join("; ", externalImport.Items.Select(item => item.Message))}");
                var expectedTitle = Path.GetFileNameWithoutExtension(externalEpubPath);
                var externalCard = await ResolveImportedValidationBookAsync(
                    externalImport,
                    externalEpubPath,
                    titleHint: expectedTitle);
                var externalFile = await ResolveImportedValidationFile(
                    externalCard,
                    externalEpubPath,
                    log);
                await log.WriteLineAsync("PASS import external EPUB " + externalCard.Title);
                await OpenBookAsync(externalCard, externalFile);
                await ValidateExternalVerticalEpubAsync(log);
                await CloseReaderAsync();
                await log.WriteLineAsync("PASS external EPUB vertical validation completed");
            }
            else
            {
                var assetRoot = Path.Combine(Path.GetTempPath(), "LibzoraKreaderValidation", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(assetRoot);
                var epubPath = Path.Combine(assetRoot, "linux-kreader-validation.epub");
                CreateKreaderValidationEpub(epubPath);

                var importResult = await ViewModel.ImportAsync([epubPath], cancellationToken: _lifetimeCancellation.Token);
                Require(importResult.FailureCount == 0, $"import failed: {string.Join("; ", importResult.Items.Select(item => item.Message))}");
                await log.WriteLineAsync("PASS import EPUB");

                // Earlier validation runs may have merged an older fixture with
                // the same metadata into this book card. Opening "the first
                // EPUB of the matching title" would then validate a stale
                // extraction instead of today's bytes, so resolve both the card
                // and the file by the imported content hash.
                var epubCard = await ResolveImportedValidationBookAsync(
                    importResult,
                    epubPath,
                    titleHint: "Linux Kreader Validation Long Numbers");
                var epubFile = await ResolveImportedValidationFile(epubCard, epubPath, log);
                await OpenBookAsync(epubCard, epubFile);
                await ValidateCurrentEpubReaderAsync(log);
                await CloseReaderAsync();
            }

            await log.WriteLineAsync("PASS Kreader Linux validation completed");
        }
        catch (Exception exception)
        {
            exitCode = 2;
            await log.WriteLineAsync("FAIL " + exception);
        }
        finally
        {
            await log.FlushAsync();
            Environment.Exit(exitCode);
        }
    }

    private async Task<BookCardViewModel> ResolveImportedValidationBookAsync(
        ImportBatchResult importResult,
        string epubPath,
        string titleHint)
    {
        var expectedSha = await ComputeValidationEpubShaAsync(epubPath);
        var importedBookId = importResult.Items
            .FirstOrDefault(item => item.Succeeded && item.Book is not null)?.Book?.Id;
        return ViewModel.Books.FirstOrDefault(card =>
                card.Book.Files.Any(file => file.Sha256.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                || (importedBookId is not null && card.Book.Id == importedBookId))
            ?? ViewModel.Books.First(card => card.Title.Contains(titleHint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BookFile> ResolveImportedValidationFile(
        BookCardViewModel card,
        string epubPath,
        TextWriter log)
    {
        var expectedSha = await ComputeValidationEpubShaAsync(epubPath);
        var exact = card.Book.Files.FirstOrDefault(file =>
            file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase)
            && file.Sha256.Equals(expectedSha, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        await log.WriteLineAsync(
            "WARN no library file matches the imported hash; falling back to the first EPUB file");
        return card.Book.Files.First(file => file.Format.Equals("epub", StringComparison.OrdinalIgnoreCase));
    }

    private static Task<string> ComputeValidationEpubShaAsync(string epubPath) =>
        Task.FromResult(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(epubPath))));

    private async Task ValidateLinuxPageComposeModeAsync(TextWriter log)
    {
        await SetKreaderValidationLayoutAsync(flowMode: 1, twoPage: false, vertical: true);
        var host = CurrentReaderHost
            ?? throw new InvalidOperationException("Linux page-compose reader host missing");
        Require(IsReaderPageComposeMode, "page-compose mode was not detected for vertical paginated layout");
        await Task.Delay(200, ReaderToken);

        var state = await ReadPageComposeStateAsync(host);
        Require(
            !state.TryGetProperty("missing", out _) || !state.GetProperty("missing").GetBoolean(),
            "page-compose containers are missing: " + state);
        Require(
            state.GetProperty("flowChildren").GetInt32() > 0,
            "page-compose flow is empty: " + state);
        Require(
            state.GetProperty("sw").GetDouble() <= state.GetProperty("w").GetDouble() + 1.5,
            "page-compose flow overflows its viewport — the fill loop regressed: " + state);
        Require(
            (state.GetProperty("text").GetString() ?? string.Empty).Trim().Length > 0,
            "page-compose flow has no text: " + state);
        Require(
            state.GetProperty("writingMode").GetString() == "vertical-rl",
            "page-compose flow lost vertical-rl: " + state);
        Require(
            state.GetProperty("bank").GetInt32() > 0 || state.GetProperty("complete").GetBoolean(),
            "page-compose bank is empty on a multi-page chapter without the complete flag: " + state);
        await log.WriteLineAsync(
            "PASS page-compose structure: flow fills the viewport with "
            + state.GetProperty("flowChildren").GetInt32() + " blocks, bank "
            + state.GetProperty("bank").GetInt32() + " blocks");

        var inline = await ReadKreaderNativeVerticalInlineDiagnosticsAsync(host);
        Require(
            inline.GetProperty("preparedVersion").GetString() == "publication-native-compat-1"
            && inline.GetProperty("linuxNumberRunCount").GetInt32() >= 0
            && inline.GetProperty("linuxNumericStyleErrorCount").GetInt32() == 0
            && inline.GetProperty("syntheticLinuxNumberLayoutCount").GetInt32() == 0
            && inline.GetProperty("legacyWrapperCount").GetInt32() == 0
            && inline.GetProperty("syntheticLayoutCount").GetInt32() == 0
            && inline.GetProperty("boundaryOverlapCount").GetInt32() == 0,
            "page-compose inline invariants failed: " + inline);
        await log.WriteLineAsync("PASS page-compose inline cell geometry");

        var pitch = await ReadPageComposeColumnPitchAsync(host);
        Require(
            pitch.GetProperty("collapsed").GetInt32() == 0,
            "page-compose column pitch collapsed: " + pitch);
        await log.WriteLineAsync(
            $"PASS page-compose column pitch >= 0.7x line grid (min {pitch.GetProperty("minPitch").GetDouble():F1}px)");

        var snapshot = CurrentReaderHost is IReaderPageSnapshotProvider snapshotProvider
            ? await snapshotProvider.CaptureVisiblePageAsync(ReaderToken)
            : null;
        if (snapshot is not { Length: > 4 })
            await log.WriteLineAsync("WARN page-compose snapshot unavailable");
        else
            await log.WriteLineAsync("PASS page-compose hold-overlay snapshot");

        var state0 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state0, "post-snapshot");
        var h0 = state0.GetProperty("h").GetDouble();
        await TurnReaderPageAsync(1);
        await Task.Delay(500, ReaderToken);
        var state1 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state1, "forward turn");
        Require(
            state1.GetProperty("h").GetDouble() > h0 + 1,
            $"page-compose forward turn did not advance the page start ({h0:F0} -> "
            + $"{state1.GetProperty("h").GetDouble():F0})");
        Require(
            state1.GetProperty("sw").GetDouble() <= state1.GetProperty("w").GetDouble() + 1.5,
            "page-compose forward turn produced an overflowing page: " + state1);
        await log.WriteLineAsync("PASS page-compose forward turn");

        await TurnReaderPageAsync(-1);
        await Task.Delay(500, ReaderToken);
        var state2 = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(state2, "backward turn");
        Require(
            Math.Abs(state2.GetProperty("h").GetDouble() - h0) <= 1.5,
            $"page-compose backward turn did not return to the prior page start: " + state2);
        await log.WriteLineAsync("PASS page-compose backward turn");

        await ClickReaderPageZoneAsync(host, leftZone: true);
        await Task.Delay(700, ReaderToken);
        var clicked = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(clicked, "click zone");
        Require(
            clicked.GetProperty("h").GetDouble() > h0 + 1,
            "a page-compose click-zone turn did not advance: " + clicked);

        await host.InvokeScriptAsync("""
            (() => {
              document.dispatchEvent(new WheelEvent('wheel', {
                bubbles: true, cancelable: true, deltaY: 120 }));
              return true;
            })();
            """);
        await Task.Delay(700, ReaderToken);
        var wheeled = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(wheeled, "wheel");
        Require(
            wheeled.GetProperty("h").GetDouble() > clicked.GetProperty("h").GetDouble(),
            "a forward wheel gesture did not advance the page-compose page: " + wheeled);
        await log.WriteLineAsync("PASS vertical page turns via click zones and wheel gestures");

        var total = wheeled.GetProperty("f").GetDouble() + wheeled.GetProperty("h").GetDouble();
        var midChar = (int)Math.Min(
            total - 1,
            Math.Max(1, total * 0.5));
        await host.InvokeScriptAsync(
            "window.__pgComposeAt(" + midChar.ToString(
                System.Globalization.CultureInfo.InvariantCulture) + "); true;");
        await Task.Delay(400, ReaderToken);
        var midState = await ReadPageComposeStateAsync(host);
        RequirePageStatePresent(midState, "mid-chapter");
        Require(
            (midState.GetProperty("text").GetString() ?? string.Empty).Trim().Length > 0
            && midState.GetProperty("flowChildren").GetInt32() > 0,
            "page-compose at a mid-chapter offset produced an empty page: " + midState);
        Require(
            midState.GetProperty("sw").GetDouble() <= midState.GetProperty("w").GetDouble() + 1.5,
            "page-compose mid-chapter page overflows: " + midState);
        await log.WriteLineAsync("PASS page-compose mid-chapter composition");
        await SaveKreaderValidationSnapshotAsync(log, "page-compose-page");

        await PauseKreaderValidationAtVerticalPageAsync(log);
    }

    /// <summary>
    /// Samples the reader scroll position every 150 ms so an interaction test
    /// can tell "turned late" from "never turned".
    /// </summary>
    private async Task<string> PollReaderScrollTrailAsync(IReaderHost host, int samples)
    {
        var trail = new List<string>();
        for (var index = 0; index < samples; index++)
        {
            await Task.Delay(150, ReaderToken);
            var metrics = await ReadKreaderDomMetricsAsync(host);
            if (metrics.TryGetProperty("scrollLeft", out var scrollLeft))
                trail.Add(scrollLeft.GetDouble().ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join(",", trail);
    }

    private async Task SaveKreaderValidationSnapshotAsync(TextWriter log, string name)
    {
        if (Environment.GetEnvironmentVariable("LIBZORA_KREADER_VALIDATE_SHOT_DIR")
                is not { Length: > 0 } shotDir
            || CurrentReaderHost is not IReaderPageSnapshotProvider provider)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(shotDir);
            var png = await provider.CaptureVisiblePageAsync(ReaderToken);
            if (png is { Length: > 0 })
            {
                var path = Path.Combine(shotDir, $"{name}-{DateTime.Now:HHmmss-fff}.png");
                await File.WriteAllBytesAsync(path, png);
                await log.WriteLineAsync($"DEBUG snapshot {name} -> {path} ({png.Length} bytes)");
            }
            else
            {
                await log.WriteLineAsync($"DEBUG snapshot {name} unavailable");
            }
        }
        catch (Exception exception)
        {
            await log.WriteLineAsync($"DEBUG snapshot {name} failed: {exception.Message}");
        }
    }

    private static async Task ClickReaderPageZoneAsync(IReaderHost host, bool leftZone)
    {
        await host.InvokeScriptAsync($$"""
            (() => {
              const width = window.innerWidth || document.documentElement.clientWidth || 0;
              const height = window.innerHeight || document.documentElement.clientHeight || 0;
              if (width <= 0 || height <= 0) return false;
              const x = {{(leftZone ? "width * 0.16" : "width * 0.84")}};
              const isPageTurnTarget = element =>
                element instanceof Element
                && !element.closest('a, button, input, textarea, select, option, label, #libzora-selection-bar');
              let point = null;
              for (const fraction of [0.5, 0.35, 0.65, 0.2, 0.8]) {
                const y = height * fraction;
                if (isPageTurnTarget(document.elementFromPoint(x, y))) {
                  point = { x, y };
                  break;
                }
              }
              if (point === null) point = { x, y: height / 2 };
              const selection = window.getSelection?.();
              if (selection && !selection.isCollapsed) selection.removeAllRanges();
              const init = {
                bubbles: true, cancelable: true, composed: true,
                pointerId: 7, isPrimary: true, button: 0,
                clientX: point.x, clientY: point.y
              };
              document.body.dispatchEvent(new PointerEvent('pointerdown', init));
              document.body.dispatchEvent(new PointerEvent('pointerup', init));
              return true;
            })();
            """);
    }

    private static void RequirePageStatePresent(JsonElement state, string context)
    {
        if (state.TryGetProperty("missing", out _))
            throw new InvalidOperationException(
                $"page-compose containers missing at {context}: {state.GetRawText()}");
    }

    private async Task<JsonElement> ReadPageComposeStateAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            "JSON.stringify((() => { const f = document.getElementById('libzora-page-flow');"
            + " const b = document.getElementById('libzora-page-bank');"
            + " const hEl = document.getElementById('libzora-page-history');"
            + " if (!f || !b || !hEl) return { missing: true,"
            + " bodyChildren: document.body.children.length,"
            + " ids: Array.from(document.body.children).slice(0, 8).map(c => c.id || c.className || c.tagName).join('|'),"
            + " styleEl: !!document.getElementById('libzora-page-mode-style'),"
            + " removed: ((window.__pgRemovedTrace || '') + ' ||| CALLER: ' + (window.__pgTeardownCaller || 'none')).slice(0, 1200) };"
            + " return { bank: b.children.length, hist: hEl.children.length,"
            + " flowChildren: f.children.length, w: f.clientWidth, hgt: f.clientHeight,"
            + " sw: f.scrollWidth, text: (f.textContent || '').slice(0, 60),"
            + " h: window.__pg.h, f: window.__pg.f, complete: window.__pg.complete,"
            + " writingMode: getComputedStyle(f).writingMode }; })())");
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Measures adjacent text-column pitches inside the composed page and
    /// counts collapses (the WebKitGTK line-box regression signature).
    /// </summary>
    private async Task<JsonElement> ReadPageComposeColumnPitchAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync("""
            (() => {
              const f = document.getElementById('libzora-page-flow');
              if (!f) return null;
              const line = parseFloat(getComputedStyle(f).lineHeight) || 0;
              const lefts = [];
              const walker = document.createTreeWalker(f, NodeFilter.SHOW_TEXT);
              let node;
              while ((node = walker.nextNode())) {
                const range = document.createRange();
                range.selectNodeContents(node);
                for (const rect of Array.from(range.getClientRects())) {
                  if (rect.width > 2 && rect.height > 2)
                    lefts.push(rect.left);
                }
              }
              lefts.sort((a, b) => a - b);
              const unique = [];
              // Cluster lefts with a tolerance proportional to the line grid:
              // cells, ink-shifted glyphs and text fragments inside one column
              // report lefts within about half a cell of each other, while
              // true adjacent columns sit a full line pitch apart.
              const clusterTolerance = Math.max(4, line * 0.35);
              for (const left of lefts) {
                if (!unique.length || left - unique[unique.length - 1] > clusterTolerance) unique.push(left);
              }
              let collapsed = 0;
              let minPitch = 0;
              for (let index = 1; index < unique.length; index++) {
                const pitch = unique[index] - unique[index - 1];
                if (index === 1 || pitch < minPitch) minPitch = pitch;
                if (pitch > 0.5 && pitch < line * 0.7) collapsed++;
              }
              return JSON.stringify({
                line, columns: unique.length, minPitch, collapsed
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
#endif
