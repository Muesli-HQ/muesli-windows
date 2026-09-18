using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed record ProcessLoopbackCapability(
    bool OperatingSystemSupported,
    bool TargetProcessAvailable,
    bool CanAttempt,
    string Diagnostic);

public static class WindowsProcessLoopbackSupport
{
    public const int MinimumWindowsBuild = 20348;

    public static ProcessLoopbackCapability Inspect(int? targetProcessId, Version? operatingSystem = null)
    {
        var version = operatingSystem ?? Environment.OSVersion.Version;
        var osSupported = OperatingSystem.IsWindows() && version.Build >= MinimumWindowsBuild;
        var targetAvailable = targetProcessId is > 0 && IsProcessAvailable(targetProcessId.Value);
        var diagnostic = !osSupported
            ? $"Windows process loopback requires build {MinimumWindowsBuild} or newer."
            : !targetAvailable
                ? "No live target process was available for process loopback."
                : "Windows process-tree loopback can be attempted.";
        return new ProcessLoopbackCapability(
            osSupported,
            targetAvailable,
            osSupported && targetAvailable,
            diagnostic);
    }

    private static bool IsProcessAvailable(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class WindowsProcessLoopbackCapture : IDisposable
{
    private const string ProcessLoopbackVirtualDevice = "VAD\\Process_Loopback";
    private readonly int _processId;
    private readonly AutoResetEvent _audioReady = new(false);
    private readonly CancellationTokenSource _captureCancellation = new();
    private AudioClient? _audioClient;
    private AudioCaptureClient? _captureClient;
    private Task? _captureLoop;
    private int _started;
    private int _stoppedRaised;

    public WindowsProcessLoopbackCapture(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }
        _processId = processId;
    }

    public WaveFormat WaveFormat { get; private set; } = new WaveFormat(48000, 32, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Process loopback capture has already started.");
        }

        try
        {
            _audioClient = await ActivateProcessAudioClientAsync(_processId, cancellationToken).ConfigureAwait(false);
            WaveFormat = _audioClient.MixFormat;
            _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality,
                1_000_000,
                0,
                WaveFormat,
                Guid.Empty);
            _audioClient.SetEventHandle(_audioReady.SafeWaitHandle.DangerousGetHandle());
            _captureClient = _audioClient.AudioCaptureClient;
            _captureLoop = Task.Run(CaptureLoop);
            _audioClient.Start();
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            _audioClient?.Stop();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        _captureCancellation.Cancel();
        _audioReady.Set();
        if (_captureLoop is not null)
        {
            try
            {
                await _captureLoop.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        DisposeResources();
        RaiseStopped(failure);
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            DisposeResources();
        }
        _captureCancellation.Dispose();
        _audioReady.Dispose();
    }

    private void CaptureLoop()
    {
        try
        {
            var waitHandles = new WaitHandle[] { _audioReady, _captureCancellation.Token.WaitHandle };
            while (!_captureCancellation.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waitHandles, 1000) != 0 || _captureCancellation.IsCancellationRequested)
                {
                    continue;
                }

                var captureClient = _captureClient;
                if (captureClient is null)
                {
                    return;
                }

                for (var packetFrames = captureClient.GetNextPacketSize();
                     packetFrames > 0;
                     packetFrames = captureClient.GetNextPacketSize())
                {
                    var bufferPointer = captureClient.GetBuffer(out var frames, out var flags);
                    try
                    {
                        var byteCount = checked(frames * WaveFormat.BlockAlign);
                        var buffer = new byte[byteCount];
                        if ((flags & AudioClientBufferFlags.Silent) == 0 && bufferPointer != IntPtr.Zero)
                        {
                            Marshal.Copy(bufferPointer, buffer, 0, byteCount);
                        }
                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, byteCount));
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(frames);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            RaiseStopped(exception);
        }
    }

    private void DisposeResources()
    {
        _captureClient?.Dispose();
        _captureClient = null;
        _audioClient?.Dispose();
        _audioClient = null;
    }

    private void RaiseStopped(Exception? exception)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) == 0)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(exception));
        }
    }

    private static async Task<AudioClient> ActivateProcessAudioClientAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        var payload = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = checked((uint)processId),
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
            }
        };
        var payloadPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParams>());
        var variantPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<PropVariant>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(payload, payloadPointer, false);
            var variant = new PropVariant
            {
                VariantType = 65, // VT_BLOB
                Blob = new Blob
                {
                    Size = checked((uint)Marshal.SizeOf<AudioClientActivationParams>()),
                    Data = payloadPointer
                }
            };
            Marshal.StructureToPtr(variant, variantPointer, false);

            var handler = new ActivationCompletionHandler();
            var iid = typeof(IAudioClient).GUID;
            var result = ActivateAudioInterfaceAsync(
                ProcessLoopbackVirtualDevice,
                ref iid,
                variantPointer,
                handler,
                out operation);
            Marshal.ThrowExceptionForHR(result);
            using var registration = cancellationToken.Register(handler.Cancel);
            var client = await handler.Task.ConfigureAwait(false);
            GC.KeepAlive(operation);
            GC.KeepAlive(handler);
            return client;
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation))
            {
                Marshal.ReleaseComObject(operation);
            }
            Marshal.FreeCoTaskMem(variantPointer);
            Marshal.FreeCoTaskMem(payloadPointer);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComVisible(true)]
    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<AudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AudioClient> Task => _completion.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var result, out var activatedInterface);
                Marshal.ThrowExceptionForHR(result);
                if (activatedInterface is not IAudioClient audioClient)
                {
                    throw new InvalidCastException("Windows did not return an IAudioClient for process loopback.");
                }
                var client = new AudioClient(audioClient);
                if (!_completion.TrySetResult(client))
                {
                    client.Dispose();
                }
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Cancel() => _completion.TrySetCanceled();
    }

    private enum AudioClientActivationType
    {
        Default,
        ProcessLoopback
    }

    private enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree,
        ExcludeTargetProcessTree
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public Blob Blob;
    }
}
