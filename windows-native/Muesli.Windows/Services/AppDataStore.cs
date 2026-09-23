using System.IO;
namespace Muesli.Windows.Services;

public sealed class AppDataStore
{
    private readonly string _dataDirectory;
    private readonly AtomicJsonFile _json;

    public AppDataStore(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "data");
        _json = new AtomicJsonFile(warning => RecoveryWarnings.Add(warning));
    }

    public List<string> RecoveryWarnings { get; } = [];

    public IReadOnlyList<PersistedDictation> LoadDictations()
    {
        return ReadJson("windows-dictations.json", new List<PersistedDictation>());
    }

    public void SaveDictations(IEnumerable<PersistedDictation> dictations)
    {
        WriteJson("windows-dictations.json", dictations.ToList());
    }

    public IReadOnlyList<PersistedMeeting> LoadMeetings()
    {
        return ReadJson("windows-meetings.json", new List<PersistedMeeting>());
    }

    public void SaveMeetings(IEnumerable<PersistedMeeting> meetings)
    {
        WriteJson("windows-meetings.json", meetings.ToList());
    }

    public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders()
    {
        return ReadJson("windows-meeting-folders.json", new List<PersistedMeetingFolder>());
    }

    public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders)
    {
        WriteJson("windows-meeting-folders.json", folders.ToList());
    }

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary()
    {
        return ReadJson("windows-dictionary.json", new List<DictionaryEntryRecord>());
    }

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries)
    {
        WriteJson("windows-dictionary.json", entries.ToList());
    }

    public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates()
    {
        return ReadJson("windows-meeting-templates.json", new List<PersistedMeetingTemplate>());
    }

    public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates)
    {
        WriteJson("windows-meeting-templates.json", templates.ToList());
    }

    private T ReadJson<T>(string fileName, T fallback)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        return _json.Load(path, fallback).Value;
    }

    private void WriteJson<T>(string fileName, T value)
    {
        var path = Path.Combine(_dataDirectory, fileName);
        _json.Save(path, value);
    }
}

public sealed record PersistedDictation(
    string Id,
    DateTime Timestamp,
    string Text,
    int DurationMs,
    string ModelProfile,
    string DeliveryMode = "active-app");

public sealed record PersistedMeeting
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int DurationMs { get; init; }
    public string Transcript { get; init; } = "";
    public string RawTranscript { get; init; } = "";
    public string Summary { get; init; } = "";
    public string ManualNotes { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string ModelProfile { get; init; } = "";
    public string? FolderId { get; init; }
    public int WordCount { get; init; }
    public string TemplateName { get; init; } = "";
    public Dictionary<string, string> SpeakerAliases { get; init; } = new();
    public List<string> HealthWarnings { get; init; } = new();
    public List<MeetingTranscriptSegment> TranscriptSegments { get; init; } = new();
    public string DiarizationStatus { get; init; } = "unknown";
    public string Status { get; init; } = "completed";
    public string Origin { get; init; } = "recording";
}

public sealed record PersistedMeetingFolder(
    string Id,
    string Name);

public sealed record DictionaryEntryRecord
{
    public string Id { get; init; } = "";
    public string Phrase { get; init; } = "";
    public string Replacement { get; init; } = "";
    public double MatchingThreshold { get; init; } = 0.85;
}

public sealed record PersistedMeetingTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Icon { get; init; } = "square.and.pencil";
}
