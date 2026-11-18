# Persistence

SQLite is the sole live store for structured library data: dictations, meetings, raw/edited transcripts,
generated/manual notes, folders, templates, dictionary entries and remembered correction rules.
Every production startup runs `PersistenceCutover.EnsureMigrated` before opening the adapter.
Fresh profiles create an authoritative database directly, without a flag or a legacy-history snapshot.
`AppDataStore` and the JSON adapter remain legacy migration/import/test utilities, not a production mode.
Settings and recording recovery journals use atomic JSON, audio/models remain files, and credentials
remain in Windows Credential Manager. Windows owns its database; the Swift DictationStore ABI is not connected.

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
| `Adapters/SqliteLibraryHistoryAdapter.cs` | SQLite-backed library, dictionary and atomic correction operations. |
| `Adapters/PersistenceCutoverGate.cs` | Compatibility constants only; always enabled. The former environment variable cannot select JSON. |
| `Adapters/PersistenceCutoverState.cs` | Singleton schema-v2 authority row and first-post-cutover-write marker. |
| `SqliteDictionaryRepository.cs`, `DictionaryMigration.cs` | Schema-v3 rules, full-value verification and transactional import-completion record. |
| `SqliteAuthorityMarker.cs` | Payload-free profile marker preventing JSON fallback if the authoritative database disappears. |
| `LegacyLibraryRecovery.cs` | Bounded migration-copy retention and targeted redaction on explicit deletion. |

Start at `MuesliPersistenceStore`, which opens a database and hands back the five repositories. The
production composition root opens the SQLite adapter only after checked initialization/upgrade succeeds.

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

The profile's `sqlite-authority` marker is flushed before startup returns a usable store. If `muesli.db`
is missing, corrupt, unavailable or newer than this build supports, startup reports failure and never
opens an empty replacement or retained JSON. Restore an explicit SQLite backup while Muesli is stopped;
do not remove the authority marker to force a legacy import. An unmarked populated database with no
identified migration run also fails closed for explicit recovery instead of guessing which source is newer.

Schema 3 imports `windows-dictionary.json` for both JSON-history profiles and already-authoritative
SQLite profiles. For the latter, only dictionary files are captured; old history is never read. Import,
all-field/count readback and the completion record commit together. The record is written even for an
empty dictionary, so later startup cannot resurrect an old rule file. Existing rule IDs, order, phrases,
replacements and double-precision thresholds are preserved. Invalid/duplicate IDs block import.
Dictionary import/export continues using `DictionaryExchangeService`'s existing JSON format.

For legacy history, the checked capture includes primary files, `.bak` files and a pending folder mutation
journal. A surviving backup can recover a missing or malformed primary, with warnings. Newer envelopes
and meeting schemas block migration rather than falling back. Originals are unchanged during migration.
History import is verified by normalized counts/content digests; dictionary import is verified by complete
record equality. A crash before authority activates can resume the recorded import without duplicating rows.

## Recovery copies and privacy

`json-history-pre-sqlite-*` directories and retained `windows-*.json` library files are recovery inputs only.
Content-addressed snapshots reuse identical retries. Failed migrations keep their recovery inputs until
resolved; they never activate an alternate history store. After successful transition, automatic startup
cleanup retains migration originals/snapshots and pre-migration database backups for 30 days, measured
from the later of history activation and dictionary import. User-created exports and explicit backups with
other names are not touched. Explicit deletion redacts the deleted IDs from retained JSON and pre-migration
SQLite copies while preserving unrelated records; unreadable/locked copies cause a visible failure.
Settings, in-progress recording journals, audio and model assets are outside this cleanup.

`Rollback` is a support operation available only before the first post-cutover write; it also removes the
external authority marker after restoring/removing the initial database. There is no automatic rollback.

## Correction integration and remaining limits

`IAtomicDictationCorrectionStore.SaveCorrection(id, expectedText, replacementText, phraseToRemember,
rememberedReplacement, confirmedConflicts)` returns `DictationCorrectionResult`. Pass null phrase and
replacement for a one-off edit. The operation compares the complete expected transcript, checks the current
phrase conflicts, and writes the rule, text, word count and search index in one SQLite transaction. An exact
confirmed-conflicts list is required before replacing conflicting rules; a stale confirmation is rejected.
It preserves IDs, timestamps, title, folder and recording metadata. `IDictationTextEditor` remains available.

Append, correction and single-record deletion are targeted writes. The compatibility dictionary list-save
diffs rows, writing only changed entries. Dashboard snapshots and several list pages still load whole histories;
meeting list projection still fetches details per meeting. Bulk save/clear and dictionary list APIs still
enumerate collections. Repository/search pagination exists, but migrating every UI list is outside this task.
Passing regression and timing tests is evidence for these paths, not a claim of complete release qualification.

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
