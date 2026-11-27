using MdxParser.Models;
using NeoSmart.Hashing.XXHash;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace MdxParser
{
    public class MdxDocument : AbsoluteBlock
    {
        #region property
        private Stream m_Stream;
        private Encoding? m_Encoding;
        private int m_Encrypt;
        private byte[] m_EncryptKey;
        private long m_BlockStartIndex;
        private Dictionary<string, (string, string)> m_StyleSheet;
        public MdxHeader Header { get; set; }
        [JsonIgnore]
        public Stream Stream { get => m_Stream; set => m_Stream = value; }
        [JsonIgnore]
        public byte[]? EncryptKey
        {
            get
            {
                if (m_EncryptKey == null)
                {
                    if (Header.UUID != null)
                    {
                        var mid = (Header.UUID.Length + 1) / 2;
                        var fDigest = XXHash64.Hash(Encoding.ASCII.GetBytes(Header.UUID.Substring(0, mid)));
                        var eDigest = XXHash64.Hash(Encoding.ASCII.GetBytes(Header.UUID.Substring(mid)));
                        List<byte> bytes = new List<byte>();
                        bytes.AddRange(Enumerable.Reverse(BitConverter.GetBytes(fDigest)));
                        bytes.AddRange(Enumerable.Reverse(BitConverter.GetBytes(eDigest)));
                        return bytes.ToArray();
                    }
                }
                return m_EncryptKey;
            }
        }

        [JsonIgnore]
        public Encoding Encoding
        {
            get
            {
                if (m_Encoding == null)
                {
                    if (Version >= 3.0)
                        m_Encoding = Encoding.UTF8;
                    else if ("GBK".Equals(Header?.Encoding, StringComparison.OrdinalIgnoreCase)
                             || "GB2312".Equals(Header?.Encoding, StringComparison.OrdinalIgnoreCase))
                        m_Encoding = Encoding.GetEncoding("GB18030");
                    else if ("UTF-16LE".Equals(Header?.Encoding, StringComparison.OrdinalIgnoreCase)
                             || "UTF16".Equals(Header?.Encoding, StringComparison.OrdinalIgnoreCase))
                        m_Encoding = Encoding.GetEncoding("utf-16");
                    else if (!string.IsNullOrEmpty(Header?.Encoding))
                        m_Encoding = Encoding.GetEncoding(Header.Encoding);
                    else
                        m_Encoding = Encoding.UTF8;
                }
                return m_Encoding;
            }
        }
        public int Encrypt { get => m_Encrypt; set => m_Encrypt = value; }
        public long BlockStartIndex { get => m_BlockStartIndex; set => m_BlockStartIndex = value; }
        public float? Version { get => Header?.GeneratedByEngineVersion; }
        /// <summary>
        /// 数量长度，4或8
        // before version 2.0, number is 4 bytes integer
        // version 2.0 and above uses 8 bytes
        /// </summary>
        public int NumberWidth { get => Version < 2.0 ? 4 : 8; }

        private Dictionary<BlockType, MdxBlock> m_Blocks = new Dictionary<BlockType, MdxBlock>();
        public Dictionary<BlockType, MdxBlock> Blocks => m_Blocks;

        public List<KeyData> KeyList { get => m_KeyList; }
        #endregion

        #region Ctr
        static MdxDocument() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        public MdxDocument(string path) : this(path, null) { }
        public MdxDocument(string path, Encoding? encoding) : this(File.OpenRead(path), encoding)
        {
        }
        public MdxDocument(Stream stream, Encoding? encoding = null)
        {
            Stream = stream;

            // A null override lets the MDX header select UTF-8, UTF-16 or a legacy code page.
            m_Encoding = encoding;

            Header = readHeader();

            readBlocks();
        }
        #endregion

        #region private
        private void readBlocks()
        {
            if (Version >= 3.0)
            {
                readBlockV3();
            }
            else
            {
                // if no regcode is given, try brutal force (only for engine <= 2)
                if (Encrypt == 1 && EncryptKey == null)
                {

                    Console.WriteLine("Try Brutal Force on Encrypted Key Blocks");
                    readKeysBrutal();
                }
                else
                    readKeysV2();
            }
        }

        private void readBlockV3()
        {
            Stream.Position = BlockStartIndex;
            long currentIndex = BlockStartIndex;
            while (currentIndex < Stream.Length)
            {
                var block = new MdxBlock(this, currentIndex);
                Blocks.Add(block.Type, block);
                currentIndex = block.EndIndex;
            }
        }
        private void readKeysV2()
        {
            Stream.Position = BlockStartIndex;
            long currentIndex = BlockStartIndex;
            while (currentIndex < Stream.Length)
            {
                var block = new MdxBlock(this, currentIndex);
                Blocks.Add(block.Type, block);
                currentIndex = block.EndIndex;
            }



            //    # read key block
            //    key_block_compressed = f.read(key_block_size)
            //    # extract key block
            //    key_list = self._decode_key_block(key_block_compressed, key_block_info_list)

            //    self._record_block_offset = f.tell()
            //    f.close()

            //    return key_list
        }

        private void readKeysBrutal()
        {
            throw new NotImplementedException();
        }


        private MdxHeader readHeader()
        {
            Stream.Position = 0;

            var headSize = readInt32(Stream);
            var headStartIndex = 4;


            var headBuf = new byte[headSize];
            readBytes(Stream, headBuf);

            byte[] utf8headBuf = headBuf.Length >= 2 && headBuf[(headBuf.Length - 2)..].SequenceEqual(new byte[] { 0, 0 }) ?
                Encoding.Convert(Encoding.GetEncoding("utf-16"), Encoding.UTF8, headBuf) :
                headBuf;

            var headStr = Encoding.UTF8.GetString(utf8headBuf).TrimEnd('\0');  // head以字节为x00结尾，此处去除；
            var adler32 = (uint)readInt32B(Stream);
            var headHash = adler32Compute(headBuf);
            if (headHash != adler32)
                throw new Exception("document is adler32 valid.");

            BlockStartIndex = Stream.Position;

            MdxHeader header = parseHeader(headStr);
            header.Size = headSize + 4;
            header.StartIndex = headStartIndex;


            // encryption flag
            if (header.Encrypted == null || header.Encrypted.Equals("No"))
                m_Encrypt = 0;
            else if (header.Encrypted.Equals("Yes"))
                m_Encrypt = 1;
            else
                int.TryParse(header.Encrypted, out m_Encrypt);

            // stylesheet attribute if present takes form of:
            // style_number # 1-255
            // style_begin  # or ''
            // style_end    # or ''
            // store stylesheet in dict in the form of
            // {'number' : ('style_begin', 'style_end')}
            m_StyleSheet = new Dictionary<string, (string, string)>();
            if (header.StyleSheet != null)
            {
                var lines = header.StyleSheet.Split('\n');
                for (int i = 0; i + 2 < lines.Length; i += 3)
                {
                    m_StyleSheet[lines[i]] = (lines[i + 1], lines[i + 2]);
                }
            }
            return header;
        }
        private MdxHeader parseHeader(string headStr)
        {
            headStr = headStr.TrimEnd('\0').Trim();
            if (headStr.StartsWith("<Library_Data", StringComparison.Ordinal))
                m_Encoding = Encoding.GetEncoding("utf-16");

            // Parse directly with XElement instead of XmlSerializer so iOS AOT
            // and trimmed builds do not rely on runtime code generation.
            var element = XElement.Parse(headStr);
            string? Attr(string name) =>
                element.Attribute(name)?.Value
                ?? element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
            float FloatAttr(string name) =>
                float.TryParse(Attr(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0f;

            return new MdxHeader
            {
                UUID = Attr("UUID"),
                GeneratedByEngineVersion = FloatAttr("GeneratedByEngineVersion"),
                RequiredEngineVersion = FloatAttr("RequiredEngineVersion"),
                Encrypted = Attr("Encrypted"),
                Encoding = Attr("Encoding"),
                Format = Attr("Format"),
                Compact = Attr("Compact"),
                Compat = Attr("Compat"),
                KeyCaseSensitive = Attr("KeyCaseSensitive"),
                Description = Attr("Description"),
                Title = Attr("Title"),
                DataSourceFormat = Attr("DataSourceFormat"),
                StyleSheet = Attr("StyleSheet")
            };
        }
        #endregion
        private List<KeyData> m_KeyList;
        [JsonIgnore]
        public List<KeyData> KeyDatas
        {
            get
            {
                if (m_KeyList == null)
                {
                    var keyBlocks = Blocks[BlockType.KeyData].KeyBlocks;
                    if (keyBlocks.Count > 1)
                    {
                        var perBlock = new List<KeyData>[keyBlocks.Count];
                        Parallel.For(0, keyBlocks.Count, index =>
                        {
                            perBlock[index] = splitKeyBlockList(keyBlocks[index].getDecompressedData());
                        });
                        var total = 0;
                        foreach (var list in perBlock) total += list.Count;
                        m_KeyList = new List<KeyData>(total);
                        foreach (var list in perBlock) m_KeyList.AddRange(list);
                    }
                    else
                    {
                        m_KeyList = new List<KeyData>();
                        foreach (var kb in keyBlocks)
                            m_KeyList.AddRange(splitKeyBlockList(kb.getDecompressedData()));
                    }
                }
                return m_KeyList;
            }
        }
        private List<KeyData> splitKeyBlockList(byte[] keyData)
        {
            var result = new List<KeyData>();
            int startIndex = 0;
            int numberWidth = NumberWidth;
            var encoding = Encoding;
            bool isUtf16 = encoding.CodePage is 1200 or 1201;
            int width = isUtf16 ? 2 : 1;
            var span = keyData.AsSpan();
            while (startIndex + numberWidth <= keyData.Length)
            {
                var keyId = readNumber(span.Slice(startIndex, numberWidth));
                int textStart = startIndex + numberWidth;
                int endIndex = keyData.Length;
                if (!isUtf16)
                {
                    int rel = span.Slice(textStart).IndexOf((byte)0);
                    if (rel >= 0) endIndex = textStart + rel;
                }
                else
                {
                    for (int i = textStart; i + 1 < keyData.Length; i += 2)
                    {
                        if (keyData[i] == 0 && keyData[i + 1] == 0)
                        {
                            endIndex = i;
                            break;
                        }
                    }
                }
                var keyText = encoding.GetString(keyData, textStart, endIndex - textStart);
                var key = KeyData.of(keyId, keyText);
                key.StartIndex = startIndex;
                key.Size = endIndex + width - startIndex;
                startIndex = endIndex + width;
                result.Add(key);
            }
            return result;
        }

        private List<RecordData> m_RecordList;
        [JsonIgnore]
        public List<RecordData> RecordDatas
        {
            get
            {
                if (m_RecordList == null)
                {
                    var keys = KeyDatas;
                    var encoding = Encoding;
                    m_RecordList = new List<RecordData>(keys.Count);
                    long offset = 0;
                    int i = 0;
                    foreach (var b in Blocks[BlockType.RecordData].KeyBlocks)
                    {
                        var recordData = b.getDecompressedData();
                        while (i < keys.Count)
                        {
                            var key = keys[i];
                            long startIndex = key.Id;
                            if (startIndex - offset >= recordData.Length)
                                break;
                            long endIndex = i < keys.Count - 1 ? keys[i + 1].Id : recordData.Length + offset;
                            int localStart = (int)(startIndex - offset);
                            int localLength = (int)(endIndex - startIndex);
                            if (key.Text.StartsWith("\\"))
                            {
                                var data = new byte[localLength];
                                Buffer.BlockCopy(recordData, localStart, data, 0, localLength);
                                m_RecordList.Add(RecordData.of(key.Text, data));
                            }
                            else
                            {
                                m_RecordList.Add(RecordData.of(key.Text, encoding.GetString(recordData, localStart, localLength)));
                            }
                            i++;
                        }
                        offset += recordData.Length;
                    }
                }
                return m_RecordList;
            }
        }
        public long readNumber(Stream stream)
        {
            if (NumberWidth == 8)
                return readInt64(stream);
            else
                return readInt32(stream);
        }
        public long readNumber(ReadOnlySpan<byte> bytes)
        {
            if (NumberWidth == 8)
                return BinaryPrimitives.ReadInt64BigEndian(bytes);
            else
                return BinaryPrimitives.ReadInt32BigEndian(bytes);
        }
        public long readNumber(byte[] bytes) => readNumber(bytes.AsSpan());
    }
}
