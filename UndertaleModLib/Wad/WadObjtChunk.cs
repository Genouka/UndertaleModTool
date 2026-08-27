using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>OBJT</c> chunk (objects). Field order from
    /// <c>ResourceWriter::writeObjectToWAD</c> (@870351 in Runner.exe.c) and the runner's
    /// per-component writers:
    /// entry = <c>{ str name, u32 parentIndex, u32 persistent, u32 visible,
    /// u32 eventCount, { u32 eventNum, u32 eventType, u32 scriptIndex }[eventCount],
    /// components section }</c>.
    /// </summary>
    public sealed class WadObjtChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadObjtEntry> Entries => _entries;
        private readonly List<WadObjtEntry> _entries = new();

        internal WadObjtChunk(WadChunkHeader header) : base(header) { }

        internal static WadObjtChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadObjtChunk(header);
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
                    chunk._entries.Add(ParseEntry(wad, entryOff, Math.Min(entryEnd, chunkEnd)));
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadObjtEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadObjtEntry ParseEntry(UndertaleWadFile wad, uint p, uint entryEnd)
        {
            var e = new WadObjtEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            e.ParentIndex = wad.ReadUInt32(p + 4);
            e.Persistent = wad.ReadUInt32(p + 8) != 0;
            e.Visible = wad.ReadUInt32(p + 12) != 0;
            uint eventCount = wad.ReadUInt32(p + 16);
            p += 20;
            var events = new List<WadObjtEvent>();
            for (uint i = 0; i < eventCount; i++)
            {
                uint eventOff = wad.ReadUInt32(p);
                p += 4;
                if ((long)eventOff + 12 > entryEnd)
                    continue;
                events.Add(new WadObjtEvent
                {
                    EventNum = wad.ReadInt32(eventOff),
                    EventType = wad.ReadUInt32(eventOff + 4),
                    ScriptIndex = wad.ReadUInt32(eventOff + 8),
                });
            }
            e.Events = events;
            // Event records are stored inline (12 bytes each) right after the offset array.
            p += 12 * eventCount;

            // Component section: { u32 fieldA, u32 fieldB, u32[fieldB] compOffsets }.
            // Each component entry: { u32 nameRef, payload per component type }.
            e.ComponentSectionOffset = p;
            if (p + 8 <= entryEnd)
            {
                uint fieldA = wad.ReadUInt32(p);
                uint fieldB = wad.ReadUInt32(p + 4);
                uint offsPos = p + 8;
                uint maxI = Math.Min(fieldB, (entryEnd - offsPos) / 4);
                for (uint i = 0; i < maxI; i++)
                {
                    uint compOff = wad.ReadUInt32(offsPos + 4 * i);
                    if (compOff == 0 || (long)compOff + 4 > entryEnd)
                        continue;
                    var comp = new WadObjtComponent { NameRef = wad.ReadUInt32(compOff) };
                    comp.Name = wad.ReadWadString(comp.NameRef);
                    comp.EntryOffset = compOff;
                    // Payload differs per component name (writer bodies; shipped wad
                    // omits the writer's debug selfOff prefix):
                    uint d = compOff + 4;
                    switch (comp.Name)
                    {
                        case "GM.Systems.collision":
                            comp.Payload = new WadObjtPayload { CollisionSolid = wad.ReadUInt32(d) != 0 };
                            break;
                        case "GM.Systems.spritemanager":
                            comp.Payload = new WadObjtPayload { SpriteIndex = wad.ReadUInt32(d), MaskIndex = wad.ReadUInt32(d + 4) };
                            break;
                        case "GM.Systems.physics":
                            var phy = new WadObjtPayload
                            {
                                PhysicsObject = wad.ReadUInt32(d),
                                PhysicsSensor = wad.ReadUInt32(d + 4),
                                PhysicsShape = wad.ReadUInt32(d + 8),
                                PhysicsDensity = wad.ReadSingle(d + 12),
                                PhysicsRestitution = wad.ReadSingle(d + 16),
                                PhysicsGroup = wad.ReadUInt32(d + 20),
                                PhysicsLinearDamping = wad.ReadSingle(d + 24),
                                PhysicsAngularDamping = wad.ReadSingle(d + 28),
                                ShapePointCount = wad.ReadUInt32(d + 32),
                                PhysicsFriction = wad.ReadSingle(d + 36),
                                PhysicsStartAwake = wad.ReadUInt32(d + 40) != 0,
                                PhysicsKinematic = wad.ReadUInt32(d + 44) != 0,
                            };
                            uint pts = wad.ReadUInt32(d + 32);
                            if (pts < 4096)
                            {
                                var shape = new List<WadShapePoint>();
                                for (uint k = 0; k < pts; k++)
                                {
                                    shape.Add(new WadShapePoint { X = wad.ReadSingle(d + 48 + 8 * k), Y = wad.ReadSingle(d + 52 + 8 * k) });
                                }
                                phy.ShapePoints = shape;
                            }
                            comp.Payload = phy;
                            break;
                        default:
                            comp.RawPayload = Array.Empty<byte>();
                            break;
                    }
                    e.Components.Add(comp);
                }
            }
            return e;
        }
    }

    public sealed class WadObjtEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint ParentIndex { get; internal set; }
        public bool Persistent { get; internal set; }
        public bool Visible { get; internal set; }
        public IReadOnlyList<WadObjtEvent> Events { get; internal set; }
        public uint ComponentSectionOffset { get; internal set; }
        public List<WadObjtComponent> Components { get; } = new();
        public Exception Error { get; internal set; }
    }

    public sealed class WadObjtEvent
    {
        public int EventNum { get; internal set; }
        public uint EventType { get; internal set; }
        public uint ScriptIndex { get; internal set; }
    }

    public sealed class WadObjtComponent
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public uint EntryOffset { get; internal set; }
        public WadObjtPayload Payload { get; internal set; }
        public byte[] RawPayload { get; internal set; }
    }

    /// <summary>Payload of the known object component writers.</summary>
    public sealed class WadObjtPayload
    {
        // GM.Systems.collision
        public bool CollisionSolid { get; internal set; }
        // GM.Systems.spritemanager
        public uint SpriteIndex { get; internal set; }
        public uint MaskIndex { get; internal set; }
        // GM.Systems.physics
        public uint PhysicsObject { get; internal set; }
        public uint PhysicsSensor { get; internal set; }
        public uint PhysicsShape { get; internal set; }
        public float PhysicsDensity { get; internal set; }
        public float PhysicsRestitution { get; internal set; }
        public uint PhysicsGroup { get; internal set; }
        public float PhysicsLinearDamping { get; internal set; }
        public float PhysicsAngularDamping { get; internal set; }
        public uint ShapePointCount { get; internal set; }
        public float PhysicsFriction { get; internal set; }
        public bool PhysicsStartAwake { get; internal set; }
        public bool PhysicsKinematic { get; internal set; }
        public IReadOnlyList<WadShapePoint> ShapePoints { get; internal set; }
    }

    public sealed class WadShapePoint
    {
        public float X { get; internal set; }
        public float Y { get; internal set; }
    }
}