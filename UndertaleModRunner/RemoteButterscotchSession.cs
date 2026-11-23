using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UndertaleModRunner;

/// <summary>Thrown when the out-of-process debug host rejects a command or dies.</summary>
public sealed class ButterscotchRemoteException : Exception
{
    public ButterscotchRemoteException(string message) : base(message)
    {
    }
}

/// <summary>
/// Out-of-process Butterscotch debug session.  Starts
/// <c>native/win-&lt;arch&gt;/bsdbg-host.exe</c>, talks to it over a
/// current-user named pipe, and keeps a cached snapshot so the WPF/ Avalonia
/// UI never blocks on a hung host.
/// </summary>
public sealed class RemoteButterscotchSession : IButterscotchSession
{
    private const ushort ProtocolVersion = 1;
    private const int SupportedAbi = 1;
    private const int MaxFrameLength = 64 * 1024 * 1024;

    private const ushort MsgHello = 0;
    private const ushort MsgRequest = 1;
    private const ushort MsgResponse = 2;
    private const ushort MsgEvent = 3;

    private const ushort OpPing = 0;
    private const ushort OpStart = 1;
    private const ushort OpShutdown = 2;
    private const ushort OpPause = 3;
    private const ushort OpResume = 4;
    private const ushort OpStepInto = 5;
    private const ushort OpStepOver = 6;
    private const ushort OpStepOut = 7;
    private const ushort OpStepFrame = 8;
    private const ushort OpSetBreakpoints = 9;
    private const ushort OpClearBreakpoints = 10;
    private const ushort OpGetState = 11;
    private const ushort OpGetCallStack = 12;
    private const ushort OpGetInstances = 13;
    private const ushort OpGetGlobals = 14;
    private const ushort OpGetFrameCount = 15;
    private const ushort OpSetInstanceVariable = 16;
    private const ushort OpSetGlobalVariable = 17;
    private const ushort OpSetFrameVariable = 18;
    private const ushort OpApplyCodePatch = 19;
    private const ushort OpInject = 20;
    private const ushort OpPublishSurface = 21;
    private const ushort OpWithdrawSurface = 22;

    private const ushort EvState = 100;
    private const ushort EvBreak = 101;
    private const ushort EvExit = 102;
    private const ushort EvError = 103;
    private const ushort EvReloaded = 104;
    private const ushort EvLog = 105;

    private readonly ButterscotchConfig _config;
    private readonly ButterscotchArchitecture _architecture;
    private readonly string _hostPath;
    private readonly ButterscotchHostTransportKind _transportKind;
    private readonly string _socketPath;
    private readonly Func<string, IReadOnlyList<string>, IHostProcess> _processFactory;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _stateRefreshGate = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Response>> _pending = new();
    private readonly BlockingCollection<SessionEvent> _events = new();
    private readonly ConcurrentQueue<string> _startupLog = new();

    private IHostProcess _process;
    private IHostTransport _transport;
    private Stream _pipe;
    private IntPtr _jobHandle;
    private Thread _readerThread;
    private Thread _eventThread;
    private CancellationTokenSource _cts;
    private int _nextId;
    private int _abiVersion;
    private DebugState _state = new();
    private CallStackResult _callStack = new();
    private InstancesResult _instances = new();
    private GlobalsResult _globals = new();
    private volatile bool _started;
    private volatile bool _disposed;
    private volatile bool _shutdownRequested;
    private volatile bool _gameExited;
    private volatile bool _readerEnded;
    private int _hostExitCode;
    private string _hostExitMessage;

    public RemoteButterscotchSession(ButterscotchConfig config, ButterscotchArchitecture architecture,
                                     string hostPath,
                                     ButterscotchHostTransportKind transportKind = ButterscotchHostTransportKind.LoopbackTcp,
                                     string socketPath = null,
                                     Func<string, IReadOnlyList<string>, IHostProcess> processFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _architecture = architecture;
        _hostPath = hostPath ?? throw new ArgumentNullException(nameof(hostPath));
        _transportKind = transportKind;
        _socketPath = socketPath;
        _processFactory = processFactory;
        if (transportKind == ButterscotchHostTransportKind.UnixSocket && string.IsNullOrEmpty(socketPath))
            throw new ArgumentException("A socket path is required for Unix transport.", nameof(socketPath));
    }

