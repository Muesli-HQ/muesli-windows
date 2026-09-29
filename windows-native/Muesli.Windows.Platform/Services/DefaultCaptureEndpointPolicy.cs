using NAudio.CoreAudioApi;

namespace Muesli.Windows.Services;

internal readonly record struct CaptureEndpointDescriptor(string Id, string FriendlyName);

/// <summary>
/// Windows exposes two capture defaults, and analog headphone jacks add a third trap.
/// Headphones steal <see cref="Role.Communications"/> onto a Hands-Free path, and a 3.5mm
/// plug often makes the numbered Realtek "Microphone (N- …)" jack the Multimedia default.
/// That jack records electrical noise that ASR reports as no speech. Dictation follows the
/// Sound Settings default unless that endpoint is Hands-Free, a mix device, or the analog
/// jack sitting next to a Microphone Array on the same adapter.
/// </summary>
internal static class DefaultCaptureEndpointPolicy
{
    public static MMDevice Resolve(MMDeviceEnumerator enumerator)
    {
        var multimediaId = TryDefaultId(enumerator, Role.Multimedia);
        var consoleId = TryDefaultId(enumerator, Role.Console);
        var communicationsId = TryDefaultId(enumerator, Role.Communications);

        var endpoints = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new CaptureEndpointDescriptor(device.ID, device.FriendlyName ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();

        var chosenId = ChoosePreferredId(multimediaId, consoleId, communicationsId, endpoints);
        if (string.IsNullOrWhiteSpace(chosenId))
        {
            throw new InvalidOperationException("Windows has no active default microphone.");
        }

        return enumerator.GetDevice(chosenId);
    }

    internal static string? ChoosePreferredId(
        string? multimediaId,
        string? consoleId,
        string? communicationsId,
        IReadOnlyList<CaptureEndpointDescriptor> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return multimediaId ?? consoleId ?? communicationsId;
        }

        var byId = endpoints
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        if (TryUsableDefault(multimediaId, byId, endpoints, out var multimedia))
        {
            return multimedia.Id;
        }

        if (TryUsableDefault(consoleId, byId, endpoints, out var console))
        {
            return console.Id;
        }

        var replacement = endpoints
            .Where(item => !IsUnusableAsDefault(item.FriendlyName))
            .OrderByDescending(item => Score(item, endpoints))
            .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(replacement.Id))
        {
            return replacement.Id;
        }

        if (Lookup(multimediaId, byId, out multimedia))
        {
            return multimedia.Id;
        }

        if (Lookup(consoleId, byId, out console))
        {
            return console.Id;
        }

        if (Lookup(communicationsId, byId, out var communications))
        {
            return communications.Id;
        }

        return endpoints[0].Id;
    }

    public static bool ShouldFollowDefaultChange(Role? role) =>
        role is null or Role.Multimedia or Role.Console;

    internal static bool IsUnusableAsDefault(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return true;
        }

        return ContainsAny(
            friendlyName,
            "Hands-Free",
            "Handsfree",
            "Stereo Mix",
            "What U Hear",
            "Wave Out Mix",
            "Steam Streaming Microphone");
    }

    internal static bool IsAnalogJackWhenArrayExists(
        string friendlyName,
        IReadOnlyList<CaptureEndpointDescriptor> endpoints)
    {
        if (IsMicrophoneArray(friendlyName) || IsUnusableAsDefault(friendlyName))
        {
            return false;
        }

        if (!friendlyName.StartsWith("Microphone (", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var adapter = AdapterName(friendlyName);
        if (string.IsNullOrWhiteSpace(adapter))
        {
            return false;
        }

        return endpoints.Any(item =>
            IsMicrophoneArray(item.FriendlyName) &&
            string.Equals(AdapterName(item.FriendlyName), adapter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryUsableDefault(
        string? id,
        IReadOnlyDictionary<string, CaptureEndpointDescriptor> byId,
        IReadOnlyList<CaptureEndpointDescriptor> endpoints,
        out CaptureEndpointDescriptor endpoint)
    {
        if (!Lookup(id, byId, out endpoint))
        {
            return false;
        }

        return !IsUnusableAsDefault(endpoint.FriendlyName) &&
               !IsAnalogJackWhenArrayExists(endpoint.FriendlyName, endpoints);
    }

    private static bool Lookup(
        string? id,
        IReadOnlyDictionary<string, CaptureEndpointDescriptor> byId,
        out CaptureEndpointDescriptor endpoint)
    {
        endpoint = default;
        return !string.IsNullOrWhiteSpace(id) && byId.TryGetValue(id, out endpoint);
    }

    private static int Score(CaptureEndpointDescriptor endpoint, IReadOnlyList<CaptureEndpointDescriptor> endpoints)
    {
        var name = endpoint.FriendlyName;
        if (IsUnusableAsDefault(name))
        {
            return -1000;
        }

        if (IsMicrophoneArray(name) || name.Contains("Internal Microphone", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (name.Contains("Internal Mic", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (IsAnalogJackWhenArrayExists(name, endpoints))
        {
            return 0;
        }

        return 40;
    }

    private static bool IsMicrophoneArray(string name) =>
        name.Contains("Microphone Array", StringComparison.OrdinalIgnoreCase);

    internal static string? AdapterName(string friendlyName)
    {
        var open = friendlyName.IndexOf('(');
        var close = friendlyName.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var inner = friendlyName[(open + 1)..close].Trim();
        var dash = inner.IndexOf("- ", StringComparison.Ordinal);
        if (dash is >= 1 and <= 3 && inner[..dash].All(char.IsDigit))
        {
            return inner[(dash + 2)..].Trim();
        }

        return inner;
    }

    private static string? TryDefaultId(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
