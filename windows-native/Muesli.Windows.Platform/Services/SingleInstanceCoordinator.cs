using System.IO.Pipes;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Muesli.Windows.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string ActivateDashboardMessage = "activate-dashboard";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _listenerTask;
    private bool _ownsMutex;

    private SingleInstanceCoordinator(Mutex mutex, string pipeName, bool ownsMutex)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static bool IsHeadlessCommand(IEnumerable<string> args) => args.Any(arg =>
            arg.Equals("--diagnose-native", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--prepare-model", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--verify-model", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--benchmark-native", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("--benchmark-meeting", StringComparison.OrdinalIgnoreCase));

    public static bool IsActivationMessage(string? message) =>
        string.Equals(message?.Trim(), ActivateDashboardMessage, StringComparison.Ordinal);

    public static SingleInstanceCoordinator AcquireForCurrentUser()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        var mutex = new Mutex(initiallyOwned: true, $"Local\\Muesli.Windows.UI.{suffix}", out var createdNew);
        return new SingleInstanceCoordinator(mutex, $"Muesli.Windows.Activation.{suffix}", createdNew);
    }

    public void StartListening(Action onActivate)
    {
        if (!IsPrimary || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    var message = await reader.ReadLineAsync(_cancellation.Token);
                    if (IsActivationMessage(message))
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    if (_cancellation.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        });
    }

    public async Task<bool> SignalActivationAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Identification);
                await client.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                await using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(ActivateDashboardMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _ownsMutex = false;
        }
        _mutex.Dispose();
        _cancellation.Dispose();
    }
}
