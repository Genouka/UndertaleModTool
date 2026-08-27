using System;
using System.Collections.Generic;

namespace UndertaleModLib.Wad
{
    /// <summary>
    /// Parsed <c>ACRV</c> chunk (animation curves). Field order from
    /// <c>SequenceWriter::writeCurveToWAD</c> (@760411 in Runner.exe.c):
    /// <c>{ (name str, optional) u32 graphType, u32 channelCount, channel[count] }</c>;
    /// channel = <c>{ str name, i32 function, i32 iterations, u32 pointCount, point[count] }</c>;
    /// point = 24 bytes of 6 floats (x, value, tx0, ty0, tx1, ty1).
    /// Embedded curves (in sequence colour/real keyframes) omit the name field.
    /// </summary>
    public sealed class WadAcrvChunk : WadChunk
    {
        public uint Count { get; private set; }
        public IReadOnlyList<WadAcrvCurve> Curves => _curves;
        private readonly List<WadAcrvCurve> _curves = new();

        internal WadAcrvChunk(WadChunkHeader header) : base(header) { }

        internal static WadAcrvChunk Parse(UndertaleWadFile wad, WadChunkHeader header)
        {
            var chunk = new WadAcrvChunk(header);
            uint data = (uint)header.DataOffset;
            chunk.Count = wad.ReadUInt32(data);
            for (uint i = 0; i < chunk.Count; i++)
            {
                uint entryOff = wad.ReadUInt32(data + 4 + 4 * i);
                try
                {
                    chunk._curves.Add(ParseCurve(wad, entryOff));
                }
                catch (Exception e)
                {
                    chunk._curves.Add(new WadAcrvCurve { Error = e });
                }
            }
            return chunk;
        }

        /// <summary>Parses a standalone curve entry (with name field).</summary>
        internal static WadAcrvCurve ParseCurve(UndertaleWadFile wad, uint p)
        {
            var curve = new WadAcrvCurve { NameRef = wad.ReadUInt32(p) };
            curve.Name = wad.ReadWadString(curve.NameRef);
            p += 4;
            ParseCurveBody(wad, ref p, curve);
            return curve;
        }

        /// <summary>Parses curve fields after the (optional) name; shared with embedded curves.</summary>
        internal static WadAcrvCurve ParseCurveBody(UndertaleWadFile wad, ref uint p)
        {
            var curve = new WadAcrvCurve();
            ParseCurveBody(wad, ref p, curve);
            return curve;
        }

        private static void ParseCurveBody(UndertaleWadFile wad, ref uint p, WadAcrvCurve curve)
        {
            curve.GraphType = wad.ReadInt32(p); p += 4;
            uint channelCount = wad.ReadUInt32(p); p += 4;
            var channels = new List<WadAcrvChannel>();
            for (uint i = 0; i < channelCount; i++)
                channels.Add(ParseChannel(wad, ref p));
            curve.Channels = channels;
        }

        private static WadAcrvChannel ParseChannel(UndertaleWadFile wad, ref uint p)
        {
            var ch = new WadAcrvChannel { NameRef = wad.ReadUInt32(p) };
            ch.Name = wad.ReadWadString(ch.NameRef);
            p += 4;
            ch.Function = wad.ReadInt32(p); p += 4;
            ch.Iterations = wad.ReadInt32(p); p += 4;
            uint pointCount = wad.ReadUInt32(p); p += 4;
            var points = new List<WadAcrvPoint>();
            for (uint i = 0; i < pointCount; i++)
            {
                points.Add(new WadAcrvPoint
                {
                    X = wad.ReadSingle(p),
                    Value = wad.ReadSingle(p + 4),
                    Tx0 = wad.ReadSingle(p + 8),
                    Ty0 = wad.ReadSingle(p + 12),
                    Tx1 = wad.ReadSingle(p + 16),
                    Ty1 = wad.ReadSingle(p + 20),
                });
                p += 24;
            }
            ch.Points = points;
            return ch;
        }
    }

    /// <summary>One animation curve (entry of ACRV, or embedded in a sequence keyframe).</summary>
    public sealed class WadAcrvCurve
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public int GraphType { get; internal set; }
        public IReadOnlyList<WadAcrvChannel> Channels { get; internal set; }
        public Exception Error { get; internal set; }
    }

    public sealed class WadAcrvChannel
    {
        public uint NameRef { get; internal set; }
        public string Name { get; internal set; }
        public int Function { get; internal set; }
        public int Iterations { get; internal set; }
        public IReadOnlyList<WadAcrvPoint> Points { get; internal set; }
    }

    /// <summary>A 24-byte curve control point.</summary>
    public sealed class WadAcrvPoint
    {
        public float X { get; internal set; }
        public float Value { get; internal set; }
        public float Tx0 { get; internal set; }
        public float Ty0 { get; internal set; }
        public float Tx1 { get; internal set; }
        public float Ty1 { get; internal set; }
    }
}