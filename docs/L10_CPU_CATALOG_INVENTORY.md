# L10 CPU catalog inventory

Status: **inventory-not-qualified**. This document is the Wave 1 Agent A design
and fixture inventory for module L10. It does **not** mark ledger row MOD-02
Complete. QUAL-01 stays Partial.

The seven advertised Windows offline CPU families already exist in
`TranscriptionModelCatalog`. The remaining L10 exit is physical: every advertised
ID must complete real-speech inference on the packaged CPU provider, with
provider-specific gates, without silently skipping a family.

Machine-readable contract: `qualification/cpu-catalog/advertised-cpu-models.json`.
Smoke entry point: `scripts/smoke-transcription-models.ps1` (reads that JSON).
Placeholder fixture template: `qualification/cpu-catalog/windows-cpu-catalog-qualification.template.json`.

## Role ownership

Every catalog row is selectable for **both** independent roles:

| Role | Setting | Consumers |
|---|---|---|
| Dictation | `DictationModelId` | hold-to-talk, onboarding dictation test |
| Final meeting / import | `FinalMeetingModelId` | recorded meeting mic/system audio, imported media |

Default for both roles is `parakeet-v3`. Selecting a role never downloads,
verifies, or substitutes a model. Live Nemotron 3.5, Silero VAD, diarization
ONNX, and optional Qwen GGUF cleanup are **not** L10 CPU families.

## Advertised CPU families

Cache roots (override with environment variables):

- Parakeet: `%USERPROFILE%\.cache\muesli\native-parakeet` or `MUESLI_NATIVE_PARAKEET_CACHE`
- Other six: `%USERPROFILE%\.cache\muesli\native-asr` or `MUESLI_NATIVE_ASR_CACHE`

Artifact path = cache root + `directoryName`. Required ONNX/token files are
pinned beside each archive hash in `TranscriptionModelCatalog` and the JSON
contract.

| Model ID | Role | Artifact location | Archive SHA-256 | Advertised languages | Current coverage | Skip reason | Remaining L10 gate |
|---|---|---|---|---|---|---|---|
| `parakeet-v3` | Both; **default** dictation and final meeting/import | `native-parakeet\sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` | `5793D0FD397C5778D2CF2126994D58E9D56B1BE7C04D13C7A15BB1B4EAFB16BF` | 25 European languages · automatic | Hardware-free catalog/hash/lifecycle tests run. Smoke script includes this ID. No checked-in reviewed WAV. | xUnit does not skip this ID. Real-audio smoke is a script, not a skippable fact. Default audio `%APPDATA%\muesli\captures\last-dictation.wav` is operator-local and missing on a clean machine (script throws). | Prepare + CPU real-speech smoke; multilingual human review for advertised languages; report must identify provider `cpu`. |
| `whisper-tiny-en` | Both; summary targets quick English dictation | `native-asr\sherpa-onnx-whisper-tiny.en` | `2BD6CF965C8BB3E068EF9FA2191387EE63A9DFA2A4E37582A8109641C20005DD` | English | Same catalog/lifecycle tests plus smoke ID list. No reviewed WAV. | Same: not an xUnit skip. | Prepare + CPU English real-speech smoke with provider `cpu`. |
| `whisper-small-en` | Both | `native-asr\sherpa-onnx-whisper-small.en` | `0CDBA2B8AAAB69E04847F3427CC9709574112E67913A1A84B7FEC3A8729FAA9A` | English | Same. | Same. | Prepare + CPU English real-speech smoke with provider `cpu`. |
| `whisper-medium-en` | Both; summary targets imported / difficult audio | `native-asr\sherpa-onnx-whisper-medium.en` | `73D95C169A410B5F23A79F8901374B26E0A16A09EA7F02B5E1DB983F4CDFDD67` | English | Same. | Same. | Prepare + CPU English real-speech smoke with provider `cpu`. |
| `sensevoice-small-int8` | Both | `native-asr\sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17` | `7D1EFA2138A65B0B488DF37F8B89E3D91A60676E416F515B952358D83DFD347E` | Chinese, English, Japanese, Korean, Cantonese · automatic | Same. English slot only in the placeholder template. | Same. Non-English advertised languages have **no** fixtures. | Prepare + CPU English smoke, then advertised-language fixtures or honest scope reduction. |
| `qwen3-asr-0.6b-int8` | Both | `native-asr\sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25` | `393F8A14E2F5FB96746AAAB342997A40641001FBD5BF9592A080A8329178EE96` | 30 languages plus Chinese dialects · automatic | Same. English slot only in the placeholder template. | Same. Extra advertised languages have **no** fixtures. | Prepare + CPU English smoke, then advertised-language fixtures or honest scope reduction. |
| `cohere-transcribe-int8-en` | Both; Windows selects English (`language=en`) | `native-asr\sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01` | `BD582588D50685A795DCD2807AB77E11361B8312D96C53884682DEF45AB4206D` | English selected (weights support 14 languages) | Same. | Same. Non-English support is not a Windows advertised role. | Prepare + CPU English real-speech smoke with provider `cpu`. |

