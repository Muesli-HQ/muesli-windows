# Shared Swift core pinning

Windows release builds consume the shared Swift `MuesliCore` package (`native/MuesliNative`) as a
real production dependency. Release packaging must resolve it from an immutable commit, never a
moving branch. The pin lives in `windows-native/shared-core.lock.json`:

```json
{
  "schemaVersion": 1,
  "repository": "Muesli-HQ/muesli",
  "packagePath": "native/MuesliNative",
  "revision": "<40-character commit SHA>",
  "requiredAbiCapabilities": 3
}
```

`revision` is intentionally empty on this branch. An empty revision:
- fails `.github/workflows/windows-ci.yml` at the "Read pinned shared-core revision" step;
- fails local release packaging (`scripts/rehearse-windows-release.ps1` and
  `scripts/build-signed-release.ps1`) unless development explicitly opts into the unpinned
  `MUESLI_SHARED_CORE_PACKAGE` override, in which case it emits a loud warning and the resulting
  build is tagged `sharedCoreReleaseEvidence=false` in the rehearsal report.

## Exact user step to pin

1. In the shared Swift/macOS checkout (`C:\Users\madha\Downloads\muesli-main\muesli-main`),
   commit the pending `native/MuesliNative` changes on `main` (or the intended release branch).
2. Take the committed SHA:

   ```powershell
   git -C C:\Users\madha\Downloads\muesli-main\muesli-main rev-parse HEAD
   ```

3. Confirm the commit contains the ABI the Windows bridge requires (capabilities bitmask `3`:
   persistence + text processing) and that the commit is reachable from the configured
   `repository`/`packagePath`.
4. Write the 40-character SHA into `windows-native/shared-core.lock.json`:

   ```powershell
   $sha = git -C C:\Users\madha\Downloads\muesli-main\muesli-main rev-parse HEAD
   # edit windows-native/shared-core.lock.json: "revision": "<$sha>"
   ```

5. Re-run CI (`Windows CI` workflow) or locally
   `pwsh -ExecutionPolicy Bypass -File scripts\rehearse-windows-release.ps1`. The rehearsal report
   must record `sharedCorePinned=true` and `sharedCoreReleaseEvidence=true`.

## Local development override (not release evidence)

Developers without a committed shared revision may set:

```powershell
$env:MUESLI_SHARED_CORE_PACKAGE = 'C:\Users\madha\Downloads\muesli-main\muesli-main\native\MuesliNative'
```

The rehearsal then builds from the working tree, warns that the pin is empty, and records
`sharedCorePinned=false`. Do not present such a build as release evidence.

## Dependency-isolation proof

The Windows bridge must resolve only the shared package's declared dependencies
(`swift-crypto`, `swift-asn1`, `swift-log`). Re-running the bridge build with an external
`--scratch-path`/`--cache-path` must leave `Package.resolved` byte-identical and create no
`.build`/`.swiftpm` residue inside either repository. This keeps the Windows build from silently
vendoring additional Swift packages.
