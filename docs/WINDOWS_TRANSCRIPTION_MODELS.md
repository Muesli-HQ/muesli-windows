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

macOS CoreML, WhisperKit, FluidAudio, and LiteRT identifiers are not Windows runtime choices. The public Wave 0 package ships the CPU sherpa-onnx provider only. CUDA-to-CPU provider fallback remains within the same sherpa-onnx engine and selected model when an externally staged matching CUDA bundle is present; there is no cross-engine fallback. NVIDIA packaging is L11 and is not a public-package claim.

## Lifecycle and integrity

Each catalog row has independent Prepare, Cancel, Retry, Verify, Delete, installed disk size, status, and diagnostics actions. Status distinguishes Missing, Downloading, Verifying, Ready, Selected, Failed, Runtime unavailable, and Deletion failed.

Preparation is explicit and network-backed. It runs outside the WPF dispatcher, uses a per-model in-process operation guard plus a cross-process file lock, removes abandoned download/staging artifacts, extracts into a unique staging directory, and moves the complete verified directory into place. Prepare all runs sequentially and never initializes a recognizer.

Every archive URL and archive SHA-256 is pinned in `TranscriptionModelCatalog`. Every ONNX/token/tokenizer file consumed by a recognizer also has a pinned SHA-256. A verification stamp may avoid re-hashing only while the pinned hash, file size, and last-write timestamp all still match. An interrupted or invalid cache is reverified or replaced; it is never treated as ready from filenames alone.

Deletion first waits for dictation/final role inference and releases matching recognizers, then serializes against preparation and deletes only that model directory. A locked or unauthorized directory produces Deletion failed and remains retryable.

The separate live catalog currently contains only multilingual Nemotron 3.5 Streaming 560 ms INT8 plus the pinned Silero VAD artifact. Both executed successfully through the packaged Windows sherpa-onnx DLLs. macOS CoreML Parakeet Realtime EOU is not presented as a Windows choice.

## Settings migration

Settings schema 4 retains the independent dictation/final fields and adds the nullable live choice, explicit ownership, and waveform-hover preference. Existing users migrate with Live Off. Session-journal schema 2 and completed-meeting schema 3 retain live-preview, final-transcript, and gap-recovery provenance after final persistence. Sanitized persistence removes legacy keys, and invalid runtime IDs normalize without fallback.

## Qualification commands

The headless benchmark accepts `--model <catalog-id>` and requires that model to already be prepared and verified:

```powershell
Muesli.exe --benchmark-native --audio <real-audio.wav> --runs 1 --model <catalog-id> --output <report.json>
```

This command never downloads a model. `scripts/smoke-transcription-models.ps1` invokes it for all twelve choices and fails if any report lacks successful real-audio inference.
