# AGENTS.md — Muesli Windows

## Project Context

- Windows-native WinUI 3 app for local-first dictation and meeting transcription.
- OG macOS reference repo: `C:\Users\madha\Downloads\muesli-main\muesli-main`.
- Shipping app: `windows-native/Muesli.Windows.WinUI/`.
- Shared product code: `windows-native/Muesli.Windows.Core/` and `windows-native/Muesli.Windows.Platform/`.
- The retired WPF product shell has been removed. `Muesli.Windows.Indicator.Wpf` remains the active notification and indicator companion.
- Legacy Electron/web files exist on disk but are excluded from git.

## User Preferences

### Git / Version Control

- **NEVER auto-push to git unless the user explicitly says "push to git".**
- The user will review and push manually when ready.
- Keep changes in the working directory until explicitly approved for commit.

### Code Style

- Follow existing WinUI 3/XAML patterns in the active app.
- Prefer exact clone of macOS behavior where possible.
- Put reusable nonvisual behavior in Core or Platform, not the UI project.
- No placeholders or fake data.

### Testing Workflow

- **After every project update made by Codex, automatically build and launch/relaunch Muesli with its dashboard visibly open and foregrounded before reporting completion.**
- Kill running `Muesli.Windows.WinUI.exe` or legacy `Muesli.exe` before rebuilding when needed.
- Build with:
  `dotnet build windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64`
- Launch packaged WinUI with:
  `powershell -ExecutionPolicy Bypass -File scripts\run-windows.ps1 -SkipBuild`
- Never launch the WinUI executable directly. Package identity is required; use `winapp run` through the script.
- Normal manual testing uses the production profile at `%APPDATA%\muesli`. Use `scripts/run-winui-preview.ps1` only for an explicitly isolated test profile.
- After launch, inspect the latest `%APPDATA%\muesli\logs\muesli-*.log` slice and confirm no fresh `ERROR`, `Unhandled UI exception`, or `XamlParseException`.
- If the change is packaging-related, also run the relevant MSIX package smoke tests.

## Shared Swift Core (MuesliCore)

- Portable domain algorithms (transcript normalization, word count, insights text analysis, LZFSE, crypto, mel) live in the shared Swift `MuesliCore` package at `native/MuesliNative` in the macOS repo.
- Windows consumes selected shared algorithms through `MuesliCoreABI.dll` (`Sources/MuesliCoreABI`). The bridge is built and staged by `scripts/build-swift-core-bridge.ps1` and loaded only by full path from the application directory (`Services/Text/TranscriptTextProcessingBootstrap.cs`).
- **Windows SQLite remains authoritative for Windows persistence.** macOS and Windows intentionally use platform-specific persistence adapters. The Swift `DictationStore` ABI exports (`open`/`insert`/`recent`) remain a tested capability and are **not** connected to the live Windows database; do not migrate `muesli.db` or create a shadow database.
- New duplicated C# business logic should not be added when an equivalent canonical Swift implementation exists. `LibraryMetrics.CountWords` and dictation transcript normalization route through `ITranscriptTextProcessor`.
- A parity-tested managed fallback (`ManagedTranscriptTextProcessor`) keeps dictation working if the bridge is missing; it must stay behaviorally identical to Swift.
- UI and OS integrations (WinUI, WPF pill, hotkeys, audio, tray, packaging) remain native to each platform.

## Architecture Notes

- `Muesli.Windows.WinUI/App.xaml` — shared WinUI resources and styles.
- `Muesli.Windows.WinUI/MainWindow.xaml` and `MainPage.xaml` — active application shell.
- `Muesli.Windows.WinUI/Pages/` and `ViewModels/` — feature UI.
- `Muesli.Windows.Core/` — platform-neutral domain, persistence, and transcription behavior.
- `Muesli.Windows.Platform/` — Windows capture, hotkey, shell, and OS integrations.
- Data is stored in `%APPDATA%\muesli\` (SQLite, JSON settings, logs, and captures).

## Known Risks

- WinUI is now the only shipping shell; stale WPF release scripts or documentation must not be reintroduced as defaults.
- Package identity, startup registration, notifications, and activation must be tested through the packaged launch path.
- Always verify real UI behavior after changes; do not rely on build success alone.
