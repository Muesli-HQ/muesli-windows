# L10 CPU catalog fixture kit

This folder is the Windows seven-family **CPU** catalog contract. It is **not**
a passing L10 qualification. Ledger row MOD-02 stays **Implemented with
verification debt** until `scripts/smoke-transcription-models.ps1` plus
provider-specific CPU gates pass on real speech for every advertised model.

Authoritative inventory: `docs/L10_CPU_CATALOG_INVENTORY.md`.
Runtime catalog: `windows-native/Muesli.Windows/Services/TranscriptionModelCatalog.cs`.

## Layout

| Path | Purpose |
|---|---|
| `advertised-cpu-models.json` | Machine-readable IDs, hashes, roles, languages. Tests fail if this drifts from `TranscriptionModelCatalog`. |
| `windows-cpu-catalog-qualification.template.json` | Placeholder slots, one per advertised model. Must remain unreviewed. |
| `audio/` | Future human-recorded WAVs (`*.wav` is gitignored). |
| `references/` | Future human-written `.txt` references. |

Do not check in dummy WAVs labelled as human-reviewed. Do not copy model output
into a reference and call it reviewed.

## Smoke selection

`scripts/smoke-transcription-models.ps1` reads model IDs from
`advertised-cpu-models.json` in catalog order. It does not download models
unless `-Prepare` is passed. Default audio is
`%APPDATA%\muesli\captures\last-dictation.wav` — that operator capture is not a
checked-in fixture.

A later qualification run should pass an explicit reviewed WAV:

```powershell
.\scripts\smoke-transcription-models.ps1 `
  -AudioPath .\qualification\cpu-catalog\audio\english-speech.wav `
  -OutputDirectory .\artifacts\benchmarks\phase1-model-smoke
```

That command must fail today because `english-speech.wav` is not checked in.

## Remaining physical gate

See `docs/L10_CPU_CATALOG_INVENTORY.md`. Public package metadata stays CPU-only.
CUDA is L11 and is not an L10 pass path.
