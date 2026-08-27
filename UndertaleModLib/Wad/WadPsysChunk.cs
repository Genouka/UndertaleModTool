using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>PSYS</c> chunk (particle systems). Field order from
    /// <c>ParticleWriter::writeParticleSystemToWAD</c> (@752482 in Runner.exe.c):
    /// entry = <c>{ str name, i32 originX, i32 originY, i32 drawOrder, i32 globalSpaceParticles,
    /// u32 emitterCount, emitter[count] }</c>. Each emitter is a flat 59-field record
    /// (name + 14 ints + 34 floats + 10 more ints — exact field order below).
    /// </summary>
    public sealed class WadPsysChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadPsysEntry> Entries => _entries;
        private readonly List<WadPsysEntry> _entries = new();

        internal WadPsysChunk(WadChunkHeader header) : base(header) { }

        internal static WadPsysChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadPsysChunk(header);
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
                    chunk._entries.Add(new WadPsysEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadPsysEntry ParseEntry(UndertaleWadFile wad, uint p)
        {
            var e = new WadPsysEntry
            {
                NameRef = wad.ReadUInt32(p),
                OriginX = wad.ReadInt32(p + 4),
                OriginY = wad.ReadInt32(p + 8),
                DrawOrder = wad.ReadInt32(p + 12),
                GlobalSpaceParticles = wad.ReadInt32(p + 16),
            };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 20;
            uint emitterCount = wad.ReadUInt32(p); p += 4;
            var emitters = new List<WadPsysEmitter>();
            for (uint i = 0; i < emitterCount; i++)
                emitters.Add(ParseEmitter(wad, ref p));
            e.Emitters = emitters;
            return e;
        }

        /// <summary>One emitter: name + 58 scalars in the writer's fixed order.</summary>
        internal static WadPsysEmitter ParseEmitter(UndertaleWadFile wad, ref uint p)
        {
            var em = new WadPsysEmitter { NameRef = wad.ReadUInt32(p) };
            em.Name = wad.ReadWadString(em.NameRef);
            p += 4;
            em.Enabled = wad.ReadInt32(p); p += 4;
            em.Mode = wad.ReadInt32(p); p += 4;
            em.EmitCount = wad.ReadSingle(p); p += 4;
            em.EmitRelative = wad.ReadInt32(p); p += 4;
            em.DelayMin = wad.ReadSingle(p); p += 4;
            em.DelayMax = wad.ReadSingle(p); p += 4;
            em.DelayUnit = wad.ReadInt32(p); p += 4;
            em.IntervalMin = wad.ReadSingle(p); p += 4;
            em.IntervalMax = wad.ReadSingle(p); p += 4;
            em.IntervalUnit = wad.ReadInt32(p); p += 4;
            em.Distribution = wad.ReadInt32(p); p += 4;
            em.Shape = wad.ReadInt32(p); p += 4;
            em.RegionX = wad.ReadSingle(p); p += 4;
            em.RegionY = wad.ReadSingle(p); p += 4;
            em.RegionW = wad.ReadSingle(p); p += 4;
            em.RegionH = wad.ReadSingle(p); p += 4;
            em.Rotation = wad.ReadSingle(p); p += 4;
            em.SpriteId = wad.ReadInt32(p); p += 4;
            em.Texture = wad.ReadInt32(p); p += 4;
            em.HeadPosition = wad.ReadSingle(p); p += 4;
            em.SpriteAnimate = wad.ReadInt32(p); p += 4;
            em.SpriteStretch = wad.ReadInt32(p); p += 4;
            em.SpriteRandom = wad.ReadInt32(p); p += 4;
            em.StartColour = wad.ReadInt32(p); p += 4;
            em.MidColour = wad.ReadInt32(p); p += 4;
            em.EndColour = wad.ReadInt32(p); p += 4;
            em.AdditiveBlend = wad.ReadInt32(p); p += 4;
            em.LifetimeMin = wad.ReadSingle(p); p += 4;
            em.LifetimeMax = wad.ReadSingle(p); p += 4;
            em.ScaleX = wad.ReadSingle(p); p += 4;
            em.ScaleY = wad.ReadSingle(p); p += 4;
            em.SizeMinX = wad.ReadSingle(p); p += 4;
            em.SizeMaxX = wad.ReadSingle(p); p += 4;
            em.SizeMinY = wad.ReadSingle(p); p += 4;
            em.SizeMaxY = wad.ReadSingle(p); p += 4;
            em.SizeIncreaseX = wad.ReadSingle(p); p += 4;
            em.SizeIncreaseY = wad.ReadSingle(p); p += 4;
            em.SizeWiggleX = wad.ReadSingle(p); p += 4;
            em.SizeWiggleY = wad.ReadSingle(p); p += 4;
            em.SpeedMin = wad.ReadSingle(p); p += 4;
            em.SpeedMax = wad.ReadSingle(p); p += 4;
            em.SpeedIncrease = wad.ReadSingle(p); p += 4;
            em.SpeedWiggle = wad.ReadSingle(p); p += 4;
            em.GravityForce = wad.ReadSingle(p); p += 4;
            em.GravityDirection = wad.ReadSingle(p); p += 4;
            em.DirectionMin = wad.ReadSingle(p); p += 4;
            em.DirectionMax = wad.ReadSingle(p); p += 4;
            em.DirectionIncrease = wad.ReadSingle(p); p += 4;
            em.DirectionWiggle = wad.ReadSingle(p); p += 4;
            em.OrientationMin = wad.ReadSingle(p); p += 4;
            em.OrientationMax = wad.ReadSingle(p); p += 4;
            em.OrientationIncrease = wad.ReadSingle(p); p += 4;
            em.OrientationWiggle = wad.ReadSingle(p); p += 4;
            em.OrientationRelative = wad.ReadInt32(p); p += 4;
            em.SpawnOnDeath = wad.ReadInt32(p); p += 4;
            em.SpawnOnDeathCount = wad.ReadInt32(p); p += 4;
            em.SpawnOnUpdate = wad.ReadInt32(p); p += 4;
            em.SpawnOnUpdateCount = wad.ReadInt32(p); p += 4;
            return em;
        }
    }

    public sealed class WadPsysEntry
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public int OriginX { get; internal set; }
        public int OriginY { get; internal set; }
        public int DrawOrder { get; internal set; }
        public int GlobalSpaceParticles { get; internal set; }
        public IReadOnlyList<WadPsysEmitter> Emitters { get; internal set; }
        public Exception Error { get; internal set; }
    }

    /// <summary>One particle emitter (name + 58 scalars).</summary>
    public sealed class WadPsysEmitter
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public int Enabled { get; internal set; }
        public int Mode { get; internal set; }
        public float EmitCount { get; internal set; }
        public int EmitRelative { get; internal set; }
        public float DelayMin { get; internal set; }
        public float DelayMax { get; internal set; }
        public int DelayUnit { get; internal set; }
        public float IntervalMin { get; internal set; }
        public float IntervalMax { get; internal set; }
        public int IntervalUnit { get; internal set; }
        public int Distribution { get; internal set; }
        public int Shape { get; internal set; }
        public float RegionX { get; internal set; }
        public float RegionY { get; internal set; }
        public float RegionW { get; internal set; }
        public float RegionH { get; internal set; }
        public float Rotation { get; internal set; }
        public int SpriteId { get; internal set; }
        public int Texture { get; internal set; }
        public float HeadPosition { get; internal set; }
        public int SpriteAnimate { get; internal set; }
        public int SpriteStretch { get; internal set; }
        public int SpriteRandom { get; internal set; }
        public int StartColour { get; internal set; }
        public int MidColour { get; internal set; }
        public int EndColour { get; internal set; }
        public int AdditiveBlend { get; internal set; }
        public float LifetimeMin { get; internal set; }
        public float LifetimeMax { get; internal set; }
        public float ScaleX { get; internal set; }
        public float ScaleY { get; internal set; }
        public float SizeMinX { get; internal set; }
        public float SizeMaxX { get; internal set; }
        public float SizeMinY { get; internal set; }
        public float SizeMaxY { get; internal set; }
        public float SizeIncreaseX { get; internal set; }
        public float SizeIncreaseY { get; internal set; }
        public float SizeWiggleX { get; internal set; }
        public float SizeWiggleY { get; internal set; }
        public float SpeedMin { get; internal set; }
        public float SpeedMax { get; internal set; }
        public float SpeedIncrease { get; internal set; }
        public float SpeedWiggle { get; internal set; }
        public float GravityForce { get; internal set; }
        public float GravityDirection { get; internal set; }
        public float DirectionMin { get; internal set; }
        public float DirectionMax { get; internal set; }
        public float DirectionIncrease { get; internal set; }
        public float DirectionWiggle { get; internal set; }
        public float OrientationMin { get; internal set; }
        public float OrientationMax { get; internal set; }
        public float OrientationIncrease { get; internal set; }
        public float OrientationWiggle { get; internal set; }
        public int OrientationRelative { get; internal set; }
        public int SpawnOnDeath { get; internal set; }
        public int SpawnOnDeathCount { get; internal set; }
        public int SpawnOnUpdate { get; internal set; }
        public int SpawnOnUpdateCount { get; internal set; }
    }
}