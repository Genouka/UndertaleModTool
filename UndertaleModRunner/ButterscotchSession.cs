using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace UndertaleModRunner;

/// <summary>
/// Managed wrapper around a ButterscotchDbg session (bsdbg.dll).
/// Events are raised on the native game thread; callers that touch UI must
/// marshal to the UI thread themselves.
/// </summary>
public sealed class ButterscotchSession : IDisposable
{
    private IntPtr _session;
    private readonly List<IntPtr> _allocatedStrings = new();
    private readonly NativeMethods.BsdbgEventCallback _eventCallback;
    private readonly NativeMethods.BsdbgLogCallback _logCallback;
    private bool _disposed;

    public ButterscotchConfig Config { get; }

    public event Action<DebugState> StateChanged;
    public event Action<DebugState> Stopped;
    public event Action<int> Exited;
    public event Action<string> Error;
    public event Action<ReloadResult> Reloaded;
    public event Action<int, string> Log;

    public ButterscotchSession(ButterscotchConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _eventCallback = OnNativeEvent;
        _logCallback = OnNativeLog;

        NativeMethods.BsdbgConfig native = new()
        {
            StructSize = (uint)Marshal.SizeOf<NativeMethods.BsdbgConfig>(),
            DataWinPath = AllocString(config.DataWinPath),
            WorkingDirectory = AllocString(config.WorkingDirectory),
            SaveFolder = AllocString(config.SaveFolder),
            WindowWidth = config.WindowWidth,
            WindowHeight = config.WindowHeight,
            Renderer = (int)config.Renderer,
            Headless = config.Headless ? 1 : 0,
            Fullscreen = config.Fullscreen ? 1 : 0,
            Speed = config.Speed,
            Seed = config.Seed,
            GameArgCount = config.GameArgs?.Count ?? 0,
            StopOnEntry = config.StopOnEntry ? 1 : 0
        };

        if (native.GameArgCount > 0)
        {
            native.GameArgs = Marshal.AllocHGlobal(IntPtr.Size * native.GameArgCount);
            for (int i = 0; i < native.GameArgCount; i++)
                Marshal.WriteIntPtr(native.GameArgs, i * IntPtr.Size, AllocString(config.GameArgs[i]));
        }

        _session = NativeMethods.Bsdbg_Create(ref native);

        // Bsdbg_Create deep-copies the configuration strings.
        if (native.GameArgs != IntPtr.Zero)
            Marshal.FreeHGlobal(native.GameArgs);
        foreach (IntPtr ptr in _allocatedStrings)
            Marshal.FreeCoTaskMem(ptr);
        _allocatedStrings.Clear();

        if (_session == IntPtr.Zero)
            throw new InvalidOperationException("Bsdbg_Create failed (bsdbg.dll missing or invalid config).");

        NativeMethods.Bsdbg_SetEventCallback(_session, _eventCallback, IntPtr.Zero);
        NativeMethods.Bsdbg_SetLogCallback(_session, _logCallback, IntPtr.Zero);
    }

    public int AbiVersion => NativeMethods.Bsdbg_AbiVersion();
    public bool IsRunning => _session != IntPtr.Zero && NativeMethods.Bsdbg_IsRunning(_session) != 0;
    public bool IsPaused => _session != IntPtr.Zero && NativeMethods.Bsdbg_IsPaused(_session) != 0;
    public int FrameCount => _session != IntPtr.Zero ? NativeMethods.Bsdbg_GetFrameCount(_session) : -1;

    public bool Start() => NativeMethods.Bsdbg_Start(_session) == 0;
    public bool Pause() => NativeMethods.Bsdbg_Pause(_session) == 0;
    public bool Resume() => NativeMethods.Bsdbg_Resume(_session) == 0;
    public bool StepFrame() => NativeMethods.Bsdbg_StepFrame(_session) == 0;
    public bool StepInto() => NativeMethods.Bsdbg_StepInto(_session) == 0;
    public bool StepOver() => NativeMethods.Bsdbg_StepOver(_session) == 0;
    public bool StepOut() => NativeMethods.Bsdbg_StepOut(_session) == 0;

    public bool SetBreakpoints(IEnumerable<BreakpointDef> breakpoints)
    {
        BreakpointDef[] list = breakpoints?.ToArray() ?? Array.Empty<BreakpointDef>();
        NativeMethods.BsdbgBreakpoint[] native = list.Select(b => new NativeMethods.BsdbgBreakpoint
        {
            CodeIndex = b.CodeIndex,
            Offset = b.Offset,
            Enabled = b.Enabled ? 1 : 0
        }).ToArray();

        return NativeMethods.Bsdbg_SetBreakpoints(_session, native, native.Length) == 0;
    }

