using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UndertaleModRunner;

public enum BsdbgRenderer
{
    Software = 0,
    ModernGL = 1,
    LegacyGL = 2,
    Noop = 3
}

/// <summary>Kind of native window the engine should render into when embedding.</summary>
public enum BsdbgEmbedWindowKind
{
    /// <summary>The engine creates its own top-level window.</summary>
    None = 0,
    /// <summary><see cref="ButterscotchConfig.EmbedWindow"/> is a Win32 HWND.</summary>
    Win32Hwnd = 1,
    /// <summary><see cref="ButterscotchConfig.EmbedWindow"/> is an Android <c>android.view.Surface</c> jobject.</summary>
    AndroidSurface = 2,
}

public sealed class ButterscotchConfig
{
    public string DataWinPath { get; set; }
    public string WorkingDirectory { get; set; }
    public string SaveFolder { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public BsdbgRenderer Renderer { get; set; } = BsdbgRenderer.ModernGL;
    public bool Headless { get; set; }
    public bool Fullscreen { get; set; }
    public double Speed { get; set; } = 1.0;
    public uint Seed { get; set; }
    public IReadOnlyList<string> GameArgs { get; set; } = Array.Empty<string>();
    public bool StopOnEntry { get; set; }

    /// <summary>
    /// Native window to render into instead of creating a top-level one. Required on
    /// single-window platforms (Android) and on any host that draws the game inside its
    /// own view tree (see Avalonia's <c>NativeControlHost</c>).
    /// </summary>
    public IntPtr EmbedWindow { get; set; }

    public BsdbgEmbedWindowKind EmbedWindowKind { get; set; }
}

public readonly struct BreakpointDef
{
    public BreakpointDef(int codeIndex, uint offset, bool enabled = true)
    {
        CodeIndex = codeIndex;
        Offset = offset;
        Enabled = enabled;
    }

    public int CodeIndex { get; }
    public uint Offset { get; }
    public bool Enabled { get; }
}

public sealed class DebugState
{
    [JsonProperty("running")] public bool Running { get; set; }
    [JsonProperty("paused")] public bool Paused { get; set; }
    [JsonProperty("frameCount")] public int FrameCount { get; set; }
    [JsonProperty("roomIndex")] public int RoomIndex { get; set; }
    [JsonProperty("roomName")] public string RoomName { get; set; }
    [JsonProperty("stop")] public StopInfo Stop { get; set; }
}

public sealed class StopInfo
{
    [JsonProperty("reason")] public string Reason { get; set; }
    [JsonProperty("frame")] public StackFrameInfo Frame { get; set; }
}

public sealed class StackFrameInfo
{
    [JsonProperty("codeIndex")] public int CodeIndex { get; set; }
    [JsonProperty("codeName")] public string CodeName { get; set; }
    [JsonProperty("ip")] public uint Ip { get; set; }
    [JsonProperty("selfId")] public int SelfId { get; set; }
    [JsonProperty("selfObjectIndex")] public int SelfObjectIndex { get; set; }
    [JsonProperty("eventType")] public int EventType { get; set; }
    [JsonProperty("eventSubtype")] public int EventSubtype { get; set; }
    [JsonProperty("eventObjectIndex")] public int EventObjectIndex { get; set; }
    [JsonProperty("locals")] public List<LocalInfo> Locals { get; set; } = new();
    [JsonProperty("args")] public List<ArgumentInfo> Args { get; set; } = new();
}

public sealed class LocalInfo
{
    [JsonProperty("slot")] public int Slot { get; set; }
    [JsonProperty("varId")] public int VarId { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("value")] public ValueInfo Value { get; set; }
}

public sealed class ArgumentInfo
{
    [JsonProperty("slot")] public int Slot { get; set; }
    [JsonProperty("value")] public ValueInfo Value { get; set; }
}

public sealed class ValueInfo
{
    [JsonProperty("type")] public string Type { get; set; }
    [JsonProperty("value")] public object Value { get; set; }
    [JsonProperty("items")] public object Items { get; set; }
    [JsonProperty("assetType")] public string AssetType { get; set; }
    [JsonProperty("id")] public int? Id { get; set; }
    [JsonProperty("truncated")] public bool? Truncated { get; set; }

    public override string ToString()
    {
        if (Type == "undefined")
            return "undefined";
        if (Type == "array")
            return "array";
        if (Type == "struct")
            return "struct";
        if (Type == "assetref")
            return $"{AssetType}:{Value}";
        return Value?.ToString() ?? Type;
    }
}

public sealed class InstanceInfo
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("objectIndex")] public int ObjectIndex { get; set; }
    [JsonProperty("objectName")] public string ObjectName { get; set; }
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("speed")] public double Speed { get; set; }
    [JsonProperty("direction")] public double Direction { get; set; }
    [JsonProperty("hspeed")] public double HSpeed { get; set; }
    [JsonProperty("vspeed")] public double VSpeed { get; set; }
    [JsonProperty("depth")] public int Depth { get; set; }
    [JsonProperty("spriteIndex")] public int SpriteIndex { get; set; }
    [JsonProperty("imageIndex")] public double ImageIndex { get; set; }
    [JsonProperty("imageSpeed")] public double ImageSpeed { get; set; }
    [JsonProperty("visible")] public bool Visible { get; set; }
    [JsonProperty("active")] public bool Active { get; set; }
    [JsonProperty("persistent")] public bool Persistent { get; set; }
    [JsonProperty("solid")] public bool Solid { get; set; }
    [JsonProperty("variables")] public List<InstanceVariableInfo> Variables { get; set; } = new();
    [JsonProperty("truncated")] public bool? Truncated { get; set; }
}

public sealed class InstanceVariableInfo
{
    [JsonProperty("varId")] public int VarId { get; set; }
    [JsonProperty("name")] public string Name { get; set; }
    [JsonProperty("value")] public ValueInfo Value { get; set; }
}

public sealed class InstancesResult
{
    [JsonProperty("paused")] public bool Paused { get; set; }
    [JsonProperty("instances")] public List<InstanceInfo> Instances { get; set; } = new();
}

public sealed class GlobalsResult
{
    [JsonProperty("paused")] public bool Paused { get; set; }
    [JsonProperty("globals")] public List<InstanceVariableInfo> Globals { get; set; } = new();
}

public sealed class ReloadResult
{
    [JsonProperty("applied")] public int Applied { get; set; }
    [JsonProperty("skipped")] public int Skipped { get; set; }
}

public sealed class CallStackResult
{
    [JsonProperty("frames")] public List<StackFrameInfo> Frames { get; set; } = new();
}
