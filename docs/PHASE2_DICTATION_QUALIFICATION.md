# Phase 2 Windows dictation qualification

Phase 2 is not release-qualified until both the human corpus and the four real
paste-target suites below pass. A build, unit-test pass, generated transcript,
or model-smoke report is not a substitute for this evidence.

## Human-reviewed corpus

Corpus audio and references are intentionally not fabricated or checked in by
automation. A human reviewer must record or select each WAV, listen to it, and
write or correct every reference transcript. Model output may be used as a
review aid, but it may not be copied into the reference and labelled reviewed
without listening to the complete audio.

Use a schema-2 JSON manifest with these top-level fields:

```json
{
  "schemaVersion": 2,
  "name": "windows-dictation-human-qualification",
  "defaultMaxWordErrorRate": 0.15,
  "defaultMaxCharacterErrorRate": 0.08,
  "defaultMaxRealtimeFactor": 0.20,
  "cases": []
}
```

Each transcript case requires `id`, `audio`, `reference`,
`expectedOutcome: "transcript"`, one or more `categories`,
`referenceProvenance`, `reviewedBy`, and `reviewedAt`. Silence and background
noise cases may use `expectedOutcome: "no-speech"` without a text reference,
but still require `referenceProvenance: "human-reviewed"` and reviewer data.
The runner rejects empty or model-only references.

The manifest must collectively cover:

- `short-command`
- `paragraph`
- `dictionary` (names, multi-word phrases, and replacements)
- `numbers-punctuation`
- `accent` (only accents/languages advertised by the selected model)
- `silence`
- `background-noise`

Validate the reviewed corpus without inference:

```powershell
.\scripts\test-transcription-corpus.ps1 `
  -ManifestPath <reviewed-manifest.json> `
  -ValidateOnly
```

Run the required CPU and CUDA gates for one explicit dictation role model:

```powershell
.\scripts\qualify-dictation-corpus.ps1 `
  -ManifestPath <reviewed-manifest.json> `
  -ModelId parakeet-v3 `
  -Runs 3
```

The combined run fails on model-identity or provider drift, missing text,
unexpected speech for silence/noise, WER/CER, RTF/warm latency, nondeterminism,
or recognizer non-reuse. CUDA unavailability is a failed CUDA qualification,
not an automatic CPU pass.

## Release-to-paste and target applications

Complete one fresh dictation into each of Notepad, Chrome, Office, and another
editable Windows application. Visually compare the entire delivered text with
what was spoken, then qualify that specific trace. Do not put expected or
observed transcript text in logs or report arguments.

```powershell
.\scripts\qualify-dictation-target.ps1 `
  -TargetKind Notepad `
  -TargetProcess notepad `
  -TraceId <fresh-trace-id> `
  -RequiredModelId parakeet-v3 `
  -MaxReleaseToPasteMs 3000 `
  -ReviewedBy <human-reviewer> `
  -ReviewedAt (Get-Date) `
  -TextVerified `
  -OutputPath .\artifacts\benchmarks\dictation-targets\notepad.json
```

Repeat for `Chrome`, `Office`, and `Other`, then enforce complete app coverage:

```powershell
.\scripts\qualify-dictation-target-suite.ps1 `
  -ReportPaths <notepad.json>,<chrome.json>,<office.json>,<other.json> `
  -OutputPath .\artifacts\benchmarks\dictation-targets\summary.json
```

The target gate requires active-app delivery, original-app foreground
restoration, an explicit native engine/model identity, a non-empty result,
human paste verification within two hours of the fresh trace, durable history,
and the configured release-to-paste limit. Window
titles and transcript text are neither captured nor persisted by this gate.

## Manual route and lifecycle matrix

In the packaged WinUI app, exercise the selected microphone, System default,
default-device change while recording, explicit device disconnect, Bluetooth
hands-free profile loss/reappearance, silence, background noise, Escape cancel
during capture, Escape cancel during transcription, hands-free click-to-stop,
clipboard-only mode, target closure, and elevated-target rejection. Confirm
that finalized route segments are recovered, fallback is disclosed, no
cancelled/no-speech item enters history, active-app delivery leaves the
pre-existing clipboard unchanged on failure, and temporary timestamped WAVs
are removed. Clipboard-only mode is intentionally allowed to replace the
clipboard because it is an explicit user-selected delivery mode.

Use a fresh log byte marker before the exercise. The appended slice must contain
no transcript text, window title, `ERROR`, `Unhandled UI exception`, or
`XamlParseException`. Expected route fallback is an informational diagnostic;
an unrecovered route failure is not a passing qualification.

## Current evidence state

A 2026-09-26 long-form timing probe repeated the pinned Parakeet v3 test WAV to
61.52 seconds of synthetic speech and decoded it through `Muesli.Windows.CommandHost
--benchmark-native` on this machine's CPU provider. Warm inference took **5.10 s**;
warm wall time was **5.18 s**; the first run including model initialization took
**7.36 s** (2.00 s model initialization). This measures the model, not microphone
stop, optional text cleanup, persistence, or active-app paste. The selected
production settings were Parakeet v3, Automatic provider, and local cleanup off.
Dictation warms the offline model while recording but starts the actual
transcription on shortcut release; incremental transcription currently belongs
to the opt-in live meeting path. The current app logs privacy-safe
`captureStopMs`, `asrWallMs`, `textPipelineMs`, `persistenceMs`, `deliveryMs`, and
`totalMs` for a real dictation. A perceived 30-second release-to-paste delay
needs a fresh one-minute trace before changing the model or pipeline.

The repository supplies the schema enforcement, CPU/CUDA runner, paste-target
runner, unit tests, and privacy gates. The human-reviewed audio/reference corpus
and four human paste reviews must be supplied by a human operator. Until those
artifacts exist and pass, Phase 2 remains implemented but not fully qualified.

Operator kit for filling this evidence (template, not a passing corpus):
`qualification/dictation-corpus/` and `docs/L14_L15_EVIDENCE_STATUS.md`. A
placeholder manifest must fail `-ValidateOnly` until real WAVs, listened
references, and reviewer identity exist. Do not treat rehearsal output as
qualification.
