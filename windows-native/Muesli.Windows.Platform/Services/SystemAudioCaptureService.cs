using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Muesli.Windows.Services;

public enum SystemAudioCaptureMode
{
    ProcessTreeLoopback,
    EndpointLoopback
}

public sealed record SystemAudioCaptureStartResult(
    SystemAudioCaptureMode Mode,
    int? TargetProcessId,
    bool UsedFallback,
    string? Warning,
    string EndpointName);

public sealed class SystemAudioCaptureService : IDisposable
{
    private readonly string _captureDirectory;
    private readonly string _segmentPrefix;
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly EndpointNotificationClient _notificationClient;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly List<CaptureSegment> _segments = [];
    private CaptureSession? _session;
    private DateTimeOffset _startedAt;
    private long _sampleCount;
    private double _sumSquares;
    private float _peak;
    private readonly StreamingPcmNormalizer _livePcmNormalizer = new();
    private int _disposed;

    public SystemAudioCaptureService(
        string? captureDirectory = null,
        string? segmentPrefix = null)
    {
        _captureDirectory = Path.GetFullPath(captureDirectory ?? DefaultCaptureDirectory());
        Directory.CreateDirectory(_captureDirectory);
        _segmentPrefix = string.IsNullOrWhiteSpace(segmentPrefix)
            ? MeetingSessionJournalStore.SystemCapturePrefix
            : segmentPrefix;
        _notificationClient = new EndpointNotificationClient(this);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    public SystemAudioCaptureMode? CurrentMode { get; private set; }
    public event EventHandler<AudioRouteChangedEventArgs>? RouteChanged;
    public event EventHandler<MeetingAudioMetrics>? MetricsAvailable;
    public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;
    public event EventHandler<LivePcmSamplesEventArgs>? PcmSamplesAvailable;

    public async Task<SystemAudioCaptureStartResult> StartAsync(
        int? targetProcessId = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopAndCleanupUnderGateAsync(deleteFiles: true).ConfigureAwait(false);
            _startedAt = DateTimeOffset.UtcNow;
            _sampleCount = 0;
            _sumSquares = 0;
            _peak = 0;

            var capability = WindowsProcessLoopbackSupport.Inspect(targetProcessId);
            if (capability.CanAttempt && targetProcessId is > 0)
            {
                try
                {
                    await StartProcessSessionAsync(targetProcessId.Value, cancellationToken).ConfigureAwait(false);
                    CurrentMode = SystemAudioCaptureMode.ProcessTreeLoopback;
                    return new SystemAudioCaptureStartResult(
                        CurrentMode.Value,
                        targetProcessId,
                        false,
                        null,
                        "Target process tree");
                }
                catch (Exception processLoopbackFailure) when (processLoopbackFailure is not OperationCanceledException)
                {
                    var endpoint = PickDefaultRenderDevice();
                    await StartEndpointSegmentAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    CurrentMode = SystemAudioCaptureMode.EndpointLoopback;
                    return new SystemAudioCaptureStartResult(
                        CurrentMode.Value,
                        targetProcessId,
                        true,
                        "Windows rejected process-targeted capture. Muesli is using default-output loopback, so unrelated system sounds may be included.",
                        endpoint.FriendlyName);
                }
            }

            var fallbackEndpoint = PickDefaultRenderDevice();
            await StartEndpointSegmentAsync(fallbackEndpoint, cancellationToken).ConfigureAwait(false);
            CurrentMode = SystemAudioCaptureMode.EndpointLoopback;
            return new SystemAudioCaptureStartResult(
                CurrentMode.Value,
                targetProcessId,
                targetProcessId is > 0,
                targetProcessId is > 0
                    ? $"{capability.Diagnostic} Muesli is using default-output loopback, so unrelated system sounds may be included."
                    : "Muesli is capturing the default Windows output mix. Unrelated system sounds can be included because no meeting process was identified.",
                fallbackEndpoint.FriendlyName);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CapturedAudio> StopAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var session = Interlocked.Exchange(ref _session, null);
            if (session is not null)
            {
                await session.StopAndDisposeAsync().ConfigureAwait(false);
            }

            var segmentPaths = _segments.Select(segment => segment.Path).ToList();
            if (segmentPaths.Count == 0)
            {
                throw new InvalidOperationException("System audio capture has no recoverable segment.");
            }

            using var failureCleanup = new CaptureFinalizationCleanup(segmentPaths.ToArray());
            foreach (var path in segmentPaths)
            {
                await WaitForFileFlushAsync(path).ConfigureAwait(false);
            }

            var normalizedPath = Path.Combine(
                _captureDirectory,
                $"meeting-system-normalized-{Guid.NewGuid():N}.wav");
            failureCleanup.Track(normalizedPath);
            NormalizeAndMerge(segmentPaths, normalizedPath);
            var byteLength = new FileInfo(normalizedPath).Length;
            var heldMs = Math.Max(1, (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds);
            var capturedAudio = new CapturedAudio(
                segmentPaths[0],
                normalizedPath,
                [.. segmentPaths, normalizedPath],
                byteLength,
                heldMs,
                _sampleCount == 0 ? 0 : Math.Sqrt(_sumSquares / _sampleCount),
                _peak,
                string.Join(" -> ", _segments.Select(segment => segment.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase)),
                string.Join(" -> ", _segments.Select(segment => segment.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase)),
                segmentPaths.Count,
                preparation: $"{segmentPaths.Count} system-audio segment(s) normalized to 16 kHz mono");
            _segments.Clear();
            failureCleanup.Complete();
            return capturedAudio;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CancelAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAndCleanupUnderGateAsync(deleteFiles: true).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _deviceEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        }
        catch
        {
            // Core Audio can already be shutting down.
        }

        _operationGate.Wait();
        try
        {
            StopAndCleanupUnderGateAsync(deleteFiles: true).GetAwaiter().GetResult();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _deviceEnumerator.Dispose();
        }
    }

    private async Task StartProcessSessionAsync(int processId, CancellationToken cancellationToken)
    {
        var source = new ProcessCaptureSource(processId);
        var path = NewSegmentPath();
        var session = CreateSession(source, path, "process-tree", $"pid:{processId}");
        try
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            _segments.Add(new CaptureSegment(path, $"pid:{processId}", "Target process tree"));
            _session = session;
        }
        catch
        {
            await session.StopAndDisposeAsync().ConfigureAwait(false);
            CapturedAudio.TryDelete(path);
            throw;
        }
    }

