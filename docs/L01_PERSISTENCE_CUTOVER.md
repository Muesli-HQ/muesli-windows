# L01 persistence boundary and L27 cutover design

Status: **design slice only**. L27 and ORG-01 remain incomplete. This document does not update `WINDOWS_LAUNCH_LEDGER.md`.

Base: `efa961ccbc78bb7b2b2542ca3f346ed2222d502a` (`codex/wave0-launch-foundation`).
Capability IDs: **L01**, **L27** (design). Related later: **ORG-01**, **SEARCH-01**, **FOLLOW-01**.
Owner lane: Agent D (data/knowledge). Integration wiring: Agent E. Nested-folder UI: L28. Full-content search UI: L29. Follow-up UI: L32.

## Ledger correction (do not edit the ledger from this slice)

`WINDOWS_LAUNCH_LEDGER.md` ORG-01 currently says: *one-level folders only; no `ParentId`*.

That rationale is stale. The **product UI** is one-level. The **SQLite repository** already stores `FolderRecord.ParentId`, lists children/ancestors/subtrees, rejects cycles, and migrates JSON folders as roots with `ParentId = null`.

| Layer | ParentId today |
|---|---|
| JSON `PersistedMeetingFolder` | No property. Record is `(Id, Name)` only. Extra `parentId` in a file is ignored on load. |
| `FeatureRuntime.SaveMeetingFolders` | Writes `new PersistedMeetingFolder(folder.Id, folder.Name)` — nesting cannot survive a save. |
| UI (`FeatureRuntime.Meetings.cs`) | Flat `MeetingFolders` list. Reorder is array order. Delete unfiles meetings. No breadcrumbs, no child listing. |
| SQLite `IFolderRepository` / `folders.parent_id` | Nested tree exists and is tested. |
| `MigrationPlan` | Sets `ParentId = null` and `SortOrder = insertion index` so one-level JSON folders become roots. |
| macOS `DictationStore.swift` | Nested `parentID`, `moveFolder`, descendant queries, sidebar tree in `SidebarView.swift`. |

ORG-01 stays **Partial** until L28 exposes the repository tree. L00 owns the ledger wording.

SEARCH-01 remains Partial: production `PassesSearch` does not index manual notes; `ISearchRepository` already does. FOLLOW-01 remains Missing in the UI; `IMeetingRepository` already has follow-up rows that JSON migration writes as empty.

## Shared-file lock (Wave 1 freeze)

Only the designated integration owner (Agent E by default) may edit:

- `Features/Runtime/FeatureRuntime.xaml.cs`
- `Features/Runtime/FeatureRuntime.Meetings.cs`
- `Features/Runtime/FeatureRuntime.Dictations.cs`
- `Features/Runtime/FeatureRuntime.Models.cs`
- `Features/Runtime/FeatureRuntime.Settings.cs`
- `Services/AppServices.cs`
- `Services/SettingsStore.cs`
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `App.xaml` / `App.xaml.cs`
- `Muesli.Windows.csproj`

Agent D owns unused adapter contracts under `Services/Persistence/Adapters/`, characterization tests, this design, and later L27 repository/migration implementation in persistence files. Agent D must not inject behavior into the lock list.

Wave 1 adapter owner: **Agent D** (contracts + later SQLite-backed implementation).
Wave 2 wiring owner: **Agent E** (swap `FeatureServiceScope` / `FeatureRuntime` onto `ILibraryHistoryAdapter` after cutover).

## Current production truth

