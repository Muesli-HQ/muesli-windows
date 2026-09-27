using NAudio.CoreAudioApi;

namespace Muesli.Windows.Services;

/// <summary>
/// Collects the platform-only facts the support bundle needs. Only counts (device categories) are
/// exposed; endpoint identifiers and friendly names are deliberately discarded here so the bundle
/// cannot leak audio hardware labels.
/// </summary>
public static class WindowsSupportBundleFacts
{
    public static SupportBundleAudioSummary CollectAudioDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var inputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Count;
            var outputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Count;
            return new SupportBundleAudioSummary(inputs, outputs);
        }
        catch
        {
            // No audio stack, no COM, or an endpoint failure must not block a support export.
            return SupportBundleAudioSummary.Unavailable;
        }
    }
}
