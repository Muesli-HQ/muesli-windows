using System.Text.Json;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>Local import provenance (macOS <c>meeting_imports</c>): workspace and owner email stay on this PC.</summary>
public sealed record MeetingImportProvenance(string Provider, string DocumentId, string WorkspaceName, string OwnerEmail, DateTimeOffset ImportedAt);

/// <summary>
/// <c>meeting-imports.json</c> beside the history (meeting id → provenance), so the history schema and older
/// profiles stay unchanged. A row goes with its meeting, like the macOS foreign key with cascade.
/// </summary>
public static class MeetingImports
{
    private static readonly object Gate = new();

    public static string PathFor(string profileRoot) => Path.Combine(profileRoot, "meeting-imports.json");

    public static MeetingImportProvenance? Find(string profileRoot, string meetingId)
    {
        lock (Gate) return Read(profileRoot).GetValueOrDefault(meetingId);
    }

    public static void Record(string profileRoot, IEnumerable<(string MeetingId, MeetingImportProvenance Provenance)> rows)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, MeetingImportProvenance>(Read(profileRoot));
            foreach (var (meetingId, provenance) in rows) all[meetingId] = provenance;
            Write(profileRoot, all);
        }
    }

    public static void RemoveMeeting(string profileRoot, string meetingId)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, MeetingImportProvenance>(Read(profileRoot));
            try { if (all.Remove(meetingId)) Write(profileRoot, all); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes the whole file when all meeting history is cleared.</summary>
    public static void Clear(string profileRoot)
    {
        lock (Gate) File.Delete(PathFor(profileRoot));
    }

    private static Dictionary<string, MeetingImportProvenance> Read(string profileRoot)
    {
        try
        {
            var path = PathFor(profileRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, MeetingImportProvenance>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Write(string profileRoot, Dictionary<string, MeetingImportProvenance> all)
    {
        var path = PathFor(profileRoot);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, path, overwrite: true);
    }
}
