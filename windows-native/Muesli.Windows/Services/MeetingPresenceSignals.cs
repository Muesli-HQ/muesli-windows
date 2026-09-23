using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Muesli.Windows.Services;

public sealed class MeetingPresenceSignals : IDisposable
{
    private const string WebcamConsentStore =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private readonly int _ownProcessId = Environment.ProcessId;
    private MMDeviceEnumerator? _enumerator;

    public bool IsMicrophoneInUseByAnotherProcess()
    {
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
                        if (session.State != AudioSessionState.AudioSessionStateActive)
                        {
                            continue;
                        }
                        var processId = (int)session.GetProcessID;
                        if (processId != 0 && processId != _ownProcessId)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            // Presence signals are advisory and unavailable on some drivers/policies.
        }
        return false;
    }

    public bool IsCameraInUse()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(WebcamConsentStore);
            return root is not null && HasOpenCameraHandle(root, 0);
        }
        catch
        {
            return false;
        }
    }

    public static bool HasZoomMeetingHost()
    {
        try
        {
            return Process.GetProcessesByName("CptHost").Any(process =>
            {
                using (process) return !process.HasExited;
            });
        }
        catch
        {
            return false;
        }
    }

    private static bool HasOpenCameraHandle(RegistryKey key, int depth)
    {
        if (depth > 2)
        {
            return false;
        }
        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is null)
            {
                continue;
            }
            if (child.GetValue("LastUsedTimeStart") is long start
                && child.GetValue("LastUsedTimeStop") is long stop
                && start > 0
                && stop == 0)
            {
                return true;
            }
            if (HasOpenCameraHandle(child, depth + 1))
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _enumerator?.Dispose();
        _enumerator = null;
    }
}

internal sealed class MeetingDetectionStabilityGate
{
    private string? _candidateKey;
    private int _observations;

    public bool Observe(string key, int requiredObservations)
    {
        if (!string.Equals(_candidateKey, key, StringComparison.OrdinalIgnoreCase))
        {
            _candidateKey = key;
            _observations = 1;
        }
        else
        {
            _observations++;
        }

        return _observations >= Math.Max(1, requiredObservations);
    }

    public void Reset()
    {
        _candidateKey = null;
        _observations = 0;
    }
}
