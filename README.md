# Muesli Windows

Native Windows app for Muesli: local-first dictation, meeting recording, transcription, notes, and searchable history.

## Privacy

Muesli is built around local-only processing. Concretely:

- **Transcription is on-device.** Native sherpa-onnx offline models run in-process. Your audio is never uploaded.
- **Microphone is only active during dictation or meeting recording.** While you hold the dictation hotkey, or while a meeting recording is running, the WASAPI capture stream is open. At every other moment it is closed.
- **System audio loopback is opt-in per meeting.** Loopback (the "other side" of a Zoom / Teams / Meet call) only starts after you explicitly click **Join & Record** on the meeting-detection toast. No silent background capture.
- **The global keyboard hook only inspects key codes.** Muesli installs a `WH_KEYBOARD_LL` hook so the dictation hotkey works in any app. The hook checks whether the pressed key matches your configured shortcut and nothing else — no keystroke content is logged, transmitted, or stored anywhere.
- **Crash reporting is opt-in and off by default.** If you toggle it on (during onboarding or in About → Privacy), stack traces and the app version are sent to Sentry; transcripts, recordings, file paths, and your Windows username are scrubbed before send.
- **Local storage paths.** Settings and history live in `%APPDATA%\muesli\`. Model weights cache to `%USERPROFILE%\.cache\muesli\`. Captured audio files live in `%APPDATA%\muesli\captures\`. Nothing leaves these locations unless you ask it to.
- **Cloud meeting summaries require your own keys.** OpenAI / OpenRouter summary providers are only used when you enter your own API key in Settings. The default summary provider runs locally.

## Features

- **Dictation**: hold the global shortcut, speak, release, and paste into the active app.
- **Native transcription**: seven pinned offline sherpa-onnx models cover Parakeet, Whisper, SenseVoice, Qwen3-ASR, and Cohere; dictation and final meeting/import roles are selected independently.
- **Native cleanup**: optional local Qwen/GGUF cleanup through LLamaSharp after transcription.
- **Meeting recording**: captures separate microphone and meeting-audio tracks with an explicit lifecycle, route repair, suspend/crash recovery, and retained-audio playback. Detected meetings attempt Windows process-tree capture and visibly disclose endpoint-loopback fallback.
- **Native speaker labels**: sherpa-onnx diarization labels recorded meeting system audio when models are cached.
- **Meeting notes**: local summaries by default, with OpenAI or OpenRouter available when you add keys.
- **History and search**: dictations, meetings, folders, filters, custom dictionary, and export.

## Install

The shipping Windows shell is the packaged WinUI 3 app. Signed public installation is still a
release gate; local development launches use the packaged WinApp path below.

The Python runtime has been removed. No Python, venv, or external transcription worker runtime is required. Model preparation is an explicit action in Models; selecting a role never starts a download or silently changes engines.

## Build

```powershell
dotnet build .\windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64
```

## Run

```powershell
.\scripts\run-windows.ps1
```

With no profile override, this launches against the real `%APPDATA%\muesli` library. Use
`scripts/run-winui-preview.ps1` only when an isolated throwaway profile is intentional.

## Package

```powershell
.\scripts\package-windows-v1.ps1
```

Recorded-meeting and import regression checks are available through
`scripts\benchmark-native-meeting.ps1` and `scripts\test-media-imports.ps1`.
The fail-closed physical meeting-session matrix is documented in
`docs\PHASE3_QUALIFICATION.md` and checked by
`scripts\qualify-meeting-session-lifecycle.ps1`.
The packaged release gate is `scripts\qualify-windows-release.ps1`; it produces
machine-readable native-runtime, stress, memory, package-hash, signature, and
fresh-launch log evidence.

## Architecture

- **UI**: WinUI 3 on .NET 10, XAML, Inter font, packaged with MSIX.
- **Audio**: NAudio WASAPI microphone capture, Windows process-tree loopback when available for a detected meeting, and an explicitly disclosed render-endpoint loopback fallback.
- **ASR**: role-scoped `NativeTranscriptionClient` instances with sherpa-onnx offline ONNX models. Dictation has one recognizer owner; recorded meetings and imports share the separately selected final-model owner.
- **Cleanup**: `NativeTextCleanupService` with LLamaSharp / llama.cpp GGUF models.
- **Diarization**: `NativeDiarizationClient` with sherpa-onnx ONNX models.
- **Model caches**:
  - `%USERPROFILE%\.cache\muesli\native-parakeet`
  - `%USERPROFILE%\.cache\muesli\native-asr`
  - `%USERPROFILE%\.cache\muesli\native-diarization`
  - `%USERPROFILE%\.cache\muesli\native-cleanup`
- **App data**: `%APPDATA%\muesli`

## Development Notes

- Active app: `windows-native/Muesli.Windows.WinUI/`
- WPF indicator companion: `windows-native/Muesli.Windows.Indicator.Wpf/` (the retired WPF product shell was removed)
- Build before launch: `dotnet build windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64`
- Launch through the packaged Windows App SDK path: `scripts/run-windows.ps1`
- Do not launch the WinUI executable directly; packaged identity is part of the product contract.
- Do not ship Python worker files, external transcription runtime setup files, or venv setup.

## Limitations

- Supported ASR families use the **packaged CPU provider** by default. The public package **does not claim NVIDIA acceleration** and includes no NVIDIA or DirectML binaries. NVIDIA CUDA is available as an optional, SHA-256-verified acceleration pack downloaded from the pinned sherpa-onnx 1.13.4 release into `%LOCALAPPDATA%\muesli`; it needs a user-supplied CUDA 12 / cuDNN 9 runtime and is only reported active after a real warm-up inference proves it. DirectML is not offered because the pinned sherpa-onnx Windows build has no DirectML execution provider and silently falls back to CPU.
- Local cleanup models are managed from Models → Cleanup with explicit download, SHA-256 verification, retry, cancel, and delete. Qwen cleanup stays disabled in Settings until a model is installed and selected; placing a GGUF manually in the cache still works for existing installs.
- Process-targeted loopback requires Windows build 20348 or newer and a live detected-process ID. When Windows blocks it, Muesli visibly falls back to render-endpoint loopback, which can include unrelated system sounds; mic recording can continue in a disclosed degraded state.
- Preparing a missing model requires an explicit network-backed download. Transcription itself fails closed when the selected role is missing or unverified.
- Live meeting transcription is off by default. The explicit Nemotron 3.5 option has passed packaged Windows real-inference and Silero-boundary qualification; preparing it is network-backed and never enables it automatically. Long physical-meeting, Bluetooth, route-change, CUDA-live, and human multilingual qualification remain open.
- Installer is not code-signed until a signing certificate is configured.

## Release status (0.3.0 beta)

- Updates are manual: Muesli does not check for, download, or install updates automatically.
- Muesli does not pause or duck unrelated media while recording ("no interference").
- PDF export is disabled pending a QuestPDF Community-license eligibility decision; Markdown export remains available and PDF must not be advertised.
- Public support/privacy URLs and the production publisher identity are not final; the package is unsigned and carries the development publisher until a signing identity is configured.

## Roadmap

See [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Acknowledgements

- NVIDIA Parakeet and Qwen3-ASR
- OpenAI Whisper, SenseVoice, and Cohere Transcribe model artifacts
- sherpa-onnx
- NAudio
- Inter font family

## Contributing

Contributions welcome! Priority areas:
- Visual polish (match OG macOS design system)
- Meeting detection reliability
- GPU path testing on diverse NVIDIA hardware
- Code signing once a certificate is available

Open an issue or PR at [github.com/Muesli-HQ/Muesli-Windows](https://github.com/Muesli-HQ/Muesli-Windows).

## License

MIT License — free for personal and commercial use.
