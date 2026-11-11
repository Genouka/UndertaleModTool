using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UndertaleModRunner;

internal static class NativeMethods
{
    internal const string DllName = "bsdbg";

    static NativeMethods()
    {
        // Load the bsdbg matching this process architecture instead of relying on
        // the flat DLL next to the executable: 32-bit and 64-bit builds share one
        // folder, with native runtimes under native/win-<arch>/.
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string libraryPath = ExternalButterscotch.FindEmbeddedButterscotchLibrary();
        if (libraryPath is null)
            return IntPtr.Zero;

        // bsdbg imports SDL2.dll. Load the sibling copy first so the dependency
        // resolves to the same architecture regardless of DLL search paths.
        string directory = Path.GetDirectoryName(libraryPath);
        if (!string.IsNullOrEmpty(directory))
        {
            string sdlPath = Path.Combine(directory, "SDL2.dll");
            if (File.Exists(sdlPath))
            {
                try { NativeLibrary.Load(sdlPath); }
                catch (Exception) { /* let the bsdbg load surface the error */ }
            }
        }

        return NativeLibrary.Load(libraryPath);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BsdbgConfig
    {
        public uint StructSize;
        public IntPtr DataWinPath;
        public IntPtr WorkingDirectory;
        public IntPtr SaveFolder;
        public int WindowWidth;
        public int WindowHeight;
        public int Renderer;
        public int Headless;
        public int Fullscreen;
        public double Speed;
        public uint Seed;
        public IntPtr GameArgs;
        public int GameArgCount;
        public int StopOnEntry;
        // Optional embedded-window override (single-window hosts). IntPtr matches the
        // native intptr_t, so the field is 4 bytes on x86 and 8 on x64 like the C side.
        public IntPtr EmbedWindow;
        public int EmbedWindowKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BsdbgBreakpoint
    {
        public int CodeIndex;
        public uint Offset;
        public int Enabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void BsdbgEventCallback(IntPtr user, int eventType, IntPtr json);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void BsdbgLogCallback(IntPtr user, int level, IntPtr message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bsdbg_Create(ref BsdbgConfig config);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bsdbg_Destroy(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_Start(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_IsRunning(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_IsPaused(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_Pause(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_Resume(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_StepFrame(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_StepInto(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_StepOver(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_StepOut(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_GetFrameCount(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_SetBreakpoints(IntPtr session, [In] BsdbgBreakpoint[] breakpoints, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_ClearBreakpoints(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bsdbg_GetStateJson(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bsdbg_GetCallStackJson(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bsdbg_GetInstancesJson(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr Bsdbg_GetGlobalsJson(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bsdbg_FreeString(IntPtr str);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_SetInstanceVariable(IntPtr session, int instanceId, IntPtr name, IntPtr valueJson, out IntPtr errorText);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_SetGlobalVariable(IntPtr session, IntPtr name, IntPtr valueJson, out IntPtr errorText);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_SetFrameVariable(IntPtr session, int frameIndex, int isArgument, int slot, IntPtr valueJson, out IntPtr errorText);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_ApplyCodePatch(IntPtr session, IntPtr json, out IntPtr errorText);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bsdbg_SetEventCallback(IntPtr session, BsdbgEventCallback callback, IntPtr user);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Bsdbg_SetLogCallback(IntPtr session, BsdbgLogCallback callback, IntPtr user);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Bsdbg_AbiVersion();
}
