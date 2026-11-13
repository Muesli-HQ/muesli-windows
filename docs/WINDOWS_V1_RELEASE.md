# Muesli Windows 0.3.0 Release Checklist

## Release policies (v1 / 0.3.0 beta)

- **Updates use the Store channel.** Muesli can check a signed update manifest and download/verify a direct-download package, but automatic direct-download installation is disabled without a production signing identity. Store-delivered upgrades still need clean-machine qualification.
- **No audio interference.** Muesli records without pausing or ducking unrelated media.
- **PDF export is enabled**: QuestPDF Community-license eligibility was approved on 2026-10-11 (Muesli is open source). `MUESLI_PDF_EXPORT_APPROVED=0` withdraws it.
- **Microsoft Store is the selected release channel.** Public support and privacy URLs are pending. The Store submission MSIX is unsigned; Microsoft signs the package after certification. See [`WINDOWS_STORE_RELEASE.md`](WINDOWS_STORE_RELEASE.md).

## Build artifacts (WinUI MSIX)

The shipping artifact is a WinUI MSIX submitted to Microsoft Store. Build the verified,
unsigned submission package from a clean committed checkout:

```powershell
.\scripts\build-store-release.ps1
```

Expected outputs:

```text
artifacts\store\Muesli.Windows.WinUI_<version>_x64.msix
artifacts\store-submission-report.json
artifacts\native-runtime-inventory.json
artifacts\package-content-inventory.json
```

The MSIX is normalized after Build: the shared Swift bridge closure and WPF companion are added,
foreign-RID (ARM64/x86/foreign) native libraries are pruned, and notices/licenses are included.
`scripts\generate-native-runtime-inventory.ps1` writes the CPU-only native inventory. Package tests
fail if notices claim CUDA is included, if a packaged native DLL is missing from the catalog, or if
the package carries ARM64/foreign or CUDA content.

## What Works

- Native Windows WinUI 3 shell with in-process floating indicators and meeting prompts.
- Hold shortcut to dictate and paste into the active app.
- Native WASAPI microphone capture and best-effort system loopback capture.
- Seven pinned offline sherpa-onnx choices across Parakeet, Whisper, SenseVoice, Qwen3-ASR, and Cohere, with separate dictation and final meeting/import roles.
- Optional native Qwen/GGUF cleanup through LLamaSharp, disabled by default.
- Native sherpa-onnx diarization for recorded meeting system audio.
- Persistent dictation and meeting history.
- Import media into meeting records.
- Meeting summaries through local, OpenAI, OpenRouter, Ollama, LM Studio, and custom compatible providers.
- Manual meeting export as Notes, Transcript, or Full Meeting in PDF or Markdown.
- System tray, startup setting, themes, model cache management, and diagnostics.

## Runtime Requirements

- Windows x64.
- Self-contained .NET app package.
- Explicit network-backed model preparation with independent cancel/retry/verify/delete behavior and on-demand diarization models.
- The Python runtime has been removed. No Python, venv, or sidecar worker runtime is required.
- Optional summary provider keys can be entered in Settings or supplied with `OPENAI_API_KEY` / `OPENROUTER_API_KEY`.

## Known Limits

- The public package includes the **CPU Sherpa provider only**. It does not
  claim NVIDIA acceleration. NVIDIA CUDA is an optional, SHA-256-verified
  acceleration pack downloaded from the pinned sherpa-onnx 1.13.4 release into
  `%LOCALAPPDATA%\muesli`; it requires a user-supplied CUDA 12 / cuDNN 9 runtime and
  is only reported active after a real warm-up inference proves which execution
  provider executed the graph. DirectML is not offered: the pinned Windows
  sherpa-onnx build was compiled without `SHERPA_ONNX_ENABLE_DIRECTML` and logs
  `DirectML is for Windows only. Fallback to cpu!`, so Muesli never labels CPU
  inference as DirectML. Timestamped segments support recorded
  meetings and speaker-diarization alignment.
- Qwen cleanup has a native v1 runtime path.
- Qwen cleanup is disabled by default and needs an explicitly installed and selected GGUF model. Existing manually placed compatible files remain usable.
- System loopback capture can be blocked by some Windows audio/device setups; mic recording still works.
- First use of a model requires network access unless the model is already cached.
- The submission MSIX is unsigned. A Store-certified package is signed by Microsoft;
  the unsigned package is not a public website installer.

## Local packaged-app QA

```powershell
dotnet build .\windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64
powershell -ExecutionPolicy Bypass -File .\scripts\run-windows.ps1 -SkipBuild
```

The script launches the development package with WinApp CLI and package identity. It is
not a substitute for installing the Store-delivered build on a clean Windows profile.
The Store build wrapper runs product tests, package smoke checks, and identity verification.
Fast packaging-contract checks can be run separately:

```powershell
.\scripts\tests\test-store-packaging.ps1
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
Store install/upgrade/uninstall exercise, CPU/GPU hardware qualification, Windows App
Certification Kit, and Store listing/certification remain separate gates. See the
outstanding items in [`WINDOWS_STORE_RELEASE.md`](WINDOWS_STORE_RELEASE.md).

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
- Startup setting enables/disables the packaged startup task.
- Package QA confirms no Python worker/runtime setup artifacts are included.
- Package QA executes `--diagnose-native` and fails if the shipped Sherpa/ONNX files exist but cannot actually load.
