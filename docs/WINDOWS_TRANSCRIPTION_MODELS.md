# Windows Transcription Model Platform

Status: offline roles implemented in Phase 1 and live role implemented in Phase 4. This is the current operational source of truth.

## Role ownership

| Role | Persisted setting | Consumers | Runtime ownership |
|---|---|---|---|
| Dictation | `DictationModelId` | shortcut dictation, onboarding dictation test | one role-scoped recognizer |
| Final meeting | `FinalMeetingModelId` | imported media, recorded meeting microphone and system audio | one shared role-scoped recognizer |
| Live preview | nullable `LiveMeetingModelId` | microphone/system streaming preview | Off by default; one session-scoped native online recognizer when explicitly selected |
| Final transcript | `LiveTranscriptOwnership` plus `FinalMeetingModelId` | saved recorded-meeting transcript | Preview-only: offline final model. Unified: live model. |
| Gap recovery | derived from ownership | measured queue/engine/interruption gaps | Unified only: configured offline final model transcribes bounded gap clips |

Selecting a role changes only the setting. It never downloads, verifies, initializes, or silently substitutes a model. Live downloads likewise never select or enable the model. An active meeting keeps its immutable model/ownership snapshot until it stops; native streams and VAD are then disposed.

## Supported offline catalog

The runnable Windows catalog contains twelve sherpa-onnx offline choices aligned with the Windows-compatible portion of the current macOS catalog:

- Parakeet v3 INT8
- Parakeet Unified English INT8
- Parakeet v2 English INT8
- Whisper Tiny Multilingual
- Whisper Tiny English
- Whisper Small Multilingual
- Whisper Small English
- Whisper Medium English
- Whisper Large Turbo Multilingual
- SenseVoice Small INT8
- Qwen3-ASR 0.6B INT8
- Cohere Transcribe INT8 (English selected)

macOS CoreML, WhisperKit, FluidAudio, and LiteRT identifiers are not Windows runtime choices. The public package ships the CPU sherpa-onnx provider only. An optional, SHA-256-verified NVIDIA CUDA acceleration pack can be downloaded from Models; it needs a user-supplied CUDA 12 / cuDNN 9 runtime and is only reported active after a real warm-up inference proves which provider executed the graph. CUDA-to-CPU provider fallback remains within the same sherpa-onnx engine and the same selected model; there is no cross-engine and no cross-model fallback. See `docs/L11_CUDA_PROVENANCE_GAP.md` and `docs/WINDOWS_GPU_QUALIFICATION.md`.

## Execution provider selection

`ExecutionProviderService` is the single policy point for dictation, recorded meeting
finalization, imported media, live Nemotron transcription, gap recovery, and diarization.

| Setting | Behavior |
|---|---|
| Automatic (default) | Attempts CUDA only for architectures where the shipping benchmark measured a meaningful warm-inference win (Whisper, Parakeet transducer), then falls back to CPU for the same model. Ties and regressions (Parakeet v3, SenseVoice, Cohere Transcribe, Qwen3-ASR, live Nemotron) stay on CPU. |
| CPU only | Always uses the packaged CPU runtime. |
| NVIDIA CUDA | Attempts CUDA for every role, including architectures Automatic keeps on CPU; CPU remains the last resort so the model still runs. |

DirectML is not offered: the pinned sherpa-onnx 1.13.4 Windows build has no DirectML
execution provider and a `directml` request silently runs on CPU. See the L11 document.

Switching between the CPU and CUDA native runtimes requires an app restart because both
cannot be loaded safely in one process. Models shows an explicit restart-required state
with a "Restart Muesli" action and never applies a provider change silently.

Provider and syntax details for diagnostics live in
`Muesli.Windows.CommandHost --acceleration-status`; measurements live in
`qualification/gpu-qualification/`.

## Lifecycle and integrity

Each catalog row has independent Prepare, Cancel, Retry, Verify, Delete, installed disk size, status, and diagnostics actions. Status distinguishes Missing, Downloading, Verifying, Ready, Selected, Failed, Runtime unavailable, and Deletion failed.

Preparation is explicit and network-backed. It runs outside the WPF dispatcher, uses a per-model in-process operation guard plus a cross-process file lock, removes abandoned download/staging artifacts, extracts into a unique staging directory, and moves the complete verified directory into place. Prepare all runs sequentially and never initializes a recognizer.

Every archive URL and archive SHA-256 is pinned in `TranscriptionModelCatalog`. Every ONNX/token/tokenizer file consumed by a recognizer also has a pinned SHA-256. A verification stamp may avoid re-hashing only while the pinned hash, file size, and last-write timestamp all still match. An interrupted or invalid cache is reverified or replaced; it is never treated as ready from filenames alone.

Deletion first waits for dictation/final role inference and releases matching recognizers, then serializes against preparation and deletes only that model directory. A locked or unauthorized directory produces Deletion failed and remains retryable.

The separate live catalog currently contains only multilingual Nemotron 3.5 Streaming 560 ms INT8 plus the pinned Silero VAD artifact. Both executed successfully through the packaged Windows sherpa-onnx DLLs. macOS CoreML Parakeet Realtime EOU is not presented as a Windows choice; the artifact blocker is recorded in `docs/WINDOWS_MACOS_MODEL_PARITY.md`.

## Local cleanup models

The Cleanup category of the Models page manages three pinned GGUF models with guided
download, SHA-256 verification, prepare/cancel/retry/verify/delete, installed size, status,
and selection without automatic activation. S1-mini keeps its fixed control-line prompt;
the other two use Muesli's configurable cleanup instructions. A `*.gguf` that a user
placed in `%USERPROFILE%\.cache\muesli\native-cleanup` by hand keeps working until a
catalog model is explicitly selected. Windows cleanup runs on the LLamaSharp CPU backend;
the GPU-offload path is not offered because the measured cleanup workloads are small and
the CUDA llama.cpp backend is not part of the pinned package.

## Settings migration

Settings schema 4 retains the independent dictation/final fields and adds the nullable live choice, explicit ownership, and waveform-hover preference. Existing users migrate with Live Off. Session-journal schema 2 and completed-meeting schema 3 retain live-preview, final-transcript, and gap-recovery provenance after final persistence. Sanitized persistence removes legacy keys, and invalid runtime IDs normalize without fallback.

## Qualification commands

The headless benchmark accepts `--model <catalog-id>` and requires that model to already be prepared and verified:

```powershell
Muesli.exe --benchmark-native --audio <real-audio.wav> --runs 1 --model <catalog-id> --output <report.json>
```

This command never downloads a model. `scripts/smoke-transcription-models.ps1` invokes it for all twelve choices and fails if any report lacks successful real-audio inference.
