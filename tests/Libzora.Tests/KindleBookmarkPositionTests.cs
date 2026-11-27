using System.Reflection;
using System.Text;
using System.Text.Json;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class KindleBookmarkPositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentBookmarksAreNotMergedByPrefixOrByteProximity(bool samePrefix)
    {
        var first = samePrefix ? "The same beginning of this bookmark text FIRST unique target with trailing context." : "Alpha bookmark starts here with its distinctive short sentence.";
        var second = samePrefix ? "The same beginning of this bookmark text SECOND different target with trailing context." : "Beta bookmark is a different point with another distinctive sentence.";
        var gap = samePrefix ? new string('x', 300) : "separator";
        using var fixture = new Fixture(Html(first, gap, second), Body(first, gap, second));
        var local = fixture.Local(second, withAnchor: true);
        var plan = await fixture.Prepare([fixture.Device(first)], [local]);
        Assert.Single(plan.NewImports);
        Assert.Equal(fixture.Byte(second), Assert.Single(plan.Mutations).Start);
    }

    [Fact]
    public async Task SavedQuoteOverridesStaleValidNumericOffset()
    {
        const string intro = "New introductory paragraph changes the old source positions significantly.";
        const string target = "Original bookmark target must be located using its saved source quote.";
        using var fixture = new Fixture(Html(intro, target), Body(intro, target));
        var local = fixture.Local(target, withAnchor: false);
        local.ContentPosition = new() { TextOffset = 0 };
        var plan = await fixture.Prepare([], [local]);
        Assert.Equal(fixture.Byte(target), Assert.Single(plan.Mutations).Start);
        var repaired = Assert.Single(plan.ComputerUpdates);
        Assert.Equal(local.Id, repaired.Id);
        Assert.Equal(local.CreatedAt, repaired.CreatedAt);
        Assert.Equal(fixture.Body.IndexOf(target, StringComparison.Ordinal), repaired.ContentPosition!.TextOffset);
        Assert.Equal(0, local.ContentPosition.TextOffset); // analysis never mutates the caller's record
    }

    [Fact]
    public async Task SavedContextCanRelocateWithoutFollowingStaleOffsetText()
    {
        const string intro = "Freshly inserted introductory paragraph.";
        const string previous = "The original preceding context remains present and long enough for a stable anchor.";
        const string target = "Original bookmarked text after this unchanged preceding context.";
        using var fixture = new Fixture(Html(intro, previous, target), Body(intro, previous, target));
        var local = fixture.Local(target, withAnchor: true);
        local.ContentPosition = local.ContentPosition! with { TextOffset = 0 };
        var plan = await fixture.Prepare([], [local]);
        Assert.Equal(fixture.Byte(target), Assert.Single(plan.Mutations).Start);
    }

    [Fact]
    public async Task StableIdWinsBeforeEarlierNearbyBookmark()
    {
        const string first = "First nearby bookmark has its own unique text and a sufficiently long tail.";
        const string second = "Second owned bookmark is distinct and must retain its stable identity.";
        using var fixture = new Fixture(Html(first, second), Body(first, second));
        var earlier = fixture.Local(first, true);
        var owned = fixture.Local(second, true);
        var device = fixture.Device(second, "libzora.bookmark." + owned.Id.ToString("N"));
        var plan = await fixture.Prepare([device], [earlier, owned]);
        Assert.Empty(plan.NewImports);
        var push = Assert.Single(plan.Mutations);
        Assert.Equal("libzora.bookmark." + earlier.Id.ToString("N"), push.AnnotationId);
        Assert.Equal(fixture.Byte(first), push.Start);
    }

    [Theory]
    [InlineData("libzora.bookmark.")]
    [InlineData("kkindle.bookmark.")]
    public async Task OwnedBookmarkPositionRepairPreservesIdentityTimeAndMetadata(string prefix)
    {
        const string first = "Old incorrect paragraph where the saved byte position used to point.";
        const string target = "Correct original bookmark target with unique words and enough trailing text.";
        using var fixture = new Fixture(Html(first, target), Body(first, target));
        var local = fixture.Local(target, true);
        var device = fixture.Device(first, prefix + local.Id.ToString("N"));
        var plan = await fixture.Prepare([device], [local]);
        Assert.Empty(plan.NewImports);
        var mutation = Assert.Single(plan.Mutations);
        Assert.Equal(device.AnnotationId, mutation.AnnotationId);
        Assert.Equal(device.CreatedTime, mutation.CreatedTime);
        Assert.Equal(fixture.Byte(target), mutation.Start);
        Assert.Equal(mutation.Start, mutation.End);
        using var payload = JsonDocument.Parse(mutation.SerializedPayload);
        Assert.Equal("preserve", payload.RootElement.GetProperty("custom_metadata").GetString());
        Assert.Equal(mutation.Start.ToString(), payload.RootElement.GetProperty("start_position").GetProperty("longPosition").GetString());
    }

    [Fact]
    public async Task NativeDeviceBookmarkIsNeverAutomaticallyRelocated()
    {
        const string first = "Existing Kindle-made bookmark has its own independent text target.";
        const string second = "Independent local bookmark needs to be merged, not overwrite the first.";
        using var fixture = new Fixture(Html(first, second), Body(first, second));
        var device = fixture.Device(first, "kindle-native-bookmark");
        var plan = await fixture.Prepare([device], [fixture.Local(second, true)]);
        Assert.Single(plan.NewImports);
        Assert.Single(plan.Mutations);
        Assert.DoesNotContain(plan.Mutations, mutation => mutation.AnnotationId == device.AnnotationId);
    }

    [Fact]
    public async Task DuplicateDevicePointsProduceOneImport()
    {
        const string text = "A single bookmarked paragraph with enough unique text for a reliable anchor.";
        using var fixture = new Fixture(Html(text), text);
        var plan = await fixture.Prepare([fixture.Device(text, "one"), fixture.Device(text, "two")], []);
        Assert.Single(plan.NewImports);
        Assert.Empty(plan.Mutations);
        Assert.Equal(0, plan.Unsupported);
    }

    [Fact]
    public async Task DuplicateLocalPointsProduceOneDeviceWrite()
    {
        const string text = "A single local point must not create two separate records on the Kindle.";
        using var fixture = new Fixture(Html(text), text);
        var plan = await fixture.Prepare([], [fixture.Local(text, true), fixture.Local(text, true)]);
        Assert.Single(plan.Mutations);
    }

    [Fact]
    public async Task OverlappingRepeatedTextUsesContextToFindTheCorrectCharacter()
    {
        var text = new string('A', 33) + "Z";
        using var fixture = new Fixture(Html(text), text);
        var device = fixture.Device(text) with { Start = fixture.Byte(text) + 1, End = fixture.Byte(text) + 1 };
        var plan = await fixture.Prepare([device], []);
        Assert.Equal(1, Assert.Single(plan.NewImports).ContentPosition!.TextOffset);
    }

    [Fact]
    public async Task MissingOriginalParagraphIsNotReplacedByUnrelatedShortPrefix()
    {
        const string original = "This shared prefix belongs to the original deleted paragraph with different text.";
        const string unrelated = "This shared prefix belongs elsewhere with entirely different following content.";
        using var fixture = new Fixture(Html(original), unrelated);
        var plan = await fixture.Prepare([fixture.Device(original)], []);
        Assert.Empty(plan.ComputerUpdates);
        Assert.Empty(plan.Mutations);
        Assert.Equal(1, plan.Unsupported);
    }

    [Fact]
    public async Task FullQuoteWithConflictingContextIsNotGuessed()
    {
        var quote = new string('Q', 32);
        var source = "original preceding context " + quote + " original following context";
        var local = "different preceding context " + quote + " different following context";
        using var fixture = new Fixture(Html(source), local);
        var plan = await fixture.Prepare([fixture.Device(quote)], []);
        Assert.Empty(plan.NewImports);
        Assert.Equal(1, plan.Unsupported);
    }

    [Fact]
    public async Task ShortChapterEndAnchorDoesNotCrossIntoNextChapter()
    {
        const string first = "TAILUNIQUE";
        const string second = "Second chapter starts with completely unrelated text and enough characters.";
        using var fixture = new Fixture(Html(first) + Html(second), [("one.xhtml", first), ("two.xhtml", second)]);
        var device = fixture.Device(first) with { Start = fixture.Byte(first) + 4, End = fixture.Byte(first) + 4 };
        var plan = await fixture.Prepare([device], []);
        var imported = Assert.Single(plan.NewImports);
        Assert.Equal("one.xhtml", imported.ChapterPath);
        Assert.Equal(4, imported.ContentPosition!.TextOffset);
        Assert.Equal("UNIQUE", imported.Quote);
    }

    [Fact]
    public async Task MobiPageBreakBoundsShortTailWithoutParagraphTags()
    {
        const string first = "TAILUNIQUE";
        const string second = "The next chapter has unrelated text that must not extend the preceding anchor.";
        using var fixture = new Fixture("<body>" + first + "<mbp:pagebreak/>" + second + "</body>",
            [("one.xhtml", first), ("two.xhtml", second)]);
        var device = fixture.Device(first) with { Start = fixture.Byte(first) + 4, End = fixture.Byte(first) + 4 };
        var plan = await fixture.Prepare([device], []);
        var imported = Assert.Single(plan.NewImports);
        Assert.Equal("one.xhtml", imported.ChapterPath);
        Assert.Equal(4, imported.ContentPosition!.TextOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceImageBookmarkDoesNotJumpToFollowingText(bool inline)
    {
        const string next = "Next chapter text must never stand in for the bookmarked illustration.";
        var html = inline ? "<body><p><img src='picture.jpg'/>" + next + "</p></body>"
            : "<body><img src='picture.jpg'/></body>" + Html(next);
        using var fixture = new Fixture(html, next);
        var device = fixture.Device(next) with { Start = fixture.Byte("<img"), End = fixture.Byte("<img") };
        var plan = await fixture.Prepare([device], []);
        Assert.Empty(plan.ComputerUpdates);
        Assert.Equal(1, plan.Unsupported);
        Assert.Equal(0, plan.UnsupportedDeviceAnnotations); // bookmark counts must not inflate highlight/note counts
    }

    [Fact]
    public async Task LocalImageBookmarkDoesNotFallBackToTextQuote()
    {
        const string text = "Following body text is not the image bookmark's source location.";
        using var fixture = new Fixture(Html(text), text);
        var local = fixture.Local(text, true);
        local.ContentPosition = local.ContentPosition! with { ImageIndex = 0 };
        var plan = await fixture.Prepare([], [local]);
        Assert.Empty(plan.Mutations);
        Assert.Equal(1, plan.Unsupported);
    }

    [Fact]
    public async Task AmbiguousRepeatedQuoteIsSkippedRatherThanWrittenAtFirstOccurrence()
    {
        const string repeated = "The entire quote repeats and no trustworthy context has been saved.";
        using var fixture = new Fixture(Html(repeated, repeated), Body(repeated, repeated));
        var local = fixture.Local(repeated, false);
        var plan = await fixture.Prepare([], [local]);
        Assert.Empty(plan.Mutations);
        Assert.Equal(1, plan.Unsupported);
    }

    [Fact]
    public async Task BookmarkOnMultibyteContinuationMapsToContainingCharacter()
    {
        const string text = "你好，世界。这里是足够独特的书签正文。";
        using var fixture = new Fixture(Html(text), text);
        var device = fixture.Device(text) with { Start = fixture.Byte(text) + 1, End = fixture.Byte(text) + 1 };
        var plan = await fixture.Prepare([device], []);
        Assert.Equal(0, Assert.Single(plan.NewImports).ContentPosition!.TextOffset);
    }

    private static string Html(params string[] paragraphs) => "<html><body>" + string.Concat(paragraphs.Select(text => "<p>" + text + "</p>")) + "</body></html>";
    private static string Body(params string[] paragraphs) => string.Join('\n', paragraphs);

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type ContextType = typeof(KindleAnnotationSyncService).GetNestedType("BookContext", BindingFlags.NonPublic)!;
        private readonly string _root = TestHelpers.CreateTempDirectory();
        private readonly string _html;
        private readonly Book _book = new() { Id = Guid.NewGuid(), Title = "Bookmark fixture" };
        private readonly BookFile _file = new() { Id = Guid.NewGuid(), Format = "epub", Sha256 = "fixture" };
        private readonly IReadOnlyList<BookContentChunk> _chunks;
        private readonly KindleAnnotationSyncService _service;
        public string Body => _chunks[0].Content;
        public KindleRawBookText Raw { get; }
        public Fixture(string html, string body) : this(html, [("chapter.xhtml", body)]) { }
        public Fixture(string html, (string Path, string Body)[] chapters)
        {
            _html = html;
            Raw = new(Encoding.UTF8.GetBytes(html), new UTF8Encoding(false, true), "Fixture", "fixture", 123, "Fixture", "Author");
            _file.BookId = _book.Id;
            _book.Files.Add(_file);
            _chunks = chapters.Select((chapter, index) => new BookContentChunk(0, _book.Id, _file.Id, "fixture", index, 0,
                "Chapter", chapter.Path, 0, chapter.Body.Length, chapter.Body, 0)).ToArray();
            _service = new(new AppPaths(_root), null!, null!, null!);
        }
        public long Byte(string text) => Encoding.UTF8.GetByteCount(_html.AsSpan(0, _html.IndexOf(text, StringComparison.Ordinal)));
        public ReaderBookmark Local(string text, bool withAnchor)
        {
            var offset = Body.IndexOf(text, StringComparison.Ordinal);
            var position = new ReaderContentPosition { TextOffset = offset };
            return new() { BookId = _book.Id, BookFileId = _file.Id, ChapterPath = _chunks[0].ChapterPath,
                ContentPosition = withAnchor ? position.WithTextAnchor(Body) : position,
                Quote = text[..Math.Min(32, text.Length)] };
        }
        public KindleAnnotationRecord Device(string text, string id = "native-bookmark")
        {
            var pos = Byte(text);
            var created = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            return new(id, new(Raw.CreateBookId()!, Raw.Asin, Raw.CreateGuid()), 2, KindleClippingType.Bookmark, pos, pos,
                created, created, "", JsonSerializer.Serialize(new { type = "BOOKMARK", custom_metadata = "preserve",
                    created_time = created, last_modified = created, json_metadata = "{\"mchl_color\":\"dark_blue\"}",
                    start_position = new { longPosition = pos.ToString(), shortPosition = pos },
                    end_position = new { longPosition = pos.ToString(), shortPosition = pos } }), _book.Title);
        }
        public async Task<Plan> Prepare(KindleAnnotationRecord[] records, ReaderBookmark[] locals)
        {
            var snapshot = new KindleAnnotationSnapshot("system/ksdk/.annotations/fixture/ksdk_annotation_v1.db", new string('a', 64), records);
            var context = Activator.CreateInstance(ContextType, All, null,
                [snapshot, _book, new KindleBook { Format = "azw3" }, Array.Empty<ReaderAnnotation>(), locals, Raw, records], null)!;
            var chapters = (Dictionary<Guid, IReadOnlyList<BookContentChunk>>)ContextType.GetProperty("ReaderChapters")!.GetValue(context)!;
            chapters[Guid.Empty] = _chunks;
            chapters[_file.Id] = _chunks;
            await (Task)typeof(KindleAnnotationSyncService).GetMethod("PrepareBookmarksAsync", All)!.Invoke(_service, [context, CancellationToken.None])!;
            var array = Array.CreateInstance(ContextType, 1);
            array.SetValue(context, 0);
            var mutations = ((List<KindleAnnotationMutation>, int, int))typeof(KindleAnnotationSyncService)
                .GetMethod("BuildDeviceMutations", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [array])!;
            var imports = (List<ReaderBookmark>)ContextType.GetProperty("BookmarksToComputer")!.GetValue(context)!;
            return new(imports, imports.Where(bookmark => !locals.Any(local => local.Id == bookmark.Id)).ToArray(), mutations.Item1,
                (int)ContextType.GetProperty("UnsupportedCount")!.GetValue(context)!,
                (int)ContextType.GetProperty("DeviceUnsupportedCount")!.GetValue(context)!);
        }
        public void Dispose() => TestHelpers.TryDelete(_root);
    }
    private sealed record Plan(IReadOnlyList<ReaderBookmark> ComputerUpdates, IReadOnlyList<ReaderBookmark> NewImports,
        IReadOnlyList<KindleAnnotationMutation> Mutations, int Unsupported, int UnsupportedDeviceAnnotations);
}