    private async Task StartEndpointSegmentAsync(MMDevice device, CancellationToken cancellationToken)
    {
        var source = new EndpointCaptureSource(device);
        var path = NewSegmentPath();
        var session = CreateSession(source, path, device.FriendlyName, device.ID);
        try
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            _segments.Add(new CaptureSegment(path, device.ID, device.FriendlyName));
            _session = session;
        }
        catch
        {
            await session.StopAndDisposeAsync().ConfigureAwait(false);
            CapturedAudio.TryDelete(path);
            throw;
        }
    }

    private CaptureSession CreateSession(
        ISystemCaptureSource source,
        string path,
        string deviceName,
        string deviceId) => new(
            source,
            path,
            deviceName,
            deviceId,
            OnDataAvailable,
            exception => CaptureFaulted?.Invoke(
                this,
                new CaptureFaultedEventArgs(MeetingAudioChannel.System, exception)));

    private void OnDataAvailable(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var metrics = AudioBufferMetrics.Measure(
            MeetingAudioChannel.System,
            buffer,
            bytesRecorded,
            format);
        _sampleCount += metrics.SampleCount;
        _sumSquares += metrics.Rms * metrics.Rms * metrics.SampleCount;
        _peak = Math.Max(_peak, metrics.Peak);
        MetricsAvailable?.Invoke(this, metrics);
        var normalized = _livePcmNormalizer.Convert(buffer, bytesRecorded, format);
        if (normalized.Samples.Length > 0)
        {
            PcmSamplesAvailable?.Invoke(this, new LivePcmSamplesEventArgs(
                LiveTranscriptChannel.System,
                normalized.Samples,
                normalized.StartSample));
        }
    }

    private void OnEndpointChanged(AudioEndpointChange change)
    {
        if (Volatile.Read(ref _disposed) != 0 || CurrentMode != SystemAudioCaptureMode.EndpointLoopback)
        {
            return;
        }
        _ = Task.Run(() => RecoverEndpointRouteAsync(change));
    }

    private async Task RecoverEndpointRouteAsync(AudioEndpointChange change)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var current = _session;
            if (current is null || !SystemAudioRouteRecoveryPolicy.ShouldRotate(current.DeviceId, change))
            {
                return;
            }

            var previousName = current.DeviceName;
            await current.StopAndDisposeAsync().ConfigureAwait(false);
            _session = null;
            await WaitForFileFlushAsync(current.Path).ConfigureAwait(false);
            try
            {
                var replacement = PickDefaultRenderDevice();
                await StartEndpointSegmentAsync(replacement, CancellationToken.None).ConfigureAwait(false);
                RouteChanged?.Invoke(this, new AudioRouteChangedEventArgs(
                    AudioRouteChangeKind.Recovered,
                    previousName,
                    replacement.FriendlyName,
                    "Windows output route changed. System-audio capture continued in a new segment."));
            }
            catch (Exception exception)
            {
                RouteChanged?.Invoke(this, new AudioRouteChangedEventArgs(
                    AudioRouteChangeKind.Failed,
                    previousName,
                    "unavailable",
                    "System-audio output route changed and capture could not restart.",
                    exception));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private MMDevice PickDefaultRenderDevice()
    {
        try
        {
            return _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        }
        catch
        {
            return _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
    }

    private async Task StopAndCleanupUnderGateAsync(bool deleteFiles)
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await session.StopAndDisposeAsync().ConfigureAwait(false);
        }
        if (deleteFiles)
        {
            foreach (var segment in _segments)
            {
                CapturedAudio.TryDelete(segment.Path);
            }
        }
        _segments.Clear();
        CurrentMode = null;
    }

    private string NewSegmentPath() => Path.Combine(
        _captureDirectory,
        $"{_segmentPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.wav");

    private static async Task WaitForFileFlushAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 44)
                {
                    return;
                }
            }
            catch (IOException)
            {
            }
            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    private static void NormalizeAndMerge(IReadOnlyList<string> sourcePaths, string destinationPath)
    {
        var readers = new List<AudioFileReader>();
        try
        {
            var providers = new List<ISampleProvider>();
            foreach (var sourcePath in sourcePaths)
            {
                var reader = new AudioFileReader(sourcePath);
                readers.Add(reader);
                ISampleProvider provider = reader;
                if (provider.WaveFormat.Channels == 2)
                {
                    provider = new StereoToMonoSampleProvider(provider);
                }
                else if (provider.WaveFormat.Channels > 2)
                {
                    var mono = new MultiplexingSampleProvider([provider], 1);
                    mono.ConnectInputToOutput(0, 0);
                    provider = mono;
                }
                if (provider.WaveFormat.SampleRate != 16000)
                {
                    provider = new WdlResamplingSampleProvider(provider, 16000);
                }
                providers.Add(provider);
            }
            WaveFileWriter.CreateWaveFile16(destinationPath, new ConcatenatingSampleProvider(providers));
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static string DefaultCaptureDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "captures");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CaptureSegment(string Path, string DeviceId, string DeviceName);

    private interface ISystemCaptureSource : IDisposable
    {
        WaveFormat WaveFormat { get; }
        event EventHandler<WaveInEventArgs>? DataAvailable;
        event EventHandler<StoppedEventArgs>? RecordingStopped;
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
    }

    private sealed class EndpointCaptureSource : ISystemCaptureSource
    {
        private readonly WasapiLoopbackCapture _capture;

        public EndpointCaptureSource(MMDevice device)
        {
            _capture = new WasapiLoopbackCapture(device);
            _capture.DataAvailable += ForwardData;
            _capture.RecordingStopped += ForwardStopped;
        }

        public WaveFormat WaveFormat => _capture.WaveFormat;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _capture.StartRecording();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _capture.StopRecording();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _capture.DataAvailable -= ForwardData;
            _capture.RecordingStopped -= ForwardStopped;
            _capture.Dispose();
        }

        private void ForwardData(object? sender, WaveInEventArgs args) => DataAvailable?.Invoke(this, args);
        private void ForwardStopped(object? sender, StoppedEventArgs args) => RecordingStopped?.Invoke(this, args);
    }

    private sealed class ProcessCaptureSource : ISystemCaptureSource
    {
        private readonly WindowsProcessLoopbackCapture _capture;

        public ProcessCaptureSource(int processId)
        {
            _capture = new WindowsProcessLoopbackCapture(processId);
            _capture.DataAvailable += ForwardData;
            _capture.RecordingStopped += ForwardStopped;
        }

        public WaveFormat WaveFormat => _capture.WaveFormat;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public Task StartAsync(CancellationToken cancellationToken) => _capture.StartAsync(cancellationToken);
        public Task StopAsync() => _capture.StopAsync();

        public void Dispose()
        {
            _capture.DataAvailable -= ForwardData;
            _capture.RecordingStopped -= ForwardStopped;
            _capture.Dispose();
        }

        private void ForwardData(object? sender, WaveInEventArgs args) => DataAvailable?.Invoke(this, args);
        private void ForwardStopped(object? sender, StoppedEventArgs args) => RecordingStopped?.Invoke(this, args);
    }

    private sealed class CaptureSession
    {
        private readonly object _writerGate = new();
        private readonly ISystemCaptureSource _source;
        private readonly Action<byte[], int, WaveFormat> _observe;
        private readonly Action<Exception> _captureFaulted;
        private WaveFileWriter? _writer;
        private int _stopping;

        public CaptureSession(
            ISystemCaptureSource source,
            string path,
            string deviceName,
            string deviceId,
            Action<byte[], int, WaveFormat> observe,
            Action<Exception> captureFaulted)
        {
            _source = source;
            Path = path;
            DeviceName = deviceName;
            DeviceId = deviceId;
            _observe = observe;
            _captureFaulted = captureFaulted;
            source.DataAvailable += OnDataAvailable;
            source.RecordingStopped += OnRecordingStopped;
        }

        public string Path { get; }
        public string DeviceName { get; }
        public string DeviceId { get; }

        public async Task StopAndDisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }
            try
            {
                await _source.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                _source.DataAvailable -= OnDataAvailable;
                _source.RecordingStopped -= OnRecordingStopped;
                lock (_writerGate)
                {
                    if (_writer is null && !File.Exists(Path))
                    {
                        _writer = new WaveFileWriter(Path, _source.WaveFormat);
                    }
                    _writer?.Dispose();
                    _writer = null;
                }
                _source.Dispose();
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            lock (_writerGate)
            {
                _writer ??= new WaveFileWriter(Path, _source.WaveFormat);
                _writer.Write(args.Buffer, 0, args.BytesRecorded);
            }
            _observe(args.Buffer, args.BytesRecorded, _source.WaveFormat);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            lock (_writerGate)
            {
                _writer?.Dispose();
                _writer = null;
            }
            if (Volatile.Read(ref _stopping) == 0)
            {
                _captureFaulted(args.Exception ?? new InvalidOperationException("System-audio capture stopped unexpectedly."));
            }
        }
    }

    private sealed class EndpointNotificationClient(SystemAudioCaptureService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            owner.OnEndpointChanged(new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, deviceId, newState));
        public void OnDeviceAdded(string pwstrDeviceId) =>
            owner.OnEndpointChanged(new AudioEndpointChange(AudioEndpointChangeKind.Added, pwstrDeviceId));
        public void OnDeviceRemoved(string deviceId) =>
            owner.OnEndpointChanged(new AudioEndpointChange(AudioEndpointChangeKind.Removed, deviceId));
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow is DataFlow.Render or DataFlow.All)
            {
                owner.OnEndpointChanged(new AudioEndpointChange(AudioEndpointChangeKind.DefaultChanged, defaultDeviceId));
            }
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

public static class SystemAudioRouteRecoveryPolicy
{
    public static bool ShouldRotate(string currentDeviceId, AudioEndpointChange change) => change.Kind switch
    {
        AudioEndpointChangeKind.DefaultChanged => true,
        AudioEndpointChangeKind.Removed => change.DeviceId.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase),
        AudioEndpointChangeKind.StateChanged =>
            change.DeviceId.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase) &&
            change.State is DeviceState.Disabled or DeviceState.NotPresent or DeviceState.Unplugged,
        _ => false
    };
}
