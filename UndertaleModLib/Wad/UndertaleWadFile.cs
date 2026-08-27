using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parser for the GameMaker runtime asset package (<c>.wad</c>) format used by
    /// GameMaker / GMRT exports since 2023.x.
    ///
    /// <para>
    /// A <c>.wad</c> is a single file container that replaces the classic <c>data.win</c>.
    /// It uses the same <c>FORM</c> container as a classic data file, but with a 16-byte
    /// header (<c>FORM</c> + 4-byte length + 8 pad bytes) instead of the 8-byte header of
    /// <c>data.win</c>, and with no <c>GEN8</c> chunk (see <see cref="UndertaleData.IsWad"/>).
    /// Instead it ships the new chunks <c>PRJT</c> (project information) and <c>RREF</c>
    /// (resource references), plus resource chunks whose entries reference each other and
    /// the string pool through <b>absolute file offsets</b>.
    /// </para>
    ///
    /// <para>
    /// This implementation was derived from the decompiled GameMaker Runner
    /// (<c>Runner.exe.c</c>/<c>Runner.exe.h</c>): <c>WADLoader</c>, <c>ParsePRJT</c>,
    /// <c>ParseRREF</c>, <c>ParseOPTN</c>, <c>ParseTAGS</c>, <c>WadResourceLoad&lt;T&gt;</c>,
    /// <c>T::LoadFromChunk</c> for every resource type, <c>LoadTexturesFromChunk</c>,
    /// <c>LoadTextureGroupsFromChunk</c>, <c>LoadEmbeddedImagesFromChunk</c> and
    /// <c>ParseAUDO</c>.
    /// </para>
    ///
    /// <para>
    /// File layout (all multi-byte integers little-endian, all offsets absolute from file start):
    /// <code>
    /// 0x00  "FORM" (4 bytes)
    /// 0x04  u32: length of the FORM payload (file size - 8)
    /// 0x08  8 bytes: padding (zero)
    /// 0x10  chunks: each { char[4] name, u32 length, payload[length] }
    /// </code>
    /// There is no alignment padding between chunks.
    /// </para>
    /// </summary>
    public sealed class UndertaleWadFile : IDisposable
    {
        /// <summary>When true, the SEQN parser prints a per-track/per-keyframe walk to the console.</summary>
        internal static bool DebugWalk = false;

        /// <summary>Magic of the container: "FORM".</summary>
        public const string FormMagic = "FORM";

        /// <summary>The FORM payload length (file size minus the 8-byte FORM header).</summary>
        public uint FormLength { get; private set; }

        /// <summary>Chunk table, in file order.</summary>
        public IReadOnlyList<WadChunkHeader> ChunkHeaders => _chunks;

        /// <summary>Parsed chunks, indexed by chunk name.</summary>
        public IReadOnlyDictionary<string, WadChunk> Chunks => _chunksByName;

        /// <summary>The string pool (STRG chunk), parsed.</summary>
        public WadStrgChunk Strings { get; private set; }

        /// <summary>Project information (PRJT chunk), parsed.</summary>
        public WadPrjtChunk Project { get; private set; }

        /// <summary>Resource reference registry (RREF chunk), parsed.</summary>
        public WadRrefChunk ResourceReferences { get; private set; }

        private readonly List<WadChunkHeader> _chunks = new();
        private readonly Dictionary<string, WadChunk> _chunksByName = new();
        private readonly Dictionary<uint, string> _stringPool = new();

        private byte[] _fileData;
        private MemoryStream _stream;

        private UndertaleWadFile()
        {
        }

        /// <summary>The source file path, when loaded from disk (blank otherwise).</summary>
        public string FilePath { get; private set; } = "";

        /// <summary>Loads and parses a .wad file from disk.</summary>
        public static UndertaleWadFile Load(string path)
        {
            if (path is null)
                throw new ArgumentNullException(nameof(path));
            UndertaleWadFile wad = Load(File.ReadAllBytes(path));
            wad.FilePath = path;
            return wad;
        }

        /// <summary>Loads and parses a .wad file from a stream (read to the end).</summary>
        public static UndertaleWadFile Load(Stream stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Load(ms.ToArray());
        }

        /// <summary>Loads and parses a .wad file from a raw byte buffer.</summary>
        public static UndertaleWadFile Load(byte[] data)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));
            var wad = new UndertaleWadFile
            {
                _fileData = data,
                _stream = new MemoryStream(data, false),
            };
            wad.ParseContainer();
            wad.ParseChunks();
            return wad;
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
            _fileData = null;
        }

        /// <summary>Reads a <c>{ u32 length, bytes[length] }</c> string record at an absolute file offset.
        /// Returns null for offset 0 / 0xFFFFFFFF / out-of-bounds.</summary>
        public string ReadWadString(uint offset)
        {
            if (offset == 0 || offset == 0xFFFFFFFF || (long)offset + 4 > _fileData.Length)
                return null;
            if (_stringPool.TryGetValue(offset, out string cached))
                return cached;
            uint length = BitConverter.ToUInt32(_fileData, checked((int)offset));
            if (length >= _fileData.Length || (long)offset + 4 + length > _fileData.Length)
                return null;
            string str = Encoding.UTF8.GetString(_fileData, checked((int)offset + 4), checked((int)length));
            _stringPool[offset] = str;
            return str;
        }

        /// <summary>Reads a little-endian u32 at an absolute file offset.</summary>
        internal uint ReadUInt32(uint offset)
        {
            if ((long)offset + 4 > _fileData.Length)
                throw new EndOfStreamException($"ReadUInt32 at 0x{offset:X8} out of bounds");
            return BitConverter.ToUInt32(_fileData, checked((int)offset));
        }

        /// <summary>Returns up to <paramref name="maxBytes"/> bytes of the raw payload of the
        /// chunk named <paramref name="name"/> (null when no such chunk).</summary>
        public byte[] GetChunkBytes(string name, int maxBytes = 1 << 20)
        {
            if (name is null || maxBytes <= 0 || _fileData is null)
                return null;
            foreach (WadChunkHeader header in _chunks)
            {
                if (header.Name != name)
                    continue;
                int take = (int)Math.Min(header.Length, maxBytes);
                if (take == 0)
                    return Array.Empty<byte>();
                if (header.DataOffset + take > _fileData.Length)
                    take = (int)(_fileData.Length - header.DataOffset);
                byte[] result = new byte[take];
                Array.Copy(_fileData, header.DataOffset, result, 0, take);
                return result;
            }
            return null;
        }

        /// <summary>Total size of the loaded buffer (for bounds checks).</summary>
        internal uint FileSize => (uint)_fileData.Length;

        /// <summary>Reads a little-endian signed i32 at an absolute file offset.</summary>
        internal int ReadInt32(uint offset)
        {
            return unchecked((int)ReadUInt32(offset));
        }

        /// <summary>Reads a little-endian single-precision float at an absolute file offset.</summary>
        internal float ReadSingle(uint offset)
        {
            if (offset + 4 > _fileData.Length)
                throw new EndOfStreamException($"ReadSingle at 0x{offset:X8} out of bounds");
            return BitConverter.ToSingle(_fileData, checked((int)offset));
        }

        /// <summary>Reads <paramref name="count"/> bytes at an absolute file offset.</summary>
        internal byte[] ReadBytes(uint offset, int count)
        {
            if (count < 0 || (long)offset + count > _fileData.Length)
                throw new EndOfStreamException($"ReadBytes at 0x{offset:X8} count {count} out of bounds");
            byte[] result = new byte[count];
            Array.Copy(_fileData, offset, result, 0, count);
            return result;
        }

        private void ParseContainer()
        {
            int len = _fileData.Length;
            if (len < 16)
                throw new InvalidDataException("File too short to be a wad container");

            string magic = Encoding.ASCII.GetString(_fileData, 0, 4);
            if (magic != FormMagic)
                throw new InvalidDataException($"Root chunk is \"{magic}\", not FORM");

            FormLength = BitConverter.ToUInt32(_fileData, 4);
            if (FormLength + 8 != (uint)len)
                throw new InvalidDataException($"FORM length mismatch: header says {FormLength}, file is {len - 8} payload bytes");

            long pos = 16;
            while (pos + 8 <= len)
            {
                string name = Encoding.ASCII.GetString(_fileData, checked((int)pos), 4);
                if (name.Length != 4)
                    throw new InvalidDataException("Truncated chunk name");
                // Chunk names are uppercase alphanumeric (like FORM chunks)
                for (int i = 0; i < 4; i++)
                {
                    char c = name[i];
                    if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                        throw new InvalidDataException($"Invalid data at offset 0x{pos:X8} (expected chunk header, got \"{name}\")");
                }
                uint length = BitConverter.ToUInt32(_fileData, checked((int)pos + 4));
                if ((long)pos + 8 + length > len)
                    throw new InvalidDataException($"Chunk \"{name}\" at 0x{pos:X8} extends past end of file");
                _chunks.Add(new WadChunkHeader(name, pos, length));
                pos += 8 + length;
            }

            if (pos != len)
                throw new InvalidDataException($"Chunk scan ended at 0x{pos:X8}, expected 0x{len:X8}");
        }

        private void ParseChunks()
        {
            foreach (WadChunkHeader header in _chunks)
            {
                WadChunk chunk = WadChunkParser.Parse(this, header);
                if (chunk != null)
                {
                    _chunksByName[chunk.Name] = chunk;
                    switch (chunk)
                    {
                        case WadStrgChunk strg:
                            Strings = strg;
                            break;
                        case WadPrjtChunk prjt:
                            Project = prjt;
                            break;
                        case WadRrefChunk rref:
                            ResourceReferences = rref;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Location of one chunk inside the container.</summary>
    public readonly struct WadChunkHeader
    {
        public WadChunkHeader(string name, long offset, uint length)
        {
            Name = name;
            Offset = offset;
            Length = length;
        }

        /// <summary>Four-character chunk name (e.g. <c>PRJT</c>, <c>RREF</c>, <c>STRG</c>).</summary>
        public string Name { get; }

        /// <summary>Absolute file offset of the chunk header.</summary>
        public long Offset { get; }

        /// <summary>Chunk payload length (excluding the 8-byte header).</summary>
        public uint Length { get; }

        /// <summary>Absolute file offset of the chunk payload.</summary>
        public long DataOffset => Offset + 8;
    }
}