using System.Text;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Keeps the FTS documents in step with the relational tables.
/// <para>
/// A document is assembled from several tables (a meeting's transcripts, notes and aliases all feed
/// one row), which is why this is code the repositories call inside their own transaction rather
/// than a set of SQL triggers: a trigger would have to fire on five tables and would still see only
/// its own table's change. Indexing inside the writing transaction means a committed record is
/// always findable and a rolled-back one is never left in the index.
/// </para>
/// </summary>
internal static class SearchIndexWriter
{
    private const int MaximumFolderDepth = 64;

    private const string UpsertDocument =
        """
        INSERT INTO search_documents (record_kind, record_id)
        VALUES ($kind, $id)
        ON CONFLICT (record_kind, record_id) DO UPDATE SET record_id = excluded.record_id
        RETURNING doc_id;
        """;

    private const string InsertIndexRow =
        """
        INSERT INTO search_index (
            rowid, record_kind, record_id, folder_id, created_at,
            title, transcript, notes, dictation_text, aliases, folder)
        VALUES (
            $docId, $kind, $id, $folderId, $createdAt,
            $title, $transcript, $notes, $dictationText, $aliases, $folder);
        """;

    public static void IndexDictation(SqliteConnection connection, string id)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT id, title, text, model_profile, folder_id, created_at_utc
            FROM dictations
            WHERE id = $id;
            """)
            .Bind("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            Remove(connection, PersistenceSchema.DictationKind, id);
            return;
        }

        var folderId = reader.Text(4);
        Write(
            connection,
            new SearchDocument(
                PersistenceSchema.DictationKind,
                reader.GetString(0),
                folderId,
                reader.Int64(5),
                Title: reader.TextOrEmpty(1),
                Transcript: "",
                Notes: "",
                DictationText: Join(reader.TextOrEmpty(2), reader.TextOrEmpty(3)),
                Aliases: "",
                Folder: FolderText(connection, folderId)));
    }

    public static void IndexMeeting(SqliteConnection connection, string id)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT
                m.id,
                m.title,
                m.folder_id,
                m.created_at_utc,
                COALESCE((SELECT group_concat(t.content, char(10))
                          FROM meeting_transcripts t WHERE t.meeting_id = m.id), ''),
                COALESCE((SELECT group_concat(n.content, char(10))
                          FROM meeting_notes n WHERE n.meeting_id = m.id), ''),
                COALESCE((SELECT group_concat(a.speaker_key || ' ' || a.alias, char(10))
                          FROM speaker_aliases a WHERE a.meeting_id = m.id), ''),
                COALESCE((SELECT group_concat(f.text || ' ' || f.owner, char(10))
                          FROM follow_ups f WHERE f.meeting_id = m.id), '')
            FROM meetings m
            WHERE m.id = $id;
            """)
            .Bind("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            Remove(connection, PersistenceSchema.MeetingKind, id);
            return;
        }

        var folderId = reader.Text(2);

        // Follow-ups ride in the notes column: they are the actionable half of a meeting's notes and
        // a user searching for "send the pricing deck" does not care which half it landed in.
        var notes = Join(reader.TextOrEmpty(5), reader.TextOrEmpty(7));
        Write(
            connection,
            new SearchDocument(
                PersistenceSchema.MeetingKind,
                reader.GetString(0),
                folderId,
                reader.Int64(3),
                Title: reader.TextOrEmpty(1),
                Transcript: reader.TextOrEmpty(4),
                Notes: notes,
                DictationText: "",
                Aliases: reader.TextOrEmpty(6),
                Folder: FolderText(connection, folderId)));
    }

    public static void IndexFolder(SqliteConnection connection, string id)
    {
        using var command = Db.Command(
            connection,
            "SELECT id, name, metadata, created_at_utc, parent_id FROM folders WHERE id = $id;")
            .Bind("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            Remove(connection, PersistenceSchema.FolderKind, id);
            return;
        }

        Write(
            connection,
            new SearchDocument(
                PersistenceSchema.FolderKind,
                reader.GetString(0),

                // A folder's location is its parent, so restricting a search to a folder finds the
                // subfolders inside it rather than the folder itself.
                FolderId: reader.Text(4),
                CreatedAt: reader.Int64(3),
                Title: reader.TextOrEmpty(1),
                Transcript: "",
                Notes: "",
                DictationText: "",
                Aliases: "",

                // The ancestry walk starts at this folder, so its own name and metadata are
                // already in the text; appending them again would double-count every term.
                Folder: FolderText(connection, reader.GetString(0))));
    }

    /// <summary>
    /// Reindexes everything filed under a folder subtree. A rename changes searchable text on every
    /// record inside it, and leaving those documents stale would make a folder searchable by a name
    /// its contents no longer carry.
    /// </summary>
    public static void IndexFolderMembers(SqliteConnection connection, string folderId)
    {
        foreach (var folder in Subtree(connection, folderId))
        {
            IndexFolder(connection, folder);

            foreach (var id in Ids(connection, "SELECT id FROM dictations WHERE folder_id = $folderId;", folder))
            {
                IndexDictation(connection, id);
            }

            foreach (var id in Ids(connection, "SELECT id FROM meetings WHERE folder_id = $folderId;", folder))
            {
                IndexMeeting(connection, id);
            }
        }
    }

    public static void Remove(SqliteConnection connection, string kind, string id)
    {
        Db.Command(
                connection,
                """
                DELETE FROM search_index
                WHERE rowid IN (
                    SELECT doc_id FROM search_documents WHERE record_kind = $kind AND record_id = $id);
                """)
            .Bind("$kind", kind)
            .Bind("$id", id)
            .Execute();
        Db.Command(connection, "DELETE FROM search_documents WHERE record_kind = $kind AND record_id = $id;")
            .Bind("$kind", kind)
            .Bind("$id", id)
            .Execute();
    }

    public static void RemoveKind(SqliteConnection connection, string kind)
    {
        Db.Command(
                connection,
                """
                DELETE FROM search_index
                WHERE rowid IN (SELECT doc_id FROM search_documents WHERE record_kind = $kind);
                """)
            .Bind("$kind", kind)
            .Execute();
        Db.Command(connection, "DELETE FROM search_documents WHERE record_kind = $kind;")
            .Bind("$kind", kind)
            .Execute();
    }

    /// <summary>Rebuilds every document from the relational tables.</summary>
    public static void Rebuild(SqliteConnection connection)
    {
        Db.Command(connection, "DELETE FROM search_index;").Execute();
        Db.Command(connection, "DELETE FROM search_documents;").Execute();

        foreach (var id in Ids(connection, "SELECT id FROM folders;"))
        {
            IndexFolder(connection, id);
        }

        foreach (var id in Ids(connection, "SELECT id FROM dictations;"))
        {
            IndexDictation(connection, id);
        }

        foreach (var id in Ids(connection, "SELECT id FROM meetings;"))
        {
            IndexMeeting(connection, id);
        }
    }

    /// <summary>
    /// Materialises the ids before any of them is reindexed. Reading and writing through the same
    /// connection at once would leave a reader open across the index writes.
    /// </summary>
    private static List<string> Ids(SqliteConnection connection, string sql, string? folderId = null)
    {
        using var command = Db.Command(connection, sql);
        if (folderId is not null)
        {
            command.Bind("$folderId", folderId);
        }

        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static List<string> Subtree(SqliteConnection connection, string folderId)
    {
        using var command = Db.Command(
            connection,
            """
            WITH RECURSIVE subtree(id, depth) AS (
                SELECT id, 0 FROM folders WHERE id = $id
                UNION ALL
                SELECT f.id, s.depth + 1
                FROM folders f JOIN subtree s ON f.parent_id = s.id
                WHERE s.depth < $maxDepth
            )
            SELECT id FROM subtree;
            """)
            .Bind("$id", folderId)
            .Bind("$maxDepth", MaximumFolderDepth);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>
    /// The folder's name and metadata plus those of its ancestors, so a record inside "Clients /
    /// Northwind" is found by either segment. The depth bound is a cycle guard: reparenting rejects
    /// cycles, but an imported tree is not trusted to be acyclic.
    /// </summary>
    private static string FolderText(SqliteConnection connection, string? folderId)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return "";
        }

        using var command = Db.Command(
            connection,
            """
            WITH RECURSIVE ancestry(id, name, metadata, parent_id, depth) AS (
                SELECT id, name, metadata, parent_id, 0 FROM folders WHERE id = $id
                UNION ALL
                SELECT f.id, f.name, f.metadata, f.parent_id, a.depth + 1
                FROM folders f JOIN ancestry a ON f.id = a.parent_id
                WHERE a.depth < $maxDepth
            )
            SELECT name, metadata FROM ancestry ORDER BY depth DESC;
            """)
            .Bind("$id", folderId)
            .Bind("$maxDepth", MaximumFolderDepth);
        using var reader = command.ExecuteReader();
        var builder = new StringBuilder();
        while (reader.Read())
        {
            Append(builder, reader.TextOrEmpty(0));
            Append(builder, reader.TextOrEmpty(1));
        }

        return builder.ToString();
    }

    private static void Write(SqliteConnection connection, SearchDocument document)
    {
        long docId;
        using (var command = Db.Command(connection, UpsertDocument)
                   .Bind("$kind", document.Kind)
                   .Bind("$id", document.RecordId))
        {
            docId = Convert.ToInt64(command.ExecuteScalar()
                ?? throw new PersistenceException(
                    $"The search document for {document.Kind} '{document.RecordId}' could not be allocated."));
        }

        Db.Command(connection, "DELETE FROM search_index WHERE rowid = $docId;")
            .Bind("$docId", docId)
            .Execute();

        Db.Command(connection, InsertIndexRow)
            .Bind("$docId", docId)
            .Bind("$kind", document.Kind)
            .Bind("$id", document.RecordId)

            // Unfiled records store an empty string rather than NULL so the folder filter is a plain
            // equality test on every row.
            .Bind("$folderId", document.FolderId ?? "")
            .Bind("$createdAt", document.CreatedAt)
            .Bind("$title", document.Title)
            .Bind("$transcript", document.Transcript)
            .Bind("$notes", document.Notes)
            .Bind("$dictationText", document.DictationText)
            .Bind("$aliases", document.Aliases)
            .Bind("$folder", document.Folder)
            .Execute();
    }

    private static string Join(params string[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            Append(builder, part);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(value);
    }

    private sealed record SearchDocument(
        string Kind,
        string RecordId,
        string? FolderId,
        long CreatedAt,
        string Title,
        string Transcript,
        string Notes,
        string DictationText,
        string Aliases,
        string Folder);
}
