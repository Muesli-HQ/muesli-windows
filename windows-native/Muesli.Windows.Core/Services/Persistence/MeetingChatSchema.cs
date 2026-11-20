using System.Text;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Schema 3: Ask Meetings threads and its rebuildable passage index. Port of macOS MeetingChatSQL.migrate
/// (2f10b23d) onto the Windows tables. macOS keeps meeting content in columns of one row; here it is
/// spread over meetings, meeting_notes, meeting_transcripts and speaker_aliases, so each of those tables
/// carries the same invalidation triggers. Meetings are hard-deleted, so the macOS soft-delete triggers
/// become one BEFORE DELETE trigger and the foreign keys cascade the derived rows.
/// <para>
/// Triggers rather than code (unlike search_documents): every trigger here only marks or invalidates
/// derived state for its own meeting, and deletion must invalidate answers on every write path.
/// </para>
/// </summary>
internal static class MeetingChatSchema
{
    public static string Sql
    {
        get
        {
            var sql = new StringBuilder(Tables);
            sql.AppendLine(MeetingTriggers);
            foreach (var table in new[] { "meeting_notes", "meeting_transcripts", "speaker_aliases" })
            {
                foreach (var (name, owner) in new[] { ("INSERT", "NEW"), ("UPDATE", "NEW"), ("DELETE", "OLD") })
                {
                    sql.AppendLine($"""
                        CREATE TRIGGER meeting_chat_{table}_{name.ToLowerInvariant()} AFTER {name} ON {table}
                        BEGIN
                            {ContentChanged($"{owner}.meeting_id")}
                        END;
                        """);
                }
            }
            sql.AppendLine(TurnTriggers);
            // Every existing meeting is indexed on first use.
            sql.AppendLine("INSERT INTO meeting_chat_dirty_sources (meeting_id) SELECT id FROM meetings;");
            return sql.ToString();
        }
    }

    private const string Tables = """
        CREATE TABLE meeting_chat_threads (
            id              TEXT PRIMARY KEY NOT NULL,
            body            TEXT NOT NULL,
            updated_at_utc  INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE meeting_chat_thread_meetings (
            session_id  TEXT NOT NULL REFERENCES meeting_chat_threads(id) ON DELETE CASCADE,
            meeting_id  TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            role        TEXT NOT NULL CHECK (role IN ('anchor', 'selected')),
            PRIMARY KEY (session_id, meeting_id, role)
        ) STRICT;

        CREATE INDEX ix_meeting_chat_thread_meetings_source ON meeting_chat_thread_meetings (meeting_id, session_id);
        CREATE UNIQUE INDEX ux_meeting_chat_single_anchor ON meeting_chat_thread_meetings (session_id) WHERE role = 'anchor';

        CREATE TABLE meeting_chat_turns (
            id          TEXT PRIMARY KEY NOT NULL,
            session_id  TEXT NOT NULL REFERENCES meeting_chat_threads(id) ON DELETE CASCADE,
            ordinal     INTEGER NOT NULL,
            state       TEXT NOT NULL,
            body        TEXT NOT NULL,
            UNIQUE (session_id, ordinal)
        ) STRICT;

        CREATE TABLE meeting_chat_dependencies (
            turn_id     TEXT NOT NULL REFERENCES meeting_chat_turns(id) ON DELETE CASCADE,
            meeting_id  TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            revision    TEXT NOT NULL,
            PRIMARY KEY (turn_id, meeting_id)
        ) STRICT;

        CREATE INDEX ix_meeting_chat_dependencies_source ON meeting_chat_dependencies (meeting_id);

        CREATE TABLE meeting_chat_passages (
            seq         INTEGER PRIMARY KEY,
            id          TEXT NOT NULL UNIQUE,
            meeting_id  TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            revision    TEXT NOT NULL,
            text        TEXT NOT NULL,
            body        TEXT NOT NULL
        ) STRICT;

        CREATE INDEX ix_meeting_chat_passages_source ON meeting_chat_passages (meeting_id);

        CREATE TABLE meeting_chat_dirty_sources (
            meeting_id  TEXT PRIMARY KEY NOT NULL REFERENCES meetings(id) ON DELETE CASCADE
        ) STRICT;

        CREATE TABLE meeting_chat_index_state (
            meeting_id      TEXT PRIMARY KEY NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            revision        TEXT NOT NULL,
            start_time_utc  INTEGER NOT NULL,
            folder_id       TEXT NULL
        ) STRICT;

        CREATE INDEX ix_meeting_chat_index_scope ON meeting_chat_index_state (folder_id, start_time_utc);

        CREATE TABLE meeting_chat_source_version (
            id      INTEGER PRIMARY KEY NOT NULL CHECK (id = 1),
            version INTEGER NOT NULL
        ) STRICT;

        INSERT INTO meeting_chat_source_version VALUES (1, 0);

        CREATE VIRTUAL TABLE meeting_chat_fts USING fts5 (text);

        CREATE TRIGGER meeting_chat_passage_inserted AFTER INSERT ON meeting_chat_passages
        BEGIN INSERT INTO meeting_chat_fts (rowid, text) VALUES (NEW.seq, NEW.text); END;

        CREATE TRIGGER meeting_chat_passage_removed AFTER DELETE ON meeting_chat_passages
        BEGIN DELETE FROM meeting_chat_fts WHERE rowid = OLD.seq; END;

        """;

