using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotNetG2P.Chinese;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// EPUB generation and ruby annotation.
/// </summary>
public sealed partial class PinyinBookService : IPinyinBookService
{
    private async Task<PinyinBookResult> GenerateFromEpubAsync(
        string epubSourcePath,
        string destinationPath,
        PinyinBookOptions options,
        IProgress<PinyinBookProgress>? progress,
        CancellationToken cancellationToken,
        string cacheSourcePath,
        string cacheSourceHash,
        PinyinBookResumeMode resumeMode)
    {
        var pages = new List<PinyinPage>();
        var xhtmlEntryNames = new List<string>();
        var hasMimetype = false;

        using (var source = ZipFile.OpenRead(epubSourcePath))
        {
            foreach (var entry in source.Entries)
            {
                EnsureSafeArchiveEntryName(entry.FullName);
                if (entry.FullName.Equals("mimetype", StringComparison.OrdinalIgnoreCase))
                    hasMimetype = true;
                if (IsXhtmlEntry(entry.FullName))
                    xhtmlEntryNames.Add(entry.FullName);
            }

            if (!hasMimetype)
                throw new InvalidDataException("EPUB 缺少 mimetype 文件。 ");

            var totalPages = xhtmlEntryNames.Count;
            Report(progress, new PinyinBookProgress(
                "正在扫描 EPUB",
                Path.GetFileName(epubSourcePath),
                0,
                totalPages,
                0,
                OverallPercentage: 0));

            for (var index = 0; index < xhtmlEntryNames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = source.GetEntry(xhtmlEntryNames[index]);
                if (entry is null) continue;
                if (entry.Length > MaxArchiveEntryBytes)
                {
                    Report(progress, new PinyinBookProgress(
                        "正在扫描 EPUB",
                        entry.FullName,
                        index + 1,
                        totalPages,
                        0,
                        OverallPercentage: totalPages <= 0 ? 0 : (index + 1) * 30d / totalPages));
                    continue;
                }

                var markup = await ReadArchiveTextAsync(entry, cancellationToken);
                if (TryParseDocument(markup, out var document))
                {
                    var body = document.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
                    if (body is not null)
                    {
                        var pageSegments = GetPinyinSegments(body, entry.FullName);
                        pages.Add(new PinyinPage(entry.FullName, document, pageSegments));
                    }
                }

                Report(progress, new PinyinBookProgress(
                    "正在扫描 EPUB",
                    entry.FullName,
                    index + 1,
                    totalPages,
                    0,
                    OverallPercentage: totalPages <= 0 ? 0 : (index + 1) * 30d / totalPages));
            }
        }

        var nextSegmentIndex = 0;
        pages = pages
            .Select(page =>
            {
                var segments = page.Segments
                    .Select(segment => segment with { Index = nextSegmentIndex++ })
                    .ToArray();
                return page with { Segments = segments };
            })
            .ToList();
        var segments = pages.SelectMany(page => page.Segments).ToArray();
        if (segments.Length == 0)
            throw new InvalidDataException("EPUB 中没有找到可注音的中文正文。 ");

        var totalSegments = segments.Length;
        var cachePath = GetPinyinCachePath(destinationPath);
        var resumeSnapshot = resumeMode == PinyinBookResumeMode.Resume
            ? await TryLoadPinyinCacheAsync(
                cachePath,
                cacheSourcePath,
                cacheSourceHash,
                options,
                cancellationToken)
            : null;
        PinyinCacheWriter? cacheWriter = null;
        var generationCompleted = false;
        try
        {
            cacheWriter = new PinyinCacheWriter(
                cachePath,
                new PinyinCacheHeader(
                    "header",
                    PinyinCacheVersion,
                    cacheSourcePath,
                    cacheSourceHash,
                    options,
                    totalSegments,
                    DateTimeOffset.UtcNow),
                append: resumeSnapshot is not null);

            var renderedDocuments = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
            var workStates = new Dictionary<int, PinyinWorkState>();
            var processedSegments = 0;
            var locallyProcessedSegments = 0;
            var annotatedCharacters = 0;
            var reviewCandidateCount = 0;
            var reviewedCandidates = 0;
            var localProgressRange = options.EnableAiReview ? 40d : 60d;
            var reviewing = false;

            void PersistWork(
                PinyinWorkState work,
                PinyinBookSegmentStatus status,
                string processFlow,
                double percentage,
                string? error = null)
            {
                if (status == PinyinBookSegmentStatus.Completed
                    && work.Status != PinyinBookSegmentStatus.Completed)
                    processedSegments++;
                work.Status = status;
                var annotation = work.Annotation;
                var update = new PinyinBookSegmentProgress(
                    work.Segment.Index, work.Segment.EntryName, work.Segment.OriginalText,
                    CreateAnnotatedDisplayText(annotation.Nodes), status, processFlow, error);
                cacheWriter.Append(
                    update, SerializeNodes(annotation.Nodes), annotation.AnnotatedCharacters,
                    annotation.Targets.Count, annotation.Targets.Count(target => target.Reviewed),
                    annotation.Targets);
                Report(progress, new PinyinBookProgress(
                    reviewing ? "正在复核疑问拼音" : "正在生成拼音",
                    work.Segment.EntryName, Math.Min(work.PageIndex + 1, xhtmlEntryNames.Count),
                    xhtmlEntryNames.Count, annotatedCharacters, reviewCandidateCount, reviewedCandidates,
                    percentage, update, processedSegments, totalSegments));
            }

            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                var page = pages[pageIndex];
                foreach (var segment in page.Segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Show the source paragraph before model loading/inference.
                    // This is not durable work yet, so do not write it over a
                    // completed resume-cache entry or count it as processed.
                    Report(progress, new PinyinBookProgress(
                        "正在生成拼音", segment.EntryName, pageIndex + 1, xhtmlEntryNames.Count,
                        annotatedCharacters, reviewCandidateCount, reviewedCandidates,
                        30 + locallyProcessedSegments * localProgressRange / totalSegments,
                        new PinyinBookSegmentProgress(
                            segment.Index, segment.EntryName, segment.OriginalText, string.Empty,
                            PinyinBookSegmentStatus.Processing, "读取原文 → 正在本地注音"),
                        processedSegments, totalSegments));
                    cancellationToken.ThrowIfCancellationRequested();
                    PinyinAnnotationResult annotation;
                    var restored = false;
                    if (resumeSnapshot is not null
                        && resumeSnapshot.Segments.TryGetValue(segment.Index, out var cached)
                        && TryRestoreAnnotation(segment, cached, out var cachedAnnotation))
                    {
                        annotation = cachedAnnotation;
                        restored = true;
                    }
                    else
                    {
                        annotation = AnnotateTextNode(segment, options, cancellationToken);
                    }

                    var work = new PinyinWorkState(segment, pageIndex, annotation);
                    workStates.Add(segment.Index, work);
                    renderedDocuments[page.EntryName] = page.Document;
                    annotatedCharacters += annotation.AnnotatedCharacters;
                    reviewCandidateCount += annotation.Targets.Count;
                    reviewedCandidates += annotation.Targets.Count(target => target.Reviewed);
                    locallyProcessedSegments++;
                    var percentage = 30 + locallyProcessedSegments * localProgressRange / totalSegments;
                    var processFlow = restored ? "读取原文 → 恢复本地缓存" : "读取原文 → 本地注音";
                    PersistWork(work, PinyinBookSegmentStatus.Processing, processFlow, percentage);

                    // Local work is durable even if cancellation arrives in the
                    // progress report. Only unresolved review targets remain.
                    if (!options.EnableAiReview || annotation.Targets.All(target => target.Reviewed))
                        PersistWork(work, PinyinBookSegmentStatus.Completed,
                            processFlow + " → 写入 EPUB", percentage);
                }
            }

            var reviewTargets = workStates.Values.SelectMany(work => work.Annotation.Targets).ToArray();
            reviewing = options.EnableAiReview;
            var reviewError = await ReviewPinyinAsync(
                reviewTargets,
                options,
                (stage, item, percentage) => Report(progress, new PinyinBookProgress(
                    stage, item, xhtmlEntryNames.Count, xhtmlEntryNames.Count,
                    annotatedCharacters, reviewCandidateCount, reviewedCandidates, percentage,
                    ProcessedSegments: processedSegments, TotalSegments: totalSegments)),
                (checkedTargets, percentage) =>
                {
                    reviewedCandidates += checkedTargets.Count;
                    foreach (var segmentIndex in checkedTargets.Select(target => target.SegmentIndex).Distinct())
                    {
                        var work = workStates[segmentIndex];
                        var complete = work.Annotation.Targets.All(target => target.Reviewed);
                        PersistWork(
                            work,
                            complete ? PinyinBookSegmentStatus.Completed : PinyinBookSegmentStatus.Processing,
                            complete ? "读取原文 → 本地注音 → AI 复核 → 写入 EPUB"
                                : "读取原文 → 本地注音 → AI 部分复核，已保存",
                            percentage);
                    }
                },
                cancellationToken);

            var failedSegments = 0;
            foreach (var work in workStates.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (work.Status == PinyinBookSegmentStatus.Completed) continue;
                var failed = options.EnableAiReview && work.Annotation.Targets.Any(target => !target.Reviewed);
                if (failed) failedSegments++;
                PersistWork(
                    work,
                    failed ? PinyinBookSegmentStatus.Failed : PinyinBookSegmentStatus.Completed,
                    failed ? "读取原文 → 本地注音 → AI 复核未完成，已保存" : "读取原文 → 本地注音 → 写入 EPUB",
                    90,
                    failed ? reviewError : null);
            }

            var renderedPages = renderedDocuments.ToDictionary(
                pair => pair.Key,
                pair => Encoding.UTF8.GetBytes(SerializeDocument(pair.Value)),
                StringComparer.OrdinalIgnoreCase);
            Report(progress, new PinyinBookProgress(
                "正在生成 EPUB",
                Path.GetFileName(destinationPath),
                xhtmlEntryNames.Count,
                xhtmlEntryNames.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                95,
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));
            await WriteArchiveAsync(epubSourcePath, destinationPath, renderedPages, cancellationToken);

            generationCompleted = failedSegments == 0 && reviewError is null;
            var result = new PinyinBookResult(
                cacheSourcePath,
                destinationPath,
                xhtmlEntryNames.Count,
                renderedPages.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                reviewError,
                failedSegments);
            Report(progress, new PinyinBookProgress(
                failedSegments == 0 ? "完成" : "部分完成",
                failedSegments == 0
                    ? $"已生成 {renderedPages.Count} 个正文页面"
                    : $"已生成 {renderedPages.Count} 个正文页面，{failedSegments:N0} 段待复核",
                xhtmlEntryNames.Count,
                xhtmlEntryNames.Count,
                annotatedCharacters,
                reviewCandidateCount,
                reviewedCandidates,
                100,
                ProcessedSegments: processedSegments,
                TotalSegments: totalSegments));
            return result;
        }
        finally
        {
            cacheWriter?.Dispose();
            if (generationCompleted)
                TryDeletePinyinCache(cachePath);
        }
    }
}
