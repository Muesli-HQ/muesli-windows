using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace Muesli.Windows.Core.Services;

/// <summary>
/// WinUI-side endpoint of the indicator pipe. WinUI owns two one-way named pipes: a snapshot pipe
/// (WinUI → companion) and a command pipe (companion → WinUI). Each direction runs on its own
/// background thread, so a write issued from the UI thread never races a blocking read on the
/// same <see cref="PipeStream"/> (which would deadlock). The server re-accepts after each
/// disconnect, and WinUI replays a full snapshot on each snapshot-pipe <see cref="Connected"/>.
/// </summary>
public sealed class IndicatorPipeServer : IDisposable
{
    private readonly string _instanceId;
    private readonly Action<IndicatorCommand>? _onCommand;
    private readonly BlockingCollection<string> _outbound = new();
    private PipeStream? _snapshotPipe;
    private PipeStream? _commandPipe;
    private Thread? _snapshotThread;
    private Thread? _commandThread;
    private int _disposed;

    public IndicatorPipeServer(string instanceId, Action<IndicatorCommand>? onCommand = null)
    {
        _instanceId = instanceId;
        _onCommand = onCommand;
    }

    public string PipeName => IndicatorProtocol.PipeNameFor(_instanceId);

    public bool IsConnected => _snapshotPipe is not null;

    public event EventHandler? Connected;

    public event EventHandler? Disconnected;

    public void Start()
    {
        if (_snapshotThread is not null) return;
        _snapshotThread = new Thread(SnapshotLoop) { IsBackground = true, Name = "Muesli indicator snapshot pipe" };
        _commandThread = new Thread(CommandLoop) { IsBackground = true, Name = "Muesli indicator command pipe" };
        _snapshotThread.Start();
        _commandThread.Start();
    }

    public void Publish(IndicatorSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _outbound.Add(IndicatorProtocol.SerializeSnapshot(snapshot));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SnapshotLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            using var pipe = new NamedPipeServerStream(
                IndicatorProtocol.SnapshotPipeName(_instanceId),
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly);
            _snapshotPipe = pipe;
            if (Volatile.Read(ref _disposed) != 0)
            {
                _snapshotPipe = null;
                pipe.Dispose();
                return;
            }

            try
            {
                pipe.WaitForConnection();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                if (ReferenceEquals(_snapshotPipe, pipe)) _snapshotPipe = null;
                continue;
            }

            Connected?.Invoke(this, EventArgs.Empty);
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    string payload;
                    try
                    {
                        payload = _outbound.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    PipeLineIO.WriteLine(pipe, payload);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_snapshotPipe, pipe)) _snapshotPipe = null;
            }
        }
    }

    private void CommandLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            using var pipe = new NamedPipeServerStream(
                IndicatorProtocol.CommandPipeName(_instanceId),
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.CurrentUserOnly);
            _commandPipe = pipe;
            if (Volatile.Read(ref _disposed) != 0)
            {
                _commandPipe = null;
                pipe.Dispose();
                return;
            }

            try
            {
                pipe.WaitForConnection();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                if (ReferenceEquals(_commandPipe, pipe)) _commandPipe = null;
                continue;
            }

            var reader = new PipeLineReader(pipe);
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    var command = IndicatorProtocol.DeserializeCommand(line);
                    if (command is not null) _onCommand?.Invoke(command);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_commandPipe, pipe)) _commandPipe = null;
                reader.Dispose();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _outbound.CompleteAdding();
        _snapshotPipe?.Dispose();
        _snapshotPipe = null;
        _commandPipe?.Dispose();
        _commandPipe = null;
    }
}

