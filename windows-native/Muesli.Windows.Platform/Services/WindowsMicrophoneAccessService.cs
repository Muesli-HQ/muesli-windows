using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

public enum MicrophoneProbeFailure { None, Denied, NoEndpoint, Busy, Disconnected, Unknown }
public sealed record MicrophoneProbeResult(bool Captured, int EndpointCount, long BytesCaptured, float? Peak, string EndpointIdentity, string PolicyHint, MicrophoneProbeFailure Failure, string Message)
{
    public bool IsUsable => Captured && BytesCaptured > 0;
}

/// <summary>Privacy registry values are explanatory only; a short WASAPI capture is authoritative.</summary>
public sealed class WindowsMicrophoneAccessService
{
    public Task<MicrophoneProbeResult> ProbeAsync(string? selectedMicrophone, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var hint = ReadPolicyHint();
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        if (endpoints.Count == 0)
            return new MicrophoneProbeResult(false, 0, 0, null, "", hint, MicrophoneProbeFailure.NoEndpoint, "Windows reports no active microphone endpoint.");
        MMDevice? endpoint;
        try
        {
            endpoint = string.IsNullOrWhiteSpace(selectedMicrophone) || selectedMicrophone.Equals(AudioCaptureService.SystemDefaultMicrophone, StringComparison.OrdinalIgnoreCase)
                ? DefaultCaptureEndpointPolicy.Resolve(enumerator)
                : endpoints.FirstOrDefault(item => item.FriendlyName.Equals(selectedMicrophone, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            return new MicrophoneProbeResult(false, endpoints.Count, 0, null, selectedMicrophone ?? AudioCaptureService.SystemDefaultMicrophone, hint, ClassifyFailure(ex), ex.Message);
        }
        if (endpoint is null)
            return new MicrophoneProbeResult(false, endpoints.Count, 0, null, selectedMicrophone ?? "", hint, MicrophoneProbeFailure.Disconnected, "The selected microphone is no longer an active Windows endpoint.");
        try
        {
            using var capture = new WasapiCapture(endpoint) { ShareMode = AudioClientShareMode.Shared };
            long bytes = 0;
            capture.DataAvailable += (_, e) => bytes += e.BytesRecorded;
            capture.StartRecording();
            Task.Delay(450, cancellationToken).GetAwaiter().GetResult();
            capture.StopRecording();
            return new MicrophoneProbeResult(bytes > 0, endpoints.Count, bytes, null, endpoint.FriendlyName, hint, bytes > 0 ? MicrophoneProbeFailure.None : MicrophoneProbeFailure.Unknown, bytes > 0 ? "A short local WASAPI capture succeeded; no audio was retained. Peak is unavailable for this probe format." : "Windows started capture but returned no audio bytes.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, endpoints.Count, 0, null, endpoint.FriendlyName, hint, ClassifyFailure(ex), ex.Message); }
    }, cancellationToken);

    public static MicrophoneProbeFailure ClassifyFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => MicrophoneProbeFailure.Denied,
        COMException com when (uint)com.HResult == 0x80070005 => MicrophoneProbeFailure.Denied,
        COMException com when (uint)com.HResult is 0x8889000A or 0x88890017 => MicrophoneProbeFailure.Busy,
        COMException com when (uint)com.HResult is 0x88890004 or 0x88890008 => MicrophoneProbeFailure.Disconnected,
        AudioDeviceUnavailableException => MicrophoneProbeFailure.Disconnected,
        _ => MicrophoneProbeFailure.Unknown
    };

    private static string ReadPolicyHint()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", false);
            var value = key?.GetValue("Value")?.ToString();
            return string.IsNullOrWhiteSpace(value) ? "Windows privacy policy could not be read; the capture test remains authoritative." : $"Windows microphone policy hint: {value}.";
        }
        catch { return "Windows privacy policy could not be read; the capture test remains authoritative."; }
    }
}
