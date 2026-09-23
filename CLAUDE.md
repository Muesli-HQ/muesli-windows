# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Muesli for Windows — a local-first dictation and meeting transcription app. The active product is a native **WPF / .NET 8** shell (`windows-native/Muesli.Windows/`) that drives a **Python worker** (`worker/transcribe_worker.py`) for ASR. End users install via a Velopack-built `Muesli-win-Setup.exe`; the installer bundles its own CPython 3.12 so users do not need a system Python. Any Electron/web files (`src/`, `electron/`, `shared/`, `package.json`, `vite.config.ts`, etc.) on disk are legacy prototype reference and are excluded from git via `.gitignore` — do not edit them.

## Common commands (PowerShell)

The shell here is bash-on-Windows (use forward slashes, `/dev/null`, etc.), but the project's build/package scripts are PowerShell. Run them from a PowerShell terminal or via `powershell -ExecutionPolicy Bypass -File ...`.

```powershell
# Build (Debug)
dotnet build .\windows-native\Muesli.Windows\Muesli.Windows.csproj

# Run the canonical development copy (refuses duplicate Muesli processes)
powershell -ExecutionPolicy Bypass -File .\scripts\run-development.ps1

# Install/refresh the Python worker .venv (contributor path; requires Python 3.11 or 3.12)
.\scripts\setup-worker-runtime.ps1
.\scripts\setup-worker-runtime.ps1 -WithPostProcessing   # Qwen cleanup deps
.\scripts\setup-worker-runtime.ps1 -WithParakeet         # NVIDIA Parakeet deps
.\scripts\setup-worker-runtime.ps1 -WithDiarization      # pyannote deps
.\scripts\setup-worker-runtime.ps1 -CheckOnly            # verify python without installing

# Package: produces both the zip AND a Velopack Setup.exe + delta .nupkg
.\scripts\package-windows-v1.ps1
# Smoke-test the produced zip
.\scripts\test-windows-package.ps1
```

Release builds are produced by `.github/workflows/release-package.yml` (manual `workflow_dispatch`). It calls `package-windows-v1.ps1` with `SENTRY_DSN` from `secrets.SENTRY_DSN` so the embedded Sentry DSN ships only in CI builds.

### Build workflow gotcha

`Muesli.exe` locks its own files while running. **Kill any running instance before rebuilding** — `package-windows-v1.ps1` and `test-windows-package.ps1` do this automatically; `dotnet build` does not:

```powershell
Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
```

Built binaries live at `windows-native/Muesli.Windows/bin/Debug/net8.0-windows/Muesli.exe` (or `Release/`).

### Tests

The Windows xUnit project covers persistence invariants, transcript formatting, meeting detection/safety policies, audio rotation/timeline normalization, and cleanup behavior:

```powershell
dotnet test .\windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore
```

The current baseline is 38 passing tests. `scripts\test-windows-package.ps1` remains the post-package smoke test. Verify UI, real meeting apps, and physical audio-device behavior manually after changes — build/test success is not sufficient.

## Architecture

### Process model

```
Muesli.exe (WPF, .NET 8)  ──┐
                            │  spawn once
                            ▼
                  python transcribe_worker.py server
                            │  JSON-RPC over stdin/stdout
                            │  newline-delimited
                            ▼
              {id, command, payload}  →  {id, ok, result|error}
```