/// <summary>
/// Companion-side endpoint of the indicator pipe. Connects to the WinUI-owned snapshot pipe and
/// (for commands) the command pipe, both with automatic reconnect.
/// </summary>
public sealed class IndicatorPipeClient : IDisposable
{
    private readonly string _instanceId;
    private readonly Action<IndicatorSnapshot>? _onSnapshot;
    private readonly Action? _onConnected;
    private readonly Action? _onDisconnected;
    private readonly BlockingCollection<string> _outbound = new();
    private PipeStream? _snapshotPipe;
    private PipeStream? _commandPipe;
    private Thread? _snapshotThread;
    private Thread? _commandThread;
    private int _disposed;

    public IndicatorPipeClient(
        string instanceId,
        Action<IndicatorSnapshot>? onSnapshot = null,
        Action? onConnected = null,
        Action? onDisconnected = null)
    {
        _instanceId = instanceId;
        _onSnapshot = onSnapshot;
        _onConnected = onConnected;
        _onDisconnected = onDisconnected;
    }

    public bool IsConnected => _commandPipe is not null;

    public void Start()
    {
        if (_snapshotThread is not null) return;
        _snapshotThread = new Thread(SnapshotLoop) { IsBackground = true, Name = "Muesli indicator snapshot reader" };
        _commandThread = new Thread(CommandLoop) { IsBackground = true, Name = "Muesli indicator command writer" };
        _snapshotThread.Start();
        _commandThread.Start();
    }

    public void SendCommand(IndicatorCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _outbound.Add(IndicatorProtocol.SerializeCommand(command));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void SnapshotLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            using var pipe = new NamedPipeClientStream(".", IndicatorProtocol.SnapshotPipeName(_instanceId), PipeDirection.In);
            try
            {
                pipe.Connect(1000);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(200);
                continue;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
                continue;
            }

            _snapshotPipe = pipe;
            var reader = new PipeLineReader(pipe);
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    var line = reader.ReadLine();
                    if (line is null) break;
                    var snapshot = IndicatorProtocol.DeserializeSnapshot(line);
                    if (snapshot is not null) _onSnapshot?.Invoke(snapshot);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_snapshotPipe, pipe)) _snapshotPipe = null;
                reader.Dispose();
                _onDisconnected?.Invoke();
            }
        }
    }

    private void CommandLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            using var pipe = new NamedPipeClientStream(".", IndicatorProtocol.CommandPipeName(_instanceId), PipeDirection.Out);
            try
            {
                pipe.Connect(1000);
            }
            catch (TimeoutException)
            {
                Thread.Sleep(200);
                continue;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
                continue;
            }

            _commandPipe = pipe;
            _onConnected?.Invoke();
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    string payload;
                    try
                    {
                        payload = _outbound.Take();
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    PipeLineIO.WriteLine(pipe, payload);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(_commandPipe, pipe)) _commandPipe = null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _outbound.CompleteAdding();
        _snapshotPipe?.Dispose();
        _snapshotPipe = null;
        _commandPipe?.Dispose();
        _commandPipe = null;
    }
}

/// <summary>Newline-framed UTF-8 writes directly on a pipe stream.</summary>
internal static class PipeLineIO
{
    public static void WriteLine(PipeStream pipe, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload + "\n");
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }
}

/// <summary>Buffered newline-delimited reader over a pipe stream, tolerating CRLF.</summary>
internal sealed class PipeLineReader : IDisposable
{
    private readonly PipeStream _pipe;
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;

    public PipeLineReader(PipeStream pipe) => _pipe = pipe;

    /// <summary>Reads one newline-delimited line, or <c>null</c> on end of stream.</summary>
    public string? ReadLine()
    {
        var builder = new StringBuilder();
        while (true)
        {
            if (_offset >= _count)
            {
                _count = _pipe.Read(_buffer, 0, _buffer.Length);
                _offset = 0;
                if (_count == 0) return builder.Length == 0 ? null : builder.ToString();
            }

            var value = _buffer[_offset++];
            if (value == (byte)'\n') return builder.ToString();
            if (value == (byte)'\r') continue;
            builder.Append((char)value);
        }
    }

    public void Dispose()
    {
    }
}
