using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>ROOM</c> chunk. Matches <c>CRoomGM::LoadFromChunk</c> in the runner.
    /// <code>
    /// { u32 count, u32[count] entryOffsets }
    /// entry: { u32 nameRecordOffset, u32 creationCodeRef, i32 width, i32 height,
    ///          u32 flag0, u32 flag1, u32 flag2, u32 flag3,
    ///          u32 viewCount, u32[viewCount] viewOffsets, view[viewCount] (56 B each, inline),
    ///          u32 instanceCount, u32[instanceCount] instanceOffsets, instance[instanceCount] (52 B each, inline),
    ///          u32 layerCount, u32[layerCount] layerOffsets, layer[layerCount] (variable, inline),
    ///          roomComponents... }
    /// </code>
    /// All offsets are absolute file offsets; sub-lists are packed sequentially, with records
    /// stored inline immediately after each offset array (the offsets point at them).
    /// Each layer record is <c>{ 40-byte header, kind-specific element data, layerComponents }</c>.
    /// Component sections use the envelope <c>{ u32 fieldA, u32 fieldB, u32[fieldB] offsets }</c>
    /// with a total section size of <c>fieldA + 4</c> bytes.
    /// </summary>
    public sealed class WadRoomChunk : WadChunk
    {
        public uint Count { get; private set; }

        public IReadOnlyList<WadRoomEntry> Rooms => _rooms;

        private readonly List<WadRoomEntry> _rooms = new();

        internal WadRoomChunk(WadChunkHeader header) : base(header)
        {
        }

        internal static WadRoomChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadRoomChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    chunk._rooms.Add(ParseEntry(wad, chunk, entryOff, i));
                }
                catch (Exception e)
                {
                    chunk._rooms.Add(new WadRoomEntry { Error = e });
                }
            }
            return chunk;
        }

        private static WadRoomEntry ParseEntry(UndertaleWadFile wad, WadRoomChunk chunk, uint r, uint entryIndex)
        {
            var entry = new WadRoomEntry();
            uint pos = r;
            entry.Name = wad.ReadWadString(wad.ReadUInt32(pos)); pos += 4;
            entry.CreationCodeRef = wad.ReadUInt32(pos); pos += 4;
            entry.Width = wad.ReadInt32(pos); pos += 4;
            entry.Height = wad.ReadInt32(pos); pos += 4;
            entry.Flag0 = wad.ReadUInt32(pos); pos += 4;
            entry.Flag1 = wad.ReadUInt32(pos); pos += 4;
            entry.Flag2 = wad.ReadUInt32(pos); pos += 4;
            entry.Flag3 = wad.ReadUInt32(pos); pos += 4;

            // Views: count, offsets, then 56-byte inline records.
            uint viewCount = wad.ReadUInt32(pos); pos += 4;
            uint[] viewOffsets = new uint[viewCount];
            for (uint i = 0; i < viewCount; i++)
            {
                viewOffsets[i] = wad.ReadUInt32(pos); pos += 4;
            }
            var views = new List<WadRoomView>();
            for (uint i = 0; i < viewCount; i++)
            {
                views.Add(ParseView(wad, viewOffsets[i]));
            }
            entry.Views = views;
            pos += 56 * viewCount; // skip inline view records

            // Instances: count, offsets, then 52-byte inline records.
            uint instanceCount = wad.ReadUInt32(pos); pos += 4;
            uint[] instanceOffsets = new uint[instanceCount];
            for (uint i = 0; i < instanceCount; i++)
            {
                instanceOffsets[i] = wad.ReadUInt32(pos); pos += 4;
            }
            var instances = new List<WadRoomInstance>();
            for (uint i = 0; i < instanceCount; i++)
            {
                instances.Add(ParseInstance(wad, instanceOffsets[i]));
            }
            entry.Instances = instances;
            pos += 52 * instanceCount; // skip inline instance records

            // Layers: count, offsets, variable inline records.
            uint layerCount = wad.ReadUInt32(pos); pos += 4;
            uint[] layerOffsets = new uint[layerCount];
            for (uint i = 0; i < layerCount; i++)
            {
                layerOffsets[i] = wad.ReadUInt32(pos); pos += 4;
            }
            uint chunkEnd = (uint)chunk.DataOffset + chunk.Length;
            var layers = new List<WadRoomLayer>();
            uint maxLayerEnd = pos;
            for (uint i = 0; i < layerCount; i++)
            {
                try
                {
                    (WadRoomLayer layer, uint end) = ParseLayer(wad, layerOffsets[i], (int)i, chunkEnd);
                    if (end > maxLayerEnd)
                        maxLayerEnd = end;
                    layers.Add(layer);
                }
                catch (Exception e)
                {
                    var layer = new WadRoomLayer { Index = (int)i, Error = e };
                    layers.Add(layer);
                }
            }
            entry.Layers = layers;

            // The room's own component section(s) follow the last layer's inline data and end
// exactly at the next room entry (or the chunk end).
            entry.ComponentsSectionOffset = maxLayerEnd;
            uint chunkData = (uint)chunk.DataOffset;
            uint sectionsEnd = (entryIndex < chunk.Count - 1) ? wad.ReadUInt32(chunkData + 4 + 4 * (entryIndex + 1)) : chunkEnd;
            entry.Components = ParseSectionsUntil(wad, maxLayerEnd, Math.Min(sectionsEnd, chunkEnd));
            return entry;
        }

        /// <summary>Parses consecutive component sections starting at <paramref name="pos"/> until
        /// <paramref name="end"/> is reached; each section spans <c>fieldA + 4</c> bytes.</summary>
        private static List<WadComponent> ParseSectionsUntil(UndertaleWadFile wad, uint start, uint end)
        {
            var list = new List<WadComponent>();
            uint pos = start;
            while (pos + 8 <= end)
            {
                uint fieldA = wad.ReadUInt32(pos);
                if ((long)pos + fieldA + 4 > end + 4)
                    break; // invalid envelope; stop
                list.AddRange(ParseComponentSectionSafe(wad, pos, end));
                pos += fieldA + 4;
                if (pos == start)
                    break; // no progress guard
            }
            return list;
        }

        /// <summary>Reads a component section at <paramref name="pos"/> (envelope
        /// <c>{ u32 fieldA, u32 fieldB, u32[fieldB] offsets }</c>, total size <c>fieldA + 4</c>).</summary>
        private static List<WadComponent> ParseComponentSectionSafe(UndertaleWadFile wad, uint pos, uint end)
        {
            var list = new List<WadComponent>();
            if (pos == 0 || (long)pos + 8 > end || end > wad.FileSize)
                return list;
            uint fieldA = wad.ReadUInt32(pos);
            uint fieldB = wad.ReadUInt32(pos + 4);
            uint offsPos = pos + 8;
            if (offsPos > end)
                return list;
            uint maxI = Math.Min(fieldB, (end - offsPos) / 4);
            for (uint i = 0; i < maxI; i++)
            {
                uint compOff = wad.ReadUInt32(offsPos + 4 * i);
                if (compOff == 0 || (long)compOff + 4 > end)
                    continue;
                string compName = wad.ReadWadString(wad.ReadUInt32(compOff));
                list.Add(new WadComponent { Name = compName, EntryOffset = compOff });
            }
            return list;
        }

        private static WadRoomView ParseView(UndertaleWadFile wad, uint v)
        {
            var view = new WadRoomView();
            view.Enabled = wad.ReadUInt32(v) != 0;
            view.Xview = wad.ReadInt32(v + 4);
            view.Yview = wad.ReadInt32(v + 8);
            view.Wview = wad.ReadInt32(v + 12);
            view.Hview = wad.ReadInt32(v + 16);
            view.Unknown0 = wad.ReadUInt32(v + 20);
            view.Unknown1 = wad.ReadUInt32(v + 24);
            view.Unknown2 = wad.ReadUInt32(v + 28);
            view.Unknown3 = wad.ReadUInt32(v + 32);
            view.Unknown4 = wad.ReadUInt32(v + 36);
            view.Unknown5 = wad.ReadUInt32(v + 40);
            view.Unknown6 = wad.ReadUInt32(v + 44);
            view.ViewObjectRef = wad.ReadUInt32(v + 48);
            view.Unknown7 = wad.ReadUInt32(v + 52);
            return view;
        }

        private static WadRoomInstance ParseInstance(UndertaleWadFile wad, uint i)
        {
            var inst = new WadRoomInstance();
            inst.Name = wad.ReadWadString(wad.ReadUInt32(i));
            inst.X = wad.ReadInt32(i + 4);
            inst.Y = wad.ReadInt32(i + 8);
            inst.ObjectRef = wad.ReadUInt32(i + 12);
            inst.InstanceId = wad.ReadUInt32(i + 16);
            inst.CreationCodeRef = wad.ReadUInt32(i + 20);
            inst.ScaleX = wad.ReadSingle(i + 24);
            inst.ScaleY = wad.ReadSingle(i + 28);
            inst.Rotation = wad.ReadSingle(i + 32);
            inst.Unknown0 = wad.ReadUInt32(i + 36);
            inst.Unknown1 = wad.ReadUInt32(i + 40);
            inst.Unknown2 = wad.ReadUInt32(i + 44);
            inst.CreationCodeRef2 = wad.ReadUInt32(i + 48);
            return inst;
        }

        /// <summary>Parses one layer; returns the layer and the absolute offset just past its
        /// inline record (40-byte header + kind data + layer component section).</summary>
        private static (WadRoomLayer, uint) ParseLayer(UndertaleWadFile wad, uint l, int index, uint chunkEnd)
        {
            var layer = new WadRoomLayer();
            layer.Index = index;
            layer.Name = wad.ReadWadString(wad.ReadUInt32(l));
            layer.SortValue = wad.ReadUInt32(l + 4);
            layer.Kind = wad.ReadUInt32(l + 8);
            layer.TypeName = wad.ReadWadString(wad.ReadUInt32(l + 12));
            for (int k = 0; k < 5; k++)
                layer.Unknown[k] = wad.ReadUInt32(l + 16u + 4u * (uint)k);
            layer.BoolFlag = wad.ReadUInt32(l + 36) != 0;

            uint pos = l + 40;
            switch (layer.Kind)
            {
                case 1: // background layer: opaque element data (observed 36 bytes)
                {
                    layer.ElementData = wad.ReadBytes(pos, 36);
                    pos += 36;
                    break;
                }
                case 2: // instance layer: { u32 elementCount, elementCount instance ids }
                {
                    uint elemCount = wad.ReadUInt32(pos); pos += 4;
                    var ids = new List<uint>();
                    for (uint k = 0; k < elemCount; k++)
                    {
                        ids.Add(wad.ReadUInt32(pos)); pos += 4;
                    }
                    layer.InstanceIds = ids;
                    break;
                }
                case 4: // tilemap layer: { u32 opaque, u32 cols, u32 rows, cols*rows tile ids }
                {
                    layer.TilemapUnknown = wad.ReadUInt32(pos); pos += 4;
                    uint cols = wad.ReadUInt32(pos); pos += 4;
                    uint rows = wad.ReadUInt32(pos); pos += 4;
                    layer.TilemapColumns = cols;
                    layer.TilemapRows = rows;
                    var tiles = new List<uint>(checked((int)(cols * rows)));
                    for (uint k = 0; k < cols * rows; k++)
                    {
                        tiles.Add(wad.ReadUInt32(pos)); pos += 4;
                    }
                    layer.Tiles = tiles;
                    break;
                }
                default:
                    layer.ElementData = Array.Empty<byte>();
                    break;
            }

            // Layer component section follows immediately.
            layer.ComponentsSectionOffset = pos;
            layer.Components = ParseComponentSectionSafe(wad, pos, chunkEnd);
            // Advance past the section (total size = fieldA + 4), bounded by the chunk end.
            if (pos + 4 <= chunkEnd)
            {
                uint fieldA = wad.ReadUInt32(pos);
                if (fieldA + 4 < chunkEnd - pos)
                    pos += fieldA + 4;
            }
            return (layer, pos);
        }
    }

    /// <summary>One room entry.</summary>
    public sealed class WadRoomEntry
    {
        public string Name { get; set; }
        public uint CreationCodeRef { get; internal set; }
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public uint Flag0 { get; set; }
        public uint Flag1 { get; set; }
        public uint Flag2 { get; set; }
        public uint Flag3 { get; set; }
        public IReadOnlyList<WadRoomView> Views { get; internal set; }
        public IReadOnlyList<WadRoomInstance> Instances { get; internal set; }
        public IReadOnlyList<WadRoomLayer> Layers { get; internal set; }
        public uint ComponentsSectionOffset { get; internal set; }
        public IReadOnlyList<WadComponent> Components { get; internal set; }
        public Exception Error { get; internal set; }
    }

    /// <summary>A room view (56 bytes).</summary>
    public sealed class WadRoomView
    {
        public bool Enabled { get; internal set; }
        public int Xview { get; internal set; }
        public int Yview { get; internal set; }
        public int Wview { get; internal set; }
        public int Hview { get; internal set; }
        public uint Unknown0 { get; internal set; }
        public uint Unknown1 { get; internal set; }
        public uint Unknown2 { get; internal set; }
        public uint Unknown3 { get; internal set; }
        public uint Unknown4 { get; internal set; }
        public uint Unknown5 { get; internal set; }
        public uint Unknown6 { get; internal set; }
        public uint ViewObjectRef { get; internal set; }
        public uint Unknown7 { get; internal set; }
    }

    /// <summary>A room instance (52 bytes).</summary>
    public sealed class WadRoomInstance
    {
        public string Name { get; set; }
        public int X { get; internal set; }
        public int Y { get; internal set; }
        public uint ObjectRef { get; internal set; }
        public uint InstanceId { get; internal set; }
        public uint CreationCodeRef { get; internal set; }
        public float ScaleX { get; internal set; }
        public float ScaleY { get; internal set; }
        public float Rotation { get; internal set; }
        public uint Unknown0 { get; internal set; }
        public uint Unknown1 { get; internal set; }
        public uint Unknown2 { get; internal set; }
        public uint CreationCodeRef2 { get; internal set; }
    }

    /// <summary>A room layer. Kind 1 = background, 2 = instance, 3 = tile, 4 = tilemap.</summary>
    public sealed class WadRoomLayer
    {
        public int Index { get; internal set; }
        public string Name { get; set; }
        public uint SortValue { get; internal set; }
        public uint Kind { get; internal set; }
        public string TypeName { get; internal set; }
        public uint[] Unknown { get; internal set; } = new uint[5];
        public bool BoolFlag { get; internal set; }

        // Kind 2: instance ids
        public IReadOnlyList<uint> InstanceIds { get; internal set; }

        // Kind 4: tilemap
        public uint TilemapUnknown { get; internal set; }
        public uint TilemapColumns { get; internal set; }
        public uint TilemapRows { get; internal set; }
        public IReadOnlyList<uint> Tiles { get; internal set; }

        // Other kinds: opaque element bytes
        public byte[] ElementData { get; internal set; }

        public uint ComponentsSectionOffset { get; internal set; }
        public IReadOnlyList<WadComponent> Components { get; internal set; }
        public Exception Error { get; internal set; }
    }

    /// <summary>A named component inside a component section (raw entry offset kept; data
    /// interpretation is per component name, see the runner's component handlers).</summary>
    public sealed class WadComponent
    {
        public string Name { get; set; }
        public uint EntryOffset { get; internal set; }
    }
}