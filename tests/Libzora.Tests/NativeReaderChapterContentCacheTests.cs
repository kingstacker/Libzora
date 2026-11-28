using Xunit;

namespace Libzora.Tests;

public sealed class NativeReaderChapterContentCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "libzora-chapter-cache", Guid.NewGuid().ToString("N"));
    private string Write(string name, string body = "可重复打开的章节正文。", bool addressed = true)
    {
        var directory = addressed ? Path.Combine(_root, new string('c', 64)) : _root;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".xhtml");
        File.WriteAllText(path, "<html><body><p>" + body + "</p></body></html>");
        return path;
    }

    [Fact]
    public void WarmOpenReusesParsedContentWithoutReparsing()
    {
        var cache = new NativeReaderChapterContentCache();
        var path = Write("body");
        var first = cache.Load(path, false, null, CancellationToken.None);
        Assert.Same(first, cache.Load(path, false, null, CancellationToken.None));
        Assert.Equal(1, cache.Snapshot.Entries);
    }

    [Fact]
    public void IndentAndChangedSourceInvalidateTheParsedChapter()
    {
        var cache = new NativeReaderChapterContentCache();
        var path = Write("body");
        var original = cache.Load(path, false, null, CancellationToken.None);
        Assert.NotSame(original, cache.Load(path, true, null, CancellationToken.None));
        File.WriteAllText(path, "<html><body><p>替换后的正文，比原有内容更长以改变源文件大小。</p></body></html>");
        var changed = cache.Load(path, false, null, CancellationToken.None);
        Assert.NotSame(original, changed);
        Assert.Contains("替换后的正文", changed.BodyText);
    }

    [Fact]
    public void FootnoteRevealsAndMutableXhtmlNeverReuseNormalContent()
    {
        var cache = new NativeReaderChapterContentCache();
        var path = Write("body");
        var normal = cache.Load(path, false, null, CancellationToken.None);
        Assert.NotSame(normal, cache.Load(path, false, "note", CancellationToken.None));
        var mutable = Write("mutable", addressed: false);
        Assert.NotSame(cache.Load(mutable, false, null, CancellationToken.None), cache.Load(mutable, false, null, CancellationToken.None));
    }

    [Fact]
    public void CapacityIsBoundedAndWholeBookMeasurementDoesNotPolluteIt()
    {
        var cache = new NativeReaderChapterContentCache(maximumEntries: 2);
        var firstPath = Write("one");
        var first = cache.Load(firstPath, false, null, CancellationToken.None);
        var secondPath = Write("two");
        var second = cache.Load(secondPath, false, null, CancellationToken.None);
        cache.Load(Write("measured"), false, null, CancellationToken.None, cacheResult: false);
        Assert.Same(first, cache.Load(firstPath, false, null, CancellationToken.None));
        cache.Load(Write("three"), false, null, CancellationToken.None);
        Assert.Equal(2, cache.Snapshot.Entries);
        Assert.NotSame(second, cache.Load(secondPath, false, null, CancellationToken.None));
        var tiny = new NativeReaderChapterContentCache(maximumWeight: 128);
        tiny.Load(Write("large", new string('字', 1000)), false, null, CancellationToken.None);
        Assert.Equal(0, tiny.Snapshot.Entries);
        Assert.InRange(tiny.Snapshot.RetainedWeight, 0, 128);
    }

    [Fact]
    public void CancellationStillAppliesToCacheHits()
    {
        var cache = new NativeReaderChapterContentCache();
        var path = Write("body");
        cache.Load(path, false, null, CancellationToken.None);
        Assert.Throws<OperationCanceledException>(() => cache.Load(path, false, null, new CancellationToken(true)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
