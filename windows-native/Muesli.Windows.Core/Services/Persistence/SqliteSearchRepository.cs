using System.Text;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed class SqliteSearchRepository : ISearchRepository
{
    /// <summary>
    /// One weight per FTS column, in declaration order. The four unindexed identity columns can
    /// never match and take a zero weight; a title hit outranks a body hit, and an alias hit ranks
    /// high because a search for a person's name is nearly always a search for their meetings.
    /// </summary>
    private const string Bm25 = "bm25(search_index, 0.0, 0.0, 0.0, 0.0, 10.0, 1.0, 2.0, 2.0, 4.0, 1.5)";

    private const string SnippetExpression = "snippet(search_index, -1, '[', ']', '…', 12)";

    private readonly MuesliDatabase _database;

    public SqliteSearchRepository(MuesliDatabase database) => _database = database;

    public IReadOnlyList<SearchHit> Search(SearchQuery query)
    {
        var match = BuildMatch(query);
        if (match is null || query.Kinds == SearchRecordKinds.None)
        {
            return [];
        }

        return _database.Read(connection =>
        {
            using var command = Db.Command(connection, Sql(query))
                .Bind("$match", match)
                .Bind("$limit", query.Limit)
                .Bind("$offset", query.Offset);
            if (query.FolderId is not null)
            {
                command.Bind("$folderId", query.FolderId);
            }

            using var reader = command.ExecuteReader();
            var hits = new List<SearchHit>();
            while (reader.Read())
            {
                hits.Add(new SearchHit
                {
                    Kind = PersistenceEnums.SearchKind(reader.GetString(0)),
                    RecordId = reader.GetString(1),
                    FolderId = string.IsNullOrEmpty(reader.TextOrEmpty(2)) ? null : reader.GetString(2),
                    CreatedAtUtc = PersistenceTime.FromStorage(reader.Int64(3)),
                    Title = reader.TextOrEmpty(4),
                    Snippet = reader.TextOrEmpty(5),
                    Rank = reader.GetDouble(6)
                });
            }

            return (IReadOnlyList<SearchHit>)hits;
        });
    }

    public int CountMatches(SearchQuery query)
    {
        var match = BuildMatch(query);
        if (match is null || query.Kinds == SearchRecordKinds.None)
        {
            return 0;
        }

        return _database.Read(connection =>
        {
            var command = Db.Command(
                connection,
                $"""
                 SELECT count(*) FROM search_index
                 WHERE search_index MATCH $match{KindFilter(query.Kinds)}{FolderFilter(query)};
                 """)
                .Bind("$match", match);
            if (query.FolderId is not null)
            {
                command.Bind("$folderId", query.FolderId);
            }

            return command.ScalarInt32();
        });
    }

    public void Rebuild() => _database.Write(SearchIndexWriter.Rebuild);

    /// <summary>
    /// Relevance ordering reads the ranked page directly. Date ordering cannot: SQLite sorts rows
    /// with their computed columns, so a single query would build a snippet for every matching
    /// record before discarding all but one page of them — on a ten-thousand record history that is
    /// the difference between about forty and about two hundred milliseconds. The date query
    /// therefore picks the page by rowid first and only then asks for snippets, which is the same
    /// result for a fraction of the work.
    /// </summary>
    private static string Sql(SearchQuery query)
    {
        var filters = $"{KindFilter(query.Kinds)}{FolderFilter(query)}";
        if (query.Sort != SearchSort.NewestFirst)
        {
            return $"""
                    SELECT record_kind, record_id, folder_id, created_at, title,
                           {SnippetExpression} AS excerpt,
                           {Bm25} AS score
                    FROM search_index
                    WHERE search_index MATCH $match{filters}
                    ORDER BY score, created_at DESC
                    LIMIT $limit OFFSET $offset;
                    """;
        }

        return $"""
                WITH page AS (
                    SELECT rowid AS doc_id
                    FROM search_index
                    WHERE search_index MATCH $match{filters}
                    ORDER BY created_at DESC
                    LIMIT $limit OFFSET $offset
                )
                SELECT record_kind, record_id, folder_id, created_at, title,
                       {SnippetExpression} AS excerpt,
                       {Bm25} AS score
                FROM search_index
                WHERE search_index MATCH $match AND rowid IN (SELECT doc_id FROM page)
                ORDER BY created_at DESC;
                """;
    }

    /// <summary>
    /// Turns user text into an FTS5 match expression. Every term is emitted as a quoted string, so
    /// a stray quote, asterisk or NEAR in what the user typed is searched for literally instead of
    /// being executed as query syntax or throwing a syntax error back at them.
    /// </summary>
    internal static string? BuildMatch(SearchQuery query)
    {
        if (query.Fields == SearchFields.None)
        {
            return null;
        }

        var terms = query.Text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Quote)
            .Where(term => term is not null)
            .ToList();
        if (terms.Count == 0)
        {
            return null;
        }

        if (query.PrefixMatchLastTerm)
        {
            terms[^1] += "*";
        }

        var expression = string.Join(" AND ", terms);
        var columns = ColumnFilter(query.Fields);
        return columns is null ? expression : $"{{{columns}}} : ({expression})";
    }

    private static string? Quote(string term)
    {
        var builder = new StringBuilder(term.Length + 2);
        builder.Append('"');
        foreach (var character in term)
        {
            // A double quote is the only character with meaning inside an FTS5 string, and doubling
            // it is how the string escapes it.
            if (character == '"')
            {
                builder.Append("\"\"");
                continue;
            }

            builder.Append(character);
        }

        builder.Append('"');

        // A term of pure punctuation produces no tokens and would make the whole AND chain match
        // nothing, so it is dropped instead.
        return term.Any(char.IsLetterOrDigit) ? builder.ToString() : null;
    }

    private static string? ColumnFilter(SearchFields fields)
    {
        if (fields == SearchFields.All)
        {
            return null;
        }

        var columns = new List<string>();
        if (fields.HasFlag(SearchFields.Title))
        {
            columns.Add("title");
        }

        if (fields.HasFlag(SearchFields.Transcript))
        {
            columns.Add("transcript");
        }

        if (fields.HasFlag(SearchFields.Notes))
        {
            columns.Add("notes");
        }

        if (fields.HasFlag(SearchFields.DictationText))
        {
            columns.Add("dictation_text");
        }

        if (fields.HasFlag(SearchFields.SpeakerAliases))
        {
            columns.Add("aliases");
        }

        if (fields.HasFlag(SearchFields.Folder))
        {
            columns.Add("folder");
        }

        return columns.Count == 0 ? null : string.Join(" ", columns);
    }

    private static string KindFilter(SearchRecordKinds kinds)
    {
        if (kinds == SearchRecordKinds.All)
        {
            return "";
        }

        var values = new List<string>();
        if (kinds.HasFlag(SearchRecordKinds.Dictation))
        {
            values.Add($"'{PersistenceSchema.DictationKind}'");
        }

        if (kinds.HasFlag(SearchRecordKinds.Meeting))
        {
            values.Add($"'{PersistenceSchema.MeetingKind}'");
        }

        if (kinds.HasFlag(SearchRecordKinds.Folder))
        {
            values.Add($"'{PersistenceSchema.FolderKind}'");
        }

        // The values are this file's own constants, never user input.
        return $" AND record_kind IN ({string.Join(", ", values)})";
    }

    private static string FolderFilter(SearchQuery query) =>
        query.FolderId is null ? "" : " AND folder_id = $folderId";
}