`AppServices.DataStore` is still `AppDataStore`. JSON files in `%APPDATA%\muesli\data\` are authoritative:

| File | Contents |
|---|---|
| `windows-dictations.json` | Dictation history |
| `windows-meetings.json` | Meetings (schema 5), notes, aliases, automation diagnostics |
| `windows-meeting-folders.json` | One-level folders |
| `windows-dictionary.json` | Personal dictionary |
| `windows-meeting-templates.json` | Custom templates |

Settings (`windows-settings.json`) and in-progress session journals stay atomic JSON by design (`Persistence/README.md`). SQLite repositories exist and are tested; **nothing in the running app uses them**.

`JsonToSqliteMigrationService` can already import JSON in one transaction with digest verification, restart idempotence, schema-forward SQLite refusal, and SQLite premigration backup. It does **not** migrate the dictionary. It does **not** delete JSON. It is **not** called at startup.

## Adapter sketch (unwired)

New unused contracts:

- `ILibraryHistoryAdapter` — the API FeatureRuntime should call instead of `_dataStore.*`.
- `ILibrarySearchAdapter` / `ProductionInMemorySearchMatch` — frozen production field list for L29 contrast.
- `IPersistenceCutover` — startup-only `EnsureMigrated` / `Rollback`. Call from `FeatureServiceScope.CreateProduction`, **not** from FeatureRuntime.

```
FeatureRuntime  --(Wave 2)-->  ILibraryHistoryAdapter
                                      |-- Sqlite*Repository (dictations, meetings, folders, templates)
                                      |-- AppDataStore dictionary methods until a dictionary table exists
                                      '-- ILibrarySearchAdapter --> ISearchRepository (L29)

FeatureServiceScope.CreateProduction
  1. IPersistenceCutover.EnsureMigrated()
  2. Open MuesliPersistenceStore
  3. Construct repository-backed ILibraryHistoryAdapter
  4. Hand adapter to FeatureRuntime (integration owner edit)
```

The test-only `JsonLibraryHistoryAdapter` in `PersistenceCutoverCharacterizationTests` proves the contract maps 1:1 onto today's `AppDataStore`. It is not production wiring.

Whole-collection JSON rewrites (`SaveMeetings` writes every meeting) must become per-record `Save`/`Delete`/`MoveToFolder` in the SQLite adapter. Blind `SaveRange` of the in-memory list will drop child rows the UI never loaded (edited transcripts, follow-ups) — see L27 gates below.

## Complete AppDataStore call-site map

Grep of `windows-native/Muesli.Windows/**/*.cs` (production only). Every live call is listed. Tests are out of scope for wiring.

### Construction and schema constants

| File:member | AppDataStore API | Target | Wave | Owner |
|---|---|---|---|---|
| `Services/AppServices.cs:83,110,138` `FeatureServiceScope` | `AppDataStore DataStore` property | Replace type with `ILibraryHistoryAdapter` (keep `AppDataStore` only as dictionary leftover inside the adapter) | Wave 2 | Agent E |
| `Services/AppServices.cs:169` `CreateProduction` | `new AppDataStore()` | After `IPersistenceCutover.EnsureMigrated()`, open `MuesliPersistenceStore` and construct the SQLite-backed adapter | Wave 2 | Agent E |
| `Features/Runtime/FeatureRuntime.xaml.cs:37,662` | field `_dataStore` assigned from `FeatureServiceScope.DataStore` | Inject `ILibraryHistoryAdapter` | Wave 2 | Agent E |
| `Features/Runtime/FeatureRuntime.xaml.cs:974` | `_dataStore = null!` teardown | Dispose adapter/store | Wave 2 | Agent E |
| `Features/Runtime/FeatureRuntime.xaml.cs:1340` `SaveMeetings` | `CurrentMeetingSchemaVersion` on every write | `MeetingRecord.SourceSchemaVersion` already defaults to 5; natively written rows keep current | Wave 2 | Agent E |
| `Services/MeetingNotesComposer.cs:117,131,140` | `CurrentMeetingSchemaVersion` only (no I/O) | Keep constant or move onto `PersistedMeeting` / adapter helper. Not a repository call. | Wave 1 leftover | Agent D if relocated; else E when meetings save through repos |
| `Services/Persistence/PersistenceModels.cs:64` | default `SourceSchemaVersion = CurrentMeetingSchemaVersion` | Keep. Identifies imported vs native rows. | — | already in repo models |
| `Services/Persistence/JsonHistorySnapshotReader.cs:132` | `MigrateMeeting` | Migration-only. Must keep using the same upgrade as `AppDataStore.LoadMeetings`. Not a UI call site. | L27 | Agent D |

### Startup load (`LoadPersistedData`)

| File:member | AppDataStore API | Target repository method | Wave | Owner |
|---|---|---|---|---|
| `FeatureRuntime.xaml.cs:730` | `LastWarning` (OR with settings warning) | `ILibraryHistoryAdapter.LastWarning` plus settings. Adapter must surface JSON leftover warnings and SQLite open/cutover failures honestly. | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1253` | `LoadMeetingFolders()` then `MeetingFolders.Add` **file order** | `IFolderRepository.List()` ordered by `SortOrder`, then name. Until L28, expose roots only (`ParentId == null`) so UI stays one-level. | Wave 2 / L28 | E then D |
| `FeatureRuntime.xaml.cs:1258` | `LoadDictations().OrderByDescending(Timestamp)` | `IDictationRepository.List(new DictationQuery { Sort = NewestFirst, Limit = Count() })`. **Do not use default `Limit = 100`.** JSON loads the entire file. | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1269` | `LoadMeetings().OrderByDescending(CreatedAt)` | `IMeetingRepository.List` **plus** `FindDetail` (or a list-detail helper). JSON meetings carry transcript, summary, manual notes, aliases, automation, audio paths on the same record. `List` returns heads only. | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1299` | `LoadDictionary()` | **No SQLite repository.** Adapter must keep JSON `windows-dictionary.json` until a dictionary table exists. | Wave 2 | Agent D leftover + E wire |
| `FeatureRuntime.xaml.cs:1305` | `LoadMeetingTemplates()` file order | `ITemplateRepository.List()` (sort order then name). JSON order is insertion/UI order; migration already assigns `SortOrder = index`. | Wave 2 | Agent E |

