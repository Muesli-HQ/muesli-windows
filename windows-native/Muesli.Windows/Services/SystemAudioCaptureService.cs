using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed class SystemAudioCaptureService : IDisposable
{
    private static readonly TimeSpan ProcessQualificationWindow = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MMDeviceEnumerator _deviceEnumerator = new();
    private WasapiLoopbackCapture? _endpointCapture;
    private MMDevice? _endpointDevice;
    private WindowsProcessLoopbackCapture? _processCapture;
    private SegmentedWaveCaptureWriter? _endpointWriter;
    private SegmentedWaveCaptureWriter? _processWriter;
    private CancellationTokenSource? _qualificationCancellation;
    private Task? _qualificationTask;
    private DateTimeOffset _startedAt;
    private string _normalizedFileName = "last-meeting-system.wav";
    private string? _captureWarning;
    private int _processAudible;
    private bool _stopping;

    public event EventHandler<string>? FallbackActivated;

    public async Task<SystemAudioCaptureStartResult> StartAsync(int? targetProcessId, string captureName)
    {
        await StopAndCleanupAsync();
        _normalizedFileName = $"{captureName}-normalized.wav";
        _startedAt = DateTimeOffset.UtcNow;
        _captureWarning = null;
        _processAudible = 0;
        _stopping = false;

        var capability = WindowsProcessLoopbackSupport.Inspect(targetProcessId);
        if (!capability.CanAttempt)
        {
            var warning = $"{capability.Diagnostic} All system audio is being captured as a fallback.";
            StartEndpointCapture(captureName, probe: false);
            _captureWarning = warning;
            return new SystemAudioCaptureStartResult("all-system-fallback", targetProcessId, true, warning);
        }

        try
        {
            var processCapture = new WindowsProcessLoopbackCapture(targetProcessId!.Value);
            _processCapture = processCapture;
            _processWriter = new SegmentedWaveCaptureWriter(
                CaptureDirectory(),
                $"{captureName}-process",
                processCapture.WaveFormat);
            processCapture.DataAvailable += OnProcessDataAvailable;
            processCapture.RecordingStopped += OnProcessRecordingStopped;
            await processCapture.StartAsync();
        }
        catch (Exception exception)
        {
            await StopProcessCaptureAsync(deleteFiles: true);
            var warning = $"Meeting-process audio capture could not start ({exception.Message}); all system audio is being captured as a fallback.";
            StartEndpointCapture(captureName, probe: false);
            _captureWarning = warning;
            return new SystemAudioCaptureStartResult("all-system-fallback", targetProcessId, true, warning);
        }

        try
        {
            StartEndpointCapture(captureName, probe: true);
        }
        catch (Exception exception)
        {
            _captureWarning = $"The meeting-process track is active, but its all-system safety probe could not start: {exception.Message}";
        }

        _qualificationCancellation = new CancellationTokenSource();
        _qualificationTask = QualifyProcessCaptureAsync(_qualificationCancellation.Token);
        return new SystemAudioCaptureStartResult("meeting-process", targetProcessId, false, _captureWarning);
    }

    public async Task<CapturedAudio> StopAsync()
    {
        _stopping = true;
        _qualificationCancellation?.Cancel();
        if (_qualificationTask is not null)
        {
            try
            {
                await _qualificationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _gate.WaitAsync();
        IReadOnlyList<string> selectedPaths;
        try
        {
            if (_processCapture is not null && _endpointCapture is not null)
            {
                if (Volatile.Read(ref _processAudible) != 0)
                {
                    await StopEndpointCaptureAsync(deleteFiles: true);
                    selectedPaths = await StopProcessCaptureAsync(deleteFiles: false);
                }
                else
                {
                    await StopProcessCaptureAsync(deleteFiles: true);
                    selectedPaths = await StopEndpointCaptureAsync(deleteFiles: false);
                    _captureWarning = ProcessSilenceFallbackWarning();
                }
            }
            else if (_processCapture is not null)
            {
                selectedPaths = await StopProcessCaptureAsync(deleteFiles: false);
            }
            else if (_endpointCapture is not null)
            {
                selectedPaths = await StopEndpointCaptureAsync(deleteFiles: false);
            }
            else
            {
                throw new InvalidOperationException("System audio capture is not running.");
            }
        }
        finally
        {
            _gate.Release();
        }

        selectedPaths = selectedPaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedPaths.Count == 0)
        {
            throw new InvalidOperationException("System audio capture produced no WAV segments.");
        }

        await WaitForFileFlushAsync(selectedPaths[0]);
        var normalizedPath = Path.Combine(CaptureDirectory(), _normalizedFileName);
        WaveFileUtilities.NormalizeToWhisperWav(selectedPaths, normalizedPath);
        var lengthBytes = new FileInfo(normalizedPath).Length;
        var heldMs = (int)(DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds;
        return new CapturedAudio(
            string.Join("; ", selectedPaths),
            normalizedPath,
            [],
            lengthBytes,
            heldMs,
            0,
            0,
            _captureWarning);
    }

    public void Dispose()
    {
        StopAndCleanupAsync().GetAwaiter().GetResult();
        _qualificationCancellation?.Dispose();
        _deviceEnumerator.Dispose();
        _gate.Dispose();
    }

    internal static bool ContainsAudiblePcm16(byte[] buffer, int bytesRecorded, int threshold = 64)
    {
        var audibleSamples = 0;
        for (var offset = 0; offset + 2 <= bytesRecorded; offset += 2)
        {
            if (Math.Abs((int)BitConverter.ToInt16(buffer, offset)) <= threshold)
            {
                continue;
            }
            if (++audibleSamples >= 16)
            {
                return true;
            }
        }
        return false;
    }

    private void StartEndpointCapture(string captureName, bool probe)
    {
        MMDevice? device = null;
        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        }
        catch
        {
            // WasapiLoopbackCapture() will use the default render endpoint.
        }

        var capture = device is null
            ? new WasapiLoopbackCapture()
            : new WasapiLoopbackCapture(device);
        var suffix = probe ? "safety" : "fallback";
        var writer = new SegmentedWaveCaptureWriter(
            CaptureDirectory(),
            $"{captureName}-{suffix}",
            capture.WaveFormat);
        _endpointDevice = device;
        _endpointCapture = capture;
        _endpointWriter = writer;
        capture.DataAvailable += OnEndpointDataAvailable;
        capture.RecordingStopped += OnEndpointRecordingStopped;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnEndpointDataAvailable;
            capture.RecordingStopped -= OnEndpointRecordingStopped;
            capture.Dispose();
            device?.Dispose();
            DeletePaths(writer.Complete());
            _endpointCapture = null;
            _endpointDevice = null;
            _endpointWriter = null;
            throw;
        }
    }

    private async Task QualifyProcessCaptureAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(ProcessQualificationWindow, cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping || _processCapture is null || _endpointCapture is null)
            {
                return;
            }

            if (Volatile.Read(ref _processAudible) != 0)
            {
                await StopEndpointCaptureAsync(deleteFiles: true);
                return;
            }

            await StopProcessCaptureAsync(deleteFiles: true);
            _captureWarning = ProcessSilenceFallbackWarning();
            FallbackActivated?.Invoke(this, _captureWarning);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnProcessDataAvailable(object? sender, WaveInEventArgs args)
    {
        _processWriter?.Write(args.Buffer, 0, args.BytesRecorded);
        if (Volatile.Read(ref _processAudible) == 0
            && ContainsAudiblePcm16(args.Buffer, args.BytesRecorded))
        {
            Interlocked.Exchange(ref _processAudible, 1);
        }
    }

    private void OnEndpointDataAvailable(object? sender, WaveInEventArgs args) =>
        _endpointWriter?.Write(args.Buffer, 0, args.BytesRecorded);

    private void OnProcessRecordingStopped(object? sender, StoppedEventArgs args)
    {
        // Stop/qualification owns finalization and fallback selection.
    }

    private void OnEndpointRecordingStopped(object? sender, StoppedEventArgs args)
    {
        // Stop/qualification owns finalization and fallback selection.
    }

    private async Task<IReadOnlyList<string>> StopProcessCaptureAsync(bool deleteFiles)
    {
        var capture = _processCapture;
        var writer = _processWriter;
        _processCapture = null;
        _processWriter = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnProcessDataAvailable;
            capture.RecordingStopped -= OnProcessRecordingStopped;
            try { await capture.StopAsync(); } catch { }
            capture.Dispose();
        }
        var paths = writer?.Complete() ?? [];
        if (deleteFiles) DeletePaths(paths);
        return paths;
    }

    private Task<IReadOnlyList<string>> StopEndpointCaptureAsync(bool deleteFiles)
    {
        var capture = _endpointCapture;
        var device = _endpointDevice;
        var writer = _endpointWriter;
        _endpointCapture = null;
        _endpointDevice = null;
        _endpointWriter = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnEndpointDataAvailable;
            capture.RecordingStopped -= OnEndpointRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        device?.Dispose();
        IReadOnlyList<string> paths = writer?.Complete() ?? [];
        if (deleteFiles) DeletePaths(paths);
        return Task.FromResult(paths);
    }

    private async Task StopAndCleanupAsync()
    {
        _stopping = true;
        _qualificationCancellation?.Cancel();
        if (_qualificationTask is not null)
        {
            try { await _qualificationTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        await _gate.WaitAsync();
        try
        {
            await StopProcessCaptureAsync(deleteFiles: false);
            await StopEndpointCaptureAsync(deleteFiles: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ProcessSilenceFallbackWarning() =>
        "Meeting-process audio remained silent during qualification; Muesli switched to all-system audio capture so remote speech would not be lost.";

    private static void DeletePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Probe cleanup is best-effort and must not stop a recording.
            }
        }
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
}

public sealed record SystemAudioCaptureStartResult(
    string Mode,
    int? TargetProcessId,
    bool UsedFallback,
    string? Warning);
