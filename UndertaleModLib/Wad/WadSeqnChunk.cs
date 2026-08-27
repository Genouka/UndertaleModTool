using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>SEQN</c> chunk (sequences). Field order from the runner writers
    /// <c>SequenceWriter::writeSequenceToWAD</c> (Runner.exe.c @759734), with nested
    /// keyframe-store / track / event2function writers. As with every chunk in the shipped
    /// wad, string references are absolute offsets into <c>STRG</c>.
    /// </summary>
    public sealed class WadSeqnChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadSeqnEntry> Entries => _entries;
        private readonly List<WadSeqnEntry> _entries = new();

        internal WadSeqnChunk(WadChunkHeader header) : base(header) { }

        internal static WadSeqnChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadSeqnChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    chunk._entries.Add(ParseEntry(wad, chunk, entryOff));
                }
                catch (Exception e)
                {
                    chunk._entries.Add(new WadSeqnEntry { Error = e });
                }
            }
            return chunk;
        }

        internal static WadSeqnEntry ParseEntry(UndertaleWadFile wad, WadChunk chunk, uint p)
        {
            var e = new WadSeqnEntry { NameRef = wad.ReadUInt32(p) };
            e.Name = wad.ReadWadString(e.NameRef);
            p += 4;
            e.Playback = wad.ReadInt32(p); p += 4;
            e.PlaybackSpeed = wad.ReadSingle(p); p += 4;
            e.PlaybackSpeedType = wad.ReadInt32(p); p += 4;
            e.Length = wad.ReadSingle(p); p += 4;
            e.Xorigin = wad.ReadInt32(p); p += 4;
            e.Yorigin = wad.ReadInt32(p); p += 4;
            e.Volume = wad.ReadSingle(p); p += 4;
            e.Width = wad.ReadSingle(p); p += 4;
            e.Height = wad.ReadSingle(p); p += 4;
            e.Events = ParseKeyframeStore(wad, ref p);
            e.Tracks = ParseTracks(wad, ref p);
            e.EventToFunction = ParseEventToFunction(wad, ref p);
            e.Moments = ParseKeyframeStore(wad, ref p);
            e.ParseEndOffset = p;
            return e;
        }

        /// <summary>KeyframeStore: <c>{ u32 count, keyframe[count] }</c>.</summary>
        internal static WadSeqnKeyframeStore ParseKeyframeStore(UndertaleWadFile wad, ref uint p)
        {
            return ParseKeyframeStore(wad, ref p, null);
        }

        /// <summary>KeyframeStore: <c>{ u32 count, keyframe[count] }</c>. When
        /// <paramref name="modelName"/> is null the keyframes belong to the moments/events
        /// stores (channels are moment-event lists); otherwise they belong to a track of the
        /// given model.</summary>
        internal static WadSeqnKeyframeStore ParseKeyframeStore(UndertaleWadFile wad, ref uint p, string modelName)
        {
            var store = new WadSeqnKeyframeStore();
            uint count = wad.ReadUInt32(p); p += 4;
            if (UndertaleWadFile.DebugWalk)
                Console.WriteLine($"      keyframes @{p - 4:X8}: count={count} model={modelName ?? "-"}");
            var frames = new List<WadSeqnKeyframe>();
            for (uint i = 0; i < count; i++)
                frames.Add(ParseKeyframe(wad, ref p, modelName));
            store.Keyframes = frames;
            return store;
        }

        /// <summary>Keyframe: <c>{ float key, float length, i32 stretch, i32 disabled,
        /// u32 channelCount, channel[count] }</c>.</summary>
        internal static WadSeqnKeyframe ParseKeyframe(UndertaleWadFile wad, ref uint p, string modelName)
        {
            var k = new WadSeqnKeyframe();
            k.Key = wad.ReadSingle(p); p += 4;
            k.Length = wad.ReadSingle(p); p += 4;
            k.Stretch = wad.ReadInt32(p); p += 4;
            k.Disabled = wad.ReadInt32(p) != 0; p += 4;
            uint channelCount = wad.ReadUInt32(p); p += 4;
            var channels = new List<WadSeqnKeyframeChannel>();
            for (uint i = 0; i < channelCount; i++)
            {
                if (modelName == null)
                    channels.Add(ParseMomentChannel(wad, ref p));
                else
                    channels.Add(ParseKeyframeChannel(wad, modelName, ref p));
            }
            k.Channels = channels;
            return k;
        }

        /// <summary>Moment-event channel: <c>{ i32 key, u32 count, u32[count] eventNameRefs }</c>.</summary>
        private static WadSeqnKeyframeChannel ParseMomentChannel(UndertaleWadFile wad, ref uint p)
        {
            var c = new WadSeqnKeyframeChannel { Key = wad.ReadInt32(p) };
            p += 4;
            uint n = wad.ReadUInt32(p); p += 4;
            var events = new List<uint>();
            for (uint i = 0; i < n; i++)
            {
                events.Add(wad.ReadUInt32(p)); p += 4;
            }
            c.Events = events;
            return c;
        }

        /// <summary>
        /// Keyframe channel. The runner writer (<c>writeKeyframeChannelToWAD</c>) dispatches on
        /// the channel's <c>"type"</c> string but never serializes it; the shipped wad omits it
        /// too — the reader infers the payload layout from the owning track's model name
        /// (GMRealTrack → realValue, GMColourTrack → colour, GMInstanceTrack → assetIndex…).
        /// </summary>
        internal static WadSeqnKeyframeChannel ParseKeyframeChannel(UndertaleWadFile wad, string modelName, ref uint p)
        {
            var c = new WadSeqnKeyframeChannel { Key = wad.ReadInt32(p) };
            p += 4;
            switch (modelName)
            {
                case "GMColourTrack":
                    c.Color = wad.ReadInt32(p); p += 4;
                    c.CurveTail = WadSeqnChunk.ParseCurveTail(wad, ref p);
                    break;
                case "GMAudioEffectTrack":
                    c.RealValue = wad.ReadSingle(p); p += 4;
                    c.CurveTail = WadSeqnChunk.ParseCurveTail(wad, ref p);
                    break;
                case "GMRealTrack":
                    c.RealValue = wad.ReadSingle(p); p += 4;
                    c.CurveTail = WadSeqnChunk.ParseCurveTail(wad, ref p);
                    break;
                case "GMInstanceTrack":
                case "GMSpriteTrack":
                case "GMSequenceTrack":
                case "GMParticleSystemTrack":
                case "GMTextTrack":
                case "GMAudioTrack":
                case "GMStringTrack":
                case "GMBoolTrack":
                default:
                    // asset-index / string / bool / audio and unknown models: a single value
                    c.AssetIndex = wad.ReadInt32(p); p += 4;
                    break;
            }
            return c;
        }

        /// <summary>Curve tail: <c>{ i32 isCurveEmbedded, (0xFFFFFFFF + embedded curve | i32 curveIndex) }</c> —
        /// both branches always emit the second u32 in the shipped wad (curveIndex = −1 when no
        /// curve).</summary>
        internal static WadCurveTail ParseCurveTail(UndertaleWadFile wad, ref uint p)
        {
            var tail = new WadCurveTail { IsCurveEmbedded = wad.ReadInt32(p) != 0 };
            p += 4;
            if (tail.IsCurveEmbedded)
            {
                tail.EmbeddedMarker = wad.ReadUInt32(p); p += 4;
                tail.EmbeddedCurve = WadAcrvChunk.ParseCurveBody(wad, ref p); // curve body without name
            }
            else
            {
                tail.CurveIndex = wad.ReadInt32(p); p += 4;
            }
            return tail;
        }

        /// <summary>Tracks: <c>{ u32 count, track[count] }</c>.</summary>
        internal static WadSeqnTracks ParseTracks(UndertaleWadFile wad, ref uint p)
        {
            var t = new WadSeqnTracks();
            uint count = wad.ReadUInt32(p); p += 4;
            var tracks = new List<WadSeqnTrack>();
            for (uint i = 0; i < count; i++)
                tracks.Add(ParseTrack(wad, ref p));
            t.Tracks = tracks;
            return t;
        }

        /// <summary>Track: modelName, name, builtinName, traits, isCreationTrack, tagCount, tags,
        /// ownedResourceModelCount, owned models (each a string ref), subTrackCount, sub-tracks
        /// (recursive), interpolation (value tracks only), keyframes store.</summary>
        internal static WadSeqnTrack ParseTrack(UndertaleWadFile wad, ref uint p)
        {
            var tr = new WadSeqnTrack
            {
                ModelNameRef = wad.ReadUInt32(p),
                NameRef = wad.ReadUInt32(p + 4),
                BuiltinName = wad.ReadInt32(p + 8),
                Traits = wad.ReadInt32(p + 12),
                IsCreationTrack = wad.ReadInt32(p + 16) != 0,
            };
            p += 20;
            uint tagCount = wad.ReadUInt32(p); p += 4;
            var tags = new List<uint>();
            for (uint i = 0; i < tagCount; i++)
            {
                tags.Add(wad.ReadUInt32(p)); p += 4;
            }
            tr.Tags = tags;

            uint ownedCount = wad.ReadUInt32(p); p += 4;
            var owned = new List<uint>();
            for (uint i = 0; i < ownedCount; i++)
            {
                owned.Add(wad.ReadUInt32(p)); p += 4;
            }
            tr.OwnedResourceModels = owned;

            uint subCount = wad.ReadUInt32(p); p += 4;
            var subs = new List<WadSeqnTrack>();
            for (uint i = 0; i < subCount; i++)
                subs.Add(ParseTrack(wad, ref p));
            tr.SubTracks = subs;
            if (UndertaleWadFile.DebugWalk)
                Console.WriteLine($"    track @{p - 4:X8}: model='{wad.ReadWadString(tr.ModelNameRef)}' name='{wad.ReadWadString(tr.NameRef)}' tags={tagCount} owned={ownedCount} subs={subCount}");

            // Interpolation precedes the keyframe store for value tracks; every track then
            // carries its own keyframe store (channels typed by the track model, since the
            // channel "type" string is not serialized in the shipped wad).
            string model = wad.ReadWadString(tr.ModelNameRef) ?? "";
            if (model is "GMRealTrack" or "GMColourTrack" or "GMAudioEffectTrack")
            {
                tr.Interpolation = wad.ReadInt32(p); p += 4;
            }
            tr.Keyframes = ParseKeyframeStore(wad, ref p, model);
            return tr;
        }

        /// <summary>EventToFunction: <c>{ u32 count, (i32 eventId, i32 scriptIndex)[count] }</c>.</summary>
        internal static WadSeqnEventToFunction ParseEventToFunction(UndertaleWadFile wad, ref uint p)
        {
            var ev = new WadSeqnEventToFunction();
            uint count = wad.ReadUInt32(p); p += 4;
            var list = new List<WadSeqnEventFunc>();
            for (uint i = 0; i < count; i++)
            {
                list.Add(new WadSeqnEventFunc
                {
                    EventId = wad.ReadInt32(p),
                    ScriptIndex = wad.ReadInt32(p + 4),
                });
                p += 8;
            }
            ev.Items = list;
            return ev;
        }
    }

    public sealed class WadSeqnEntry
    {
        /// <summary>Absolute offset of the name string record.</summary>
        public uint NameRef { get; internal set; }
        public int Playback { get; set; }
        public float PlaybackSpeed { get; set; }
        public int PlaybackSpeedType { get; set; }
        public float Length { get; set; }
        public int Xorigin { get; set; }
        public int Yorigin { get; set; }
        public float Volume { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public WadSeqnKeyframeStore Events { get; internal set; }
        public WadSeqnTracks Tracks { get; internal set; }
        public WadSeqnEventToFunction EventToFunction { get; internal set; }
        public WadSeqnKeyframeStore Moments { get; internal set; }
        public Exception Error { get; internal set; }
        /// <summary>Offset just past the parsed entry (for verification against the next
        /// entry offset).</summary>
        public uint ParseEndOffset { get; internal set; }

        public string Name { get; set; }
    }

    public sealed class WadSeqnKeyframeStore
    {
        public IReadOnlyList<WadSeqnKeyframe> Keyframes { get; internal set; }
    }

    public sealed class WadSeqnKeyframe
    {
        public float Key { get; internal set; }
        public float Length { get; set; }
        public int Stretch { get; internal set; }
        public bool Disabled { get; internal set; }
        public IReadOnlyList<WadSeqnKeyframeChannel> Channels { get; internal set; }
    }

    public sealed class WadSeqnKeyframeChannel
    {
        public int Key { get; internal set; }
        public uint TypeRef { get; internal set; }
        public int AssetIndex { get; internal set; }
        public uint StringRef { get; internal set; }
        public int Value { get; internal set; }
        public int SpriteFrameIndex { get; internal set; }
        public int SoundIndex { get; internal set; }
        public int EmitterIndex { get; internal set; }
        public int Mode { get; internal set; }
        public uint TextRef { get; internal set; }
        public int WrapMode0 { get; internal set; }
        public int Alignment { get; internal set; }
        public int FontIndex { get; internal set; }
        public int WrapMode1 { get; internal set; }
        public int Origin { get; internal set; }
        public float RealValue { get; internal set; }
        public int Color { get; internal set; }
        public WadCurveTail CurveTail { get; internal set; }
        public IReadOnlyList<uint> Events { get; internal set; }
    }

    /// <summary>Curve tail of colour/real channels.</summary>
    public sealed class WadCurveTail
    {
        public bool IsCurveEmbedded { get; internal set; }
        public uint EmbeddedMarker { get; internal set; }
        public WadAcrvCurve EmbeddedCurve { get; internal set; }
        public int CurveIndex { get; internal set; }
    }

    public sealed class WadSeqnTracks
    {
        public IReadOnlyList<WadSeqnTrack> Tracks { get; internal set; }
    }

    public sealed class WadSeqnTrack
    {
        public uint ModelNameRef { get; internal set; }
        public uint NameRef { get; internal set; }
        public int BuiltinName { get; internal set; }
        public int Traits { get; internal set; }
        public bool IsCreationTrack { get; internal set; }
        public IReadOnlyList<uint> Tags { get; internal set; }
        public IReadOnlyList<uint> OwnedResourceModels { get; internal set; }
        public IReadOnlyList<WadSeqnTrack> SubTracks { get; internal set; }
        public int? Interpolation { get; internal set; }
        public WadSeqnKeyframeStore Keyframes { get; internal set; }
    }

    public sealed class WadSeqnEventToFunction
    {
        public IReadOnlyList<WadSeqnEventFunc> Items { get; internal set; }
    }

    public sealed class WadSeqnEventFunc
    {
        public int EventId { get; internal set; }
        public int ScriptIndex { get; internal set; }
    }
}