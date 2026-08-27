using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>PATH</c> chunk. Field order from <c>PathWriter::writePathToWAD</c>
    /// (@754813 in Runner.exe.c): entry = <c>{ str name, i32 kind, i32 closed, i32 precision,
    /// u32 pointCount, point[count] }</c>; point = 12 bytes of 3 floats (x, y, speed).
    /// </summary>
    public sealed class WadPathChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadPathEntry> Entries => _entries;
        private readonly List<WadPathEntry> _entries = new();

        internal WadPathChunk(WadChunkHeader header) : base(header) { }

        internal static WadPathChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadPathChunk(header);
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
                    chunk._entries.Add(new WadPathEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadPathEntry ParseEntry(UndertaleWadFile wad, uint p)
        {
            var e = new WadPathEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 4;
            e.Kind = wad.ReadInt32(p); p += 4;
            e.Closed = wad.ReadInt32(p) != 0; p += 4;
            e.Precision = wad.ReadInt32(p); p += 4;
            uint pointCount = wad.ReadUInt32(p); p += 4;
            var points = new List<WadPathPoint>();
            for (uint i = 0; i < pointCount; i++)
            {
                points.Add(new WadPathPoint
                {
                    X = wad.ReadSingle(p),
                    Y = wad.ReadSingle(p + 4),
                    Speed = wad.ReadSingle(p + 8),
                });
                p += 12;
            }
            e.Points = points;
            return e;
        }
    }

    public sealed class WadPathEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; set; }
        public int Kind { get; set; }
        public bool Closed { get; set; }
        public int Precision { get; set; }
        public IReadOnlyList<WadPathPoint> Points { get; internal set; }
        public Exception Error { get; internal set; }
    }

    /// <summary>A 12-byte path point.</summary>
    public sealed class WadPathPoint
    {
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Speed { get; internal set; }
    }
}