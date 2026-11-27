# Muesli Windows development guide

Muesli is a local-first Windows dictation and meeting-transcription app. The shipping product is
the packaged WinUI 3 application in `windows-native/Muesli.Windows.WinUI/`, targeting .NET 10 and
x64. The old WPF product shell and WPF indicator companion have been removed; the floating indicator is drawn in-process.

Shared behavior belongs in `Muesli.Windows.Core` or `Muesli.Windows.Platform`. The WinUI project
owns active pages, view models, dialogs, activation, and packaged Windows App SDK integration.
There is no Python worker in the shipping runtime.

## Build and launch

```powershell
dotnet build .\windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64
.\scripts\run-windows.ps1 -SkipBuild
```

WinUI must be launched through `winapp run` (the script above), not by executing the generated
`.exe` directly. A normal launch reads the production library at `%APPDATA%\muesli`; use
`scripts/run-winui-preview.ps1` only for an isolated profile.

To test a branch while another Muesli build keeps running, launch it as a development slot:

```powershell
.\scripts\run-windows-dev.ps1 -Slot A
```

That registers the build as its own package (`MuesliDevA`) with its own profile at
`%APPDATA%\MuesliDevA` and its own single-instance scope. Model downloads are shared with every
other build through `%USERPROFILE%\.cache\muesli`.

## Package

```powershell
.\scripts\package-windows-v1.ps1
```

That compatibility command routes to the WinUI MSIX packager and produces an unsigned package.

## Release

```powershell
.\scripts\release-windows.ps1 -Version 0.3.0 -DryRun
.\scripts\release-windows.ps1 -Version 0.3.0
```

The release is built by the `Windows release` GitHub Actions workflow, not locally; the script
checks preconditions, starts the workflow, verifies the draft's assets and publishes. GitHub
Releases carries the unsigned developer build as a pre-release; signed distribution is through the
Microsoft Store. Read `docs/WINDOWS_RELEASE.md` before changing the release path. Never publish,
tag or submit to the Store without the owner's explicit go-ahead.

## Product rules

- Local transcription is the default and must fail closed when its selected model is unavailable.
- Microphone and meeting-loopback capture must always be explicit and visibly disclosed.
- Never add fake history or placeholder product data.
- Library history lives in SQLite (`data\muesli.db`); read `database-schema.md` before changing persistence. Never edit a shipped schema version; add a new numbered migration. Preserve compatibility with existing settings files. JSON history files from pre-release builds are ignored: the app never reads, converts or deletes them.
- Keep the retired WPF product shell out of the active solution and release path.
