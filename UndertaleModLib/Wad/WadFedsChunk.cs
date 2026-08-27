using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>FEDS</c> chunk (filters and effects). Field order from
    /// <c>FilterOrEffectWriter</c> (@701033/@701172 in Runner.exe.c): the chunk's single
    /// entry is the whole array: <c>{ u32 count, element[count] }</c>; element =
    /// <c>{ u32 regionEnd (abs offset, backpatched), str name, str defJson }</c>.
    /// </summary>
    public sealed class WadFedsChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadFedsElement> Elements => _elements;
        private readonly List<WadFedsElement> _elements = new();

        internal WadFedsChunk(WadChunkHeader header) : base(header) { }

        internal static WadFedsChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadFedsChunk(header);
            uint data = (uint)header.DataOffset;
            uint chunkEnd = (uint)header.DataOffset + header.Length;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    uint count = wad.ReadUInt32(entryOff);
                    uint p = entryOff + 4;
                    for (uint k = 0; k < count && p + 12 <= chunkEnd; k++)
                    {
                        var el = new WadFedsElement { RegionEnd = wad.ReadUInt32(p) };
                        el.NameRef = wad.ReadUInt32(p + 4);
                        el.Name = wad.ReadWadString(el.NameRef);
                        el.DefJsonRef = wad.ReadUInt32(p + 8);
                        el.DefJson = wad.ReadWadString(el.DefJsonRef);
                        chunk._elements.Add(el);
                        p += 12;
                    }
                }
                catch (Exception e)
                {
                    chunk._elements.Add(new WadFedsElement { Error = e });
                }
            }
            return chunk;
        }
    }

    public sealed class WadFedsElement
    {
        public uint RegionEnd { get; internal set; }
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint DefJsonRef { get; internal set; }
        public string DefJson { get; internal set; }
        public Exception Error { get; internal set; }
    }
}