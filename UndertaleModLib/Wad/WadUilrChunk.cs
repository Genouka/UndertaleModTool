using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>UILR</c> chunk (UI layers). Field order from <c>UILayerWriter</c> writers
    /// (@772126/@772172 in Runner.exe.c): the chunk's single entry is the whole layer array:
    /// <c>{ u32 layerCount, layer[layerCount] }</c>. Each layer element:
    /// <c>{ u32 regionEnd (abs offset, backpatched), str name, payload }</c> where the payload
    /// begins with a type code (GMUILayer=0, GMUIFlexPanel=1, GMInstance=3, GMSequenceGraphic=4,
    /// GMSpriteGraphic=5, GMTextItem=6, GMUIEffectLayer=7) followed by the per-type fields.
    /// </summary>
    public sealed class WadUilrChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadUilrLayer> Layers => _layers;
        private readonly List<WadUilrLayer> _layers = new();

        internal WadUilrChunk(WadChunkHeader header) : base(header) { }

        internal static WadUilrChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadUilrChunk(header);
            uint data = (uint)header.DataOffset;
            uint chunkEnd = (uint)header.DataOffset + header.Length;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    ParseArray(wad, entryOff, chunkEnd, chunk._layers);
                }
                catch (Exception e)
                {
                    // keep the chunk; per-element errors are recorded on the layers
                    _ = e;
                }
            }
            return chunk;
        }

        private static void ParseArray(UndertaleWadFile wad, uint p, uint chunkEnd, List<WadUilrLayer> sink)
        {
            uint layerCount = wad.ReadUInt32(p);
            p += 4;
            for (uint i = 0; i < layerCount && p < chunkEnd; i++)
            {
                var layer = new WadUilrLayer();
                try
                {
                    layer.ChildrenRegionEnd = wad.ReadUInt32(p);
                    layer.NameRef = wad.ReadUInt32(p + 4);
                    layer.Name = wad.ReadWadString(layer.NameRef);
                    layer.PayloadOffset = p + 8;
                    p = ParsePayload(wad, p + 8, chunkEnd, layer);
                }
                catch (Exception e)
                {
                    layer.Error = e;
                    break;
                }
                sink.Add(layer);
            }
        }

        /// <summary>Parses one layer payload; returns the offset just past it.</summary>
        private static uint ParsePayload(UndertaleWadFile wad, uint p, uint chunkEnd, WadUilrLayer layer)
        {
            layer.Type = wad.ReadUInt32(p);
            p += 4;
            switch (layer.Type)
            {
                case 0: // GMUILayer
                case 1: // GMUIFlexPanel
                {
                    uint childRegionEnd = wad.ReadUInt32(p); p += 4;
                    uint childCount = wad.ReadUInt32(p); p += 4;
                    var children = new List<WadUilrLayer>();
                    for (uint i = 0; i < childCount && p < chunkEnd; i++)
                    {
                        var child = new WadUilrLayer();
                        child.ChildrenRegionEnd = wad.ReadUInt32(p);
                        child.NameRef = wad.ReadUInt32(p + 4);
                        child.Name = wad.ReadWadString(child.NameRef);
                        child.PayloadOffset = p + 8;
                        p = ParsePayload(wad, p + 8, chunkEnd, child);
                        children.Add(child);
                    }
                    layer.Children = children;
                    if (layer.Type == 0)
                    {
                        layer.NameRef2 = wad.ReadUInt32(p); p += 4;
                        layer.DrawSpace = wad.ReadUInt32(p); p += 4;
                        layer.Visible = wad.ReadUInt32(p); p += 4;
                    }
                    else
                    {
                        // flex panel: 10 flex values + 4 scalar + 4 scalars + 4 flex values + 2 floats
                        p += 10 * 8 + 8 + 4 * 8 + 8;
                    }
                    // flex properties tail
                    p += 4 * 4 + 8 + 4 * 8 + 8; // alignItems..layoutDirection
                    break;
                }
                case 3: // GMInstance
                {
                    layer.NameRef2 = wad.ReadUInt32(p); p += 4;
                    p += 4 + 4 + 4 + 4 + 4; // x, y, index, id, compiledIndex
                    p += 4 + 4 + 4;         // scaleX, scaleY, imageSpeed
                    p += 4 + 4;             // imageIndex, colour
                    p += 4;                 // rotation
                    p += 4;                 // compiledPreCreateIndex
                    p += 7 * 4;             // instance flex properties
                    break;
                }
                case 4: // GMSequenceGraphic
                case 5: // GMSpriteGraphic
                {
                    layer.NameRef2 = wad.ReadUInt32(p); p += 4;
                    p += 4 + 4 + 4; // index, x, y
                    p += 4 + 4;     // xScale, yScale
                    p += 4;         // colour
                    p += 4 + 4;     // animationFPS, animationSpeedType
                    p += 4 + 4;     // headPosition, rotation
                    p += 7 * 4;     // instance flex
                    break;
                }
                case 6: // GMTextItem
                {
                    layer.NameRef2 = wad.ReadUInt32(p); p += 4;
                    p += 4 + 4 + 4; // index, x, y
                    p += 4 + 4;     // xScale, yScale
                    p += 4;         // rotation
                    p += 4;         // colour
                    p += 4 + 4 + 4; // xOrigin, yOrigin, origin
                    p += 4;         // text
                    p += 4;         // alignment
                    p += 4 + 4 + 4; // charSpacing, lineSpacing, paragraphSpacing
                    p += 4 + 4;     // frameW, frameH
                    p += 4 + 4;     // wrap, wrapMode
                    p += 7 * 4;     // instance flex
                    break;
                }
                case 7: // GMUIEffectLayer
                {
                    layer.EffectEnabled = wad.ReadUInt32(p); p += 4;
                    layer.EffectTypeRef = wad.ReadUInt32(p); p += 4;
                    layer.EffectType = wad.ReadWadString(layer.EffectTypeRef);
                    uint propCount = wad.ReadUInt32(p); p += 4;
                    p += propCount * 12; // props { type, name, value }
                    break;
                }
                default:
                    layer.Opaque = true;
                    break;
            }
            return p;
        }
    }

    public sealed class WadUilrLayer
    {
        public uint Type { get; internal set; }
        public uint ChildrenRegionEnd { get; internal set; }
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint PayloadOffset { get; internal set; }
        public IReadOnlyList<WadUilrLayer> Children { get; internal set; }
        // type 0/1 tail fields (kept minimal) and element name for 3-7
        public uint NameRef2 { get; internal set; }
        public uint DrawSpace { get; internal set; }
        public uint Visible { get; internal set; }
        public uint EffectEnabled { get; internal set; }
        public uint EffectTypeRef { get; internal set; }
        public string EffectType { get; internal set; }
        // type 3-6 element name (string ref) resolved through NameRef2
        public bool Opaque { get; internal set; }
        public Exception Error { get; internal set; }
    }
}