using Libzora.Layout;

namespace Libzora;

/// <summary>
/// Bounded process-memory cache of font-independent, immutable parsed chapters.
/// Only content-addressed EPUB cache roots qualify; arbitrary XHTML and visible
/// footnote fragments always reload. No book text is written to another file.
/// </summary>
internal sealed class NativeReaderChapterContentCache(int maximumEntries = 12, int maximumWeight = 8 * 1024 * 1024)
{
    internal static NativeReaderChapterContentCache Shared { get; } = new();
    private readonly record struct Key(string Path, bool Indent, long Length, long Modified);
    private sealed record Entry(ChapterContent Content, long LastUse, int Weight);
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];
    private long _use;
    private int _weight;

    internal (int Entries, int RetainedWeight) Snapshot
    {
        get { lock (_gate) return (_entries.Count, _weight); }
    }

    internal ChapterContent Load(string path, bool indent, string? visibleFragment, CancellationToken token, bool cacheResult = true)
    {
        token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(visibleFragment) || !IsContentAddressed(fullPath))
            return new XhtmlChapterLoader(indent, visibleFragment).Load(fullPath, token);
        var info = new FileInfo(fullPath);
        var key = new Key(fullPath, indent, info.Length, info.LastWriteTimeUtc.Ticks);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                _entries[key] = cached with { LastUse = ++_use };
                return cached.Content;
            }
        }
        var content = new XhtmlChapterLoader(indent).Load(fullPath, token);
        token.ThrowIfCancellationRequested();
        var weight = (long)content.BodyText.Length * 4 + key.Length
            + content.Blocks.Count * 128L + content.Blocks.Sum(block => (long)block.Items.Count * 64);
        if (!cacheResult || weight > maximumWeight || maximumEntries < 1) return content;
        info.Refresh();
        if (!info.Exists || info.Length != key.Length || info.LastWriteTimeUtc.Ticks != key.Modified) return content;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var raced)) return raced.Content;
            foreach (var obsolete in _entries.Keys.Where(existing => existing.Path == key.Path && existing.Indent == key.Indent).ToArray())
                Remove(obsolete);
            _entries[key] = new(content, ++_use, (int)weight);
            _weight += (int)weight;
            while (_entries.Count > maximumEntries || _weight > maximumWeight)
                Remove(_entries.MinBy(pair => pair.Value.LastUse).Key);
        }
        return content;
    }

    private void Remove(Key key)
    {
        if (_entries.Remove(key, out var removed)) _weight -= removed.Weight;
    }

    private static bool IsContentAddressed(string path)
    {
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var name = Path.GetFileName(directory);
            if (name.Length == 64 && name.All(Uri.IsHexDigit)) return true;
        }
        return false;
    }
}
