# Windows Native Direction

## Architecture Decision

The active Windows product is a native WPF/.NET 8 application. It uses a persistent Python worker over newline-delimited JSON for local ASR and packages through Velopack with bundled CPython 3.12.

- `Muesli.Windows`: native WPF shell and product UI.
- `DictationCoordinator`: shared hold/double-tap dictation state machine.
- `AudioCaptureService`: WASAPI microphone capture with route recovery and timeline-preserving gaps.
- `SystemAudioCaptureService`: meeting-process capture with qualified all-system fallback.
- `MeetingRecordingCoordinator`: capture finalization, chunked transcription, diarization attempt, chronological reconciliation, warnings, and cleanup metadata.
- `TranscriptionWorkerClient`: persistent worker bridge for Whisper, optional Parakeet/Qwen, model lifecycle, and diarization.
- `AppDataStore` / `SettingsStore`: atomic versioned JSON persistence and protected secrets under `%APPDATA%\muesli`.

WPF + Python worker + Velopack is the shipping architecture. A native inference rewrite remains a future decision, not an assumed migration.

## Product Rules

- Never fabricate a transcript or mark a failed operation as successful.
- Preserve local transcripts when optional providers or diarization fail.
- Keep microphone and remote transcript segments on a shared wall-clock timeline.
- Save processing/failure state before destructive cleanup.
- Delete successful temporary meeting audio only after transcript and timed segments are durably saved.
- Retain audio for partial/failed meetings when it is needed for recovery.
- Do not silently widen meeting-process capture to all-system audio; qualify it and notify the user.
- Audio remains local and is excluded from any future sync payload.

## Development and Verification

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-development.ps1
```

The launcher is the canonical development entry point and refuses duplicate Muesli processes.

```powershell
Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\windows-native\Muesli.Windows\Muesli.Windows.csproj --no-restore
dotnet test .\windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore
```

Build/test success is necessary but not sufficient. Meeting/audio changes require real-app and physical-device verification. The dated status and remaining risks live in [`PROJECT_STATUS.md`](PROJECT_STATUS.md).
