# Windows versus macOS model parity

This document records, model by model, what Windows can honestly do. Nothing here is a
placeholder: a model appears as Windows-supported only when the Windows artifact is
pinned, downloadable, SHA-256 verified, and consumed by a real runtime.

Source of truth for macOS: `native/MuesliNative/Sources/MuesliNativeApp/Models.swift`
(`BackendOption`, `PostProcessorOption`, `MeetingLiveCaptionBackend`) and the macOS model
stores. Source of truth for Windows: `TranscriptionModelCatalog`,
`StreamingModelPlatform`, `CleanupModelCatalog`, and `ModelLanguageSupport`.

## Classification summary

| macOS option | Windows status | Evidence |
|---|---|---|
| Apple Speech (`apple-speech-transcriber`) | **Impossible — Apple system service** | `SpeechAnalyzer` / `SpeechTranscriber` / `AssetInventory` are macOS 26-only frameworks. No Windows equivalent provides long-form on-device dictation with comparable accuracy; see "Apple Speech" below. |
| Parakeet Unified / v3 / v2 | **Supported** | Same sherpa-onnx INT8 ONNX artifacts, now with measured provider selection. |
| Whisper Tiny / Small / Medium / Large Turbo | **Supported** | Same sherpa-onnx INT8 ONNX artifacts. |
| SenseVoice Small | **Supported** | Same sherpa-onnx INT8 artifact. |
| Qwen3-ASR 0.6B | **Supported** | Same sherpa-onnx INT8 artifact. |
| Cohere Transcribe | **Supported** | Same sherpa-onnx INT8 artifact. |
| Nemotron 3.5 Streaming | **Supported** | sherpa-onnx 0.6B 560 ms streaming transducer with Silero VAD; live and unified ownership both implemented. |
| Parakeet Realtime EOU (320 ms) | **Blocked — no qualified Windows artifact** | See "Parakeet Realtime EOU" below. Nemotron remains the live option; there is no EOU card. |
| Bodhan Core / Flex | **Blocked — CoreML + MLX only** | See "Bodhan" below. No ONNX/GGUF release exists for the encoder/decoder pair. |
| Gemma 4 E2B / E4B | **Blocked — no official Windows runtime path shipped** | See "Gemma 4" below. No card is shown. |
| Muesli Cleanup (qwen35-postproc-v3), S1-mini, Qwen Basic Cleanup | **Supported** | Identical GGUF artifacts with pinned URL/SHA-256 and a managed download/verify/delete lifecycle. |
| Qwen3.5 (Quil/summary local models) | **Not offered** | Quil and hosted summary providers are macOS-side product features; Windows ships summaries through its own provider settings. |
| LocalVQE AEC, diarization | **Supported (runtime-matched)** | sherpa-onnx pyannote segmentation + TitaNet embeddings; Silero VAD. Not the same CoreML models, but the same product capability. |

## Apple Speech

Apple Speech cannot be ported. The macOS implementation is the macOS 26
`SpeechAnalyzer`/`SpeechTranscriber` stack with `AssetInventory` locale reservation;
those APIs do not exist outside Apple platforms and there is no redistributable artifact.

The closest Windows system-managed option is `Windows.Media.SpeechRecognition`, whose
recognizer is grammar/list-constraint oriented, needs a per-language system speech pack,
offers no equivalent of progressive time-indexed transcription, and does not expose a
comparable on-device locale asset lifecycle. Shipping it as "Apple Speech parity" would be
a new, materially worse feature with unclear privacy guarantees, so Windows deliberately
keeps its local sherpa-onnx models instead. This is a platform difference, not a gap that
fake UI can close.

## Parakeet Realtime EOU

macOS runs `FluidInference/parakeet-realtime-eou-120m-coreml/320ms` through FluidAudio's
`StreamingEouAsrManager`, preview-only, while a separate meeting model owns the final
transcript.

Windows requirements for a faithful port would be a legally distributable streaming ONNX
export that the sherpa-onnx `OnlineRecognizer` can consume as a cache-aware streaming
transducer, with a tokenizer and endpointing behavior matching the 320 ms chunking.

What was checked:

- The pinned sherpa-onnx 1.13.4 release asset list contains no EOU/parakeet streaming
  transducer model (`k2-fsa/sherpa-onnx` `asr-models` release); the only NVIDIA streaming
  transducer published there is Nemotron.
