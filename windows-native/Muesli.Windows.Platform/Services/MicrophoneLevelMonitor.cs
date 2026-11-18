using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Muesli.Windows.Services;

/// <summary>
/// Live input level for setup's microphone meter. Opens the selected endpoint in shared mode and
/// reports each buffer's peak as 0..1; no audio is kept. Stop it as soon as the meter is hidden.
/// </summary>
public sealed class MicrophoneLevelMonitor : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;

    /// <summary>Raised on the capture thread with the buffer's peak mapped to 0..1.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Raised on the capture thread if Windows stops the capture with an error.</summary>
    public event Action<Exception>? Failed;

    /// <exception cref="Exception">Windows refused the endpoint (access denied, busy, unplugged).</exception>
    public void Start(string? microphoneName)
    {
        lock (_gate)
        {
            StopCore();
            using var enumerator = new MMDeviceEnumerator();
            var endpoint = string.IsNullOrWhiteSpace(microphoneName) ||
                           microphoneName.Equals(AudioCaptureService.SystemDefaultMicrophone, StringComparison.OrdinalIgnoreCase)
                ? DefaultCaptureEndpointPolicy.Resolve(enumerator)
                : enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(item => item.FriendlyName.Equals(microphoneName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new AudioDeviceUnavailableException(microphoneName);
            var capture = new WasapiCapture(endpoint) { ShareMode = AudioClientShareMode.Shared };
            var format = capture.WaveFormat;
            capture.DataAvailable += (_, e) => LevelChanged?.Invoke(Normalize(Peak(e.Buffer, e.BytesRecorded, format)));
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is { } exception) Failed?.Invoke(exception);
            };
            capture.StartRecording();
            _capture = capture;
        }
    }

    public void Stop()
    {
        lock (_gate) StopCore();
    }

    public void Dispose() => Stop();

    private void StopCore()
    {
        if (_capture is null) return;
        try { _capture.StopRecording(); } catch { /* already stopped by Windows */ }
        _capture.Dispose();
        _capture = null;
    }

    /// <summary>
    /// Peak absolute sample in the buffer, 0..1. Handles IEEE float and 16/24/32-bit integer PCM;
    /// an extensible format is decided by its sub-format, since 32 bits can be either float or int.
    /// </summary>
    public static double Peak(byte[] buffer, int count, WaveFormat format)
    {
        var peak = 0.0;
        var bits = format.BitsPerSample;
        if (IsFloat(format) && bits == 32)
        {
            for (var offset = 0; offset + 4 <= count; offset += 4)
            {
                var sample = Math.Abs(BitConverter.ToSingle(buffer, offset));
                if (float.IsFinite(sample)) peak = Math.Max(peak, sample);
            }
        }
        else if (bits == 16)
        {
            for (var offset = 0; offset + 2 <= count; offset += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, offset) / 32768.0));
        }
        else if (bits == 24)
        {
            for (var offset = 0; offset + 3 <= count; offset += 3)
            {
                var sample = (buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16) << 8 >> 8;
                peak = Math.Max(peak, Math.Abs(sample / 8388608.0));
            }
        }
        else if (bits == 32)
        {
            for (var offset = 0; offset + 4 <= count; offset += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buffer, offset) / 2147483648.0));
        }
        return Math.Min(peak, 1.0);
    }

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat);

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT.</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// Maps a peak to the meter on a decibel scale: -50 dBFS and below is empty, -5 dBFS is full,
    /// so room noise barely registers and normal speech fills most of the bars.
    /// </summary>
    public static double Normalize(double peak)
    {
        if (peak <= 0) return 0;
        var decibels = 20 * Math.Log10(peak);
        return Math.Clamp((decibels + 50) / 45, 0, 1);
    }
}