    public bool ClearBreakpoints() => NativeMethods.Bsdbg_ClearBreakpoints(_session) == 0;

    public DebugState GetState() => ReadJson<DebugState>(NativeMethods.Bsdbg_GetStateJson(_session));
    public CallStackResult GetCallStack() => ReadJson<CallStackResult>(NativeMethods.Bsdbg_GetCallStackJson(_session));
    public InstancesResult GetInstances() => ReadJson<InstancesResult>(NativeMethods.Bsdbg_GetInstancesJson(_session));
    public GlobalsResult GetGlobals() => ReadJson<GlobalsResult>(NativeMethods.Bsdbg_GetGlobalsJson(_session));

    public bool SetInstanceVariable(int instanceId, string name, string valueJson, out string error)
    {
        IntPtr namePtr = AllocTempString(name);
        IntPtr valuePtr = AllocTempString(valueJson);
        int result;
        IntPtr errorPtr;
        try
        {
            result = NativeMethods.Bsdbg_SetInstanceVariable(_session, instanceId, namePtr, valuePtr, out errorPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
            Marshal.FreeCoTaskMem(valuePtr);
        }

        error = TakeString(errorPtr);
        return result == 0;
    }

    public bool ApplyCodePatch(string patchJson, out string error)
    {
        IntPtr jsonPtr = AllocTempString(patchJson);
        int result;
        IntPtr errorPtr;
        try
        {
            result = NativeMethods.Bsdbg_ApplyCodePatch(_session, jsonPtr, out errorPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(jsonPtr);
        }

        error = TakeString(errorPtr);
        return result == 0;
    }

    private void OnNativeEvent(IntPtr user, int eventType, IntPtr jsonPtr)
    {
        // Event payloads are owned by the native side and only valid for the
        // duration of this callback; do NOT call Bsdbg_FreeString here.
        string json = jsonPtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(jsonPtr);
        try
        {
            switch (eventType)
            {
                case 0: // STATE
                    StateChanged?.Invoke(ParseState(json));
                    break;
                case 1: // BREAK
                    Stopped?.Invoke(ParseState(json));
                    break;
                case 2: // EXIT
                    Exited?.Invoke(ParseCode(json));
                    break;
                case 3: // ERROR
                    Error?.Invoke(ParseMessage(json));
                    break;
                case 4: // RELOADED
                    Reloaded?.Invoke(ParseReload(json));
                    break;
                case 5: // LOG
                    Log?.Invoke(0, ParseMessage(json));
                    break;
            }
        }
        catch
        {
            // Never let a managed exception escape into the native game thread.
        }
    }

    private void OnNativeLog(IntPtr user, int level, IntPtr messagePtr)
    {
        try
        {
            string message = messagePtr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(messagePtr);
            Log?.Invoke(level, message);
        }
        catch
        {
            // See above.
        }
    }

    private static DebugState ParseState(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<DebugState>(json) ?? new DebugState();
        }
        catch
        {
            return new DebugState();
        }
    }

    private static int ParseCode(string json)
    {
        try
        {
            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return obj != null && obj.TryGetValue("code", out object value) ? Convert.ToInt32(value) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ParseMessage(string json)
    {
        try
        {
            var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
            return obj != null && obj.TryGetValue("message", out object value) ? value?.ToString() : json;
        }
        catch
        {
            return json;
        }
    }

    private static ReloadResult ParseReload(string json)
    {
        try
        {
            return JsonConvert.DeserializeObject<ReloadResult>(json) ?? new ReloadResult();
        }
        catch
        {
            return new ReloadResult();
        }
    }

    private static T ReadJson<T>(IntPtr ptr) where T : new()
    {
        string json = TakeString(ptr);
        if (string.IsNullOrEmpty(json))
            return new T();
        try
        {
            return JsonConvert.DeserializeObject<T>(json) ?? new T();
        }
        catch
        {
            return new T();
        }
    }

    private static string TakeString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        string value = Marshal.PtrToStringUTF8(ptr);
        NativeMethods.Bsdbg_FreeString(ptr);
        return value;
    }

    private IntPtr AllocString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return IntPtr.Zero;
        IntPtr ptr = Marshal.StringToCoTaskMemUTF8(value);
        _allocatedStrings.Add(ptr);
        return ptr;
    }

    private static IntPtr AllocTempString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return IntPtr.Zero;
        return Marshal.StringToCoTaskMemUTF8(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_session != IntPtr.Zero)
        {
            NativeMethods.Bsdbg_Destroy(_session);
            _session = IntPtr.Zero;
        }
        foreach (IntPtr ptr in _allocatedStrings)
            Marshal.FreeCoTaskMem(ptr);
        _allocatedStrings.Clear();
    }
}
