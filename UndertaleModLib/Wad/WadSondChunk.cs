using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>SOND</c> chunk (sounds). Field order from
    /// <c>AudioWriter::writeSoundToWAD</c> (@692312 in Runner.exe.c), verified against the
    /// shipped wad (fixed 40-byte entries):
    /// <c>{ str name, u32 formatCode (0/1 legacy, 100 streamed-new, 101 compressed-new,
    /// 103 uncompressed-new), u32 zero, str fileName (.ogg/.wav), u32 zero,
    /// float volume, u32 zero, u32 zero, u32 zero, float unknown }</c>. The audio payload
    /// blobs themselves sit in the AUDO chunk.
    /// </summary>
    public sealed class WadSondChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadSondEntry> Entries => _entries;
        private readonly List<WadSondEntry> _entries = new();

        internal WadSondChunk(WadChunkHeader header) : base(header) { }

        internal static WadSondChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadSondChunk(header);
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
                    chunk._entries.Add(new WadSondEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadSondEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadSondEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            if (p + 40 <= entryEnd)
            {
                e.FormatCode = wad.ReadUInt32(p + 4);
                e.Zero0 = wad.ReadUInt32(p + 8);
                e.FileNameRef = wad.ReadUInt32(p + 12);
                e.FileName = wad.ReadWadString(e.FileNameRef);
                e.Zero1 = wad.ReadUInt32(p + 16);
                e.Volume = wad.ReadSingle(p + 20);
                e.Zero2 = wad.ReadUInt32(p + 24);
                e.Zero3 = wad.ReadUInt32(p + 28);
                e.Zero4 = wad.ReadUInt32(p + 32);
                e.UnknownFloat = wad.ReadSingle(p + 36);
            }
            return e;
        }
    }

    public sealed class WadSondEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint FormatCode { get; internal set; }
        public uint Zero0 { get; internal set; }
        public uint FileNameRef { get; internal set; }
        public string FileName { get; internal set; }
        public uint Zero1 { get; internal set; }
        public float Volume { get; internal set; }
        public uint Zero2 { get; internal set; }
        public uint Zero3 { get; internal set; }
        public uint Zero4 { get; internal set; }
        public float UnknownFloat { get; internal set; }
        public Exception Error { get; internal set; }
    }
}