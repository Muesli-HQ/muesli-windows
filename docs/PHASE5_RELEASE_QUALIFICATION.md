# Phase 5: Native Release Qualification

> Historical evidence note: this document describes the Parakeet-only build qualified at the time. It does not qualify the current seven-model catalog or separate role routing; see `WINDOWS_TRANSCRIPTION_MODELS.md`.

Phase 5 turns the packaged Windows build into an auditable release candidate.
It adds no transcription choices and no consumer UI. Parakeet TDT 0.6B v3
remains the single ASR engine; CPU and CUDA are execution providers for the
same model.

## Headless native diagnostics

The WPF executable supports a non-UI diagnostics mode:

```powershell
.\Muesli.exe --diagnose-native `
  --audio .\testdata\dictation.wav `
  --runs 10 `
  --output .\native-runtime.json
```

Without `--audio`, the command validates runtime loading and records the app,
.NET, Windows, architecture, provider, cache state, and SHA-256 evidence for
the Sherpa/ONNX runtime files found beside the executable. With audio, it also
runs 2-50 transcriptions through one client and records every wall time, RTF,
provider, device, compute type, model-reuse state, output hashes, working set,
and private memory.

The stress report fails internally if decoded duration is zero, the model is
recreated, output changes, timestamp layout changes, or the execution provider
changes during the run.

Memory evidence separates first-run provider allocation from steady state.
CUDA and CPU providers can reserve native memory lazily on their first measured
decode; release gates therefore retain total growth for investigation but gate
growth from the second through final run.

## Packaged release gate

Run the complete release candidate qualification from the repository:

```powershell
.\scripts\qualify-windows-release.ps1 `
  -AudioPath .\testdata\dictation.wav `
  -Provider cuda `
  -Runs 10 `
  -MaxRealtimeFactor 0.10 `
  -MaxWorkingSetGrowthMb 256 `
  -MaxPrivateMemoryGrowthMb 256
```

Repeat with `-Provider cpu` on the CPU release machine. The command:

1. validates and extracts the release ZIP;
2. rejects Python/venv/removed-backend artifacts and incomplete native files;
3. runs the packaged executable's headless stress path;
4. applies provider, RTF, wall-time, memory, reuse, determinism, and optional
   approved transcript/segment-hash gates;
5. launches the packaged WPF app and checks only newly written log content for
   `ERROR`, `Unhandled UI exception`, or `XamlParseException`;
6. records CPU/GPU/OS evidence, ZIP size/SHA-256, native file hashes, and the
   executable's Authenticode status in one JSON report.

Use `-RequireSignature` for the public release gate after a real signing
certificate is configured. Until then, the report records `NotSigned` without
pretending that signing is complete.

## Local ten-run baseline

The 29.27-second local speech capture produced identical transcript and segment
hashes on CPU and CUDA. The retained development-build measurements are:

| Provider | Warm wall | Warm RTF | Steady working-set growth | Steady private-memory growth |
| --- | ---: | ---: | ---: | ---: |
| CUDA | 1.17 s | 0.040 | 2.8 MB | 1.5 MB |
| CPU | 1.40 s | 0.048 | 2.0 MB | 0.8 MB |

The packaged Phase 5 reports under `artifacts/qualification` are the release
evidence. Local paths and machine details make those reports unsuitable as
portable test fixtures, so they remain ignored by git.

## Human checks that remain human

Automation cannot truthfully confirm that pasted text visually appeared in
Notepad/Chrome, that a real multi-speaker meeting was labelled correctly, or
that onboarding feels correct on a new user profile. Phase 5 bundles machine
evidence and blocks measurable regressions, but the human-confirmed Phase 2
dictation corpus/paste matrix and the remaining release-checklist items remain
required before a public release. See `PHASE2_DICTATION_QUALIFICATION.md`.
