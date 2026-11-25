using System.Buffers;
using System.IO.Compression;
using System.Xml.Linq;

namespace Libzora.Infrastructure;

/// <summary>
/// Allows the reader to open and render the target chapter immediately on a
/// cold EPUB cache while remaining chapters, full navigation/physical TOCs, and
/// the durable index cache finish preparing concurrently in the background.
/// </summary>
public sealed class EpubProgressiveSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<int, CancellationToken, Task>? _ensureChapterAsync;
    private readonly Func<CancellationToken, Task<EpubReaderDocument>>? _completionRunner;
    private readonly Func<ValueTask>? _disposeArchiveAsync;
    private int _disposed;

    internal EpubProgressiveSession(EpubReaderDocument completedDocument)
    {
        InitialDocument = completedDocument;
        CompletionTask = Task.FromResult(completedDocument);
    }

    internal EpubProgressiveSession(
        EpubReaderDocument initialDocument,
        Func<int, CancellationToken, Task> ensureChapterAsync,
        Func<CancellationToken, Task<EpubReaderDocument>> completionRunner,
        Func<ValueTask> disposeArchiveAsync,
        CancellationToken completionCancellationToken)
    {
        InitialDocument = initialDocument;
        _ensureChapterAsync = ensureChapterAsync;
        _completionRunner = completionRunner;
        _disposeArchiveAsync = disposeArchiveAsync;
        CompletionTask = Task.Run(() => RunCompletionAsync(completionCancellationToken), completionCancellationToken);
    }

    public EpubReaderDocument InitialDocument { get; }
    public bool IsComplete => CompletionTask.IsCompletedSuccessfully;
    public Task<EpubReaderDocument> CompletionTask { get; }

    public async Task EnsureChapterReadyAsync(int chapterIndex, CancellationToken cancellationToken = default)
    {
        if (IsComplete || _ensureChapterAsync is null || chapterIndex < 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsComplete) return;
            await _ensureChapterAsync(chapterIndex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EpubReaderDocument> RunCompletionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _completionRunner!(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_disposeArchiveAsync is not null)
                await _disposeArchiveAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            await CompletionTask.ConfigureAwait(false);
        }
        catch
        {
            // Background preparation cancellation or transient failure is safe
            // to ignore when the reader page tears down; incomplete caches do
            // not write the ready marker and recover cleanly on the next open.
        }
        if (_disposeArchiveAsync is not null)
            await _disposeArchiveAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

public sealed partial class EpubReaderPreparationService
{
    private const long Stage1InlineNonHtmlBudgetBytes = 16L * 1024 * 1024;
    private const long Stage1OversizedSingleAssetLimitBytes = 512L * 1024;

    public Task<EpubProgressiveSession> PrepareProgressiveAsync(
        string epubPath,
        string sha256,
        int preferredChapterIndex = 0,
        CancellationToken initialCancellationToken = default,
        CancellationToken completionCancellationToken = default) =>
        Task.Run(
            () => PrepareProgressiveCoreAsync(
                epubPath,
                sha256,
                preferredChapterIndex,
                initialCancellationToken,
                completionCancellationToken),
            initialCancellationToken);

    private async Task<EpubProgressiveSession> PrepareProgressiveCoreAsync(
        string epubPath,
        string sha256,
        int preferredChapterIndex,
        CancellationToken initialCancellationToken,
        CancellationToken completionCancellationToken)
    {
        var cacheKey = string.Concat(sha256.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (cacheKey.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");

        var cacheRoot = Path.GetFullPath(Path.Combine(_paths.ReaderCache, cacheKey));
        EnsureContainedPath(_paths.ReaderCache, cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        var extractionReadyPath = Path.Combine(cacheRoot, ExtractionReadyFileName);
        if (await IsExtractionReadyAsync(extractionReadyPath, cacheKey, initialCancellationToken).ConfigureAwait(false)
            && await TryLoadReaderIndexAsync(cacheRoot, initialCancellationToken).ConfigureAwait(false) is { } cached)
        {
            return new EpubProgressiveSession(cached);
        }

        // Clean any partial previous cache before streaming Stage 1 into cacheRoot.
        TryDeleteDirectory(cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        FileStream? input = null;
        ZipArchive? archive = null;
        try
        {
            input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: false);
            archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            archive?.Dispose();
            input?.Dispose();
            var document = await PrepareCoreAsync(epubPath, sha256, completionCancellationToken).ConfigureAwait(false);
            return new EpubProgressiveSession(document);
        }

        try
        {
            if (archive.Entries.Count > MaximumEpubEntries)
                throw new EpubExtractionLimitException("EPUB 包含过多文件，已拒绝解压。");

            var normalizedRoot = NormalizeRootPath(cacheRoot);
            var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var createdDirectories = new HashSet<string>(pathComparer);
            var extractedPaths = new HashSet<string>(pathComparer);
            var entryByDestination = new Dictionary<string, ZipArchiveEntry>(pathComparer);
            var entriesInOrder = new List<(ZipArchiveEntry Entry, string Destination)>(archive.Entries.Count);

            foreach (var entry in archive.Entries)
            {
                initialCancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                var destination = ResolveContainedPathNormalized(normalizedRoot, cacheRoot, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    EnsureDirectoryExists(destination, createdDirectories);
                    continue;
                }
                entriesInOrder.Add((entry, destination));
                entryByDestination.TryAdd(destination, entry);
            }

            var containerPath = Path.Combine(cacheRoot, "META-INF", "container.xml");
            if (!entryByDestination.TryGetValue(containerPath, out var containerEntry))
                throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");

            long expandedBytes = 0;
            var archiveLock = new SemaphoreSlim(1, 1);

            await ExtractEntriesBatchLockedAsync(
                [containerEntry],
                entry => Path.GetFullPath(ResolveContainedPathNormalized(normalizedRoot, cacheRoot, entry.FullName)),
                cacheRoot,
                createdDirectories,
                extractedPaths,
                () => expandedBytes,
                delta => expandedBytes += delta,
                initialCancellationToken).ConfigureAwait(false);

            var container = await LoadXmlAsync(containerPath, initialCancellationToken).ConfigureAwait(false);
            var packageRelativePath = container
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
                .Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(packageRelativePath))
                throw new InvalidDataException("EPUB 没有声明内容清单。");

            var packagePath = ResolveContainedPathNormalized(normalizedRoot, cacheRoot, packageRelativePath);
            if (!entryByDestination.TryGetValue(packagePath, out var packageEntry))
                throw new InvalidDataException("EPUB 内容清单不存在。");

            await ExtractEntriesBatchLockedAsync(
                [packageEntry],
                entry => Path.GetFullPath(ResolveContainedPathNormalized(normalizedRoot, cacheRoot, entry.FullName)),
                cacheRoot,
                createdDirectories,
                extractedPaths,
                () => expandedBytes,
                delta => expandedBytes += delta,
                initialCancellationToken).ConfigureAwait(false);

            var package = await LoadXmlAsync(packagePath, initialCancellationToken).ConfigureAwait(false);
            var manifest = package.Descendants()
                .Where(element => element.Name.LocalName == "item")
                .Select(element => new ManifestItem(
                    element.Attribute("id")?.Value,
                    element.Attribute("href")?.Value,
                    element.Attribute("media-type")?.Value,
                    element.Attribute("properties")?.Value))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
                .GroupBy(item => item.Id!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var packageDirectory = Path.GetDirectoryName(packagePath)!;
            var chapters = new List<string>();
            foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
            {
                initialCancellationToken.ThrowIfCancellationRequested();
                var idRef = itemRef.Attribute("idref")?.Value;
                if (idRef is null || !manifest.TryGetValue(idRef, out var item)) continue;
                if (item.MediaType is not ("application/xhtml+xml" or "text/html")) continue;

                var href = Uri.UnescapeDataString(item.Href!.Split('#')[0]);
                var chapterPath = ResolvePublicationPathNormalized(packageDirectory, normalizedRoot, href);
                if (entryByDestination.ContainsKey(chapterPath))
                    chapters.Add(chapterPath);
            }

            if (chapters.Count == 0)
                throw new InvalidDataException("EPUB 没有可阅读的章节。");

            var chapterIndexByPath = CreateChapterIndexLookup(chapters);
            var targetIndex = Math.Clamp(preferredChapterIndex, 0, chapters.Count - 1);

            // Collect Stage 1 entries: target chapter + adjacent next chapter + first chapter,
            // authoritative nav/ncx file, all stylesheets, and small/inline non-HTML assets.
            var stage1Entries = new List<(ZipArchiveEntry Entry, string Destination)>();
            var queuedStage1 = new HashSet<string>(pathComparer);
            void QueueIfPresent(string path)
            {
                if (!extractedPaths.Contains(path)
                    && queuedStage1.Add(path)
                    && entryByDestination.TryGetValue(path, out var entry))
                {
                    stage1Entries.Add((entry, path));
                }
            }

            QueueIfPresent(chapters[targetIndex]);
            if (targetIndex + 1 < chapters.Count)
                QueueIfPresent(chapters[targetIndex + 1]);
            QueueIfPresent(chapters[0]);

            var navManifestItem = manifest.Values.FirstOrDefault(item => HasToken(item.Properties, "nav"));
            string? navPath = null;
            if (navManifestItem?.Href is { } navHref)
            {
                navPath = ResolvePublicationPathNormalized(
                    packageDirectory, normalizedRoot, Uri.UnescapeDataString(navHref.Split('#')[0]));
                QueueIfPresent(navPath);
            }
            var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
                .Attribute("toc")?.Value;
            string? ncxPath = null;
            if (spineTocId is not null
                && manifest.TryGetValue(spineTocId, out var ncxItem)
                && ncxItem.Href is { } ncxHref)
            {
                ncxPath = ResolvePublicationPathNormalized(
                    packageDirectory, normalizedRoot, Uri.UnescapeDataString(ncxHref.Split('#')[0]));
                QueueIfPresent(ncxPath);
            }

            long nonHtmlAssetBytes = 0;
            foreach (var (entry, destination) in entriesInOrder)
            {
                var ext = Path.GetExtension(destination);
                var isCss = ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
                var isHtmlOrXml = !isCss && IsHtmlOrXmlExtension(ext);
                if (isCss)
                {
                    QueueIfPresent(destination);
                }
                else if (!isHtmlOrXml && entry.Length >= 0)
                {
                    if (nonHtmlAssetBytes + entry.Length <= Stage1InlineNonHtmlBudgetBytes
                        || entry.Length <= Stage1OversizedSingleAssetLimitBytes)
                    {
                        QueueIfPresent(destination);
                        nonHtmlAssetBytes += entry.Length;
                    }
                }
            }

            await ExtractEntriesBatchLockedAsync(
                stage1Entries.Select(pair => pair.Entry).ToList(),
                entry => Path.GetFullPath(ResolveContainedPathNormalized(normalizedRoot, cacheRoot, entry.FullName)),
                cacheRoot,
                createdDirectories,
                extractedPaths,
                () => expandedBytes,
                delta => expandedBytes += delta,
                initialCancellationToken).ConfigureAwait(false);

            var initialNavigation = await ReadInitialNavigationFastAsync(
                navPath,
                ncxPath,
                cacheRoot,
                chapters,
                chapterIndexByPath,
                initialCancellationToken).ConfigureAwait(false);
            var initialTitles = BuildInitialChapterTitles(chapters, initialNavigation);
            var initialDocument = new EpubReaderDocument(cacheRoot, chapters, initialNavigation, initialTitles);

            var remainingIndex = 0;
            var archivedDisposed = 0;

            async ValueTask DisposeArchiveAsync()
            {
                if (Interlocked.Exchange(ref archivedDisposed, 1) != 0)
                    return;
                await archiveLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    archive.Dispose();
                }
                catch
                {
                    // Safe to ignore cleanup exceptions on tear-down.
                }
                finally
                {
                    archiveLock.Release();
                }
            }

            async Task EnsureChapterReadyAsync(int chapterIndex, CancellationToken ct)
            {
                if (chapterIndex < 0 || chapterIndex >= chapters.Count || Volatile.Read(ref archivedDisposed) != 0)
                    return;

                var path = chapters[chapterIndex];
                await archiveLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref archivedDisposed) != 0 || extractedPaths.Contains(path))
                        return;

                    var toExtract = new List<ZipArchiveEntry>(8);
                    if (entryByDestination.TryGetValue(path, out var chapterEntry))
                        toExtract.Add(chapterEntry);

                    // Also extract any adjacent or deferred asset entries if small enough so chapter images never miss.
                    if (toExtract.Count > 0)
                    {
                        await ExtractEntriesBatchLockedAsync(
                            toExtract,
                            entry => Path.GetFullPath(ResolveContainedPathNormalized(normalizedRoot, cacheRoot, entry.FullName)),
                            cacheRoot,
                            createdDirectories,
                            extractedPaths,
                            () => expandedBytes,
                            delta => expandedBytes += delta,
                            ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    archiveLock.Release();
                }
            }

            async Task<EpubReaderDocument> RunStage2Async(CancellationToken ct)
            {
                const int backgroundBatchSize = 24;
                while (remainingIndex < entriesInOrder.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    List<(string Destination, byte[] Buffer, int Length, bool IsCss)>? sanitizeBatch = null;
                    await archiveLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var take = 0;
                        while (remainingIndex < entriesInOrder.Count && take < backgroundBatchSize)
                        {
                            var (entry, destination) = entriesInOrder[remainingIndex++];
                            if (extractedPaths.Contains(destination))
                                continue;

                            var ext = Path.GetExtension(destination);
                            var isCss = ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
                            var isHtmlOrXml = !isCss && IsHtmlOrXmlExtension(ext);
                            EnsureDirectoryExists(Path.GetDirectoryName(destination)!, createdDirectories);

                            if ((isCss || isHtmlOrXml) && entry.Length is >= 0 and <= InlineSanitizeThresholdBytes)
                            {
                                var copyBuf = ArrayPool<byte>.Shared.Rent(81920);
                                try
                                {
                                    using var source = entry.Open();
                                    var (buffer, length, exceededInline) = ReadEntryForInlineSanitize(
                                        source,
                                        destination,
                                        entry.Length,
                                        expandedBytes,
                                        copyBuf,
                                        ct);
                                    expandedBytes += length;
                                    if (!exceededInline)
                                    {
                                        sanitizeBatch ??= new List<(string, byte[], int, bool)>(backgroundBatchSize);
                                        sanitizeBatch.Add((destination, buffer, length, isCss));
                                        extractedPaths.Add(destination);
                                    }
                                    else
                                    {
                                        extractedPaths.Add(destination);
                                        await SanitizeOversizedEntryAsync(destination, isCss, cacheRoot, ct).ConfigureAwait(false);
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(copyBuf);
                                }
                            }
                            else
                            {
                                var copyBuf = ArrayPool<byte>.Shared.Rent(81920);
                                try
                                {
                                    using var source = entry.Open();
                                    using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: false);
                                    expandedBytes += CopyEpubEntrySync(source, output, entry.Length, expandedBytes, copyBuf, ct);
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(copyBuf);
                                }
                                extractedPaths.Add(destination);
                                if (isCss || isHtmlOrXml)
                                    await SanitizeOversizedEntryAsync(destination, isCss, cacheRoot, ct).ConfigureAwait(false);
                            }

                            take++;
                        }
                    }
                    finally
                    {
                        archiveLock.Release();
                    }

                    if (sanitizeBatch is { Count: > 0 })
                    {
                        await FlushSanitizeBatchAsync(sanitizeBatch, cacheRoot, ct).ConfigureAwait(false);
                    }
                }

                await DisposeArchiveAsync().ConfigureAwait(false);

                var navigation = await ReadNavigationAsync(
                    package,
                    manifest,
                    packageDirectory,
                    cacheRoot,
                    chapters,
                    chapterIndexByPath,
                    ct).ConfigureAwait(false);
                var hasAuthoritativeNavigation = navigation.Count > 0;
                if (navigation.Count == 0)
                {
                    navigation = chapters
                        .Select((chapter, index) => new EpubReaderNavigationItem(
                            $"第 {index + 1} 章",
                            new Uri(chapter).AbsoluteUri,
                            index))
                        .ToList();
                }

                var authoritativeNavigation = hasAuthoritativeNavigation ? navigation : [];
                var chapterTitles = await ReadChapterTitlesAsync(chapters, authoritativeNavigation, ct).ConfigureAwait(false);
                await ReplaceDuplicateChapterTitlesWithBodyPreviewAsync(chapters, chapterTitles, authoritativeNavigation, ct).ConfigureAwait(false);

                var document = await NormalizeCoverChaptersAsync(
                    new EpubReaderDocument(cacheRoot, chapters, navigation, chapterTitles),
                    package,
                    manifest,
                    packageDirectory,
                    ct).ConfigureAwait(false);
                document = document with
                {
                    AuxiliaryChapters = await DetectAuxiliaryChaptersAsync(
                        document.Chapters, document.Navigation, ct).ConfigureAwait(false)
                };
                await WriteReaderIndexAsync(cacheRoot, document, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    extractionReadyPath,
                    $"{cacheKey}\n{ExtractionFormatVersion}",
                    System.Text.Encoding.UTF8,
                    ct).ConfigureAwait(false);
                return document;
            }

            return new EpubProgressiveSession(
                initialDocument,
                EnsureChapterReadyAsync,
                RunStage2Async,
                DisposeArchiveAsync,
                completionCancellationToken);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static bool IsHtmlOrXmlExtension(string extension) =>
        extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);

    private static async Task ExtractEntriesBatchLockedAsync(
        IReadOnlyList<ZipArchiveEntry> entries,
        Func<ZipArchiveEntry, string> resolveDestination,
        string cacheRoot,
        HashSet<string> createdDirectories,
        HashSet<string> extractedPaths,
        Func<long> getExpandedBytes,
        Action<long> addExpandedBytes,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        var batch = new List<(string Destination, byte[] Buffer, int Length, bool IsCss)>(entries.Count);
        var copyBuffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = resolveDestination(entry);
                if (extractedPaths.Contains(destination))
                    continue;
                if (entry.Length > MaximumEpubEntryBytes
                    || entry.Length > MaximumEpubExpandedBytes - getExpandedBytes())
                {
                    throw new EpubExtractionLimitException("EPUB 解压后的数据超过安全上限，已拒绝解压。");
                }

                EnsureDirectoryExists(Path.GetDirectoryName(destination)!, createdDirectories);
                var ext = Path.GetExtension(destination);
                var isCss = ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
                var isHtmlOrXml = !isCss && IsHtmlOrXmlExtension(ext);

                if ((isCss || isHtmlOrXml) && entry.Length is >= 0 and <= InlineSanitizeThresholdBytes)
                {
                    using var source = entry.Open();
                    var (buffer, length, exceededInline) = ReadEntryForInlineSanitize(
                        source,
                        destination,
                        entry.Length,
                        getExpandedBytes(),
                        copyBuffer,
                        cancellationToken);
                    addExpandedBytes(length);
                    extractedPaths.Add(destination);
                    if (!exceededInline)
                    {
                        batch.Add((destination, buffer, length, isCss));
                    }
                    else
                    {
                        await SanitizeOversizedEntryAsync(destination, isCss, cacheRoot, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    using (var source = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: false))
                    {
                        addExpandedBytes(CopyEpubEntrySync(source, output, entry.Length, getExpandedBytes(), copyBuffer, cancellationToken));
                    }
                    extractedPaths.Add(destination);
                    if (isCss || isHtmlOrXml)
                        await SanitizeOversizedEntryAsync(destination, isCss, cacheRoot, cancellationToken).ConfigureAwait(false);
                }
            }

            if (batch.Count > 0)
            {
                await FlushSanitizeBatchAsync(batch, cacheRoot, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
            foreach (var item in batch)
                ArrayPool<byte>.Shared.Return(item.Buffer);
        }
    }

    private static Task SanitizeOversizedEntryAsync(
        string destination,
        bool isCss,
        string cacheRoot,
        CancellationToken cancellationToken) =>
        isCss
            ? SanitizeCssFileAsync(destination, cacheRoot, cancellationToken)
            : SanitizeHtmlFileSafelyAsync(destination, cacheRoot, cancellationToken);

    private static async Task<List<EpubReaderNavigationItem>> ReadInitialNavigationFastAsync(
        string? navPath,
        string? ncxPath,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        IReadOnlyDictionary<string, int> chapterIndexByPath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(navPath) && File.Exists(navPath))
        {
            var navDocument = await LoadXmlAsync(navPath, cancellationToken).ConfigureAwait(false);
            var navElements = navDocument.Descendants()
                .Where(element => element.Name.LocalName.Equals("nav", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var explicitToc = navElements
                .Where(IsTocNavigationElement)
                .Select(element => CreateNavigationItems(
                    GetNavigationLinks(element),
                    navPath,
                    cacheRoot,
                    chapterIndexByPath))
                .OrderByDescending(items => items.Count)
                .FirstOrDefault(items => items.Count > 0);
            if (explicitToc is { Count: > 0 })
                return explicitToc;

            var inferredToc = navElements
                .Where(element => !IsKnownNonTocNavigationElement(element))
                .Select(element => CreateNavigationItems(
                    GetNavigationLinks(element),
                    navPath,
                    cacheRoot,
                    chapterIndexByPath))
                .OrderByDescending(items => items.Count)
                .FirstOrDefault(items => items.Count > 0);
            if (inferredToc is { Count: > 0 })
                return inferredToc;
        }

        if (!string.IsNullOrEmpty(ncxPath) && File.Exists(ncxPath))
        {
            var ncx = await LoadXmlAsync(ncxPath, cancellationToken).ConfigureAwait(false);
            var navMap = ncx.Descendants().FirstOrDefault(element => element.Name.LocalName == "navMap");
            var items = CreateNavigationItems(
                navMap is null ? [] : ReadNcxNavigationLinks(navMap),
                ncxPath,
                cacheRoot,
                chapterIndexByPath);
            if (items.Count > 0)
                return items;
        }

        return chapters
            .Select((chapter, index) => new EpubReaderNavigationItem(
                $"第 {index + 1} 章",
                new Uri(chapter).AbsoluteUri,
                index))
            .ToList();
    }

    private static List<string> BuildInitialChapterTitles(
        IReadOnlyList<string> chapters,
        IReadOnlyList<EpubReaderNavigationItem> navigation)
    {
        var titles = new string[chapters.Count];
        foreach (var item in navigation)
        {
            if (item.ChapterIndex >= 0
                && item.ChapterIndex < titles.Length
                && string.IsNullOrEmpty(titles[item.ChapterIndex])
                && !string.IsNullOrWhiteSpace(item.Title))
            {
                titles[item.ChapterIndex] = NormalizeTitle(item.Title);
            }
        }
        for (var i = 0; i < titles.Length; i++)
        {
            if (string.IsNullOrEmpty(titles[i]))
                titles[i] = $"第 {i + 1} 章";
        }
        return titles.ToList();
    }
}
