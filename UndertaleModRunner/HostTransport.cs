using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UndertaleModRunner;

/// <summary>How the editor and the debug host find each other.</summary>
public enum ButterscotchHostTransportKind
{
    /// <summary>TCP on 127.0.0.1 with an ephemeral port (Windows).</summary>
    LoopbackTcp,
    /// <summary>Unix domain socket in a private directory (Android, Linux, macOS).</summary>
    UnixSocket,
}

/// <summary>A process launched as the debug host. Android cannot use System.Diagnostics.Process.</summary>
public interface IHostProcess : IDisposable
{
    int Id { get; }
    IntPtr Handle { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(int milliseconds);
}

internal sealed class DotNetHostProcess : IHostProcess
{
    private readonly Process _process;

    public DotNetHostProcess(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public int ExitCode => _process.ExitCode;
    public bool HasExited => _process.HasExited;
    public void Kill() => _process.Kill(true);
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public void Dispose() => _process.Dispose();
}

/// <summary>Listens for a single debug host connection.</summary>
internal interface IHostTransport : IDisposable
{
    Stream Stream { get; }
    void Start();
    Task AcceptAsync(TimeSpan timeout);
    void Stop();
}

internal sealed class TcpHostTransport : IHostTransport
{
    private TcpListener _listener;
    private TcpClient _client;

    public int Port { get; private set; }
    public Stream Stream { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task AcceptAsync(TimeSpan timeout)
    {
        Task<TcpClient> accept = _listener.AcceptTcpClientAsync();
        Task completed = await Task.WhenAny(accept, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != accept)
            throw new TimeoutException("Timed out waiting for the debug host.");
        _client = await accept.ConfigureAwait(false);
        _client.NoDelay = true;
        Stream = _client.GetStream();
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        try { Stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        Stream = null;
        _client = null;
    }

    public void Dispose() => Stop();
}

internal sealed class UnixHostTransport : IHostTransport
{
    private readonly string _path;
    private Socket _listener;
    private Socket _accepted;

    public UnixHostTransport(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public Stream Stream { get; private set; }

    public void Start()
    {
        string directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        try { File.Delete(_path); } catch { }
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_path));
        _listener.Listen(1);
    }

    public async Task AcceptAsync(TimeSpan timeout)
    {
        Task<Socket> accept = _listener.AcceptAsync(CancellationToken.None).AsTask();
        Task completed = await Task.WhenAny(accept, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != accept)
            throw new TimeoutException("Timed out waiting for the debug host.");
        _accepted = await accept.ConfigureAwait(false);
        Stream = new NetworkStream(_accepted, ownsSocket: true);
    }

    public void Stop()
    {
        try { _listener?.Dispose(); } catch { }
        try { Stream?.Dispose(); } catch { }
        try { _accepted?.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
        Stream = null;
        _accepted = null;
    }

    public void Dispose() => Stop();
}
