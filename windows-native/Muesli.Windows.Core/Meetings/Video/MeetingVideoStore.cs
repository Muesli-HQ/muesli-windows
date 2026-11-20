using System.Text.Json;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Services;

/// <summary>macOS VisualMemoryItem of kind meetingVideo (2f10b23d): one MP4 per uninterrupted capture interval.
/// Path is relative to the videos root; Duration and MeetingOffset are seconds.</summary>
public sealed record MeetingVideoClip(
    string Id, string MeetingId, DateTimeOffset CapturedAt, string Source, string Path,
    double Duration = 0, double MeetingOffset = 0, long Bytes = 0, string AppId = "", string Text = "",
    DateTimeOffset? CaptureCompletedAt = null, int? PixelWidth = null, int? PixelHeight = null)
{
    public const string Kind = "meetingVideo";
}

/// <summary>
/// Port of macOS MeetingVideoStore. The catalog lives in the library database; media bytes live under
/// <see cref="Root"/>. Deleting a meeting queues its clips in the durable cleanup outbox (schema v4
/// trigger), and <see cref="Maintain"/> retries file removal.
/// </summary>
public sealed class MeetingVideoStore
{
    private const string PendingSuffix = ".pending.json";
    private const string RecordingSuffix = ".recording.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MuesliDatabase _database;
    private readonly object _gate = new();
    private readonly HashSet<string> _activeRecordings = [];

    public MeetingVideoStore(MuesliDatabase database, string root)
    {
        _database = database;
        Root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The absolute file for a relative clip path; rejects anything outside the root.</summary>
    public string FullPath(string path)
    {
        var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, path));
        if (System.IO.Path.IsPathRooted(path) || !resolved.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid media path");
        return resolved;
    }

    public void Save(IEnumerable<MeetingVideoClip> clips)
    {
        lock (_gate)
        {
            _database.Write(connection =>
            {
                foreach (var clip in clips)
                {
                    if (string.IsNullOrEmpty(clip.MeetingId)) throw new InvalidOperationException("Video requires a meeting owner.");
                    _ = FullPath(clip.Path);
                    var changed = Db.Command(connection, """
                            INSERT OR REPLACE INTO meeting_video_clips
                            SELECT $id, $kind, $meeting, $captured, $body WHERE EXISTS(SELECT 1 FROM meetings WHERE id = $meeting);
                            """)
                        .Bind("$id", clip.Id).Bind("$kind", MeetingVideoClip.Kind).Bind("$meeting", clip.MeetingId)
                        .Bind("$captured", clip.CapturedAt.UtcTicks).Bind("$body", JsonSerializer.Serialize(clip, Json)).Execute();
                    if (changed != 1) throw new PersistenceException("The meeting for this video no longer exists.");
                }
            });
        }
    }

