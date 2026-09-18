# Muesli Windows Native

This is the active Windows-native app path. It has no Python worker, virtual
environment, or embedded Python runtime.

## Transcription

Muesli exposes seven pinned offline sherpa-onnx choices: Parakeet v3, Whisper
Tiny/Small/Medium English, SenseVoice Small INT8, Qwen3-ASR 0.6B INT8, and
Cohere Transcribe INT8. Dictation and final meeting/import transcription have
separate persisted roles. Live meeting transcription is Off by default and can
explicitly use the packaged-runtime-qualified multilingual Nemotron 3.5 560 ms
INT8 model with native Silero VAD.

Selecting a role never downloads or activates a model. Models are prepared
explicitly, and both the archive and every runtime-required file are checked
against pinned SHA-256 hashes before recognition. Downloads, extraction, and
verification run away from the WinUI dispatcher. Dictation owns one warm
recognizer; recorded meetings and imports share another. Switching a role waits
for active inference and disposes the previous recognizer deterministically.

## Stack

- Packaged WinUI 3 / .NET 10 x64 desktop app
- Native `RegisterHotKey` global shortcut handling
- NAudio WASAPI microphone capture plus process-tree meeting loopback where Windows supports it, with an explicit render-endpoint fallback
- Durable meeting-session state/journal recovery and in-app retained-track playback
- sherpa-onnx offline ASR for five model families and seven catalog choices
- sherpa-onnx online Nemotron 3.5 plus native Silero VAD for opt-in live meetings
- sherpa-onnx native speaker diarization for recorded meetings
- LLamaSharp / llama.cpp optional GGUF transcript cleanup
- Persistent data and settings under `%APPDATA%\muesli`

## Build

```powershell
dotnet build .\windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64
```

## Run

```powershell
.\scripts\run-windows.ps1
```

This uses the supported packaged launch path and opens the production `%APPDATA%\muesli` profile.
The retired WPF source is retained at `windows-native/Muesli.Windows.Wpf.Legacy/` for reference only.

## Deterministic benchmark

```powershell
.\scripts\benchmark-native-transcription.ps1 `
  -AudioPath .\testdata\dictation.wav `
  -ReferencePath .\testdata\dictation.txt `
  -Runs 5
```

The JSON report records audio and transcript hashes, repeated-output
determinism, model load and transcription wall time, real-time factor, provider,
device, compute type, model reuse, timestamps, and optional WER/CER.