    private static readonly string MeetingTriggers = $"""
        CREATE TRIGGER meeting_chat_meeting_inserted AFTER INSERT ON meetings
        BEGIN
            {MarkDirty("NEW.id")}
            {BumpVersion}
        END;

        CREATE TRIGGER meeting_chat_meeting_edited AFTER UPDATE OF title, created_at_utc, session_state ON meetings
        BEGIN
            {ContentChanged("NEW.id")}
        END;

        -- Folder membership changes retrieval scope, never source content.
        CREATE TRIGGER meeting_chat_meeting_moved AFTER UPDATE OF folder_id ON meetings
        BEGIN
            {MarkDirty("NEW.id")}
            {BumpVersion}
        END;

        -- Runs before the cascade removes the dependencies that identify derived answers.
        CREATE TRIGGER meeting_chat_meeting_deleted BEFORE DELETE ON meetings
        BEGIN
            UPDATE meeting_chat_turns SET state = 'sourceDeleted', body = json_set(body,
                '$.state', 'sourceDeleted', '$.originalAnswer', NULL, '$.editableDraft', NULL, '$.citations', json('[]'), '$.error', NULL)
            WHERE id IN (SELECT turn_id FROM meeting_chat_dependencies WHERE meeting_id = OLD.id);
            {InvalidateGeneratedTitles("OLD.id")}
            {InvalidateRollingSummaries("OLD.id")}
            {BumpVersion}
        END;

        """;

    private const string TurnTriggers = """
        CREATE TRIGGER meeting_chat_summary_turn_changed AFTER UPDATE OF state, body ON meeting_chat_turns
        WHEN OLD.state IS NOT NEW.state OR json_extract(OLD.body, '$.question') IS NOT json_extract(NEW.body, '$.question')
          OR json_extract(OLD.body, '$.originalAnswer') IS NOT json_extract(NEW.body, '$.originalAnswer')
          OR json_extract(OLD.body, '$.attemptId') IS NOT json_extract(NEW.body, '$.attemptId')
        BEGIN
            UPDATE meeting_chat_threads SET body = json_remove(body, '$.rollingSummary') WHERE EXISTS
              (SELECT 1 FROM json_each(body, '$.rollingSummary.coveredTurnFingerprints') WHERE key = NEW.id);
        END;

        CREATE TRIGGER meeting_chat_summary_turn_removed BEFORE DELETE ON meeting_chat_turns
        BEGIN
            UPDATE meeting_chat_threads SET body = json_remove(body, '$.rollingSummary') WHERE EXISTS
              (SELECT 1 FROM json_each(body, '$.rollingSummary.coveredTurnFingerprints') WHERE key = OLD.id);
        END;
        """;

    private const string BumpVersion = "UPDATE meeting_chat_source_version SET version = version + 1 WHERE id = 1;";

    /// <summary>
    /// macOS: an outer UPSERT's conflict policy can override a trigger's OR IGNORE, so guard with NOT EXISTS.
    /// A child row deleted by its meeting's own cascade must not reference the vanishing meeting.
    /// </summary>
    private static string MarkDirty(string meetingId) =>
        $"INSERT INTO meeting_chat_dirty_sources (meeting_id) SELECT id FROM meetings WHERE id = {meetingId} " +
        $"AND NOT EXISTS (SELECT 1 FROM meeting_chat_dirty_sources WHERE meeting_id = {meetingId});";

    private static string ContentChanged(string meetingId) =>
        $"""
        {MarkDirty(meetingId)}
            {BumpVersion}
            {InvalidateGeneratedTitles(meetingId)}
            {InvalidateRollingSummaries(meetingId)}
        """;

    internal static string InvalidateRollingSummaries(string meetingId) =>
        $"""
        UPDATE meeting_chat_threads SET body = json_remove(body, '$.rollingSummary') WHERE EXISTS
              (SELECT 1 FROM json_each(body, '$.rollingSummary.dependencies') WHERE json_extract(value, '$.meetingId') = {meetingId});
        """;

    internal static string InvalidateGeneratedTitles(string meetingId) =>
        $"""
        UPDATE meeting_chat_threads SET body = json_set(json_remove(body, '$.generatedTitleTurnId'), '$.title', 'New chat')
            WHERE json_extract(body, '$.titleIsAutomatic') = 1 AND json_extract(body, '$.generatedTitleTurnId') IN
              (SELECT turn_id FROM meeting_chat_dependencies WHERE meeting_id = {meetingId});
        """;
}
