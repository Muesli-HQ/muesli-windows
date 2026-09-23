using System.IO;

namespace Muesli.Windows.Services;

public static class MeetingRecordingSafetyPolicy
{
    public const int MissingScansBeforeConfirmation = 15;
    public const int ContinueRecordingSnoozeMinutes = 15;
    public const int PostMeetingPromptCooldownMinutes = 10;
    public const long MinimumFreeBytes = 1024L * 1024 * 1024;

    public static bool IsSameMeeting(
        string? currentKey,
        int? currentProcessId,
        DetectedMeeting? detected)
    {
        if (detected is null || string.IsNullOrWhiteSpace(currentKey))
        {
            return false;
        }

        if (string.Equals(detected.Key, currentKey, StringComparison.OrdinalIgnoreCase)
            || (currentProcessId is > 0 && detected.ProcessId == currentProcessId))
        {
            return true;
        }

        var separator = currentKey.IndexOf('|');
        var currentPlatform = separator >= 0 ? currentKey[..separator] : currentKey;
        return currentPlatform.Equals(detected.Platform, StringComparison.OrdinalIgnoreCase);
    }

    public static string? PlatformFromKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var separator = key.IndexOf('|');
        return separator >= 0 ? key[..separator] : key;
    }

    public static bool IsDiskCriticallyLow(string? captureDirectory = null, long minimumFreeBytes = MinimumFreeBytes)
    {
        try
        {
            var path = captureDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "muesli",
                "captures");
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).AvailableFreeSpace < minimumFreeBytes;
        }
        catch
        {
            // An unavailable drive reading must not terminate a healthy recording.
            return false;
        }
    }
}
