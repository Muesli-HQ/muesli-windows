# Muesli Windows development guide

Muesli is a local-first Windows dictation and meeting-transcription app. The shipping product is
the packaged WinUI 3 application in `windows-native/Muesli.Windows.WinUI/`, targeting .NET 10 and
x64. The old WPF shell is archived at `windows-native/Muesli.Windows.Wpf.Legacy/` for historical
reference only.

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

## Package

```powershell
.\scripts\package-windows-v1.ps1
```

That compatibility command routes to the WinUI MSIX packager. Signed distribution and Store
submission remain release gates.

## Product rules

- Local transcription is the default and must fail closed when its selected model is unavailable.
- Microphone and meeting-loopback capture must always be explicit and visibly disclosed.
- Never add fake history or placeholder product data.
- Preserve compatibility with existing `%APPDATA%\muesli` history and settings.
- Treat `windows-native/Muesli.Windows.Wpf.Legacy/` as read-only reference, never as a dependency
  of the active solution or release path.
