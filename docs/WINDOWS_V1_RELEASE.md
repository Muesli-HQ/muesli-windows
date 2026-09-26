# Muesli Windows 0.3.0 Release Checklist

## Release policies (v1 / 0.3.0 beta)

- **Updates are manual.** Muesli can check a signed update manifest and download/verify a package, but automatic installation is disabled until a production signing identity exists. Release notes and the project page remain the update channel.
- **No audio interference.** Muesli records without pausing or ducking unrelated media.
- **PDF export is disabled** (`PdfExportApproved=false`) pending an explicit QuestPDF Community-license eligibility decision. Markdown export remains available; PDF must not be advertised.
- **Support/privacy publication and production signing are not final.** Public support and privacy URLs are pending, the package carries the development publisher, and it is unsigned until a production signing identity is configured.

## Build artifacts (WinUI MSIX)

The shipping artifact is the unsigned/signed WinUI MSIX.

```powershell
.\scripts\package-winui-msix.ps1
.\scripts\sign-windows-release.ps1 -MsixPath <path-to.msix>   # production: cert + timestamp required
```

Expected outputs:

```text
artifacts\msix\Muesli.Windows.WinUI_<version>_x64.msix
artifacts\native-runtime-inventory.json
artifacts\package-content-inventory.json
```

The MSIX is normalized after Build: the shared Swift bridge closure and WPF companion are added,
foreign-RID (ARM64/x86/foreign) native libraries are pruned, and notices/licenses are included.
`scripts\generate-native-runtime-inventory.ps1` writes the CPU-only native inventory. Package tests
fail if notices claim CUDA is included, if a packaged native DLL is missing from the catalog, or if
the package carries ARM64/foreign or CUDA content.

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
