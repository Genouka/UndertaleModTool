using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>SHDR</c> chunk (shaders), layout verified byte-for-byte against the shipped
    /// wad and the runner reader <c>CShaderGM::LoadFromChunk</c> (@762437 in Runner.exe.c).
    /// Entry =
    /// <c>{ u32 nameRef, u32 vertexBlobOffset, u32 metaAOffset, u32 scalarA,
    /// u32 fragmentBlobOffset, u32 metaBOffset, u32 scalarB,
    /// vertexBlob (inline, { u32 len, bytes[len] }), metaA blob, fragmentBlob, metaB blob }</c>.
    /// The blob offsets are absolute and point back into the entry body, which physically
    /// follows the 28-byte header in order: vertex, metaA, fragment, metaB.
    /// Vertex/fragment payloads are SPIR-V binaries (GLSL compiled) stored as
    /// <c>{ u32 length, bytes[length] }</c> (length excludes the 4-byte header).
    /// </summary>
    public sealed class WadShdrChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadShdrEntry> Entries => _entries;
        private readonly List<WadShdrEntry> _entries = new();

        internal WadShdrChunk(WadChunkHeader header) : base(header) { }

        internal static WadShdrChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadShdrChunk(header);
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
                    chunk._entries.Add(ParseEntry(wad, entryOff, entryEnd));
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadShdrEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadShdrEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadShdrEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            e.VertexBlobOffset = wad.ReadUInt32(p + 4);
            e.MetaABlobOffset = wad.ReadUInt32(p + 8);
            e.ScalarA = wad.ReadUInt32(p + 12);
            e.FragmentBlobOffset = wad.ReadUInt32(p + 16);
            e.MetaBBlobOffset = wad.ReadUInt32(p + 20);
            e.ScalarB = wad.ReadUInt32(p + 24);

            e.VertexLen = wad.ReadUInt32(e.VertexBlobOffset);
            e.VertexSource = wad.ReadBytes(e.VertexBlobOffset + 4, checked((int)e.VertexLen));

            e.FragmentLen = wad.ReadUInt32(e.FragmentBlobOffset);
            e.FragmentSource = wad.ReadBytes(e.FragmentBlobOffset + 4, checked((int)e.FragmentLen));

            // metaA physically sits between the vertex blob and the fragment blob.
            uint metaAStart = e.MetaABlobOffset;
            uint metaAEnd = e.FragmentBlobOffset;
            if (metaAStart < metaAEnd && metaAEnd <= entryEnd)
                e.MetaA = wad.ReadBytes(metaAStart, checked((int)(metaAEnd - metaAStart)));

            uint metaBStart = e.MetaBBlobOffset;
            if (metaBStart < entryEnd && metaBStart + 4 <= entryEnd)
                e.MetaB = wad.ReadBytes(metaBStart, checked((int)(entryEnd - metaBStart)));
            return e;
        }
    }

    public sealed class WadShdrEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; set; }
        public uint VertexBlobOffset { get; internal set; }
        public uint MetaABlobOffset { get; internal set; }
        public uint ScalarA { get; internal set; }
        public uint FragmentBlobOffset { get; internal set; }
        public uint MetaBBlobOffset { get; internal set; }
        public uint ScalarB { get; internal set; }
        public uint VertexLen { get; internal set; }
        public byte[] VertexSource { get; internal set; }
        public uint FragmentLen { get; internal set; }
        public byte[] FragmentSource { get; internal set; }
        public byte[] MetaA { get; internal set; }
        public byte[] MetaB { get; internal set; }
        public Exception Error { get; internal set; }
    }
}