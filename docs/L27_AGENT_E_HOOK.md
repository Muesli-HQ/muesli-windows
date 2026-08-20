# L27 Agent E integration hook

Status: **implementation exists, unwired.** L27, ORG-01, and SEARCH-01 remain incomplete. Do not edit the launch ledger from this note.

Feature flag: `PersistenceCutoverGate.FeatureFlagName` = `L27SqliteHistoryCutover`.
Default: `PersistenceCutoverGate.DefaultEnabled` = **false**. Qualification can opt in with the
process-only `MUESLI_SQLITE_HISTORY_CUTOVER=1`; every other value is off.

Agent D does not edit `AppServices.cs`. Agent E adds the following in `FeatureServiceScope.CreateProduction`, after logging/settings exist and **before** FeatureRuntime loads history. JSON `AppDataStore` stays authoritative while the flag is off.

```csharp
if (PersistenceCutoverGate.IsEnabled)
{
    var cutover = new PersistenceCutover(PersistencePaths.DefaultDataDirectory);
    var migrated = cutover.EnsureMigrated();
    if (!migrated.Succeeded)
        throw new InvalidOperationException(migrated.Failure ?? "History cutover failed.");
    var history = SqliteLibraryHistoryAdapter.Open(PersistencePaths.DefaultDataDirectory);
    // Hand `history` to FeatureRuntime instead of AppDataStore.
}
```

`EnsureMigrated` reads the schema-v2 `persistence_cutover_state` row before reading JSON. Once its
authority is `sqlite`, it ignores changes to the retained JSON snapshot and never re-imports or
falls back. Keep the adapter alive for the FeatureRuntime scope so its first successful mutation
can record the durable post-cutover-write marker. If cutover fails, fail closed before constructing
the SQLite adapter.

Dictionary leftovers stay inside `SqliteLibraryHistoryAdapter` (`windows-dictionary.json`). Settings and session journals stay atomic JSON. Do not delete JSON snapshots; retention is a later point.
