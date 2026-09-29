using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed class SqliteTemplateRepository : ITemplateRepository
{
    private const string Columns = "id, name, prompt, icon, sort_order, created_at_utc, updated_at_utc";

    private readonly MuesliDatabase _database;

    public SqliteTemplateRepository(MuesliDatabase database) => _database = database;

    public TemplateRecord? Find(string id) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(connection, $"SELECT {Columns} FROM templates WHERE id = $id;")
                .Bind("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        });

    public IReadOnlyList<TemplateRecord> List() =>
        _database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                $"SELECT {Columns} FROM templates ORDER BY sort_order, name COLLATE NOCASE, id;");
            using var reader = command.ExecuteReader();
            var templates = new List<TemplateRecord>();
            while (reader.Read())
            {
                templates.Add(Map(reader));
            }

            return (IReadOnlyList<TemplateRecord>)templates;
        });

    public TemplateRecord? FindByName(string name) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                $"""
                 SELECT {Columns} FROM templates
                 WHERE name = $name COLLATE NOCASE
                 ORDER BY sort_order, id
                 LIMIT 1;
                 """)
                .Bind("$name", name);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        });

    public void Upsert(TemplateRecord template) =>
        _database.Write(connection => Write(connection, template));

    public void UpsertRange(IEnumerable<TemplateRecord> templates) =>
        _database.Write(connection =>
        {
            foreach (var template in templates)
            {
                Write(connection, template);
            }
        });

    public bool Delete(string id) =>
        _database.Write(connection => Db.Command(connection, "DELETE FROM templates WHERE id = $id;")
            .Bind("$id", id)
            .Execute() > 0);

    public int Count() =>
        _database.Read(connection => Db.Command(connection, "SELECT count(*) FROM templates;").ScalarInt32());

    private static void Write(SqliteConnection connection, TemplateRecord template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template.Id);
        Db.Command(
                connection,
                """
                INSERT INTO templates (id, name, prompt, icon, sort_order, created_at_utc, updated_at_utc)
                VALUES ($id, $name, $prompt, $icon, $sortOrder, $createdAt, $updatedAt)
                ON CONFLICT (id) DO UPDATE SET
                    name = excluded.name,
                    prompt = excluded.prompt,
                    icon = excluded.icon,
                    sort_order = excluded.sort_order,
                    updated_at_utc = excluded.updated_at_utc;
                """)
            .Bind("$id", template.Id)
            .Bind("$name", template.Name)
            .Bind("$prompt", template.Prompt)
            .Bind("$icon", template.Icon)
            .Bind("$sortOrder", template.SortOrder)
            .Bind("$createdAt", template.CreatedAtUtc)
            .Bind("$updatedAt", template.UpdatedAtUtc)
            .Execute();
    }

    private static TemplateRecord Map(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.TextOrEmpty(1),
            Prompt = reader.TextOrEmpty(2),
            Icon = reader.TextOrEmpty(3),
            SortOrder = reader.Int32(4),
            CreatedAtUtc = reader.Instant(5),
            UpdatedAtUtc = reader.Instant(6)
        };
}