Required file SHA-256 values are pinned in `TranscriptionModelCatalog` and
copied into `advertised-cpu-models.json`. `CpuCatalogInventoryTests` fails if
either side drifts.

## How smoke selection works

`scripts/smoke-transcription-models.ps1`:

1. Requires a built `Muesli.exe`.
2. Requires a real WAV longer than an empty header. Default:
   `%APPDATA%\muesli\captures\last-dictation.wav`. That file is **not** a
   repository fixture.
3. Loads every `models[].id` from `qualification/cpu-catalog/advertised-cpu-models.json`.
4. Refuses to run if that JSON claims CUDA is in the public package.
5. Optionally `--prepare-model` when `-Prepare` is set. Prepare is sequential
   and network-backed. The script never treats a missing model as success.
6. Runs `--benchmark-native --model <id>` for each ID.
7. Fails the process if any model exits non-zero, writes no report, reports a
   different model ID, or has zero successful inference rows.

The script does not assert WER/CER, RTF, deterministic reuse, or
`WarmBackend=cpu`. Those are remaining provider-specific L10 gates
(`scripts/benchmark-native-transcription.ps1 -ExpectedProvider cpu` plus corpus
thresholds). A later run must pass an explicit reviewed WAV, not rely on
`last-dictation.wav`.

## Tests that run vs skip

| Test / script | Runs today? | Relation to L10 |
|---|---|---|
| `TranscriptionModelPlatformTests` | Yes, hardware-free | Pins 7 IDs, archive/file hashes, lifecycle. No real audio. |
| `ModelAndSingleInstanceTests.TranscriptionCatalogHasUniqueSafeModelDefinitions` | Yes | Unique IDs and hash format. |
| `CpuCatalogInventoryTests` | Yes (this slice) | Fails if advertised IDs/hashes/languages drift from the catalog, smoke script, or inventory doc. |
| `PackageDisclosureAndNativeInventoryTests` | Yes | Public package remains CPU-only. Not seven-family ASR evidence. |
| `smoke-transcription-models.ps1` | Operator script; not in xUnit | **Does not skip.** Throws if audio, exe, or any model fails. |
| `QualificationFact("MUESLI_MEDIA_FIXTURE_DIR")` | Skips unless env dir exists | Import transcodes (IMP-01). Not L10. |
| `QualificationFact("MUESLI_MULTISPEAKER_FIXTURE_SOURCE")` | Skips unless env dir exists | Diarization (DIA-01). Not L10. |
| `QualificationFact("MUESLI_STREAMING_QUALIFICATION_MODEL")` | Skips unless env dir exists | Live Nemotron (MOD-04). Not L10. |
| L14 dictation corpus template | ValidateOnly fails on placeholders | Phase 2 human corpus, default model `parakeet-v3` only. Not the seven-family matrix. |

No xUnit test currently executes real ASR for these seven IDs. That is
intentional: real speech is a physical gate, not a silent pass.

## Fixtures that exist vs missing

Present (honest placeholders):

- `qualification/cpu-catalog/advertised-cpu-models.json`
- `qualification/cpu-catalog/windows-cpu-catalog-qualification.template.json` (`placeholder: true`, empty `reviewedBy`)
- `qualification/cpu-catalog/audio/README.md` and `references/README.md`

Missing (required before L10 can exit):

- Any checked-in or operator-reviewed WAV for `english-speech.wav`
- Human-written `references/english-speech.txt`
- Non-English reviewed audio for Parakeet, SenseVoice, and Qwen advertised languages
- Prepared/verified model caches for all seven IDs on the qualification machine
- Per-model smoke reports under `artifacts/benchmarks/phase1-model-smoke/` on this tree
- CPU provider identity, RTF, WER/CER, cancellation, and corrupt-file failure evidence named in the launch-plan L10 exit

Do not fill the template with generated audio labelled as reviewed.

## Remaining L10 exit (physical)

From `docs/WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md`:

> Exit gate: `smoke-transcription-models.ps1` plus provider-specific gates pass
> for all advertised CPU models. Models that cannot pass are removed or honestly
> scoped before launch.

Still open:

1. Prepare and SHA-verify all seven caches.
2. Record or select real speech; human-listen; write references.
3. Run smoke against that WAV for every advertised ID on the packaged CPU
   provider (`-ExpectedProvider cpu` / report `WarmBackend=cpu`).
4. Add provider-specific gates the smoke script does not yet enforce: RTF,
   WER/CER against the reviewed reference, deterministic reuse, missing/corrupt
   file failure.
5. Either add advertised-language fixtures for multilingual families or reduce
   the advertised language claims before launch.
6. Keep CUDA out of this gate. NVIDIA packaging is L11.

Ledger rows MOD-01, MOD-02, and QUAL-01 are not promoted by this inventory.
