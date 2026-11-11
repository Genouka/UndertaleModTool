using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using UndertaleModLib.Models;
using UndertaleModRunner;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>A single breakpoint, identified by its CODE chunk index and bytecode offset.</summary>
public sealed class BreakpointEntry
{
    public int CodeIndex { get; set; }
    public string CodeName { get; set; } = "";
    public uint Offset { get; set; }
    public bool Enabled { get; set; } = true;

    public override string ToString() => $"{CodeName} + 0x{Offset:X}";
}

/// <summary>
/// Owns the Butterscotch debug session for the main view model and fans native events out to
/// the debug tool windows on the UI thread. Port of the WPF tool's DebugSessionService.
/// </summary>
public sealed class ButterscotchDebugService : IDisposable
{
    /// <summary>The service owned by the application's (singleton) main view model.</summary>
    public static ButterscotchDebugService Instance =>
        App.Services.GetRequiredService<MainViewModel>().Butterscotch;

    public MainViewModel Main { get; }

    public ButterscotchSession? Session { get; private set; }
    public DebugState State { get; private set; } = new();
    public ObservableCollection<BreakpointEntry> Breakpoints { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public event Action? Updated;
    public event Action<DebugState>? Stopped;
    /// <summary>Raised when only the lightweight status (frame/paused) changed.</summary>
    public event Action? StatusUpdated;
    public event Action<string>? LogAdded;

    public bool IsActive => Session is not null;
    public bool IsPaused => State?.Paused == true;

    readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ButterscotchDebugService(MainViewModel main)
    {
        Main = main;
        _stateTimer.Tick += (_, _) => RefreshState();
    }

    /// <summary>The data file a session runs: the project's exported file when one is open.</summary>
    public string? CurrentDataPath => Main.Project?.SaveDataPath ?? Main.DataPath;

    public bool Start(bool stopOnEntry)
        => Start(CurrentDataPath, stopOnEntry);

    /// <summary>
    /// Starts an embedded session. <paramref name="embedWindow"/> is the native window the game
    /// renders into; single-window hosts (Android) pass the surface of a NativeControlHost-backed
    /// view, desktop passes 0 so the engine opens its own window.
    /// </summary>
    public bool Start(string? dataPath, bool stopOnEntry,
                      IntPtr embedWindow = default, BsdbgEmbedWindowKind embedKind = BsdbgEmbedWindowKind.None)
    {
        if (Session is not null)
            Stop();

        if (string.IsNullOrEmpty(dataPath) || !File.Exists(dataPath))
        {
            ReportError(LocalizationSource.GetString("Msg_ButterscotchSaveBeforeRun"));
            return false;
        }

        ButterscotchConfig config = new()
        {
            DataWinPath = dataPath,
            WorkingDirectory = Path.GetDirectoryName(dataPath),
            Renderer = BsdbgRenderer.ModernGL,
            Speed = 1.0,
            StopOnEntry = stopOnEntry,
            EmbedWindow = embedWindow,
            EmbedWindowKind = embedKind,
        };

        try
        {
            Session = new ButterscotchSession(config);
        }
        catch (Exception ex)
        {
            ReportError($"Could not load ButterscotchDbg: {ex.Message}");
            Session = null;
            return false;
        }

        Session.StateChanged += state => OnState(state, stopped: false);
        Session.Stopped += state => OnState(state, stopped: true);
        Session.Exited += code => Dispatch(() =>
        {
            AppendLog($"Game exited (code {code}).");
            CloseSession();
        });
        Session.Error += message => Dispatch(() => AppendLog("ERROR: " + message));
        Session.Log += (level, message) => Dispatch(() => AppendLog($"[{LevelName(level)}] {message}"));
        Session.Reloaded += result => Dispatch(() => AppendLog($"Hot reload applied: {result.Applied} code entries."));

        ApplyBreakpoints();
        if (!Session.Start())
        {
            ReportError("Butterscotch runner failed to start (see the debug output window).");
            CloseSession();
            return false;
        }

        AppendLog($"Butterscotch session started: {dataPath}");
        _stateTimer.Start();
        return true;
    }

    public void Stop()
    {
        if (Session is null)
            return;
        try { Session.Dispose(); }
        catch { /* ignore shutdown races */ }
        CloseSession();
        _stateTimer.Stop();
    }

    void CloseSession()
    {
        Session = null;
        State = new DebugState();
        Dispatch(() => Updated?.Invoke());
    }

    public void Pause()
    {
        Session?.Pause();
        RefreshState();
    }

    public void Resume()
    {
        Session?.Resume();
        RefreshState();
    }

    public void StepInto()
    {
        Session?.StepInto();
        RefreshState();
    }

    public void StepOver()
    {
        Session?.StepOver();
        RefreshState();
    }

    public void StepOut()
    {
        Session?.StepOut();
        RefreshState();
    }

    public void StepFrame()
    {
        Session?.StepFrame();
        RefreshState();
    }

    /// <summary>
    /// Pulls the current runner state from the native side. While the game is running only the
    /// lightweight status fields change; full stop data is only present while paused.
    /// </summary>
    public void RefreshState()
    {
        if (Session is null)
            return;

        try
        {
            DebugState state = Session.GetState() ?? new DebugState();
            bool pausedChanged = State?.Paused != state.Paused;
            State = state;
            StatusUpdated?.Invoke();
            if (pausedChanged)
                Updated?.Invoke();
        }
        catch
        {
            // The session may be tearing down while the timer fires.
        }
    }

    public void ToggleBreakpoint(UndertaleCode? code, uint offset)
    {
        if (code is null || Main.Data is null)
            return;
        int codeIndex = Main.Data.Code.IndexOf(code);
        if (codeIndex < 0)
            return;

        BreakpointEntry? existing = Breakpoints.FirstOrDefault(b => b.CodeIndex == codeIndex && b.Offset == offset);
        if (existing is not null)
        {
            Breakpoints.Remove(existing);
        }
        else
        {
            Breakpoints.Add(new BreakpointEntry
            {
                CodeIndex = codeIndex,
                CodeName = code.Name?.Content ?? "",
                Offset = offset,
                Enabled = true,
            });
        }

        ApplyBreakpoints();
        Updated?.Invoke();
    }

    public bool HasBreakpoint(int codeIndex, uint offset)
        => Breakpoints.Any(b => b.Enabled && b.CodeIndex == codeIndex && b.Offset == offset);

    public void ApplyBreakpoints()
    {
        if (Session is null)
            return;
        Session.SetBreakpoints(Breakpoints
            .Where(b => b.Enabled)
            .Select(b => new BreakpointDef(b.CodeIndex, b.Offset)));
    }

    public void ClearBreakpoints()
    {
        Breakpoints.Clear();
        if (Session is not null)
            Session.ClearBreakpoints();
        Updated?.Invoke();
    }

    public IReadOnlyList<InstanceInfo> GetInstances()
    {
        if (Session is null || !IsPaused)
            return Array.Empty<InstanceInfo>();
        return Session.GetInstances()?.Instances ?? (IReadOnlyList<InstanceInfo>)Array.Empty<InstanceInfo>();
    }

    public IReadOnlyList<StackFrameInfo> GetCallStack()
    {
        if (Session is null || !IsPaused)
            return Array.Empty<StackFrameInfo>();
        return Session.GetCallStack()?.Frames ?? (IReadOnlyList<StackFrameInfo>)Array.Empty<StackFrameInfo>();
    }

    public IReadOnlyList<InstanceVariableInfo> GetGlobals()
    {
        if (Session is null || !IsPaused)
            return Array.Empty<InstanceVariableInfo>();
        return Session.GetGlobals()?.Globals ?? (IReadOnlyList<InstanceVariableInfo>)Array.Empty<InstanceVariableInfo>();
    }

    public bool SetInstanceVariable(int instanceId, string name, string valueJson, out string error)
    {
        error = "No active session.";
        if (Session is null || !IsPaused)
            return false;
        return Session.SetInstanceVariable(instanceId, name, valueJson, out error);
    }

    public bool SetGlobalVariable(string name, string valueJson, out string error)
    {
        error = "No active session.";
        if (Session is null || !IsPaused)
            return false;
        return Session.SetGlobalVariable(name, valueJson, out error);
    }

    public bool SetFrameVariable(int frameIndex, bool isArgument, int slot, string valueJson, out string error)
    {
        error = "No active session.";
        if (Session is null || !IsPaused)
            return false;
        return Session.SetFrameVariable(frameIndex, isArgument, slot, valueJson, out error);
    }

    public void Log(string message) => AppendLog(message);

    public bool HotReload(IEnumerable<UndertaleCode> codes)
    {
        if (Session is null || Main.Data is null)
            return false;
        try
        {
            string patch = CodePatchBuilder.Build(Main.Data, codes);
            if (!Session.ApplyCodePatch(patch, out string error))
            {
                AppendLog("Hot reload failed: " + error);
                return false;
            }
            AppendLog("Hot reload queued.");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("Hot reload failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Reloads the code currently shown in the active code editor tab.</summary>
    public bool HotReloadCurrentTab()
    {
        if (Main.Tabs.TabSelected?.Content is UndertaleCodeViewModel codeViewModel)
            return HotReload(new[] { codeViewModel.Code });
        AppendLog("Open a code entry to hot reload it.");
        return false;
    }

    /// <summary>Opens the given CODE entry in a tab and shows its disassembly.</summary>
    public async Task OpenCodeEntry(int codeIndex)
    {
        if (Main.Data is null || codeIndex < 0 || codeIndex >= Main.Data.Code.Count)
            return;

        await Main.Tabs.TabOpen(Main.Data.Code[codeIndex]);
        if (Main.Tabs.TabSelected?.Content is UndertaleCodeViewModel codeViewModel)
            codeViewModel.SelectedTab = UndertaleCodeViewModel.Tab.ASM;
    }

    void OnState(DebugState? state, bool stopped)
    {
        Dispatch(() =>
        {
            State = state ?? new DebugState();
            if (stopped)
            {
                AppendLog($"Paused at {State.Stop?.Frame?.CodeName ?? "<frame boundary>"} (frame {State.FrameCount}).");
                Stopped?.Invoke(State);
            }
            Updated?.Invoke();
        });
    }

    void AppendLog(string message)
    {
        LogLines.Add(message);
        while (LogLines.Count > 2000)
            LogLines.RemoveAt(0);
        LogAdded?.Invoke(message);
    }

    static string LevelName(int level) => level switch
    {
        0 => "debug",
        1 => "info",
        2 => "warn",
        _ => "error",
    };

    void ReportError(string message)
    {
        Log(message);
        _ = Main.View?.MessageDialog(message);
    }

    static void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose() => Stop();
}
