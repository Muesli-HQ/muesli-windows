# Persistence

The SQLite substrate for dictation and meeting history. The JSON stores in `AppDataStore` remain
authoritative while the L27 gate is disabled; production switches the active history adapter only
after a verified startup cutover.

## Layout

| File | Role |
| --- | --- |
| `MuesliDatabase.cs` | Owns the single connection, the pragmas and the transaction scope. |
| `PersistenceSchema.cs` | The schema, as an ordered list of numbered migrations. |
| `SchemaMigrator.cs` | Applies pending migrations; refuses a database from a newer build. |
| `I*Repository.cs` | The contracts: dictations, meetings, folders, templates, search. |
| `Sqlite*Repository.cs` | Their implementations. |
| `SearchIndexWriter.cs` | Keeps the FTS documents in step with the relational tables. |
| `JsonHistorySnapshotReader.cs` | Reads the JSON history without modifying it. |
| `MigrationPlan.cs` | Maps that snapshot onto records, resolving duplicates and orphans. |
| `JsonToSqliteMigrationService.cs` | Runs the import, verifies it, and can undo it. |
| `PersistenceDigest.cs` | Content hashes used to prove an import was faithful. |
| `Adapters/PersistenceCutover.cs` | L27 `EnsureMigrated`: JSON snapshot, digest import, durable authority transition, fail closed. |
| `Adapters/SqliteLibraryHistoryAdapter.cs` | SQLite-backed `ILibraryHistoryAdapter` used after the verified production cutover. Dictionary stays JSON. |
| `Adapters/PersistenceCutoverGate.cs` | Feature flag `L27SqliteHistoryCutover`, default **off**; qualification uses `MUESLI_SQLITE_HISTORY_CUTOVER=1`. |
| `Adapters/PersistenceCutoverState.cs` | Singleton schema-v2 authority row and first-post-cutover-write marker. |

Start at `MuesliPersistenceStore`, which opens a database and hands back the five repositories. The
production composition root owns the gate and selects the active adapter before history loads.

## Decisions worth knowing before you change something

**One connection, not a pool.** SQLite has a single writer anyway, and one connection makes
`BeginTransaction` a real scope that repository calls join. The lock is re-entrant, so a transaction
must be committed and disposed on the thread that opened it.

**Timestamps are UTC ticks in INTEGER columns.** Migration verification compares hashes of the
values before and after the move, so rounding to milliseconds would turn every imported timestamp
into either a verification failure or a silent shift.

**Notes and transcripts are rows keyed by kind, not columns.** A re-summarization rewrites the
generated note and must be structurally unable to touch the manual one; an edit must not overwrite
what the model actually produced.

**Deleting a folder never deletes history.** Foreign keys use `ON DELETE SET NULL` for folder
references, so a folder tidy-up unfiles recordings instead of destroying them.

**Search documents are maintained in code, not triggers.** A meeting's document is assembled from
five tables, and a trigger only ever sees its own. Indexing happens inside the writing transaction,
so a committed record is always findable and a rolled-back one is never indexed.

**Date-ordered search uses a two-phase query.** SQLite sorts rows together with their computed
columns, so a single query would build a snippet for every match before discarding all but one page.
See the comment on `SqliteSearchRepository.Sql`.

**Settings and in-progress meeting journals stay as atomic JSON.** They are small, single-writer
documents rewritten constantly during a recording, and crash recovery reads them before the app has
a database open. `AtomicJsonFile` is the better fit and this layer deliberately leaves them alone.

**SQLite authority is durable and one-way for normal startup.** Schema migration 2 creates a
singleton `persistence_cutover_state` row with `json` authority. `PersistenceCutover` snapshots the
source JSON and marks that row `sqlite` only after the import's count and digest verification has
committed. Once it is `sqlite`, startup ignores retained-JSON fingerprint changes and never falls
back or re-imports from that snapshot. Every adapter mutation records the first write's UTC and
operation; JSON rollback is allowed only before that marker exists. Afterward an operator must use
an explicit SQLite backup/restore procedure.

## Migrating from JSON

```csharp
var service = new JsonToSqliteMigrationService(dataDirectory);
var result = service.Migrate();
if (!result.Succeeded)
{
    // Nothing was imported and the database is as it was.
}
```

The guarantees the tests hold it to:

- the JSON files are only ever read, never renamed, restored or rewritten;
- the import is one transaction, so an interruption leaves the database untouched;
- the pre-migration database is copied aside first, and `Rollback` puts it back;
- imported rows are read back and compared against a content hash of the source before commit;
- re-running over an unchanged history is recognised by fingerprint and does nothing.

## Adding a schema change

Append a new `SchemaMigration` with the next version number. Never edit a released one: a database in
the wild has already run it, so a correction has to arrive as the next version instead.
