# WinUI UI qualification

Automated coverage lives in `windows-native/Muesli.Windows.UITests`. The packaged WinUI shell is
launched through `winapp run` against an isolated `%TEMP%` profile; production `%APPDATA%\muesli`
data is never touched. These tests are opt-in:

```powershell
$env:MUESLI_UI_AUTOMATION = '1'
dotnet test windows-native\Muesli.Windows.UITests\Muesli.Windows.UITests.csproj -c Debug
```

Without the opt-in (or without `winapp.exe` on `PATH`), each test reports `Skipped` with its named
prerequisite instead of a green pass. `MuesliCleanProfileTests` is the only un-gated member.

## Required surfaces

| Surface | Automated evidence |
|---|---|
| Dashboard / Timeline | `WinUiShellQualificationTests.Shell_navigates_every_destination_and_publishes_its_controls` (`NavTimeline` → `TimelineRefreshButton`), `Populated_profile_captures_all_reference_states_and_secondary_surfaces` (`TimelineList`, resizes 1280/1440/1600 DIPs, keyboard focus) |
| Dictations | same navigation test (`NavDictations` → `DictationRecordButton`); populated captures (`DictationHistoryList`) |
| Meetings | same navigation test (`NavMeetings` → `QuickNoteButton`); `Meetings_page_hides_capture_and_recovery_panels_while_idle` |
| Meeting detail | `Populated_profile_*` (`MeetingTitleEditor` → `MeetingTranscriptTab` → `MeetingTranscriptEditor`) |
| Models | same navigation test (`NavModels` → `ModelsRefreshButton`) |
| Insights | `Insights_share_preview_renders_the_generated_card`; populated captures (hero/overview/heatmap) |
| Dictionary | same navigation test (`NavDictionary` → `DictionaryAddButton`); populated captures |
| Shortcuts | same navigation test (`NavShortcuts` → `ChangePushToTalkButton`) |
| Settings | same navigation test; `Theme_setting_switches_the_shell_between_light_and_dark`; `Startup_registration_*` |
| About | same navigation test (`NavAbout` → `RefreshDiagnosticsButton`); `About_diagnostics_detail_is_reachable_behind_its_disclosure` |
| Search | `Search_box_routes_the_shell_to_matching_local_history` |
| Onboarding | `First_run_profile_opens_the_onboarding_window` |
| Tray restore | Not automatable in this suite: the Windows 11 tray overflow flyout is not UIA-enumerable. Operator step below. |
| Single-instance second activation | `Second_process_redirects_and_the_primary_shell_stays_attached` (unpackaged); `Second_process_activation_returns_the_primary_shell_to_the_dashboard` (unpackaged, `SingleInstanceCoordinator` → `MainWindow.ShowDashboard`). Packaged AUMID re-activation is platform-mediated; operator step below. |
| Startup-task status | `Startup_registration_is_available_with_package_identity`, `Startup_registration_is_reported_unavailable_without_package_identity` |
| Light / Dark | `Theme_setting_switches_the_shell_between_light_and_dark` (measured mean luminance) |
| High Contrast | `WinUiShellQualificationTests` records `HighContrastAutomationAvailable` in `winui-qualification-capabilities.json`; rendering is an operator step (the harness never changes the OS contrast theme). |
| WPF companion visible / fallback hidden / no simultaneous indicators | `Muesli.Windows.Tests.IndicatorHostContractTests` pins the mutual-exclusion invariants at the source level; the live cross-process launch is an operator step. |
| Content visible (not just title bar) | Page sentinels are page-only controls required on-screen; indicator states assert accessible name + measured DIP size. |
| AutomationIds / accessible names / keyboard focus | `RequireAutomationId`, `RequireAccessibleName`, `AssertKeyboardFocusTraversal`; `UiScreenshot` captures on failure. |
| No accent leaks / clipping | Populated reference captures under both themes; pixel inspection is an operator review (screenshots alone are not proof). |

## Operator steps (external prerequisites named)

1. **Tray restore.** Launch the packaged app, enable the tray icon, then right-click the Muesli tray
   icon and choose "Open dashboard". Confirm the dashboard is foregrounded. The Win11 overflow
   flyout cannot be driven by UIA, so this remains a human observation.
2. **Packaged AUMID re-activation.** With the packaged app running on the Settings page, launch
   Muesli from the Start menu. Confirm the existing shell returns to the dashboard and no second
   process remains.
3. **WPF companion.** Launch the packaged app with the floating indicator enabled and confirm
   `Muesli.Windows.Indicator.Wpf.exe` owns exactly one pill and the WinUI process hosts no second
   indicator window (`Get-Process` + window enumeration).
4. **High Contrast.** Enable a High Contrast theme in Windows Settings and confirm the shell and
   floating pill repaint with system colors; capture the result.
5. **Multi-monitor / DPI.** Move the shell and pill across monitors at 100 / 125 / 150 / 200% and
   confirm placement, dragging, and DPI-correct geometry.

## Floating pill drag regression (Phase D)

Automated evidence: `IndicatorDragSessionTests`, `IndicatorDragTests`,
`IndicatorRenderRegressionTests`, `IndicatorWaveformTests` cover the fixed-origin drag math,
amplitude-only frames not being structural, and capture-loss ending the drag with exactly one
persisted position. A 30 FPS screen recording of preparing → speech → drag-while-speaking →
release/persisted → stop/transcribe/success → second dictation is an operator step (requires live
microphone speech and a capture tool).