    public IReadOnlyList<MeetingVideoClip> Clips(string? meetingId = null) => _database.Read(connection =>
    {
        using var command = Db.Command(connection, meetingId is null
            ? "SELECT body FROM meeting_video_clips ORDER BY captured_at_utc DESC;"
            : "SELECT body FROM meeting_video_clips WHERE meeting_id = $meeting ORDER BY captured_at_utc;");
        if (meetingId is not null) command.Bind("$meeting", meetingId);
        using var reader = command.ExecuteReader();
        var result = new List<MeetingVideoClip>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<MeetingVideoClip>(reader.GetString(0), Json)!);
        return (IReadOnlyList<MeetingVideoClip>)result;
    });

    /// <summary>Persist ownership before capture starts; active files are never recovered mid-write.</summary>
    public void BeginRecording(MeetingVideoClip clip)
    {
        lock (_gate)
        {
            WriteAtomically(FullPath(clip.Path + RecordingSuffix), JsonSerializer.Serialize(clip, Json));
            _activeRecordings.Add(clip.Id);
        }
    }

    public void EndRecording(MeetingVideoClip clip)
    {
        lock (_gate) _activeRecordings.Remove(clip.Id);
    }

    /// <summary>True when the meeting has saved clips or recovery journals (used by the retention prompt).</summary>
    public bool HasMedia(string meetingId)
    {
        lock (_gate) return Clips(meetingId).Count > 0 || Journals().Any(clip => clip.MeetingId == meetingId);
    }

    /// <summary>Journal a completed recording before the database write, so a busy database leaves both
    /// the MP4 and its owner available for recovery on the next launch.</summary>
    public void PersistCompleted(MeetingVideoClip clip)
    {
        lock (_gate)
        {
            var pending = FullPath(clip.Path + PendingSuffix);
            WriteAtomically(pending, JsonSerializer.Serialize(clip, Json));
            Save([clip]);
            File.Delete(pending);
            File.Delete(FullPath(clip.Path + RecordingSuffix));
        }
    }

    /// <summary>Recovers journaled clips whose meeting still exists and removes queued media.
    /// <paramref name="recoverRecording"/> returns the playable duration of an unfinished file, or null.</summary>
    public void Maintain(Func<string, double?>? recoverRecording = null)
    {
        lock (_gate)
        {
            Exception? firstError = null;
            foreach (var file in Directory.EnumerateFiles(Root).Where(IsJournal).ToList())
            {
                try
                {
                    if (Read(file) is not { } clip || _activeRecordings.Contains(clip.Id)) continue;
                    var owned = _database.Read(connection => Db.Command(connection, """
                            SELECT EXISTS(SELECT 1 FROM meetings WHERE id = $meeting)
                               AND NOT EXISTS(SELECT 1 FROM meeting_video_cleanup WHERE id = $id);
                            """).Bind("$meeting", clip.MeetingId).Bind("$id", clip.Id).ScalarInt64() == 1);
                    var video = FullPath(clip.Path);
                    if (owned)
                    {
                        if (!File.Exists(video)) continue;
                        if (file.EndsWith(RecordingSuffix, StringComparison.Ordinal))
                        {
                            if (recoverRecording?.Invoke(video) is not ({ } duration and > 0) || !double.IsFinite(duration)) continue;
                            clip = clip with { Duration = duration, Bytes = new FileInfo(video).Length };
                        }
                        Save([clip]);
                    }
                    else File.Delete(video);
                    File.Delete(file);
                }
                catch (Exception error) { firstError ??= error; }
            }

            var pending = _database.Read(connection =>
            {
                using var reader = Db.Command(connection, "SELECT body FROM meeting_video_cleanup;").ExecuteReader();
                var result = new List<MeetingVideoClip>();
                while (reader.Read()) result.Add(JsonSerializer.Deserialize<MeetingVideoClip>(reader.GetString(0), Json)!);
                return result;
            });
            var retained = Clips().Select(clip => clip.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in pending)
            {
                try
                {
                    if (!retained.Contains(clip.Path))
                        foreach (var path in new[] { clip.Path, clip.Path + PendingSuffix, clip.Path + RecordingSuffix }) File.Delete(FullPath(path));
                    _database.Write(connection => Db.Command(connection, "DELETE FROM meeting_video_cleanup WHERE id = $id;").Bind("$id", clip.Id).Execute());
                }
                catch (Exception error) { firstError ??= error; }
            }
            if (firstError is not null) throw firstError;
        }
    }

    /// <summary>Queues clips, including journals whose database save has not succeeded yet, then sweeps.</summary>
    public void Delete(IReadOnlySet<string> ids)
    {
        lock (_gate)
        {
            _database.Write(connection =>
            {
                foreach (var clip in Journals().Where(clip => ids.Contains(clip.Id)))
                    Db.Command(connection, "INSERT OR REPLACE INTO meeting_video_cleanup VALUES($id, $body);")
                        .Bind("$id", clip.Id).Bind("$body", JsonSerializer.Serialize(clip, Json)).Execute();
                foreach (var id in ids)
                {
                    Db.Command(connection, "INSERT OR REPLACE INTO meeting_video_cleanup SELECT id, body FROM meeting_video_clips WHERE id = $id;").Bind("$id", id).Execute();
                    Db.Command(connection, "DELETE FROM meeting_video_clips WHERE id = $id;").Bind("$id", id).Execute();
                }
            });
            Maintain();
        }
    }

    private IEnumerable<MeetingVideoClip> Journals() =>
        Directory.EnumerateFiles(Root).Where(IsJournal).Select(Read).OfType<MeetingVideoClip>();

    private static bool IsJournal(string file) =>
        file.EndsWith(PendingSuffix, StringComparison.Ordinal) || file.EndsWith(RecordingSuffix, StringComparison.Ordinal);

    private static MeetingVideoClip? Read(string file)
    {
        try { return JsonSerializer.Deserialize<MeetingVideoClip>(File.ReadAllText(file), Json); }
        catch (Exception error) when (error is JsonException or IOException) { return null; }
    }

    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
