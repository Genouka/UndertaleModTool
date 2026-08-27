using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>AGRP</c> chunk (audio groups). From
    /// <c>AudioWriter::writeAudioGroupToWAD</c> (@694248 in Runner.exe.c) and the shipped
    /// wad: each entry is <c>{ str name, str exportDir }</c> (e.g.
    /// <c>audiogroup_default</c> / <c>audiogroup_default.dat</c>).
    /// </summary>
    public sealed class WadAgrpChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadAgrpEntry> Entries => _entries;
        private readonly List<WadAgrpEntry> _entries = new();

        internal WadAgrpChunk(WadChunkHeader header) : base(header) { }

        internal static WadAgrpChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadAgrpChunk(header);
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
                    if (entryEnd < entryOff + 8)
                    {
                        chunk._entries.Add(new WadAgrpEntry { NameRef = wad.ReadUInt32(entryOff) });
                    }
                    else
                    {
                        chunk._entries.Add(new WadAgrpEntry
                        {
                            NameRef = wad.ReadUInt32(entryOff),
                            ExportDirRef = wad.ReadUInt32(entryOff + 4),
                        });
                    }
                    chunk._entries[^1].Name = wad.ReadWadString(chunk._entries[^1].NameRef);
                    chunk._entries[^1].ExportDir = wad.ReadWadString(chunk._entries[^1].ExportDirRef);
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadAgrpEntry { Error = e });
                }
            }
            return chunk;
        }
    }

    public sealed class WadAgrpEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint ExportDirRef { get; internal set; }
        public string ExportDir { get; internal set; }
        public Exception Error { get; internal set; }
    }
}