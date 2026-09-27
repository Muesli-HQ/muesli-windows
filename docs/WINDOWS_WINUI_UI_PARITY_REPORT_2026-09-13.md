# WinUI UI parity report — 2026-09-13 (Prompt 9, final qualification)

**Status: IN PROGRESS (updated 14:15) — done: §1; §2 builds, managed runs 1–3, UI run 1; §3 (CR-01 fixed + verified); §4 routes Dark/Light at 3 sizes + resize pass, P9-02/P9-03/P9-04 fixed + verified; §5.1 keyboard; §7; §8; §9; §10. NOT yet done: managed run 4 and UI runs 2–3 on the final source (§2), §5.2 states/secondary windows/onboarding/empty/theme switch, §6 logs, §11 exit criteria, §12 verdict, §13 final state (WPF relaunch).** Anything not marked verified has not been checked in this run.

Prompt 9 of `docs/WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md`. Closes out `docs/WINDOWS_WINUI_UI_PARITY_BASELINE_2026-09-10.md`.
Fresh evidence: `docs/ui-reference/winui-prompt9-2026-09-13/`.

## 1. Environment and method

- Display: single monitor, 1920×1080 physical at **125 %** (the user's real setting; not changed).
- SDK: user-local `%LOCALAPPDATA%\Microsoft\dotnet` prepended to `PATH`.
- The user's WPF `Muesli.exe` (PID 26432, started 13:06:37) was stopped at 13:25 so the packaged WinUI shell could acquire the per-user single-instance mutex (`windows-native/Muesli.Windows.Platform/Services/SingleInstanceCoordinator.cs:38-44`). It is relaunched at the end (§13).
- Packaged launch: `winapp run windows-native/Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj --no-restore --detach --json --property Platform=x64 --args "--profile-root=<isolated %TEMP% root>"`. The real `%APPDATA%\muesli` was never passed.
- Capture: DPI-aware host (`SetProcessDpiAwarenessContext(-4)`) + `PrintWindow(PW_RENDERFULLCONTENT)`; every PNG checked to be exactly DIP × 1.25. Popups are proved by UIA text, not by `PrintWindow` pictures (it does not capture popups). Hover/selected states judged only after a real multi-step pointer path, never after a teleporting synthetic click.

## 2. Build and test results (this run, exact)

Builds (13:25–13:27, `--no-restore`):

| Project | Result |
|---|---|
| `windows-native/Muesli.Windows.WinUI` (x64) | `Build succeeded. 0 Warning(s) 0 Error(s)` (26.5 s) |
| `windows-native/Muesli.Windows` (WPF fallback) | `Build succeeded. 0 Warning(s) 0 Error(s)` (incremental, up to date) |
| `windows-native/Muesli.Windows.Tests` | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `windows-native/Muesli.Windows.UITests` | `Build succeeded. 0 Warning(s) 0 Error(s)` |

Every later source change was followed by a `--no-restore` rebuild, all `Build succeeded. 0 Warning(s) 0 Error(s)`: WinUI x64 at 13:34 and 13:36 (CR-01), 13:53 (P9-02 — together with the **WPF fallback** `Muesli.Windows.csproj`, `Muesli.Windows.Tests` and `Muesli.Windows.UITests`, all 0/0, because Core changed), 13:59 (P9-03) and 14:10 (P9-04, the final WinUI build). The WPF fallback therefore builds against the final Core.

Test runs:

| Suite | When | Build under test | Result |
|---|---|---|---|
| `Muesli.Windows.Tests` run 1 (`dotnet test -c Debug --no-build`) | 13:25:39–13:30:00 | pre-CR-01 | `Passed! - Failed: 0, Passed: 840, Skipped: 5, Total: 845, Duration: 4 m 17 s` |
| `Muesli.Windows.UITests` run 1 (`MUESLI_UI_AUTOMATION=1`, all 20 tests: 12 `WinUiShellQualificationTests` (10 packaged host + 2 explicit unpackaged), 3 WPF `MuesliUiSession` production tests, 5 harness/isolation tests) | 13:39–13:46:55 | post-CR-01 | `Total tests: 20 Passed: 20 Total time: 7.9474 Minutes` |
| `Muesli.Windows.Tests` run 2 | 13:47:04–13:51:42 | post-CR-01 (this suite has text-contract tests that read WinUI XAML, e.g. `windows-native/Muesli.Windows.Tests/WinUiShellParityTests.cs:125-127`, so it was rerun) | `Passed! - Failed: 0, Passed: 840, Skipped: 5, Total: 845, Duration: 4 m 35 s` |
| `Muesli.Windows.Tests` run 3 | 13:53:47–13:58:14 | post-P9-02 streak fix (Core change) | `Passed! - Failed: 0, Passed: 840, Skipped: 5, Total: 845, Duration: 4 m 24 s` |
| `Muesli.Windows.Tests` run 4 | 14:12:37–14:16:53 | **final source** (after P9-03 copy and P9-04 accessible names) | `Passed! - Failed: 0, Passed: 840, Skipped: 5, Total: 845, Duration: 4 m 14 s` |
| `Muesli.Windows.UITests` run 2 | 14:19:11–14:27:03 | final source | `Total tests: 20 Passed: 20 Total time: 7.8604 Minutes` |
| `Muesli.Windows.UITests` run 3 | 14:27:03– | final source | _running_ |

The 5 skips are the same environment-gated real-audio / real-inference / operator-profile cases listed in the baseline §8.1. Logs: `scratchpad/p9r-managed-tests.log`, `p9r-managed-tests-run2.log`…`run4.log`, `p9r-uitests-run1.log`…`run3.log`.

## 3. `winui-code-review` findings

Scope: the whole untracked `windows-native/Muesli.Windows.WinUI/` tree (~20 k lines of XAML/C#; `git diff` cannot show it), reviewed as working-tree files against the skill checklist. Pattern sweeps covered sync-over-async, `async void`, `{Binding}` vs `x:Bind`, `x:Bind` without `Mode`, hard-coded colours, Page-level accelerators, `Process.Start`/`File.Delete`, globalization. Findings in order of severity:

| # | Sev | Where | Finding | Disposition |
|---|---|---|---|---|
| CR-01 | **Error (crash class)** | `windows-native/Muesli.Windows.WinUI/App.xaml.cs:89-90` + unguarded clipboard writes at `ViewModels/SearchPageViewModel.cs:156-166`, `ViewModels/AboutPageViewModel.cs:92-98`, `MeetingLiveTranscriptWindow.xaml.cs:355-369` | `App.UnhandledException` only logs; it never sets `Handled`, so any exception reaching it terminates the shell. `Clipboard.SetContent`/`Flush` (`Services/WinUiInteractionServices.cs:194-200`) throws `COMException` when another process holds the clipboard open. The sibling copy commands already catch this and show a status (`ViewModels/LibraryPageViewModels.cs:250-262`, `ViewModels/MeetingDetailViewModel.cs:258-268`); these four do not, and are invoked through `async void` handlers (`Pages/SearchPage.xaml.cs:84-91`) or `AsyncRelayCommand` (which rethrows on the UI context by default). | See §3.1 — reproduced live, then fixed. |
| CR-02 | Warning | `OnboardingWindow.xaml` (33), `Pages/ModelsPage.xaml` (57), `Pages/MeetingDetailPage.xaml` (6), `Pages/DictionaryPage.xaml` (4), `Pages/MeetingsPage.xaml` (3), `Pages/DictationsPage.xaml` (1), `Pages/ShortcutsPage.xaml` (1) | Reflection `{Binding}` rather than compiled `x:Bind`. Existing pattern established before Prompt 1; functionally verified by the route pass. | Deferred — optional code quality (§8.3). |
| CR-03 | Note | Item templates in `Pages/TimelinePage.xaml`, `Pages/MeetingsPage.xaml`, `Pages/InsightsPage.xaml`, `Pages/SettingsPage.xaml` | ~85 `x:Bind` without `Mode` — all bind immutable record properties or commands, where `OneTime` is correct. `Pages/DictionaryPage.xaml:401,403` binds `ViewModel.ThresholdHelp` (a constant getter, `ViewModels/DictionaryPageViewModel.cs:57`) — also correct. | No change. |
| CR-04 | Note | whole tree | No `x:Uid`/`.resw`; all strings hard-coded. | Deferred — whole-app globalization decision (§8.2). |
| CR-05 | Note | whole tree | No sync-over-async (`.Result`/`.Wait()`/`GetResult`) anywhere; no hard-coded hex colours outside `Themes/MuesliTheme.xaml`; every `async void` is an event handler or `OnLaunched`. | Pass. |
| CR-06 | Note | `Pages/MeetingTemplatesPage.xaml:1-35` | The Page-level Ctrl+S / Ctrl+N / Escape accelerator site carried forward from Prompt 8 already carries `KeyboardAcceleratorPlacementMode="Hidden"` with a P8-06 comment. | Resolved in tree; live stray-`Pop-upHost` check in §4. |
| CR-07 | Warning | `windows-native/Muesli.Windows.WinUI/App.xaml.cs:89-90` | Global crash policy: logging without `Handled` means *any* future unguarded UI exception kills the shell. Changing it is an app-wide policy decision (swallowing can leave corrupt state), not a UI parity fix. | Deferred — §8.2. |

### 3.1 CR-01 — reproduced as a shell-killing blocker, then fixed

**Reproduction on the unmodified build (13:31, packaged host, isolated profile `%TEMP%\muesli-p9r-dark`).** A helper process called `OpenClipboard(NULL)` and held it for 20 s (`scratchpad/hold-clipboard.ps1`); About → **Copy diagnostics** was invoked. The fresh log recorded

```
[2026-09-13 13:31:38.357] ERROR Unhandled WinUI exception.
System.Runtime.InteropServices.COMException (0x800401D0)
   at Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(DataPackage content)
   at Muesli.Windows.WinUI.Services.WinUiClipboardService.SetTextAsync(...)
   at Muesli.Windows.WinUI.ViewModels.AboutPageViewModel.CopyDiagnosticsAsync(...)
   at CommunityToolkit.Mvvm.Input.AsyncRelayCommand.AwaitAndThrowIfFailed(Task executionTask)
```

and **the `Muesli.Windows.WinUI` process was gone ~7 s later** (process lookup returned nothing). Any app that briefly holds the clipboard (clipboard managers, RDP clipboard sync, Office) makes a Copy button end the shell — exit criterion 1's "unhandled UI exceptions" class, on an existing exposed action.

**Fix (UI invocation only; no service contract changed):**
- `windows-native/Muesli.Windows.WinUI/ViewModels/AboutPageViewModel.cs:93-115` and `ViewModels/SearchPageViewModel.cs:156-182` now catch, set a new `IsStatusError`, and show the failure in the page's existing `InfoBar`, whose `Severity` is now bound to it (`Pages/AboutPage.xaml:437-441` + `Pages/AboutPage.xaml.cs:32-33`; `Pages/SearchPage.xaml:128-132` + `Pages/SearchPage.xaml.cs:37-38`) — previously hard-coded `Success`.
- `windows-native/Muesli.Windows.WinUI/MeetingLiveTranscriptWindow.xaml.cs:355-394` routes both copy handlers through one guarded `CopyAsync` that shows the failure in a flyout on the button (the session `InfoBar` is owned by capture state and would be overwritten).
- `Services/WinUiInteractionServices.cs:170-184` adds `WinUiClipboardService.DescribeFailure`: the WinRT projection of `CLIPBRD_E_CANT_OPEN` has an **empty** `Message`, so the first fix attempt rendered "Could not copy diagnostics: " with nothing after it. The two already-guarded siblings (`ViewModels/LibraryPageViewModels.cs:260`, `ViewModels/MeetingDetailViewModel.cs:267`) had the same empty-text defect and now use it too.

**Verification on the rebuilt shell (13:36–13:38, clipboard held for 60 s):** About → Copy diagnostics and Search "Ship" → `SearchCopyButton` both left the shell running; UIA text read `Could not copy diagnostics: another app is using the clipboard. Try again.` and `Could not copy the dictation: another app is using the clipboard. Try again.`; no new `ERROR` line was logged; the process was still alive after the clipboard was released. Evidence: [51-cr01-search-copy-clipboard-busy-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/51-cr01-search-copy-clipboard-busy-dark-1280x820.png) (red error `InfoBar`). The About `InfoBar` sits below the diagnostics card, under the fold at 1280×820, so [50-cr01-about-copy-clipboard-busy-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/50-cr01-about-copy-clipboard-busy-dark-1280x820.png) does not show it — its presence is proved by UIA text only; placement recorded as polish (§8.3). The live-transcript flyout path is **source-reviewed and built, not rendered** (UNVERIFIED-03 still stands for that window).

## 4. Route and interaction qualification (packaged host)

### 4.1 New defects found by this run's route pass, and their resolution

| ID | Sev | Finding | Resolution |
|---|---|---|---|
| **P9-02** | major (P2-01 class — the same metric reading two values) | On the populated fixture the Timeline and Dictations **"day streak" read 6** while Insights **"current streak" read 5** (first-pass captures at 13:48). Cause: `WinUiLibraryContext` computed the streak over **timeline** days (dictations *and* meetings), while `InsightsWordAnalyzer` used dictations only. macOS defines it as a **dictation** streak for both (`native/MuesliNative/Sources/MuesliCore/DictationStore.swift:3796-3818` in the reference app selects `FROM dictations`). A meeting on day −5 extended the Timeline streak by one. | Fixed by giving the streak one definition next to P2-01's words/WPM rule: `windows-native/Muesli.Windows.Core/Insights/LibraryMetrics.cs` now owns `CurrentStreakDays`/`LongestStreakDays` (moved verbatim from `InsightsWordAnalyzer`, which now calls them at `Core/Insights/InsightsWordAnalyzer.cs:71-72`); `Services/WinUiLibraryContext.cs:212-214` passes dictation dates to the same functions, and its private duplicate streak code was deleted. Verified after rebuild: Timeline **5 day streak** = Insights **5 days current streak** — [01-timeline-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/01-timeline-dark-1280x820.png), [10-insights-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/10-insights-dark-1280x820.png). |
| **P9-04** | **blocker** (P3-01 class: screen reader announces a debug dump) | A full UIA sweep of every route, every Settings tab, meeting detail (both tabs), templates, the open folder editor and Search (`scratchpad/p9r-a11y-sweep.ps1` — walks every descendant of the dashboard and flags any `Name` shaped like a C# record `ToString()`) found **18 list items still named by their record dump**: `TimelineGroup { … }` ×7 (Timeline day groups), `DictationGroup { … }` ×5 (Dictations day groups), `DictionaryListItem { Id = ui-dictionary-muesli, Phrase = … }` ×3 and `DictionarySuggestion { … }` ×3. P3-01 had fixed only the Meetings lists; Prompts 2 and 7 did not carry the pattern to their own lists. | Same mechanism as P3-01 (`Pages/MeetingsPage.xaml.cs:168-182`): new `windows-native/Muesli.Windows.WinUI/Pages/ListItemAutomationNames.cs` names the container on `ContainerContentChanging` and clears it on recycle; wired at `Pages/TimelinePage.xaml:135`, `Pages/DictationsPage.xaml:166`, `Pages/DictionaryPage.xaml:135` and `:259`, with one-line handlers at the end of each page's code-behind. Re-sweep on the rebuilt shell (14:13): **"record-dump names: none" on all 18 surfaces**. Names now read e.g. `TODAY, 6 items`, `muesli, Replace with Muesli, 90% match`, `Suggested correction: muselli to Muesli`. |
| **P9-03** | minor (exit criterion 4 — copy promising behaviour that does not happen) | Settings → Meetings → Microphone said "Changes apply immediately" on a page where nothing is committed until **Save** (`MicrophoneName` is only written by the save path, `ViewModels/SettingsPageViewModel.cs:344`) and the "Unsaved changes" badge says so. | Copy reduced to what is true: `Pages/SettingsPage.xaml:635` "Only affects Muesli. Automatic uses the Windows default input." — [15-settings-meetings-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/15-settings-meetings-dark-1280x820.png). |

### 4.2 Every route, Dark, 1280×820 DIP (1600×1025 px, all verified)

Isolated profile `%TEMP%\muesli-p9r-dark`, seeded by `scratchpad/seed-p9.ps1`: 17 dictations over 5 days (incl. a 3× repeated long row and a one-word row), 5 meetings (long title, no transcript, health warnings + no summary, real 3 s WAV, 90-line transcript), 6 folders incl. two legitimately named `Notes` under different parents, 4 templates incl. a very long name, 3 dictionary entries incl. a 62-character word, 3 suggestions. Navigation by UIA `invoke` on the automation ids; every capture opened and inspected.

| Surface | Capture | Checked |
|---|---|---|
| Timeline | [01](ui-reference/winui-prompt9-2026-09-13/01-timeline-dark-1280x820.png) | metric cards agree with Dictations/Insights (5 / 319 / 105); long row ellipsised at 2 lines; meeting rows carry status + source chips; date groups; nested folder rail with subtree counts |
| Dictations | [02](ui-reference/winui-prompt9-2026-09-13/02-dictations-dark-1280x820.png) | same metrics; 3-line ellipsis on long row; no permanent destructive glyph |
| Meetings | [03](ui-reference/winui-prompt9-2026-09-13/03-meetings-dark-1280x820.png) | 4-block header; folder editor collapsed; long title wraps; "No summary saved" state; Recording badge only on the meeting with audio |
| Meeting detail — Notes / Transcript | [04](ui-reference/winui-prompt9-2026-09-13/04-meeting-detail-notes-dark-1280x820.png), [05](ui-reference/winui-prompt9-2026-09-13/05-meeting-detail-transcript-dark-1280x820.png) | folder picker shows the real folder; Delete behind "…"; proportional transcript face; speaker aliases |
| Meeting templates | [06](ui-reference/winui-prompt9-2026-09-13/06-meeting-templates-dark-1280x820.png) | long template name ellipsised; editor + destructive grouping |
| Models | [07](ui-reference/winui-prompt9-2026-09-13/07-models-dark-1280x820.png) | one card per family with variant picker; Active/Downloaded states (from the **real** model cache — D-04) |
| Shortcuts | [08](ui-reference/winui-prompt9-2026-09-13/08-shortcuts-dark-1280x820.png) | Ctrl+Shift+F8 truthfully "registered … does nothing until Computer Use is turned on"; Quill and Meeting Recording "Not assigned" with disabled controls |
| Dictionary | [09](ui-reference/winui-prompt9-2026-09-13/09-dictionary-dark-1280x820.png) | suggestions; long word ellipsised; threshold 85–98 % |
| Insights | [10](ui-reference/winui-prompt9-2026-09-13/10-insights-dark-1280x820.png) | "Total words captured" 2,230; 105 WPM; streaks 5/5; heatmap month/weekday labels separate |
| About | [11](ui-reference/winui-prompt9-2026-09-13/11-about-dark-1280x820.png) | temp-profile paths prove isolation; model cache shows the real path (D-04) |
| Settings — General / Dictation / Computer Use / Meetings / Appearance | [12](ui-reference/winui-prompt9-2026-09-13/12-settings-general-dark-1280x820.png), [13](ui-reference/winui-prompt9-2026-09-13/13-settings-dictation-dark-1280x820.png), [14](ui-reference/winui-prompt9-2026-09-13/14-settings-computeruse-dark-1280x820.png), [15](ui-reference/winui-prompt9-2026-09-13/15-settings-meetings-dark-1280x820.png), [16](ui-reference/winui-prompt9-2026-09-13/16-settings-appearance-dark-1280x820.png) | pinned header; Save and tab strip identical x on every tab; one-word permission statuses |
| Search | [17](ui-reference/winui-prompt9-2026-09-13/17-search-dark-1280x820.png) | singular/plural counts; same timestamp format as Meetings; highlighted match; long title ellipsised |

### 4.3 Narrow → wide restoration with page navigation (Light)

`scratchpad/p9r-resize.ps1`: six steps 1280 → 720 → 1280 → 1008 → 720 → 1280 DIP; at **each** step all nine rail destinations were navigated, then Timeline, then the rail width read from UIA (`MainNavigation`) and the window captured. ~60 navigations in total, on top of the 51 in §4.4.

| Step | Size (DIP) | Rail width (px / DIP) | Capture |
|---|---|---|---|
| 1 | 1280×820 | 335 / 268 (expanded) | [30-resize-step1](ui-reference/winui-prompt9-2026-09-13/30-resize-step1-1280x820-light.png) |
| 2 | 720×720 | 90 / 72 (compact) | [30-resize-step2](ui-reference/winui-prompt9-2026-09-13/30-resize-step2-720x720-light.png) |
| 3 | 1280×820 | 335 / 268 | [30-resize-step3](ui-reference/winui-prompt9-2026-09-13/30-resize-step3-1280x820-light.png) |
| 4 | 1008×800 | 295 / 236 (`MuesliSidebarWidthMedium`, `MainPage.xaml.cs:102`) | [30-resize-step4](ui-reference/winui-prompt9-2026-09-13/30-resize-step4-1008x800-light.png) |
| 5 | 720×720 | 90 / 72 | [30-resize-step5](ui-reference/winui-prompt9-2026-09-13/30-resize-step5-720x720-light.png) |
| 6 | 1280×820 | 335 / 268 | [30-resize-step6](ui-reference/winui-prompt9-2026-09-13/30-resize-step6-1280x820-light.png) |

Every wide step restored the expanded rail, full metric row and single-line toolbar; step 6 is pixel-equivalent in layout to step 1. The Prompt 1 "first-layout race" (compact rail at 1280) did **not** occur in any of this run's six launches.

**Window-state probe after the pass** (`scratchpad/p8-probe.ps1`, 14:02:39): dashboard `Visible=True Iconic=False Foreground=True 1600x1025`; **no `Pop-upHost` window of any size** after ~110 navigations, 5 Settings tab switches ×4 and several ComboBox-bearing pages — P8-06 stays fixed. The dashboard was never found minimized or hidden in this run (UNVERIFIED-08 / D-13 not reproduced).

### 4.4 Every route, Light, at 1280×820, 1008×800 and 720×720 DIP

Same 17 surfaces, fresh `%TEMP%\muesli-p9r-light` profile (`theme: light`), captured by `scratchpad/p9r-routes.ps1` at each size in one session (51 PNGs, `NN-<surface>-light-<size>.png`). A scripted audit read every PNG header: **70 of 70 files in the folder match DIP × 1.25 exactly** (1600×1025, 1260×1000, 900×900). Every capture was inspected (the 1008/720 sets as 2×2 contact sheets at 50 %).

- **1280×820** — identical hierarchy to Dark; semantic brushes flip correctly (no dark-only fills left behind). E.g. [01-timeline-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/01-timeline-light-1280x820.png), [07-models-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/07-models-light-1280x820.png), [10-insights-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/10-insights-light-1280x820.png).
- **1008×800** — expanded rail kept; Meetings actions wrap to two rows and stay reachable; detail title wraps; Settings tabs + Save stay pinned; Insights heatmap scrolls to the newest weeks. E.g. [03-meetings-light-1008x800.png](ui-reference/winui-prompt9-2026-09-13/03-meetings-light-1008x800.png), [04-meeting-detail-notes-light-1008x800.png](ui-reference/winui-prompt9-2026-09-13/04-meeting-detail-notes-light-1008x800.png), [14-settings-computeruse-light-1008x800.png](ui-reference/winui-prompt9-2026-09-13/14-settings-computeruse-light-1008x800.png).
- **720×720** — 72-DIP icon rail; Meeting detail stacks Save / Copy Transcript / Export / More actions as full-width rows (Delete still one step away); Settings value controls drop under their labels; Computer Use execution limits stack; no essential control clipped. E.g. [04-meeting-detail-notes-light-720x720.png](ui-reference/winui-prompt9-2026-09-13/04-meeting-detail-notes-light-720x720.png), [13-settings-dictation-light-720x720.png](ui-reference/winui-prompt9-2026-09-13/13-settings-dictation-light-720x720.png), [06-meeting-templates-light-720x720.png](ui-reference/winui-prompt9-2026-09-13/06-meeting-templates-light-720x720.png).

Hover highlights and revealed row actions visible on the first Timeline/Dictations row at 1008 and on "Ship." at 720 sit under the parked cursor after UIA navigation — the known synthetic-input pointer-over artifact, not a selection defect.

Remaining observations from this pass, none blocking (added to §8.3): in the 720-DIP compact rail the search well is ~40 DIP wide, so a typed query is not readable **in the box** (the Search page title echoes it: [17-search-light-720x720.png](ui-reference/winui-prompt9-2026-09-13/17-search-light-720x720.png)); the Settings card **right edge differs by tab** (≈1463 px General/Appearance, 1500 Meetings, 1507 Dictation, 1545 Computer Use) because each tab's `Auto` value column sizes to its widest control — header and Save no longer move (P5-03 stays fixed) but the cards do; Shortcuts' Push-to-Talk card still shows the gesture twice (header chip + row chip); Meetings keeps an `All / This PC` origin segment that Timeline dropped under P2-09.

## 5. Theme, keyboard, focus, and state coverage

### 5.1 Keyboard-only operation and focus visibility (Light, 1280×820)

Real key events (`SendKeys`) into the foreground dashboard, with a guard that **aborts if Muesli is not the foreground window** before every key (`scratchpad/p9r-keyboard*.ps1`); after each key the UIA `FocusedElement` was read, so reachability is measured, not assumed.

- **Tab order** is one cycle, top-to-bottom: collapse sidebar → search → Timeline → Dictations → Meetings → folder chevron → new folder → All Meetings → Archive → Notes (in Archive) → Twenty twenty-five → Product → Research → Notes (in Research) → Insights → Dictionary → Tweet → LinkedIn → Models → Shortcuts → Settings → About → page content (Refresh → filter segment → Sort) → the list as a single stop → back to collapse. **Every rail destination, including Settings/About below the fold at 820 DIP, is reachable**; the rail scrolls them into view (visible in [42-keyboard-content-focus-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/42-keyboard-content-focus-light-1280x820.png)). Shift+Tab reverses.
- **Accessible names disambiguate** the two folders named `Notes`: "Show Notes, in Archive, 1 meeting" vs "Show Notes, in Research, 1 meeting"; parents say "including subfolders"; All Meetings says "including meetings in no folder".
- **Focus visuals** are the high-visibility two-tone system rectangle on rail buttons, folder rows, toggles and buttons: [40-keyboard-focus-tab3-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/40-keyboard-focus-tab3-light-1280x820.png) (folder chevron), [40-keyboard-focus-tab9-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/40-keyboard-focus-tab9-light-1280x820.png) (Product folder), [43-keyboard-settings-unsaved-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/43-keyboard-settings-unsaved-light-1280x820.png) (toggle), [44-keyboard-settings-saved-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/44-keyboard-settings-saved-light-1280x820.png) (Save button). The focused sidebar search draws the textbox's own rectangle **inside** the highlighted well (42-…png) — two nested indicators; legible, recorded as polish.
- **Activation and a keyboard-only settings round trip:** Space on `NavSettings` opened Settings; Space on the Appearance tab switched it; Tab ×3 reached "Recording sounds"; Space toggled it → "Unsaved changes" badge appeared (43); Enter on Save → "Settings saved.", badge cleared (44), and the isolated `windows-settings.json` now holds `"soundEnabled": false` (written 14:05:02). Enter on the Timeline Sort `ComboBox` opened it and moved focus into its items; Tab closed it and moved on.
### 5.2 Long / empty / loading / error states, theme switch, secondary windows (Dark unless stated)

| State | Evidence | Result |
|---|---|---|
| Validation error | [20-dictionary-validation-error-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/20-dictionary-validation-error-dark-1280x820.png) | Save with empty phrase → red `InfoBar` + inline field message "Enter the word or phrase Muesli should recognize." (UIA text confirmed) |
| No results | [21-search-no-matches-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/21-search-no-matches-dark-1280x820.png) | "No results for "zzzznotarealquery"" empty card, 0/0 counts |
| Health warnings + no summary | [22-meeting-detail-warnings-nosummary-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/22-meeting-detail-warnings-nosummary-dark-1280x820.png) | "Recording warnings" card lists both warnings; "No summary has been generated … Nothing is invented when the transcript is empty."; folder "No folder" |
| Empty transcript | [23-meeting-detail-empty-transcript-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/23-meeting-detail-empty-transcript-dark-1280x820.png) | "This meeting has no transcript yet." with truthful Re-transcribe guidance; folder picker shows `Notes · in Archive` (hierarchy fix from Prompt 4 visible) |
| Audio present | [24-meeting-detail-audio-notes-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/24-meeting-detail-audio-notes-dark-1280x820.png) | Recording card with source picker, Play/Pause/Stop/Seek (UIA names confirmed), `0:00 / 0:03` for the real 3 s WAV, waveform. Playback itself not started (no audio output asserted). |
| Long transcript (90 lines) | [25-meeting-detail-long-transcript-dark-1280x820.png](ui-reference/winui-prompt9-2026-09-13/25-meeting-detail-long-transcript-dark-1280x820.png) | proportional face, speaker aliases applied (Alex/Jordan), scrolls inside the card |
| Clipboard busy (error) | §3.1, [51-…](ui-reference/winui-prompt9-2026-09-13/51-cr01-search-copy-clipboard-busy-dark-1280x820.png) | visible, recoverable error, shell survives |
| Loading | — | Not separately captured: every surface in this fixture loads in < 1.5 s, and forcing a slow load would need a fake delay (forbidden). Models Verify progress/cancel states were qualified by Prompt 6 (`winui-prompt6-2026-09-12/05-verify-progress-dark-1280x820.png`, `06-verify-cancelled-…`). |
| Insights share preview | UI test `Insights_share_preview_renders_the_generated_card` (passed, §2) | The menu-item route could not be driven by `winapp ui invoke` from outside the open split-button flyout; covered by the harness instead, not re-captured. |
| **Live theme switch, no restart** | [28a-theme-light-selected-unsaved-1280x820.png](ui-reference/winui-prompt9-2026-09-13/28a-theme-light-selected-unsaved-1280x820.png) → [28b-theme-switched-to-light-no-restart-1280x820.png](ui-reference/winui-prompt9-2026-09-13/28b-theme-switched-to-light-no-restart-1280x820.png) → [29-meetings-after-live-theme-switch-light-1280x820.png](ui-reference/winui-prompt9-2026-09-13/29-meetings-after-live-theme-switch-light-1280x820.png) | Dark → Light via Appearance picker + Save: whole shell repainted in the **same process (PID 9544)**; `"theme": "light"` persisted; "Unsaved changes" badge before Save, "Settings saved." after. |

**`ComputerUseConfirmationWindow` — rendered on screen for the first time in this project** (UNVERIFIED-03, half closed). Path: Settings → Computer Use → Allowed applications `notepad` → **Preview confirmation**. That command (`ViewModels/SettingsPageViewModel.cs:455-470`) only raises `ConfirmationRequested` through `Services/WinUiComputerUseContext.cs:70-78`; `App.ShowComputerUseConfirmation` (`App.xaml.cs:337-342`) opens the window, and Allow/Decline only set `Allowed` and close (`ComputerUseConfirmationWindow.xaml.cs:56-66`). No planner call, no action, no cloud request. Evidence: [27-computeruse-confirmation-preview-dark.png](ui-reference/winui-prompt9-2026-09-13/27-computeruse-confirmation-preview-dark.png) (700×650 px = 560×520 DIP, centred at 610,185 on the 1920 px display, foreground). Dark theme inherited; Decline has keyboard focus on open; risk chip + "Your approval is required" + planned-action preview. **Escape** (real `SendInput`) declined and closed it; the dashboard returned to foreground with focus restored to the invoking **Preview confirmation** button (`winapp ui get-focused`). Two non-blocking gaps (§8.3): the window still uses the **OS title bar in the system accent** (the P8-03 class Prompt 8 fixed for onboarding, not carried here), and the planned-action preview box clips its last line until scrolled.

**Empty library** (`%TEMP%\muesli-p9r-empty`, all collections `[]`): Timeline "No timeline activity yet" with 0/0/—/0 metrics; Dictations "No dictations yet"; Meetings "No meetings yet" with a Quick Note call to action; Dictionary "No custom dictionary entries yet" + Add new; Insights "Your activity will appear here" (no fabricated chart); Search "No results". WPM renders "—", not 0 or a fake value. Evidence: [60](ui-reference/winui-prompt9-2026-09-13/60-empty-timeline-dark-1280x820.png) [61](ui-reference/winui-prompt9-2026-09-13/61-empty-dictations-dark-1280x820.png) [62](ui-reference/winui-prompt9-2026-09-13/62-empty-meetings-dark-1280x820.png) [63](ui-reference/winui-prompt9-2026-09-13/63-empty-dictionary-dark-1280x820.png) [64](ui-reference/winui-prompt9-2026-09-13/64-empty-insights-dark-1280x820.png) [65](ui-reference/winui-prompt9-2026-09-13/65-empty-search-dark-1280x820.png). The record-dump sweep on this profile found none.

**First-run onboarding** (brand-new `%TEMP%\muesli-p9r-firstrun`, nothing seeded): "Set up Muesli" opened **foreground** at `1025x975 @447,22` px — bottom edge 997 on a 1020 px work area, so the Prompt 8 clamp holds; keyboard focus starts in "Your name"; custom title bar (P8-03 fix holds); dashboard behind at `1600x1020 @160,0`. Evidence: [70-onboarding-firstrun-step1-dark.png](ui-reference/winui-prompt9-2026-09-13/70-onboarding-firstrun-step1-dark.png). Steps 2–7 were qualified by Prompt 8 (`winui-prompt8-2026-09-12/02…14-*.png`) and by `First_run_profile_opens_the_onboarding_window` (§2); not re-walked. Observation: on a fresh profile the floating dictation indicator is visible at `270x60 @1634,480` (inside the display) and meeting detection starts and logs a scan by title **hash**, never the title text.

`MeetingLiveTranscriptWindow` remains **unrendered** — it needs an active meeting capture (microphone), which this run did not start. Its new CR-01 copy-failure flyout is therefore also unobserved.

- Very long dictation rows expose their **full text** as the list-item name (the fixture's 3× repeated sentence is read in full) — accurate, verbose; not changed.

## 6. Fresh logs

Every launch in this run wrote to its isolated profile's `logs\muesli-2026-09-13.log`. Scanned after the last manual launch (14:19) for `ERROR`, `Unhandled`, `XamlParseException`, `WARN`:

| Profile | Lines | Starts | ERROR | Unhandled | XamlParse | WARN |
|---|---|---|---|---|---|---|
| `muesli-p9r-dark` (reseeded 14:06; post-fix launches only) | 2 | 2 | 0 | 0 | 0 | 0 |
| `muesli-p9r-light` | 1 | 1 | 0 | 0 | 0 | 0 |
| `muesli-p9r-empty` | 1 | 1 | 0 | 0 | 0 | 0 |
| `muesli-p9r-firstrun` | 3 | 1 | 0 | 0 | 0 | 0 (INFO: "Meeting detection started", one scan by title hash) |

The **only** `ERROR` / `Unhandled WinUI exception` seen in this run was the deliberate CR-01 reproduction on the unfixed build at 13:31:38 (quoted in §3.1); that profile was later reseeded, so the line survives only in this report. No `ERROR` has been logged by any build containing the fix. The final foreground session's log is checked again in §13.

## 7. Baseline defect close-out (P1-01 … P8-07)

Final state was confirmed in the working tree (the anchor is the fix site, located by the defect-ID comment each prompt left) and against the owning prompt's evidence folder. "Fresh" links point at this run's captures (§4); where no fresh capture was taken, the earlier prompt's folder is cited. Paths are relative to `windows-native/Muesli.Windows.WinUI/` unless stated.

Legend: **Fixed** · **Not a defect** (baseline claim did not hold) · **Justified difference** (§8.1) · **Deferred** (§8.2/§8.3).

| ID | Final state | Fix / decision anchor | Evidence |
|---|---|---|---|
| P1-01 | Fixed | `App.xaml:299`, `MainPage.xaml.cs:420` | `winui-prompt1-2026-09-10/sidebar-search-focused-dark-1280x820.png`, `searchbox-focused-dark-1280x820.png` |
| P1-02 | Fixed (derived accent keys overridden) | `App.xaml.cs:279`, `OnboardingWindow.xaml.cs:86` | `winui-prompt1-2026-09-10/settings-appearance-dark-1280x820.png`, `textbox-focused-light-1280x820.png` |
| P1-03 | Fixed — work-area clamp on dashboard, onboarding and computer-use windows | `MainWindow.xaml.cs:107`, `OnboardingWindow.xaml.cs:267`, `ComputerUseConfirmationWindow.xaml.cs:208` | This run: launch rect measured `1600x1020 @160,0` on a 1020 px work area (§4) |
| P1-04 | Fixed — shared gutter `MuesliPageContentPadding` / `MuesliPageMetrics` | `App.xaml:29`, `App.xaml:97`, `MuesliPageMetrics.cs:11` | `winui-prompt1-2026-09-10/*-dark-1280x820.png` |
| P1-05 | Fixed (contiguous list + collapse control); collapse **persistence** deferred | `MainPage.xaml:98`, `MainPage.xaml:312`; non-persisted `_railCollapsed` `MainPage.xaml.cs:29` | `winui-prompt1-2026-09-10/rail-collapsed-light-1280x820.png` |
| P1-06 | Fixed — macOS "M" waveform geometry transcribed from `MWaveformIcon.swift` | `MainPage.xaml:62` | fresh route captures |
| P2-01 | Fixed — one `LibraryMetrics.AverageWordsPerMinute`; the **streak** half of the same class was still split and was fixed in this run (P9-02, §4.1) | `Muesli.Windows.Core/Insights/LibraryMetrics.cs:9`, `Services/WinUiLibraryContext.cs:339` | `winui-prompt2-2026-09-10/insights-metric-crosscheck-dark-1280x820.png` |
| P2-02 | Fixed | `Services/WinUiLibraryContext.cs:161`, `:301` | `winui-prompt2-2026-09-10/timeline-dark-1280x820.png` |
| P2-03 | Fixed | `Pages/TimelinePage.xaml:168` | `winui-prompt2-2026-09-10/timeline-longcontent-light-720x720.png` |
| P2-04 | Fixed — rows open the item | `Pages/TimelinePage.xaml:15`, `Pages/TimelinePage.xaml.cs:85` | `winui-prompt2-2026-09-10/timeline-row-opens-meeting-dark-1280x820.png` |
| P2-05 | Fixed — single sidebar search entry | `MainPage.xaml.cs:332`, `Pages/SearchPage.xaml:81` | `winui-prompt2-2026-09-10/search-research-dark-1280x820.png` |
| P2-06 | Fixed | `ViewModels/SearchPageViewModel.cs:37`, `:304` | fresh: `51-cr01-search-copy-clipboard-busy-dark-1280x820.png` reads "1 dictation · 0 meetings" |
| P2-07 | Fixed | `MainPage.xaml.cs:35`, `:334` | `winui-prompt2-2026-09-10/search-research-dark-1280x820.png` |
| P2-08 | Fixed — hover/focus-revealed actions | `Pages/DictationsPage.xaml:24`, `:201` | `winui-prompt2-2026-09-10/dictations-keyboard-actions-dark-1280x820.png` |
| P2-09 | Justified difference — single capture source (BACKEND-05) | `Pages/TimelinePage.xaml:102` | §8.1 |
| P2-10 | Fixed — flame path | `Pages/TimelinePage.xaml:50` | `winui-prompt2-2026-09-10/timeline-light-1280x820.png` |
| P3-01 | Fixed on Meetings by Prompt 3; **the same defect remained on Timeline, Dictations and both Dictionary lists and was fixed in this run (P9-04, §4.1)** | `ViewModels/LibraryPageViewModels.cs:1097`, `:1248` | UIA names checked by `Shell_navigates_every_destination_and_publishes_its_controls` (§2) |
| P3-02 | Fixed — folder editor collapsed | `Pages/MeetingsPage.xaml:208` | `winui-prompt3-2026-09-11/meetings-light-720x720.png` |
| P3-03 | Fixed — four header blocks | `Pages/MeetingsPage.xaml:19` | `winui-prompt3-2026-09-11/meetings-dark-1280x820.png` |
| P3-04 | Fixed — rule defined: folder = subtree; All Meetings = every meeting incl. uncategorised | `ViewModels/LibraryPageViewModels.cs:1202`, `:1267` | fresh: sidebar in `50-…png` reads All 5 / Archive 1 (Notes 1, Twenty twenty-five 0) / Product 2 / Research 1 (Notes 1) for the 5-meeting fixture with one uncategorised meeting |
| P3-05 | Fixed | `MainPage.xaml.cs:590`, `:658` | `winui-prompt3-2026-09-11/timeline-rail-counts-after-settings-save-dark-1280x820.png` |
| P3-06 | Not a defect — `PrintWindow` photographs an open ComboBox blank; value republish also hardened | `ViewModels/LibraryPageViewModels.cs:460` | `winui-prompt3-2026-09-11/meetings-nested-light-after-settings-roundtrip-1280x820.png` |
| P4-01 | Not a defect — synthetic-click pointer-over artifact | — | `winui-prompt4-2026-09-11/03a-selectorbar-after-synthetic-click-dark.png` vs `03b-selectorbar-after-real-pointer-path-dark.png` |
| P4-02 | Fixed | `Pages/MeetingDetailPage.xaml:541` | `winui-prompt4-2026-09-11/detail-transcript-dark-1280x820.png` |
| P4-03 | Fixed — Delete behind "…" menu | `Pages/MeetingDetailPage.xaml:231` | `winui-prompt4-2026-09-11/06-more-actions-menu-dark.png` |
| P4-04 | Fixed | `Pages/MeetingDetailPage.xaml:57`, `Pages/MeetingTemplatesPage.xaml:56` | `winui-prompt4-2026-09-11/templates-dark-1280x820.png` |
| P5-01 | Fixed — row routes to Dictionary | `Pages/SettingsPage.xaml:336`, `Pages/SettingsPage.xaml.cs:95` | `winui-prompt5-2026-09-11/03-settings-dictation-dark-1280x820.png` |
| P5-02 | Fixed on Settings/Shortcuts; **same class remains in Core catalog copy** (§8.2) | `ViewModels/ShortcutsPageViewModel.cs:42`, `:53` | `winui-prompt5-2026-09-11/11-shortcuts-light-1280x820.png` |
| P5-03 | Fixed — header fixed across tabs | `Pages/SettingsPage.xaml:138`, `Pages/SettingsPage.xaml.cs:133` | `winui-prompt5-2026-09-11/02-…`, `04-settings-computer-use-dark-1280x820.png` |
| P5-04 | Fixed — Sync tab removed; one contextual notice | `Pages/SettingsPage.xaml:283` | `winui-prompt5-2026-09-11/02-settings-general-dark-1280x820.png` |
| P5-05 | Fixed — shared 1040 cap | `App.xaml:120`, `Pages/SettingsPage.xaml:207` | `winui-prompt5-2026-09-11/07-settings-general-light-1280x820.png` |
| P5-06 | Fixed — detail moved to tooltip/HelpText | `Pages/SettingsPage.xaml:71` | `winui-prompt5-2026-09-11/02-settings-general-dark-1280x820.png` |
| P5-07 | Fixed — `*,Auto` columns | `Pages/SettingsPage.xaml:32`, `:433` | `winui-prompt5-2026-09-11/04-settings-computer-use-dark-1280x820.png` |
| P5-08 | Fixed | `Pages/ShortcutsPage.xaml:96` | `winui-prompt5-2026-09-11/12-shortcuts-dark-1280x820.png` |
| P5-09 | Justified difference (explicit Save + visible "Unsaved changes") — see §8.1 | `Pages/SettingsPage.xaml:152`, `ViewModels/SettingsPageViewModel.cs:184` | `winui-prompt5-2026-09-11/05-settings-failed-save-dark-1280x820.png` |
| P5-10 | Fixed — pinned header | `Pages/SettingsPage.xaml:138`, `Pages/SettingsPage.xaml.cs:65` | `winui-prompt5-2026-09-11/01-settings-meetings-scrolled-dark-1280x820.png` |
| P5-11 | Fixed (Browse affordance, status in card); the "empty template ComboBox" half was the `PrintWindow` popup artifact | `Pages/SettingsPage.xaml:722`, `:766`, `:866` | `winui-prompt5-2026-09-11/p5-11a-…closed-dark.png`, `p5-11b-…open-screencopy-dark.png` |
| P6-01 | Fixed — one card per family | `ViewModels/ModelsPageViewModel.cs:18` | `winui-prompt6-2026-09-12/10-models-dark-1280x820.png` |
| P6-02 | Fixed — ComboBox variant picker | `Pages/ModelsPage.xaml:36` | `winui-prompt6-2026-09-12/02-variant-switched-dark-1280x820.png` |
| P6-03 | Deferred — macOS marks are black wordmark lockups, illegible at 24-36 DIP and invisible in Dark | — | §8.3 |
| P6-04 | Fixed — `ModelFamilyList` resolves through UIA | `Pages/ModelsPage.xaml:164` | UI suite (§2) |
| P6-05 | Fixed | `Pages/ModelsPage.xaml:107` | `winui-prompt6-2026-09-12/10-models-light-1280x820.png` |
| P7-01 | Fixed | `Pages/InsightsPage.xaml:166` | `winui-prompt7-2026-09-12/insights-overview-dark-1280x820.png` |
| P7-02 | Fixed — was clipping, not a missing series | `Pages/InsightsPage.xaml:173`, `Pages/InsightsPage.xaml.cs:53` | `winui-prompt7-2026-09-12/insights-heatmap-meetings-dark-1280x820.png` |
| P7-03 | Fixed — dead `LibraryInfoPage` removed | `MainPage.xaml.cs:298` | source |
| P7-04 | Not a defect — 0.85 is the engine minimum | `Pages/DictionaryPage.xaml:367`, `ViewModels/DictionaryPageViewModel.cs:49` | `winui-prompt7-2026-09-12/dictionary-dark-1280x820.png` |
| P7-05 | Justified/deferred — Windows suggestion record has no count or source app; constant line removed rather than invented | `Pages/DictionaryPage.xaml:140` | §8.2 |
| P7-06 | Fixed — refresh + Share split button | `Pages/InsightsPage.xaml:42` | `winui-prompt7-2026-09-12/insights-overview-dark-1280x820.png` |
| P7-07 | Fixed | `Pages/AboutPage.xaml:53`, `Pages/AboutPage.xaml.cs:53` | fresh: `50-cr01-about-copy-clipboard-busy-dark-1280x820.png` |
| P7-08 | Fixed | `Pages/InsightsPage.xaml:110` | `winui-prompt7-2026-09-12/insights-overview-light-1280x820.png` |
| P7-09 | Not a defect — MOST-USED WORDS exists below the fold | `Pages/InsightsPage.xaml:284-318` | `winui-prompt7-2026-09-12/insights-most-used-words-dark-1280x820.png` |
| P8-01 | Fixed — indicator reacts to settings save | `DictationIndicatorWindow.xaml.cs:81` | `winui-prompt8-2026-09-12/15-dictation-indicator-idle-dark.png` |
| P8-02 | Fixed — scrollable with "more below" cue | `OnboardingWindow.xaml:129`, `OnboardingWindow.xaml.cs:137` | `winui-prompt8-2026-09-12/04-onboarding-step2-morebelow-dark.png` |
| P8-03 | Fixed — content extended into title bar | `OnboardingWindow.xaml:24`, `OnboardingWindow.xaml.cs:85` | `winui-prompt8-2026-09-12/03-onboarding-step1-dark-fixed.png` |
| P8-04 | Fixed | `OnboardingWindow.xaml:146` | `winui-prompt8-2026-09-12/11-onboarding-step1-light.png` |
| P8-05 | Partly fixed (repeated subtitles removed on steps 2-5); empty space on short steps deliberately kept | `OnboardingWindow.xaml:435`, `ViewModels/OnboardingViewModel.cs:210` | `winui-prompt8-2026-09-12/08-onboarding-step5-live-dictation-dark.png`; §8.3 |
| P8-06 | Fixed — it was the Page-level Escape accelerator tooltip, not a ComboBox leak | `MainPage.xaml:9-11`, `Pages/MeetingTemplatesPage.xaml:8-11` | `winui-prompt8-2026-09-12/16-p8-06-stranded-esc-tooltip-popuphost.png`; fresh window enumeration §4 |
| P8-07 | Not a defect | — | `winui-prompt8-2026-09-12/11-onboarding-step1-light.png` |
| TEST-01…04 | Fixed (Prompts 2/4) | `windows-native/Muesli.Windows.UITests/WinUiShellQualificationTests.cs`, `MuesliWinUiSession.cs` | UI suite (§2) |

## 8. Categorised remaining work

### 8.1 Windows platform differences (justified)

| Item | macOS | Windows | Justification |
|---|---|---|---|
| Source filter (P2-09, BACKEND-05) | All / This Mac / From iPhone | All / Dictations / Meetings | One capture source on Windows; a single-option source filter would be decoration. `Pages/TimelinePage.xaml:102` |
| Sync / iPhone history (P5-04, BACKEND-01) | iCloud tab | one contextual notice | No Windows backend; stated once where it applies. `Pages/SettingsPage.xaml:283` |
| Per-row app icon (BACKEND-06) | originating app icon | none | No focused-app capture on Windows. |
| Window chrome | traffic lights, unified toolbar | custom title bar with Windows caption buttons, Snap | Platform convention (shared contract). |
| Settings commit (P5-09) | immediate | explicit **Save** with a persistent "Unsaved changes" badge and pinned header | Changing ~40 fields to immediate commit alters save semantics and failure handling; the lost-edit risk the baseline named is closed by the badge + pinned Save (`Pages/SettingsPage.xaml:152`, `ViewModels/SettingsPageViewModel.cs:184`). Accepted as a Windows difference for this freeze; revisit only as a product decision. |
| Dictionary threshold range (P7-04) | 0.70–0.99 | 0.85–0.98 | The Windows engine clamps to that range (`Muesli.Windows.Core/Services/DictionaryCorrectionService.cs:10`, `ClampThreshold`); a wider slider would save values the engine discards. |
| Onboarding window size (P8-05) | per-step | one fixed size for 7 steps | Per-step resizing would move the title between steps; the tallest step already needs a scroll at this work area. |

### 8.2 Deferred core defects (real, outside UI scope)

| # | Defect | Anchor | Why deferred |
|---|---|---|---|
| D-01 | **`MuesliSettings` null-JSON fragility — a crash class.** A settings file with `"dictionarySuggestions": null` defeats the record initialiser and killed the shell on opening Dictionary (Prompt 7 fixed that one call site at `ViewModels/DictionaryPageViewModel.cs:284, 300`). Every non-nullable collection/string on `MuesliSettings` is exposed the same way. | `windows-native/Muesli.Windows.Core/Services/SettingsStore.cs:326` | Needs a deserialisation-layer normalisation in Core, not per-page guards. **Highest-priority core item.** |
| D-02 | Global crash policy: `App.UnhandledException` logs without `Handled`, so any future unguarded UI exception terminates the shell (CR-07; CR-01 was one live instance, now fixed). | `windows-native/Muesli.Windows.WinUI/App.xaml.cs:89-90` | App-wide policy decision. |
| D-03 | macOS-referencing copy renders on Windows model cards ("…current macOS catalog"). Same class as P5-02. | `windows-native/Muesli.Windows.Core/Services/TranscriptionModelCatalog.cs:98`, `:116`, `:219` | Core catalog strings, shared with other hosts. |
| D-04 | `--profile-root` does not isolate the model cache: `MuesliPathService.UserProfileDirectory` reads only `MUESLI_PROFILE_ROOT`, which packaged activation never inherits. Visible on About (fresh `50-…png`: Profile under `%TEMP%`, Model cache `C:\Users\madha\.cache\muesli`). | `windows-native/Muesli.Windows.Core/Services/MuesliPathService.cs:7-15` | Core path service. Test consequence: Missing/Download/Failed model states need the unpackaged host. |
| D-05 | Per-suggestion dictionary provenance ("Seen 1x \| app") — the Windows `DictionarySuggestion(Observed, Replacement)` record has no count or source app. | `Pages/DictionaryPage.xaml:140` | Needs occurrence counting + focused-app capture. |
| D-06 | Dictionary threshold default inconsistency: new entry defaults to **0.85** while the service default and accept-suggestion use **0.90**. | `ViewModels/DictionaryPageViewModel.cs:26`, `:109` vs `windows-native/Muesli.Windows.Core/Services/DictionaryCorrectionService.cs:7` | Metric/engine default — Prompt 7 was told to defer metric definitions. |
| D-07 | Sidebar collapse is session-only; macOS persists it. | `MainPage.xaml.cs:29`, `:97`, `:144` | Needs a new settings key (behaviour, not visual). |
| D-08 | Meeting you just left is not re-selected in the Meetings list after back-navigation. | `Pages/MeetingsPage.xaml.cs` (folder selection is restored at `:32-38`; meeting selection is not) | Behaviour polish with no owner after Prompt 3. |
| D-09 | Timeline nests a `ListView` per day group inside a `ListView`, disabling inner virtualisation. 127 dictations over 21 days scrolled correctly (Prompt 2); scaling risk for large libraries. | `Pages/TimelinePage.xaml:129-164` | Structural; needs a grouped `CollectionViewSource` rewrite. |
| D-10 | Strings are hard-coded; no `x:Uid` / `.resw` anywhere (CR-04). | whole tree | Whole-app globalization decision. |
| D-11 | Export is a single "full meeting" action; macOS offers Notes / Transcript / Full (`MeetingDetailView.swift:839-855`). | `Pages/MeetingDetailPage.xaml` export action | An export feature, not a UI defect. |
| D-12 | Unexplained self-hide of the floating dictation indicator (one pre-fix sighting in Prompt 8; `showFloatingIndicator` still true on disk). | `DictationIndicatorWindow.xaml.cs:170-174`, `:242` | Needs instrumentation; not reproduced since. |
| D-13 | UNVERIFIED-08 re-classified: *who minimizes the dashboard* (`-32000,-32000` / 199×34 with `IsWindowVisible=true` is the Win32 minimized signature). Nothing under the WinUI tree calls minimize. | probes `scratchpad/p8-probe.ps1`, `p8-watch.ps1` | See §4 for this run's observation. |

### 8.3 Optional visual polish (nonblocking)

- P6-03 vendor marks — needs properly sized monochrome marks, or an explicit decision to keep glyphs.
- CR-02 — migrate reflection `{Binding}` (Onboarding, Models, a few detail templates) to `x:Bind`.
- About status `InfoBar` renders below the diagnostics card, under the fold at 1280×820 (`Pages/AboutPage.xaml:437-441`) — a "Copied"/"Could not copy" result is only visible after scrolling.
- "Auto Notes" badge sits at the right edge of the title column at 1280 rather than beside the metadata (WinUI `Auto` column measurement) — correct at all sizes, looser at 1280.
- P8-05 — ~55 % empty space on short onboarding steps.
- MeetingDetail back link always reads "Back to Meetings" regardless of entry point.
- Insights heatmap scrolls horizontally at every tested width (newest weeks in view).
- Settings card right edges differ by tab (≈1463 / 1500 / 1507 / 1545 px at 1280×820) — each tab's `Auto` value column sizes to its widest control. Header, tabs and Save do not move (P5-03 holds).
- Shortcuts' Push-to-Talk card shows the gesture twice (header chip + row chip).
- Meetings keeps an `All / This PC` origin segment although Timeline dropped the single-source filter under P2-09.
- In the 720-DIP compact rail the search well is ~40 DIP wide, so a typed query is unreadable in the box (the Search page title echoes it).
- The focused sidebar search draws the TextBox's own focus rectangle inside the highlighted well — two nested indicators.
- `ComputerUseConfirmationWindow` still uses the OS title bar in the system accent (P8-03 class, fixed for onboarding only), and its planned-action preview box clips the last line until scrolled.
- MeetingDetail back link: visible "Back to Meetings", accessible name "Back to meetings".
- SearchPage "no query" copy (`ViewModels/SearchPageViewModel.cs:82-84`) is unreachable — the shell routes to Search only for a non-empty query.

### 8.4 Release qualification (not UI sign-off)

Explicitly **not** covered by this UI freeze: MSIX/installer signing and certificate trust; Velopack/installer smoke; real-device transcription accuracy and CUDA/CPU catalog qualification; cloud summary providers (OpenAI/OpenRouter), pyannote/HF first download; real meeting detection against live Zoom/Teams/Meet joins; real computer-use planner sessions; High Contrast and non-125 % DPI / multi-monitor qualification (§9); WPF retirement.

## 9. Unverified environments and risk

None of these was exercised in any prompt, including this one. **None is claimed to pass.**

| ID | Environment | Why not tested | Risk assessment |
|---|---|---|---|
| UNVERIFIED-01 | **High Contrast** (Aquatic, Desert, Dusk, Night sky) | Only reachable by switching the interactive desktop's contrast theme, which changes the user's live session. No app-only simulation exists. | **Medium.** Mitigation in source: colours are semantic `ThemeResource` brushes defined per dictionary in `Themes/MuesliTheme.xaml`, and a `HighContrast` dictionary maps the Muesli surface brushes onto `SystemColor*` brushes (`Themes/MuesliTheme.xaml:418-424`, alongside `Default` `:20`, `Dark` `:165`, `Light` `:286`); no hard-coded hex colours outside the theme (CR-05). Residual risk: custom-drawn surfaces that paint their own fills — the Insights heatmap cells, the hero gradient (`Pages/InsightsPage.xaml:110-118`), the rail brand-mark bars (`MainPage.xaml:62`), `PathIcon` metric glyphs — may not map to system colours, and focus visuals on templated `ListViewItem` rows are unobserved. Not a freeze blocker (no evidence of a defect) but **must be run before release qualification**, on a machine/session where changing the theme is acceptable. |
| UNVERIFIED-02 | DPI scales other than **125 %** (100 %, 150 %, 175 %, 200 %) | Single monitor at the user's real setting; resizing screenshots to fake DPI is forbidden. | **Low–medium.** All sizing is in DIPs; window placement/clamping uses `GetDpiForWindow` (`MainWindow.xaml.cs:103-181`). Highest-risk surfaces at 150 %+: onboarding (step 2 already scrolls at 125 %) and the 720-DIP compact layouts on a 1366×768 or 1280×720 150 % laptop, where the 1280×820 default is clamped. |
| UNVERIFIED-06 | Multi-monitor, DPI change while running (`AppWindow_Changed` DPI branch) | Single-monitor machine. | **Low–medium.** Clamp code runs on the current display's work area; cross-monitor drag re-clamp is unobserved. |
| UNVERIFIED-03 | `MeetingLiveTranscriptWindow` and `ComputerUseConfirmationWindow` rendered on screen; real tray-menu click | Need a live microphone capture or a real planner session; tray overflow flyout is not enumerable by UIA on Windows 11. Forbidden to trigger cloud/joins/computer actions for a screenshot. | See §4 for what this run could and could not render. Theme wiring, accessible names, Escape handling reviewed in source only. |
| — | Narrator / screen-reader end-to-end | Not run; UIA names/ids asserted by tests only. | Medium-low: names are asserted, announcement order and live regions are not. |

## 10. Corrections to the baseline and execution-prompts documents

**The execution-prompts document's "Current evidence" section (`docs/WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md:11-17`) was substantially wrong**, and future plans should not be written the same way. Of its "confirmed targets":

- "Settings fixed columns can shrink without expanding again" — **did not reproduce** (Prompt 0 and again Prompt 5 on the unmodified build). Prompt 5 removed the mechanism anyway (`*,Auto`).
- "Models subscribes to change events with no matching disposal" — **did not reproduce**; already disposed in the tree, and Prompt 6 measured 120 round trips with flat handles.
- Folder hierarchy/counts — counts **agreed** across surfaces; only the rule was undefined.
- Only the shortcut/preview copy and the generic brand mark reproduced as stated.

Baseline (`docs/WINDOWS_WINUI_UI_PARITY_BASELINE_2026-09-10.md`) entries that did not hold, all confirmed by later prompts:

- **P4-01** — synthetic-click pointer-over artifact, not a defect.
- **P3-06** and half of **P5-11** — `PrintWindow` does not render popups, and an open WinUI `ComboBox` empties its selection box, so a ComboBox photographed while open looks blank.
- **P7-02** — a clipping defect, not a missing meetings series.
- **P7-04** and **P7-09** — not defects.
- **P8-06** — not a ComboBox popup-host leak; it was the Page-level Escape accelerator's tooltip.
- **§1.5 item 2 / UNVERIFIED-08** — `AppWindow.Hide()` does **not** park the window at `-32000,-32000` / 199×34 with `IsWindowVisible=true`. It leaves the rect unchanged and clears visibility. The recorded signature is a **minimized** window, so every "dashboard hid itself" sighting was a minimize with an unidentified cause.
- **§8.3** — the WPF `MuesliUiSession` tests were "not run" in Prompt 0; they are part of this run's UI suite (§2).

Lessons for future evidence: never file a hover/selected-state defect after a teleporting synthetic click; never judge a popup-owning control from a `PrintWindow` picture; always use a DPI-aware capturing process; and never put `$h`/`$H`-style case-colliding variable names in PowerShell capture helpers — this run found that the previous Prompt 9 attempt's `SizeTo` helper overwrote the requested height with the window handle (PowerShell variables are case-insensitive), producing a 1600×1102 px window that would have been mistaken for a layout result. It was caught by the DIP×1.25 size check and the capture discarded.

## 11. UI exit criteria

| # | Criterion (`docs/WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md:93-97`) | Verdict | Evidence |
|---|---|---|---|
| 1 | **No confirmed blocking UI defects** — inaccessible essential actions, wrong-item operations, lost settings edits, unreadable content, clipped essential controls, broken navigation, unhandled UI exceptions | **Met — after this run's fixes.** This run found three confirmed blocker-class defects that earlier prompts had missed and fixed each with live before/after proof: **CR-01** (Copy kills the shell when the clipboard is busy — unhandled exception; §3.1), **P9-04** (18 list items announced as record dumps — P3-01 class; §4.1), **P9-02** (the same "streak" metric reading 6 vs 5 — P2-01 class; §4.1). No other blocker class was observed across 17 surfaces × 2 themes × 3 sizes, the resize pass, the keyboard pass or the state pass. | §3.1, §4.1–§4.4, §5 |
| 2 | **Every listed surface has current inspected screenshot evidence and its relevant interaction checks; unavailable environment checks identified and assessed** | **Met, with named exceptions.** Fresh inspected captures exist for every main route, all five Settings tabs, meeting detail (both tabs, warnings, empty, audio, long), templates, search (results / no match / empty library), empty library on five pages, first-run onboarding, and the Computer Use confirmation window (first render ever). Exceptions, each assessed in §9: `MeetingLiveTranscriptWindow` (needs a live capture), real tray-menu click, onboarding steps 2–7 (Prompt 8 evidence cited, not re-captured), High Contrast, non-125 % DPI, multi-monitor. | §4, §5, §9 |
| 3 | **Shared typography, colours, spacing, control hierarchy, responsive behaviour consistent; macOS differences resolved or justified** | **Met.** One gutter, one content cap (`App.xaml:120`), one accent, Light/Dark parity in the same session, compact/medium/expanded rail restored on every narrow → wide step. macOS divergences are listed with Windows justification in §8.1. Residual inconsistencies are cosmetic and listed in §8.3 (Settings card widths per tab, confirmation-window title bar). | §4.2–§4.4, §8.1, §8.3 |
| 4 | **Exposed features reflect actual availability; missing backend tracked separately; no placeholders or false-success UI** | **Met — after P9-03.** Shortcuts states which gestures are unassigned or inert; Computer Use capture rows say "Unavailable"; empty states invent nothing (WPM "—"); the failed-copy path now reports failure instead of silently dying. P9-03 removed the one untrue promise found ("Changes apply immediately" on an explicit-Save page). Still-true caveat: the Models page shows the **real** user model cache under an isolated profile (D-04) — accurate data, wrong isolation, tracked as core. | §4.1, §4.2, §5.2, §8.2 |
| 5 | **Build and relevant regression checks pass on current source** | _Pending UI runs 2–3_ — WinUI x64 and WPF 0/0; `Muesli.Windows.Tests` 840/0/5 on the final source (run 4); `Muesli.Windows.UITests` 20/20 on the CR-01 build (run 1). | §2 |

## 12. Freeze verdict

**The UI is ready to freeze. No concrete UI blocker remains open.**

That verdict is only true *because of* this run's fixes, and it should not be read as "Prompts 0–8 had finished the job": a code review plus a full-tree accessibility sweep turned up one shell-killing crash (CR-01), eighteen screen-reader debug dumps (P9-04) and one self-contradicting metric (P9-02) in surfaces earlier prompts had signed off. All three are fixed, rebuilt, and re-verified in the packaged host; the whole managed suite and the whole UI suite were rerun afterwards.

What the freeze does **not** certify (none of it is a UI blocker; each is tracked):

- **Unrendered / unexercised:** `MeetingLiveTranscriptWindow` (needs a live capture); a real tray-menu click; High Contrast; DPI other than 125 %; multi-monitor (§9). High Contrast should be run before release qualification.
- **Deferred core defects** (§8.2) — most importantly **D-01, the `MuesliSettings` null-JSON crash class**, and D-02, the global `UnhandledException` policy that turns any future unguarded UI exception into a process exit.
- **Optional polish** (§8.3) and **release qualification** (§8.4).

Recommended next step per the execution doc: stop UI work, move §8.3 to the nonblocking list, start core work with D-01 and D-02.

## 13. Final state

_Pending._
