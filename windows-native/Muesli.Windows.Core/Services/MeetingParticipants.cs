using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>One person added to a meeting (macOS MeetingContactIdentity without Apple Contacts).</summary>
public sealed record MeetingParticipant(string Id, string Name, string? Email)
{
    /// <summary>macOS compactDisplayName: an email shown as its local part.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Name.Contains('@') ? Name[..Name.IndexOf('@')] : Name;
}

/// <summary>
/// macOS MeetingParticipantsView storage. Kept in <c>meeting-participants.json</c> beside the history
/// (meeting id → people) so the library schema and older profiles stay unchanged.
/// </summary>
public static class MeetingParticipants
{
    public const int PromptNameCharacterLimit = 200;
    public const int PromptCharacterLimit = 4_000;
    private static readonly object Gate = new();

    public static string PathFor(string profileRoot) => Path.Combine(profileRoot, "meeting-participants.json");

    public static IReadOnlyList<MeetingParticipant> Load(string profileRoot, string meetingId)
    {
        lock (Gate) return Read(profileRoot).TryGetValue(meetingId, out var people) ? people : [];
    }

    /// <summary>Adds a person; an email (or name, without one) already on the meeting is not added twice.</summary>
    public static IReadOnlyList<MeetingParticipant> Add(string profileRoot, string meetingId, string name, string? email)
    {
        var trimmedEmail = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        var trimmedName = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (trimmedName.Length == 0) trimmedName = trimmedEmail ?? throw new ArgumentException("Enter a name or email.", nameof(name));
        lock (Gate)
        {
            var all = Read(profileRoot);
            var people = all.TryGetValue(meetingId, out var existing) ? existing.ToList() : [];
            var key = trimmedEmail ?? trimmedName;
            if (!people.Any(person => string.Equals(person.Email ?? person.Name, key, StringComparison.OrdinalIgnoreCase)))
                people.Add(new MeetingParticipant(Guid.NewGuid().ToString("N"), trimmedName, trimmedEmail));
            all[meetingId] = people;
            Write(profileRoot, all);
            return people;
        }
    }

    public static IReadOnlyList<MeetingParticipant> Remove(string profileRoot, string meetingId, string participantId)
    {
        lock (Gate)
        {
            var all = Read(profileRoot);
            if (!all.TryGetValue(meetingId, out var existing)) return [];
            var people = existing.Where(person => person.Id != participantId).ToList();
            if (people.Count == 0) all.Remove(meetingId);
            else all[meetingId] = people;
            Write(profileRoot, all);
            return people;
        }
    }

    /// <summary>Drops a deleted meeting's people; best effort, the meeting is already gone.</summary>
    public static void RemoveMeeting(string profileRoot, string meetingId)
    {
        lock (Gate)
        {
            var all = Read(profileRoot);
            try { if (all.Remove(meetingId)) Write(profileRoot, all); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>macOS participantNamesForPrompt: unique, letter-bearing names, each and all bounded.</summary>
    public static IReadOnlyList<string> NamesForPrompt(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var total = 0;
        foreach (var candidate in names)
        {
            var name = string.Join(' ', candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0 || name.Contains('@') || !name.Any(char.IsLetter) || !seen.Add(name)) continue;
            if (name.Length > PromptNameCharacterLimit) name = name[..(PromptNameCharacterLimit - 1)] + "…";
            var added = PromptLine(name).Length + (result.Count == 0 ? 0 : 1);
            if (total + added > PromptCharacterLimit) continue;
            total += added;
            result.Add(name);
        }
        return result;
    }

    public static string PromptLine(string name) =>
        $"- <participant_name>{name.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}</participant_name>";

    private static Dictionary<string, List<MeetingParticipant>> Read(string profileRoot)
    {
        try
        {
            var path = PathFor(profileRoot);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<MeetingParticipant>>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Write(string profileRoot, Dictionary<string, List<MeetingParticipant>> all)
    {
        var path = PathFor(profileRoot);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, path, overwrite: true);
    }
}
