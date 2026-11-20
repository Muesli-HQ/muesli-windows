using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// One numbered, forward-only step. A released version is never edited afterwards: a database in the
/// wild has already run it, so a correction has to arrive as the next version instead.
/// </summary>
public sealed record SchemaMigration(int Version, string Description, Action<SqliteConnection> Apply);

/// <summary>
/// The database schema, expressed as an ordered list of migrations.
/// <para>
/// Timestamps are stored as UTC ticks in INTEGER columns. Ticks rather than seconds or milliseconds
/// because a history imported from JSON must hash identically before and after the move, and
/// rounding a timestamp is a silent loss the verification would then have to forgive.
/// </para>
/// </summary>
public static class PersistenceSchema
{
    /// <summary>The schema this build writes and expects.</summary>
    public const int CurrentVersion = 5;

    public const string DictationKind = "dictation";
    public const string MeetingKind = "meeting";
    public const string FolderKind = "folder";

    private const string InitialSchema = """
        CREATE TABLE schema_migrations (
            version         INTEGER PRIMARY KEY NOT NULL,
            description     TEXT NOT NULL,
            applied_at_utc  INTEGER NOT NULL
        ) STRICT;

        CREATE TABLE folders (
            id              TEXT PRIMARY KEY NOT NULL,
            name            TEXT NOT NULL,
            parent_id       TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
            sort_order      INTEGER NOT NULL DEFAULT 0,
            metadata        TEXT NOT NULL DEFAULT '',
            created_at_utc  INTEGER NOT NULL,
            updated_at_utc  INTEGER NOT NULL
        ) STRICT;

        CREATE INDEX ix_folders_parent ON folders (parent_id, sort_order, name);

        CREATE TABLE dictations (
            id              TEXT PRIMARY KEY NOT NULL,
            created_at_utc  INTEGER NOT NULL,
            updated_at_utc  INTEGER NOT NULL,
            title           TEXT NOT NULL DEFAULT '',
            text            TEXT NOT NULL DEFAULT '',
            duration_ms     INTEGER NOT NULL DEFAULT 0,
            model_profile   TEXT NOT NULL DEFAULT '',
            folder_id       TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
            word_count      INTEGER NOT NULL DEFAULT 0,
            audio_path      TEXT NULL,
            audio_retained  INTEGER NOT NULL DEFAULT 0
        ) STRICT;

        CREATE INDEX ix_dictations_created ON dictations (created_at_utc DESC);
        CREATE INDEX ix_dictations_folder ON dictations (folder_id, created_at_utc DESC);

        CREATE TABLE meetings (
            id                              TEXT PRIMARY KEY NOT NULL,
            title                           TEXT NOT NULL DEFAULT '',
            title_is_manual                 INTEGER NOT NULL DEFAULT 0,
            created_at_utc                  INTEGER NOT NULL,
            updated_at_utc                  INTEGER NOT NULL,
            duration_ms                     INTEGER NOT NULL DEFAULT 0,
            model_profile                   TEXT NOT NULL DEFAULT '',
            folder_id                       TEXT NULL REFERENCES folders(id) ON DELETE SET NULL,
            word_count                      INTEGER NOT NULL DEFAULT 0,
            template_name                   TEXT NOT NULL DEFAULT '',
            session_state                   TEXT NOT NULL DEFAULT 'Completed',
            recovered_from_interruption     INTEGER NOT NULL DEFAULT 0,
            health_warnings_json            TEXT NOT NULL DEFAULT '[]',
            source_path                     TEXT NOT NULL DEFAULT '',
            microphone_audio_path           TEXT NULL,
            system_audio_path               TEXT NULL,
            system_capture_mode             TEXT NOT NULL DEFAULT '',
            live_preview_model_id           TEXT NULL,
            live_transcript_ownership       TEXT NOT NULL DEFAULT 'off',
            final_transcript_owner_model_id TEXT NOT NULL DEFAULT '',
            gap_recovery_model_id           TEXT NULL,
            automation_result_json          TEXT NULL,
            source_schema_version           INTEGER NOT NULL DEFAULT 0
        ) STRICT;

        CREATE INDEX ix_meetings_created ON meetings (created_at_utc DESC);
        CREATE INDEX ix_meetings_folder ON meetings (folder_id, created_at_utc DESC);
        CREATE INDEX ix_meetings_state ON meetings (session_state, created_at_utc DESC);

        CREATE TABLE meeting_notes (
            meeting_id      TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            kind            TEXT NOT NULL CHECK (kind IN ('generated', 'manual')),
            content         TEXT NOT NULL DEFAULT '',
            created_at_utc  INTEGER NOT NULL,
            updated_at_utc  INTEGER NOT NULL,
            PRIMARY KEY (meeting_id, kind)
        ) STRICT;

        CREATE TABLE meeting_transcripts (
            meeting_id      TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            kind            TEXT NOT NULL CHECK (kind IN ('raw', 'edited')),
            content         TEXT NOT NULL DEFAULT '',
            created_at_utc  INTEGER NOT NULL,
            updated_at_utc  INTEGER NOT NULL,
            PRIMARY KEY (meeting_id, kind)
        ) STRICT;

        CREATE TABLE speaker_aliases (
            meeting_id      TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            speaker_key     TEXT NOT NULL,
            alias           TEXT NOT NULL,
            PRIMARY KEY (meeting_id, speaker_key)
        ) STRICT;

        CREATE TABLE templates (
            id              TEXT PRIMARY KEY NOT NULL,
            name            TEXT NOT NULL,
            prompt          TEXT NOT NULL DEFAULT '',
            icon            TEXT NOT NULL DEFAULT '',
            sort_order      INTEGER NOT NULL DEFAULT 0,
            created_at_utc  INTEGER NOT NULL,
            updated_at_utc  INTEGER NOT NULL
        ) STRICT;

        CREATE INDEX ix_templates_order ON templates (sort_order, name);

        CREATE TABLE follow_ups (
            id                  TEXT PRIMARY KEY NOT NULL,
            meeting_id          TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            text                TEXT NOT NULL DEFAULT '',
            owner               TEXT NOT NULL DEFAULT '',
            due_at_utc          INTEGER NULL,
            status              TEXT NOT NULL DEFAULT 'open'
                                CHECK (status IN ('open', 'done', 'dismissed')),
            linked_meeting_id   TEXT NULL REFERENCES meetings(id) ON DELETE SET NULL,
            linked_dictation_id TEXT NULL REFERENCES dictations(id) ON DELETE SET NULL,
            created_at_utc      INTEGER NOT NULL,
            updated_at_utc      INTEGER NOT NULL
        ) STRICT;

        CREATE INDEX ix_follow_ups_meeting ON follow_ups (meeting_id, status, created_at_utc);
        CREATE INDEX ix_follow_ups_linked_meeting ON follow_ups (linked_meeting_id);
        CREATE INDEX ix_follow_ups_linked_dictation ON follow_ups (linked_dictation_id);

        CREATE TABLE search_documents (
            doc_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            record_kind TEXT NOT NULL,
            record_id   TEXT NOT NULL
        ) STRICT;

        CREATE UNIQUE INDEX ux_search_documents ON search_documents (record_kind, record_id);

        CREATE TABLE migration_runs (
            id                  TEXT PRIMARY KEY NOT NULL,
            source_fingerprint  TEXT NOT NULL,
            state               TEXT NOT NULL
                                CHECK (state IN ('started', 'completed', 'failed')),
            started_at_utc      INTEGER NOT NULL,
            completed_at_utc    INTEGER NULL,
            dictation_count     INTEGER NOT NULL DEFAULT 0,
            meeting_count       INTEGER NOT NULL DEFAULT 0,
            folder_count        INTEGER NOT NULL DEFAULT 0,
            template_count      INTEGER NOT NULL DEFAULT 0,
            backup_path         TEXT NULL,
            failure             TEXT NULL
        ) STRICT;

        CREATE INDEX ix_migration_runs_state ON migration_runs (state, started_at_utc DESC);
        """;

