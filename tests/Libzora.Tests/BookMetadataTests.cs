using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Libzora.Infrastructure;

namespace Libzora.Tests;

public sealed class BookMetadataTests
{
    [Fact]
    public async Task ReadsTitleAndAuthorsFromAzw3MobiMetadata()
    {
        var root = TestHelpers.CreateTempDirectory();
        var path = Path.Combine(root, "converted.azw3");
        try
        {
            var title = Encoding.UTF8.GetBytes("雪的练习生");
            var author = Encoding.UTF8.GetBytes("作者甲");
            const int firstRecordOffset = 86;
            const int mobiOffset = 16;
            const int exthOffset = 160;
            const int titleOffset = 260;
            var bytes = new byte[512];
            WriteUInt16(bytes, 76, 1);
            WriteUInt32(bytes, 78, firstRecordOffset);
            "MOBI"u8.CopyTo(bytes.AsSpan(firstRecordOffset + mobiOffset));
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x0C, 65001);
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x44, titleOffset);
            WriteUInt32(bytes, firstRecordOffset + mobiOffset + 0x48, title.Length);
            title.CopyTo(bytes.AsSpan(firstRecordOffset + titleOffset));

            var exth = firstRecordOffset + exthOffset;
            "EXTH"u8.CopyTo(bytes.AsSpan(exth));
            WriteUInt32(bytes, exth + 4, 20 + author.Length);
            WriteUInt32(bytes, exth + 8, 1);
            WriteUInt32(bytes, exth + 12, 100);
            WriteUInt32(bytes, exth + 16, 8 + author.Length);
            author.CopyTo(bytes.AsSpan(exth + 20));
            await File.WriteAllBytesAsync(path, bytes);

            var metadata = await new BookMetadataService().ReadMetadataAsync(path);

            Assert.Equal("雪的练习生", metadata.Title);
            Assert.Equal("作者甲", metadata.Authors);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("<meta name='calibre:series' content='Library Series'/><meta name='calibre:series_index' content='2.5'/>", "Library Series", 2.5)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Library Series</meta><meta refines='#s' property='collection-type'>series</meta><meta refines='#other' property='group-position'>99</meta><meta refines='#s' property='group-position'>2.5</meta>", "Library Series", 2.5)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Publisher Set</meta><meta refines='#s' property='collection-type'>set</meta>", null, null)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Library Series</meta><meta refines='#s' property='collection-type'>series</meta><meta name='calibre:series' content='Library Series'/><meta name='calibre:series_index' content='3.5'/>", "Library Series", 3.5)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Library Series</meta><meta refines='#s' property='collection-type'>series</meta><meta refines='#s' property='group-position'>2</meta><meta name='calibre:series' content='Library Series'/><meta name='calibre:series_index' content='3.5'/>", "Library Series", 2.0)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Library Series</meta><meta refines='#s' property='collection-type'>series</meta><meta name='calibre:series' content='Other Series'/><meta name='calibre:series_index' content='3.5'/>", "Library Series", null)]
    [InlineData("<meta property='belongs-to-collection' id='série'>Library Series</meta><meta refines='#s%C3%A9rie' property='collection-type'>series</meta><meta refines='#s%C3%A9rie' property='group-position'>4.5</meta>", "Library Series", 4.5)]
    [InlineData("<meta name='calibre:series' content='Library Series'/><meta name='calibre:series_index' content='1e999'/>", "Library Series", null)]
    [InlineData("<meta property='belongs-to-collection' id='s'>Library Series</meta><meta refines='#s' property='collection-type'>series</meta><meta refines='#s' property='group-position'>NaN</meta><meta name='calibre:series' content='Library Series'/><meta name='calibre:series_index' content='2'/>", "Library Series", 2.0)]
    public async Task ReadsOnlySeriesCollectionsFromEpub(string properties, string? name, double? index)
    {
        var root = TestHelpers.CreateTempDirectory();
        var path = Path.Combine(root, "series.epub");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                await WriteEntryAsync("META-INF/container.xml", "<container><rootfiles><rootfile full-path='book.opf'/></rootfiles></container>");
                await WriteEntryAsync("book.opf", $"<package><metadata><title>Volume</title>{properties}</metadata></package>");

                async Task WriteEntryAsync(string entry, string contents)
                {
                    await using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                    await writer.WriteAsync(contents);
                }
            }

            var metadata = await new BookMetadataService().ReadMetadataAsync(path);

            Assert.Equal(name, metadata.Series);
            Assert.Equal(index, metadata.SeriesIndex);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value);

    private static void WriteUInt32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), (uint)value);
}