### Dictation writes

| File:member | AppDataStore API | Target | Wave | Owner |
|---|---|---|---|---|
| `FeatureRuntime.xaml.cs:1328` `SaveDictations(afterExplicitDeletion: true)` | `SaveDictationsAfterDeletion` (privacy purge of artifacts) | `IDictationRepository.Delete(id)` for the removed row; do not rewrite the whole table. Retain privacy-sensitive artifact purge for leftover JSON until JSON is retired. | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1332` `SaveDictations(false)` | `SaveDictations` whole list | `IDictationRepository.Upsert` the changed row. Today's JSON rewrite of the entire collection is the behavior to replace, not copy. | Wave 2 | Agent E |
| `FeatureRuntime.Dictations.cs:446` | `SaveDictations()` after insert-at-0 | `Upsert` the new dictation. Display order is newest-first; SQLite `NewestFirst` matches. Timestamp today is `DateTime.Now` (local), not `UtcNow`. | Wave 2 | Agent E |
| `FeatureRuntime.Dictations.cs:774` | `SaveDictations(afterExplicitDeletion: true)` | `Delete(id)` | Wave 2 | Agent E |

### Meeting writes

| File:member | AppDataStore API | Target | Wave | Owner |
|---|---|---|---|---|
| `FeatureRuntime.xaml.cs:1369` | `SaveMeetingsAfterDeletion` | `IMeetingRepository.Delete(id)` (cascades notes/transcripts/aliases/follow-ups/search docs) | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1373` | `SaveMeetings` whole list mapped from `MeetingItem` | Per-field repository writes. **Must not** `Save(MeetingDetail)` from UI state that lacks edited-transcript / follow-up children — that would delete them. | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:110` `DeleteMeeting` | `SaveMeetings(afterExplicitDeletion: true)` | `Delete(id)` after owned-audio policy | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:259` `SaveActiveSpeakerAliases` | `SaveMeetings()` | `ReplaceSpeakerAliases` | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:561` notes generation | `SaveMeetings()` | `SetNote(Generated)` + `Upsert` template/title fields. Manual note must stay `SetNote(Manual)` untouched. | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:777` move meeting | `SaveMeetings()` | `MoveToFolder(id, folderId)` | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:931` import persist | `SaveMeetings()` | `Save(MeetingDetail)` for the new meeting only | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1151` recording persist | `SaveMeetings()` | `Save(MeetingDetail)` for the new meeting only | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1215` automation diagnostics | `SaveMeetings()` | `Upsert` meeting head `AutomationResultJson` only | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1833` folder delete | `SaveMeetings()` after nulling `FolderId` | `MoveToFolder(id, null)` for each member; folder `Delete` already SET NULL in SQLite — do not also rewrite meetings if using `IFolderRepository.Delete` | Wave 2 | Agent E |
| `FeatureRuntime.MeetingDetail.cs:48` `UpdateSelectedMeeting` | `SaveMeetings()` | Title → `Upsert` + `TitleIsManual`. Manual notes → `SetNote(Manual)`. | Wave 2 | Agent E |

