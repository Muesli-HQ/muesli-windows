using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed class SqliteMeetingRepository : IMeetingRepository
{
    private const string Columns =
        "id, title, title_is_manual, created_at_utc, updated_at_utc, duration_ms, model_profile, " +
        "folder_id, word_count, template_name, session_state, recovered_from_interruption, " +
        "health_warnings_json, source_path, microphone_audio_path, system_audio_path, " +
        "system_capture_mode, live_preview_model_id, live_transcript_ownership, " +
        "final_transcript_owner_model_id, gap_recovery_model_id, automation_result_json, " +
        "source_schema_version";

    private const string FollowUpColumns =
        "id, meeting_id, text, owner, due_at_utc, status, linked_meeting_id, linked_dictation_id, " +
        "created_at_utc, updated_at_utc";

    private readonly MuesliDatabase _database;

    public SqliteMeetingRepository(MuesliDatabase database) => _database = database;

    public MeetingRecord? Find(string id) =>
        _database.Read(connection => ReadHead(connection, id));

    public MeetingDetail? FindDetail(string id) =>
        _database.Read(connection =>
        {
            var meeting = ReadHead(connection, id);
            return meeting is null
                ? null
                : new MeetingDetail
                {
                    Meeting = meeting,
                    Notes = ReadNotes(connection, id),
                    Transcripts = ReadTranscripts(connection, id),
                    SpeakerAliases = ReadAliases(connection, id),
                    FollowUps = ReadFollowUps(connection, "meeting_id", id)
                };
        });

    public IReadOnlyList<MeetingRecord> List(MeetingQuery query) =>
        _database.Read(connection =>
        {
            var filters = new List<string>();
            if (query.FilterByFolder)
            {
                filters.Add(query.FolderId is null ? "folder_id IS NULL" : "folder_id = $folderId");
            }

            if (query.Since is not null)
            {
                filters.Add("created_at_utc >= $since");
            }

            if (query.Until is not null)
            {
                filters.Add("created_at_utc <= $until");
            }

            if (query.SessionState is not null)
            {
                filters.Add("session_state = $sessionState");
            }

            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            using var command = Db.Command(
                connection,
                $"SELECT {Columns} FROM meetings {where} ORDER BY {Order(query.Sort)} LIMIT $limit OFFSET $offset;");
            if (query.FilterByFolder && query.FolderId is not null)
            {
                command.Bind("$folderId", query.FolderId);
            }

            if (query.Since is not null)
            {
                command.Bind("$since", query.Since.Value);
            }

            if (query.Until is not null)
            {
                command.Bind("$until", query.Until.Value);
            }

            if (query.SessionState is not null)
            {
                command.Bind("$sessionState", PersistenceEnums.ToStorage(query.SessionState.Value));
            }

            command.Bind("$limit", query.Limit).Bind("$offset", query.Offset);
            using var reader = command.ExecuteReader();
            var results = new List<MeetingRecord>();
            while (reader.Read())
            {
                results.Add(Map(reader));
            }

            return (IReadOnlyList<MeetingRecord>)results;
        });

    public int Count() =>
        _database.Read(connection => Db.Command(connection, "SELECT count(*) FROM meetings;").ScalarInt32());

    public void Upsert(MeetingRecord meeting) =>
        _database.Write(connection =>
        {
            WriteHead(connection, meeting);
            SearchIndexWriter.IndexMeeting(connection, meeting.Id);
        });

    public void Save(MeetingDetail detail) =>
        _database.Write(connection => WriteDetail(connection, detail));

    public void SaveRange(IEnumerable<MeetingDetail> details) =>
        _database.Write(connection =>
        {
            foreach (var detail in details)
            {
                WriteDetail(connection, detail);
            }
        });

    public bool Delete(string id) =>
        _database.Write(connection =>
        {
            // The child rows go with it through ON DELETE CASCADE; the index entry is ours to clear.
            var removed = Db.Command(connection, "DELETE FROM meetings WHERE id = $id;")
                .Bind("$id", id)
                .Execute();
            SearchIndexWriter.Remove(connection, PersistenceSchema.MeetingKind, id);
            return removed > 0;
        });

    public int DeleteAll() =>
        _database.Write(connection =>
        {
            var removed = Db.Command(connection, "DELETE FROM meetings;").Execute();
            SearchIndexWriter.RemoveKind(connection, PersistenceSchema.MeetingKind);
            return removed;
        });

    public void MoveToFolder(string id, string? folderId) =>
        _database.Write(connection =>
        {
            Db.Command(
                    connection,
                    "UPDATE meetings SET folder_id = $folderId, updated_at_utc = $updatedAt WHERE id = $id;")
                .Bind("$folderId", folderId)
                .Bind("$updatedAt", DateTimeOffset.UtcNow)
                .Bind("$id", id)
                .Execute();
            SearchIndexWriter.IndexMeeting(connection, id);
        });

    public void SetNote(string meetingId, MeetingNoteKind kind, string content) =>
        _database.Write(connection =>
        {
            RequireMeeting(connection, meetingId);
            if (string.IsNullOrEmpty(content))
            {
                Db.Command(
                        connection,
                        "DELETE FROM meeting_notes WHERE meeting_id = $meetingId AND kind = $kind;")
                    .Bind("$meetingId", meetingId)
                    .Bind("$kind", PersistenceEnums.ToStorage(kind))
                    .Execute();
            }
            else
            {
                WriteNote(connection, meetingId, kind, content, DateTimeOffset.UtcNow);
            }

            SearchIndexWriter.IndexMeeting(connection, meetingId);
        });

    public IReadOnlyList<MeetingNote> ListNotes(string meetingId) =>
        _database.Read(connection => ReadNotes(connection, meetingId));

    public void SetTranscript(string meetingId, MeetingTranscriptKind kind, string content) =>
        _database.Write(connection =>
        {
            RequireMeeting(connection, meetingId);
            if (string.IsNullOrEmpty(content))
            {
                Db.Command(
                        connection,
                        "DELETE FROM meeting_transcripts WHERE meeting_id = $meetingId AND kind = $kind;")
                    .Bind("$meetingId", meetingId)
                    .Bind("$kind", PersistenceEnums.ToStorage(kind))
                    .Execute();
            }
            else
            {
                WriteTranscript(connection, meetingId, kind, content, DateTimeOffset.UtcNow);
            }

            SearchIndexWriter.IndexMeeting(connection, meetingId);
        });

    public IReadOnlyList<MeetingTranscript> ListTranscripts(string meetingId) =>
        _database.Read(connection => ReadTranscripts(connection, meetingId));

    public void ReplaceSpeakerAliases(string meetingId, IReadOnlyDictionary<string, string> aliases) =>
        _database.Write(connection =>
        {
            RequireMeeting(connection, meetingId);
            ReplaceAliases(
                connection,
                meetingId,
                aliases.Select(pair => new SpeakerAliasRecord
                {
                    MeetingId = meetingId,
                    SpeakerKey = pair.Key,
                    Alias = pair.Value
                }).ToList());
            SearchIndexWriter.IndexMeeting(connection, meetingId);
        });

    public IReadOnlyDictionary<string, string> GetSpeakerAliases(string meetingId) =>
        _database.Read(connection => (IReadOnlyDictionary<string, string>)ReadAliases(connection, meetingId)
            .ToDictionary(alias => alias.SpeakerKey, alias => alias.Alias, StringComparer.Ordinal));

    public void UpsertFollowUp(FollowUpRecord followUp) =>
        _database.Write(connection =>
        {
            RequireMeeting(connection, followUp.MeetingId);
            WriteFollowUp(connection, followUp);
            SearchIndexWriter.IndexMeeting(connection, followUp.MeetingId);
        });

    public IReadOnlyList<FollowUpRecord> ListFollowUps(string meetingId) =>
        _database.Read(connection => ReadFollowUps(connection, "meeting_id", meetingId));

    public IReadOnlyList<FollowUpRecord> ListFollowUpsLinkedTo(string meetingId) =>
        _database.Read(connection => ReadFollowUps(connection, "linked_meeting_id", meetingId));

    public bool DeleteFollowUp(string id) =>
        _database.Write(connection =>
        {
            string? meetingId;
            using (var lookup = Db.Command(connection, "SELECT meeting_id FROM follow_ups WHERE id = $id;")
                       .Bind("$id", id))
            {
                meetingId = lookup.ExecuteScalar() as string;
            }

            var removed = Db.Command(connection, "DELETE FROM follow_ups WHERE id = $id;")
                .Bind("$id", id)
                .Execute();
            if (meetingId is not null)
            {
                SearchIndexWriter.IndexMeeting(connection, meetingId);
            }

            return removed > 0;
        });

    private static void WriteDetail(SqliteConnection connection, MeetingDetail detail)
    {
        var meetingId = detail.Meeting.Id;
        WriteHead(connection, detail.Meeting);

        var timestamp = DateTimeOffset.UtcNow;
        foreach (var note in detail.Notes)
        {
            WriteNote(connection, meetingId, note.Kind, note.Content, timestamp, note.CreatedAtUtc);
        }

        DeleteMissing(
            connection,
            "meeting_notes",
            "kind",
            meetingId,
            detail.Notes.Select(note => PersistenceEnums.ToStorage(note.Kind)));

        foreach (var transcript in detail.Transcripts)
        {
            WriteTranscript(
                connection,
                meetingId,
                transcript.Kind,
                transcript.Content,
                timestamp,
                transcript.CreatedAtUtc);
        }

        DeleteMissing(
            connection,
            "meeting_transcripts",
            "kind",
            meetingId,
            detail.Transcripts.Select(transcript => PersistenceEnums.ToStorage(transcript.Kind)));

        ReplaceAliases(connection, meetingId, detail.SpeakerAliases);

        foreach (var followUp in detail.FollowUps)
        {
            WriteFollowUp(connection, followUp with { MeetingId = meetingId });
        }

        DeleteMissing(
            connection,
            "follow_ups",
            "id",
            meetingId,
            detail.FollowUps.Select(followUp => followUp.Id));

        SearchIndexWriter.IndexMeeting(connection, meetingId);
    }

    private static void WriteHead(SqliteConnection connection, MeetingRecord meeting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meeting.Id);
        Db.Command(
                connection,
                """
                INSERT INTO meetings (
                    id, title, title_is_manual, created_at_utc, updated_at_utc, duration_ms,
                    model_profile, folder_id, word_count, template_name, session_state,
                    recovered_from_interruption, health_warnings_json, source_path,
                    microphone_audio_path, system_audio_path, system_capture_mode,
                    live_preview_model_id, live_transcript_ownership,
                    final_transcript_owner_model_id, gap_recovery_model_id,
                    automation_result_json, source_schema_version)
                VALUES (
                    $id, $title, $titleIsManual, $createdAt, $updatedAt, $durationMs,
                    $modelProfile, $folderId, $wordCount, $templateName, $sessionState,
                    $recovered, $healthWarnings, $sourcePath,
                    $microphonePath, $systemPath, $captureMode,
                    $livePreviewModelId, $liveOwnership,
                    $finalOwnerModelId, $gapRecoveryModelId,
                    $automationResult, $sourceSchemaVersion)
                ON CONFLICT (id) DO UPDATE SET
                    title = excluded.title,
                    title_is_manual = excluded.title_is_manual,
                    created_at_utc = excluded.created_at_utc,
                    updated_at_utc = excluded.updated_at_utc,
                    duration_ms = excluded.duration_ms,
                    model_profile = excluded.model_profile,
                    folder_id = excluded.folder_id,
                    word_count = excluded.word_count,
                    template_name = excluded.template_name,
                    session_state = excluded.session_state,
                    recovered_from_interruption = excluded.recovered_from_interruption,
                    health_warnings_json = excluded.health_warnings_json,
                    source_path = excluded.source_path,
                    microphone_audio_path = excluded.microphone_audio_path,
                    system_audio_path = excluded.system_audio_path,
                    system_capture_mode = excluded.system_capture_mode,
                    live_preview_model_id = excluded.live_preview_model_id,
                    live_transcript_ownership = excluded.live_transcript_ownership,
                    final_transcript_owner_model_id = excluded.final_transcript_owner_model_id,
                    gap_recovery_model_id = excluded.gap_recovery_model_id,
                    automation_result_json = excluded.automation_result_json,
                    source_schema_version = excluded.source_schema_version;
                """)
            .Bind("$id", meeting.Id)
            .Bind("$title", meeting.Title)
            .Bind("$titleIsManual", meeting.TitleIsManual)
            .Bind("$createdAt", meeting.CreatedAtUtc)
            .Bind("$updatedAt", meeting.UpdatedAtUtc)
            .Bind("$durationMs", meeting.DurationMs)
            .Bind("$modelProfile", meeting.ModelProfile)
            .Bind("$folderId", meeting.FolderId)
            .Bind("$wordCount", meeting.WordCount)
            .Bind("$templateName", meeting.TemplateName)
            .Bind("$sessionState", PersistenceEnums.ToStorage(meeting.SessionState))
            .Bind("$recovered", meeting.RecoveredFromInterruption)
            .Bind("$healthWarnings", JsonSerializer.Serialize(meeting.HealthWarnings))
            .Bind("$sourcePath", meeting.Audio.SourcePath)
            .Bind("$microphonePath", meeting.Audio.MicrophoneAudioPath)
            .Bind("$systemPath", meeting.Audio.SystemAudioPath)
            .Bind("$captureMode", meeting.Audio.SystemCaptureMode)
            .Bind("$livePreviewModelId", meeting.Audio.LivePreviewModelId)
            .Bind("$liveOwnership", meeting.Audio.LiveTranscriptOwnership)
            .Bind("$finalOwnerModelId", meeting.Audio.FinalTranscriptOwnerModelId)
            .Bind("$gapRecoveryModelId", meeting.Audio.GapRecoveryModelId)
            .Bind("$automationResult", meeting.AutomationResultJson)
            .Bind("$sourceSchemaVersion", meeting.SourceSchemaVersion)
            .Execute();
    }

    private static void WriteNote(
        SqliteConnection connection,
        string meetingId,
        MeetingNoteKind kind,
        string content,
        DateTimeOffset timestamp,
        DateTimeOffset? createdAt = null)
    {
        Db.Command(
                connection,
                """
                INSERT INTO meeting_notes (meeting_id, kind, content, created_at_utc, updated_at_utc)
                VALUES ($meetingId, $kind, $content, $createdAt, $updatedAt)
                ON CONFLICT (meeting_id, kind) DO UPDATE SET
                    content = excluded.content,
                    updated_at_utc = excluded.updated_at_utc;
                """)
            .Bind("$meetingId", meetingId)
            .Bind("$kind", PersistenceEnums.ToStorage(kind))
            .Bind("$content", content)
            .Bind("$createdAt", createdAt ?? timestamp)
            .Bind("$updatedAt", timestamp)
            .Execute();
    }

    private static void WriteTranscript(
        SqliteConnection connection,
        string meetingId,
        MeetingTranscriptKind kind,
        string content,
        DateTimeOffset timestamp,
        DateTimeOffset? createdAt = null)
    {
        Db.Command(
                connection,
                """
                INSERT INTO meeting_transcripts (meeting_id, kind, content, created_at_utc, updated_at_utc)
                VALUES ($meetingId, $kind, $content, $createdAt, $updatedAt)
                ON CONFLICT (meeting_id, kind) DO UPDATE SET
                    content = excluded.content,
                    updated_at_utc = excluded.updated_at_utc;
                """)
            .Bind("$meetingId", meetingId)
            .Bind("$kind", PersistenceEnums.ToStorage(kind))
            .Bind("$content", content)
            .Bind("$createdAt", createdAt ?? timestamp)
            .Bind("$updatedAt", timestamp)
            .Execute();
    }

    private static void WriteFollowUp(SqliteConnection connection, FollowUpRecord followUp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(followUp.Id);
        Db.Command(
                connection,
                """
                INSERT INTO follow_ups (
                    id, meeting_id, text, owner, due_at_utc, status,
                    linked_meeting_id, linked_dictation_id, created_at_utc, updated_at_utc)
                VALUES (
                    $id, $meetingId, $text, $owner, $dueAt, $status,
                    $linkedMeetingId, $linkedDictationId, $createdAt, $updatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    meeting_id = excluded.meeting_id,
                    text = excluded.text,
                    owner = excluded.owner,
                    due_at_utc = excluded.due_at_utc,
                    status = excluded.status,
                    linked_meeting_id = excluded.linked_meeting_id,
                    linked_dictation_id = excluded.linked_dictation_id,
                    updated_at_utc = excluded.updated_at_utc;
                """)
            .Bind("$id", followUp.Id)
            .Bind("$meetingId", followUp.MeetingId)
            .Bind("$text", followUp.Text)
            .Bind("$owner", followUp.Owner)
            .Bind("$dueAt", followUp.DueAtUtc)
            .Bind("$status", PersistenceEnums.ToStorage(followUp.Status))
            .Bind("$linkedMeetingId", followUp.LinkedMeetingId)
            .Bind("$linkedDictationId", followUp.LinkedDictationId)
            .Bind("$createdAt", followUp.CreatedAtUtc)
            .Bind("$updatedAt", followUp.UpdatedAtUtc)
            .Execute();
    }

    private static void ReplaceAliases(
        SqliteConnection connection,
        string meetingId,
        IReadOnlyList<SpeakerAliasRecord> aliases)
    {
        foreach (var alias in aliases)
        {
            Db.Command(
                    connection,
                    """
                    INSERT INTO speaker_aliases (meeting_id, speaker_key, alias)
                    VALUES ($meetingId, $speakerKey, $alias)
                    ON CONFLICT (meeting_id, speaker_key) DO UPDATE SET alias = excluded.alias;
                    """)
                .Bind("$meetingId", meetingId)
                .Bind("$speakerKey", alias.SpeakerKey)
                .Bind("$alias", alias.Alias)
                .Execute();
        }

        DeleteMissing(
            connection,
            "speaker_aliases",
            "speaker_key",
            meetingId,
            aliases.Select(alias => alias.SpeakerKey));
    }

    /// <summary>
    /// Removes the rows of a meeting's child table whose key is not in <paramref name="keep"/>. The
    /// caller's collection is authoritative, so an alias or note the caller dropped is dropped here.
    /// </summary>
    private static void DeleteMissing(
        SqliteConnection connection,
        string table,
        string keyColumn,
        string meetingId,
        IEnumerable<string> keep)
    {
        var keys = keep.ToList();
        var placeholders = string.Join(", ", keys.Select((_, index) => $"$key{index}"));
        var filter = keys.Count == 0 ? "" : $" AND {keyColumn} NOT IN ({placeholders})";
        using var command = Db.Command(
            connection,
            $"DELETE FROM {table} WHERE meeting_id = $meetingId{filter};")
            .Bind("$meetingId", meetingId);
        for (var index = 0; index < keys.Count; index++)
        {
            command.Bind($"$key{index}", keys[index]);
        }

        command.Execute();
    }

    private static void RequireMeeting(SqliteConnection connection, string meetingId)
    {
        var exists = Db.Command(connection, "SELECT count(*) FROM meetings WHERE id = $id;")
            .Bind("$id", meetingId)
            .ScalarInt32();
        if (exists == 0)
        {
            throw new PersistenceException($"Meeting '{meetingId}' does not exist.");
        }
    }

    private static MeetingRecord? ReadHead(SqliteConnection connection, string id)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM meetings WHERE id = $id;")
            .Bind("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static IReadOnlyList<MeetingNote> ReadNotes(SqliteConnection connection, string meetingId)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT meeting_id, kind, content, created_at_utc, updated_at_utc
            FROM meeting_notes WHERE meeting_id = $meetingId ORDER BY kind;
            """)
            .Bind("$meetingId", meetingId);
        using var reader = command.ExecuteReader();
        var notes = new List<MeetingNote>();
        while (reader.Read())
        {
            notes.Add(new MeetingNote
            {
                MeetingId = reader.GetString(0),
                Kind = PersistenceEnums.NoteKind(reader.GetString(1)),
                Content = reader.TextOrEmpty(2),
                CreatedAtUtc = reader.Instant(3),
                UpdatedAtUtc = reader.Instant(4)
            });
        }

        return notes;
    }

    private static IReadOnlyList<MeetingTranscript> ReadTranscripts(SqliteConnection connection, string meetingId)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT meeting_id, kind, content, created_at_utc, updated_at_utc
            FROM meeting_transcripts WHERE meeting_id = $meetingId ORDER BY kind;
            """)
            .Bind("$meetingId", meetingId);
        using var reader = command.ExecuteReader();
        var transcripts = new List<MeetingTranscript>();
        while (reader.Read())
        {
            transcripts.Add(new MeetingTranscript
            {
                MeetingId = reader.GetString(0),
                Kind = PersistenceEnums.TranscriptKind(reader.GetString(1)),
                Content = reader.TextOrEmpty(2),
                CreatedAtUtc = reader.Instant(3),
                UpdatedAtUtc = reader.Instant(4)
            });
        }

        return transcripts;
    }

    private static IReadOnlyList<SpeakerAliasRecord> ReadAliases(SqliteConnection connection, string meetingId)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT meeting_id, speaker_key, alias
            FROM speaker_aliases WHERE meeting_id = $meetingId ORDER BY speaker_key;
            """)
            .Bind("$meetingId", meetingId);
        using var reader = command.ExecuteReader();
        var aliases = new List<SpeakerAliasRecord>();
        while (reader.Read())
        {
            aliases.Add(new SpeakerAliasRecord
            {
                MeetingId = reader.GetString(0),
                SpeakerKey = reader.GetString(1),
                Alias = reader.TextOrEmpty(2)
            });
        }

        return aliases;
    }

    private static IReadOnlyList<FollowUpRecord> ReadFollowUps(
        SqliteConnection connection,
        string keyColumn,
        string keyValue)
    {
        using var command = Db.Command(
            connection,
            $"""
             SELECT {FollowUpColumns}
             FROM follow_ups WHERE {keyColumn} = $key ORDER BY created_at_utc, id;
             """)
            .Bind("$key", keyValue);
        using var reader = command.ExecuteReader();
        var followUps = new List<FollowUpRecord>();
        while (reader.Read())
        {
            followUps.Add(new FollowUpRecord
            {
                Id = reader.GetString(0),
                MeetingId = reader.GetString(1),
                Text = reader.TextOrEmpty(2),
                Owner = reader.TextOrEmpty(3),
                DueAtUtc = reader.NullableInstant(4),
                Status = PersistenceEnums.FollowUp(reader.GetString(5)),
                LinkedMeetingId = reader.Text(6),
                LinkedDictationId = reader.Text(7),
                CreatedAtUtc = reader.Instant(8),
                UpdatedAtUtc = reader.Instant(9)
            });
        }

        return followUps;
    }

    private static string Order(HistorySort sort) =>
        sort switch
        {
            HistorySort.OldestFirst => "created_at_utc ASC, id ASC",
            HistorySort.TitleAscending => "title COLLATE NOCASE ASC, created_at_utc DESC",
            _ => "created_at_utc DESC, id ASC"
        };

    private static MeetingRecord Map(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Title = reader.TextOrEmpty(1),
            TitleIsManual = reader.Flag(2),
            CreatedAtUtc = reader.Instant(3),
            UpdatedAtUtc = reader.Instant(4),
            DurationMs = reader.Int32(5),
            ModelProfile = reader.TextOrEmpty(6),
            FolderId = reader.Text(7),
            WordCount = reader.Int32(8),
            TemplateName = reader.TextOrEmpty(9),
            SessionState = PersistenceEnums.SessionState(reader.TextOrEmpty(10)),
            RecoveredFromInterruption = reader.Flag(11),
            HealthWarnings = ReadWarnings(reader.TextOrEmpty(12)),
            Audio = new MeetingAudioOwnership
            {
                SourcePath = reader.TextOrEmpty(13),
                MicrophoneAudioPath = reader.Text(14),
                SystemAudioPath = reader.Text(15),
                SystemCaptureMode = reader.TextOrEmpty(16),
                LivePreviewModelId = reader.Text(17),
                LiveTranscriptOwnership = reader.TextOrEmpty(18),
                FinalTranscriptOwnerModelId = reader.TextOrEmpty(19),
                GapRecoveryModelId = reader.Text(20)
            },
            AutomationResultJson = reader.Text(21),
            SourceSchemaVersion = reader.Int32(22)
        };

    private static IReadOnlyList<string> ReadWarnings(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A warning list is diagnostics. Losing it must not make the meeting unreadable.
            return [];
        }
    }
}
