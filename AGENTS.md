# AGENTS.md — Muesli Windows

## Project Context
- Windows-native WPF clone of the macOS Muesli app (dictation + meeting transcription)
- OG reference repo: `C:\Users\madha\Downloads\muesli-main\muesli-main`
- Active app lives in `windows-native/Muesli.Windows/`
- Legacy Electron/web files exist on disk but are excluded from git
- Current implementation status and verified limitations: `docs/PROJECT_STATUS.md`

## User Preferences

### Git / Version Control
- **NEVER auto-push to git unless user explicitly says "push to git"**
- User will review and push manually when ready
- Keep changes in working directory until explicitly approved for commit

### Code Style
- Follow existing WPF/XAML patterns in the codebase
- Prefer exact clone of macOS behavior where possible
- No placeholders or fake data

### Testing Workflow
- Kill running `Muesli.exe` before building when needed
- Build with `dotnet build --no-restore`
- Run `dotnet test windows-native/Muesli.Windows.Tests/Muesli.Windows.Tests.csproj --no-restore`
- Use `scripts/run-development.ps1` for the canonical development instance
- Run executable directly from `bin/Debug/net8.0-windows/`

## Architecture Notes
- `MainWindow.xaml` — Main UI (sidebar, pages, dictation list)
- `MainWindow.xaml.cs` — Code-behind (theme switching, data binding, event handlers)
- `App.xaml` — Shared resources, brushes, styles
- `Services/` — Meeting detection, prompt service, tray icon, etc.
- Data stored in versioned, atomic JSON files under `%APPDATA%/muesli/`; SQLite is not implemented

## Known Risks
- `MainWindow.xaml.cs` was severely corrupted (~3444→979 lines) and reconstructed from fragments
- Build passes but subtle bugs may remain from reconstruction
- Always verify UI behavior after changes, don't rely solely on build success