### Folder / template / dictionary writes

| File:member | AppDataStore API | Target | Wave | Owner |
|---|---|---|---|---|
| `FeatureRuntime.xaml.cs:1451` `SaveMeetingFolders` | `SaveMeetingFolders` Id+Name only | `IFolderRepository.Upsert` / `Move` / `Delete`. Adapter must pass `ParentId` through even while UI is one-level, or L28 nesting written by tests/migration will be flattened on the next UI save. | Wave 2 / L28 | E then D |
| `FeatureRuntime.Meetings.cs:1492` add folder | `SaveMeetingFolders()` | `Upsert` with `ParentId = null` | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1775` rename | `SaveMeetingFolders()` | `Upsert` name | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1801` reorder | `SaveMeetingFolders()` | `Upsert` `SortOrder` from list index | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1832` delete folder | `SaveMeetingFolders()` | `Delete(id, Detach)` | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1456` `SaveDictionary` | `SaveDictionary` | JSON leftover (no repo) | Wave 2 | Agent D/E |
| `FeatureRuntime.Dictionary.cs:24` add | `SaveDictionary()` | JSON leftover | Wave 2 | Agent E |
| `FeatureRuntime.Dictionary.cs:29` save | `SaveDictionary()` | JSON leftover | Wave 2 | Agent E |
| `FeatureRuntime.Dictionary.cs:38` delete | `SaveDictionary()` | JSON leftover; consider privacy-sensitive rewrite | Wave 2 | Agent E |
| `FeatureRuntime.xaml.cs:1461` `SaveMeetingTemplates` | `SaveMeetingTemplates` | `ITemplateRepository.Upsert` / `Delete` | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1666` save template | `SaveMeetingTemplates()` | `Upsert` | Wave 2 | Agent E |
| `FeatureRuntime.Meetings.cs:1679` delete template | `SaveMeetingTemplates()` | `Delete(id)` | Wave 2 | Agent E |

### Search (not AppDataStore, but must move after cutover)

| File:member | Current API | Target | Wave | Owner |
|---|---|---|---|---|
| `FeatureRuntime.Search.cs:61-80` `PassesSearch` | In-memory substring on dictation `Text`/`ModelProfile` and meeting `Title`/`Summary`/`Transcript`/`Metadata` | `ISearchRepository.Search`. Manual notes are persisted in JSON and SQLite but **are not** in this filter. L29 owns UI completeness. | L29 | Agent D |
| `FeatureRuntime.Search.cs:188-196` `RefreshSearchResults` | `ICollectionView.Refresh` | Same until L29 | L29 | Agent D |

No other production `AppDataStore` method calls exist. `FeatureRuntime.Models.cs` and `FeatureRuntime.Settings.cs` do not call the data store. Calendar/CloudKit are excluded.

If a future grep finds `_dataStore` or `DataStore.` outside this table, treat the map as stale and update this document before L27 wiring.

## JSON vs SQLite gaps (honest)

| Topic | JSON / UI today | SQLite today | Cutover implication |
|---|---|---|---|
| Authority | JSON files | Unused | Cutover must switch reads/writes together |
| Load size | Entire file | `DictationQuery.Limit` default **100** | Adapter must pass `Count()` / `int.MaxValue` until UI paging exists |
| Sort | File order; UI re-sorts newest-first on load | `HistorySort.NewestFirst` | After first UI save, JSON is already newest-first |
| Folders | One-level, no ParentId | Nested ParentId | Migrate as roots; **never write folders back through JSON-shaped Id+Name or nesting is destroyed** |
| Search | In-memory; no manual notes | FTS includes notes, aliases, follow-ups, folder text | Do not claim SEARCH-01 complete at L27 |
| Dictionary | JSON | No table | Keep JSON behind the adapter |
| Follow-ups | None in JSON; UI missing | Rows exist; migration writes `FollowUps = []` | L32 after L27 |
| Transcripts | Single blob | Raw + Edited kinds | JSON import stores the blob as Raw |
| Notes | `Summary` + `ManualNotes` on one record | Separate kinds | Regeneration must use `SetNote(Generated)` only |
| Timestamps | `DateTime.Now` (local) on new dictations/meetings | UTC ticks; `PersistenceTime.Normalize` treats Unspecified as UTC | Reconcile Kind before hashing; the `PersistenceTime` comment that claims `DateTime.UtcNow` does not match FeatureRuntime |
| Selection | In-memory only (`_selectedMeeting`, `_selectedMeetingFolderId`, `NavigationService.SelectedMeetingId`) | Not stored | There is no selection to migrate across restart. Same-process cutover must not clear in-memory selection. Do not invent a selected-id file. |
| Settings references | `MuesliSettings.MeetingSummaryTemplate` is a **name string**; `PersistedMeeting.TemplateName` and `FolderId` are strings | Template/folder ids and names | Preserve name and id strings; do not rewrite settings during history import |
| Schema-forward JSON envelope | Treated as **corruption**: quarantine, empty load, sticky `LastWarning` | N/A | Do not treat this as a successful migration |
| Schema-forward meeting record (`SchemaVersion > 5`) | `LoadMeetings` **throws** `InvalidDataException`; file is left in place | N/A | Awkward. Startup can crash. L27 must fail closed without deleting JSON. |
| Schema-forward SQLite | N/A | `SchemaMigrator` refuses newer `user_version` | Keep. Older builds must not open a newer DB. |
| Corrupt JSON without backup | Quarantine to `.corrupt-*`, return empty, set `LastWarning` | Snapshot reader does **not** rename; blocks migration | Running app and migrator must not share the quarantine path. Migration already uses `JsonHistorySnapshotReader`. |
| `LastWarning` | Sticky: a later successful load does not clear it | N/A | Preserve or replace with an explicit cutover warning list |
| Privacy delete | `AtomicJsonSaveMode.PrivacySensitive` purges `.bak` / temps | Row delete + search index | Keep JSON purge until JSON retention ends |
| Settings / journals | Atomic JSON | Deliberately not migrated | Do not fold into SQLite in L27 |

## L27 atomic cutover sequence (implementation, not this slice)

Run once in `FeatureServiceScope.CreateProduction`, before `LoadPersistedData`.

1. **Resolve paths.** JSON directory = `%APPDATA%\muesli\data`. Database = `PersistencePaths.DatabasePathFor` (`muesli.db` beside the JSON). Settings and journals stay where they are.
2. **Refuse a newer SQLite schema.** Opening `muesli.db` must throw `PersistenceSchemaException` and **not** start FeatureRuntime against empty JSON. Surface a user-visible failure. Leave JSON untouched.
3. **Read JSON with `JsonHistorySnapshotReader`.** Never load JSON for migration through `AppDataStore` (that quarantines/renames). If blocking problems exist, fail the cutover, keep JSON authoritative, do not create a half-imported DB as the runtime store.
4. **Backup ownership.**
   - SQLite: existing `JsonToSqliteMigrationService` copies a pre-migration DB to `muesli.db.premigration-<utc>.bak` when the DB already has rows.
   - JSON: copy or retain the five history files (and `.bak` siblings) as a dated snapshot under a dedicated folder such as `data/json-history-pre-sqlite-<utc>/`. The current migrator only reads JSON; L27 must add an explicit JSON snapshot if runtime will stop writing JSON. **Do not delete JSON** until a separately defined retention point (below).
5. **Import one transaction.** Folders → templates → dictations → meetings (`JsonToSqliteMigrationService.Import` order). Digest-compare before commit. On mismatch, roll the transaction; JSON remains authority.
6. **Idempotence.** Completed `migration_runs` row keyed by source fingerprint. Second launch with unchanged JSON is `AlreadyCurrent` and writes nothing. If JSON changed after a completed run (user restored backup, or a crash mixed writers), **do not** silently re-import on top; fail closed and keep the existing DB plus JSON snapshot for support.
7. **Flip the adapter.** Subsequent FeatureRuntime reads/writes go to repositories. JSON history becomes read-only leftover except dictionary until a dictionary table exists.
8. **Restart.** A killed process during import leaves the transaction uncommitted (`migration_runs.state = started` → marked failed on retry). No partial meetings.
9. **Do not report success** unless verification digests match and the adapter is the process that will serve the next load.

### Rollback

- If this run created the DB: delete `muesli.db` plus `-wal`/`-shm` (existing `Rollback`).
- If a premigration DB backup exists: restore it over `muesli.db`.
- JSON snapshot: copy back to `windows-*.json` if L27 had already stopped writing JSON.
- Dictionary JSON is never part of the SQLite transaction; do not revert it unless the failed run wrote it.
- After rollback, FeatureRuntime must load JSON again. Do not leave the process on an empty SQLite store.

### JSON backup retention (define here, implement in L27; do not delete in the first green launch)

Proposed retention point (product can tighten): keep the JSON snapshot and `.bak` files until **all** of:

1. three consecutive successful launches with `AlreadyCurrent`;
2. a cloned-profile digest equality recorded in tests;
3. an explicit support/export copy is not in progress.

Until that point, L27 must not delete `windows-dictations.json`, `windows-meetings.json`, `windows-meeting-folders.json`, `windows-meeting-templates.json`, their `.bak` files, or the dated snapshot directory. Dictionary JSON remains live. Settings JSON is unrelated.

Worktrees of `efa961c` do not contain `Muesli.Windows/Models/*.cs` or `Features/Models/` because `.gitignore` has `models/` and Windows git is case-insensitive. Copy those local files from a machine that already built Wave 0 before compiling. Do not add them from this slice; they are not L01-owned.

### Schema-version rules

| Store | Current | Forward | Backward |
|---|---|---|---|
| JSON envelope (`AtomicJsonFile`) | 1 | Envelope `> 1` is corruption (quarantine, empty) | Version 0 = raw array/object, still loaded |
| Meeting record | 5 | `> 5` throws on `LoadMeetings` today | `MigrateMeeting` upgrades 0–5 |
| Settings | 8 | File left unchanged; saves suppressed | Migrated on load |
| SQLite `PRAGMA user_version` | 1 | Refuse to open | Apply numbered migrations in order; never edit a released migration |

L27 must not auto-upgrade a meeting schema 6 into SQLite. Fail closed, keep JSON.

## Preserve list (L27 must not lose)

Ordering, IDs, timestamps (tick precision), folder ids, folder **array order → SortOrder**, notes (generated vs manual), aliases, automation JSON (verbatim, including unknown fields), settings template **name** references, audio path strings, title ownership, session state, health warnings, model ids / live ownership, dictionary entries (JSON leftover). Visible selection: preserve in-process; there is no on-disk selection.

ParentId: imported folders stay roots. After cutover, folder saves must round-trip `ParentId` even if the UI is still flat, or L28 cannot ship.

## macOS parity note (no CloudKit)

| Capability | macOS reference | Windows now | After L27/L28/L29 |
|---|---|---|---|
| Dictation history | `DictationStore.swift` | JSON `AppDataStore` + WPF list | SQLite `IDictationRepository` |
| Nested folders | `DictationStore.swift` `parentID` / `SidebarView.swift` tree | Repo has ParentId; UI does not | L28 UI |
| Search | `SearchResultsView.swift` | In-memory title/summary/transcript/metadata | L29 via `ISearchRepository` (notes already indexed in SQLite) |
| Follow-up meetings | `MeetingFollowUpPolicy.swift` | Repo rows; UI Missing | L32 |
| Sync | `MuesliICloudSyncEngine.swift` | Excluded. Do not design a CloudKit replacement. | D3 |

Windows remains local SQLite + leftover JSON. Phase 11 / SYNC-01 stays externally blocked.

## Characterization tests

File: `windows-native/Muesli.Windows.Tests/PersistenceCutoverCharacterizationTests.cs`

Locks current JSON load/save/order, absence of persisted selection, one-level folders vs SQLite ParentId, notes/aliases/templates/automation/dictionary, sticky warnings, missing files, corrupt+backup, forward envelope as corruption, forward meeting schema **throw**, schema-0 upgrade, default SQLite limit 100 vs JSON-all, no implied `muesli.db`, deletion purge, and production search skipping manual notes while SQLite finds them.

No placeholder migrated-success path. Tokens only; warnings/exceptions must not contain titles or body text except opaque ids required to identify the schema-forward throw.

### Commands

```powershell
dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --filter FullyQualifiedName~PersistenceCutoverCharacterization --no-restore
dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --filter FullyQualifiedName~PersistenceRepositoryTests --no-restore
dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --filter FullyQualifiedName~PersistenceCutoverCharacterization --no-restore -c Release
```

Also run `PersistenceMigrationTests` / `PersistenceSchemaTests` before claiming L27 implementation complete (not required to pass this design slice beyond the characterization filter).

## Remaining L27 physical / implementation gates

Do **not** mark L27 complete from this slice. Implementation must still:

1. Wire `IPersistenceCutover` in `FeatureServiceScope` only (lock-list edit by Agent E).
2. Replace whole-list JSON saves with per-record repository writes so child rows survive.
3. Load meeting details, not heads, before painting the UI.
4. Pass an unbounded (or `Count()`-sized) list query; never default `Limit = 100` for the dashboard.
5. Keep dictionary on JSON or add a table **before** dropping `AppDataStore`.
6. Round-trip `ParentId` on folder save; do not flatten through `PersistedMeetingFolder`.
7. Snapshot JSON; do not delete it until the retention point above.
8. Fail closed on corrupt JSON, forward meeting schema, and forward SQLite schema. No fake success.
9. Reconcile `DateTime.Now` vs UTC ticks so digest verification does not shift local timestamps.
10. Preserve verbatim `AutomationResultJson`.
11. Keep settings/journals as JSON.
12. Same-process selection stays; do not invent on-disk selection.
13. Cloned real-profile digest equality; second launch `AlreadyCurrent`; forced-failure rollback keeps JSON intact.
14. Privacy: deletion still removes search docs and leftover JSON artifacts; no transcript/title/path in logs.
15. L28/L29/L32 remain separate. Nested UI, full-content search UI, and follow-up UI are out of L27.
16. Debug + Release tests, zero warnings/errors, visible launch only when runtime wiring lands.

## Ledger update

**Not warranted.** L01 design + characterization only. L27 is not implemented. ORG-01 is still Partial (UI). SEARCH-01 still does not index notes in the product UI.