- `Services/TranscriptionWorkerClient.cs` owns the worker subprocess. It starts the worker lazily on the first call, multiplexes requests by `id`, and routes responses back via `ConcurrentDictionary<string, TaskCompletionSource>`.
- Worker commands (see `worker/transcribe_worker.py:run_server`): `transcribe`, `postprocess`, `download_model`, `diarize`. Anything new must be implemented on both sides.
- The worker script is **copied into the build output** via `<Content Include="..\..\worker\**\*">` in the csproj — at runtime it sits at `worker/transcribe_worker.py` next to `Muesli.exe`.
- Python **is** bundled for end users. `scripts/fetch-python-runtime.ps1` downloads CPython 3.12.11 from `astral-sh/python-build-standalone` (SHA-256 pinned) into `<publish>/python/` at packaging time, and `package-windows-v1.ps1` pip-installs base worker deps into `<publish>/python/site-packages-muesli/` via `--target`.
- `Services/WorkerRuntimeLocator.cs` resolves the interpreter in this order: `MUESLI_PYTHON` env → `<app>/python/python.exe` (bundled) → `.venv/Scripts/python.exe` beside the exe → parent `.venv` → `.venv-worker/` at repo root → user-local Python 3.12 → 3.11 → plain `python` on PATH. `LastResolutionSource` records which branch fired; `App.xaml.cs` logs it at startup so you can see which Python the worker is using.
- `WorkerRuntimeLocator.ApplyWorkerEnv(startInfo, python)` sets `PYTHONHOME`, `PYTHONPATH`, `PYTHONNOUSERSITE`, and prepends the bundled `python\` + `python\Scripts` to `PATH` whenever the resolved Python is the bundled one. `TranscriptionWorkerClient.EnsureWorker` and `RuntimeDiagnosticsService.RunProcessAsync` both call it — anything that spawns the bundled Python must too, or imports will silently fall through to a system site-packages.

### App lifecycle

- `App.xaml` uses `Page` build action (not `ApplicationDefinition`) so a custom `Main` can run. `<StartupObject>Muesli.Windows.App</StartupObject>` points at it. The `Main` in `App.xaml.cs` calls `Velopack.VelopackApp.Build().Run()` before any WPF init — this is where Velopack handles install/uninstall/firstrun args before the rest of the app loads.
- Crash reporting: `Sentry` 5.16.3 is opt-in. `App.OnStartup` initializes the SDK only when `MuesliSettings.CrashReportingEnabled == true` AND a DSN resolves (env `MUESLI_SENTRY_DSN`, or build-time `[assembly: AssemblyMetadata("SentryDsn", ...)]` injected via `dotnet build /p:SentryDsn=...`). `Services/SentryScrubber.cs` strips captures/cache paths and `Environment.UserName` from event text and drops breadcrumbs containing transcript / dictation / meeting / audio / apikey / token markers before send.
- Auto-update: `Velopack.UpdateManager` with `GithubSource("https://github.com/Muesli-HQ/Muesli-Windows", null, false)`. `MainWindow.StartBackgroundUpdateCheck` runs after `StartRuntime`; `CheckForUpdates_Click` doubles as "check" and "restart and apply" via `IsUpdateReady` / `UpdateButtonLabel`. Restart prompts are suppressed if `_meetingRecordingCoordinator.IsRecording` or `_dictationCoordinator.IsBusy`.

### WPF app shape

- `App.xaml.cs` — startup, global exception logging, decides whether to show the main window or park in background mode (`--background` / `--startup` args, or a recent boot when start-at-login is enabled).
- `MainWindow.xaml` (~110 KB) and `MainWindow.xaml.cs` (~159 KB) — most of the UI and code-behind. Heavy file; expect long edits and search-driven navigation.
- `Services/` — single-responsibility units, instantiated and wired up from `MainWindow`:
  - **Audio**: `AudioCaptureService` (WASAPI mic, segmented files, route recovery with preserved timeline gaps), `SystemAudioCaptureService` (meeting-process capture with a qualified and visible all-system fallback).
  - **Input**: `GlobalHotkeyService` (low-level `WH_KEYBOARD_LL` hook — supports modifier+key gestures like `Ctrl+Shift+F8`, not just `RegisterHotKey`).
  - **Coordinators**: `DictationCoordinator` (hold/double-tap state machine), `MeetingRecordingCoordinator` (capture finalization, chunked transcription, chronological reconciliation, diarization status, and cleanup metadata).
  - **Meetings**: `MeetingDetectionService` (heuristic: foreground window titles + browser URLs + process names for Meet/Zoom/Teams/Webex), `MeetingPromptService`, `MeetingSummaryService` (local fallback / OpenAI / OpenRouter), `MeetingExporter` (QuestPDF + markdown).
  - **Output**: `ActiveAppPasteService` (paste back into previously-focused app), `DictionaryCorrectionService` (phrase replacements), `TranscriptFormatter`.
  - **Shell**: `TrayIconService`, `ToastNotificationService`, `StartupRegistrationService` (HKCU Run key with `--background`).
  - **Persistence**: `SettingsStore`, `AppDataStore`, `AtomicJsonFile`, `MeetingSessionJournalStore`, `AppLogService`.
  - **Diagnostics**: `RuntimeDiagnosticsService` (worker-found / mic-test / model-download UI in Models page).

### Persistence

All under `%APPDATA%\muesli\`:

| Path | Contents |
|------|----------|
| `windows-settings.json` | Single `MuesliSettings` record — hotkey, model profile, paste behavior, theme, summary provider, API keys, indicator position, etc. |
| `data\windows-dictations.json` | Dictation history |
| `data\windows-meetings.json` | Meeting history (edited/raw transcript, timed segments, notes, status, summary, source recovery audio, warnings) |
| `data\windows-meeting-folders.json` | Folder organization |
| `data\windows-meeting-templates.json` | Custom summary templates |
| `data\windows-dictionary.json` | Phrase → replacement pairs |
| `logs\*.log` | App logs (About → Open Logs reveals) |

The JSON files use versioned envelopes, atomic replacement, backups, and corrupt-file quarantine. **SQLite is not implemented** — do not assume a database exists.

The current dated implementation summary and known risks live in `docs/PROJECT_STATUS.md`; update it when a substantial capability or verification baseline changes.

Whisper/Parakeet/Qwen model weights cache to `%USERPROFILE%\.cache\muesli` (override with `MUESLI_MODEL_CACHE`).

### ASR backend selection

- Engine and model come from settings (`AsrEngine` ∈ `whisper` | `parakeet-v3`, `ModelProfile` ∈ `tiny|base|small|medium|large-v3-turbo`).
- Whisper device selection (`worker/transcribe_worker.py:device_candidates`) checks `nvidia-smi -L`; falls back CUDA → CPU. Override with `MUESLI_DEVICE` (`auto`|`cuda`|`cpu`) and `MUESLI_COMPUTE_TYPE`.
- Qwen post-processor model downloads are gated: set `MUESLI_ALLOW_MODEL_DOWNLOAD=1` for the first download or pre-place weights in the cache. Default model is `Qwen/Qwen2.5-3B-Instruct`; override via `MUESLI_POST_PROCESSOR_MODEL`. If post-process deps are missing the worker silently falls back to `fallback_cleanup()` (regex-style cleanup).

### Relevant environment variables

- `MUESLI_PYTHON` — explicit path to `python.exe` for the worker (wins over bundled).
- `MUESLI_MODEL_CACHE` — override model cache directory.
- `MUESLI_DEVICE`, `MUESLI_COMPUTE_TYPE` — force Whisper backend.
- `MUESLI_ALLOW_MODEL_DOWNLOAD=1` — allow first-time Qwen download.
- `MUESLI_POST_PROCESSOR_MODEL` — alternate HF model id.
- `MUESLI_SKIP_AUTODOWNLOAD=1` — skip the post-onboarding base-model auto-download (`MainWindow.EnsureBaseModelDownloadedAsync` short-circuits).
- `MUESLI_SENTRY_DSN` — runtime override of the embedded Sentry DSN.
- `OPENAI_API_KEY`, `OPENROUTER_API_KEY` — summary providers (also enterable in Settings UI).
- `HF_TOKEN` — required for pyannote diarization first-download.

## Project conventions

- **Do not auto-push to git.** The user reviews and pushes manually. Stage and commit only when explicitly asked; do not push unless told to "push to git" (from `AGENTS.md`).
- **Match the macOS Muesli product behavior** where the Windows app is porting features. The OG reference (per `AGENTS.md`) is `C:\Users\madha\Downloads\muesli-main\muesli-main` — not in this repo. Prefer faithful clones of macOS UX over inventing new flows.
- **No placeholder/fake data.** If a flow can't be fully implemented, surface that in UI/logs rather than fabricating values.
- **`MainWindow.xaml.cs` was reconstructed from corrupted fragments** (per `AGENTS.md`). Build success does not imply correctness — verify the UI path you touched.
- WPF/XAML patterns already in use in `MainWindow.xaml` and `App.xaml` (shared brushes/styles) should be the template for new UI — don't introduce a different theming approach.
