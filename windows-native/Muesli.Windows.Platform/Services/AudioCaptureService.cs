using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Muesli.Windows.Services;

public sealed class AudioCaptureService : IDisposable
{
    public const string SystemDefaultMicrophone = "System default microphone";

    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly EndpointNotificationClient _notificationClient;
    private readonly List<CaptureSegment> _segments = [];
    private readonly string _captureDirectory;
    private readonly string _segmentPrefix;
    private readonly string _mergedPrefix;
    private CaptureSession? _session;
    private PreparedCapture? _prepared;
    private string? _preferredDeviceName;
    private bool _usingFallback;
    private long _sampleCount;
    private double _sumSquares;
    private float _peak;
    private DateTimeOffset _startedAt;
    private long _lastLevelNotificationTicks;
    private readonly StreamingPcmNormalizer _livePcmNormalizer = new();
    private int _disposed;

    public AudioCaptureService(
        string? captureDirectory = null,
        string? segmentPrefix = null,
        bool cleanupInterruptedDictation = true)
    {
        _captureDirectory = Path.GetFullPath(captureDirectory ?? DefaultCaptureDirectory());
        Directory.CreateDirectory(_captureDirectory);
        _segmentPrefix = string.IsNullOrWhiteSpace(segmentPrefix)
            ? DictationTemporaryAudioPolicy.SegmentPrefix
            : segmentPrefix;
        _mergedPrefix = cleanupInterruptedDictation
            ? DictationTemporaryAudioPolicy.MergedPrefix
            : _segmentPrefix.Equals(MeetingSessionJournalStore.MicrophoneCapturePrefix, StringComparison.Ordinal)
                ? "meeting-mic-merged-"
                : $"{_segmentPrefix}merged-";
        StartupCleanupResult = cleanupInterruptedDictation
            ? DictationTemporaryAudioPolicy.CleanupInterruptedFiles(_captureDirectory)
            : new CaptureCleanupResult(0, []);
        _notificationClient = new EndpointNotificationClient(this);
        _deviceEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
    }

    public CaptureCleanupResult StartupCleanupResult { get; }

    public event EventHandler? DeviceListChanged;
    public event EventHandler<AudioRouteChangedEventArgs>? RouteChanged;
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler<MeetingAudioMetrics>? MetricsAvailable;
    public event EventHandler<CaptureFaultedEventArgs>? CaptureFaulted;
    public event EventHandler<LivePcmSamplesEventArgs>? PcmSamplesAvailable;

    public IReadOnlyList<string> ListCaptureDevices()
    {
        var devices = _deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        return [SystemDefaultMicrophone, .. devices];
    }

    public string PickPreferredDeviceName() => SystemDefaultMicrophone;

    /// <summary>Per-step timing of the most recent <see cref="StartAsync"/>, for latency logs.</summary>
    public string LastStartTiming { get; private set; } = "";

    // NAudio initializes the WASAPI client inside StartRecording; on Realtek arrays that alone took
    // ~650 ms. Calling its idempotent private initializer early leaves StartRecording at ~10 ms.
    // ponytail: reflection into NAudio 2.2.1; on a missing member we fall back to opening on demand.
    private static readonly MethodInfo? InitializeCaptureDevice =
        typeof(WasapiCapture).GetMethod("InitializeCaptureDevice", BindingFlags.NonPublic | BindingFlags.Instance);
    private const int HeadsetFormFactor = 5;
    private const int HandsetFormFactor = 6;

