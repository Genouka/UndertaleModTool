using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>Parses a resource chunk with the standard entry envelope
    /// <c>{ u32 count, u32[count] entryOffsets, entries@entryOffsets }</c>.</summary>
    public sealed class WadResourceChunk : WadChunk
    {
        /// <summary>Owning wad file (set by the parser before entries are parsed).</summary>
        internal UndertaleWadFile File { get; private set; }

        public uint Count { get; private set; }

        /// <summary>All entries of the chunk, in chunk order.</summary>
        public IReadOnlyList<WadResourceEntry> Entries => _entries;

        private readonly List<WadResourceEntry> _entries = new();

        internal WadResourceChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadResourceChunk Parse(UndertaleWadFile wad, WadChunkHeader header,
                                               Func<WadResourceChunk, uint, WadResourceEntry> entryParser)
        {
            var chunk = new WadResourceChunk(header) { File = wad };
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                WadResourceEntry entry;
                try
                {
                    entry = entryParser(chunk, entryOff);
                }
                catch (Exception e)
                {
                    entry = new WadUnparsedEntry(entryOff, e);
                }
                entry.Chunk = chunk;
                entry.Offset = entryOff;
                chunk._entries.Add(entry);
            }
            return chunk;
        }
    }

    /// <summary>Base for a parsed resource entry (name + generic record offset).</summary>
    public abstract class WadResourceEntry
    {
        /// <summary>The owning chunk.</summary>
        public WadResourceChunk Chunk { get; internal set; }

        /// <summary>Absolute file offset of the entry.</summary>
        public long Offset { get; internal set; }

        /// <summary>Resolves a string record at an absolute offset through the wad string pool.</summary>
        protected string ResolveString(uint recordOffset) => Chunk?.File?.ReadWadString(recordOffset);

        /// <summary>Resource name (resolved through the string pool), if the entry has one.</summary>
        public virtual string Name => null;
    }

    /// <summary>An entry whose payload could not be parsed (stores the exception).</summary>
    public sealed class WadUnparsedEntry : WadResourceEntry
    {
        public WadUnparsedEntry(long offset, Exception error)
        {
            Offset = offset;
            Error = error;
        }

        public Exception Error { get; }
    }

    /// <summary>
    /// One <c>SCPT</c> entry (a script or automatically-named function resource).
    /// Matches <c>CScriptGM::LoadFromChunk</c>: the entry is
    /// <c>{ u32 nameRecordOffset, u32 codeRef }</c> where <c>codeRef</c> points at a
    /// <c>{ u32 length, bytecode... }</c> blob consumed by
    /// <c>CodeManager::LoadBytecodeFunctionsFromIR</c> when non-zero. In GMRT packages the
    /// bytecode usually lives in the separate <c>MBytecode</c> file, hence 0 here.
    /// </summary>
    public sealed class WadScriptEntry : WadResourceEntry
    {
        public uint NameRecordOffset { get; private set; }
        public uint CodeRef { get; private set; }

        public override string Name => ResolveString(NameRecordOffset);

        internal static WadResourceEntry Parse(WadResourceChunk chunk, uint entryOff)
        {
            uint nameOff = chunk.File.ReadUInt32(entryOff);
            uint codeRef = chunk.File.ReadUInt32(entryOff + 4);
            return new WadScriptEntry { NameRecordOffset = nameOff, CodeRef = codeRef };
        }
    }
}