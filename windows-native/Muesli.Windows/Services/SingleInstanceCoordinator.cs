using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Muesli.Windows.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _ownsMutex;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, bool ownsMutex)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceCoordinator AcquireForCurrentUser()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var mutex = new Mutex(true, $"Local\\Muesli.Windows.UI.{suffix}", out var createdNew);
        return new SingleInstanceCoordinator(mutex, $"Muesli.Windows.Activation.{suffix}", createdNew);
    }

    public void StartListening(Action onActivate)
    {
        if (!IsPrimary) return;
        _ = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    if (string.Equals(await reader.ReadLineAsync(_cancellation.Token), "activate-dashboard", StringComparison.Ordinal)) onActivate();
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { break; }
                catch (IOException) when (!_cancellation.IsCancellationRequested) { }
            }
        });
    }

    public async Task SignalActivationAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await client.ConnectAsync(250);
                await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync("activate-dashboard");
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                await Task.Delay(75);
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
