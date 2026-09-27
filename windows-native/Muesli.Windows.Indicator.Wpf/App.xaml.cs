using System.Diagnostics;
using System.IO;
using System.Windows;
using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Indicator.Wpf;

/// <summary>
/// Entry point for the WPF floating-indicator companion. The companion is deliberately narrow:
/// it renders snapshots and relays product commands, and never registers a hotkey, captures audio,
/// runs transcription, or opens the database. It is launched by the WinUI owner with
/// <c>--pipe</c> and <c>--parent-pid</c>, connects to the owner's named pipe, and exits when the
/// pipe closes or the parent process ends.
/// </summary>
public partial class App : Application
{
    private IndicatorWindow? _window;
    private MeetingNotificationWindow? _notification;
    private IndicatorPipeClient? _client;
    private CancellationTokenSource? _lifetime;
    private int _parentProcessId = -1;
    private string _pipeName = "";
    private int _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ParseArguments(e.Args);

        if (string.IsNullOrWhiteSpace(_pipeName))
        {
            // No owner pipe identity means the companion was launched incorrectly. Exit quietly;
            // it must never appear as a standalone indicator.
            Shutdown(2);
            return;
        }

        _lifetime = new CancellationTokenSource();

        _window = new IndicatorWindow(SendCommand);
        _client = new IndicatorPipeClient(
            _pipeName,
            OnSnapshot,
            onConnected: () => SendCommand(new IndicatorCommand { SessionId = 0, Type = IndicatorCommandType.Ready }));
        _client.Start();

        StartParentWatchdog(_lifetime.Token);
    }

    private void ParseArguments(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
            {
                _pipeName = arg["--pipe=".Length..];
            }
            else if (arg.Equals("--pipe", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                _pipeName = args[++index];
            }
            else if (arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(arg["--parent-pid=".Length..], out _parentProcessId);
            }
            else if (arg.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                _ = int.TryParse(args[++index], out _parentProcessId);
            }
        }
    }

    private void OnSnapshot(IndicatorSnapshot snapshot)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_window is not null) _window.Apply(snapshot);
            var request = snapshot.MeetingNotification;
            if (request is null)
            {
                _notification?.CloseSilently();
                _notification = null;
            }
            else if (_notification?.PromptId != request.PromptId)
            {
                _notification?.CloseSilently();
                _notification = new MeetingNotificationWindow(request, SendCommand);
                _notification.Show();
            }
        });
    }

    private void SendCommand(IndicatorCommand command)
    {
        _client?.SendCommand(command);
    }

    private void StartParentWatchdog(CancellationToken token)
    {
        if (_parentProcessId <= 0) return;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1000), token);
                if (!ProcessExists(_parentProcessId))
                {
                    BeginShutdown();
                    return;
                }
            }
        }, token);
    }

    private static bool ProcessExists(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.InvokeAsync(() =>
        {
            _notification?.CloseSilently();
            _window?.Close();
            Shutdown(0);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notification?.CloseSilently();
        _client?.Dispose();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        base.OnExit(e);
    }
}
