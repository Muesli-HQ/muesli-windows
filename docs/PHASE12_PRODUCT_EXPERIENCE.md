# Phase 12 product experience

Muesli’s onboarding is a dedicated window with durable, non-secret resume metadata in `%APPDATA%\muesli\onboarding-progress.json`. Settings remain authoritative: each selected setting is saved before progress metadata, and model readiness is reconstructed from lifecycle verification rather than persisted download state.

The required completion gates are a successful non-retaining WASAPI microphone capture, prepared selected offline model roles, advisory hotkey availability followed by the existing real hook path, and a successful non-empty local dictation pipeline result. Pausing keeps a Resume setup entry in About and the tray. The tray receives only real loaded history titles/times; it calls calendar availability unavailable until there is a calendar source.

`--phase12-page-case=<page/case>` is intentionally isolated before normal startup composition. It does not open `%APPDATA%`, log, mutate the startup registry, acquire single-instance state, create hooks/tray/detection, access microphones/models/credentials, or present fake stats/history. It verifies the actual `MainWindow.xaml` visual tree as well as the existing actual onboarding and tour windows.

## DPI and monitor verification

Run `powershell -ExecutionPolicy Bypass -File scripts\verify-phase12-ui.ps1 -ValidateOnly` to enumerate the 352 required cells without launching the app or writing files. To capture one cell, use an explicit output directory, for example:

`powershell -ExecutionPolicy Bypass -File scripts\verify-phase12-ui.ps1 -PageCase models/offline -Theme dark -Size narrow -RequiredScale 125 -OutputDirectory C:\temp\muesli-phase12`

The script launches the isolated preview, foregrounds its real HWND, requires `GetDpiForWindow` to equal the requested scale (100/125/150/200 = 96/120/144/192 DPI), then records the actual window/monitor bounds and PNG path in JSON. It refuses a DPI mismatch and never changes Windows scale. Repeat the same command after moving the preview to every real monitor, or in each VM at the required scale; the matrix and one-host capture do not claim physical multi-monitor coverage by themselves. A scaled viewport is not evidence of Windows per-monitor DPI behavior. The placement service uses the target monitor work area and window DPI, not the primary `SystemParameters.WorkArea`.

Muesli embeds an explicit application manifest with `dpiAwareness` set to `PerMonitorV2, PerMonitor` for Windows 10+ and the `dpiAware` `true/pm` fallback for older Windows. The source of truth is `windows-native/Muesli.Windows/app.manifest` embedded as `Muesli.exe` RT_MANIFEST; automated tests extract that built resource rather than grepping the csproj string. WPF owns startup via `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` with no conflicting WinForms `HighDpiMode` call. Physical 125/150/200% and multi-monitor captures remain remaining gates after a 100% smoke.

## Privacy and support

Microphone registry policy values are hints only; the short capture probe is authoritative and retains no audio. Cloud summary choices disclose destinations and only secure-store configuration is read; selection does not call a provider. About links users to privacy, logs, diagnostics/capture inspection, and manual release guidance. Diagnostics should exclude keys and transcript contents.
## Visual verification composition

`--phase12-page-case=<page/case>` is parsed before logging, single-instance handling,
startup repair, or production composition. Product-page cases construct the real
`MainWindow.xaml` tree through its isolated preview constructor; onboarding and the
feature tour retain their actual windows. The verification banner always states the
page, case, theme, viewport selection, and isolation status.

The truthful catalog has 22 cases: dashboard empty/long-text; meetings empty/long-text;
search empty; dictionary empty; models ready/downloading/failure/offline; shortcuts
conflict; settings normal/startup-unavailable; about diagnostics; onboarding
welcome/long-text/downloading/failure/offline/permissions-denied/completed; and feature
tour. Preview statistics and history are always zero/empty. Synthetic loading and
failure copy is explicitly preview-only and its controls have no effect.

`scripts/verify-phase12-ui.ps1 -ValidateOnly` reports the full formula: 22 cases × 2
themes × 2 viewport selections × 4 required scales = 352 cells. Captures refuse a
`GetDpiForWindow` mismatch, record requested and actual viewport, HWND and monitor
bounds, and can run the bounded `-CaptureAllForCurrentScale` sweep for only one verified
physical scale. Viewport DIPs are not DPI evidence.

The preview constructor does not create stores, AppData, onboarding progress, logging,
dictation/native clients, tray, hooks, meeting detection, registry access, or external
services. It also does not subscribe `SystemEvents`; normal construction still owns the
complete production lifecycle.
