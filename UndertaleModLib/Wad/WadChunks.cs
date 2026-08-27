using System;
using System.Collections.Generic;
using System.IO;

namespace UndertaleModLib.Wad
{
    /// <summary>Base class for parsed wad chunks.</summary>
    public abstract class WadChunk
    {
        protected WadChunk(WadChunkHeader header)
        {
            Name = header.Name;
            Offset = header.Offset;
            DataOffset = header.DataOffset;
            Length = header.Length;
        }

        /// <summary>Four-character chunk name.</summary>
        public string Name { get; }

        /// <summary>Absolute file offset of the chunk header.</summary>
        public long Offset { get; }

        /// <summary>Absolute file offset of the chunk payload.</summary>
        public long DataOffset { get; }

        /// <summary>Chunk payload length.</summary>
        public uint Length { get; }
    }

    /// <summary>
    /// Dispatches chunk parsing to the per-type parser. Mirrors the <c>WADLoader</c> handler
    /// registry of the runner: <c>ParsePRJT</c>, <c>ParseOPTN</c>, <c>ParseRREF</c>,
    /// <c>ParseTAGS</c>, <c>WadResourceLoad&lt;T&gt;</c>, <c>LoadTexturesFromChunk</c>,
    /// <c>LoadTextureGroupsFromChunk</c>, <c>LoadEmbeddedImagesFromChunk</c>, <c>ParseAUDO</c>
    /// and <c>ParseEXTN</c>.
    /// </summary>
    internal static class WadChunkParser
    {
        private static readonly Dictionary<string, Func<UndertaleWadFile, WadChunkHeader, WadChunk>> Parsers = new()
        {
            { "STRG", (wad, h) => WadStrgChunk.Parse(wad, h) },
            { "PRJT", (wad, h) => WadPrjtChunk.Parse(wad, h) },
            { "RREF", (wad, h) => WadRrefChunk.Parse(wad, h) },
            { "TAGS", (wad, h) => WadTagsChunk.Parse(wad, h) },
            { "EMBI", (wad, h) => WadEmbeddedImagesChunk.Parse(wad, h) },
            { "AUDO", (wad, h) => WadAudoChunk.Parse(wad, h) },
            { "TXTR", (wad, h) => WadTxTrChunk.Parse(wad, h) },
            { "TPAG", (wad, h) => WadTpagChunk.Parse(wad, h) },
            { "TGIN", (wad, h) => WadTginChunk.Parse(wad, h) },
            { "ROOM", (wad, h) => WadRoomChunk.Parse(wad, h) },
            { "SEQN", (wad, h) => WadSeqnChunk.Parse(wad, h) },
            { "ACRV", (wad, h) => WadAcrvChunk.Parse(wad, h) },
            { "TMLN", (wad, h) => WadTmlnChunk.Parse(wad, h) },
            { "PATH", (wad, h) => WadPathChunk.Parse(wad, h) },
            { "PSYS", (wad, h) => WadPsysChunk.Parse(wad, h) },
            { "SHDR", (wad, h) => WadShdrChunk.Parse(wad, h) },
            { "OBJT", (wad, h) => WadObjtChunk.Parse(wad, h) },
            { "OPTN", (wad, h) => WadOptnChunk.Parse(wad, h) },
            { "UILR", (wad, h) => WadUilrChunk.Parse(wad, h) },
            { "EXTN", (wad, h) => WadExtnChunk.Parse(wad, h) },
            { "FEDS", (wad, h) => WadFedsChunk.Parse(wad, h) },
            { "SPRT", (wad, h) => WadSprtChunk.Parse(wad, h) },
            { "BGND", (wad, h) => WadBgndChunk.Parse(wad, h) },
            { "SOND", (wad, h) => WadSondChunk.Parse(wad, h) },
            { "AGRP", (wad, h) => WadAgrpChunk.Parse(wad, h) },
            { "FONT", (wad, h) => WadFontChunk.Parse(wad, h) },
            // Resource chunks are all parsed through the same generic entry envelope; their
            // per-entry layouts differ, see WadResourceChunk.
            { "SCPT", (wad, h) => WadResourceChunk.Parse(wad, h, WadScriptEntry.Parse) },
        };

