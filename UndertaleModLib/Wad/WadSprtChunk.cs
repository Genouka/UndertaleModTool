using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>SPRT</c> chunk (sprites). Field order from
    /// <c>SpriteWriter::WriteSpriteToWAD</c> (@767921 in Runner.exe.c), verified
    /// byte-for-byte against the shipped wad:
    /// <code>
    /// { str name, i32 width, i32 height, i32 bBoxLeft, i32 bBoxRight, i32 bBoxBottom,
    ///   i32 bBoxTop, i32 transparent, i32 smooth, i32 preload, i32 bBoxMode, i32 colCheck,
    ///   i32 xOrig, i32 yOrig, u32 0xFFFFFFFF, u32 3, i32 spriteType, float playbackSpeed,
    ///   i32 playbackSpeedType, u32 nineSliceOffset (0=absent), u32 sequenceOffset (0=absent),
    ///   u32 frameCount, u32[frameCount] frameOffsets, frame[frameCount] (records at offsets),
    ///   ...collision-mask/sequence tail... }
    /// </code>
    /// A frame record begins with the absolute offset of its <c>TPAG</c> texture-region record,
    /// followed by per-frame fields; the remainder of the entry holds the collision mask and
    /// optional embedded sequence data (kept as raw bytes).
    /// </summary>
    public sealed class WadSprtChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadSprtEntry> Entries => _entries;
        private readonly List<WadSprtEntry> _entries = new();

        internal WadSprtChunk(WadChunkHeader header) : base(header) { }

        internal static WadSprtChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadSprtChunk(header);
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
                    chunk._entries.Add(new WadSprtEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadSprtEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadSprtEntry
            {
                NameRef = wad.ReadUInt32(p),
                Width = wad.ReadInt32(p + 4),
                Height = wad.ReadInt32(p + 8),
                BBoxLeft = wad.ReadInt32(p + 12),
                BBoxRight = wad.ReadInt32(p + 16),
                BBoxBottom = wad.ReadInt32(p + 20),
                BBoxTop = wad.ReadInt32(p + 24),
                Transparent = wad.ReadUInt32(p + 28) != 0,
                Smooth = wad.ReadUInt32(p + 32) != 0,
                Preload = wad.ReadUInt32(p + 36) != 0,
                BBoxMode = wad.ReadInt32(p + 40),
                ColCheck = wad.ReadUInt32(p + 44),
                XOrig = wad.ReadInt32(p + 48),
                YOrig = wad.ReadInt32(p + 52),
                Marker1 = wad.ReadUInt32(p + 56),
                Marker2 = wad.ReadUInt32(p + 60),
                SpriteType = wad.ReadInt32(p + 64),
                PlaybackSpeed = wad.ReadSingle(p + 68),
                PlaybackSpeedType = wad.ReadInt32(p + 72),
                NineSliceOffset = wad.ReadUInt32(p + 76),
                SequenceOffset = wad.ReadUInt32(p + 80),
            };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 84;
            e.FrameCount = wad.ReadUInt32(p);
            p += 4;
            var frames = new List<WadSprtFrame>();
            for (uint i = 0; i < e.FrameCount && p + 4 <= entryEnd; i++)
            {
                uint frOff = wad.ReadUInt32(p);
                p += 4;
                if (frOff == 0 || (long)frOff + 8 > entryEnd)
                    continue;
                var fr = new WadSprtFrame
                {
                    TpagRecordOffset = wad.ReadUInt32(frOff),
                    Unknown0 = wad.ReadUInt32(frOff + 4),
                    FrameNameRef = wad.ReadUInt32(frOff + 8),
                    BBox1 = wad.ReadUInt32(frOff + 12),
                    BBox2 = wad.ReadUInt32(frOff + 16),
                    BBox3 = wad.ReadUInt32(frOff + 20),
                    BBox4 = wad.ReadUInt32(frOff + 24),
                };
                fr.FrameName = fr.FrameNameRef == 0 || fr.FrameNameRef == 0xFFFFFFFF
                    ? null
                    : wad.ReadWadString(fr.FrameNameRef);
                frames.Add(fr);
            }
            e.Frames = frames;
            // Tail: collision mask / sequence data (opaque).
            uint tailStart = p;
            if (tailStart < entryEnd)
                e.TailBytes = wad.ReadBytes(tailStart, checked((int)(entryEnd - tailStart)));
            else
                e.TailBytes = Array.Empty<byte>();
            return e;
        }
    }

    public sealed class WadSprtEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int BBoxLeft { get; set; }
        public int BBoxRight { get; set; }
        public int BBoxBottom { get; set; }
        public int BBoxTop { get; set; }
        public bool Transparent { get; set; }
        public bool Smooth { get; set; }
        public bool Preload { get; set; }
        public int BBoxMode { get; set; }
        public uint ColCheck { get; set; }
        public int XOrig { get; set; }
        public int YOrig { get; set; }
        public uint Marker1 { get; set; }
        public uint Marker2 { get; set; }
        public int SpriteType { get; set; }
        public float PlaybackSpeed { get; set; }
        public int PlaybackSpeedType { get; set; }
        /// <summary>Absolute offset of the nine-slice data (0 = absent).</summary>
        public uint NineSliceOffset { get; internal set; }
        /// <summary>Absolute offset of the embedded sequence data (0 = absent).</summary>
        public uint SequenceOffset { get; internal set; }
        public uint FrameCount { get; internal set; }
        public IReadOnlyList<WadSprtFrame> Frames { get; internal set; }
        public byte[] TailBytes { get; internal set; }
        public Exception Error { get; internal set; }
    }

    /// <summary>One sprite frame; the first field links to the TPAG texture region.</summary>
    public sealed class WadSprtFrame
    {
        public uint TpagRecordOffset { get; internal set; }
        public uint Unknown0 { get; internal set; }
        public uint FrameNameRef { get; internal set; }
        public string FrameName { get; internal set; }
        public uint BBox1 { get; internal set; }
        public uint BBox2 { get; internal set; }
        public uint BBox3 { get; internal set; }
        public uint BBox4 { get; internal set; }
    }
}