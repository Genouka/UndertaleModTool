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

        /// <summary>
        /// Raw-edit region: chunk name -> authoritative payload bytes. A chunk present here
        /// is "raw_edit"-marked: <see cref="CaptureChanges"/> skips the model-vs-file comparison
        /// (the modification check) for it and <see cref="Save"/> writes these bytes verbatim.
        /// </summary>
        private readonly Dictionary<string, byte[]> _rawChunks = new();

        /// <summary>
        /// Explicitly-repointed pointer fields, keyed by absolute file offset. When a field
        /// was precisely repointed via <see cref="RepointU32"/>, the string-rename path
        /// (<see cref="CaptureName"/>) must not fight it by appending a replacement record.
        /// </summary>
        private readonly HashSet<long> _explicitPointers = new();

        /// <summary>
        /// Patches queued by <see cref="RepointU32"/>. Kept separate from <see cref="_pending"/>
        /// so that <see cref="CaptureChanges"/> (which clears <c>_pending</c>) does not discard
        /// a precise pointer edit recorded before the capture cycle.
        /// </summary>
        private readonly List<OffsetPatch> _explicitPatches = new();

        public UndertaleWadFile Wad => _wad;
        public bool HasChanges => _pending.Count > 0 || _appends.Count > 0 || _rawChunks.Count > 0 || _explicitPatches.Count > 0;

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

        // ------------------------------------------------------ precise pointer control

        /// <summary>
        /// Precisely repoints a pointer/offset u32 field of an entry to an explicit absolute
        /// file offset (no STRG append is performed, unlike <see cref="PatchStringRef"/>).
        /// Marked as an explicit pointer so <see cref="CaptureName"/> will not overwrite it
        /// with an appended-string repoint for the same field.
        /// </summary>
        public void RepointU32(string chunkName, int entryIndex, int fieldOffset, uint value)
        {
            long fileOffset = EntryLocation(chunkName, entryIndex) + fieldOffset;
            _explicitPointers.Add(fileOffset);
            // Stored separately from _pending so CaptureChanges (which clears _pending)
            // does not discard this precise edit.
            _explicitPatches.Add(new OffsetPatch { FileOffset = fileOffset, Bytes = BitConverter.GetBytes(value) });
        }

        // ------------------------------------------------------- raw chunk ("raw_edit")

        /// <summary>Absolute offset of the chunk's payload.</summary>
        private long ChunkDataOffset(string chunkName)
        {
            if (!TryGetHeader(chunkName, out WadChunkHeader header))
                throw new KeyNotFoundException($"Chunk '{chunkName}' not found.");
            return header.DataOffset;
        }

        /// <summary>
        /// Marks chunk <paramref name="chunkName"/> as <c>raw_edit</c> and stores its raw
        /// payload bytes verbatim. The payload length must match the chunk on disk. Once marked,
        /// <see cref="Save"/> writes these bytes without re-checking the parsed model
        /// (<see cref="CaptureChanges"/> skips the chunk).
        /// </summary>
        public void SetChunkRaw(string chunkName, byte[] raw)
        {
            if (raw is null)
                throw new ArgumentNullException(nameof(raw));
            if (string.Equals(chunkName, "STRG", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The string pool (STRG) cannot be raw-edited; it is managed through string renames.");
            if (!TryGetHeader(chunkName, out WadChunkHeader header))
                throw new KeyNotFoundException($"Chunk '{chunkName}' not found.");
            if (raw.Length != checked((int)header.Length))
                throw new ArgumentException($"Chunk '{chunkName}' payload is {header.Length} bytes; a raw edit must keep the same length (got {raw.Length}).");
            _rawChunks[chunkName] = (byte[])raw.Clone();
        }

        /// <summary>True when the chunk carries the <c>raw_edit</c> mark (its bytes are authoritative).</summary>
        public bool IsChunkRawEdited(string chunkName) => _rawChunks.ContainsKey(chunkName);

        /// <summary>The stored raw-edit payload for a marked chunk (null if not raw_edit).</summary>
        public byte[] GetChunkRaw(string chunkName)
            => _rawChunks.TryGetValue(chunkName, out byte[] raw) ? (byte[])raw.Clone() : null;

        /// <summary>Removes the <c>raw_edit</c> mark and stored bytes for a chunk (reverts to model-based saving).</summary>
        public void ClearChunkRaw(string chunkName) => _rawChunks.Remove(chunkName);

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

        private bool CaptureName(WadChunk chunk, string chunkName, int index, string currentName)
        {
            if (currentName is null)
                return false;
            long loc;
            try
            {
                loc = EntryLocation(chunkName, index);
            }
            catch (Exception)
            {
                return false;
            }
            // A precise pointer repoint on this name ref is authoritative; don't append.
            if (_explicitPointers.Contains(loc))
                return false;
            string onDisk;
            try
            {
                uint refOff = BitConverter.ToUInt32(_original, (int)loc);   // field 0 = string ref
                onDisk = ReadStringAt(refOff);
            }
            catch (Exception)
            {
                return false;
            }
            if (!string.Equals(onDisk, currentName, StringComparison.Ordinal))
            {
                PatchStringRef(chunkName, index, 0, currentName);
                return true;
            }
            return false;
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

        /// <summary>Collects every model-vs-file difference into the patch list. Chunks marked
        /// <c>raw_edit</c> are skipped (their bytes are authoritative and are not re-checked).</summary>
        public void CaptureChanges()
        {
            _pending.Clear();
            _appends.Clear();
            _appendedBytes = 0;
            // Chunks marked raw_edit are guarded per-capture and skipped (bytes authoritative).
            // STRG is only needed for string renames; raw edits save without it.
            CaptureSond(); CaptureSprt(); CaptureBgnd(); CaptureObjt(); CaptureFont();
            CaptureShdr(); CapturePath(); CaptureRoom(); CaptureSeqn();
        }

        private bool ChunkRawEdited(string name) => _rawChunks.ContainsKey(name);

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
            if (ChunkRawEdited("SOND") || !TryGetChunk("SOND", out WadChunk chunk) || chunk is not WadSondChunk c)
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
            if (ChunkRawEdited("SPRT") || !TryGetChunk("SPRT", out WadChunk chunk) || chunk is not WadSprtChunk c)
                return;
            for (int i = 0; i < c.Entries.Count; i++)
            {
                WadSprtEntry e = c.Entries[i];
                if (e.Error is not null)
                    continue;
                long loc = EntryLocation("SPRT", i);
                bool renamed = CaptureName(chunk, "SPRT", i, e.Name);
                // If the name text was not renamed (or the ref was precisely repointed),
                // persist the NameRef pointer value directly so precise pointer edits stick.
                if (!renamed && !_explicitPointers.Contains(loc))
                    PatchIfDiff(loc + 0, BitConverter.GetBytes(e.NameRef));
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
                // Precise pointers: repoint to explicit absolute offsets when changed.
                PatchPtr(loc + 76, e.NineSliceOffset);
                PatchPtr(loc + 80, e.SequenceOffset);
            }
        }

        private void PatchPtr(long fileOffset, uint value)
        {
            if (_explicitPointers.Contains(fileOffset))
                return; // already queued by RepointU32 (authoritative)
            PatchIfDiff(fileOffset, BitConverter.GetBytes(value));
        }

        private void CaptureBgnd()
        {
            if (ChunkRawEdited("BGND") || !TryGetChunk("BGND", out WadChunk chunk) || chunk is not WadBgndChunk c)
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
            if (ChunkRawEdited("OBJT") || !TryGetChunk("OBJT", out WadChunk chunk) || chunk is not WadObjtChunk c)
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
            if (ChunkRawEdited("FONT") || !TryGetChunk("FONT", out WadChunk chunk) || chunk is not WadFontChunk c)
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
            if (ChunkRawEdited("SHDR") || !TryGetChunk("SHDR", out WadChunk chunk) || chunk is not WadShdrChunk c)
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
            if (ChunkRawEdited("PATH") || !TryGetChunk("PATH", out WadChunk chunk) || chunk is not WadPathChunk c)
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
            if (ChunkRawEdited("ROOM") || !TryGetChunk("ROOM", out WadChunk chunk) || chunk is not WadRoomChunk c)
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
            if (ChunkRawEdited("SEQN") || !TryGetChunk("SEQN", out WadChunk chunk) || chunk is not WadSeqnChunk c)
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
        /// Chunks carrying the <c>raw_edit</c> mark are written verbatim and are not re-checked.
        /// </summary>
        public void Save(string path = null)
        {
            path ??= _wad.FilePath;
            bool hasStrg = TryGetHeader("STRG", out WadChunkHeader strgHeader);

            // Raw edits are applied even without a string pool; renames need STRG.
            if (hasStrg)
                _appendBase = strgHeader.DataOffset + strgHeader.Length;

            CaptureChanges();
            if (_pending.Count == 0 && _appends.Count == 0 && _rawChunks.Count == 0 && _explicitPatches.Count == 0)
                return;

            int delta = _appendedBytes;
            long strgPayloadEnd = hasStrg ? strgHeader.DataOffset + strgHeader.Length : _original.Length;
            long strgLenField = hasStrg ? strgHeader.Offset + 4 : -1;

            // Any resource chunk that sits after STRG must have its stored absolute
            // offsets shifted by the appended delta.
            if (hasStrg && delta > 0)
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
            foreach (OffsetPatch p in _explicitPatches)
            {
                long target = p.FileOffset >= strgPayloadEnd ? p.FileOffset + delta : p.FileOffset;
                byOffset[target] = p.Bytes;
            }

            byte[] result = new byte[_original.Length + delta];
            if (hasStrg)
            {
                Array.Copy(_original, result, checked((int)strgPayloadEnd));
                int off = (int)strgPayloadEnd;
                foreach (byte[] append in _appends)
                {
                    append.CopyTo(result, off);
                    off += append.Length;
                }
                Array.Copy(_original, checked((int)strgPayloadEnd), result, checked((int)strgPayloadEnd) + delta, _original.Length - checked((int)strgPayloadEnd));
            }
            else
            {
                // No string pool: produce an output of the same length (delta always 0).
                Array.Copy(_original, result, _original.Length);
            }

            // Apply per-offset patches.
            foreach (KeyValuePair<long, byte[]> kv in byOffset)
                for (int i = 0; i < kv.Value.Length; i++)
                    result[kv.Key + i] = kv.Value[i];

            // Apply raw_edit chunks last so their bytes are authoritative within their region.
            foreach (KeyValuePair<string, byte[]> raw in _rawChunks)
            {
                if (!TryGetHeader(raw.Key, out WadChunkHeader h))
                    continue;
                long dst = ChunkDataOffset(raw.Key);
                if (dst >= strgPayloadEnd)
                    dst += delta;
                checked
                {
                    if (dst + raw.Value.Length > result.Length)
                        continue;
                }
                Array.Copy(raw.Value, 0, result, dst, raw.Value.Length);
            }

            if (hasStrg && delta > 0)
            {
                BitConverter.GetBytes(unchecked((uint)(result.Length - 8))).CopyTo(result, 4);       // FORM length
                BitConverter.GetBytes(unchecked((uint)(strgHeader.Length + delta))).CopyTo(result, checked((int)strgLenField));
            }

            if (File.Exists(path))
                File.Copy(path, path + ".bak", true);
            File.WriteAllBytes(path, result);
            _pending.Clear();
            _explicitPatches.Clear();
            _explicitPointers.Clear();
            _appends.Clear();
            _appendedBytes = 0;
        }
    }
}