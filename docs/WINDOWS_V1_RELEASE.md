# Muesli Windows 0.3.0 Release Checklist

## Build Artifacts

```powershell
.\scripts\package-windows-v1.ps1
.\scripts\build-installer.ps1
```

Expected outputs:

```text
artifacts\muesli-windows-0.3.0-win-x64.zip
artifacts\MuesliSetup-0.3.0-win-x64.exe
artifacts\native-runtime-inventory.json
```

`scripts\generate-native-runtime-inventory.ps1` also writes that inventory from a publish directory. Package tests fail if notices claim CUDA is included or if a packaged native DLL is missing from the inventory/catalog.

## What Works

- Native Windows WPF shell.
- Hold shortcut to dictate and paste into the active app.
- Native WASAPI microphone capture and best-effort system loopback capture.
- Seven pinned offline sherpa-onnx choices across Parakeet, Whisper, SenseVoice, Qwen3-ASR, and Cohere, with separate dictation and final meeting/import roles.
- Optional native Qwen/GGUF cleanup through LLamaSharp, disabled by default.
- Native sherpa-onnx diarization for recorded meeting system audio.
- Persistent dictation and meeting history.
- Import media into meeting records.
- Meeting summaries through local fallback, OpenAI, or OpenRouter.
- System tray, startup setting, themes, model cache management, and diagnostics.

## Runtime Requirements

- Windows x64.
- Self-contained .NET app package.
- Explicit network-backed model preparation with independent cancel/retry/verify/delete behavior and on-demand diarization models.
- The Python runtime has been removed. No Python, venv, or sidecar worker runtime is required.
- Optional summary provider keys can be entered in Settings or supplied with `OPENAI_API_KEY` / `OPENROUTER_API_KEY`.

## Known Limits

- The public Wave 0 package includes the CPU Sherpa provider only. It does not
  claim NVIDIA acceleration. CUDA remains optional external staging until L11
  qualifies a version-matched provider. Timestamped segments support recorded
  meetings and speaker-diarization alignment.
- Qwen cleanup has a native v1 runtime path.
- Qwen cleanup is disabled by default and needs a compatible GGUF model in the native-cleanup cache.
- System loopback capture can be blocked by some Windows audio/device setups; mic recording still works.
- First use of a model requires network access unless the model is already cached.
- Installer is not code-signed until a signing certificate is configured.

## Clean Install QA

```powershell
Expand-Archive .\artifacts\muesli-windows-0.3.0-win-x64.zip -DestinationPath $env:TEMP\muesli-0.3.0 -Force
cd $env:TEMP\muesli-0.3.0
.\Muesli.exe
```

Automated package smoke test:

```powershell
.\scripts\test-windows-package.ps1
```

Packaged CPU release evidence (CUDA is not included in the public package):

```powershell
.\scripts\qualify-windows-release.ps1 `
  -AudioPath .\testdata\dictation.wav `
  -Provider cpu `
  -Runs 10
```

Pass criteria:

The build, automated tests, package-structure checks, and native diagnostic are automated
gates. The transcription-quality corpus, target-application paste checks, fresh-account
installer/uninstaller exercise, CPU/GPU hardware qualification, signing, and SmartScreen
reputation are separate human or externally provisioned gates and must not be inferred from
automated benchmark JSON.

- App opens without crashing.
- Models page reports native runtime status.
- Models page reports each catalog item as Missing, Downloading, Verifying, Ready/Selected, Failed, Runtime unavailable, or Deletion failed.
- Models page reports Qwen cleanup as `Disabled`, `Needs model`, `Ready`, or `Runtime unavailable`.
- Explicit preparation preserves role selection and pinned archive plus required-file SHA-256 verification succeeds.
- Hold-to-dictate creates a row in Dictations.
- Active-app paste works in Notepad; fallback keeps transcript in the app.
- Recorded meeting produces transcript and notes.
- Recorded meeting transcript uses speaker labels when diarization models and system audio are available.
- Import media creates a meeting row.
- Native meeting qualification passes deterministic ASR, diarization, merge, provider, reuse, and latency gates.
- Approved media-import fixtures pass decoded-duration, determinism, latency, and optional WER/CER gates.
- About > Open Logs opens `%APPDATA%\muesli\logs`.
- Startup setting creates/removes the HKCU startup entry.
- Package QA confirms no Python worker/runtime setup artifacts are included.
- Package QA executes `--diagnose-native` and fails if the shipped Sherpa/ONNX files exist but cannot actually load.
