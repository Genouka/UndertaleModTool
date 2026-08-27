using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>BGND</c> chunk (tilesets/backgrounds-as-tilesets in GMRT). Field order from
    /// <c>BackgroundWriter::WriteBackgroundToWAD</c> (@698059 in Runner.exe.c), verified
    /// against the shipped wad (each entry is 84 fixed bytes + frame-data tail):
    /// <code>
    /// { str name, i32 transparent, i32 smooth, i32 preload, u32 imageRef (TPAG record offset
    ///   or 0xFFFFFFFF when no image), u32 zero,
    ///   i32 tileWidth, i32 tileHeight, i32 tileHSep, i32 tileVSep, i32 tileBorderX,
    ///   i32 tileBorderY, i32 columns, i32 frames, i32 tileCount, i32 spriteIndex,
    ///   u32 frameLengthLo, u32 frameLengthHi, u32 unknown (constant), u32 zero,
    ///   u32 frameDataCount, ... }
    /// </code>
    /// </summary>
    public sealed class WadBgndChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadBgndEntry> Entries => _entries;
        private readonly List<WadBgndEntry> _entries = new();

        internal WadBgndChunk(WadChunkHeader header) : base(header) { }

        internal static WadBgndChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadBgndChunk(header);
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
                    chunk._entries.Add(new WadBgndEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadBgndEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadBgndEntry
            {
                NameRef = wad.ReadUInt32(p),
                Transparent = wad.ReadUInt32(p + 4) != 0,
                Smooth = wad.ReadUInt32(p + 8) != 0,
                Preload = wad.ReadUInt32(p + 12) != 0,
                ImageRef = wad.ReadUInt32(p + 16),
                Unknown0 = wad.ReadUInt32(p + 20),
                TileWidth = wad.ReadInt32(p + 24),
                TileHeight = wad.ReadInt32(p + 28),
                TileHSep = wad.ReadInt32(p + 32),
                TileVSep = wad.ReadInt32(p + 36),
                TileBorderX = wad.ReadInt32(p + 40),
                TileBorderY = wad.ReadInt32(p + 44),
                Columns = wad.ReadInt32(p + 48),
                Frames = wad.ReadInt32(p + 52),
                TileCount = wad.ReadInt32(p + 56),
                SpriteIndex = wad.ReadInt32(p + 60),
                FrameLengthLo = wad.ReadUInt32(p + 64),
                FrameLengthHi = wad.ReadUInt32(p + 68),
                UnknownConst = wad.ReadUInt32(p + 72),
                Unknown1 = wad.ReadUInt32(p + 76),
                FrameDataCount = wad.ReadUInt32(p + 80),
            };
            e.Name = wad.ReadWadString(e.NameRef);
            // Frame data tail.
            uint pos = p + 84;
            if (e.FrameDataCount > 0 && e.FrameDataCount < 1_000_000 && pos + 4 * e.FrameDataCount <= entryEnd)
            {
                var data = new List<uint>();
                for (uint i = 0; i < e.FrameDataCount; i++)
                {
                    data.Add(wad.ReadUInt32(pos));
                    pos += 4;
                }
                e.FrameData = data;
            }
            if (pos < entryEnd)
                e.TailBytes = wad.ReadBytes(pos, checked((int)(entryEnd - pos)));
            else
                e.TailBytes = Array.Empty<byte>();
            return e;
        }
    }

    public sealed class WadBgndEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public bool Transparent { get; internal set; }
        public bool Smooth { get; internal set; }
        public bool Preload { get; internal set; }
        /// <summary>TPAG record offset of the image (0xFFFFFFFF when no image is set).</summary>
        public uint ImageRef { get; internal set; }
        public uint Unknown0 { get; internal set; }
        public int TileWidth { get; internal set; }
        public int TileHeight { get; internal set; }
        public int TileHSep { get; internal set; }
        public int TileVSep { get; internal set; }
        public int TileBorderX { get; internal set; }
        public int TileBorderY { get; internal set; }
        public int Columns { get; internal set; }
        public int Frames { get; internal set; }
        public int TileCount { get; internal set; }
        public int SpriteIndex { get; internal set; }
        public uint FrameLengthLo { get; internal set; }
        public uint FrameLengthHi { get; internal set; }
        public uint UnknownConst { get; internal set; }
        public uint Unknown1 { get; internal set; }
        public uint FrameDataCount { get; internal set; }
        public IReadOnlyList<uint> FrameData { get; internal set; }
        public byte[] TailBytes { get; internal set; }
        public Exception Error { get; internal set; }
    }
}