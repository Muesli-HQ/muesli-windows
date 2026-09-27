using System.IO;

namespace Muesli.Windows.Services;

/// <summary>
/// Sidecar files for in-flight and ready retranscription candidates, plus a generated-notes-stale
/// marker. This is not the meeting record: failure and cancellation must be able to delete scratch
/// without touching the original transcript.
/// </summary>
public sealed class RetranscriptionCandidateStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _root;
    private readonly AtomicJsonFile _json;

    public RetranscriptionCandidateStore(string scratchDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        _root = Path.GetFullPath(scratchDirectory);
        Directory.CreateDirectory(_root);
        _json = new AtomicJsonFile();
    }

    public string RootDirectory => _root;

    public RetranscriptionScratchState? TryLoad(string meetingId)
    {
        var path = StatePath(meetingId);
        if (!File.Exists(path))
        {
            return null;
        }

        var loaded = _json.Load(path, (RetranscriptionScratchState?)null);
        return loaded.Value;
    }

    public void Save(RetranscriptionScratchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateMeetingId(state.MeetingId);
        _json.Save(StatePath(state.MeetingId), state);
    }

    public bool Delete(string meetingId)
    {
        ValidateMeetingId(meetingId);
        return DeletePathAndSidecars(StatePath(meetingId));
    }

    public void SetGeneratedNotesStale(string meetingId, string reason, DateTimeOffset atUtc)
    {
        ValidateMeetingId(meetingId);
        _json.Save(
            StalePath(meetingId),
            new GeneratedNotesStaleMarker
            {
                MeetingId = meetingId,
                Reason = reason,
                MarkedAtUtc = atUtc
            });
    }

    public bool IsGeneratedNotesStale(string meetingId)
    {
        ValidateMeetingId(meetingId);
        return File.Exists(StalePath(meetingId));
    }

    public void ClearGeneratedNotesStale(string meetingId)
    {
        ValidateMeetingId(meetingId);
        DeletePathAndSidecars(StalePath(meetingId));
    }

    /// <summary>Removes candidate scratch and the stale marker. Call this when the meeting is deleted.</summary>
    public void DeleteAllForMeeting(string meetingId)
    {
        ValidateMeetingId(meetingId);
        Delete(meetingId);
        ClearGeneratedNotesStale(meetingId);
    }

    public IReadOnlyList<string> ListMeetingIds()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_root, "*-candidate.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            const string suffix = "-candidate.json";
            if (name.Length > suffix.Length &&
                name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(name[..^suffix.Length]);
            }
        }

        return ids;
    }

    private string StatePath(string meetingId) =>
        Path.Combine(_root, meetingId + "-candidate.json");

    private string StalePath(string meetingId) =>
        Path.Combine(_root, meetingId + "-notes-stale.json");

    private bool DeletePathAndSidecars(string path)
    {
        if (!Directory.Exists(_root))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        var deleted = false;
        foreach (var candidate in Directory.EnumerateFiles(_root))
        {
            var name = Path.GetFileName(candidate);
            if (!IsSidecar(name, fileName))
            {
                continue;
            }

            File.Delete(candidate);
            deleted = true;
        }

        return deleted;
    }

    private static bool IsSidecar(string candidateName, string fileName)
    {
        if (candidateName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            candidateName.Equals(fileName + ".bak", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidateName.StartsWith("." + fileName + ".", StringComparison.OrdinalIgnoreCase) ||
               candidateName.StartsWith(fileName + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateMeetingId(string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId) ||
            !meetingId.All(character => char.IsLetterOrDigit(character) || character is '_' or '-'))
        {
            throw new ArgumentException("Meeting IDs may contain only letters, digits, underscores, and hyphens.", nameof(meetingId));
        }
    }
}

public sealed record RetranscriptionScratchState
{
    public int SchemaVersion { get; init; } = RetranscriptionCandidateStore.CurrentSchemaVersion;
    public string MeetingId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string? Transcript { get; init; }
    public string? TranscriptFingerprint { get; init; }
    public string? Error { get; init; }
    public string? AudioFileName { get; init; }
    public long AudioByteLength { get; init; }
    public int DurationMs { get; init; }
}

public sealed record GeneratedNotesStaleMarker
{
    public string MeetingId { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTimeOffset MarkedAtUtc { get; init; }
}