        public static WadChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            if (Parsers.TryGetValue(header.Name, out Func<UndertaleWadFile, WadChunkHeader, WadChunk> parser))
                return parser(wad, header);
            // Unknown/not-yet-supported chunks are kept as raw data, matching the runner
            // behavior of leaving unused chunks untouched.
            return new WadRawChunk(header, wad.ReadBytes((uint)header.DataOffset, (int)Math.Min(header.Length, int.MaxValue)));
        }
    }

    /// <summary>A chunk whose payload is kept as raw bytes (unparsed or unparseable).</summary>
    public sealed class WadRawChunk : WadChunk
    {
        public WadRawChunk(WadChunkHeader header, byte[] rawData) : base(header)
        {
            RawData = rawData ?? throw new ArgumentNullException(nameof(rawData));
        }

        public byte[] RawData { get; }
    }

    /// <summary>
    /// Parsed <c>STRG</c> chunk: the string pool.
    ///
    /// <para>
    /// Layout: <c>{ u32 countHint, u32 flags, u8 pad, records... }</c> where each record is
    /// <c>{ u32 length, bytes[length], u8 0 }</c>. Strings are referenced everywhere in the
    /// wad by the absolute file offset of a record's length field. The count hint equals the
    /// number of records plus one (an implicit empty string).</para>
    /// </summary>
    public sealed class WadStrgChunk : WadChunk
    {
        public uint CountHint { get; private set; }
        public uint Flags { get; private set; }

        /// <summary>Enumerated records (offset keys match absolute file offsets).</summary>
        public IReadOnlyList<uint> RecordOffsets => _recordOffsets;

        private readonly List<uint> _recordOffsets = new();

        private WadStrgChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadStrgChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadStrgChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.CountHint = wad.ReadUInt32(data);
            chunk.Flags = wad.ReadUInt32(data + 4);

            // Strings begin after { u32, u32, u8 } (the trailing byte is an empty-record terminator).
            long pos = data + 9;
            long end = data + header.Length;
            while (pos + 4 <= end)
            {
                long recordOffset = pos;
                uint len = wad.ReadUInt32((uint)pos);
                if (len > header.Length || pos + 4 + len + 1 > end)
                    break; // not a valid record anymore (trailing garbage)
                chunk._recordOffsets.Add((uint)recordOffset);
                pos += 4 + len + 1;
            }
            return chunk;
        }

        /// <summary>Resolves the string at an absolute record offset through this pool (null if absent).</summary>
        public string GetString(UndertaleWadFile wad, uint recordOffset)
        {
            return wad.ReadWadString(recordOffset);
        }
    }

    /// <summary>
    /// Parsed <c>PRJT</c> chunk: project information. Matches <c>ParsePRJT</c> in the runner:
    /// <code>
    /// { u32 count, u32[count] entryOffsets }
    /// entry: { u32 projectNameOffset, u32 startRoomRef, u32 width, u32 height, u32 buildType,
    ///          u32 crc, u8 md5[16], u32 fileListOffset, u32 folderListOffset,
    ///          u32 maxRoomInstanceId, u32 roomOrderCount, u32[roomOrderCount] roomOrderRefs }
    /// </code>
    /// where <c>fileListOffset</c>/<c>folderListOffset</c> point at
    /// <c>{ u32 count, u32[count] stringRecordOffsets }</c> lists of string records
    /// (<c>Application::ffe</c>/<c>ffd</c> in the runner).
    /// </summary>
    public sealed class WadPrjtChunk : WadChunk
    {
        /// <summary>Parsed project entry.</summary>
        public WadPrjtEntry Entry { get; private set; }

        private WadPrjtChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadPrjtChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadPrjtChunk(header);
            uint data = (uint)header.DataOffset;
            uint count = wad.ReadUInt32(data);
            if (count == 0)
                return chunk;
            if (count > 1)
                throw new InvalidDataException($"PRJT chunk has {count} entries, expected 1");
            uint entryOff = wad.ReadUInt32(data + 4);
            uint e = entryOff;
            var entry = new WadPrjtEntry
            {
                ProjectNameOffset = wad.ReadUInt32(e),
                StartRoomRef = wad.ReadUInt32(e + 4),
                Width = wad.ReadUInt32(e + 8),
                Height = wad.ReadUInt32(e + 12),
                BuildType = wad.ReadUInt32(e + 16),
                Crc = wad.ReadUInt32(e + 20),
                Md5 = wad.ReadBytes(e + 24, 16),
                FileListOffset = wad.ReadUInt32(e + 40),
                FolderListOffset = wad.ReadUInt32(e + 44),
                MaxRoomInstanceId = wad.ReadUInt32(e + 48),
                RoomOrderCount = wad.ReadUInt32(e + 52),
            };
            entry.RoomOrderRefs = new uint[entry.RoomOrderCount];
            for (int i = 0; i < entry.RoomOrderCount && count > 0; i++)
            {
                entry.RoomOrderRefs[i] = wad.ReadUInt32(e + 56 + (uint)(4 * i));
            }
            entry.ProjectName = wad.ReadWadString(entry.ProjectNameOffset);
            entry.FileList = ReadWadStringList(wad, entry.FileListOffset);
            entry.FolderList = ReadWadStringList(wad, entry.FolderListOffset);
            chunk.Entry = entry;
            return chunk;
        }

        private static List<string> ReadWadStringList(UndertaleWadFile wad, uint listOffset)
        {
            var result = new List<string>();
            if (listOffset == 0)
                return result;
            uint count = wad.ReadUInt32(listOffset);
            for (uint i = 0; i < count; i++)
            {
                uint recOffset = wad.ReadUInt32(listOffset + 4 + 4 * i);
                // Doubly-indirected: the list holds offsets to u32 values that are themselves
                // offsets to string records (matches the runner's ffe/ffd reads).
                if (recOffset != 0)
                    recOffset = wad.ReadUInt32(recOffset);
                result.Add(wad.ReadWadString(recOffset));
            }
            return result;
        }
    }

    /// <summary>One <c>PRJT</c> project entry.</summary>
    public sealed class WadPrjtEntry
    {
        public uint ProjectNameOffset { get; internal set; }
        public string ProjectName { get; internal set; }
        public uint StartRoomRef { get; internal set; }
        public uint Width { get; internal set; }
        public uint Height { get; internal set; }
        public uint BuildType { get; internal set; }
        public uint Crc { get; internal set; }
        public byte[] Md5 { get; internal set; }
        public uint FileListOffset { get; internal set; }
        public uint FolderListOffset { get; internal set; }
        public IReadOnlyList<string> FileList { get; internal set; }
        public IReadOnlyList<string> FolderList { get; internal set; }
        public uint MaxRoomInstanceId { get; internal set; }
        public uint RoomOrderCount { get; internal set; }
        public uint[] RoomOrderRefs { get; internal set; }
    }

    /// <summary>
    /// Parsed <c>RREF</c> chunk: the resource reference registry. Matches <c>ParseRREF</c>:
    /// every named resource in the wad is listed here once with its type and numeric key.
    /// <code>
    /// { u32 count, u32[count] entryOffsets }
    /// entry (10 bytes): { u32 nameRecordOffset, u16 resourceType, u32 key }
    /// </code>
    /// </summary>
    public sealed class WadRrefChunk : WadChunk
    {
        public IReadOnlyList<WadRrefEntry> Entries => _entries;

        private readonly List<WadRrefEntry> _entries = new();

        private WadRrefChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadRrefChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadRrefChunk(header);
            uint data = (uint)header.DataOffset;
            uint count = wad.ReadUInt32(data);
            for (uint i = 0; i < count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                uint nameOff = wad.ReadUInt32(entryOff);
                ushort type = BitConverter.ToUInt16(wad.ReadBytes(entryOff + 4, 2), 0);
                uint key = wad.ReadUInt32(entryOff + 6);
                chunk._entries.Add(new WadRrefEntry(wad.ReadWadString(nameOff), type, key, nameOff));
            }
            return chunk;
        }
    }

    /// <summary>One <c>RREF</c> resource reference. <c>ResourceType</c> is the byte index into
    /// the runner's <c>TypeTag</c> table (1 = sprite, 3 = object, 4 = room, 6 = script/function,
    /// 7 = shader, 9 = background/tileset, 11 = sound, 12 = audio group, 14 = sequence,
    /// 15 = animation curve, 2 = texture group, ...).</summary>
    public sealed class WadRrefEntry
    {
        public WadRrefEntry(string name, ushort resourceType, uint key, uint nameRecordOffset)
        {
            Name = name;
            ResourceType = (ushort)(resourceType >> 8);
            Key = key;
            NameRecordOffset = nameRecordOffset;
        }

        public string Name { get; }
        public ushort ResourceType { get; }
        public uint Key { get; }
        public uint NameRecordOffset { get; }
    }

    /// <summary>
    /// Parsed <c>TAGS</c> chunk: the tag manager (<c>CTagManager::LoadFromChunk</c>). Maps each
    /// resource (identified by packed type/key) to tag data. Empirically:
    /// <code>
    /// { u32 1, u32 0, u32 count, u32[count] recordOffsets, records... }
    /// record (10 bytes): { u32 (key &lt;&lt; 16) | (resourceType &lt;&lt; 8), u32 0, u16 0 }
    /// </code>
    /// The <c>count</c> equals the number of <c>RREF</c> entries, and record
    /// <c>i</c> corresponds to <c>RREF</c> entry <c>i</c>.
    /// </summary>
    public sealed class WadTagsChunk : WadChunk
    {
        /// <summary>Tag records, one per <c>RREF</c> entry (in RREF order).</summary>
        public IReadOnlyList<WadTagRecord> Tags => _tags;

        /// <summary>The remaining raw bytes after the record list (runner keeps tag details there).</summary>
        public byte[] TailData { get; private set; }

        private readonly List<WadTagRecord> _tags = new();

        private WadTagsChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadTagsChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadTagsChunk(header);
            uint data = (uint)header.DataOffset;
            uint head1 = wad.ReadUInt32(data);
            uint head2 = wad.ReadUInt32(data + 4);
            uint count = wad.ReadUInt32(data + 8);
            long pos = data + 12;
            for (uint i = 0; i < count && pos + 4 <= data + header.Length; i++)
            {
                uint recOff = wad.ReadUInt32((uint)pos);
                pos += 4;
                if (recOff == 0 || recOff + 10 > data + header.Length)
                {
                    chunk._tags.Add(new WadTagRecord(0, null));
                    continue;
                }
                uint packed = wad.ReadUInt32(recOff);
                byte[] tail = wad.ReadBytes(recOff + 4, 6);
                chunk._tags.Add(new WadTagRecord(packed, tail));
            }
            if (pos < data + header.Length)
                chunk.TailData = wad.ReadBytes((uint)pos, (int)(data + header.Length - pos));
            return chunk;
        }
    }

    /// <summary>One tag record: a packed resource reference plus raw detail bytes.</summary>
    public sealed class WadTagRecord
    {
        public WadTagRecord(uint packedRef, byte[] detail)
        {
            PackedRef = packedRef;
            ResourceType = (ushort)((packedRef >> 8) & 0xFF);
            Key = packedRef >> 16;
            Detail = detail;
        }

        /// <summary><c>(key &lt;&lt; 16) | (resourceType &lt;&lt; 8)</c></summary>
        public uint PackedRef { get; }

        public ushort ResourceType { get; }

        public uint Key { get; }

        /// <summary>6 raw tag-detail bytes (all zero in the reference wad).</summary>
        public byte[] Detail { get; }
    }

    /// <summary>
    /// Parsed <c>EMBI</c> chunk: embedded images (<c>LoadEmbeddedImagesFromChunk</c> — particle
    /// shapes, fallback texture etc.). Entries are <c>{ u32 nameRecordOffset, u32 dataOffset }</c>
    /// pairs whose <c>dataOffset</c> points at a QOI-like image blob.
    /// </summary>
    public sealed class WadEmbeddedImagesChunk : WadChunk
    {
        public uint Count { get; private set; }

        /// <summary>Embedded image entries, in chunk order.</summary>
        public IReadOnlyList<WadEmbeddedImage> Images => _images;

        private readonly List<WadEmbeddedImage> _images = new();

        private WadEmbeddedImagesChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadEmbeddedImagesChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadEmbeddedImagesChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                uint nameOff = wad.ReadUInt32(entryOff);
                uint dataOff = wad.ReadUInt32(entryOff + 4);
                chunk._images.Add(new WadEmbeddedImage(wad.ReadWadString(nameOff), entryOff, dataOff));
            }
            return chunk;
        }
    }

    /// <summary>One embedded image (EMBI): a name and the offset of its image blob.</summary>
    public sealed class WadEmbeddedImage
    {
        public WadEmbeddedImage(string name, uint entryOffset, uint dataOffset)
        {
            Name = name;
            EntryOffset = entryOffset;
            DataOffset = dataOffset;
        }

        public string Name { get; }
        public uint EntryOffset { get; }
        public uint DataOffset { get; }
    }

    /// <summary>
    /// Parsed <c>TXTR</c> chunk: texture page bitmaps (<c>LoadTexturesFromChunk</c>).
    /// <code>
    /// { u32 count, u32[count] entryOffsets, entry[count] }
    /// entry (32 bytes): { u32 texId, u32 flags0, u32 hasAlpha, u32 format,
    ///                     u32 blobSize, u32 width, u32 height, u32 blobOffset }
    /// </code>
    /// <c>format</c> 0 = plain PNG blob; 1/2 = "qoz2" blob
    /// (<c>{ u32 magic 'qoz2', u32 (height &lt;&lt; 16) | width, u32 uncompressedQoiSize, bzip2(QOI) bytes }</c>).
    /// </summary>
    public sealed class WadTxTrChunk : WadChunk
    {
        public uint Count { get; private set; }

        public IReadOnlyList<WadTextureEntry> Textures => _textures;

        private readonly List<WadTextureEntry> _textures = new();

        private WadTxTrChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadTxTrChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadTxTrChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                var entry = new WadTextureEntry
                {
                    TexId = wad.ReadUInt32(entryOff),
                    Flags0 = wad.ReadUInt32(entryOff + 4),
                    HasAlpha = wad.ReadUInt32(entryOff + 8),
                    Format = wad.ReadUInt32(entryOff + 12),
                    BlobSize = wad.ReadUInt32(entryOff + 16),
                    Width = wad.ReadUInt32(entryOff + 20),
                    Height = wad.ReadUInt32(entryOff + 24),
                    BlobOffset = wad.ReadUInt32(entryOff + 28),
                };
                if (entry.Format == 0)
                {
                    entry.RawPng = wad.ReadBytes(entry.BlobOffset, (int)Math.Min(entry.BlobSize, header.Length));
                }
                else
                {
                    uint qozWidth = wad.ReadUInt32(entry.BlobOffset + 4) & 0xFFFF;
                    uint qozHeight = (wad.ReadUInt32(entry.BlobOffset + 4) >> 16) & 0xFFFF;
                    int qoiSize = (int)wad.ReadUInt32(entry.BlobOffset + 8);
                    // bzip2(QOI) data begins at +12
                    uint dataLen = Math.Max(0, entry.BlobSize - 12);
                    entry.QoiCompressed = wad.ReadBytes(entry.BlobOffset + 12, (int)Math.Min(dataLen, header.Length));
                    entry.QoiWidth = qozWidth;
                    entry.QoiHeight = qozHeight;
                    entry.QoiUncompressedSize = qoiSize;
                }
                chunk._textures.Add(entry);
            }
            return chunk;
        }
    }

    /// <summary>One texture (TXTR) entry.</summary>
    public sealed class WadTextureEntry
    {
        public uint TexId { get; internal set; }
        public uint Flags0 { get; internal set; }
        public uint HasAlpha { get; internal set; }

        /// <summary>0 = PNG, 1/2 = QOI (bzip2-compressed "qoz2").</summary>
        public uint Format { get; internal set; }
        public uint BlobSize { get; internal set; }
        public uint Width { get; internal set; }
        public uint Height { get; internal set; }
        public uint BlobOffset { get; internal set; }

        /// <summary>Raw PNG bytes when <see cref="Format"/> == 0.</summary>
        public byte[] RawPng { get; internal set; }

        /// <summary>bzip2(QOI) payload when <see cref="Format"/> != 0 (width/height/size below).</summary>
        public byte[] QoiCompressed { get; internal set; }
        public uint QoiWidth { get; internal set; }
        public uint QoiHeight { get; internal set; }
        public int QoiUncompressedSize { get; internal set; }
    }

    /// <summary>
    /// Parsed <c>TPAG</c> chunk: texture page records (handler <c>sub_1403401F0</c>, which is a
    /// stub in the runner and never parses this chunk at runtime). Records are fixed 24 bytes:
    /// <code>
    /// { u16 pageX, u16 pageY, u16 rectW, u16 rectH, u16 srcOffX, u16 srcOffY,
    ///   u16 rectW2, u16 rectH2, u16 srcW, u16 srcH, u32 pageTexId }
    /// </code>
    /// where <c>pageTexId</c> references a <c>TXTR</c> texture id.
    /// </summary>
    public sealed class WadTpagChunk : WadChunk
    {
        public uint Count { get; private set; }

        public IReadOnlyList<WadTexturePage> Pages => _pages;

        private readonly List<WadTexturePage> _pages = new();

        private WadTpagChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadTpagChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadTpagChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint recOff = wad.ReadUInt32(data + 4 + 4 * i);
                byte[] rec = wad.ReadBytes(recOff, 24);
                chunk._pages.Add(new WadTexturePage
                {
                    PageX = BitConverter.ToUInt16(rec, 0),
                    PageY = BitConverter.ToUInt16(rec, 2),
                    RectW = BitConverter.ToUInt16(rec, 4),
                    RectH = BitConverter.ToUInt16(rec, 6),
                    SrcOffX = BitConverter.ToUInt16(rec, 8),
                    SrcOffY = BitConverter.ToUInt16(rec, 10),
                    RectW2 = BitConverter.ToUInt16(rec, 12),
                    RectH2 = BitConverter.ToUInt16(rec, 14),
                    SrcW = BitConverter.ToUInt16(rec, 16),
                    SrcH = BitConverter.ToUInt16(rec, 18),
                    PageTexId = BitConverter.ToUInt32(rec, 20),
                });
            }
            return chunk;
        }
    }

    /// <summary>One texture page (TPAG) record.</summary>
    public sealed class WadTexturePage
    {
        public ushort PageX { get; internal set; }
        public ushort PageY { get; internal set; }
        public ushort RectW { get; internal set; }
        public ushort RectH { get; internal set; }
        public ushort SrcOffX { get; internal set; }
        public ushort SrcOffY { get; internal set; }
        public ushort RectW2 { get; internal set; }
        public ushort RectH2 { get; internal set; }
        public ushort SrcW { get; internal set; }
        public ushort SrcH { get; internal set; }
        public uint PageTexId { get; internal set; }
    }

    /// <summary>
    /// Parsed <c>TGIN</c> chunk: texture groups (<c>LoadTextureGroupsFromChunk</c>).
    /// <code>
    /// { u32 version, u32 count, u32[count] groupOffsets, groups... }
    /// group: { u32 nameOffset, u32 name2Offset, u32 compressionOffset, u32 flags,
    ///          u32 pageBlockOffset, u32 categoryBlockOffset }
    /// pageBlock: { u32 count, u32[count] pageTexIds }
    /// categoryBlock: { u32 count, u32[count] categoryOffsets }
    /// category: { u32 nameOffset, u32 tagCount, u32[tagCount] tagOffsets }
    /// </summary>
    public sealed class WadTginChunk : WadChunk
    {
        public uint Version { get; private set; }
        public uint Count { get; private set; }

        public IReadOnlyList<WadTextureGroup> Groups => _groups;

        private readonly List<WadTextureGroup> _groups = new();

        private WadTginChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadTginChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadTginChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Version = wad.ReadUInt32(data);
            chunk.Count = wad.ReadUInt32(data + 4);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint groupOff = wad.ReadUInt32(data + 8 + 4 * i);
                var group = new WadTextureGroup
                {
                    Name = wad.ReadWadString(wad.ReadUInt32(groupOff)),
                    Name2 = wad.ReadWadString(wad.ReadUInt32(groupOff + 4)),
                    Compression = wad.ReadWadString(wad.ReadUInt32(groupOff + 8)),
                    Flags = wad.ReadUInt32(groupOff + 12),
                    PageBlockOffset = wad.ReadUInt32(groupOff + 16),
                    CategoryBlockOffset = wad.ReadUInt32(groupOff + 20),
                };

                // page block: { count, pageTexIds }
                uint pageBlockOff = group.PageBlockOffset;
                if (pageBlockOff != 0)
                {
                    uint pageCount = wad.ReadUInt32(pageBlockOff);
                    var pages = new List<uint>();
                    for (uint k = 0; k < pageCount; k++)
                        pages.Add(wad.ReadUInt32(pageBlockOff + 4 + 4 * k));
                    group.Pages = pages;
                }

                // category block: { count, categoryOffsets }
                uint catBlockOff = group.CategoryBlockOffset;
                if (catBlockOff != 0)
                {
                    uint catCount = wad.ReadUInt32(catBlockOff);
                    var categories = new List<WadTextureCategory>();
                    for (uint k = 0; k < catCount; k++)
                    {
                        uint catOff = wad.ReadUInt32(catBlockOff + 4 + 4 * k);
                        var cat = new WadTextureCategory
                        {
                            Name = wad.ReadWadString(wad.ReadUInt32(catOff)),
                        };
                        uint tagCount = wad.ReadUInt32(catOff + 4);
                        var tags = new List<string>();
                        for (uint t = 0; t < tagCount; t++)
                        {
                            uint tagOff = wad.ReadUInt32(catOff + 8 + 4 * t);
                            tags.Add(wad.ReadWadString(tagOff));
                        }
                        cat.Tags = tags;
                        categories.Add(cat);
                    }
                    group.Categories = categories;
                }
                chunk._groups.Add(group);
            }
            return chunk;
        }
    }

    /// <summary>One texture group (TGIN).</summary>
    public sealed class WadTextureGroup
    {
        public string Name { get; internal set; }
        public string Name2 { get; internal set; }
        public string Compression { get; internal set; }
        public uint Flags { get; internal set; }
        public uint PageBlockOffset { get; internal set; }
        public uint CategoryBlockOffset { get; internal set; }
        public IReadOnlyList<uint> Pages { get; internal set; }
        public IReadOnlyList<WadTextureCategory> Categories { get; internal set; }
    }

    /// <summary>One texture group category (a named list of texture tags).</summary>
    public sealed class WadTextureCategory
    {
        public string Name { get; internal set; }
        public IReadOnlyList<string> Tags { get; internal set; }
    }

    /// <summary>
    /// Parsed <c>AUDO</c> chunk: audio data (<c>ParseAUDO</c>). Layout:
    /// <code>
    /// { u32 count, u32[count] blobOffsets, blobs... }
    /// blob: { u32 dataSize, byte audio[dataSize] }  (audio is RIFF/WAVE etc., zero-padded)
    /// </code>
    /// </summary>
    public sealed class WadAudoChunk : WadChunk
    {
        public uint Count { get; private set; }

        /// <summary>Audio blobs, in chunk order.</summary>
        public IReadOnlyList<WadAudioBlob> Audio => _audio;

        private readonly List<WadAudioBlob> _audio = new();

        private WadAudoChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadAudoChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadAudoChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint blobOff = wad.ReadUInt32(data + 4 + 4 * i);
                uint size = wad.ReadUInt32(blobOff);
                if (blobOff + 4 + size > (uint)header.DataOffset + header.Length)
                    size = (uint)Math.Min(size, (uint)header.DataOffset + header.Length - (blobOff + 4));
                chunk._audio.Add(new WadAudioBlob(wad.ReadBytes(blobOff + 4, (int)size), blobOff, size));
            }
            return chunk;
        }
    }

    /// <summary>One audio blob (AUDO): raw audio file data (RIFF/WAVE etc.).</summary>
    public sealed class WadAudioBlob
    {
        public WadAudioBlob(byte[] data, uint offset, uint paddedLength)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Offset = offset;
            PaddedLength = paddedLength;
        }

        public byte[] Data { get; }
        public uint Offset { get; }
        public uint PaddedLength { get; }
    }
}