using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed class SqliteFolderRepository : IFolderRepository
{
    private const int MaximumDepth = 64;

    private const string Columns =
        "id, name, parent_id, sort_order, metadata, created_at_utc, updated_at_utc";

    private readonly MuesliDatabase _database;

    public SqliteFolderRepository(MuesliDatabase database) => _database = database;

    public FolderRecord? Find(string id) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(connection, $"SELECT {Columns} FROM folders WHERE id = $id;")
                .Bind("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        });

    public IReadOnlyList<FolderRecord> List() =>
        _database.Read(connection => Read(
            connection,
            $"SELECT {Columns} FROM folders ORDER BY sort_order, name COLLATE NOCASE;"));

    public IReadOnlyList<FolderRecord> ListChildren(string? parentId) =>
        _database.Read(connection =>
        {
            var filter = parentId is null ? "parent_id IS NULL" : "parent_id = $parentId";
            using var command = Db.Command(
                connection,
                $"SELECT {Columns} FROM folders WHERE {filter} ORDER BY sort_order, name COLLATE NOCASE;");
            if (parentId is not null)
            {
                command.Bind("$parentId", parentId);
            }

            return Read(command);
        });

    public IReadOnlyList<FolderRecord> ListAncestors(string id) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                $"""
                 WITH RECURSIVE ancestry(id, depth) AS (
                     SELECT parent_id, 0 FROM folders WHERE id = $id AND parent_id IS NOT NULL
                     UNION ALL
                     SELECT f.parent_id, a.depth + 1
                     FROM folders f JOIN ancestry a ON f.id = a.id
                     WHERE f.parent_id IS NOT NULL AND a.depth < $maxDepth
                 )
                 SELECT {Prefixed(Columns, "f")}
                 FROM ancestry a JOIN folders f ON f.id = a.id
                 ORDER BY a.depth DESC;
                 """)
                .Bind("$id", id)
                .Bind("$maxDepth", MaximumDepth);
            return Read(command);
        });

    public IReadOnlyList<FolderRecord> ListSubtree(string id) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                $"""
                 WITH RECURSIVE subtree(id, depth) AS (
                     SELECT id, 0 FROM folders WHERE id = $id
                     UNION ALL
                     SELECT f.id, s.depth + 1
                     FROM folders f JOIN subtree s ON f.parent_id = s.id
                     WHERE s.depth < $maxDepth
                 )
                 SELECT {Prefixed(Columns, "f")}
                 FROM subtree s JOIN folders f ON f.id = s.id
                 ORDER BY s.depth, f.sort_order, f.name COLLATE NOCASE;
                 """)
                .Bind("$id", id)
                .Bind("$maxDepth", MaximumDepth);
            return Read(command);
        });

    public void Upsert(FolderRecord folder) =>
        _database.Write(connection =>
        {
            Write(connection, folder);
            SearchIndexWriter.IndexFolderMembers(connection, folder.Id);
        });

    public void UpsertRange(IEnumerable<FolderRecord> folders) =>
        _database.Write(connection =>
        {
            var written = new List<string>();
            foreach (var folder in folders)
            {
                Write(connection, folder);
                written.Add(folder.Id);
            }

            // Indexed after the whole batch: a child written before its parent would otherwise be
            // indexed with an incomplete folder path.
            foreach (var id in written)
            {
                SearchIndexWriter.IndexFolderMembers(connection, id);
            }
        });

    public void Move(string id, string? newParentId) =>
        _database.Write(connection =>
        {
            if (Find(connection, id) is null)
            {
                throw new PersistenceException($"Folder '{id}' does not exist.");
            }

            if (string.Equals(id, newParentId, StringComparison.Ordinal))
            {
                throw new PersistenceException($"Folder '{id}' cannot be its own parent.");
            }

            if (newParentId is not null)
            {
                var descendants = Descendants(connection, id);
                if (descendants.Contains(newParentId))
                {
                    throw new PersistenceException(
                        $"Folder '{id}' cannot be moved inside its own subtree; that would orphan the branch.");
                }
            }

            Db.Command(
                    connection,
                    "UPDATE folders SET parent_id = $parentId, updated_at_utc = $updatedAt WHERE id = $id;")
                .Bind("$parentId", newParentId)
                .Bind("$updatedAt", DateTimeOffset.UtcNow)
                .Bind("$id", id)
                .Execute();
            SearchIndexWriter.IndexFolderMembers(connection, id);
        });

    public bool Delete(string id, FolderDeleteMode mode = FolderDeleteMode.Detach) =>
        _database.Write(connection =>
        {
            var folder = Find(connection, id);
            if (folder is null)
            {
                return false;
            }

            List<string> removed = mode == FolderDeleteMode.CascadeFolders
                ? Descendants(connection, id)
                : [id];

            // The records inside are read before the delete so their search documents can be
            // refreshed once the foreign key has unfiled them. They are unfiled rather than deleted:
            // a folder tidy-up must never destroy a recording.
            var dictations = Members(connection, "dictations", removed);
            var meetings = Members(connection, "meetings", removed);
            List<string> reparented = mode == FolderDeleteMode.Detach
                ? ChildIds(connection, id)
                : [];

            if (mode == FolderDeleteMode.Detach)
            {
                Db.Command(connection, "UPDATE folders SET parent_id = $parentId WHERE parent_id = $id;")
                    .Bind("$parentId", folder.ParentId)
                    .Bind("$id", id)
                    .Execute();
            }

            foreach (var folderId in removed)
            {
                Db.Command(connection, "DELETE FROM folders WHERE id = $id;")
                    .Bind("$id", folderId)
                    .Execute();
                SearchIndexWriter.Remove(connection, PersistenceSchema.FolderKind, folderId);
            }

            foreach (var dictationId in dictations)
            {
                SearchIndexWriter.IndexDictation(connection, dictationId);
            }

            foreach (var meetingId in meetings)
            {
                SearchIndexWriter.IndexMeeting(connection, meetingId);
            }

            // A promoted subtree sits at a new path, so everything under it is reindexed.
            foreach (var child in reparented)
            {
                SearchIndexWriter.IndexFolderMembers(connection, child);
            }

            return true;
        });

    public int Count() =>
        _database.Read(connection => Db.Command(connection, "SELECT count(*) FROM folders;").ScalarInt32());

    private static void Write(SqliteConnection connection, FolderRecord folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder.Id);
        if (string.Equals(folder.Id, folder.ParentId, StringComparison.Ordinal))
        {
            throw new PersistenceException($"Folder '{folder.Id}' cannot be its own parent.");
        }

        Db.Command(
                connection,
                """
                INSERT INTO folders (id, name, parent_id, sort_order, metadata, created_at_utc, updated_at_utc)
                VALUES ($id, $name, $parentId, $sortOrder, $metadata, $createdAt, $updatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    parent_id = excluded.parent_id,
                    sort_order = excluded.sort_order,
                    metadata = excluded.metadata,
                    updated_at_utc = excluded.updated_at_utc;
                """)
            .Bind("$id", folder.Id)
            .Bind("$name", folder.Name)
            .Bind("$parentId", folder.ParentId)
            .Bind("$sortOrder", folder.SortOrder)
            .Bind("$metadata", folder.Metadata)
            .Bind("$createdAt", folder.CreatedAtUtc)
            .Bind("$updatedAt", folder.UpdatedAtUtc)
            .Execute();
    }

    /// <summary>The folder and everything beneath it, used to guard moves and to scope deletes.</summary>
    private static List<string> Descendants(SqliteConnection connection, string id)
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
            .Bind("$id", id)
            .Bind("$maxDepth", MaximumDepth);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>The ids of the records filed directly under any of the given folders.</summary>
    private static List<string> Members(
        SqliteConnection connection,
        string table,
        IReadOnlyList<string> folderIds)
    {
        var ids = new List<string>();
        foreach (var folderId in folderIds)
        {
            using var command = Db.Command(connection, $"SELECT id FROM {table} WHERE folder_id = $folderId;")
                .Bind("$folderId", folderId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        return ids;
    }

    private static List<string> ChildIds(SqliteConnection connection, string parentId)
    {
        using var command = Db.Command(connection, "SELECT id FROM folders WHERE parent_id = $parentId;")
            .Bind("$parentId", parentId);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static FolderRecord? Find(SqliteConnection connection, string id)
    {
        using var command = Db.Command(connection, $"SELECT {Columns} FROM folders WHERE id = $id;")
            .Bind("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static IReadOnlyList<FolderRecord> Read(SqliteConnection connection, string sql)
    {
        using var command = Db.Command(connection, sql);
        return Read(command);
    }

    private static IReadOnlyList<FolderRecord> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var folders = new List<FolderRecord>();
        while (reader.Read())
        {
            folders.Add(Map(reader));
        }

        return folders;
    }

    private static string Prefixed(string columns, string alias) =>
        string.Join(", ", columns.Split(',').Select(column => $"{alias}.{column.Trim()}"));

    private static FolderRecord Map(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.TextOrEmpty(1),
            ParentId = reader.Text(2),
            SortOrder = reader.Int32(3),
            Metadata = reader.TextOrEmpty(4),
            CreatedAtUtc = reader.Instant(5),
            UpdatedAtUtc = reader.Instant(6)
        };
}