- FluidAudio's CoreML `.mlmodelc` packages are not convertible to ONNX without the
  original training checkpoint and a re-export, which is not published.
- `nvidia/parakeet_realtime_eou_120m` publishes NeMo checkpoints, not an ONNX runtime
  package with a pinned hash for Windows.

Result: **no qualified Windows artifact exists today.** Nemotron 3.5 Streaming remains the
live model and already implements both ownership modes
(`preview-only` with a separate final model, and `unified-live-final`). No EOU card,
setting, or runtime is exposed. If NVIDIA or the community publishes a pinned streaming
ONNX export that sherpa-onnx can load, it can be added as a second
`StreamingModelCatalog` entry without changing the session design.

## Bodhan Core and Flex

macOS runs a CoreML encoder plus an MLX Swift autoregressive decoder, with a
`frontend.bin` feature frontend and a 7152-entry tokenizer. Core supports 25 Indic
languages, Flex 27 including mixed-script and spoken numbers.

What was checked:

- The published `phequals/indic-transcribe-*` repositories contain CoreML `.mlpackage`
  bundles, MLX `.safetensors` decoders, and native frontend/tokenizer assets. There is no
  ONNX, GGUF, or other Windows-loadable release.
- A faithful conversion would need: an ONNX export of the encoder and decoder with a
  pinned source revision, a reimplementation of the `frontend.bin` feature pipeline, the
  tokenizer vocabulary, and numerical parity checks per language — plus licensing review
  of the converted artifacts.

Result: **blocked.** Windows does not substitute a different Indic model and call it
Bodhan. If an official ONNX or GGUF release appears, it can be added with reproducible
conversion scripts and per-language transcription tests.

## Gemma 4 E2B / E4B

macOS runs `litert-community/gemma-4-*-it-litert-lm` through Google's LiteRT-LM C API
(Metal GPU decoder, CPU audio encoder), experimental, and never as the recommended ASR
model. The same Gemma model may not be active for transcription and cleanup at once.

What was checked:

- The macOS package consumes a macOS-only `CLiteRTLM_mac.xcframework` from the pinned
  LiteRT-LM release; the pinned file is 2.6 GB and is fetched from Hugging Face.
- No Windows LiteRT-LM binary or NuGet package is used by this repository, and no
  ONNX/GGUF conversion of the multimodal Gemma 4 audio path exists with a reproducible
  license and quality story.

Result: **blocked and not shown.** There is no Gemma card, no Gemma setting, and no
success message. The mutual-exclusion rule is therefore moot on Windows: Gemma is not
selectable for either role. If Google publishes a Windows LiteRT-LM runtime and the
artifact can be hash-pinned, this can be revisited with the experimental label and the
same "not simultaneously transcription and cleanup" restriction intact.

## Cleanup parity

Windows now mirrors the macOS cleanup catalog with managed preparation:

| Windows cleanup model | macOS equivalent | Artifact |
|---|---|---|
| Muesli Cleanup | `qwen35-postproc-v3` (default) | `phequals/qwen35-postproc-v3-gguf` `qwen35-postproc-v3-Q4_K_M.gguf` (529 296 768 B, SHA-256 `DD44D9F8…`) |
| S1-mini by Superwhisper | `superwhisper-s1-mini` | `superwhisper/s1-mini-GGUF` `s1-mini-q4_k_m.gguf` (484 219 808 B, SHA-256 `3B41EBE2…`) |
| Qwen Basic Cleanup | `qwen35-0.8b` | `unsloth/Qwen3.5-0.8B-GGUF` `Qwen3.5-0.8B-Q4_K_M.gguf` (532 517 120 B, SHA-256 `BD258782…`) |

Each supports guided download, pinned URL and SHA-256, prepare/cancel/retry/verify/delete,
installed size and status, and selection without automatic activation. S1-mini keeps its
fixed `[Styling: semi-formal] [Structure: prose] [Context: general]` control line instead
of Muesli's configurable instructions, exactly like macOS. A GGUF that an existing user
placed in the cache by hand keeps working until they pick a catalog model.

Windows runs these through LLamaSharp/llama.cpp on CPU. A local `*.gguf` that a user
places in the cache root remains a supported legacy path.
