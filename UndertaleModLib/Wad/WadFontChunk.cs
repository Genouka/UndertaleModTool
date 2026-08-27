using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>FONT</c> chunk. Field order from <c>FontWriter::WriteFontToWAD</c>
    /// (@702116 in Runner.exe.c): <c>{ str name, str fontName, float size, u32 bold,
    /// u32 italic, u32 packedChars (antiAlias&lt;&lt;24 | first | charSet&lt;&lt;16), u32 last,
    /// ...glyph texture data... }</c>. The tail holds the font texture/page data and is kept
    /// as raw bytes.
    /// </summary>
    public sealed class WadFontChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadFontEntry> Entries => _entries;
        private readonly List<WadFontEntry> _entries = new();

        internal WadFontChunk(WadChunkHeader header) : base(header) { }

        internal static WadFontChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadFontChunk(header);
            uint data = (uint)header.DataOffset;
            uint chunkEnd = (uint)header.DataOffset + header.Length;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                uint entryEnd = (i + 1 < chunk.Count)
                    ? wad.ReadUInt32(data + 4 + 4 * (i + 1))
                    : chunkEnd;
                try
                {
                    chunk._entries.Add(ParseEntry(wad, entryOff, Math.Min(entryEnd, chunkEnd)));
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadFontEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadFontEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadFontEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            if (p + 28 <= entryEnd)
            {
                e.FontNameRef = wad.ReadUInt32(p + 4);
                e.FontName = wad.ReadWadString(e.FontNameRef);
                e.Size = wad.ReadSingle(p + 8);
                e.Bold = wad.ReadUInt32(p + 12) != 0;
                e.Italic = wad.ReadUInt32(p + 16) != 0;
                e.PackedChars = wad.ReadUInt32(p + 20);
                e.Last = wad.ReadUInt32(p + 24);
            }
            uint tailStart = p + 28;
            if (tailStart < entryEnd)
                e.TailBytes = wad.ReadBytes(tailStart, checked((int)(entryEnd - tailStart)));
            else
                e.TailBytes = Array.Empty<byte>();
            return e;
        }
    }

    public sealed class WadFontEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint FontNameRef { get; internal set; }
        public string FontName { get; internal set; }
        public float Size { get; internal set; }
        public bool Bold { get; internal set; }
        public bool Italic { get; internal set; }
        /// <summary>antiAlias&lt;&lt;24 | first | charSet&lt;&lt;16</summary>
        public uint PackedChars { get; internal set; }
        public uint Last { get; internal set; }
        public byte[] TailBytes { get; internal set; }
        public Exception Error { get; internal set; }
    }
}