using System.IO;
using System.Text.Json;

namespace Muesli.Windows.Services.Persistence;

/// <summary>A source file that could not be read, and what was done about it.</summary>
public sealed record SnapshotProblem(string FileName, string Message, bool Blocking);

/// <summary>
/// What the JSON history contained at the moment it was read. Meetings keep the verbatim JSON of
/// their automation result and their original schema version, neither of which survives the trip
/// through <see cref="PersistedMeeting"/>.
/// </summary>
public sealed record JsonHistorySnapshot
{
    public IReadOnlyList<PersistedDictation> Dictations { get; init; } = [];
    public IReadOnlyList<SnapshotMeeting> Meetings { get; init; } = [];
    public IReadOnlyList<PersistedMeetingFolder> Folders { get; init; } = [];
    public IReadOnlyList<PersistedMeetingTemplate> Templates { get; init; } = [];
    public IReadOnlyList<SnapshotProblem> Problems { get; init; } = [];

    /// <summary>
    /// The last write time of the newest source file, used as the created and updated timestamp for
    /// folders and templates. Those records carry no timestamp of their own in JSON, and a stamp
    /// taken from the clock would change on every run and defeat the content fingerprint that makes
    /// re-running the migration a no-op.
    /// </summary>
    public DateTimeOffset SourceTimestampUtc { get; init; } = DateTimeOffset.UnixEpoch;

    public bool HasBlockingProblems => Problems.Any(problem => problem.Blocking);

    public int RecordCount => Dictations.Count + Meetings.Count + Folders.Count + Templates.Count;
}

public sealed record SnapshotMeeting(
    PersistedMeeting Meeting,
    int OriginalSchemaVersion,
    string? AutomationResultJson);

/// <summary>
/// Reads the JSON history without changing it.
/// <para>
/// Deliberately not <see cref="AppDataStore"/>: loading through that class quarantines an
/// unreadable file by renaming it and can restore a backup over it. Those are the right moves for
/// the running app and the wrong ones for a migration, which must be able to run, fail, and leave
/// the user's files exactly as it found them.
/// </para>
/// </summary>
public static class JsonHistorySnapshotReader
{
    public const string DictationsFileName = "windows-dictations.json";
    public const string MeetingsFileName = "windows-meetings.json";
    public const string FoldersFileName = "windows-meeting-folders.json";
    public const string TemplatesFileName = "windows-meeting-templates.json";

    /// <summary>
    /// Matches <see cref="AtomicJsonFile"/>'s serializer settings. The two must agree: this reader
    /// parses exactly what that writer produced.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static JsonHistorySnapshot Read(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        var problems = new List<SnapshotProblem>();
        var newest = DateTimeOffset.UnixEpoch;

        var dictations = ReadArray(
            directory,
            DictationsFileName,
            problems,
            ref newest,
            element => element.Deserialize<PersistedDictation>(JsonOptions));

        var meetings = ReadArray(
            directory,
            MeetingsFileName,
            problems,
            ref newest,
            ReadMeeting);

        var folders = ReadArray(
            directory,
            FoldersFileName,
            problems,
            ref newest,
            element => element.Deserialize<PersistedMeetingFolder>(JsonOptions));

        var templates = ReadArray(
            directory,
            TemplatesFileName,
            problems,
            ref newest,
            element => element.Deserialize<PersistedMeetingTemplate>(JsonOptions));

        return new JsonHistorySnapshot
        {
            Dictations = dictations,
            Meetings = meetings,
            Folders = folders,
            Templates = templates,
            Problems = problems,
            SourceTimestampUtc = newest
        };
    }

    private static SnapshotMeeting? ReadMeeting(JsonElement element)
    {
        var meeting = element.Deserialize<PersistedMeeting>(JsonOptions);
        if (meeting is null)
        {
            return null;
        }

        var originalSchemaVersion = TryGetProperty(element, "schemaVersion", out var version) &&
                                    version.TryGetInt32(out var parsed)
            ? parsed
            : 0;

        if (originalSchemaVersion > AppDataStore.CurrentMeetingSchemaVersion)
        {
            throw new InvalidDataException(
                $"Meeting '{meeting.Id}' uses unsupported schema {originalSchemaVersion}.");
        }

        // The automation payload is carried across as its own JSON text so a field this build does
        // not know about still arrives intact on the other side.
        var automation = TryGetProperty(element, "automationResult", out var result) &&
                         result.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? result.GetRawText()
            : null;

        return new SnapshotMeeting(AppDataStore.MigrateMeeting(meeting), originalSchemaVersion, automation);
    }

    private static IReadOnlyList<T> ReadArray<T>(
        string directory,
        string fileName,
        List<SnapshotProblem> problems,
        ref DateTimeOffset newest,
        Func<JsonElement, T?> map)
        where T : class
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        if (written > newest)
        {
            newest = written;
        }

        try
        {
            return Parse(path, map);
        }
        catch (InvalidDataException exception)
        {
            // Envelope schema > 1 and meeting schema > 5 are not "unreadable JSON". Falling back to
            // a .bak would look like a successful import of an older snapshot. Fail closed.
            problems.Add(new SnapshotProblem(fileName, exception.Message, Blocking: true));
            return [];
        }
        catch (JsonException)
        {
            // The app keeps a .bak alongside each history file. Reading it here is recovery, not
            // repair: nothing is renamed, restored or written back. Do not include the parser's
            // payload snippet: logs and Failure strings must not carry transcript or title text.
            var backupPath = $"{path}.bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    var recovered = Parse(backupPath, map);
                    problems.Add(new SnapshotProblem(
                        fileName,
                        $"{fileName} could not be read; its backup was used instead.",
                        Blocking: false));
                    return recovered;
                }
                catch (Exception backupException) when (backupException is JsonException or InvalidDataException)
                {
                    _ = backupException;
                    problems.Add(new SnapshotProblem(
                        fileName,
                        $"Neither {fileName} nor its backup could be read.",
                        Blocking: true));
                    return [];
                }
            }

            problems.Add(new SnapshotProblem(
                fileName,
                $"{fileName} could not be read.",
                Blocking: true));
            return [];
        }
    }

    private static List<T> Parse<T>(string path, Func<JsonElement, T?> map)
        where T : class
    {
        // FileShare.ReadWrite so a running app holding the file open cannot turn a migration into an
        // IO failure, and FileAccess.Read so this reader could not write even by mistake.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "data", out var data))
        {
            if (!TryGetProperty(root, "schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version < 1 ||
                version > AtomicJsonFile.CurrentSchemaVersion)
            {
                throw new InvalidDataException("The persisted JSON envelope has an unsupported schema version.");
            }

            root = data;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Expected a JSON array in {Path.GetFileName(path)}.");
        }

        var results = new List<T>();
        foreach (var element in root.EnumerateArray())
        {
            var mapped = map(element);
            if (mapped is not null)
            {
                results.Add(mapped);
            }
        }

        return results;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
