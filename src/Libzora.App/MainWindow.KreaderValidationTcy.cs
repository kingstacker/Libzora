#if DEBUG
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Libzora.Core;

namespace Libzora;

/// <summary>
/// TCY diagnostics and invariants.
/// </summary>
public partial class MainWindow
{
    private async Task<JsonElement> ReadKreaderTcyDiagnosticsAsync(IReaderHost host)
    {
        var result = await host.InvokeScriptAsync(
            """
            (() => {
              const body = document.body;
              if (!body) return null;
              const tolerance = 0.8;
              const runs = Array.from(body.querySelectorAll(
                '.libzora-tcy[data-libzora-vertical-run="1"], .libzora-tcy-all[data-libzora-vertical-run="1"]'));
              let missingInnerCount = 0;
              let outsideCount = 0;
              let maxCenterDelta = 0;
              let minInnerHeightRatio = Number.POSITIVE_INFINITY;
              let minInnerWidthRatio = Number.POSITIVE_INFINITY;
              let maxCellSizeError = 0;
              const items = [];
              const outsideItems = [];
              for (const run of runs) {
                const inner = run.querySelector(':scope > .libzora-tcy-inner');
                if (!inner) {
                  missingInnerCount++;
                  continue;
                }
                const outer = run.getBoundingClientRect();
                const ink = inner.getBoundingClientRect();
                const fontSize = parseFloat(getComputedStyle(run).fontSize) || 0;
                const centerDeltaX = Math.abs((outer.left + outer.width / 2) - (ink.left + ink.width / 2));
                const centerDeltaY = Math.abs((outer.top + outer.height / 2) - (ink.top + ink.height / 2));
                const centerDelta = Math.max(centerDeltaX, centerDeltaY);
                const innerHeightRatio = fontSize > 0 ? ink.height / fontSize : 0;
                const cellSizeError = fontSize > 0
                  ? Math.max(Math.abs(outer.width - fontSize), Math.abs(outer.height - fontSize))
                  : Number.POSITIVE_INFINITY;
                const outside = ink.left < outer.left - tolerance
                  || ink.right > outer.right + tolerance
                  || ink.top < outer.top - tolerance
                  || ink.bottom > outer.bottom + tolerance;
                if (outside) outsideCount++;
                if (outside && outsideItems.length < 16) {
                  outsideItems.push({
                    text: run.textContent,
                    className: run.className,
                    outer: [outer.left, outer.top, outer.width, outer.height].map(value => +value.toFixed(2)),
                    inner: [ink.left, ink.top, ink.width, ink.height].map(value => +value.toFixed(2)),
                    fontSize: +fontSize.toFixed(2),
                    fontWeight: getComputedStyle(inner).fontWeight,
                    fontStyle: getComputedStyle(inner).fontStyle
                  });
                }
                maxCenterDelta = Math.max(maxCenterDelta, centerDelta);
                minInnerHeightRatio = Math.min(minInnerHeightRatio, innerHeightRatio);
                minInnerWidthRatio = Math.min(
                  minInnerWidthRatio,
                  fontSize > 0 ? ink.width / fontSize : 0);
                maxCellSizeError = Math.max(maxCellSizeError, cellSizeError);
                if (items.length < 16) {
                  items.push({
                    text: run.textContent,
                    length: run.dataset.libzoraTcyLength || '',
                    outer: [outer.left, outer.top, outer.width, outer.height].map(value => +value.toFixed(2)),
                    inner: [ink.left, ink.top, ink.width, ink.height].map(value => +value.toFixed(2)),
                    fontSize: +fontSize.toFixed(2),
                    fontFamily: getComputedStyle(inner).fontFamily,
                    transform: getComputedStyle(inner).transform,
                    centerDelta: +centerDelta.toFixed(3),
                    innerHeightRatio: +innerHeightRatio.toFixed(3),
                    outside
                  });
                }
              }
              const threeDigitRuns = Array.from(body.querySelectorAll(
                '.libzora-vertical-number[data-libzora-vertical-run="1"]'))
                .filter(run => /^\d{3}$/.test(run.textContent || ''));
              // Single digits and ASCII punctuation cells must carry the
              // same centered inner box as tate-chu-yoko; baseline-positioned
              // ink drifts across the neighbouring CJK cell edge.
              const centeredCells = Array.from(body.querySelectorAll(
                '.libzora-vertical-digit[data-libzora-vertical-run="1"], '
                + '.libzora-vertical-punctuation[data-libzora-vertical-run="1"]'));
              let centeredMissingInnerCount = 0;
              let centeredOutsideCount = 0;
              let centeredMaxDelta = 0;
              let centeredDigitCount = 0;
              const centeredItems = [];
              for (const cell of centeredCells) {
                if (cell.classList.contains('libzora-vertical-digit')) centeredDigitCount++;
                const inner = cell.querySelector(':scope > .libzora-cell-inner');
                if (!inner) {
                  centeredMissingInnerCount++;
                  continue;
                }
                const outer = cell.getBoundingClientRect();
                const ink = inner.getBoundingClientRect();
                const dx = Math.abs((outer.left + outer.width / 2) - (ink.left + ink.width / 2));
                const dy = Math.abs((outer.top + outer.height / 2) - (ink.top + ink.height / 2));
                const delta = Math.max(dx, dy);
                centeredMaxDelta = Math.max(centeredMaxDelta, delta);
                const outside = ink.left < outer.left - tolerance
                  || ink.right > outer.right + tolerance
                  || ink.top < outer.top - tolerance
                  || ink.bottom > outer.bottom + tolerance;
                if (outside) {
                  centeredOutsideCount++;
                  if (centeredItems.length < 12) {
                    centeredItems.push({
                      text: cell.textContent,
                      className: cell.className,
                      outer: [outer.left, outer.top, outer.width, outer.height].map(v => +v.toFixed(2)),
                      inner: [ink.left, ink.top, ink.width, ink.height].map(v => +v.toFixed(2)),
                      delta: +delta.toFixed(3)
                    });
                  }
                }
              }
              let threeDigitOrientationErrorCount = 0;
              let minThreeDigitInlineRatio = Number.POSITIVE_INFINITY;
              let maxThreeDigitBlockRatio = 0;
              for (const run of threeDigitRuns) {
                const rect = run.getBoundingClientRect();
                const style = getComputedStyle(run);
                const fontSize = parseFloat(style.fontSize) || 0;
                if (style.textOrientation !== 'upright')
                  threeDigitOrientationErrorCount++;
                if (fontSize > 0) {
                  minThreeDigitInlineRatio = Math.min(
                    minThreeDigitInlineRatio,
                    rect.height / fontSize);
                  maxThreeDigitBlockRatio = Math.max(
                    maxThreeDigitBlockRatio,
                    rect.width / fontSize);
                }
              }
              return JSON.stringify({
                preparedVersion: body.dataset.libzoraVerticalInlinePrepared || '',
                count: runs.length,
                missingInnerCount,
                outsideCount,
                maxCenterDelta,
                minInnerHeightRatio: Number.isFinite(minInnerHeightRatio) ? minInnerHeightRatio : 0,
                minInnerWidthRatio: Number.isFinite(minInnerWidthRatio) ? minInnerWidthRatio : 0,
                maxCellSizeError,
                threeDigitCount: threeDigitRuns.length,
                threeDigitOrientationErrorCount,
                minThreeDigitInlineRatio: Number.isFinite(minThreeDigitInlineRatio)
                  ? minThreeDigitInlineRatio : 0,
                maxThreeDigitBlockRatio,
                centeredCellCount: centeredCells.length,
                centeredDigitCount,
                centeredMissingInnerCount,
                centeredOutsideCount,
                centeredMaxDelta,
                outsideItems,
                items,
                centeredItems
              });
            })();
            """);
        var raw = DecodeReaderScriptString(result) ?? result ?? "{}";
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Kreader tate-chu-yoko diagnostic returned empty result.");
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static void RequireReaderBodyVisible(JsonElement metrics, string stage)
    {
        Require(metrics.GetProperty("bodyDisplay").GetString() != "none", $"EPUB body display hidden after {stage}");
        Require(metrics.GetProperty("bodyVisibility").GetString() == "visible", $"EPUB body visibility hidden after {stage}");
        Require(metrics.GetProperty("bodyOpacity").GetString() == "1", $"EPUB body opacity hidden after {stage}");
    }

    // The Linux reader has two candidate surfaces: the native webview slot and
    // the plain-text recovery overlay. The native surface paints over Avalonia
    // content, so showing both at once hides the overlay behind a blank
    // webview — exactly the "blank page after opening a book" failure.
    private void RequireLinuxVisibleReaderSurface(string stage)
    {
        if (!OperatingSystem.IsLinux()) return;
        var overlay = ReaderLinuxTextFallbackOverlay.IsVisible;
        var webview = ReaderActiveHostSlot.IsVisible;
        Require(
            overlay || webview,
            $"Linux visible reader surface missing after {stage}");
        Require(
            !(overlay && webview),
            $"Linux reader shows the webview over the text overlay after {stage}");
    }

    private async Task SaveKreaderValidationAnnotationAsync()
    {
        var card = _readerBookCard
            ?? throw new InvalidOperationException("reader book card missing");
        var file = _readerBookFile
            ?? throw new InvalidOperationException("reader book file missing");
        _ = _readerDocument
            ?? throw new InvalidOperationException("reader document missing");
        var chapterPath = GetReaderChapterPath()
            ?? throw new InvalidOperationException("reader chapter path missing");
        var annotation = new ReaderAnnotation
        {
            Id = Guid.NewGuid(),
            BookId = card.Book.Id,
            BookFileId = file.Id,
            ChapterPath = chapterPath,
            StartOffset = 0,
            EndOffset = 32,
            SelectedText = "Linux validation paragraph 001",
            Prefix = string.Empty,
            Suffix = "search-token-linux",
            Note = "Linux validation note",
            Color = "#000000",
            UnderlineStyle = "solid",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _readerData.SaveAnnotationAsync(annotation, ReaderToken);
        await RefreshReaderAnnotationsAsync(ReaderToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Invariants that must hold on every vertical page whatever the content.
    /// The clipping and margin checks alone cannot see either of these: a page
    /// whose lines are all bottom aligned, or one whose over-long atomic run
    /// spills past the page bottom into the margin mask, both keep perfect
    /// left/right edges and symmetric margins.
    /// </summary>
    /// <param name="requireTopAlignedColumns">
    /// Only for content whose column layout is known. Real books legitimately
    /// place text below a figure or a centered heading, so the bottom-aligned
    /// column count is logged rather than asserted there.
    /// </param>
}
#endif
