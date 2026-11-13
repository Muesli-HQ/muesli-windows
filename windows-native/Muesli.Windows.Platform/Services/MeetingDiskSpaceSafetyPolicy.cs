namespace Muesli.Windows.Services;

/// <summary>Checks the capture volume while a meeting is recording, leaving room to finalize its audio.</summary>
public static class MeetingDiskSpaceSafetyPolicy
{
    public const long MinimumFreeBytes = 1024L * 1024 * 1024;

    public static bool IsCriticallyLow(string captureDirectory, long minimumFreeBytes = MinimumFreeBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(captureDirectory));
            return !string.IsNullOrWhiteSpace(root) &&
                   new DriveInfo(root).AvailableFreeSpace < minimumFreeBytes;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A failed drive query is not proof that the recording should be stopped.
            return false;
        }
    }
}
