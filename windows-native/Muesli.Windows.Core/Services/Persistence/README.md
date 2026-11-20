# Persistence

SQLite is the only store for the library: dictations, meetings, raw and edited transcripts,
generated and manual notes, folders, templates, and dictionary entries (including remembered
corrections). There is no JSON history mode and no flag that selects one.

**Read [`database-schema.md`](../../../../database-schema.md) at the repository root before
changing anything here.** It documents every table and column, versioning, launch behaviour,
backups, deletion and safe inspection. Update it in the same change as the code.

Settings and in-progress recording journals stay as atomic JSON, audio and models stay as files,
and credentials stay in Windows Credential Manager. The Windows database is its own schema; it is
not the macOS `DictationStore` database and nothing syncs between them.

## Layout

| File | Role |
| --- | --- |
| `MuesliDatabase.cs` | Owns the single connection, the pragmas and the transaction scope. Refuses a database without Muesli's application id. |
| `PersistenceSchema.cs` | The schema, as an ordered list of numbered migrations. Currently one: version 1. |
| `SchemaMigrator.cs` | Applies pending migrations; refuses a database from a newer build. |
| `LibraryDatabaseStartup.cs` | The launch check: ready, create for a new profile, or refuse with a reason. |
| `LibraryDatabaseBackup.cs` | Rolling `VACUUM INTO` backups, replacement after deletion, and restore. |
| `I*Repository.cs`, `Sqlite*Repository.cs` | Dictations, meetings, folders, templates, dictionary, search. |
| `SearchIndexWriter.cs` | Keeps the FTS documents in step with the relational tables. |
| `Adapters/SqliteLibraryHistoryAdapter.cs` | The app-facing library: history, dictionary and the atomic correction save. |
| `Adapters/ILibraryHistoryAdapter.cs` and friends | The contracts the app codes against. |
| `LegacyJsonHistoryImporter.cs` | One-time developer import of JSON history into a new database. |
| `JsonHistorySnapshotReader.cs`, `MigrationPlan.cs`, `JsonToSqliteMigrationService.cs`, `PersistenceDigest.cs` | Read-only JSON reading, mapping, verified import and content hashes used by that importer. |
| `Adapters/JsonLibraryHistoryAdapter.cs` (with `AppDataStore`) | The JSON-era store, kept so tests can author legacy fixtures. Not a production mode. |

Start at `MuesliPersistenceStore`, which opens a database and hands back the repositories. The
app's composition root (`WinUiLibraryContext`) calls `LibraryDatabaseStartup.Prepare` and then
opens `SqliteLibraryHistoryAdapter`.

## Decisions worth knowing before you change something

**One connection, not a pool.** SQLite has a single writer anyway, and one connection makes
`BeginTransaction` a real scope that repository calls join. The lock is re-entrant, so a
transaction must be committed and disposed on the thread that opened it. A nested
`BeginTransaction` joins the outer one; nothing commits until the outermost scope does.

**Timestamps are UTC ticks in INTEGER columns.** The import verifies content hashes of values
before and after the move, so rounding to milliseconds would turn every imported timestamp into
a verification failure or a silent shift.

**Notes and transcripts are rows keyed by kind, not columns.** A re-summarization rewrites the
generated note and must be structurally unable to touch the manual one; an edit must not
overwrite what the model actually produced.

**Deleting a folder never deletes history.** Folder references use `ON DELETE SET NULL`, so a
folder tidy-up unfiles recordings instead of destroying them.

**Search documents are maintained in code, not triggers.** A meeting's document is assembled
from five tables, and a trigger only ever sees its own. Indexing happens inside the writing
transaction, so a committed record is always findable and a rolled-back one is never indexed.

**Date-ordered search uses a two-phase query.** SQLite sorts rows together with their computed
columns, so a single query would build a snippet for every match before discarding all but one
page. See the comment on `SqliteSearchRepository.Sql`.

**Settings and in-progress meeting journals stay as atomic JSON.** They are small, single-writer
documents rewritten constantly during a recording, and crash recovery reads them before the app
has a database open.

**The app never converts JSON history.** No build was released before the SQLite library, so
there is no in-app migration, snapshot, marker or fallback. A launch that finds legacy
`windows-*.json` history and no database stops and points at
`scripts/import-legacy-history.ps1`; it does not open an empty library over history it can see.
The importer is all-or-nothing, only reads its source, and refuses to touch an existing
database.

**Launch never guesses.** A missing database with backups, an unreadable database, one from a
newer build and one from a pre-release build each produce a specific outcome and message
(`LibraryStartupOutcome`). Only a profile with no history of any kind gets a new empty database.

**The database is backed up, and deletion replaces the backups.** `LibraryDatabaseBackup` keeps
the newest seven daily copies. Deleting a dictation or meeting raises
`SqliteLibraryHistoryAdapter.ContentDeleted`, and the host then deletes every backup and takes a
fresh one, so a backup never keeps text the user removed. A new deletion path must raise it too.

## Corrections

`IAtomicDictationCorrectionStore.SaveCorrection(id, expectedText, replacementText,
phraseToRemember, rememberedReplacement, confirmedConflicts)` returns a
`DictationCorrectionResult`. Pass a null phrase and replacement for a one-off edit. The
operation compares the complete expected transcript, checks the phrase for conflicting rules,
and writes the rule, the text, the word count and the search document in one transaction. An
exact confirmed-conflicts list is required before a conflicting rule is replaced; a stale
confirmation is rejected. Ids, timestamps, title, folder and recording metadata are preserved.

"Remember" is off by default in the editor: a correction changes one dictation unless the user
asks for a rule. A remembered rule only accepts a selection of whole words separated by the
spacing the matcher itself accepts (`DictionaryCorrectionService.CanRememberSelection`), so a
saved rule always matches the phrase it came from.

## Known limits

Append, correction and single-record deletion are targeted writes. The dictionary list-save
diffs rows and writes only changed entries. Dashboard snapshots and several list pages still
load whole histories, and the meeting list still fetches details per meeting. Repository and
search pagination exist; moving every list onto them is not done.

## Tests

`SqliteLibraryStorageTests` covers launch outcomes, backups and restore, the importer, and the
correction transaction. `PersistenceMigrationTests` covers the schema migrator and the verified
import. `PersistenceLargeHistoryTests` has timing budgets that are sensitive to machine load.
Every test uses a temporary directory; none may point at a real profile.