    /// <summary>
    /// One row per record, with the searchable text split across columns so a query can be narrowed
    /// to titles or notes with FTS5's own column filter instead of filtering hits afterwards. The
    /// leading UNINDEXED columns carry the identity and the sort/filter keys, which keeps the common
    /// "search within this folder, newest first" query inside the index.
    /// </summary>
    private const string SearchIndexSchema = """
        CREATE VIRTUAL TABLE search_index USING fts5 (
            record_kind UNINDEXED,
            record_id UNINDEXED,
            folder_id UNINDEXED,
            created_at UNINDEXED,
            title,
            transcript,
            notes,
            dictation_text,
            aliases,
            folder,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    private const string CutoverStateSchema = """
        CREATE TABLE persistence_cutover_state (
            id                              INTEGER PRIMARY KEY NOT NULL CHECK (id = 1),
            authority                       TEXT NOT NULL CHECK (authority IN ('json', 'sqlite')),
            source_fingerprint              TEXT NOT NULL DEFAULT '',
            activated_at_utc                INTEGER NULL,
            retained_backup_path            TEXT NULL,
            first_post_cutover_write_at_utc INTEGER NULL,
            first_post_cutover_write        TEXT NULL
        ) STRICT;

        INSERT INTO persistence_cutover_state (id, authority)
        VALUES (1, 'json');
        """;

    public static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        new SchemaMigration(
            1,
            "Dictations, meetings, notes, transcripts, folders, templates, aliases, follow-ups and full-text search.",
            connection =>
            {
                Execute(connection, InitialSchema);
                Execute(connection, SearchIndexSchema);
            }),
        new SchemaMigration(
            2,
            "Durable JSON/SQLite cutover authority and first-write state.",
            connection => Execute(connection, CutoverStateSchema)),
        new SchemaMigration(
            3,
            "Ask Meetings threads, source dependencies and the rebuildable passage index.",
            connection => Execute(connection, MeetingChatSchema.Sql)),
        new SchemaMigration(
            4,
            "Meeting screen-recording clips and their durable file-cleanup outbox.",
            connection => Execute(connection, MeetingVideoSchema)),
        new SchemaMigration(
            5,
            "Cotypist typeahead history: suggestions and per-app sessions (local only).",
            connection => Execute(connection, CotypistSchema))
    ];

    /// <summary>
    /// macOS DictationStore.migrateCotypist (2f10b23d). Local-only, no sync identity. The app and the
    /// user's sentence are written only for accepted or partial suggestions, so the macOS scrubbing
    /// UPDATEs for older rows have nothing to fix on Windows. Times are UTC ticks like every table here.
    /// </summary>
    private const string CotypistSchema = """
        CREATE TABLE cotypist_sessions (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at_utc       INTEGER NOT NULL,
            last_activity_at_utc INTEGER NOT NULL,
            target_app_name      TEXT NOT NULL DEFAULT '',
            target_app_id        TEXT NOT NULL DEFAULT '',
            source               TEXT NOT NULL DEFAULT 'cotypist'
        ) STRICT;

