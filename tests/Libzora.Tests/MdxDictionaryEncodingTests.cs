using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class MdxDictionaryEncodingTests
{
    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16")]
    [InlineData("GBK")]
    public async Task ImportsUsingDeclaredEncodingAndLooksUpDefinition(string encodingName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "sample.mdx");
            await File.WriteAllTextAsync(Path.Combine(root, "sample.css"), ".sense { color: blue; }");
            var encoding = Encoding.GetEncoding(encodingName);
            await File.WriteAllBytesAsync(file, CreateMdx(encodingName, encoding));
            var service = new DictionaryService(new AppPaths(Path.Combine(root, "app")));
            var imported = await service.ImportAsync(file);
            Assert.Equal(1, imported.EntryCount);
            var entry = Assert.Single(await service.LookupAsync("apple"));
            Assert.Contains("苹果", entry.Definition);
            Assert.Contains("<p>", entry.Html);
            Assert.Contains(".sense", entry.Stylesheet);
            // Reimporting a dictionary from the old plain-text format upgrades it in place.
            var database = Directory.GetFiles(root, "*.rich.db", SearchOption.AllDirectories).Single();
            File.Delete(database);
            Assert.Null(Assert.Single(await service.LookupAsync("apple")).Html);
            Assert.Equal(imported.Id, (await service.ImportAsync(file)).Id);
            Assert.NotNull(Assert.Single(await service.LookupAsync("APPLE")).Html);
            await service.RemoveAsync(imported.Id);
            Assert.False(File.Exists(database));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResolvesMdxLinkRedirectsAndClosingHeaderTags()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "linked.mdx");
            await File.WriteAllBytesAsync(file, CreateMdxWithEntries(
                "UTF-8",
                Encoding.UTF8,
                [
                    ("apple", "<p>fruit</p>"),
                    ("apples", "@@@LINK=apple\r\n"),
                    ("loop1", "@@@LINK=loop2"),
                    ("loop2", "@@@LINK=loop1")
                ],
                useClosingTag: true));
            var service = new DictionaryService(new AppPaths(Path.Combine(root, "app")));
            var imported = await service.ImportAsync(file);
            Assert.Equal(4, imported.EntryCount);
            var redirected = Assert.Single(await service.LookupAsync("apples"));
            Assert.Equal("apple", redirected.Term);
            Assert.Contains("fruit", redirected.Definition);
            Assert.Contains("<p>fruit</p>", redirected.Html);
            var loop = Assert.Single(await service.LookupAsync("loop1"));
            Assert.StartsWith("@@@LINK=", loop.Definition);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NormalizesWikipediaExtractParagraphEdges()
    {
        var raw = "  第一段内容。  \r\n\r\n\r\n   第二段内容。 \n  \n";
        Assert.Equal("第一段内容。\n\n第二段内容。", WikipediaLookupService.NormalizeExtract(raw));
    }

    private static byte[] CreateMdx(string name, Encoding encoding) =>
        CreateMdxWithEntries(name, encoding, [("apple", "<p>苹果 fruit</p>")], useClosingTag: false);

    private static byte[] CreateMdxWithEntries(
        string name,
        Encoding encoding,
        IReadOnlyList<(string Key, string Value)> items,
        bool useClosingTag)
    {
        using var output = new MemoryStream();
        var headerText = useClosingTag
            ? $"<Dictionary GeneratedByEngineVersion=\"2.0\" RequiredEngineVersion=\"2.0\" Encoding=\"{name}\" Encrypted=\"No\" StyleSheet=\"1\n&lt;b&gt;\n&lt;/b&gt;\n\"></Dictionary>\0"
            : $"<Dictionary GeneratedByEngineVersion=\"2.0\" RequiredEngineVersion=\"2.0\" Encoding=\"{name}\" Encrypted=\"No\"/>\0";
        var header = Encoding.Unicode.GetBytes(headerText);
        WriteNumber(output, header.Length, 4);
        output.Write(header);
        output.Write(BitConverter.GetBytes(Adler(header)));

        using var keysStream = new MemoryStream();
        using var recordsStream = new MemoryStream();
        long recordOffset = 0;
        byte[]? firstWord = null;
        byte[]? lastWord = null;
        foreach (var (keyText, valueText) in items)
        {
            var wordBytes = encoding.GetBytes(keyText + "\0");
            firstWord ??= wordBytes;
            lastWord = wordBytes;
            WriteNumber(keysStream, recordOffset);
            keysStream.Write(wordBytes);
            var recordBytes = encoding.GetBytes(valueText + "\0");
            recordsStream.Write(recordBytes);
            recordOffset += recordBytes.Length;
        }
        var keyBlock = keysStream.ToArray();
        var recordBlock = recordsStream.ToArray();

        using var info = new MemoryStream();
        WriteNumber(info, items.Count);
        WriteNumber(info, items[0].Key.Length, 2);
        info.Write(firstWord!);
        WriteNumber(info, items[^1].Key.Length, 2);
        info.Write(lastWord!);
        WriteNumber(info, keyBlock.Length + 8);
        WriteNumber(info, keyBlock.Length);
        using var compressed = new MemoryStream();
        compressed.Write(new byte[] { 2, 0, 0, 0 });
        WriteNumber(compressed, Adler(info.ToArray()), 4);
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) info.WriteTo(zlib);
        using var numbers = new MemoryStream();
        foreach (var number in new long[] { 1, items.Count, info.Length, compressed.Length, keyBlock.Length + 8 }) WriteNumber(numbers, number);
        numbers.WriteTo(output);
        WriteNumber(output, Adler(numbers.ToArray()), 4);
        compressed.WriteTo(output);
        output.Write(new byte[8]); // uncompressed block marker and unused checksum
        output.Write(keyBlock);
        foreach (var number in new long[] { 1, items.Count, 16, recordBlock.Length + 8, recordBlock.Length + 8, recordBlock.Length }) WriteNumber(output, number);
        output.Write(new byte[8]);
        output.Write(recordBlock);
        return output.ToArray();
    }

    private static void WriteNumber(Stream stream, long value, int width = 8)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes[(8 - width)..]);
    }

    private static uint Adler(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
