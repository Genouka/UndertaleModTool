using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Byte-level edit session for a wad file. Editors mutate the parsed entry models
    /// directly; <see cref="Save()"/> compares each model against the ORIGINAL file bytes
    /// at the recorded field locations and emits minimal patches, so unchanged data is
    /// never rewritten and revert-to-original cancels the patch.
    ///
    /// String renames are handled "internally": the new <c>{u32 len, utf8}</c> record is
    /// appended to the END of the STRG payload and the entry's name reference (a u32 at a
    /// known field offset) is repointed at the new absolute offset. The FORM container
    /// length and the STRG chunk length are updated accordingly, so the chunk table stays
    /// valid; resources after STRG (and their stored absolute offsets) are shifted by the
    /// appended delta when STRG is not the trailing chunk.
    /// </summary>
    public sealed class WadEditSession
    {
        private sealed class OffsetPatch
        {
            public long FileOffset;
            public byte[] Bytes;
        }

        private readonly UndertaleWadFile _wad;
        private readonly byte[] _original;
        private readonly List<OffsetPatch> _pending = new();
        private readonly List<byte[]> _appends = new();
        private long _appendBase;
        private int _appendedBytes;

        public UndertaleWadFile Wad => _wad;
        public bool HasChanges => _pending.Count > 0 || _appends.Count > 0;

        public WadEditSession(UndertaleWadFile wad)
        {
            _wad = wad ?? throw new ArgumentNullException(nameof(wad));
            _original = File.ReadAllBytes(wad.FilePath);
        }

        // ------------------------------------------------------------------ patches

        /// <summary>Absolute file offset of entry <paramref name="entryIndex"/> of a resource chunk.</summary>
        private long EntryLocation(string chunkName, int entryIndex)
        {
            if (entryIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(entryIndex));
            for (int i = 0; i < _wad.ChunkHeaders.Count; i++)
            {
                WadChunkHeader hdr = _wad.ChunkHeaders[i];
                if (hdr.Name == chunkName)
                {
                    long tableBase = hdr.DataOffset + 4 + 4L * entryIndex;
                    if (tableBase + 4 > _original.Length)
                        throw new InvalidOperationException($"Entry {entryIndex} of {chunkName} is out of range of the offsets table.");
                    return BitConverter.ToUInt32(_original, (int)tableBase);
                }
            }
            throw new KeyNotFoundException($"Chunk '{chunkName}' not found.");
        }

        /// <summary>Records a raw 4-byte write; repeated writes to the same offset keep the last one (revert works).</summary>
        public void PatchU32(string chunkName, int entryIndex, int fieldOffset, uint value)
        {
            long fileOffset = EntryLocation(chunkName, entryIndex) + fieldOffset;
            _pending.Add(new OffsetPatch { FileOffset = fileOffset, Bytes = BitConverter.GetBytes(value) });
        }

        public void PatchI32(string chunkName, int entryIndex, int fieldOffset, int value)
            => PatchU32(chunkName, entryIndex, fieldOffset, unchecked((uint)value));

        public void PatchF32(string chunkName, int entryIndex, int fieldOffset, float value)
        {
            long fileOffset = EntryLocation(chunkName, entryIndex) + fieldOffset;
            _pending.Add(new OffsetPatch { FileOffset = fileOffset, Bytes = BitConverter.GetBytes(value) });
        }

        public void PatchBool(string chunkName, int entryIndex, int fieldOffset, bool value)
            => PatchU32(chunkName, entryIndex, fieldOffset, value ? 1U : 0U);

        /// <summary>
        /// Renames a string-referenced field: the new string record is appended to the end
        /// of the STRG payload and the u32 reference at <paramref name="refFieldOffset"/>
        /// is repointed at the record. Only meaningful inside <see cref="Save()"/>.
        /// </summary>
        public void PatchStringRef(string chunkName, int entryIndex, int refFieldOffset, string value)
        {
            if (value is null)
                return;
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            if (utf8.Length > int.MaxValue - 4)
                throw new ArgumentOutOfRangeException(nameof(value), "String too long for the wad record format.");
            byte[] record = new byte[4 + utf8.Length];
            BitConverter.GetBytes((uint)utf8.Length).CopyTo(record, 0);
            utf8.CopyTo(record, 4);

            long appendOffset = _appendBase + _appendedBytes;
            _appends.Add(record);
            _appendedBytes += record.Length;
            PatchU32(chunkName, entryIndex, refFieldOffset, (uint)appendOffset);
        }

        // -------------------------------------------------- per-chunk model capture
        // Compares the parsed entry models with the original bytes and records patches
        // for every difference. Only fixed-size fields are editable this way; variable
        // regions (frame data, event stores, blobs) stay untouched.

        private void CaptureName(WadChunk chunk, string chunkName, int index, string currentName)
        {
            if (currentName is null)
                return;
            string onDisk;
            try
            {
                long loc = EntryLocation(chunkName, index);
                uint refOff = BitConverter.ToUInt32(_original, (int)loc);   // field 0 = string ref
                onDisk = ReadStringAt(refOff);
            }
            catch (Exception)
            {
                return;
            }
            if (!string.Equals(onDisk, currentName, StringComparison.Ordinal))
                PatchStringRef(chunkName, index, 0, currentName);
        }

        private string ReadStringAt(long fileOffset)
        {
            if (fileOffset + 4 > _original.Length)
                throw new InvalidOperationException("String reference outside the file.");
            uint len = BitConverter.ToUInt32(_original, (int)fileOffset);
            long start = fileOffset + 4;
            if (start + len > _original.Length)
                throw new InvalidOperationException("String record truncated.");
            return Encoding.UTF8.GetString(_original, (int)start, (int)len);
        }

        private void PatchIfDiff(long fileOffset, byte[] bytes)
        {
            bool same = true;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (_original[fileOffset + i] != bytes[i])
                {
                    same = false;
                    break;
                }
            }
            if (!same)
                _pending.Add(new OffsetPatch { FileOffset = fileOffset, Bytes = bytes });
        }

        /// <summary>Collects every model-vs-file difference into the patch list.</summary>
        public void CaptureChanges()
        {
            _pending.Clear();
            _appends.Clear();
            _appendedBytes = 0;
            if (!TryGetChunk("STRG", out _))
                return;   // no string pool -> no renames possible

            CaptureSond(); CaptureSprt(); CaptureBgnd(); CaptureObjt(); CaptureFont();
            CaptureShdr(); CapturePath(); CaptureRoom(); CaptureSeqn();
        }

        private bool TryGetChunk(string name, out WadChunk chunk)
            => _wad.Chunks.TryGetValue(name, out chunk);

        private bool TryGetHeader(string name, out WadChunkHeader header)
        {
            foreach (WadChunkHeader h in _wad.ChunkHeaders)
            {
                if (h.Name == name)
                {
                    header = h;
                    return true;
                }
            }
            header = default;
            return false;
        }

        private void CaptureSond()
        {
            if (!TryGetChunk("SOND", out WadChunk chunk) || chunk is not WadSondChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadSondEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("SOND", i);
                CaptureName(chunk, "SOND", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.FormatCode));
                PatchIfDiff(loc + 20, BitConverter.GetBytes(e.Volume));
            }
        }

        private void CaptureSprt()
        {
            if (!TryGetChunk("SPRT", out WadChunk chunk) || chunk is not WadSprtChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadSprtEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("SPRT", i);
                CaptureName(chunk, "SPRT", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.Width));
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.Height));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.BBoxLeft));
                PatchIfDiff(loc + 16, BitConverter.GetBytes(e.BBoxRight));
                PatchIfDiff(loc + 20, BitConverter.GetBytes(e.BBoxBottom));
                PatchIfDiff(loc + 24, BitConverter.GetBytes(e.BBoxTop));
                PatchIfDiff(loc + 28, BitConverter.GetBytes(e.Transparent ? 1U : 0U));
                PatchIfDiff(loc + 32, BitConverter.GetBytes(e.Smooth ? 1U : 0U));
                PatchIfDiff(loc + 36, BitConverter.GetBytes(e.Preload ? 1U : 0U));
                PatchIfDiff(loc + 40, BitConverter.GetBytes(e.BBoxMode));
                PatchIfDiff(loc + 44, BitConverter.GetBytes(e.ColCheck));
                PatchIfDiff(loc + 48, BitConverter.GetBytes(e.XOrig));
                PatchIfDiff(loc + 52, BitConverter.GetBytes(e.YOrig));
                PatchIfDiff(loc + 56, BitConverter.GetBytes(e.Marker1));
                PatchIfDiff(loc + 60, BitConverter.GetBytes(e.Marker2));
                PatchIfDiff(loc + 64, BitConverter.GetBytes(e.SpriteType));
                PatchIfDiff(loc + 68, BitConverter.GetBytes(e.PlaybackSpeed));
                PatchIfDiff(loc + 72, BitConverter.GetBytes(e.PlaybackSpeedType));
            }
        }

        private void CaptureBgnd()
        {
            if (!TryGetChunk("BGND", out WadChunk chunk) || chunk is not WadBgndChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadBgndEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("BGND", i);
                CaptureName(chunk, "BGND", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.Transparent ? 1U : 0U));
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.Smooth ? 1U : 0U));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.Preload ? 1U : 0U));
                PatchIfDiff(loc + 24, BitConverter.GetBytes(e.TileWidth));
                PatchIfDiff(loc + 28, BitConverter.GetBytes(e.TileHeight));
                PatchIfDiff(loc + 32, BitConverter.GetBytes(e.TileHSep));
                PatchIfDiff(loc + 36, BitConverter.GetBytes(e.TileVSep));
                PatchIfDiff(loc + 40, BitConverter.GetBytes(e.TileBorderX));
                PatchIfDiff(loc + 44, BitConverter.GetBytes(e.TileBorderY));
                PatchIfDiff(loc + 48, BitConverter.GetBytes(e.Columns));
                PatchIfDiff(loc + 52, BitConverter.GetBytes(e.Frames));
                PatchIfDiff(loc + 56, BitConverter.GetBytes(e.TileCount));
                PatchIfDiff(loc + 60, BitConverter.GetBytes(e.SpriteIndex));
            }
        }

        private void CaptureObjt()
        {
            if (!TryGetChunk("OBJT", out WadChunk chunk) || chunk is not WadObjtChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadObjtEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("OBJT", i);
                CaptureName(chunk, "OBJT", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.ParentIndex));
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.Persistent ? 1U : 0U));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.Visible ? 1U : 0U));
            }
        }

        private void CaptureFont()
        {
            if (!TryGetChunk("FONT", out WadChunk chunk) || chunk is not WadFontChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadFontEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("FONT", i);
                CaptureName(chunk, "FONT", i, e.Name);
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.Size));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.Bold ? 1U : 0U));
                PatchIfDiff(loc + 16, BitConverter.GetBytes(e.Italic ? 1U : 0U));
            }
        }

        private void CaptureShdr()
        {
            if (!TryGetChunk("SHDR", out WadChunk chunk) || chunk is not WadShdrChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadShdrEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                CaptureName(chunk, "SHDR", i, e.Name);
            }
        }

        private void CapturePath()
        {
            if (!TryGetChunk("PATH", out WadChunk chunk) || chunk is not WadPathChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadPathEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("PATH", i);
                CaptureName(chunk, "PATH", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.Kind));
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.Closed ? 1U : 0U));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.Precision));
            }
        }

        private void CaptureRoom()
        {
            if (!TryGetChunk("ROOM", out WadChunk chunk) || chunk is not WadRoomChunk c)
                return;
            for (int i = 0; i < c.Rooms.Count; i++)
            {
                WadRoomEntry e = c.Rooms[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("ROOM", i);
                CaptureName(chunk, "ROOM", i, e.Name);
                PatchIfDiff(loc + 16, BitConverter.GetBytes(e.Flag0));
                PatchIfDiff(loc + 20, BitConverter.GetBytes(e.Flag1));
                PatchIfDiff(loc + 24, BitConverter.GetBytes(e.Flag2));
                PatchIfDiff(loc + 28, BitConverter.GetBytes(e.Flag3));
            }
        }

        private void CaptureSeqn()
        {
            if (!TryGetChunk("SEQN", out WadChunk chunk) || chunk is not WadSeqnChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadSeqnEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("SEQN", i);
                CaptureName(chunk, "SEQN", i, e.Name);
                PatchIfDiff(loc + 4, BitConverter.GetBytes(e.Playback));
                PatchIfDiff(loc + 8, BitConverter.GetBytes(e.PlaybackSpeed));
                PatchIfDiff(loc + 12, BitConverter.GetBytes(e.PlaybackSpeedType));
                PatchIfDiff(loc + 16, BitConverter.GetBytes(e.Length));
                PatchIfDiff(loc + 20, BitConverter.GetBytes(e.Xorigin));
                PatchIfDiff(loc + 24, BitConverter.GetBytes(e.Yorigin));
                PatchIfDiff(loc + 28, BitConverter.GetBytes(e.Volume));
                PatchIfDiff(loc + 32, BitConverter.GetBytes(e.Width));
                PatchIfDiff(loc + 36, BitConverter.GetBytes(e.Height));
            }
        }

        // --------------------------------------------------------------------- save

        /// <summary>
        /// Collects model changes, applies them on top of the original bytes and writes
        /// the result (a <c>.bak</c> of the pre-save file is kept next to <paramref name="path"/>).
        /// </summary>
        public void Save(string path = null)
        {
            path ??= _wad.FilePath;
            if (!TryGetHeader("STRG", out WadChunkHeader strgHeader))
                return;   // no string pool to grow; leave the file untouched

            _appendBase = strgHeader.DataOffset + strgHeader.Length;

            CaptureChanges();
            if (_pending.Count == 0 && _appends.Count == 0)
                return;

            int delta = _appendedBytes;
            long strgPayloadEnd = strgHeader.DataOffset + strgHeader.Length;
            long strgLenField = strgHeader.Offset + 4;

            // Any resource chunk that sits after STRG must have its stored absolute
            // offsets shifted by the appended delta.
            if (delta > 0)
            {
                foreach (WadChunkHeader h in _wad.ChunkHeaders)
                {
                    if (h.DataOffset <= strgPayloadEnd)
                        continue;
                    long tableBase = h.DataOffset;
                    if (tableBase + 4 > _original.Length)
                        continue;
                    uint count = BitConverter.ToUInt32(_original, (int)tableBase);
                    for (uint k = 0; k < count && tableBase + 4 + 4L * k + 4 <= _original.Length; k++)
                    {
                        long offPos = tableBase + 4 + 4L * k;
                        uint offVal = BitConverter.ToUInt32(_original, (int)offPos);
                        _pending.Add(new OffsetPatch { FileOffset = offPos, Bytes = BitConverter.GetBytes(unchecked((uint)(offVal + delta))) });
                    }
                }
            }

            // Last write wins per offset (reverting a field cancels the earlier patch).
            Dictionary<long, byte[]> byOffset = new();
            foreach (OffsetPatch p in _pending)
            {
                long target = p.FileOffset >= strgPayloadEnd ? p.FileOffset + delta : p.FileOffset;
                byOffset[target] = p.Bytes;
            }

            byte[] result = new byte[_original.Length + delta];
            Array.Copy(_original, result, checked((int)strgPayloadEnd));
            int off = (int)strgPayloadEnd;
            foreach (byte[] append in _appends)
            {
                append.CopyTo(result, off);
                off += append.Length;
            }
            Array.Copy(_original, checked((int)strgPayloadEnd), result, checked((int)strgPayloadEnd) + delta, _original.Length - checked((int)strgPayloadEnd));

            foreach (KeyValuePair<long, byte[]> kv in byOffset)
                for (int i = 0; i < kv.Value.Length; i++)
                    result[kv.Key + i] = kv.Value[i];

            if (delta > 0)
            {
                BitConverter.GetBytes(unchecked((uint)(result.Length - 8))).CopyTo(result, 4);       // FORM length
                BitConverter.GetBytes(unchecked((uint)(strgHeader.Length + delta))).CopyTo(result, checked((int)strgLenField));
            }

            if (File.Exists(path))
                File.Copy(path, path + ".bak", true);
            File.WriteAllBytes(path, result);
            _pending.Clear();
            _appends.Clear();
            _appendedBytes = 0;
        }
    }
}