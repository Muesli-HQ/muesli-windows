# Transcription Benchmarks

The current Windows-native launch path supports seven offline sherpa-onnx
models across five families. CUDA and CPU are execution providers for the same
selected model and engine. The old Python sidecar, CTranslate2 bridge scaffold,
and cross-engine fallback are not part of the product path. The Parakeet data
below is retained historical evidence, not qualification for the full catalog.

## Current Recommendation

- Catalog qualification: run `scripts/smoke-transcription-models.ps1` on real audio, plus provider-specific latency/quality gates where release criteria define them.
- Qwen cleanup benchmark target: native LLamaSharp/GGUF cleanup latency and preservation checks.

## Parakeet TDT 0.6B v3 Baseline

The July 31, 2026 Phase 1 baseline uses the same 29.27-second local WAV and one
reused model instance for three runs per provider:

| Provider | Host threads | Model load | Warm wall time | RTF | Deterministic |
| --- | ---: | ---: | ---: | ---: | --- |
| CUDA (RTX 4070 Laptop) | 4 | 2.53 s | 1.41 s | 0.048 | yes |
| CPU | 8 | 1.90 s | 1.65 s | 0.056 | yes |

Both providers produced the same transcript SHA-256. The CUDA thread default is
four because it beat 1, 2, and 8 threads on the same recording. The retained
reports are `artifacts/benchmarks/phase1-parakeet-v3-cuda.json` and
`artifacts/benchmarks/phase1-parakeet-v3-cpu.json`.

## Phase 2 Latency Hardening

The July 31 Phase 2 pass moves model construction, warmup, audio preprocessing,
inference, and timestamp segmentation off the WPF dispatcher. It also writes a
small verification stamp after a successful full SHA-256 model verification.
On later launches the stamp is accepted only if the model id, every pinned
hash, file length, and last-write timestamp are unchanged. A missing, stale, or
unwritable stamp never bypasses verification and never makes a read-only model
cache unusable.

On the same 29.27-second recording, a full four-file verification took 1.02 s.
The unchanged-cache validation took 43 ms, removing roughly 0.98 s from later
startup initialization. The retained Phase 2 warm results are:

| Provider | Model load | Cache verification | Warm wall time | RTF | Deterministic |
| --- | ---: | ---: | ---: | ---: | --- |
| CUDA (RTX 4070 Laptop) | 2.40 s | 43 ms | 1.17 s | 0.040 | yes |
| CPU | 1.77 s | 42 ms | 1.31 s | 0.045 | yes |

Reports:

- `artifacts/benchmarks/phase2-parakeet-v3-cuda-first-verify.json`
- `artifacts/benchmarks/phase2-parakeet-v3-cuda-cached.json`
- `artifacts/benchmarks/phase2-parakeet-v3-cpu-cached.json`

The meeting-segmentation regression uses the same 29.27-second dictation and
produces 9 token-aligned segments. Across five CUDA runs, the transcript and
segment-layout hashes are both deterministic, segment text reconstructs the
complete transcript, timestamps are chronological, and the longest segment is
5.92 seconds. A 165.89-second local meeting microphone recording completes in
about 7.94 seconds warm (RTF 0.048); its sparse speech is split into 5
chronological segments with a maximum duration of 4.96 seconds. Reports:

- `artifacts/benchmarks/parakeet-v3-meeting-segments.json`
- `artifacts/benchmarks/parakeet-v3-long-mic-segments.json`

## Benchmark Harness

The Models page includes an app-aligned benchmark harness backed by
`TranscriptionBenchmarkService`. It uses the most recent captured dictation or
meeting WAV and reports:

- model download/load time
- warm transcription wall time
- real-time factor
- segment count
- timestamp usefulness
- backend, thread count, and audio preprocessing diagnostics

Release validation should repeat the harness on representative CPU-only and
NVIDIA machines and compare results with the retained regression baselines.

For deterministic regression runs, use explicit local audio and an optional
reference transcript:

```powershell
.\scripts\benchmark-native-transcription.ps1 `
  -AudioPath .\testdata\dictation.wav `
  -ReferencePath .\testdata\dictation.txt `
  -Runs 5
```

The command runs the same normalized WAV multiple times and writes a JSON report
under `artifacts/benchmarks`. It records the audio SHA-256, model load time,
inference and wall time, real-time factor, backend, device, compute type, model
reuse, transcript SHA-256, deterministic-output status, WER, CER, and timestamp
coverage. It never invokes Python.

The same command can enforce release gates and return a failing exit code on a
latency, provider, model-reuse, transcript, segment-layout, WER, or CER
regression:

```powershell
.\scripts\benchmark-native-transcription.ps1 `
  -AudioPath .\testdata\dictation.wav `
  -ReferencePath .\testdata\dictation.txt `
  -Runs 5 `
  -MaxRealtimeFactor 0.07 `
  -MaxWordErrorRate 0.12 `
  -ExpectedTranscriptSha256 <approved-hash> `
  -RequireDeterministic `
  -RequireModelReuse
```

## Release-to-paste latency

Every real dictation now writes one correlated `Dictation latency trace` log
entry. It separates capture stop/dispose, file flush, audio preparation,
transcription, dictionary cleanup, target-window focus, direct-input or
clipboard fallback, persistence/UI work, release-to-paste, and
release-to-settled time. The default active-app path does not touch the
user's clipboard; `clipboardMs=0` is expected when direct input succeeds.

Summarize the latest real dictations without Python:

```powershell
.\scripts\summarize-dictation-latency.ps1 `
  -Last 100 `
  -OutputPath .\artifacts\benchmarks\dictation-latency.json
```

For Parakeet, WASAPI capture is preserved through a hard link rather than
resampled at key release. Parakeet performs the required Media Foundation
downmix/resample directly into memory, avoiding a temporary normalized WAV.
The 165.89-second regression retained the previous transcript and segment-layout
hashes while reducing measured preprocessing from about 301 ms to 203 ms.

Phase 2 now enforces release-to-paste, explicit model identity, CPU/CUDA,
WER/CER, no-speech, target-app coverage, and human-reference provenance. See
`docs/PHASE2_DICTATION_QUALIFICATION.md`. The automation refuses to treat a
model-generated transcript as ground truth without a named human review.

## Recorded-meeting and media-import qualification

Phase 4 exercises the production meeting pipeline as one unit: microphone and
system-audio ASR, system-audio speaker diarization, and timestamp-based speaker
merge. The same Parakeet and diarization model instances remain alive across
every run.

```powershell
.\scripts\benchmark-native-meeting.ps1 `
  -SystemAudioPath .\testdata\meeting-system.wav `
  -Runs 3 `
  -MaxTotalRealtimeFactor 0.20 `
  -MaxDiarizationRealtimeFactor 0.15 `
  -RequireDeterministic `
  -RequireModelReuse
```

The report records ASR and diarization providers, device, model load/reuse,
individual ASR/diarization/merge times, total RTF, speaker count and coverage,
and hashes for transcript, timestamp segments, diarization, and merged output.
CUDA qualification overlaps diarization with independent ASR work; CPU runs the
stages sequentially to avoid native-session thread contention.

Media import uses a schema-1 JSON manifest and the same native benchmark path:

```powershell
.\scripts\test-media-imports.ps1 `
  -ManifestPath .\testdata\media-imports.json `
  -Runs 3
```

Each case can gate decoded duration, transcript length, RTF, warm wall time,
WER/CER with a human reference, or an expected clear decode rejection. Decoder
duration is measured from the samples delivered to Parakeet, which avoids zero
or inaccurate duration metadata from some media containers.
