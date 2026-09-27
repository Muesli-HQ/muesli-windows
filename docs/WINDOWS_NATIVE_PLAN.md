# Windows Native Direction

The Windows app is a native WPF product with local dictation and meeting
workflows. It ships without Python, virtual environments, or a worker process.

## Production transcription platform

- `NativeTranscriptionClient`: a role-scoped ASR facade that owns at most one recognizer.
- `NativeParakeetClient` and `NativeOfflineAsrClient`: seven pinned offline choices across Parakeet, Whisper, SenseVoice, Qwen3-ASR, and Cohere.
- Dictation and final meeting/import roles persist independently. The optional live role is explicitly Off.
- Provider selection is automatic: CUDA first when the packaged provider can
  initialize, otherwise CPU. Both providers run the same model and engine.
- Downloads occur only after an explicit Prepare action, report progress, and verify both archive and required-file SHA-256 values before loading.
- Selecting or downloading never activates a recognizer. Role owners initialize only for transcription/warmup and dispose the old recognizer on safe switch.
- Model caches: `%USERPROFILE%\.cache\muesli\native-parakeet` and `%USERPROFILE%\.cache\muesli\native-asr`.

## Supporting native services

- NAudio WASAPI microphone and loopback capture.
- sherpa-onnx speaker diarization for recorded meetings.
- Optional LLamaSharp / llama.cpp GGUF transcript cleanup.
- Native runtime diagnostics and deterministic benchmark reporting.

## Launch rules

- No Python, venv, worker script, or external transcription setup.
- Do not display macOS-only CoreML or LiteRT artifacts as runnable Windows choices.
- Do not expose streaming choices until packaged Windows real inference passes.
- No silent cross-engine fallback.
- Provider, device, compute type, model load time, transcription wall time, RTF,
  and model-reuse status remain available in diagnostics and benchmark logs.
