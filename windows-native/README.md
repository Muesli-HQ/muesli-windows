# Muesli Windows Native

This is the Windows-native app path. The Electron app remains in the repository only as a prototype/reference while the native port is built.

## Stack

- WPF / .NET 8 desktop app
- Native `RegisterHotKey` global shortcut handling
- Native WASAPI microphone capture via NAudio
- Meeting-process audio capture with qualified all-system WASAPI fallback
- Local ASR worker boundary for Whisper CPU, Whisper CUDA, and optional NVIDIA Parakeet
- Atomic/versioned persistent dictations, meetings, dictionary entries, and settings under `%APPDATA%\muesli`
- Chronological two-track meeting transcripts with compact persisted timing metadata

## Build

```powershell
Get-Process Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\windows-native\Muesli.Windows\Muesli.Windows.csproj --no-restore
dotnet test .\windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore
```

## Run

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-development.ps1
```

The launcher prevents duplicate development instances. Quit Muesli from the tray before launching a new build.

## Current Status

The app now includes the core dictation and meeting workflow, bundled Python packaging, provider-backed summaries, exports, recovery-oriented meeting persistence, process-targeted meeting audio, automatic temporary-audio cleanup, and a Windows test project.

See [`../docs/PROJECT_STATUS.md`](../docs/PROJECT_STATUS.md) for the verified state, remaining risks, and current development workflow.
