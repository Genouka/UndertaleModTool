using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>TMLN</c> chunk (timelines). Field order from
    /// <c>SequenceWriter::writeTimelineToWAD</c> (@761105 in Runner.exe.c):
    /// entry = <c>{ str name, u32 momentCount, moment[count] }</c>;
    /// moment = <c>{ i32 frame, i32 scriptIndex, i32 eventType, i32 eventNum, str name }</c>.
    /// </summary>
    public sealed class WadTmlnChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadTmlnEntry> Entries => _entries;
        private readonly List<WadTmlnEntry> _entries = new();

        internal WadTmlnChunk(WadChunkHeader header) : base(header) { }

        internal static WadTmlnChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadTmlnChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    chunk._entries.Add(ParseEntry(wad, entryOff));
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadTmlnEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadTmlnEntry ParseEntry(UndertaleWadFile wad, uint p)
        {
            var e = new WadTmlnEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 4;
            uint momentCount = wad.ReadUInt32(p); p += 4;
            var moments = new List<WadTmlnMoment>();
            for (uint i = 0; i < momentCount; i++)
            {
                var m = new WadTmlnMoment
                {
                    Frame = wad.ReadInt32(p),
                    ScriptIndex = wad.ReadInt32(p + 4),
                    EventType = wad.ReadInt32(p + 8),
                    EventNum = wad.ReadInt32(p + 12),
                    NameRef = wad.ReadUInt32(p + 16),
                };
                p += 20;
                m.Name = wad.ReadWadString(m.NameRef);
                moments.Add(m);
            }
            e.Moments = moments;
            return e;
        }
    }

    public sealed class WadTmlnEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public IReadOnlyList<WadTmlnMoment> Moments { get; internal set; }
        public Exception Error { get; internal set; }
    }

    public sealed class WadTmlnMoment
    {
        public int Frame { get; internal set; }
        public int ScriptIndex { get; internal set; }
        public int EventType { get; internal set; }
        public int EventNum { get; internal set; }
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
    }
}