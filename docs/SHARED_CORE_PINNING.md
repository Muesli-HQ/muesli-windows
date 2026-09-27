# Shared Swift core pinning

Windows can consume the shared Swift `MuesliCore` package (`native/MuesliNative`) through the
`MuesliCoreABI` C bridge. That bridge is **optional**: the only shipping consumer is text
processing (transcript normalization and word count), and `ManagedTranscriptTextProcessor` is a
parity-tested managed implementation of the same contract. Windows persistence is authoritative in
Windows SQLite and never uses the Swift `DictationStore`.

The pin lives in `windows-native/shared-core.lock.json`:

```json
{
  "schemaVersion": 1,
  "repository": "Muesli-HQ/muesli",
  "packagePath": "native/MuesliNative",
  "revision": "<40-character commit SHA>",
  "requiredAbiCapabilities": 3,
  "bridgeRequired": true,
  "shippingMode": "shared-core"
}
```

## Shipping modes

`bridgeRequired` selects one of two deliberate modes; there is no third "resolve a branch" mode.

- **`bridgeRequired` true** — the release builds and stages the Swift bridge from the immutable
  `revision`. The revision must be a full 40-character SHA. An empty revision fails
  `.github/workflows/windows-ci.yml` at the "Read pinned shared-core revision" step and fails local
  release packaging. Release CI must never resolve a moving branch.
- **`bridgeRequired` false** — no approved upstream ABI commit exists, so the release ships the
  parity-tested managed text processor. `revision` must stay empty; the workflow skips the Swift
  toolchain, vcpkg and shared checkout, quarantines any staged bridge DLLs, and the packaged app
  starts on the managed fallback. The rehearsal report records
  `sharedCoreMode=ManagedFallback` and `sharedCoreReleaseEvidence=false`.

The withdrawn macOS bridge PR left `revision` empty, so the repository currently ships in
managed-fallback mode. This is a recorded product decision, not a disabled check: the workflow
still rejects an empty revision whenever `bridgeRequired` is true.

## Pin only after an approved upstream source exists

1. Obtain a reviewed commit already reachable from `Muesli-HQ/muesli` that contains the
   ABI Windows requires (capabilities bitmask `3`: persistence and text processing).
   A local commit, a deleted fork branch, or the withdrawn PR is not a valid release pin.
2. Check that commit out in the shared repository and take its full SHA, not a branch name:

   ```powershell
   git -C C:\Users\madha\Downloads\muesli-main\muesli-main fetch origin
   git -C C:\Users\madha\Downloads\muesli-main\muesli-main checkout <approved-commit>
   git -C C:\Users\madha\Downloads\muesli-main\muesli-main rev-parse HEAD
   ```

3. Confirm the commit is reachable from the configured `repository`/`packagePath`.
4. Write the 40-character SHA and `"bridgeRequired": true` into
   `windows-native/shared-core.lock.json`, and set `"shippingMode": "shared-core"`.
5. Re-run CI (`Windows CI` workflow) or locally
   `pwsh -ExecutionPolicy Bypass -File scripts\rehearse-windows-release.ps1`. The rehearsal report
   must record `sharedCorePinned=true` and `sharedCoreReleaseEvidence=true`.

## Local development override (not release evidence)

Developers without a committed shared revision may set:

```powershell
$env:MUESLI_SHARED_CORE_PACKAGE = 'C:\Users\madha\Downloads\muesli-main\muesli-main\native\MuesliNative'
```

The rehearsal then builds from the working tree, warns that the pin is empty, and records
`sharedCorePinned=false` and `sharedCoreMode=DevOverride`. Do not present such a build as release
evidence. This override only applies while `bridgeRequired` is true.

## Dependency-isolation proof

The Windows bridge must resolve only the shared package's declared dependencies
(`swift-crypto`, `swift-asn1`, `swift-log`). Re-running the bridge build with an external
`--scratch-path`/`--cache-path` must leave `Package.resolved` byte-identical and create no
`.build`/`.swiftpm` residue inside either repository. This keeps the Windows build from silently
vendoring additional Swift packages.