    public int AbiVersion => _abiVersion;
    public bool IsRunning => _started && !_disposed && !_gameExited;
    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
                return _state.Paused;
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_stateLock)
                return _state.FrameCount;
        }
    }
    /// <summary>PID of the host process, or 0 before it starts.</summary>
    public int HostProcessId => _process?.Id ?? 0;

    public event Action<DebugState> StateChanged;
    public event Action<DebugState> Stopped;
    public event Action<int> Exited;
    public event Action<string> Error;
    public event Action<ReloadResult> Reloaded;
    public event Action<int, string> Log;

    public bool Start()
    {
        if (_started)
            return true;
        if (_disposed)
            throw new ObjectDisposedException(nameof(RemoteButterscotchSession));

        EnsureSafeLaunchConditions();
        _cts = new CancellationTokenSource();
        try
        {
            _transport = _transportKind == ButterscotchHostTransportKind.UnixSocket
                ? new UnixHostTransport(_socketPath)
                : new TcpHostTransport();
            _transport.Start();
            string endpoint = _transport is TcpHostTransport tcp
                ? tcp.Port.ToString()
                : _socketPath;
            var hostArgs = new List<string>();
            AddHostArguments(hostArgs, endpoint);

            if (_processFactory is not null)
            {
                _process = _processFactory(endpoint, hostArgs);
            }
            else
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _hostPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = string.IsNullOrEmpty(_config.WorkingDirectory)
                        ? Path.GetDirectoryName(Path.GetFullPath(_config.DataWinPath))
                        : _config.WorkingDirectory,
                };
                foreach (string arg in hostArgs)
                    startInfo.ArgumentList.Add(arg);
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => CaptureStartupLine(e.Data);
                process.ErrorDataReceived += (_, e) => CaptureStartupLine(e.Data);
                if (!process.Start())
                    throw new ButterscotchRemoteException("Failed to start the debug host.");
                _process = new DotNetHostProcess(process);
                AssignHostToJob();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            _transport.AcceptAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            _pipe = _transport.Stream;

            var hello = new TaskCompletionSource<HostHello>(TaskCreationOptions.RunContinuationsAsynchronously);
            _readerThread = new Thread(() => ReaderLoop(hello))
            {
                IsBackground = true,
                Name = "bsdbg-pipe-reader",
            };
            _eventThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "bsdbg-event-dispatch",
            };
            _readerThread.Start();
            _eventThread.Start();

            HostHello info;
            try
            {
                info = hello.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                throw new ButterscotchRemoteException(
                    "The debug host did not send a handshake.\n" + StartupLogTail());
            }
            ValidateHello(info);

            if (_config.EmbedWindow != IntPtr.Zero)
            {
                byte[] surface = new byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(surface.AsSpan(0), (int)_config.EmbedWindowKind);
                BinaryPrimitives.WriteUInt64LittleEndian(surface.AsSpan(4), (ulong)_config.EmbedWindow.ToInt64());
                Response published = SendRequestAsync(OpPublishSurface, surface,
                    TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (published.Status != 0)
                    throw new ButterscotchRemoteException(published.ErrorMessage);
            }

            // The host reports startup failures as ERROR/EXIT events.  Waiting for a
            // separate ACK here would serialise badly with the first STATE/LOG
            // events the runner emits while it boots.
            _writeLock.Wait();
            try
            {
                WriteFrameAsync(MsgRequest, OpStart, 0, 0, Array.Empty<byte>(),
                                CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                _writeLock.Release();
            }

            _started = true;
            return true;
        }
        catch (Exception ex)
        {
            CleanupAfterStartFailure();
            string detail = StartupLogTail();
            string message = ex is ButterscotchRemoteException
                ? ex.Message
                : "Could not start the out-of-process debugger: " + ex.Message;
            if (!string.IsNullOrEmpty(detail))
                message += Environment.NewLine + detail;
            throw new ButterscotchRemoteException(message);
        }
    }

    private void AddHostArguments(ICollection<string> args, string endpoint)
    {
        args.Add(_transportKind == ButterscotchHostTransportKind.UnixSocket ? "--socket" : "--port");
        args.Add(endpoint);
        args.Add("--data");
        args.Add(Path.GetFullPath(_config.DataWinPath));
        if (!string.IsNullOrEmpty(_config.WorkingDirectory))
        {
            args.Add("--workdir");
            args.Add(_config.WorkingDirectory);
        }
        if (!string.IsNullOrEmpty(_config.SaveFolder))
        {
            args.Add("--save");
            args.Add(_config.SaveFolder);
        }
        args.Add("--renderer");
        args.Add(((int)_config.Renderer).ToString());
        if (_config.WindowWidth > 0)
        {
            args.Add("--width");
            args.Add(_config.WindowWidth.ToString());
        }
        if (_config.WindowHeight > 0)
        {
            args.Add("--height");
            args.Add(_config.WindowHeight.ToString());
        }
        if (_config.Headless)
            args.Add("--headless");
        if (_config.Fullscreen)
            args.Add("--fullscreen");
        if (_config.Speed > 0 && Math.Abs(_config.Speed - 1.0) > 0.0001)
        {
            args.Add("--speed");
            args.Add(_config.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (_config.Seed != 0)
        {
            args.Add("--seed");
            args.Add(_config.Seed.ToString());
        }
        if (_config.StopOnEntry)
            args.Add("--stop-on-entry");
        if (_config.GameArgs is not null)
        {
            foreach (string argument in _config.GameArgs)
            {
                args.Add("--game-arg");
                args.Add(argument);
            }
        }
        args.Add("--dump-dir");
        args.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                              "UndertaleModTool", "crashes"));
    }

    private void CaptureStartupLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        _startupLog.Enqueue(line.Trim());
        while (_startupLog.Count > 80 && _startupLog.TryDequeue(out _))
        {
        }
        if (Environment.GetEnvironmentVariable("BSDBG_HOST_DEBUG") == "1")
            Console.Error.WriteLine("[bsdbg-host] " + line.Trim());
        if (_started)
            Log?.Invoke(3, line.Trim());
    }

    private string StartupLogTail()
    {
        return string.Join(Environment.NewLine, _startupLog.TakeLast(12));
    }

    private void ValidateHello(HostHello hello)
    {
        if (hello.Protocol != ProtocolVersion)
            throw new ButterscotchRemoteException(
                $"Debug host protocol mismatch (host {hello.Protocol}, expected {ProtocolVersion}).");
        if (hello.Abi != SupportedAbi)
            throw new ButterscotchRemoteException(
                $"Debug host ABI mismatch (host {hello.Abi}, expected {SupportedAbi}).");
        _abiVersion = hello.Abi;

        string expected = ButterscotchHost.DescribeArchitecture(_architecture);
        if (!string.Equals(hello.Arch, expected, StringComparison.OrdinalIgnoreCase))
            throw new ButterscotchRemoteException(
                $"Debug host architecture mismatch (host {hello.Arch}, expected {expected}).");
        // Java.Lang.Process on Android does not expose the child PID, so JavaHostProcess.Id is 0
        // and the handshake PID is only informational. Compare only when the launcher knows it.
        if (_process is not null && _process.Id > 0 && hello.Pid > 0 && hello.Pid != _process.Id)
            throw new ButterscotchRemoteException(
                $"Debug host handshake PID does not match the started process (host {hello.Pid}, started {_process.Id}).");
    }

    private void CleanupAfterStartFailure()
    {
        try { _cts?.Cancel(); } catch { }
        try { _transport?.Stop(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        if (_jobHandle != IntPtr.Zero)
        {
            try { CloseHandle(_jobHandle); } catch { }
            _jobHandle = IntPtr.Zero;
        }
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
    }

    private async Task<Response> SendRequestAsync(ushort opcode, byte[] payload, TimeSpan timeout,
                                                  CancellationToken cancellationToken = default)
    {
        if (_pipe is null || _readerEnded)
            throw new ButterscotchRemoteException("The debug host is not connected.");

        uint id = (uint)Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        CancellationTokenSource timeoutCts = null;
        if (Environment.GetEnvironmentVariable("BSDBG_HOST_DEBUG") == "1")
            Console.Error.WriteLine($"[remote] send op={opcode} id={id} len={payload.Length}");
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteFrameAsync(MsgRequest, opcode, id, 0, payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
            if (Environment.GetEnvironmentVariable("BSDBG_HOST_DEBUG") == "1")
                Console.Error.WriteLine($"[remote] send done op={opcode} id={id}, waiting");
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ButterscotchRemoteException(
                    $"The debug host did not answer '{OpcodeName(opcode)}' within {timeout.TotalSeconds:0.#}s.");
            }
        }
        finally
        {
            timeoutCts?.Dispose();
            _pending.TryRemove(id, out _);
        }
    }

    private Task WriteFrameAsync(ushort type, ushort opcode, uint id, ushort status,
                                 byte[] payload, CancellationToken cancellationToken)
    {
        payload ??= Array.Empty<byte>();
        if (payload.Length > MaxFrameLength)
            throw new ButterscotchRemoteException("Request frame is too large.");

        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), (uint)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), id);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), status);

        cancellationToken.ThrowIfCancellationRequested();
        _pipe.Write(header, 0, header.Length);
        if (payload.Length > 0)
            _pipe.Write(payload, 0, payload.Length);
        _pipe.Flush();
        return Task.CompletedTask;
    }

    private void ReaderLoop(TaskCompletionSource<HostHello> helloCompletion)
    {
        byte[] header = new byte[16];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                ReadExact(header, 16);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0));
                ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
                ushort status = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));

                if (version != ProtocolVersion || length > MaxFrameLength)
                    throw new ButterscotchRemoteException("The debug host sent an invalid frame.");

                byte[] payload = Array.Empty<byte>();
                if (length > 0)
                {
                    payload = new byte[length];
                    ReadExact(payload, (int)length);
                }


                switch (type)
                {
                    case MsgHello:
                        helloCompletion.TrySetResult(JsonConvert.DeserializeObject<HostHello>(
                            Encoding.UTF8.GetString(payload)) ?? new HostHello());
                        break;
                    case MsgResponse:
                        if (_pending.TryRemove(id, out TaskCompletionSource<Response> pending))
                            pending.TrySetResult(new Response(status, payload));
                        break;
                    case MsgEvent:
                        _events.Add(new SessionEvent(opcode, payload));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            helloCompletion.TrySetException(ex);
            if (!_shutdownRequested && !_disposed)
                HandleUnexpectedDisconnect(ex);
        }
        finally
        {
            _readerEnded = true;
        }
    }

    private void ReadExact(byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = _pipe.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new EndOfStreamException("The debug host closed the pipe.");
            offset += read;
        }
    }

    private void HandleUnexpectedDisconnect(Exception exception)
    {
        int exitCode = -1;
        if (_process is not null)
        {
            try
            {
                if (_process.WaitForExit(2000))
                    exitCode = _process.ExitCode;
            }
            catch { }
        }
        _started = false;
        _hostExitCode = exitCode;
        _hostExitMessage =
            $"The Butterscotch debug host exited unexpectedly (code 0x{exitCode:X8}). " +
            "UndertaleModTool is still running; the debug session was closed.";

        foreach (TaskCompletionSource<Response> pending in _pending.Values)
            pending.TrySetException(new ButterscotchRemoteException(_hostExitMessage));
        _pending.Clear();

        Error?.Invoke(_hostExitMessage);
        if (!_gameExited)
            Exited?.Invoke(-1);
    }

    private void EventLoop()
    {
        try
        {
            foreach (SessionEvent sessionEvent in _events.GetConsumingEnumerable())
                DispatchEvent(sessionEvent);
        }
        catch
        {
            // The session is tearing down; never let an event handler kill the process.
        }
    }

    private void DispatchEvent(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Opcode)
        {
            case EvState:
            case EvBreak:
            {
                DebugState state = ParseJson<DebugState>(sessionEvent.Payload) ?? new DebugState();
                lock (_stateLock)
                    _state = state;
                StateChanged?.Invoke(state);
                if (sessionEvent.Opcode == EvBreak)
                    Stopped?.Invoke(state);
                break;
            }
            case EvExit:
            {
                _gameExited = true;
                int code = 0;
                try
                {
                    JObject obj = JObject.Parse(Encoding.UTF8.GetString(sessionEvent.Payload));
                    code = obj.Value<int?>("code") ?? 0;
                }
                catch
                {
                }
                Exited?.Invoke(code);
                break;
            }
            case EvError:
            {
                string message = ParseMessage(sessionEvent.Payload);
                Error?.Invoke(message);
                break;
            }
            case EvReloaded:
            {
                ReloadResult result = ParseJson<ReloadResult>(sessionEvent.Payload) ?? new ReloadResult();
                Reloaded?.Invoke(result);
                break;
            }
            case EvLog:
            {
                if (sessionEvent.Payload.Length < 4)
                    break;
                int level = BinaryPrimitives.ReadInt32LittleEndian(sessionEvent.Payload.AsSpan(0));
                string message = Encoding.UTF8.GetString(sessionEvent.Payload, 4,
                    sessionEvent.Payload.Length - 4);
                Log?.Invoke(level, message);
                break;
            }
        }
    }

    private static T ParseJson<T>(byte[] payload) where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(payload));
        }
        catch
        {
            return null;
        }
    }

    private static string ParseMessage(byte[] payload)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(payload)));
            JObject obj = JObject.Load(reader);
            return obj.Value<string>("message") ?? Encoding.UTF8.GetString(payload);
        }
        catch
        {
            return Encoding.UTF8.GetString(payload);
        }
    }

    public Task RefreshStateAsync()
    {
        if (!_started || _disposed || _readerEnded)
            return Task.CompletedTask;
        return RefreshStateCoreAsync();
    }

    private async Task RefreshStateCoreAsync()
    {
        if (!await _stateRefreshGate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            Response response = await SendRequestAsync(OpGetState, Array.Empty<byte>(),
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (response.Status != 0)
                return;
            DebugState state = ParseJson<DebugState>(response.Payload);
            if (state is null)
                return;
            lock (_stateLock)
                _state = state;
        }
        catch
        {
            // A hung/stopping host must not take down the UI timer.
        }
        finally
        {
            _stateRefreshGate.Release();
        }
    }

    public async Task RefreshInspectionAsync()
    {
        if (!_started || _disposed || _readerEnded || !IsPaused)
            return;

        try
        {
            Response stack = await SendRequestAsync(OpGetCallStack, Array.Empty<byte>(),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (stack.Status == 0)
            {
                CallStackResult parsed = ParseJson<CallStackResult>(stack.Payload);
                if (parsed is not null)
                {
                    lock (_stateLock)
                        _callStack = parsed;
                }
            }

            Response instances = await SendRequestAsync(OpGetInstances, Array.Empty<byte>(),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (instances.Status == 0)
            {
                InstancesResult parsed = ParseJson<InstancesResult>(instances.Payload);
                if (parsed is not null)
                {
                    lock (_stateLock)
                        _instances = parsed;
                }
            }

            Response globals = await SendRequestAsync(OpGetGlobals, Array.Empty<byte>(),
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (globals.Status == 0)
            {
                GlobalsResult parsed = ParseJson<GlobalsResult>(globals.Payload);
                if (parsed is not null)
                {
                    lock (_stateLock)
                        _globals = parsed;
                }
            }
        }
        catch
        {
            // Keep the last good snapshot.
        }
    }

    public bool Pause() => SendControl(OpPause, "pause");
    public bool Resume() => SendControl(OpResume, "resume");
    public bool StepInto() => SendControl(OpStepInto, "step into");
    public bool StepOver() => SendControl(OpStepOver, "step over");
    public bool StepOut() => SendControl(OpStepOut, "step out");
    public bool StepFrame() => SendControl(OpStepFrame, "step frame");

    private bool SendControl(ushort opcode, string name)
    {
        try
        {
            Response response = SendRequestAsync(opcode, Array.Empty<byte>(),
                TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            return response.Status == 0;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Could not {name}: {ex.Message}");
            return false;
        }
    }

    public bool SetBreakpoints(IEnumerable<BreakpointDef> breakpoints)
    {
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            foreach (BreakpointDef breakpoint in breakpoints ?? Enumerable.Empty<BreakpointDef>())
            {
                writer.Write(breakpoint.CodeIndex);
                writer.Write(breakpoint.Offset);
                writer.Write(breakpoint.Enabled ? 1 : 0);
            }
            writer.Flush();
            Response response = SendRequestAsync(OpSetBreakpoints, stream.ToArray(),
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return response.Status == 0;
        }
        catch (Exception ex)
        {
            Error?.Invoke("Could not update breakpoints: " + ex.Message);
            return false;
        }
    }

    public bool ClearBreakpoints() => SendControl(OpClearBreakpoints, "clear breakpoints");

    public DebugState GetState()
    {
        lock (_stateLock)
            return _state;
    }

    public CallStackResult GetCallStack()
    {
        lock (_stateLock)
            return _callStack;
    }

    public InstancesResult GetInstances()
    {
        lock (_stateLock)
            return _instances;
    }

    public GlobalsResult GetGlobals()
    {
        lock (_stateLock)
            return _globals;
    }

    public bool SetInstanceVariable(int instanceId, string name, string valueJson, out string error)
    {
        error = null;
        try
        {
            byte[] payload = EncodeNameValue(instanceId, name, valueJson);
            Response response = SendRequestAsync(OpSetInstanceVariable, payload,
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (response.Status == 0)
                return true;
            error = response.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool SetGlobalVariable(string name, string valueJson, out string error)
    {
        error = null;
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteBlob(writer, name);
            WriteBlob(writer, valueJson);
            writer.Flush();
            Response response = SendRequestAsync(OpSetGlobalVariable, stream.ToArray(),
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (response.Status == 0)
                return true;
            error = response.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool SetFrameVariable(int frameIndex, bool isArgument, int slot, string valueJson, out string error)
    {
        error = null;
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(frameIndex);
            writer.Write(isArgument ? 1 : 0);
            writer.Write(slot);
            WriteBlob(writer, valueJson);
            writer.Flush();
            Response response = SendRequestAsync(OpSetFrameVariable, stream.ToArray(),
                TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (response.Status == 0)
                return true;
            error = response.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool ApplyCodePatch(string patchJson, out string error)
    {
        error = null;
        try
        {
            Response response = SendRequestAsync(OpApplyCodePatch, Encoding.UTF8.GetBytes(patchJson ?? ""),
                TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            if (response.Status == 0)
                return true;
            error = response.ErrorMessage;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] EncodeNameValue(int instanceId, string name, string valueJson)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(instanceId);
        WriteBlob(writer, name);
        WriteBlob(writer, valueJson);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteBlob(BinaryWriter writer, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    public void PublishSurface(IntPtr handle, BsdbgEmbedWindowKind kind)
    {
        if (handle == IntPtr.Zero)
            return;
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), (int)kind);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(4), (ulong)handle.ToInt64());
        Response response = SendRequestAsync(OpPublishSurface, payload,
            TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (response.Status != 0)
            Error?.Invoke("Could not publish the game surface: " + response.ErrorMessage);
    }

    public void WithdrawSurface()
    {
        if (_started || _pipe is null)
            return;
        try
        {
            SendRequestAsync(OpWithdrawSurface, Array.Empty<byte>(), TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort; disposing the host also releases the surface.
        }
    }

    public void InjectKey(int gmlKey, bool down) => SendInject(writer =>
    {
        writer.Write(0);
        writer.Write(gmlKey);
        writer.Write(down ? 1 : 0);
    });

    public void InjectText(uint character) => SendInject(writer =>
    {
        writer.Write(1);
        writer.Write(character);
    });

    public void InjectMouseButton(int button, bool down) => SendInject(writer =>
    {
        writer.Write(2);
        writer.Write(button);
        writer.Write(down ? 1 : 0);
    });

    public void InjectMousePos(double x, double y) => SendInject(writer =>
    {
        writer.Write(3);
        writer.Write(x);
        writer.Write(y);
    });

    public void InjectWheel(double offset) => SendInject(writer =>
    {
        writer.Write(4);
        writer.Write(offset);
    });

    public void InjectGamepadButton(int button, bool down, float value) => SendInject(writer =>
    {
        writer.Write(5);
        writer.Write(button);
        writer.Write(down ? 1 : 0);
        writer.Write(value);
    });

    public void InjectGamepadAxis(int axis, float value) => SendInject(writer =>
    {
        writer.Write(6);
        writer.Write(axis);
        writer.Write(value);
    });

    private void SendInject(Action<BinaryWriter> writePayload)
    {
        if (_pipe is null || _readerEnded)
            return;
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writePayload(writer);
            writer.Flush();

            _writeLock.Wait();
            try
            {
                WriteFrameAsync(MsgRequest, OpInject, 0, 0, stream.ToArray(),
                                CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Input is lossy; never let a stale session block the UI thread.
        }
    }
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdownRequested = true;

        try
        {
            if (_started && _pipe is not null && !_readerEnded)
            {
                SendRequestAsync(OpShutdown, Array.Empty<byte>(), TimeSpan.FromSeconds(2))
                    .GetAwaiter().GetResult();
            }
        }
        catch { }

        try { _cts?.Cancel(); } catch { }
        try { _transport?.Stop(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _transport = null;
        _pipe = null;
        _events.CompleteAdding();

        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(5000))
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }
        if (_readerThread is not null && Thread.CurrentThread != _readerThread)
            _readerThread.Join(2000);
        if (_eventThread is not null && Thread.CurrentThread != _eventThread)
            _eventThread.Join(2000);

        foreach (TaskCompletionSource<Response> pending in _pending.Values)
            pending.TrySetException(new ButterscotchRemoteException("The debug session was closed."));
        _pending.Clear();
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }

    private static void EnsureSafeLaunchConditions()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (Environment.GetEnvironmentVariable("UTMT_ALLOW_ELEVATED_DEBUG_HOST") == "1")
            return;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out IntPtr token))
            return;
        try
        {
            int elevated = 0;
            int length;
            if (GetTokenInformation(token, 20, out elevated, sizeof(int), out length) && elevated != 0)
            {
                throw new ButterscotchRemoteException(
                    "UndertaleModTool is running as administrator. Restart it with normal " +
                    "privileges before debugging an untrusted game. Set " +
                    "UTMT_ALLOW_ELEVATED_DEBUG_HOST=1 only if you accept the risk.");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private void AssignHostToJob()
    {
        if (!OperatingSystem.IsWindows() || _process is null || _process.Handle == IntPtr.Zero)
            return;
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
            throw new ButterscotchRemoteException("Could not create a job object for the debug host.");

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                // Killing the job when UTMT's handle closes prevents orphan
                // game processes and windows after an editor crash.
                LimitFlags = 0x00002000,
            },
        };
        int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        int error = 0;
        bool assigned = SetInformationJobObject(_jobHandle, 9, ref limits, size);
        if (assigned)
            assigned = AssignProcessToJobObject(_jobHandle, _process.Handle);
        if (!assigned)
            error = Marshal.GetLastWin32Error();
        if (!assigned)
        {
            try { _process.Kill(); } catch { }
            throw new ButterscotchRemoteException(
                $"Could not isolate the debug host in a job object (Win32 error {error}).");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int tokenInformationClass,
                                                   out int tokenInformation, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass,
                                                       ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information,
                                                       int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
    private static string OpcodeName(ushort opcode) => opcode switch
    {
        OpStart => "start",
        OpPause => "pause",
        OpResume => "resume",
        OpGetState => "getState",
        OpGetCallStack => "getCallStack",
        OpGetInstances => "getInstances",
        OpGetGlobals => "getGlobals",
        OpSetBreakpoints => "setBreakpoints",
        OpApplyCodePatch => "applyCodePatch",
        _ => "request",
    };

    private readonly struct Response
    {
        public Response(ushort status, byte[] payload)
        {
            Status = status;
            Payload = payload ?? Array.Empty<byte>();
        }

        public ushort Status { get; }
        public byte[] Payload { get; }
        public string ErrorMessage => Status == 0 ? null : Encoding.UTF8.GetString(Payload);
    }

    private readonly struct SessionEvent
    {
        public SessionEvent(ushort opcode, byte[] payload)
        {
            Opcode = opcode;
            Payload = payload ?? Array.Empty<byte>();
        }

        public ushort Opcode { get; }
        public byte[] Payload { get; }
    }

    private sealed class HostHello
    {
        [JsonProperty("protocol")] public ushort Protocol { get; set; }
        [JsonProperty("abi")] public int Abi { get; set; }
        [JsonProperty("arch")] public string Arch { get; set; }
        [JsonProperty("pid")] public int Pid { get; set; }
        [JsonProperty("build")] public string Build { get; set; }
    }
}
