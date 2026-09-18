using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Muesli.Windows.Services;

/// <summary>
/// Reads the two Windows sensor signals that corroborate a meeting: whether another process is
/// capturing from an audio input endpoint, and whether the camera is currently held open.
///
/// Both are advisory. They exist to suppress false positives, never to trigger a meeting on their
/// own — see <see cref="MeetingCandidateResolver"/>. Every probe is bounded and failure-tolerant,
/// because these APIs are affected by privacy settings, group policy, and driver behaviour.
/// </summary>
public sealed class MeetingPresenceSignals : IDisposable
{
    /// <summary>
    /// Windows records camera consent per app here. A subkey whose LastUsedTimeStop is 0 while
    /// LastUsedTimeStart is set means that app currently holds the camera. This is the documented
    /// per-user store the Settings privacy page itself reports from.
    /// </summary>
    private const string WebcamConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private readonly int _ownProcessId = Environment.ProcessId;
    private MMDeviceEnumerator? _enumerator;
    private bool _disposed;

    /// <summary>True when a process other than Muesli is actively capturing audio input.</summary>
    public bool IsMicrophoneInUseByAnotherProcess()
    {
        if (_disposed) return false;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        using var session = sessions[index];
                        if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                        var processId = (int)session.GetProcessID;
                        // Muesli's own dictation or meeting capture must never corroborate itself.
                        if (processId == 0 || processId == _ownProcessId) continue;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Endpoint enumeration fails on locked-down or driver-restricted machines; the signal is
            // advisory, so an unreadable state is reported as "not observed" rather than assumed.
        }
        return false;
    }

    /// <summary>True when Windows reports the camera as currently in use by some application.</summary>
    public bool IsCameraInUse()
    {
        if (_disposed) return false;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(WebcamConsentStore);
            return root is not null && HasOpenCameraHandle(root, depth: 0);
        }
        catch
        {
            return false;
        }
    }

    public MeetingPresenceSnapshot Capture(MeetingEvidenceStrength evidence, string? candidateKey) =>
        new(evidence, IsMicrophoneInUseByAnotherProcess(), IsCameraInUse(), candidateKey);

    private static bool HasOpenCameraHandle(RegistryKey key, int depth)
    {
        // NonPackaged apps nest one level deeper; anything beyond that is not part of this store.
        if (depth > 2) return false;
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is null) continue;
            if (child.GetValue("LastUsedTimeStart") is long start &&
                child.GetValue("LastUsedTimeStop") is long stop &&
                start > 0 && stop == 0)
            {
                return true;
            }
            if (HasOpenCameraHandle(child, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>Process names that are meeting clients in their own right, not merely titled like one.</summary>
    public static bool IsDedicatedMeetingProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        return DedicatedProcesses.Contains(processName.Trim());
    }

    private static readonly HashSet<string> DedicatedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom",
        "Zoom Workplace",
        "CptHost",       // Zoom's in-meeting window host; present only during a live meeting
        "ms-teams",
        "Teams",
        "Webex",
        "webexmta",
        "CiscoCollabHost",
        "atmgr",         // Webex meeting manager
        "chime",
        "Amazon Chime",
        "slack",         // Slack huddles run in the desktop client
        "Discord"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}

/// <summary>Static helpers describing what a scan observed, kept separate so they can be unit tested.</summary>
public static class MeetingEvidenceClassifier
{
    /// <summary>
    /// Classifies the evidence for one window. A validated join URL or a dedicated meeting process is
    /// strong; a title that merely mentions a platform is weak and must be corroborated.
    /// </summary>
    public static MeetingEvidenceStrength Classify(string? windowTitle, string? processName, string? browserUrl)
    {
        if (MeetingUrlParser.IsSupportedMeetingUrl(browserUrl)) return MeetingEvidenceStrength.Strong;
        if (MeetingPresenceSignals.IsDedicatedMeetingProcess(processName)) return MeetingEvidenceStrength.Strong;
        return TitleMentionsPlatform(windowTitle) ? MeetingEvidenceStrength.Weak : MeetingEvidenceStrength.None;
    }

    /// <summary>
    /// Title heuristics only ever produce weak evidence. These phrases are common in documents,
    /// articles, and chat threads, which is precisely why they cannot prompt on their own.
    /// </summary>
    public static bool TitleMentionsPlatform(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return false;
        var title = windowTitle.ToLowerInvariant();
        return title.Contains("zoom meeting") ||
               title.Contains("google meet") ||
               title.Contains("microsoft teams") ||
               title.Contains("teams meeting") ||
               title.Contains("webex meeting") ||
               title.Contains("chime meeting") ||
               title.Contains("facetime");
    }
}
