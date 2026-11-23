using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices;

/// <summary>Restores EPUB locations without involving a window or rendering surface.</summary>
public static class ReaderResourcePolicy
{
    private static StringComparison FilePathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static ReaderProgressRow? RestoreProgress(EpubReaderDocument document, ReaderProgressRow? progress)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (progress is null || string.IsNullOrWhiteSpace(progress.ChapterPath)) return null;
        try
        {
            var savedPath = Path.GetFullPath(Path.Combine(document.RootPath,
                progress.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInside(document.RootPath, savedPath)) return null;
            var isAlias = document.ChapterAliases.TryGetValue(savedPath, out var canonicalPath);
            if (isAlias) savedPath = canonicalPath!;
            var chapterIndex = document.Chapters
                .Select((chapter, index) => (chapter, index))
                .Where(item => string.Equals(Path.GetFullPath(item.chapter), savedPath,
                    FilePathComparison))
                .Select(item => item.index)
                .DefaultIfEmpty(-1)
                .First();
            if (chapterIndex < 0) return null;
            return progress with
            {
                ChapterPath = isAlias
                    ? Path.GetRelativePath(document.RootPath, savedPath).Replace('\\', '/')
                    : progress.ChapterPath,
                ChapterIndex = chapterIndex,
                ScrollPosition = isAlias ? 0 : Math.Max(0, progress.ScrollPosition),
                Fragment = isAlias ? null : DecodeFragment(progress.Fragment),
                ContentPosition = isAlias ? null : ReaderContentPosition.Validate(progress.ContentPosition)
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, FilePathComparison);
    }

    public static string? DecodeFragment(string? value)
    {
        var fragment = value?.TrimStart('#');
        if (string.IsNullOrWhiteSpace(fragment)) return null;
        try { return Uri.UnescapeDataString(fragment); }
        catch { return fragment; }
    }
}
