using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed class AudioCaptureService : IDisposable
{
    private const int RecoveryAttempts = 5;

    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private readonly object _sync = new();
    private readonly List<TimedWaveSource> _completedSourceLegs = [];
    private readonly List<string> _captureWarnings = [];
    private WasapiCapture? _capture;
    private SegmentedWaveCaptureWriter? _writer;
    private CancellationTokenSource? _recoveryCancellation;
    private Task? _recoveryTask;
    private long _sampleCount;
    private double _sumSquares;
    private float _peak;
    private DateTimeOffset _startedAt;
    private string _normalizedFileName = "last-dictation.wav";
    private string _captureName = "dictation";
    private string? _preferredDeviceName;
    private bool _stopRequested;
    private bool _recoveryInProgress;
    private int _activeLegStartOffsetMs;

    public IReadOnlyList<string> ListCaptureDevices()
    {
        var devices = _deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => device.FriendlyName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
            .ToList();

        return devices.Count > 0 ? devices : ["System default microphone"];
    }

    public string PickPreferredDeviceName()
    {
        var devices = ListCaptureDevices();
        return devices.FirstOrDefault(IsPreferredPhysicalMicrophone) ??
               devices.FirstOrDefault(device => device.Contains("microphone", StringComparison.OrdinalIgnoreCase) && !IsLikelyVirtualDevice(device)) ??
               devices.FirstOrDefault() ??
               "System default microphone";
    }

    public Task StartAsync(string? preferredDeviceName, string? captureName = null)
    {
        StopAndCleanup();

        _preferredDeviceName = preferredDeviceName;
        _captureName = string.IsNullOrWhiteSpace(captureName)
            ? $"native-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
            : captureName;
        _normalizedFileName = string.IsNullOrWhiteSpace(captureName)
            ? "last-dictation.wav"
            : $"{captureName}-normalized.wav";
        _sampleCount = 0;
        _sumSquares = 0;
        _peak = 0;
        _startedAt = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            _completedSourceLegs.Clear();
            _captureWarnings.Clear();
            _stopRequested = false;
            _recoveryInProgress = false;
            _recoveryCancellation = new CancellationTokenSource();
            _recoveryTask = null;
        }

        StartCaptureLeg(preferredDeviceName, _captureName, useCommunicationsDefault: false);
        return Task.CompletedTask;
    }

    public async Task<CapturedAudio> StopAsync(bool includeBytes = true)
    {
        Task? recoveryTask;
        lock (_sync)
        {
            if (_capture is null && _writer is null && _completedSourceLegs.Count == 0)
            {
                throw new InvalidOperationException("Audio capture is not running.");
            }

            _stopRequested = true;
            _recoveryCancellation?.Cancel();
            recoveryTask = _recoveryTask;
        }

        FinalizeActiveLeg();
        if (recoveryTask is not null)
        {
            try
            {
                await recoveryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        FinalizeActiveLeg();

        IReadOnlyList<TimedWaveSource> sourceLegs;
        IReadOnlyList<string> sourcePaths;
        string? warning;
        lock (_sync)
        {
            sourceLegs = _completedSourceLegs
                .Select(leg => new TimedWaveSource(
                    leg.Paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    leg.StartOffsetMs))
                .Where(leg => leg.Paths.Count > 0)
                .OrderBy(leg => leg.StartOffsetMs)
                .ToList();
            sourcePaths = sourceLegs
                .SelectMany(leg => leg.Paths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            warning = _captureWarnings.Count == 0
                ? null
                : string.Join(" ", _captureWarnings.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("Audio capture produced no WAV segments.");
        }

        await WaitForFileFlushAsync(sourcePaths[0]);
        var normalizedPath = Path.Combine(CaptureDirectory(), _normalizedFileName);
        WaveFileUtilities.NormalizeToWhisperWav(sourceLegs, normalizedPath);
        var lengthBytes = new FileInfo(normalizedPath).Length;
        var bytes = includeBytes ? await File.ReadAllBytesAsync(normalizedPath) : [];
        var rms = _sampleCount == 0 ? 0 : Math.Sqrt(_sumSquares / _sampleCount);
        var heldMs = (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds;
        return new CapturedAudio(
            string.Join("; ", sourcePaths),
            normalizedPath,
            bytes,
            lengthBytes,
            heldMs,
            rms,
            _peak,
            warning);
    }

    public Task CancelAsync()
    {
        StopAndCleanup();
        List<string> paths;
        lock (_sync)
        {
            paths = _completedSourceLegs.SelectMany(leg => leg.Paths).ToList();
            _completedSourceLegs.Clear();
        }

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Cancel should be best-effort and never throw into the UI.
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAndCleanup();
        _recoveryCancellation?.Dispose();
        _deviceEnumerator.Dispose();
    }

    private void StartCaptureLeg(
        string? preferredDeviceName,
        string captureName,
        bool useCommunicationsDefault)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        SegmentedWaveCaptureWriter? writer = null;
        try
        {
            device = useCommunicationsDefault
                ? _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : PickDevice(preferredDeviceName);
            capture = new WasapiCapture(device)
            {
                ShareMode = AudioClientShareMode.Shared
            };
            writer = new SegmentedWaveCaptureWriter(CaptureDirectory(), captureName, capture.WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            lock (_sync)
            {
                if (_stopRequested)
                {
                    throw new OperationCanceledException("Microphone recovery was cancelled.");
                }
                _capture = capture;
                _writer = writer;
                _activeLegStartOffsetMs = (int)Math.Min(
                    int.MaxValue,
                    Math.Max(0, (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds));
            }

            capture.StartRecording();
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
                if (ReferenceEquals(_writer, writer)) _writer = null;
            }
            if (capture is not null)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
            }
            CompleteWriter(writer);
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        WaveFormat? format;
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _capture))
            {
                return;
            }
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            format = _capture?.WaveFormat;
        }
        AccumulateLevels(e.Buffer, e.BytesRecorded, format);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not WasapiCapture stoppedCapture)
        {
            return;
        }

        SegmentedWaveCaptureWriter? writer;
        int legStartOffsetMs;
        var shouldRecover = false;
        lock (_sync)
        {
            if (!ReferenceEquals(stoppedCapture, _capture))
            {
                return;
            }

            writer = _writer;
            legStartOffsetMs = _activeLegStartOffsetMs;
            _capture = null;
            _writer = null;
            shouldRecover = !_stopRequested;
            if (shouldRecover)
            {
                var detail = e.Exception?.Message;
                _captureWarnings.Add(string.IsNullOrWhiteSpace(detail)
                    ? "Microphone device changed during the meeting."
                    : $"Microphone device changed during the meeting ({detail}).");
                if (!_recoveryInProgress)
                {
                    _recoveryInProgress = true;
                    var token = _recoveryCancellation?.Token ?? CancellationToken.None;
                    _recoveryTask = Task.Run(() => RecoverMicrophoneAsync(token), token);
                }
            }
        }

        CompleteWriter(writer, legStartOffsetMs);
        stoppedCapture.DataAvailable -= OnDataAvailable;
        stoppedCapture.RecordingStopped -= OnRecordingStopped;
        _ = Task.Run(stoppedCapture.Dispose);
    }

    private async Task RecoverMicrophoneAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        try
        {
            for (var attempt = 1; attempt <= RecoveryAttempts; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                lock (_sync)
                {
                    if (_stopRequested)
                    {
                        return;
                    }
                }

                try
                {
                    var useDefault = attempt >= 3;
                    StartCaptureLeg(
                        _preferredDeviceName,
                        $"{_captureName}-recovery{attempt:D2}",
                        useCommunicationsDefault: useDefault);
                    lock (_sync)
                    {
                        if (_capture is not null)
                        {
                            _captureWarnings.Add(useDefault
                                ? "Microphone capture resumed on the current communications device."
                                : "Microphone capture resumed automatically.");
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }
            }

            lock (_sync)
            {
                if (!_stopRequested)
                {
                    _captureWarnings.Add(lastFailure is null
                        ? "Microphone capture failed after a device change and could not resume."
                        : $"Microphone capture failed after a device change and could not resume: {lastFailure.Message}");
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _recoveryInProgress = false;
            }
        }
    }

    private void FinalizeActiveLeg()
    {
        WasapiCapture? capture;
        SegmentedWaveCaptureWriter? writer;
        int legStartOffsetMs;
        lock (_sync)
        {
            capture = _capture;
            writer = _writer;
            legStartOffsetMs = _activeLegStartOffsetMs;
            _capture = null;
            _writer = null;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        CompleteWriter(writer, legStartOffsetMs);
    }

    private void CompleteWriter(SegmentedWaveCaptureWriter? writer, int legStartOffsetMs = 0)
    {
        if (writer is null)
        {
            return;
        }
        var paths = writer.Complete();
        lock (_sync)
        {
            var newPaths = paths
                .Where(path => !_completedSourceLegs
                    .SelectMany(leg => leg.Paths)
                    .Contains(path, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (newPaths.Count > 0)
            {
                _completedSourceLegs.Add(new TimedWaveSource(newPaths, legStartOffsetMs));
            }
        }
    }

    private MMDevice PickDevice(string? preferredDeviceName)
    {
        var devices = _deviceEnumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredDeviceName) &&
            !preferredDeviceName.Equals("System default microphone", StringComparison.OrdinalIgnoreCase))
        {
            var exact = devices.FirstOrDefault(device =>
                device.FriendlyName.Equals(preferredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var fuzzy = devices.FirstOrDefault(device =>
                device.FriendlyName.Contains(preferredDeviceName, StringComparison.OrdinalIgnoreCase) ||
                preferredDeviceName.Contains(device.FriendlyName, StringComparison.OrdinalIgnoreCase));
            if (fuzzy is not null) return fuzzy;
        }

        var preferred = devices.FirstOrDefault(device => IsPreferredPhysicalMicrophone(device.FriendlyName));
        if (preferred is not null) return preferred;

        var physical = devices.FirstOrDefault(device =>
            device.FriendlyName.Contains("microphone", StringComparison.OrdinalIgnoreCase) &&
            !IsLikelyVirtualDevice(device.FriendlyName));
        return physical ?? _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
    }

    private void AccumulateLevels(byte[] buffer, int bytesRecorded, WaveFormat? format)
    {
        if (format is null) return;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= bytesRecorded; offset += 4)
            {
                AddSample(BitConverter.ToSingle(buffer, offset));
            }
            return;
        }

        if (format.BitsPerSample == 16)
        {
            for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
            {
                AddSample(BitConverter.ToInt16(buffer, offset) / 32768f);
            }
        }
    }

    private void AddSample(float sample)
    {
        var absolute = Math.Abs(sample);
        _peak = Math.Max(_peak, absolute);
        _sumSquares += sample * sample;
        _sampleCount++;
    }

    private static async Task WaitForFileFlushAsync(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 44) return;
            }
            catch (IOException)
            {
            }
            await Task.Delay(40);
        }
    }

    private static string CaptureDirectory()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "captures");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsPreferredPhysicalMicrophone(string deviceName) =>
        deviceName.Contains("microphone array", StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains("realtek", StringComparison.OrdinalIgnoreCase);

    private static bool IsLikelyVirtualDevice(string deviceName) =>
        deviceName.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains("streaming", StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains("cable", StringComparison.OrdinalIgnoreCase) ||
        deviceName.Contains("monitor", StringComparison.OrdinalIgnoreCase);

    private void StopAndCleanup()
    {
        Task? recoveryTask;
        lock (_sync)
        {
            _stopRequested = true;
            _recoveryCancellation?.Cancel();
            recoveryTask = _recoveryTask;
        }
        FinalizeActiveLeg();
        if (recoveryTask is not null)
        {
            try { recoveryTask.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        FinalizeActiveLeg();
    }
}

public sealed record CapturedAudio(
    string FilePath,
    string LastCapturePath,
    byte[] Bytes,
    long LengthBytes,
    int HeldMs,
    double Rms,
    float Peak,
    string? Warning = null)
{
    public IReadOnlyList<string> OwnedPaths => FilePath
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Append(LastCapturePath)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
