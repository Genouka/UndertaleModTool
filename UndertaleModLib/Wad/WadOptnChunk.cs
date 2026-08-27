using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>OPTN</c> chunk (game options). Field order from
    /// <c>ResourceWriter::writeOptionsToWAD</c> (@872765 in Runner.exe.c):
    /// entry = <c>{ u32 version, u32 flagsA, u32 flagsB, u32 colorDepth, u32 resolution,
    /// u32 frequency, components section }</c> — flagsA holds the 6-bit error mask when the
    /// wad debug byte is not set, flagsB otherwise. Components: <c>GM.Core</c> =
    /// <c>{ str config }</c>, <c>GM.Systems.collision</c> = <c>{ u32 flags }</c>.
    /// </summary>
    public sealed class WadOptnChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadOptnEntry> Entries => _entries;
        private readonly List<WadOptnEntry> _entries = new();

        internal WadOptnChunk(WadChunkHeader header) : base(header) { }

        internal static WadOptnChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadOptnChunk(header);
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
                    chunk._entries.Add(new WadOptnEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadOptnEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadOptnEntry
            {
                Version = wad.ReadUInt32(p),
                FlagsA = wad.ReadUInt32(p + 4),
                FlagsB = wad.ReadUInt32(p + 8),
                ColorDepth = wad.ReadUInt32(p + 12),
                Resolution = wad.ReadUInt32(p + 16),
                Frequency = wad.ReadUInt32(p + 20),
                ComponentSectionOffset = p + 24,
            };
            // Components envelope: { u32 fieldA, u32 fieldB, u32[fieldB] offsets }.
            uint pos = p + 24;
            if (pos + 8 <= entryEnd)
            {
                uint fieldA = wad.ReadUInt32(pos);
                uint fieldB = wad.ReadUInt32(pos + 4);
                uint offsPos = pos + 8;
                uint maxI = Math.Min(fieldB, (entryEnd - offsPos) / 4);
                for (uint i = 0; i < maxI; i++)
                {
                    uint compOff = wad.ReadUInt32(offsPos + 4 * i);
                    if (compOff == 0 || (long)compOff + 4 > entryEnd)
                        continue;
                    var comp = new WadOptnComponent { NameRef = wad.ReadUInt32(compOff) };
                    comp.Name = wad.ReadWadString(comp.NameRef);
                    comp.EntryOffset = compOff;
                    uint d = compOff + 4;
                    switch (comp.Name)
                    {
                        case "GM.Core":
                            comp.ConfigRef = wad.ReadUInt32(d);
                            comp.Config = wad.ReadWadString(comp.ConfigRef);
                            break;
                        case "GM.Systems.collision":
                            comp.CollisionFlags = wad.ReadUInt32(d);
                            break;
                    }
                    e.Components.Add(comp);
                }
            }
            return e;
        }
    }

    public sealed class WadOptnEntry
    {
        public uint Version { get; internal set; }
        public uint FlagsA { get; internal set; }
        public uint FlagsB { get; internal set; }
        public uint ColorDepth { get; internal set; }
        public uint Resolution { get; internal set; }
        public uint Frequency { get; internal set; }
        public uint ComponentSectionOffset { get; internal set; }
        public List<WadOptnComponent> Components { get; } = new();
        public Exception Error { get; internal set; }
    }

    public sealed class WadOptnComponent
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint EntryOffset { get; internal set; }
        // GM.Core
        public uint ConfigRef { get; internal set; }
        public string Config { get; internal set; }
        // GM.Systems.collision
        public uint CollisionFlags { get; internal set; }
    }
}