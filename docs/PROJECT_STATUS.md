# Muesli Windows Project Status

Last verified: 2026-09-23

## Current Product

Muesli Windows is a native WPF/.NET 8 application in `windows-native/Muesli.Windows/`. It uses a persistent Python worker for local Whisper transcription and optional Qwen, Parakeet, and pyannote capabilities. Packaged builds bundle CPython 3.12; contributors can use the repository worker environment.

The shipping architecture remains WPF + Python worker + Velopack. Experimental native-ASR branches are not part of `main` and should not be merged wholesale without a separate architecture decision.

## Implemented Baseline

- Hold-to-talk and double-tap dictation with active-app paste, clipboard mode, and history-only voice notes.
- Whisper model selection, language hints, separate dictation/meeting microphones and models, optional Parakeet, and local Qwen cleanup.
- Deterministic filler-word removal and portable dictionary import/export.
- Atomic, versioned JSON persistence with backups and corrupt-file recovery.
- API-key protection through the Windows secret store; plaintext settings keys are migrated and removed.
- Editable meeting titles, transcripts, generated notes, and manual notes.
- Local/OpenAI/OpenRouter/Ollama/LM Studio/custom-compatible summary providers.
- Markdown/PDF export, recording retention policy, automatic export, playback, and post-meeting hooks.
- Single-instance development/desktop behavior, startup registration, tray operation, and Velopack updates.

## Meeting Reliability State

- Meeting prompts require stable live-meeting evidence; an idle Zoom home window is not treated as a meeting.
- Ending a detected meeting uses a visible user confirmation instead of silently stopping a recording.
- Remote audio first attempts meeting-process capture. If that stream remains digitally silent during qualification, Muesli visibly falls back to all-system loopback.
- Microphone device changes trigger bounded recovery. Recovery legs retain their wall-clock offsets, with silence inserted for the outage so microphone and remote transcript timestamps stay aligned.
- Long WAV files are transcribed in five-minute chunks. Capture files rotate below the WAV size limit.
- Active recordings are journaled and processing records are saved before transcription so interruption/failure is visible and recoverable.
- Successful meetings delete temporary audio after the transcript and compact timed text segments are durably saved. Partial/failed meetings retain the named recovery audio.
- Microphone and remote transcript segments are always merged chronologically. If pyannote is unavailable, the transcript uses truthful `You` and `System audio` labels instead of grouping each whole track.
- Filler removal preserves transcript line breaks and timestamps.
- Each meeting records speaker-identification status (`identified`, `fallback`, or `not-applicable`) and exposes the state in meeting details.

## Optional Speaker Identification

Chronological transcripts do not require diarization. Individual remote-speaker labels do.

```powershell
.\scripts\setup-worker-runtime.ps1 -WithDiarization
```

pyannote model access may also require `HF_TOKEN` and acceptance of the model's Hugging Face access terms. Missing dependencies or access now produce an explicit meeting warning while preserving the chronological fallback transcript.

## Development Workflow

Use one canonical development instance from the repository:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-development.ps1
```

The launcher refuses to start while another `Muesli.exe` is running. Quit Muesli from its tray menu before rebuilding or launching another development copy.

Direct verification:

```powershell
Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\windows-native\Muesli.Windows\Muesli.Windows.csproj --no-restore
dotnet test .\windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore
```

Current automated baseline: 38 passing tests, zero build warnings. Manual Zoom/Teams/Meet/Webex, Bluetooth/device-change, packaging, and updater qualification are still required before a public release.

## Data and Privacy

- App data: `%APPDATA%\muesli\`
- Settings: `%APPDATA%\muesli\windows-settings.json`
- Versioned history: `%APPDATA%\muesli\data\`
- Logs: `%APPDATA%\muesli\logs\`
- Model cache: `%USERPROFILE%\.cache\muesli\` unless overridden
- Temporary captures: `%APPDATA%\muesli\captures\`

Audio transcription is local. Cloud summary providers are used only when selected and configured. Successful meeting audio is removed after transcript metadata has been saved.

## Remaining Priority Risks

- Repetition/hallucination suppression for low-information microphone sections is not implemented. This was intentionally left out of the 2026-09-23 transcript-ordering patch.
- Process-loopback and fallback behavior needs broader physical testing across Zoom, Teams, Meet, Webex, Bluetooth headsets, CPU-only PCs, and supported NVIDIA systems.
- Optional pyannote and Parakeet dependency installation remains large and environment-sensitive.
- The application and installer are not Authenticode-signed.
- Existing meetings created before timed segments were persisted cannot be fully reordered after their audio has been deleted.
- SQLite, cross-device sync, calendar integration, Computer Use, and full streaming/live-caption parity remain future work.
