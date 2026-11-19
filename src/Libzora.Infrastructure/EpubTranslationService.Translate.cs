using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Page reading and segment translation.
/// </summary>
public sealed partial class EpubTranslationService : IEpubTranslationService
{
    private async Task<IReadOnlyList<TranslationPage>> ReadTranslationPagesAsync(
        string sourcePath,
        IProgress<BookTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pages = new List<TranslationPage>();
        using var archive = ZipFile.OpenRead(sourcePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isNcx = Path.GetExtension(entry.FullName).Equals(".ncx", StringComparison.OrdinalIgnoreCase);
            if (entry.Length > MaxArchiveEntryBytes || (!IsXhtmlEntry(entry.FullName) && !isNcx)) continue;
            EnsureSafeArchiveEntryName(entry.FullName);

            var markup = await ReadArchiveTextAsync(entry, cancellationToken);
            if (!TryParseDocument(markup, out var document)) continue;
            var segments = ExtractSegments(document, entry.FullName);
            pages.Add(new TranslationPage(
                entry.FullName,
                markup,
                segments,
                IsTocNavigationDocument(document),
                isNcx));
            Report(progress, new BookTranslationProgress(
                "正在扫描 EPUB",
                entry.FullName,
                0,
                0));
        }

        if (pages.Count == 0 || pages.All(page => page.Segments.Count == 0))
            throw new InvalidDataException("EPUB 中没有找到可翻译的正文。 ");
        return pages;
    }

    private async Task<Dictionary<string, string>> TranslateSegmentsAsync(
        IReadOnlyList<TranslationSegment> segments,
        BookTranslationSettings settings,
        IProgress<BookTranslationProgress>? progress,
        long totalCharacters,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<int, TranslationCacheSegmentLine>? resumedSegments,
        TranslationCacheWriter cacheWriter,
        TranslationRequestContext requestContext)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var processed = 0;
        long processedCharacters = 0;
        var index = 0;
        var batchingEnabled = true;

        bool TryGetResumed(TranslationSegment segment, out string value)
        {
            value = string.Empty;
            if (resumedSegments?.TryGetValue(segment.Index, out var resumed) != true
                || resumed is null
                || resumed.Status != BookTranslationSegmentStatus.Completed
                || string.IsNullOrWhiteSpace(resumed.TranslatedText)
                || resumed.OriginalText != segment.Text
                || resumed.RequestText != segment.Markup.RequestText
                || !segment.Markup.IsValidTranslation(resumed.TranslatedText)) return false;
            value = resumed.TranslatedText;
            return true;
        }

        void ReportSegmentState(
            IProgress<BookTranslationProgress>? currentProgress,
            TranslationSegment segment,
            int currentProcessed,
            int total,
            long currentProcessedCharacters,
            long currentTotalCharacters,
            BookTranslationSegmentStatus status,
            string translatedText,
            string processFlow,
            string? errorMessage = null) => ReportSegmentStateCore(
                currentProgress,
                segment,
                currentProcessed,
                total,
                currentProcessedCharacters,
                currentTotalCharacters,
                status,
                translatedText,
                processFlow,
                errorMessage,
                cacheWriter);

        var batchLimit = GetBatchCharacterLimit(settings.Provider);
        // One request is prefetched while the current batch is applied. Free
        // endpoints rate-limit aggressively, so this stays at two in-flight
        // requests and any prefetch failure simply falls back to the existing
        // serial path.
        var prefetchConcurrency = GetTranslationConcurrency(settings.Provider);
        var prefetched = new Dictionary<int, (List<TranslationSegment> Batch, Task<string?> Translation)>();

        List<TranslationSegment> BuildBatch(int start)
        {
            var batch = new List<TranslationSegment>();
            var batchLength = 0;
            while (batchingEnabled && start + batch.Count < segments.Count)
            {
                var candidate = segments[start + batch.Count];
                var candidateCacheKey = CreateTranslationCacheKey(settings, candidate.Markup.RequestText);
                if (cache.ContainsKey(candidateCacheKey)) break;
                if (TryGetResumed(candidate, out _)) break;
                if (candidate.Markup.RequestText.Contains("__LIBZORA_SEG_", StringComparison.Ordinal)) break;
                var candidateLength = candidate.Markup.RequestText.Length + 2 * CreateMarker(batch.Count).Length;
                var separatorLength = batch.Count == 0 ? 0 : Environment.NewLine.Length;
                if (batchLength + separatorLength + candidateLength > batchLimit)
                    break;
                batch.Add(candidate);
                batchLength += separatorLength + candidateLength;
            }
            return batch;
        }

        static string BuildMarkerPayload(List<TranslationSegment> batch) => string.Join(
            Environment.NewLine,
            batch.Select((segment, offset) =>
                $"{CreateMarker(offset)}{segment.Markup.RequestText}{CreateMarker(offset)}"));

        async Task<string?> PrefetchBatchAsync(string payload)
        {
            try
            {
                return await TranslateTextWithRetryAsync(
                    payload, requestContext, cancellationToken, cacheParts: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch
            {
                return null;
            }
        }

        while (index < segments.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = segments[index];
            var cacheKey = CreateTranslationCacheKey(settings, current.Markup.RequestText);
            if (TryGetResumed(current, out var resumedText))
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Processing,
                    string.Empty,
                    "读取原文 → 恢复本地缓存");
                cache[cacheKey] = resumedText;
                translations[current.Key] = resumedText;
                index++;
                processed++;
                processedCharacters += current.Text.Length;
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Completed,
                    resumedText,
                    "读取原文 → 恢复本地缓存 → 写入译文");
                continue;
            }

