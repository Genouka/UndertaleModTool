using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Android.App;
using UndertaleModRunner;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Launches the packaged native debug host from the APK's nativeLibraryDir and
/// connects it to the Avalonia debug service through a private Unix socket.
/// Android applications cannot use System.Diagnostics.Process for arbitrary
/// native executables, so Java's Process API is used instead.
/// </summary>
public static class AndroidDebugHost
{
    private static bool _pausedByLifecycle;

    public static void OnPause()
    {
        try
        {
            ButterscotchDebugService service = ButterscotchDebugService.Instance;
            if (service.IsActive && !service.IsPaused)
            {
                service.Pause();
                _pausedByLifecycle = true;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void OnResume()
    {
        if (!_pausedByLifecycle)
            return;
        _pausedByLifecycle = false;
        try
        {
            ButterscotchDebugService service = ButterscotchDebugService.Instance;
            if (service.IsActive && service.IsPaused)
                service.Resume();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void OnDestroy()
    {
        _pausedByLifecycle = false;
        try
        {
            ButterscotchDebugService.Instance.Stop();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void Install()
    {
        if (!AndroidSurfaceBridge.IsAvailable)
            return;

        ButterscotchSurfaceHost.UseRemoteHost = true;
        ButterscotchSurfaceHost.StartRemote = StartRemote;
    }

    private static bool StartRemote(string dataPath, bool stopOnEntry,
                                    IntPtr nativeWindow, BsdbgEmbedWindowKind windowKind)
    {
        string? nativeDir = Application.Context.ApplicationInfo?.NativeLibraryDir;
        if (string.IsNullOrEmpty(nativeDir))
            return false;
        string hostPath = Path.Combine(nativeDir, "libbsdbg-host.so");
        if (!File.Exists(hostPath))
            return false;

        string socketPath = Path.Combine(
            Application.Context.CacheDir!.AbsolutePath,
            "bsdbg-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");

        ButterscotchArchitecture architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => ButterscotchArchitecture.Arm64,
            _ => ButterscotchArchitecture.X64,
        };

        // Make sure the executable bit is set; some Android versions extract
        // native libraries without it.
        try
        {
            Java.Lang.Runtime.GetRuntime()?.Exec(new[] { "chmod", "755", hostPath })?.WaitFor();
        }
        catch
        {
            // chmod is best effort; exec below reports the real failure.
        }

        IHostProcess Factory(string endpoint, IReadOnlyList<string> arguments)
        {
            var list = new List<string>(arguments)
            {
                "--dll",
                Path.Combine(nativeDir, "libbsdbg.so"),
            };
            var argv = new string[list.Count + 1];
            argv[0] = hostPath;
            for (int i = 0; i < list.Count; i++)
                argv[i + 1] = list[i];
            Java.Lang.Process? process = Java.Lang.Runtime.GetRuntime()?.Exec(argv)
                ?? throw new InvalidOperationException("Could not start the debug host process.");
            return new JavaHostProcess(process);
        }

        return ButterscotchDebugService.Instance.StartRemoteWithTransport(
            dataPath, stopOnEntry, architecture, hostPath, socketPath,
            Factory, nativeWindow, windowKind);
    }

    private sealed class JavaHostProcess : IHostProcess
    {
        private readonly Java.Lang.Process _process;

        public JavaHostProcess(Java.Lang.Process process)
        {
            _process = process;
        }

        public int Id => 0; // Java Process does not expose a PID; the handshake carries it.
        public IntPtr Handle => IntPtr.Zero;
        public int ExitCode
        {
            get
            {
                try { return _process.ExitValue(); }
                catch { return -1; }
            }
        }

        public bool HasExited
        {
            get
            {
                try { _process.ExitValue(); return true; }
                catch { return false; }
            }
        }

        public void Kill() => _process.Destroy();

        public bool WaitForExit(int milliseconds)
        {
            try
            {
                if (!HasExited && milliseconds > 0)
                    System.Threading.Thread.Sleep(milliseconds);
                return HasExited;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!HasExited)
                Kill();
            _process.Dispose();
        }
    }
}