        CREATE INDEX ix_cotypist_sessions_app_activity ON cotypist_sessions (target_app_id, last_activity_at_utc DESC);

        CREATE TABLE cotypist_suggestions (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at_utc     INTEGER NOT NULL,
            session_id         INTEGER NULL REFERENCES cotypist_sessions(id) ON DELETE SET NULL,
            target_app_name    TEXT NOT NULL DEFAULT '',
            target_app_id      TEXT NOT NULL DEFAULT '',
            trigger            TEXT NOT NULL,
            suggestion_text    TEXT NOT NULL,
            accepted_text      TEXT NOT NULL DEFAULT '',
            outcome            TEXT NOT NULL,
            words_suggested    INTEGER NOT NULL DEFAULT 0,
            words_accepted     INTEGER NOT NULL DEFAULT 0,
            first_word_logprob REAL NULL,
            first_ms           INTEGER NULL,
            full_ms            INTEGER NULL,
            context_source     TEXT NOT NULL DEFAULT '',
            model              TEXT NOT NULL DEFAULT '',
            context_before     TEXT NOT NULL DEFAULT ''
        ) STRICT;

        CREATE INDEX ix_cotypist_suggestions_session ON cotypist_suggestions (session_id);
        CREATE INDEX ix_cotypist_suggestions_time ON cotypist_suggestions (created_at_utc);
        """;

    /// <summary>
    /// macOS MeetingVideoStore.migrate (2f10b23d). The outbox has no meeting FK so a queued file survives
    /// the meeting row. Windows deletes meetings outright (no tombstones), so only the hard-delete
    /// trigger applies; it queues cleanup before the cascade removes the clip rows.
    /// </summary>
    private const string MeetingVideoSchema = """
        CREATE TABLE meeting_video_clips (
            id              TEXT PRIMARY KEY NOT NULL,
            kind            TEXT NOT NULL,
            meeting_id      TEXT NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
            captured_at_utc INTEGER NOT NULL,
            body            TEXT NOT NULL
        ) STRICT;

        CREATE INDEX ix_meeting_video_clips_time ON meeting_video_clips (kind, captured_at_utc);
        CREATE INDEX ix_meeting_video_clips_meeting ON meeting_video_clips (meeting_id);

        CREATE TABLE meeting_video_cleanup (
            id   TEXT PRIMARY KEY NOT NULL,
            body TEXT NOT NULL
        ) STRICT;

        CREATE TRIGGER meeting_video_hard_delete BEFORE DELETE ON meetings
        BEGIN
            INSERT OR REPLACE INTO meeting_video_cleanup SELECT id, body FROM meeting_video_clips WHERE meeting_id = OLD.id;
        END;
        """;

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
