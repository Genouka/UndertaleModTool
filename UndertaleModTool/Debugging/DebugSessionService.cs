using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModRunner;

namespace UndertaleModTool.Debugging
{
    public sealed class BreakpointEntry
    {
        public int CodeIndex { get; set; }
        public string CodeName { get; set; } = "";
        public uint Offset { get; set; }
        public bool Enabled { get; set; } = true;

        public override string ToString()
        {
            return $"{CodeName} + 0x{Offset:X}";
        }
    }

    /// <summary>
    /// Owns the Butterscotch debug session for the main window and fans native
    /// events out to the debug tool windows on the UI thread.
    /// </summary>
    public sealed class DebugSessionService : IDisposable
    {
        public static DebugSessionService Instance { get; private set; }

        public MainWindow MainWindow { get; }
        public ButterscotchSession Session { get; private set; }
        public DebugState State { get; private set; } = new();
        public ObservableCollection<BreakpointEntry> Breakpoints { get; } = new();
        public ObservableCollection<string> LogLines { get; } = new();

        public event Action Updated;
        public event Action<DebugState> Stopped;
        /// <summary>Raised when only the lightweight status (frame/paused) changed.</summary>
        public event Action StatusUpdated;

        private readonly DispatcherTimer _stateTimer;
        public event Action<string> LogAdded;

        public bool IsActive => Session is not null;
        public bool IsPaused => State?.Paused == true;

        public DebugSessionService(MainWindow mainWindow)
        {
            MainWindow = mainWindow;
            Instance = this;
            _stateTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _stateTimer.Tick += (_, _) => RefreshState();
        }

        public bool Start(bool stopOnEntry)
        {
            return Start(MainWindow.Project?.SaveDataPath ?? MainWindow.FilePath, stopOnEntry);
        }

        public bool Start(string dataPath, bool stopOnEntry)
        {
            if (Session is not null)
                Stop();

            if (string.IsNullOrEmpty(dataPath) || !File.Exists(dataPath))
            {
                MainWindow.ShowError("Save the data file before starting a Butterscotch session.");
                return false;
            }

            ButterscotchConfig config = new()
            {
                DataWinPath = dataPath,
                WorkingDirectory = Path.GetDirectoryName(dataPath),
                Renderer = BsdbgRenderer.ModernGL,
                Speed = 1.0,
                StopOnEntry = stopOnEntry
            };

            try
            {
                Session = new ButterscotchSession(config);
            }
            catch (Exception ex)
            {
                MainWindow.ShowError($"Could not load ButterscotchDbg: {ex.Message}");
                Session = null;
                return false;
            }

            Session.StateChanged += s => OnState(s, false);
            Session.Stopped += s => OnState(s, true);
            Session.Exited += code => Dispatch(() =>
            {
                AppendLog($"Game exited (code {code}).");
                CloseSession();
            });
            Session.Error += message => Dispatch(() => AppendLog("ERROR: " + message));
            Session.Log += (level, message) => Dispatch(() => AppendLog($"[{LevelName(level)}] {message}"));
            Session.Reloaded += r => Dispatch(() => AppendLog($"Hot reload applied: {r.Applied} code entries."));

            ApplyBreakpoints();
            if (!Session.Start())
            {
                MainWindow.ShowError("Butterscotch runner failed to start (see output window).");
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

        private void CloseSession()
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
        /// Pulls the current runner state from the native side. While the game is
        /// running only the lightweight status fields change; the full stop data
        /// is only present while paused.
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

        public void ToggleBreakpoint(UndertaleCode code, uint offset)
        {
            if (code is null || MainWindow.Data is null)
                return;
            int codeIndex = MainWindow.Data.Code.IndexOf(code);
            if (codeIndex < 0)
                return;

            BreakpointEntry existing = Breakpoints.FirstOrDefault(b => b.CodeIndex == codeIndex && b.Offset == offset);
            if (existing is not null)
                Breakpoints.Remove(existing);
            else
                Breakpoints.Add(new BreakpointEntry
                {
                    CodeIndex = codeIndex,
                    CodeName = code.Name?.Content ?? "",
                    Offset = offset,
                    Enabled = true
                });

            ApplyBreakpoints();
            Updated?.Invoke();
        }

        public bool HasBreakpoint(int codeIndex, uint offset)
        {
            return Breakpoints.Any(b => b.Enabled && b.CodeIndex == codeIndex && b.Offset == offset);
        }

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
            if (Session is null || MainWindow.Data is null)
                return false;
            try
            {
                string patch = CodePatchBuilder.Build(MainWindow.Data, codes);
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
            if (MainWindow.CurrentTab?.CurrentObject is UndertaleCode code)
                return HotReload(new[] { code });
            AppendLog("Open a code entry to hot reload it.");
            return false;
        }

        private void OnState(DebugState state, bool stopped)
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

        private void AppendLog(string message)
        {
            LogLines.Add(message);
            while (LogLines.Count > 2000)
                LogLines.RemoveAt(0);
            LogAdded?.Invoke(message);
        }

        private static string LevelName(int level) => level switch
        {
            0 => "debug",
            1 => "info",
            2 => "warn",
            _ => "error"
        };

        private void Dispatch(Action action)
        {
            Dispatcher dispatcher = MainWindow?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }

        public void Dispose() => Stop();
    }
}