            if (cache.TryGetValue(cacheKey, out var cached))
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Processing,
                    string.Empty,
                    "读取原文 → 命中本地缓存");
                translations[current.Key] = cached;
                index++;
                processed++;
                processedCharacters += current.Text.Length;
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Completed,
                    cached,
                    "读取原文 → 命中本地缓存 → 写入译文");
                continue;
            }

            var batch = BuildBatch(index);

            if (batch.Count > 1)
            {
                foreach (var segment in batch)
                {
                    ReportSegmentState(
                        progress,
                        segment,
                        processed,
                        segments.Count,
                        processedCharacters,
                        totalCharacters,
                        BookTranslationSegmentStatus.Processing,
                        string.Empty,
                        "读取原文 → 批量请求翻译");
                }

                var markerPayload = BuildMarkerPayload(batch);

                // Start the next batch now so its network wait overlaps this
                // batch's request and result application.
                if (prefetchConcurrency > 1 && batchingEnabled)
                {
                    var nextIndex = index + batch.Count;
                    var nextBatch = BuildBatch(nextIndex);
                    if (nextBatch.Count > 1 && !prefetched.ContainsKey(nextIndex))
                    {
                        prefetched[nextIndex] =
                            (nextBatch, PrefetchBatchAsync(BuildMarkerPayload(nextBatch)));
                    }
                }

                string? batchResult;
                try
                {
                    if (prefetched.TryGetValue(index, out var prefetchedEntry)
                        && prefetchedEntry.Batch.Count == batch.Count
                        && prefetchedEntry.Batch.Select(segment => segment.Key)
                            .SequenceEqual(batch.Select(segment => segment.Key)))
                    {
                        prefetched.Remove(index);
                        batchResult = await prefetchedEntry.Translation;
                    }
                    else
                    {
                        batchResult = await TranslateTextWithRetryAsync(
                            markerPayload,
                            requestContext,
                            cancellationToken,
                            cacheParts: false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    foreach (var segment in batch)
                    {
                        ReportSegmentState(
                            progress,
                            segment,
                            processed,
                            segments.Count,
                            processedCharacters,
                            totalCharacters,
                            BookTranslationSegmentStatus.Failed,
                            string.Empty,
                            "读取原文 → 批量请求翻译 → 失败",
                            exception.Message);
                    }

                    throw;
                }

                if (batchResult is not null
                    && TryReadMarkedTranslations(batchResult, batch.Count, out var markedTranslations)
                    && batch.Select((segment, offset) => segment.Markup.IsValidTranslation(markedTranslations[offset])).All(valid => valid))
                {
                    for (var offset = 0; offset < batch.Count; offset++)
                    {
                        var segment = batch[offset];
                        var value = markedTranslations[offset];
                        var key = CreateTranslationCacheKey(settings, segment.Markup.RequestText);
                        cache[key] = value;
                        translations[segment.Key] = value;
                        processed++;
                        processedCharacters += segment.Text.Length;
                        ReportSegmentState(
                            progress,
                            segment,
                            processed,
                            segments.Count,
                            processedCharacters,
                            totalCharacters,
                            BookTranslationSegmentStatus.Completed,
                            value,
                            "读取原文 → 批量请求翻译 → 接收译文 → 写入译文");
                    }

                    index += batch.Count;
                    continue;
                }

                batchingEnabled = false;
                prefetched.Clear();
                foreach (var segment in batch)
                {
                    ReportSegmentState(
                        progress,
                        segment,
                        processed,
                        segments.Count,
                        processedCharacters,
                        totalCharacters,
                        BookTranslationSegmentStatus.Processing,
                        string.Empty,
                        "批量结果校验失败 → 改为逐段翻译");
                }
            }

            // Some web translators normalize or remove the markers. Falling
            // back to one segment at a time keeps the output correct even when
            // a provider changes its response format.
            ReportSegmentState(
                progress,
                current,
                processed,
                segments.Count,
                processedCharacters,
                totalCharacters,
                BookTranslationSegmentStatus.Processing,
                string.Empty,
                "读取原文 → 请求翻译服务");

            string translated;
            try
            {
                translated = await TranslateSegmentAsync(
                    current,
                    requestContext,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ReportSegmentState(
                    progress,
                    current,
                    processed,
                    segments.Count,
                    processedCharacters,
                    totalCharacters,
                    BookTranslationSegmentStatus.Failed,
                    string.Empty,
                    "读取原文 → 请求翻译服务 → 失败",
                    exception.Message);
                throw;
            }

            cache[cacheKey] = translated;
            translations[current.Key] = translated;
            processed++;
            processedCharacters += current.Text.Length;
            ReportSegmentState(
                progress,
                current,
                processed,
                segments.Count,
                processedCharacters,
                totalCharacters,
                BookTranslationSegmentStatus.Completed,
                translated,
                "读取原文 → 请求翻译服务 → 接收译文 → 写入译文");
            index++;
        }

        return translations;
    }
}
