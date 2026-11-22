using System;
using System.Runtime.InteropServices;

namespace UndertaleModRunner;

/// <summary>
/// Bridge for the Android out-of-process host. The UI process owns the
/// SurfaceView and acquires an ANativeWindow reference from its Surface; the
/// pointer is sent to the debug host, whose libbsdbg uses the raw window kind
/// instead of JNI references from the wrong process.
/// </summary>
public static class AndroidSurfaceBridge
{
    [DllImport("bsdbg")]
    private static extern IntPtr Bsdbg_AcquireAndroidNativeWindow(IntPtr surfaceJObject);

    [DllImport("bsdbg")]
    private static extern void Bsdbg_ReleaseAndroidNativeWindow(IntPtr nativeWindow);

    /// <summary>Whether the current libbsdbg provides the Android bridge.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsAndroid())
                return false;
            try
            {
                return ExternalButterscotch.CanUseEmbeddedSession;
            }
            catch
            {
                return false;
            }
        }
    }

    public static IntPtr Acquire(IntPtr surfaceJObject)
    {
        try
        {
            return Bsdbg_AcquireAndroidNativeWindow(surfaceJObject);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static void Release(IntPtr nativeWindow)
    {
        if (nativeWindow == IntPtr.Zero)
            return;
        try
        {
            Bsdbg_ReleaseAndroidNativeWindow(nativeWindow);
        }
        catch
        {
            // The runtime may already be unloaded.
        }
    }
}
