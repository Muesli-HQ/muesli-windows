using System.IO;

namespace Muesli.Windows.Services;

public interface ICapturedAudioArtifact
{
    string LastCapturePath { get; }
}

public sealed class CaptureStorageService
{
    private readonly string _captureDirectory;
    private readonly string _recordingsDirectory;

    public CaptureStorageService(string? captureDirectory = null)
    {
        _captureDirectory = Path.GetFullPath(captureDirectory ??
            Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().CaptureDirectory);
        _recordingsDirectory = Path.Combine(_captureDirectory, "recordings");
    }

    public MeetingAudioPaths PersistMeetingAudio(
        string meetingId,
        ICapturedAudioArtifact? microphone,
        ICapturedAudioArtifact? system)
    {
        ValidateMeetingId(meetingId);
        var meetingDirectory = Path.Combine(_recordingsDirectory, meetingId);
        Directory.CreateDirectory(meetingDirectory);
        string? micPath = null;
        string? systemPath = null;
        try
        {
            micPath = microphone is null
                ? null
                : AtomicCopy(microphone.LastCapturePath, Path.Combine(meetingDirectory, "microphone.wav"));
            systemPath = system is null
                ? null
                : AtomicCopy(system.LastCapturePath, Path.Combine(meetingDirectory, "system.wav"));
            return new MeetingAudioPaths(micPath, systemPath);
        }
        catch
        {
            if (micPath is not null)
            {
                TryDelete(micPath);
            }
            if (systemPath is not null)
            {
                TryDelete(systemPath);
            }
            throw;
        }
    }

    public bool IsOwnedMeetingAudioPath(string meetingId, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsValidMeetingId(meetingId))
        {
            return false;
        }

        var meetingDirectory = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(_recordingsDirectory, meetingId)));
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);
        var resumed = name.StartsWith("microphone-resume-", StringComparison.OrdinalIgnoreCase)
            ? name[18..] : name.StartsWith("system-resume-", StringComparison.OrdinalIgnoreCase) ? name[14..] : "";
        return fullPath.StartsWith(meetingDirectory, StringComparison.OrdinalIgnoreCase) &&
               (name.Equals("microphone.wav", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("system.wav", StringComparison.OrdinalIgnoreCase) ||
                resumed.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && IsValidMeetingId(resumed[..^4]));
    }

    public CaptureCleanupResult DeleteOwnedMeetingAudio(string meetingId, IEnumerable<string> paths)
    {
        var deleted = 0;
        var failed = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsOwnedMeetingAudioPath(meetingId, path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch
            {
                failed.Add(path);
            }
        }

        var directory = Path.Combine(_recordingsDirectory, meetingId);
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Empty-directory cleanup is cosmetic and must not affect transcript deletion.
        }

        return new CaptureCleanupResult(deleted, failed);
    }

    public CaptureInventory InspectLegacyAndTransientCaptures()
    {
        if (!Directory.Exists(_captureDirectory))
        {
            return new CaptureInventory([], 0);
        }

        var files = Directory.EnumerateFiles(_captureDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsLegacyOrTransientCapture)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new CaptureInventory(files.Select(file => file.FullName).ToList(), files.Sum(file => file.Length));
    }

    public CaptureCleanupResult DeleteLegacyAndTransientCaptures(CaptureInventory inventory)
    {
        var deleted = 0;
        var failed = new List<string>();
        foreach (var path in inventory.Paths)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Path.GetDirectoryName(fullPath)!.Equals(_captureDirectory, StringComparison.OrdinalIgnoreCase) ||
                !IsLegacyOrTransientCapture(fullPath))
            {
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    deleted++;
                }
            }
            catch
            {
                failed.Add(fullPath);
            }
        }

        return new CaptureCleanupResult(deleted, failed);
    }

    private static bool IsLegacyOrTransientCapture(string path)
    {
        var name = Path.GetFileName(path);
        return (name.StartsWith("native-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(DictationTemporaryAudioPolicy.SegmentPrefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(DictationTemporaryAudioPolicy.MergedPrefix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("meeting-system-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("last-meeting-system.wav", StringComparison.OrdinalIgnoreCase)) &&
               !name.Equals("last-dictation.wav", StringComparison.OrdinalIgnoreCase);
    }

    private static string AtomicCopy(string sourcePath, string destinationPath)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
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
            // Best-effort cleanup; a locked artifact can be retried later.
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void ValidateMeetingId(string meetingId)
    {
        if (!IsValidMeetingId(meetingId))
        {
            throw new ArgumentException("Meeting IDs may contain only letters, digits, underscores, and hyphens.", nameof(meetingId));
        }
    }

    private static bool IsValidMeetingId(string meetingId) =>
        !string.IsNullOrWhiteSpace(meetingId) &&
        meetingId.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
}

public sealed record MeetingAudioPaths(string? MicrophonePath, string? SystemPath);
public sealed record CaptureInventory(IReadOnlyList<string> Paths, long TotalBytes)
{
    public int FileCount => Paths.Count;
}
public sealed record CaptureCleanupResult(int DeletedCount, IReadOnlyList<string> FailedPaths);
