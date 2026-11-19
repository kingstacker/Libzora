using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Libzora.Infrastructure;

/// <summary>
/// Reader index cache document and writer.
/// </summary>
public sealed partial class EpubReaderPreparationService
{
    private sealed record ReaderIndexCacheDocument(
        string FormatVersion,
        List<string>? Chapters,
        List<ReaderIndexNavigationItem>? Navigation,
        List<string>? ChapterTitles);

    private sealed record ReaderIndexNavigationItem(
        string Title,
        string TargetPath,
        string Fragment,
        int ChapterIndex,
        int Level);

    private static async Task<EpubReaderDocument?> TryLoadReaderIndexAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(cacheRoot, ReaderIndexFileName);
        if (!File.Exists(indexPath)) return null;

        try
        {
            await using var stream = new FileStream(
                indexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var cache = await JsonSerializer.DeserializeAsync<ReaderIndexCacheDocument>(
                stream,
                ReaderIndexJsonOptions,
                cancellationToken);
            if (cache is null
                || !string.Equals(cache.FormatVersion, ReaderIndexFormatVersion, StringComparison.Ordinal)
                || cache.Chapters is null
                || cache.Navigation is null
                || cache.ChapterTitles is null
                || cache.ChapterTitles.Count != cache.Chapters.Count
                || cache.Chapters.Count == 0)
            {
                return null;
            }

            var chapters = new List<string>(cache.Chapters.Count);
            foreach (var relativePath in cache.Chapters)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) return null;
                var chapterPath = ResolveContainedPath(cacheRoot, relativePath);
                if (!File.Exists(chapterPath)) return null;
                chapters.Add(chapterPath);
            }

            var chapterIndexByPath = CreateChapterIndexLookup(chapters);
            var navigation = new List<EpubReaderNavigationItem>(cache.Navigation.Count);
            foreach (var item in cache.Navigation)
            {
                if (item is null
                    || item.ChapterIndex < 0
                    || item.ChapterIndex >= chapters.Count
                    || string.IsNullOrWhiteSpace(item.Title)
                    || string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    return null;
                }

                var targetPath = ResolveContainedPath(cacheRoot, item.TargetPath);
                if (!File.Exists(targetPath)
                    || !chapterIndexByPath.TryGetValue(targetPath, out var actualChapterIndex)
                    || actualChapterIndex != item.ChapterIndex)
                {
                    return null;
                }

                var fragment = item.Fragment ?? string.Empty;
                if (fragment.Length > 0 && !fragment.StartsWith('#'))
                    fragment = $"#{fragment}";
                navigation.Add(new EpubReaderNavigationItem(
                    item.Title,
                    new Uri(targetPath).AbsoluteUri + fragment,
                    item.ChapterIndex,
                    item.Level));
            }

            return new EpubReaderDocument(
                cacheRoot,
                chapters,
                navigation,
                cache.ChapterTitles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A partial or manually edited metadata file is disposable. The
            // normal preparation path rebuilds it without affecting the book.
            return null;
        }
    }

    private static async Task WriteReaderIndexAsync(
        string cacheRoot,
        EpubReaderDocument document,
        CancellationToken cancellationToken)
    {
        var cachedNavigation = new List<ReaderIndexNavigationItem>(document.Navigation.Count);
        foreach (var item in document.Navigation)
        {
            if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var target)
                || !target.IsFile)
            {
                return;
            }

            var targetPath = Path.GetFullPath(target.LocalPath);
            EnsureContainedPath(cacheRoot, targetPath);
            cachedNavigation.Add(new ReaderIndexNavigationItem(
                item.Title,
                Path.GetRelativePath(cacheRoot, targetPath).Replace('\\', '/'),
                target.Fragment,
                item.ChapterIndex,
                item.Level));
        }

        var cache = new ReaderIndexCacheDocument(
            ReaderIndexFormatVersion,
            document.Chapters
                .Select(chapter => Path.GetRelativePath(cacheRoot, chapter).Replace('\\', '/'))
                .ToList(),
            cachedNavigation,
            document.ChapterTitles.ToList());
        var indexPath = Path.Combine(cacheRoot, ReaderIndexFileName);
        var temporaryPath = Path.Combine(
            cacheRoot,
            $"{ReaderIndexFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    cache,
                    ReaderIndexJsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The index is an optional acceleration layer. A read-only cache
            // or an interrupted replacement must not prevent opening the EPUB.
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }
}
