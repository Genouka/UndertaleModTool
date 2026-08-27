using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>EXTN</c> chunk (extensions). Field order from
    /// <c>ExtensionWriter::writeExtensionToWAD</c> (@699645 in Runner.exe.c): entry =
    /// <c>{ str name, str version, str className, u32 fileCount, file[fileCount],
    /// u32 optionCount, option[optionCount] }</c>; file = <c>{ str name, str initFunction,
    /// str finalFunction, u32 kind, u32 funcCount, function[funcCount] }</c>; function =
    /// <c>{ str name, str externalName, u32 kind, u32 returnType, u32 argCount,
    /// u32[argCount] args }</c>; option = <c>{ str name, str value, u32 optionType }</c>.
    /// </summary>
    public sealed class WadExtnChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadExtnEntry> Entries => _entries;
        private readonly List<WadExtnEntry> _entries = new();

        internal WadExtnChunk(WadChunkHeader header) : base(header) { }

        internal static WadExtnChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadExtnChunk(header);
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
                    chunk._entries.Add(new WadExtnEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadExtnEntry ParseEntry(UndertaleWadFile wad, uint p)
        {
            var e = new WadExtnEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 4;
            e.VersionRef = wad.ReadUInt32(p); p += 4;
            e.Version = wad.ReadWadString(e.VersionRef);
            e.ClassNameRef = wad.ReadUInt32(p); p += 4;
            e.ClassName = wad.ReadWadString(e.ClassNameRef);
            uint fileCount = wad.ReadUInt32(p); p += 4;
            var files = new List<WadExtnFile>();
            for (uint i = 0; i < fileCount; i++)
            {
                var f = new WadExtnFile { NameRef = wad.ReadUInt32(p) };
                f.Name = wad.ReadWadString(f.NameRef);
                p += 4;
                f.InitFunctionRef = wad.ReadUInt32(p); p += 4;
                f.InitFunction = wad.ReadWadString(f.InitFunctionRef);
                f.FinalFunctionRef = wad.ReadUInt32(p); p += 4;
                f.FinalFunction = wad.ReadWadString(f.FinalFunctionRef);
                f.Kind = wad.ReadUInt32(p); p += 4;
                uint funcCount = wad.ReadUInt32(p); p += 4;
                var funcs = new List<WadExtnFunction>();
                for (uint k = 0; k < funcCount; k++)
                {
                    var fn = new WadExtnFunction { NameRef = wad.ReadUInt32(p) };
                    fn.Name = wad.ReadWadString(fn.NameRef);
                    p += 4;
                    fn.ExternalNameRef = wad.ReadUInt32(p); p += 4;
                    fn.ExternalName = wad.ReadWadString(fn.ExternalNameRef);
                    fn.Kind = wad.ReadUInt32(p); p += 4;
                    fn.ReturnType = wad.ReadUInt32(p); p += 4;
                    uint argCount = wad.ReadUInt32(p); p += 4;
                    var args = new List<uint>();
                    for (uint a = 0; a < argCount; a++)
                    {
                        args.Add(wad.ReadUInt32(p)); p += 4;
                    }
                    fn.Args = args;
                    funcs.Add(fn);
                }
                f.Functions = funcs;
                files.Add(f);
            }
            e.Files = files;
            uint optionCount = wad.ReadUInt32(p); p += 4;
            var options = new List<WadExtnOption>();
            for (uint i = 0; i < optionCount; i++)
            {
                var o = new WadExtnOption { NameRef = wad.ReadUInt32(p) };
                o.Name = wad.ReadWadString(o.NameRef);
                p += 4;
                o.ValueRef = wad.ReadUInt32(p); p += 4;
                o.Value = wad.ReadWadString(o.ValueRef);
                o.OptionType = wad.ReadUInt32(p); p += 4;
                options.Add(o);
            }
            e.Options = options;
            return e;
        }
    }

    public sealed class WadExtnEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint VersionRef { get; internal set; }
        public string Version { get; internal set; }
        public uint ClassNameRef { get; internal set; }
        public string ClassName { get; internal set; }
        public IReadOnlyList<WadExtnFile> Files { get; internal set; }
        public IReadOnlyList<WadExtnOption> Options { get; internal set; }
        public Exception Error { get; internal set; }
    }

    public sealed class WadExtnFile
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint InitFunctionRef { get; internal set; }
        public string InitFunction { get; internal set; }
        public uint FinalFunctionRef { get; internal set; }
        public string FinalFunction { get; internal set; }
        public uint Kind { get; internal set; }
        public IReadOnlyList<WadExtnFunction> Functions { get; internal set; }
    }

    public sealed class WadExtnFunction
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint ExternalNameRef { get; internal set; }
        public string ExternalName { get; internal set; }
        public uint Kind { get; internal set; }
        public uint ReturnType { get; internal set; }
        public IReadOnlyList<uint> Args { get; internal set; }
    }

    public sealed class WadExtnOption
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint ValueRef { get; internal set; }
        public string Value { get; internal set; }
        public uint OptionType { get; internal set; }
    }
}