    /// <summary>
    /// Initializes, but does not start, the shared-mode client for the next capture. No audio is
    /// read until <see cref="StartAsync"/>. Headset endpoints are skipped because initializing a
    /// Bluetooth hands-free endpoint can switch the headset into call mode.
    /// </summary>
    public async Task PrepareAsync(string? preferredDeviceName)
    {
        if (InitializeCaptureDevice is null) return;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _session is not null) return;
            var key = NormalizeDeviceKey(preferredDeviceName);
            if (Volatile.Read(ref _prepared)?.Key == key) return;
            DisposePrepared();
            var device = PickDevice(key);
            var formFactor = DefaultRenderRouteInspector.ReadFormFactor(device);
            if (formFactor is HeadsetFormFactor or HandsetFormFactor) return;
            var capture = new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
            try
            {
                InitializeCaptureDevice.Invoke(capture, null);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            Volatile.Write(ref _prepared, new PreparedCapture(key, device, capture));
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string NormalizeDeviceKey(string? preferredDeviceName) =>
        string.IsNullOrWhiteSpace(preferredDeviceName) ? SystemDefaultMicrophone : preferredDeviceName.Trim();

    private void DisposePrepared() => Interlocked.Exchange(ref _prepared, null)?.Capture.Dispose();

    private void StartPreferredSegment(string key)
    {
        if (Interlocked.Exchange(ref _prepared, null) is { } prepared)
        {
            if (prepared.Key == key)
            {
                try
                {
                    StartSegment(prepared.Device, prepared.Capture);
                    LastStartTiming = $"prepared=True; {LastStartTiming}";
                    return;
                }
                catch (Exception exception)
                {
                    // A long-idle client can be invalidated by sleep or a driver reset; reopen once.
                    new AppLogService().Error("Prepared microphone failed to start; reopening.", exception);
                }
            }
            else
            {
                prepared.Capture.Dispose();
            }
        }

        var pickTimer = Stopwatch.StartNew();
        var device = PickDevice(key);
        var pickMs = pickTimer.ElapsedMilliseconds;
        StartSegment(device);
        LastStartTiming = $"prepared=False; pickDeviceMs={pickMs}; {LastStartTiming}";
    }

    public async Task StartAsync(string? preferredDeviceName)
    {
        var entryTimer = Stopwatch.StartNew();
        await Task.Yield();
        var yieldMs = entryTimer.ElapsedMilliseconds;
        await _operationGate.WaitAsync().ConfigureAwait(false);
        var gateMs = entryTimer.ElapsedMilliseconds - yieldMs;
        try
        {
            ThrowIfDisposed();
            var staleSegments = _segments.Count;
            StopAndCleanupUnderGate(deleteFiles: true);
            var cleanupMs = entryTimer.ElapsedMilliseconds - yieldMs - gateMs;
            _preferredDeviceName = NormalizeDeviceKey(preferredDeviceName);
            _usingFallback = false;
            _sampleCount = 0;
            _sumSquares = 0;
            _peak = 0;
            _startedAt = DateTimeOffset.UtcNow;
            _lastLevelNotificationTicks = 0;

            try
            {
                StartPreferredSegment(_preferredDeviceName);
                LastStartTiming = $"yieldMs={yieldMs}; captureGateMs={gateMs}; cleanupMs={cleanupMs}; staleSegments={staleSegments}; {LastStartTiming}";
            }
            catch (Exception preferredException) when (!IsSystemDefault(_preferredDeviceName))
            {
                var requested = _preferredDeviceName;
                try
                {
                    StartSegment(PickDefaultCaptureDevice());
                }
                catch (Exception fallbackException)
                {
                    throw new InvalidOperationException(
                        "The selected microphone failed and the Windows default microphone could not start.",
                        new AggregateException(preferredException, fallbackException));
                }
                _usingFallback = true;
                RaiseRouteChanged(new AudioRouteChangedEventArgs(
                    AudioRouteChangeKind.Fallback,
                    requested ?? "selected microphone",
                    _session?.DeviceName ?? SystemDefaultMicrophone,
                    "The selected microphone is unavailable. Recording is using the current Windows default microphone."));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<CapturedAudio> StopAsync(bool keepLatestDictationAlias = true)
    {
        await Task.Yield();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var stopStarted = Stopwatch.StartNew();
            var current = Interlocked.Exchange(ref _session, null);
            current?.StopAndDispose();
            stopStarted.Stop();

            var segmentPaths = _segments.Select(segment => segment.Path).ToList();
            if (segmentPaths.Count == 0)
            {
                throw new InvalidOperationException("Audio capture is not running and no recoverable audio segment exists.");
            }

            using var failureCleanup = new CaptureFinalizationCleanup(segmentPaths.ToArray());
            var flushStarted = Stopwatch.StartNew();
            foreach (var path in segmentPaths)
            {
                await WaitForFileFlushAsync(path).ConfigureAwait(false);
            }
            flushStarted.Stop();

            var preparationStarted = Stopwatch.StartNew();
            var ownedTransientPaths = new List<string>(segmentPaths);
            var sourcePath = segmentPaths[0];
            var transcriptionPath = sourcePath;
            var preparation = "raw WASAPI capture used directly";
            if (segmentPaths.Count > 1)
            {
                transcriptionPath = Path.Combine(_captureDirectory, $"{_mergedPrefix}{Guid.NewGuid():N}.wav");
                MergeSegments(segmentPaths, transcriptionPath);
                ownedTransientPaths.Add(transcriptionPath);
                failureCleanup.Track(transcriptionPath);
                preparation = $"{segmentPaths.Count} route-safe WASAPI segments normalized to 16 kHz mono";
            }

            var heldMs = Math.Max(1, (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds);
            var byteLength = new FileInfo(transcriptionPath).Length;
            if (keepLatestDictationAlias && BenchmarkCaptureRetentionPolicy.ShouldRetain(byteLength, heldMs))
            {
                var retainedPath = Path.Combine(_captureDirectory, "last-dictation.wav");
                LinkOrCopyCapture(transcriptionPath, retainedPath);
                transcriptionPath = retainedPath;
                byteLength = new FileInfo(transcriptionPath).Length;
                preparation += "; retained as bounded latest-dictation benchmark audio";
            }
            else if (keepLatestDictationAlias)
            {
                CapturedAudio.TryDelete(Path.Combine(_captureDirectory, "last-dictation.wav"));
                preparation += "; benchmark retention skipped because the capture exceeded its privacy bound";
            }
            preparationStarted.Stop();

            var deviceNames = _segments
                .Select(segment => segment.DeviceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var deviceIds = _segments
                .Select(segment => segment.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var rms = _sampleCount == 0 ? 0 : Math.Sqrt(_sumSquares / _sampleCount);
            var captured = new CapturedAudio(
                sourcePath,
                transcriptionPath,
                ownedTransientPaths,
                byteLength,
                heldMs,
                rms,
                _peak,
                string.Join(" -> ", deviceNames),
                string.Join(" -> ", deviceIds),
                segmentPaths.Count,
                (int)stopStarted.ElapsedMilliseconds,
                (int)flushStarted.ElapsedMilliseconds,
                (int)preparationStarted.ElapsedMilliseconds,
                preparation);
            _segments.Clear();
            failureCleanup.Complete();
            return captured;
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
            StopAndCleanupUnderGate(deleteFiles: true);
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
            // Core Audio may already be shutting down.
        }

        _operationGate.Wait();
        try
        {
            DisposePrepared();
            StopAndCleanupUnderGate(deleteFiles: true);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _deviceEnumerator.Dispose();
        }
    }

    private MMDevice PickDevice(string? preferredDeviceName)
    {
        if (IsSystemDefault(preferredDeviceName))
        {
            return PickDefaultCaptureDevice();
        }

        var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        var exact = devices.FirstOrDefault(device =>
            device.FriendlyName.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var unambiguous = devices.Where(device =>
            device.FriendlyName.Contains(preferredDeviceName!, StringComparison.OrdinalIgnoreCase) ||
            preferredDeviceName!.Contains(device.FriendlyName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unambiguous.Count == 1)
        {
            return unambiguous[0];
        }

        throw new AudioDeviceUnavailableException(preferredDeviceName!);
    }

    private MMDevice PickDefaultCaptureDevice() => DefaultCaptureEndpointPolicy.Resolve(_deviceEnumerator);

    private void StartSegment(MMDevice device, WasapiCapture? prepared = null)
    {
        var path = Path.Combine(_captureDirectory, $"{_segmentPrefix}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.wav");
        var stepTimer = Stopwatch.StartNew();
        var capture = prepared ?? new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };
        var clientMs = stepTimer.ElapsedMilliseconds;
        CaptureSession? createdSession = null;
        try
        {
            var writer = new WaveFileWriter(path, capture.WaveFormat);
            var writerMs = stepTimer.ElapsedMilliseconds - clientMs;
            createdSession = new CaptureSession(
                capture,
                writer,
                path,
                device.ID,
                device.FriendlyName,
                AccumulateLevels,
                exception => CaptureFaulted?.Invoke(
                    this,
                    new CaptureFaultedEventArgs(MeetingAudioChannel.Microphone, exception)));
            _segments.Add(new CaptureSegment(path, device.ID, device.FriendlyName));
            _session = createdSession;
            var beforeStartMs = stepTimer.ElapsedMilliseconds;
            capture.StartRecording();
            LastStartTiming = $"wasapiClientMs={clientMs}; writerMs={writerMs}; " +
                $"startRecordingMs={stepTimer.ElapsedMilliseconds - beforeStartMs}; device={device.FriendlyName}";
        }
        catch
        {
            _session = null;
            if (createdSession is not null)
            {
                createdSession.StopAndDispose();
            }
            else
            {
                capture.Dispose();
            }
            _segments.RemoveAll(segment => segment.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            CapturedAudio.TryDelete(path);
            throw;
        }
    }

    private void AccumulateLevels(byte[] buffer, int bytesRecorded, WaveFormat? format)
    {
        if (format is null)
        {
            return;
        }

        var metrics = AudioBufferMetrics.Measure(
            MeetingAudioChannel.Microphone,
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
                LiveTranscriptChannel.Microphone,
                normalized.Samples,
                normalized.StartSample));
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref _lastLevelNotificationTicks);
        if (Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromMilliseconds(50) &&
            Interlocked.CompareExchange(ref _lastLevelNotificationTicks, now, previous) == previous)
        {
            LevelChanged?.Invoke(this, new AudioLevelEventArgs(metrics.Peak, (float)metrics.Rms));
        }
    }

    private void OnDeviceTopologyChanged(AudioEndpointChange change)
    {
        DeviceListChanged?.Invoke(this, EventArgs.Empty);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = Task.Run(() => RecoverRouteAsync(change));
    }

    private async Task RecoverRouteAsync(AudioEndpointChange change)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // The prepared client may now point at a removed or no-longer-default endpoint. Released
            // here, not in the notification callback (where releasing MMDevice objects is unsupported),
            // and under the gate so an in-flight PrepareAsync cannot publish a stale client afterwards.
            DisposePrepared();
            var current = _session;
            if (change.Kind == AudioEndpointChangeKind.Added && _usingFallback &&
                !AddedDeviceMatchesPreference(change.DeviceId))
            {
                return;
            }
            if (current is null || !AudioRouteRecoveryPolicy.ShouldRotate(
                    _preferredDeviceName,
                    current.DeviceId,
                    _usingFallback,
                    change))
            {
                return;
            }

            var previousName = current.DeviceName;
            current.StopAndDispose();
            _session = null;
            await WaitForFileFlushAsync(current.Path).ConfigureAwait(false);

            try
            {
                var replacement = PickDevice(_preferredDeviceName);
                StartSegment(replacement);
                _usingFallback = false;
                RaiseRouteChanged(new AudioRouteChangedEventArgs(
                    AudioRouteChangeKind.Recovered,
                    previousName,
                    replacement.FriendlyName,
                    "Microphone route changed. Recording continued in a new loss-bounded segment."));
            }
            catch (Exception preferredException) when (!IsSystemDefault(_preferredDeviceName))
            {
                try
                {
                    var fallback = PickDefaultCaptureDevice();
                    StartSegment(fallback);
                    _usingFallback = true;
                    RaiseRouteChanged(new AudioRouteChangedEventArgs(
                        AudioRouteChangeKind.Fallback,
                        previousName,
                        fallback.FriendlyName,
                        "The selected microphone disconnected. Recording continued on the Windows default microphone."));
                }
                catch (Exception fallbackException)
                {
                    RaiseRouteChanged(new AudioRouteChangedEventArgs(
                        AudioRouteChangeKind.Failed,
                        previousName,
                        "unavailable",
                        $"Microphone disconnected and recovery failed: {fallbackException.Message}",
                        new AggregateException(preferredException, fallbackException)));
                }
            }
            catch (Exception exception)
            {
                RaiseRouteChanged(new AudioRouteChangedEventArgs(
                    AudioRouteChangeKind.Failed,
                    previousName,
                    "unavailable",
                    $"The Windows default microphone changed, but recording could not continue: {exception.Message}",
                    exception));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void RaiseRouteChanged(AudioRouteChangedEventArgs args) => RouteChanged?.Invoke(this, args);

    private bool AddedDeviceMatchesPreference(string deviceId)
    {
        if (IsSystemDefault(_preferredDeviceName))
        {
            return false;
        }
        try
        {
            var device = _deviceEnumerator.GetDevice(deviceId);
            return device.FriendlyName.Equals(_preferredDeviceName, StringComparison.OrdinalIgnoreCase) ||
                   device.FriendlyName.Contains(_preferredDeviceName!, StringComparison.OrdinalIgnoreCase) ||
                   _preferredDeviceName!.Contains(device.FriendlyName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void StopAndCleanupUnderGate(bool deleteFiles)
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.StopAndDispose();
        if (deleteFiles)
        {
            foreach (var segment in _segments)
            {
                CapturedAudio.TryDelete(segment.Path);
            }
        }
        _segments.Clear();
    }

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
                // Recorder is still closing its writer.
            }
            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    private static void MergeSegments(IReadOnlyList<string> paths, string destinationPath)
    {
        var readers = new List<AudioFileReader>();
        try
        {
            var providers = new List<ISampleProvider>();
            foreach (var path in paths)
            {
                var reader = new AudioFileReader(path);
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

    private static void LinkOrCopyCapture(string sourcePath, string destinationPath)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!CreateHardLink(temporaryPath, sourcePath, IntPtr.Zero))
            {
                File.Copy(sourcePath, temporaryPath, overwrite: true);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            CapturedAudio.TryDelete(temporaryPath);
        }
    }

    private static string DefaultCaptureDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "muesli", "captures");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static bool IsSystemDefault(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Equals(SystemDefaultMicrophone, StringComparison.OrdinalIgnoreCase);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CaptureSegment(string Path, string DeviceId, string DeviceName);

    private sealed class CaptureSession
    {
        private readonly object _writerGate = new();
        private readonly Action<byte[], int, WaveFormat?> _accumulateLevels;
        private readonly Action<Exception> _captureFaulted;
        private WaveFileWriter? _writer;
        private int _stopped;

        public CaptureSession(
            WasapiCapture capture,
            WaveFileWriter writer,
            string path,
            string deviceId,
            string deviceName,
            Action<byte[], int, WaveFormat?> accumulateLevels,
            Action<Exception> captureFaulted)
        {
            Capture = capture;
            _writer = writer;
            Path = path;
            DeviceId = deviceId;
            DeviceName = deviceName;
            _accumulateLevels = accumulateLevels;
            _captureFaulted = captureFaulted;
            Capture.DataAvailable += OnDataAvailable;
            Capture.RecordingStopped += OnRecordingStopped;
        }

        public WasapiCapture Capture { get; }
        public string Path { get; }
        public string DeviceId { get; }
        public string DeviceName { get; }

        private void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            lock (_writerGate)
            {
                _writer?.Write(args.Buffer, 0, args.BytesRecorded);
            }
            _accumulateLevels(args.Buffer, args.BytesRecorded, Capture.WaveFormat);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            DisposeWriter();
            if (Volatile.Read(ref _stopped) == 0)
            {
                _captureFaulted(args.Exception ?? new InvalidOperationException("Microphone capture stopped unexpectedly."));
            }
        }

        public void StopAndDispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }
            try
            {
                Capture.StopRecording();
            }
            finally
            {
                Capture.DataAvailable -= OnDataAvailable;
                Capture.RecordingStopped -= OnRecordingStopped;
                Capture.Dispose();
                DisposeWriter();
            }
        }

        private void DisposeWriter()
        {
            lock (_writerGate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private sealed class EndpointNotificationClient(AudioCaptureService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
            owner.OnDeviceTopologyChanged(new AudioEndpointChange(AudioEndpointChangeKind.StateChanged, deviceId, newState));

        public void OnDeviceAdded(string pwstrDeviceId) =>
            owner.OnDeviceTopologyChanged(new AudioEndpointChange(AudioEndpointChangeKind.Added, pwstrDeviceId));

        public void OnDeviceRemoved(string deviceId) =>
            owner.OnDeviceTopologyChanged(new AudioEndpointChange(AudioEndpointChangeKind.Removed, deviceId));

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow is DataFlow.Capture or DataFlow.All)
            {
                owner.OnDeviceTopologyChanged(new AudioEndpointChange(
                    AudioEndpointChangeKind.DefaultChanged,
                    defaultDeviceId,
                    DefaultRole: role));
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) =>
            owner.DeviceListChanged?.Invoke(owner, EventArgs.Empty);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

public static class AudioRouteRecoveryPolicy
{
    public static bool ShouldRotate(
        string? preferredDeviceName,
        string currentDeviceId,
        bool usingFallback,
        AudioEndpointChange change)
    {
        // WASAPI raises a default-device notification for each role. A notification can
        // describe the endpoint that is already recording (especially when Multimedia and
        // Console are changed together); rotating in that case creates an unnecessary segment
        // and can make a short dictation look like a route-loss recovery.
        if (change.Kind == AudioEndpointChangeKind.DefaultChanged &&
            change.DeviceId.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return change.Kind switch
        {
            AudioEndpointChangeKind.DefaultChanged =>
                (AudioCaptureService.IsSystemDefault(preferredDeviceName) || usingFallback)
                && DefaultCaptureEndpointPolicy.ShouldFollowDefaultChange(change.DefaultRole),
            AudioEndpointChangeKind.Removed => change.DeviceId.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase),
            AudioEndpointChangeKind.StateChanged =>
                change.DeviceId.Equals(currentDeviceId, StringComparison.OrdinalIgnoreCase) &&
                change.State is DeviceState.Disabled or DeviceState.NotPresent or DeviceState.Unplugged,
            AudioEndpointChangeKind.Added => usingFallback && !AudioCaptureService.IsSystemDefault(preferredDeviceName),
            _ => false
        };
    }
}

public enum AudioEndpointChangeKind { Added, Removed, StateChanged, DefaultChanged }
public sealed record AudioEndpointChange(
    AudioEndpointChangeKind Kind,
    string DeviceId,
    DeviceState State = DeviceState.Active,
    Role? DefaultRole = null);
public enum AudioRouteChangeKind { Recovered, Fallback, Failed }
public sealed class AudioLevelEventArgs(float peak, float rms = 0) : EventArgs
{
    public float Peak { get; } = peak;

    /// <summary>Linear RMS of the same buffer; the floating indicator meters average power.</summary>
    public float Rms { get; } = rms;
}

public sealed class AudioRouteChangedEventArgs(
    AudioRouteChangeKind kind,
    string previousDevice,
    string currentDevice,
    string message,
    Exception? exception = null) : EventArgs
{
    public AudioRouteChangeKind Kind { get; } = kind;
    public string PreviousDevice { get; } = previousDevice;
    public string CurrentDevice { get; } = currentDevice;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}

public sealed class CaptureFaultedEventArgs(
    MeetingAudioChannel channel,
    Exception exception) : EventArgs
{
    public MeetingAudioChannel Channel { get; } = channel;
    public Exception Exception { get; } = exception;
}

public sealed class AudioDeviceUnavailableException(string deviceName)
    : InvalidOperationException($"The selected microphone '{deviceName}' is not currently available.");

public sealed class CapturedAudio : ICapturedAudioArtifact, IDisposable
{
    private int _cleaned;

    public CapturedAudio(
        string sourcePath,
        string transcriptionPath,
        IReadOnlyList<string> ownedTransientPaths,
        long byteLength,
        int heldMs,
        double rms,
        float peak,
        string deviceName,
        string deviceId,
        int routeSegmentCount = 1,
        int stopDisposeMs = 0,
        int flushWaitMs = 0,
        int preparationMs = 0,
        string preparation = "not measured")
    {
        SourcePath = sourcePath;
        TranscriptionPath = transcriptionPath;
        OwnedTransientPaths = ownedTransientPaths;
        ByteLength = byteLength;
        HeldMs = heldMs;
        Rms = rms;
        Peak = peak;
        DeviceName = deviceName;
        DeviceId = deviceId;
        RouteSegmentCount = routeSegmentCount;
        StopDisposeMs = stopDisposeMs;
        FlushWaitMs = flushWaitMs;
        PreparationMs = preparationMs;
        Preparation = preparation;
    }

    public string SourcePath { get; }
    public string TranscriptionPath { get; }
    public IReadOnlyList<string> OwnedTransientPaths { get; }
    public long ByteLength { get; }
    public int HeldMs { get; }
    public double Rms { get; }
    public float Peak { get; }
    public string DeviceName { get; }
    public string DeviceId { get; }
    public int RouteSegmentCount { get; }
    public int StopDisposeMs { get; }
    public int FlushWaitMs { get; }
    public int PreparationMs { get; }
    public string Preparation { get; }
    public string FilePath => SourcePath;
    public string LastCapturePath => TranscriptionPath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _cleaned, 1) != 0)
        {
            return;
        }
        foreach (var path in OwnedTransientPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryDelete(path);
        }
    }

    internal static void TryDelete(string path)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(25 * attempt);
            }
            catch
            {
                return;
            }
        }
    }
}

internal sealed class CaptureFinalizationCleanup : IDisposable
{
    private readonly HashSet<string> _ownedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    public CaptureFinalizationCleanup(params string[] ownedPaths)
    {
        foreach (var path in ownedPaths)
        {
            Track(path);
        }
    }

    public void Track(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _ownedPaths.Add(Path.GetFullPath(path));
        }
    }

    public void Complete() => _completed = true;

    public void Dispose()
    {
        if (_completed)
        {
            return;
        }
        foreach (var path in _ownedPaths)
        {
            CapturedAudio.TryDelete(path);
        }
    }
}

internal sealed record PreparedCapture(string Key, MMDevice Device, WasapiCapture Capture);
