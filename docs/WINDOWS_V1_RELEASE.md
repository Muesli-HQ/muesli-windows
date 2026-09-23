# Windows v1 Release Checklist

## Build Artifact

Create the downloadable zip and Velopack installer artifacts:

```powershell
.\scripts\package-windows-v1.ps1
```

Output:

```text
artifacts\muesli-windows-v1-win-x64.zip
artifacts\velopack\Muesli-win-Setup.exe
artifacts\velopack\*.nupkg
artifacts\velopack\releases.win.json
```

## What Works

- Native Windows WPF shell.
- Hold `F8` to dictate.
- Native WASAPI microphone capture.
- Local Whisper transcription through the bundled worker script.
- Optional Qwen post-processing toggle for grammar cleanup, punctuation, casing, and dictated list formatting.
- Auto-paste into the previously focused app.
- Clipboard fallback behavior.
- Microphone/model/paste settings persisted locally.
- Persistent dictation history with manual copy.
- Persistent dictation history with delete controls.
- Persistent dictionary replacements applied after dictation and meeting transcription.
- Meeting media import/transcription with persistent meeting list.
- Live meeting recording with microphone capture and meeting-process audio, plus a visible qualified all-system fallback.
- Timeline-preserving microphone recovery after device changes.
- Chronological `You` / remote transcript reconciliation even when optional diarization is unavailable.
- Persisted timed transcript segments, processing journals, truthful partial/failed records, and successful-audio cleanup.
- Automatic meeting detection prompt for active Google Meet, Zoom, Teams, and Webex windows.
- Meeting summaries stored with each meeting transcript.
- Summary providers: local fallback, OpenAI API key, or OpenRouter API key.
- Meeting copy, delete, and open-audio controls.
- Real words-dictated and average-WPM stats based on captured dictation duration.
- System tray icon with reopen/quit menu.
- Installer and differential update support through Velopack.
- Sidebar light/dark theme toggle with persisted theme setting.

## Current v1 Runtime Requirements

- Windows x64.
- Packaged installs include CPython 3.12 and base Whisper dependencies; end users do not need system Python.
- Source contributors can prepare the canonical worker runtime with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-worker-runtime.ps1
```

Optional transcript cleanup dependencies:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-worker-runtime.ps1 -WithPostProcessing
powershell -ExecutionPolicy Bypass -File .\scripts\setup-worker-runtime.ps1 -WithParakeet
powershell -ExecutionPolicy Bypass -File .\scripts\setup-worker-runtime.ps1 -WithDiarization
```

Qwen model downloads are opt-in. Set `MUESLI_ALLOW_MODEL_DOWNLOAD=1` for the first post-processor model download, or place the configured model in `%USERPROFILE%\.cache\muesli`. The default post-processor model is `Qwen/Qwen2.5-3B-Instruct`; override it with `MUESLI_POST_PROCESSOR_MODEL`.

Optional meeting summary provider keys can be entered in Settings, or supplied through environment variables:

```powershell
$env:OPENAI_API_KEY="..."
$env:OPENROUTER_API_KEY="..."
```

## Known v1 Limits

- The app is not code-signed yet, so Windows SmartScreen may warn on first launch.
- ChatGPT account sign-in summary backend is not implemented yet; OpenAI/OpenRouter API-key backends are implemented.
- Qwen post-processing uses a fallback cleanup when the optional model/dependencies are not installed.
- Automatic meeting detection is heuristic in v1: it uses active app titles, supported browser URLs where accessible, and meeting app process names.
- Meeting transcripts stay chronological with `You` / `System audio` fallback labels; individual remote-speaker labels require optional pyannote dependencies and model access.
- Meeting-process capture can fall back to all-system audio on unsupported/silent paths, which may include unrelated computer sounds.
- Parakeet is selectable as an optional backend when its runtime dependencies are installed.
- Conservative filtering for rare Whisper repetition loops is not implemented yet.

## Clean Install QA

Use a clean Windows user profile or a disposable folder under `%TEMP%`:

```powershell
Expand-Archive .\artifacts\muesli-windows-v1-win-x64.zip -DestinationPath $env:TEMP\muesli-v1 -Force
cd $env:TEMP\muesli-v1
powershell -ExecutionPolicy Bypass -File .\setup-worker-runtime.ps1
.\Muesli.exe
```

Automated package smoke test:

```powershell
.\scripts\test-windows-package.ps1
```

Pass criteria:

- App opens without crashing.
- Models > First-run Setup shows worker dependencies ready and worker found.
- Download selected Whisper model succeeds for `base`.
- Test mic moves from checking to a real signal or a clear device error.
- Hold-to-dictate creates a row in Dictations.
- Active-app paste works in Notepad; if it fails, transcript remains in Dictations and logs capture the failure.
- About > Open Logs opens `%APPDATA%\muesli\logs`.
- Settings > Start Muesli when I sign in creates/removes the HKCU startup entry.
- Meeting detection prompt appears when opening a supported Google Meet/Zoom/Teams/Webex foreground window.
- Import media creates a Meeting row and allows manual copy/open-audio.

## Signing

Before public distribution, sign `Muesli.exe` and the packaged scripts/artifact if possible:

```powershell
.\scripts\sign-windows-release.ps1
```

Unsigned builds are usable for internal testing, but they will have materially worse download/install trust.
