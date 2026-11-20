using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace Muesli.Windows.Services;

/// <summary>
/// Windows counterpart of macOS CotypistSocketServer (2f10b23d): one JSON object per line in, one per
/// line out, one request per connection. The pipe's ACL admits only this user and denies network
/// logons, and each connection is served only if the client process passes <c>isTrustedPeer</c>.
/// The TSF helper runs inside the app being typed in, so the default check is that the client has
/// the bundled helper DLL loaded from Muesli's own directory; anything unreadable fails closed.
/// </summary>
public sealed class CotypistPipeServer : IDisposable
{
    private const int MaxRequestBytes = 1 << 20;
    private readonly Func<JsonObject, Task<JsonObject>> _handler;
    private readonly Func<int, bool> _isTrustedPeer;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();

    public static string PipeName => $"muesli-cotypist-{WindowsIdentity.GetCurrent().User!.Value}";

    public string Name { get; }

    public CotypistPipeServer(Func<JsonObject, Task<JsonObject>> handler, Func<int, bool> isTrustedPeer, Action<string> log, string? name = null)
    {
        _handler = handler;
        _isTrustedPeer = isTrustedPeer;
        _log = log;
        Name = name ?? PipeName;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The client process has <paramref name="helperPath"/> loaded (the TSF text service from
    /// Muesli's directory). Access denied — an elevated or protected app — fails closed.</summary>
    public static bool HasHelperLoaded(int processId, string helperPath)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Modules.Cast<ProcessModule>().Any(module => string.Equals(module.FileName, helperPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = Create(); }
            catch (Exception error)
            {
                _log($"Cotypist pipe could not be created. category={error.GetType().Name}");
                return;
            }
            try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { await pipe.DisposeAsync().ConfigureAwait(false); return; }
            catch (IOException) { await pipe.DisposeAsync().ConfigureAwait(false); continue; }
            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || !_isTrustedPeer((int)processId))
                {
                    _log("Cotypist pipe refused a client that isn't the Cotypist helper.");
                    return;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                if (await ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false) is not { } request) return;
                var response = await _handler(request).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
                await pipe.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    internal static async Task<JsonObject?> ReadLineAsync(Stream stream, CancellationToken cancellation)
    {
        var data = new MemoryStream();
        var buffer = new byte[16_384];
        while (data.Length < MaxRequestBytes)
        {
            var count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (count <= 0) break;
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            data.Write(buffer, 0, newline >= 0 ? newline : count);
            if (newline >= 0) break;
        }
        try { return JsonNode.Parse(data.ToArray()) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);
}
