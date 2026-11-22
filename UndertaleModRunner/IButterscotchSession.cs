using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UndertaleModRunner;

/// <summary>
/// Common surface for the embedded (same-process) and out-of-process Butterscotch
/// debug sessions.  The debug UI only talks to this interface.
/// </summary>
public interface IButterscotchSession : IDisposable
{
    int AbiVersion { get; }
    bool IsRunning { get; }
    bool IsPaused { get; }
    int FrameCount { get; }

    event Action<DebugState> StateChanged;
    event Action<DebugState> Stopped;
    event Action<int> Exited;
    event Action<string> Error;
    event Action<ReloadResult> Reloaded;
    event Action<int, string> Log;

    bool Start();
    bool Pause();
    bool Resume();
    bool StepInto();
    bool StepOver();
    bool StepOut();
    bool StepFrame();

    bool SetBreakpoints(IEnumerable<BreakpointDef> breakpoints);
    bool ClearBreakpoints();

    DebugState GetState();
    CallStackResult GetCallStack();
    InstancesResult GetInstances();
    GlobalsResult GetGlobals();

    bool SetInstanceVariable(int instanceId, string name, string valueJson, out string error);
    bool SetGlobalVariable(string name, string valueJson, out string error);
    bool SetFrameVariable(int frameIndex, bool isArgument, int slot, string valueJson, out string error);
    bool ApplyCodePatch(string patchJson, out string error);

    // Surface and input commands. Default implementations keep existing hosts
    // source-compatible; remote and embedded sessions override them.
    void PublishSurface(IntPtr handle, BsdbgEmbedWindowKind kind) { }
    void WithdrawSurface() { }
    void InjectKey(int gmlKey, bool down) { }
    void InjectText(uint character) { }
    void InjectMouseButton(int button, bool down) { }
    void InjectMousePos(double x, double y) { }
    void InjectWheel(double offset) { }
    void InjectGamepadButton(int button, bool down, float value) { }
    void InjectGamepadAxis(int axis, float value) { }

    /// <summary>
    /// Refreshes the cached state snapshot.  In-process sessions are already
    /// synchronous, so they complete immediately; remote sessions perform a
    /// short pipe round-trip off the UI thread.
    /// </summary>
    Task RefreshStateAsync();

    /// <summary>
    /// Refreshes the cached call stack / instances / globals snapshots.  Only
    /// useful while paused; in-process sessions complete immediately.
    /// </summary>
    Task RefreshInspectionAsync();
}
