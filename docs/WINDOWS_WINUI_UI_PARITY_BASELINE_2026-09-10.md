# WinUI UI parity baseline — 2026-09-10

Prompt 0 of `docs/WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md`. This is **measurement only**: nothing on any page was restyled and no gap below was fixed. Prompts 1–9 own the fixes.

Screenshots: [`docs/ui-reference/winui-baseline-2026-09-10/`](ui-reference/winui-baseline-2026-09-10/).
macOS truth: [`docs/ui-reference/macos-current-2026-08-27/`](ui-reference/macos-current-2026-08-27/).
The `docs/ui-reference/winui-*-2026-08-2[89]/` folders are **stale** — do not compare against them.

**Parity is not claimed.** This document is the starting line, not a verdict.

---

## 1. How this baseline was produced (reuse this exactly)

### 1.1 Environment facts measured during this run

| Fact | Measured value |
|---|---|
| Display | 1 monitor, 1920×1080 physical, **125 % scale** → 1536×864 DIP; work area 1536×816 DIP |
| .NET SDK | user-local `%LOCALAPPDATA%\Microsoft\dotnet` 10.0.400 (machine-wide `dotnet` is 8.0.424 and cannot build this repo) |
| WinUI TFM / output | `net10.0-windows10.0.26100.0`, `windows-native\Muesli.Windows.WinUI\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\` |
| WinUI x64 build | `Build succeeded. 0 Warning(s), 0 Error(s)` (21.7 s) |
| Packaged AUMID | `Muesli.WinUI.Preview_1z32rh13vfry6!App`; process name `Muesli.Windows.WinUI` |
| Profile used | isolated `--profile-root` under `%TEMP%`; the real `%APPDATA%\muesli` library was never opened (proof: `about-dark-1280x820.png` shows the temp profile paths) |

### 1.2 Build

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
Get-Process -Name Muesli.Windows.WinUI,Muesli -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build .\windows-native\Muesli.Windows.WinUI\Muesli.Windows.WinUI.csproj --no-restore -p:Platform=x64
```

### 1.3 Packaged launch against an isolated profile

```powershell
$root = "$env:TEMP\muesli-baseline-populated"     # seeded first, see 1.4
winapp run windows-native/Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj `
  --no-restore --detach --json --property Platform=x64 `
  --args "--profile-root `"$root`""
```

Returns `{"AUMID": "...", "ProcessId": <pid>}`. Preconditions: no `Muesli.exe` (WPF) and no `Muesli.Windows.WinUI` already running — `SingleInstanceCoordinator.AcquireForCurrentUser()` (`windows-native/Muesli.Windows.Platform/Services/SingleInstanceCoordinator.cs:38-44`) is keyed on the user SID only, so an isolated profile does **not** avoid the collision.

### 1.4 Deterministic data

The xunit harness seeds it in-process: `MuesliCleanProfile(seedPopulatedData: true)` — 6 dictations, 3 meetings, 3 folders, 4 dictionary entries, 3 suggestions, 3 templates (`windows-native/Muesli.Windows.UITests/MuesliCleanProfile.cs:198-273`).

For the by-hand captures in this baseline the same fixture was written to `%TEMP%\muesli-baseline-populated` by a scratch script mirroring those lines. **`MuesliCleanProfile.cs` remains the single source of truth** — later prompts should drive captures through the xunit harness (§1.6) rather than re-deriving the fixture.

### 1.5 Capture (the part that is easy to get wrong)

Two mistakes will silently corrupt every measurement:

1. **The capturing process must be DPI-aware.** A default PowerShell host is DPI-*unaware*, so `GetWindowRect`/`MoveWindow` operate in virtualised 96-DPI space and every size you read is wrong. Call `SetProcessDpiAwarenessContext(-4)` **before** any window call. Requested DIPs → physical pixels is `dip * GetDpiForWindow(hwnd) / 96` (×1.25 on this machine): 1280×820 DIP = 1600×1025 px, 1008×800 = 1260×1000 px, 720×720 = 900×900 px. Every PNG in this folder was verified against those numbers.
2. **The dashboard must actually be shown.** `MainWindow.HideDashboard()` is `AppWindow.Hide()` (`windows-native/Muesli.Windows.WinUI/MainWindow.xaml.cs:195`), which parks the HWND at `-32000,-32000` at 199×34 px while still reporting `IsWindowVisible = true`. Re-activating the AUMID (`Start-Process "shell:AppsFolder\Muesli.WinUI.Preview_1z32rh13vfry6!App"`) makes the primary instance call `ShowDashboard()` (`App.xaml.cs:104-107`) and restores the previous size.

Capture itself is `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`, which renders the full window even where the taskbar overlaps it, so a 820-DIP-tall window on an 816-DIP work area is still valid layout evidence. `PrintWindow` returns an **all-black bitmap for a WinUI window that has never been shown** — see UNVERIFIED-03.

Navigation between captures used `winapp ui invoke/click/set-value -a <pid>` against the automation ids listed in each page's XAML.

### 1.6 The automation harness — inspected, not rewritten

`windows-native/Muesli.Windows.UITests/MuesliWinUiSession.cs` already does everything Prompt 0 asks for, so **no harness change was made**:

- packaged host by default via `winapp run --no-restore --detach --json --property Platform=x64` (`MuesliWinUiSession.cs:100-183`);
- isolated profile passed as `--profile-root` because AUMID activation cannot inherit env vars (`:146-147`);
- explicit unpackaged coverage preserved in `LaunchUnpackaged(...)` (`:214-262`) and used only by the two tests that genuinely qualify no-package-identity behaviour (`WinUiShellQualificationTests.cs:260-283`, `:372-397`);
- refuses to run when a shell is already running (`:730-746`) and asserts profile isolation (`MuesliCleanProfile.cs:60-74`);
- DIP-correct resizing via `GetDpiForWindow` (`:68-89`), foreground enforcement before capture (`:399-414`), secondary-window lookup (`:280-311`), onboarding launch (`:271-273`).

Four harness *accuracy* defects were found and are filed as TEST-01…TEST-04 in §5 — they are wrong selectors and one fragile UIA query, not missing capability. All three test failures in §8.2 come from them, not from the product.

---

## 2. Coverage actually captured

| Surface | Dark 1280×820 | Light 1280×820 | Light 1008×800 | Light 720×720 |
|---|---|---|---|---|
| Timeline | ✅ | ✅ | ✅ | ✅ |
| Dictations | ✅ | ✅ | — | — |
| Meetings | ✅ | ✅ | ✅ | ✅ |
| MeetingDetail (Notes + Transcript) | ✅ | — | — | — |
| MeetingTemplates | ✅ | — | — | — |
| Search results | ✅ | — | — | — |
| Models | ✅ | ✅ | ✅ | ✅ |
| Shortcuts | ✅ | ✅ | — | — |
| Dictionary | ✅ | ✅ | ✅ | ✅ |
| Insights overview / detail / streaks | ✅ | ✅ (overview) | — | — |
| Settings — General/Sync/Dictation/Computer Use/Meetings/Appearance | ✅ (all 6) | ✅ (all 6) | ✅ (2) | ✅ (2) |
| About | ✅ | ✅ | — | — |
| Onboarding steps 1/2/3/5 | — | ✅ | — | — |
| Dictation indicator (idle) | — | ✅ | — | — |
| Empty-library Timeline (fresh profile) | ✅ | — | — | — |

Additional targeted captures in the same folder:

| File | What it shows |
|---|---|
| `settings-dictation-light-restore-{a-1280,b-720,c-1280}.png` | Settings narrow → wide restoration test (§4) |
| `settings-meetings-light-narrowfirst-{720,expanded-1280}.png` | Settings responsive test, narrow-first path (§4) |
| `settings-meetings-scrolled-light-1280x820.png` | Settings → Meetings below the fold (P5-10, P5-11) |
| `meetings-folder-count-mismatch-light-1280x820.png`, `sidebar-folder-counts-missing-light-1280x820.png` | Folder-count regression (P3-05, P3-06) |
| `firstrun-main-window-default-placement.png` | Empty-library Timeline + off-screen default placement (P1-03) |
| `final-state-foregrounded-1280x820.png` | Closing state (§9) |

Not captured: `LibraryInfoPage` (unreachable — defect P7-03), `MeetingLiveTranscriptWindow`, `ComputerUseConfirmationWindow` (UNVERIFIED-03).

---

## 3. Confirmed defects

Severity: **blocker** = breaks an essential action or makes content unreadable/wrong; **major** = clear parity or usability failure; **minor** = visible inconsistency; **polish** = cosmetic.

### Prompt 1 — shared shell and visual resources

| ID | Sev | Surface | Gap vs macOS reference | Evidence | Repro |
|---|---|---|---|---|---|
| P1-01 | major | Sidebar search box | While the search `TextBox` has focus it renders offset up/left inside its rounded container, its top edge is clipped, the magnifier glyph overlaps the caret position, and a full-width **teal** focus underline is drawn inside the pill. macOS has a flat inset field with no underline. Cause: `MuesliSearchTextBoxStyle` fixes `Height=32`/`Padding=0` on top of `DefaultTextBoxStyle`, which still draws its own 32-px template + focus underline (`windows-native/Muesli.Windows.WinUI/App.xaml:253-264`, used at `MainPage.xaml:91-97`). | [meeting-templates-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-templates-dark-1280x820.png), [meeting-detail-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-detail-dark-1280x820.png) | Launch packaged, click the sidebar search field, screenshot the sidebar. |
| P1-02 | major | Every toggle / text field | `ToggleSwitch` on-fill, `TextBox` focus underline and the onboarding `ProgressBar` render in the **Windows system accent (teal on this machine)** while buttons, selected `SelectorBar` items and links render Muesli blue. Two accents co-exist on the same screen; macOS is blue throughout. Root cause is resource lookup, not a missing palette: `Themes/MuesliTheme.xaml` overrides `SystemAccentColor` and `AccentFillColorDefaultBrush` in each theme dictionary (`:105-111`, `:219-225`, `:333-338`) but the framework's **derived** keys — `ToggleSwitchFillOnBrush`, `TextControlBorderBrushFocused`, `ProgressBarForeground` — are not overridden anywhere in the project and keep resolving against the OS accent. | [settings-appearance-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-appearance-dark-1280x820.png), [settings-general-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-general-light-1280x820.png), [onboarding-step2-permissions-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step2-permissions-light.png) | Settings → Appearance. Compare the "Recording sounds" switch fill with the "Save settings" button fill. |
| P1-03 | major | Default window placement | Default size is 1280×820 DIP (`windows-native/Muesli.Windows.WinUI/MainWindow.xaml.cs:21-22`) with no work-area clamping in `ResizeForDpi` (`:103-119`). On a genuinely fresh profile the window was placed at 160,160 px and sized 1600×1025 px, i.e. it extends to y=1185 on a 1080 px-tall screen — **105 px (84 DIP) below the bottom of the display**, 165 px below the work area. 820 DIP is already taller than this display's 816 DIP work area before any cascade offset. | measured window rect this run: `pos=160,160 size=1600x1025 px` (DPI-aware enumeration); [firstrun-main-window-default-placement.png](ui-reference/winui-baseline-2026-09-10/firstrun-main-window-default-placement.png) | Launch packaged with a brand-new `--profile-root`; read the main window rect from a process that has called `SetProcessDpiAwarenessContext(-4)`. |
| P1-04 | minor | Page headers | The page-title gutter is not shared. Measured with `winapp ui search` at a fixed 1600×1025 px window pinned to 0,0 (physical px, sidebar ends at 343): Meetings/Dictionary **475**, Timeline/Dictations/Settings **483**, Models/About **490**, Shortcuts **523** — a 48 px (38 DIP) spread. Baseline y also differs: 170 for most pages, 162 for Shortcuts and About. macOS uses one gutter for every page. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) vs [shortcuts-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/shortcuts-dark-1280x820.png) vs [models-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/models-dark-1280x820.png) | For each nav item: `winapp ui invoke Nav<X> -a <pid>` then `winapp ui search "<Page title>" -a <pid> --json` and read the `Text` match's `x`. |
| P1-05 | minor | Nav rail | The Models/Shortcuts/Settings/About group is bottom-pinned, leaving a ~150 px empty band under "Post on LinkedIn" at 820 DIP tall. macOS keeps the whole list contiguous. Also absent: the macOS sidebar collapse control. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) vs `macos-current-2026-08-27/01-timeline.png` | Any page at 1280×820. |
| P1-06 | polish | Brand mark | Generic blue waveform bars; macOS uses the Muesli waveform glyph + wordmark lockup. (Already listed in the doc's "Current evidence" — **reproduced**.) | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) | Any page. |

### Prompt 2 — Timeline, Dictations, Search

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P2-01 | blocker | Timeline metric cards | "avg WPM" reads **2** on Timeline, **63** on Dictations and **67 WPM** on Insights for the same library; "words dictated" reads **129** on Timeline vs **131** on Insights vs **63** on Dictations. Three independent computations: `LibraryPageViewModels.cs:45-48` (library snapshot, divides dictation words by *meeting* durations too), `LibraryPageViewModels.cs:285-292` (dictation-only), `InsightsPageViewModel.cs:146-162` (`InsightsWordAnalyzer`). macOS shows the same 139 WPM on Timeline and Dictations. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png), [dictations-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/dictations-dark-1280x820.png), [insights-overview-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-overview-dark-1280x820.png) | Seeded populated profile → Timeline, Dictations, Insights; read the metric cards. |
| P2-02 | major | Timeline rows | Every dictation row prints its text **twice** — once as the bold title, once as the secondary line. `WinUiLibraryContext.cs:161-169` passes `item.Text` as both `Title` and `Detail`. macOS prints it once. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) | Timeline with any dictation in history. |
| P2-03 | major | Timeline rows | Row titles are **hard-clipped with no ellipsis** at narrow widths: the title `TextBlock` sits in a horizontal `StackPanel` (infinite available width), so no trimming can apply (`Pages/TimelinePage.xaml:120-131`). The secondary line, which has `MaxLines="1"`, ellipsizes correctly — so the two lines of the same row truncate differently. | [timeline-light-720x720.png](ui-reference/winui-baseline-2026-09-10/timeline-light-720x720.png) | Resize to 720×720 DIP on Timeline; look at "Remember to schedule the customer researc". |
| P2-04 | major | Timeline rows | Rows are not activatable: `TimelineList` sets `SelectionMode="None"` and `IsItemClickEnabled="False"` (`Pages/TimelinePage.xaml:90-93`), so a meeting cannot be opened from the Timeline. macOS opens the item from this list. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) | Click any Timeline row — nothing happens. |
| P2-05 | major | Timeline / Dictations / Meetings / Search | Each page carries its **own** inline search box in addition to the always-present sidebar search, and on the Search page both boxes show the same query simultaneously. macOS has one search entry point. | [search-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/search-dark-1280x820.png) | Type in the sidebar search; the Search page renders a second populated field. |
| P2-06 | minor | Search page | Result count reads "0 dictations · **1 meetings**" (no singular form); result timestamps use `2026-09-10 10:00` while the Meetings list uses `10 Sept 2026 at 10:00:00 AM` for the same record. | [search-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/search-dark-1280x820.png) vs [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) | Search "planning". |
| P2-07 | minor | Search page | Entering search does not change the nav rail selection — "Insights" stayed highlighted while Search results were shown. | [search-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/search-dark-1280x820.png) | Navigate to Insights, then type in the sidebar search. |
| P2-08 | minor | Dictations rows | Every row shows a permanent red trash glyph and a copy glyph, plus a `parakeet-v3` model line per row. macOS reveals actions on hover and never prints the model per row. Destructive action is the most saturated element in the list. | [dictations-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/dictations-dark-1280x820.png) vs `macos-current-2026-08-27/02-dictations.png` | Dictations page, populated profile. |
| P2-09 | minor | Timeline filters | Filter set is "All / Dictations / Meetings / This PC(disabled)" plus a count and a sort combo; macOS is "All / This Mac / From iPhone" + Apps + sort icon. Different taxonomy — decide and align, or justify. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) | Timeline. |
| P2-10 | polish | Timeline metric icons | "day streak" uses a speech-bubble glyph (`&#xE7E7;`, `Pages/TimelinePage.xaml:28`); macOS uses a flame. Metaphor mismatch, and the orange fill is the lowest-contrast element in Light. | [timeline-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-light-1280x820.png) | Timeline. |

### Prompt 3 — Meetings list and folders

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P3-01 | blocker | Meetings list + folder list | List item accessible **Names are the raw record `ToString()`**: `"MeetingListItem { Id = ui-meeting-today, FolderId = ui-folder-product, Title = …, ShowSourceBadge = False, HasDuration = True }"` and `"MeetingFolderListItem { Id = ui-folder-product, Name = Product, Count = 1, Depth = 0, … }"`. A screen reader announces the whole debug dump. No `AutomationProperties.Name` on either container (`windows-native/Muesli.Windows.WinUI/Pages/MeetingsPage.xaml:253-265` and the folder `ListView` in the same file). | `winapp ui search "Product planning" -a <pid> --json` and `winapp ui search "MeetingFolder" -a <pid> --json` output recorded this run | Meetings page → `winapp ui search "Product planning" -a <pid> --json`; read `matches[0].name`. |
| P3-02 | major | Meetings page | The "Manage folders" `Expander` ships **expanded** — `IsExpanded="True"` directly contradicts the comment one line above it, "collapsed by default" (`Pages/MeetingsPage.xaml:194-196`). It consumes ~380 px above the list at 1280×820 and pushes the meeting list **entirely below the fold at 720×720**. macOS has no inline folder editor on this page. | [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png), [meetings-light-720x720.png](ui-reference/winui-baseline-2026-09-10/meetings-light-720x720.png) | Meetings page at 720×720 — no meeting row is visible without scrolling. |
| P3-03 | minor | Meetings header | Title + folder name + action row + caption + filter + search occupy 6 stacked rows before the list; macOS packs title, actions and caption into 2. | [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) vs `macos-current-2026-08-27/03-meetings.png` | Meetings page. |
| P3-04 | minor | Folder counts | Sidebar and in-page folder list agree (All Meetings 3 / Archive 0 / Product 1 / Research 1), but nothing states whether "All Meetings" includes uncategorised items — with this fixture 1 of 3 meetings is in no folder, so the folder rows sum to 2 while the parent reads 3. Define and label the rule. (Doc "Current evidence" item — **counts are consistent across surfaces; the descendant/uncategorised rule is still undefined**.) | [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) | Meetings page, populated profile. |
| P3-05 | major | Sidebar folder rail vs in-page folder list | The **sidebar counts disappear** while the in-page "MEETING FOLDERS" list still shows them. In one frame the rail shows `Archive` / `Product` / `Research` with no number at all (and `All Meetings`'s "3" at near-invisible contrast on the selected row) while the panel two hundred pixels to the right shows 3 / 0 / 1 / 1. The data is present — `winapp ui search "MeetingFolder"` reports `Count = 3/0/1/1` for all four rail items — so this is presentation, not data. Appeared after visiting Settings and did not return on navigation or `MeetingsRefreshButton`. This is the sharpest form of the doc's "folder counts need consistency across surfaces" target. | [meetings-folder-count-mismatch-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-folder-count-mismatch-light-1280x820.png), [sidebar-folder-counts-missing-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/sidebar-folder-counts-missing-light-1280x820.png); contrast with [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) captured earlier in the same session with counts present | Populated profile → Timeline (counts visible) → Settings → any tab → Save settings → back to Timeline/Meetings; rail counts are gone. |
| P3-06 | major | Meetings header | The time-range `ComboBox` (`MeetingsTimeRange`) renders **blank** after the same sequence — it read "All time" earlier in the session. A filter control with no visible value. | [meetings-folder-count-mismatch-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-folder-count-mismatch-light-1280x820.png) vs [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) | Same sequence as P3-05, then look at the third control in the Meetings action row. |

### Prompt 4 — Meeting detail, templates, export

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P4-01 | major | MeetingDetail | The `Notes`/`Transcript` `SelectorBar` loses its selected fill once `Transcript` is chosen — `Notes` renders as a solid blue pill when selected, `Transcript` renders as plain text with no pill. | [meeting-detail-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-detail-dark-1280x820.png) vs [meeting-detail-transcript-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-detail-transcript-dark-1280x820.png) | Open a meeting → click "Transcript". |
| P4-02 | minor | MeetingDetail | Transcript body renders in a **monospace** face; nothing else in the app does, and macOS uses the proportional UI face for transcripts. The editor also reserves ~340 px of empty height for a two-line transcript. | [meeting-detail-transcript-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-detail-transcript-dark-1280x820.png) | Open a meeting → Transcript tab. |
| P4-03 | minor | MeetingDetail | `Delete` is a fully saturated red filled button sitting in the same row and weight as `Save`; the destructive action is as prominent as the primary one. | [meeting-detail-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-detail-dark-1280x820.png) | Open any meeting. |
| P4-04 | polish | MeetingTemplates | Title/back-link gutters differ from the rest of the shell (back link at 390 px, title at 379 px) — see P1-04. | [meeting-templates-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meeting-templates-dark-1280x820.png) | Meetings → Manage Templates. |

### Prompt 5 — Settings and shortcut truthfulness

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P5-01 | major | Settings → Dictation | "Dictionary suggestions" is rendered as a settings **row with no control at all** — title + help text only (`Pages/SettingsPage.xaml:266-271`). It reads as a broken toggle. | [settings-dictation-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-dictation-dark-1280x820.png) | Settings → Dictation → "Dictionary suggestions" row. |
| P5-02 | major | Settings + Shortcuts copy | User-facing strings name macOS and Apple services: "Windows does not read focused-app text through **macOS Accessibility APIs**" (`SettingsPage.xaml:269`), "Cloud cleaner presets **from macOS** are not offered here" (`SettingsPage.xaml:311`), "Private text sync and iPhone history use **iCloud on macOS**…" (`SettingsPage.xaml:231`), "This **macOS command** does not yet have a shipping Windows global shortcut" (`ViewModels/ShortcutsPageViewModel.cs:28`). (Doc "Current evidence" item — **reproduced**.) | [settings-dictation-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-dictation-dark-1280x820.png), [settings-sync-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-sync-dark-1280x820.png), [shortcuts-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/shortcuts-dark-1280x820.png) | Settings → Dictation / Sync; Shortcuts page. |
| P5-03 | major | Settings header | The `Save settings` button and the whole tab strip **move horizontally when you switch tabs**, because the header tracks the current tab's content width. Measured at a fixed 1600×1025 px window (`winapp ui search`, physical px): Save button x = General **1333**, Sync 1327, Dictation 1315, Computer Use 1295, Meetings **1339**, Appearance **1113** — a 226 px (181 DIP) swing; the tab strip's first item moves 629 → 516 px over the same six clicks. | [settings-general-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-general-dark-1280x820.png), [settings-appearance-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-appearance-dark-1280x820.png), [settings-computer-use-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-computer-use-dark-1280x820.png) | Settings at 1280×820 → click each tab → `winapp ui search "SettingsSaveButton" -a <pid> --json` and read `x`. |
| P5-04 | minor | Settings → Sync | An entire tab whose only content is a "this is unavailable on Windows" notice referencing iCloud. Contract asks for platform limitations to be "concise and contextual" rather than a first-class destination. | [settings-sync-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-sync-dark-1280x820.png) | Settings → Sync. |
| P5-05 | minor | Settings (all tabs) | Content is capped at 840/880 DIP and left-aligned, leaving ~430 px of dead space on the right at 1280×820 while permission descriptions wrap to two lines inside the cap. macOS settings use the full content width. | [settings-general-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-general-dark-1280x820.png) | Settings at 1280×820. |
| P5-06 | minor | Settings → General | Permission rows carry 2-line technical paragraphs ("process-tree loopback … disclosed render-endpoint fallback"); macOS uses one-word status rows. | [settings-general-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-general-dark-1280x820.png) vs `macos-current-2026-08-27/09-settings-general.png` | Settings → General. |
| P5-07 | minor | Settings → Computer Use | Label/control pairs are split to opposite ends of an 880-DIP row (label at 414 px, control at 950 px), so the eye has to cross ~530 px of empty space. | [settings-computer-use-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-computer-use-dark-1280x820.png) | Settings → Computer Use. |
| P5-08 | minor | Shortcuts | The Push-to-Talk card shows its current gesture as plain text ("F8") top-right, while the other three cards show a chip; the same value is then repeated as a chip in the row below. | [shortcuts-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/shortcuts-dark-1280x820.png) | Shortcuts page. |
| P5-09 | minor | Settings | Every change requires an explicit `Save settings`; macOS commits immediately. Combined with tab switching this is a lost-edit risk that Prompt 5 must test explicitly. | [settings-general-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-general-dark-1280x820.png) | Settings → change a value → switch tab without saving. |
| P5-10 | major | Settings scrolling | The page header — title, tab strip **and the `Save settings` button** — scrolls out of view with the content. Once you reach the newer Meetings-tab controls there is no visible Save button and no indication of which tab you are on; you must scroll back up to commit. Combined with P5-09 this is a concrete lost-edit path. | [settings-meetings-scrolled-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-meetings-scrolled-light-1280x820.png) | Settings → Meetings → scroll to the bottom. |
| P5-11 | minor | Settings → Meetings (below the fold) | "Default notes template" renders as an **empty** `ComboBox` although the profile has three templates; "Export folder" is an empty field with no Browse affordance; the "Idle / Meeting detection is off." status text floats between sections outside any card. | [settings-meetings-scrolled-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/settings-meetings-scrolled-light-1280x820.png) | Settings → Meetings → scroll down. |

### Prompt 6 — Models

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P6-01 | major | Models cards | The Whisper family is split into **multiple sibling cards both titled "Whisper"** (Tiny English, Default: Small, …). macOS renders one Whisper card with a variant picker, matching the Parakeet card next to it. | [models-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/models-dark-1280x820.png) vs `macos-current-2026-08-27/04-models.png` | Models → Dictation category; scroll. |
| P6-02 | minor | Models cards | "Variant" renders as a flat chip with no chevron or pressed affordance, so it does not read as a picker; macOS uses an explicit popup button. | [models-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/models-dark-1280x820.png) | Models page. |
| P6-03 | minor | Models cards | Vendor identity is a generic Segoe glyph in a blue rounded square for every provider; macOS shows the Apple / NVIDIA / OpenAI marks. | [models-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/models-dark-1280x820.png) | Models page. |
| P6-04 | minor | Models automation | `winapp ui wait-for "ModelList"` fails after 8 s on every run even though `Pages/ModelsPage.xaml:145-147` declares the id — the `ItemsRepeater` does not surface its `AutomationId` through UIA. Any later prompt's Models assertions must use a different sentinel (`ModelsRefreshButton` works). | `capture-log-dark-1280x820.json` and the three light logs, all with `SENTINEL-MISSING ModelList` | `winapp ui wait-for "ModelList" -a <pid> -t 8000` on the Models page. |
| P6-05 | polish | Models header | "Model category" label is stacked above the segmented control and left-aligned; macOS puts the label inline to the left of the segments and centres the group. | [models-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/models-dark-1280x820.png) | Models page. |

### Prompt 7 — Dictionary, Insights, About, LibraryInfo

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P7-01 | major | Insights heatmap | The weekday label column has no vertical offset for the month-label row, so the first weekday label collides with the first month label and renders as **"MonSept"**. The weekday repeater (`Pages/InsightsPage.xaml:125-133`) is `VerticalAlignment="Top"` in the same grid row as the month repeater (`:134-142`). | [insights-overview-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-overview-dark-1280x820.png), [insights-streaks-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-streaks-dark-1280x820.png) | Insights → Daily activity, top-left of the grid. |
| P7-02 | major | Insights heatmap | Switching Daily activity to **Meetings** renders a completely empty grid even though the fixture has 3 meetings on 3 distinct days; the Words mode does light a cell. Needs confirmation of whether the meetings series is wired to the heatmap at all. | [insights-detail-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-detail-dark-1280x820.png), [insights-streaks-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-streaks-dark-1280x820.png) | Insights → Daily activity → "Meetings". |
| P7-03 | major | LibraryInfoPage | Unreachable dead route. `MainPage.Navigate` only maps it as the `_ =>` fallback for an unknown tag (`MainPage.xaml.cs:252-263`) and no nav item produces an unmatched tag, so the page can never be shown. It still publishes `PageMetrics`/`PageDetailList`. | `Pages/LibraryInfoPage.xaml` + `MainPage.xaml.cs:252-263` | Try to reach it from the rail — no entry point exists. |
| P7-04 | minor | Dictionary | The "Matching threshold" slider thumb sits at the far left while the value label reads "85 % match" — the thumb position and the label disagree. | [dictionary-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/dictionary-dark-1280x820.png) | Dictionary → "Add dictionary entry" panel. |
| P7-05 | minor | Dictionary suggestions | Every suggestion's secondary line is the constant "Pending dictionary correction"; macOS shows provenance ("Seen 1x \| ChatGPT"). | [dictionary-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/dictionary-dark-1280x820.png) vs `macos-current-2026-08-27/06-dictionary.png` | Dictionary page, populated profile. |
| P7-06 | minor | Insights header | Four unlabelled icon buttons (list / copy / save / refresh) flank "Share"; macOS shows refresh + Share only. | [insights-overview-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-overview-dark-1280x820.png) | Insights page. |
| P7-07 | polish | About | Content capped ~850 DIP and left-aligned, leaving a large right dead zone at 1280×820 (same root cause as P5-05). | [about-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/about-dark-1280x820.png) | About page. |
| P7-08 | polish | Insights hero (Light only) | The blue gradient overlay on "Your time with Muesli" is inset from the card bounds, so a hard vertical seam is visible where the gradient rectangle starts and the card's own fill shows through on the left. Not visible in Dark. | [insights-overview-light-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-overview-light-1280x820.png) vs [insights-overview-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-overview-dark-1280x820.png) | Insights in Light theme at 1280×820. |
| P7-09 | minor | Insights detail | macOS `08-insights-detail.png` has a **MOST-USED WORDS** section (two word-cloud cards for dictations and meetings). No equivalent was observed in the WinUI page down to the Streaks card. Whether it exists further down is **unverified** (UNVERIFIED-09). | [insights-streaks-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/insights-streaks-dark-1280x820.png) vs `macos-current-2026-08-27/08-insights-detail.png` | Insights → scroll past Streaks. |

### Prompt 8 — Onboarding and secondary windows

| ID | Sev | Surface | Gap | Evidence | Repro |
|---|---|---|---|---|---|
| P8-01 | major | Dictation indicator | Turning **Settings → Appearance → "Floating dictation indicator"** on and saving does nothing until the app restarts (or a dictation state change occurs). `DictationIndicatorWindow.UpdateState` is only invoked from the constructor, `ActualThemeChanged` and `Dictation.Changed` (`DictationIndicatorWindow.xaml.cs:52-60`); the show/hide decision reads `settings.ShowFloatingIndicator` at `:89`/`:129-139` but nothing re-runs it on a settings save. Verified: setting persisted (`showFloatingIndicator: true`) with the window still hidden; after restart the indicator rendered. | [secondary-dictation-indicator-idle-light.png](ui-reference/winui-baseline-2026-09-10/secondary-dictation-indicator-idle-light.png) | Settings → Appearance → toggle on → Save → observe no indicator; restart → indicator appears. |
| P8-02 | major | Onboarding step 2 | The permissions list is clipped by the fixed footer bar mid-row ("System audio" description cut) with no scroll affordance at the window's own 820×700 DIP default size. | [onboarding-step2-permissions-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step2-permissions-light.png) | Fresh `--profile-root` → Next once. |
| P8-03 | minor | Onboarding chrome | The onboarding window uses the **default system title bar** (system-accent fill when active, default app icon, visible title text) while the main window extends content into a custom title bar. The two windows of the same app look like different products. | [onboarding-step1-apptheme-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step1-apptheme-light.png), [onboarding-step2-permissions-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step2-permissions-light.png) | Fresh `--profile-root`; compare the two windows. |
| P8-04 | minor | Onboarding header | "SETUP · n OF 7" is printed twice on every step — once as the eyebrow label and once as a badge on the same line. | [onboarding-step1-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step1-light.png) | Any onboarding step. |
| P8-05 | minor | Onboarding step 5 | The step subtitle is repeated verbatim as the first line inside the card ("Record a short local test. It does not paste or add a history item."), and ~55 % of the window is empty. | [onboarding-step5-live-dictation-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step5-live-dictation-light.png) | Fresh `--profile-root` → Next ×4. |
| P8-06 | minor | Shell | A `Pop-upHost` window (62×50 px) was left visible at 1600,488 — outside the main window's right edge — after using a settings `ComboBox`. An orphaned popup host outside the owner window. | measured window enumeration during this run | Settings → Appearance → open the theme `ComboBox`, pick an item, enumerate the process's top-level windows. |
| P8-07 | minor | Onboarding theme | *Not a defect* — verified that the onboarding window **does** follow the app theme (dark profile → dark window, `theme: light` profile → light window). Recorded so Prompt 8 does not re-investigate. | [onboarding-step1-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step1-light.png) (dark) vs [onboarding-step1-apptheme-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step1-apptheme-light.png) (light) | Seed `windows-settings.json` with `"theme": "light"`, `"onboardingCompleted": false`. |

---

## 4. Status of the five "Current evidence" targets

| Target from the execution-prompts doc | Reproduced? | Evidence |
|---|---|---|
| Settings fixed columns can shrink without expanding again | **No — not reproduced.** Two paths tested at 125 % scale: (a) 1280 → 720 → 1280 on Settings → Dictation; (b) go narrow *first*, then open a never-realised tab (Meetings) at 720 and expand to 1280. Both restored `MinWidth`/column width to 375 px. Residual risk remains in `Pages/SettingsPage.xaml.cs:107-119`: discovery only registers a column whose width is still literally 360 px, so a grid first realised while narrow *after* the initial pass would never be registered — worth a targeted Prompt 5 test rather than an assumed fix. | [settings-dictation-light-restore-a-1280.png](ui-reference/winui-baseline-2026-09-10/settings-dictation-light-restore-a-1280.png) → [-b-720](ui-reference/winui-baseline-2026-09-10/settings-dictation-light-restore-b-720.png) → [-c-1280](ui-reference/winui-baseline-2026-09-10/settings-dictation-light-restore-c-1280.png); [settings-meetings-light-narrowfirst-720.png](ui-reference/winui-baseline-2026-09-10/settings-meetings-light-narrowfirst-720.png) → [expanded](ui-reference/winui-baseline-2026-09-10/settings-meetings-light-narrowfirst-expanded-1280.png) |
| Models subscribes to change events with no matching disposal | **No — already handled in the working tree.** `ModelsPageViewModel` subscribes at `windows-native/Muesli.Windows.WinUI/ViewModels/ModelsPageViewModel.cs:24` and unsubscribes in `Dispose()` at `:195-200`; `windows-native/Muesli.Windows.WinUI/Pages/ModelsPage.xaml.cs:14` calls `ViewModel.Dispose()` on `Unloaded`. No page sets `NavigationCacheMode`, so each navigation builds a fresh page + view model and handlers cannot accumulate. Prompt 6 should still confirm behaviour by navigating away/back repeatedly rather than by reading the code alone. | source inspection |
| Shortcut/preview copy vs actual integration | **Yes — reproduced.** See P5-02, P5-08. | [shortcuts-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/shortcuts-dark-1280x820.png) |
| Generic waveform brand mark | **Yes — reproduced.** See P1-06. | [timeline-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/timeline-dark-1280x820.png) |
| Folder hierarchy and counts need consistency across surfaces | **Partly.** Counts *agree* between the sidebar rail and the in-page folder list on this fixture; what is undefined is whether a parent count includes descendants/uncategorised items. See P3-04. The bigger meetings-page issue found is P3-02. | [meetings-dark-1280x820.png](ui-reference/winui-baseline-2026-09-10/meetings-dark-1280x820.png) |

---

## 5. Harness / test-accuracy defects (Prompt 0 scope, not fixed here)

These are wrong assertions in the existing WinUI qualification tests, not missing harness capability. They were found by driving the same surfaces by hand. **No code was changed** — Prompt 0's mandate was "extend/verify, do not rewrite", and both are one-line selector corrections that belong with the prompt that owns the surface.

| ID | Where | Problem |
|---|---|---|
| TEST-01 | `windows-native/Muesli.Windows.UITests/WinUiShellQualificationTests.cs:134-136` | After opening a meeting from search results the test requires `MeetingTranscriptEditor`. Meeting detail opens on the **Notes** tab; the transcript editor only exists once `MeetingTranscriptTab` is clicked. Verified by hand: `winapp ui wait-for "MeetingTranscriptEditor" -t 8000` fails immediately after opening the meeting and succeeds after clicking the Transcript tab. |
| TEST-02 | `windows-native/Muesli.Windows.UITests/WinUiShellQualificationTests.cs:118`, `:121`, `:181`, `:183` | **This is the cause of both test failures in §8.2.** The settings-tab steps call `SelectAccessibleName("Dictation")` / `("Meetings")`, but the `SelectorBarItem`s publish `AutomationProperties.Name="Settings: Dictation"` / `"Settings: Meetings"` (`windows-native/Muesli.Windows.WinUI/Pages/SettingsPage.xaml:132-137`). The bare name therefore matches some *other* element (the nav-rail "Meetings" button and the Timeline/Insights "Meetings" segments all publish it), the harness invokes that instead, and the Settings → Meetings tab never opens — so `MeetingModelPicker` is legitimately absent. Use the stable ids `SettingsDictationTab` / `SettingsMeetingsTab`. |
| TEST-03 | `windows-native/Muesli.Windows.WinUI/Pages/ModelsPage.xaml:145-147` | `ModelList` never resolves through UIA (see P6-04), so any future Models sentinel must not use it. |
| TEST-04 | `windows-native/Muesli.Windows.UITests/MuesliWinUiSession.cs:283-286` | `RequireSecondaryWindow` enumerates `AutomationElement.RootElement.FindAll(TreeScope.Children, …)` — a **desktop-wide** UIA query. On this machine that threw `COMException: Operation timed out (0x80131505)` and failed `First_run_profile_opens_the_onboarding_window` after 1 m 26 s (§8.2), even though the onboarding window demonstrably opens (captured by hand in §3, Prompt 8 rows). `WaitForShellWindow` (`:773-812`) already avoids this by enumerating HWNDs with `EnumWindows` and calling `AutomationElement.FromHandle`; secondary-window lookup should do the same. |

---

## 6. Unverified states — and why

| ID | What | Why it is unverified |
|---|---|---|
| UNVERIFIED-01 | **High Contrast** | Requires switching the interactive desktop's contrast theme, which this run did not do (it would change the user's live session). The harness explicitly records it as unexercised (`WinUiShellQualificationTests.cs:88-89`). Not simulated. |
| UNVERIFIED-02 | **Other DPI scales** | Only 125 % (the machine's real setting) was measured. Nothing was resized to fake another scale, per the shared contract. 100 %, 150 % and 200 % remain untested. |
| UNVERIFIED-03 | `MeetingLiveTranscriptWindow`, `ComputerUseConfirmationWindow` | Both need a live capture / a computer-use confirmation request, which this run declined to trigger for a screenshot. `PrintWindow` on a WinUI window that has never been shown returns an **all-black** bitmap (attempted and discarded), so there is no offline way to render them. Prompt 8 needs a presentation-only path or an explicit, consented live test. |
| UNVERIFIED-04 | `LibraryInfoPage` visuals | The route is unreachable (P7-03), so its rendering has never been observed. |
| UNVERIFIED-05 | Keyboard-only operation, focus order, focus visuals | Not exercised in this run beyond the harness's existing three-stop traversal assertion. Prompt 9 owns full keyboard qualification. |
| UNVERIFIED-06 | Multi-monitor / DPI-change-while-running (`AppWindow_Changed` DPI branch, `MainWindow.xaml.cs:96-101`) | Single-monitor machine. |
| UNVERIFIED-07 | Dictations / Insights / MeetingDetail / Search at 1008×800 and 720×720; Meeting detail and templates in Light | Not captured this run; see the coverage table in §2. |
| UNVERIFIED-08 | Whether the dashboard hiding itself mid-session is a defect | **Three times** during this run the main window went from shown to `AppWindow.Hide()`-parked (rect `-32000,-32000`, 199×34 px) with no close ever issued and nothing in the log. Re-activating the AUMID restored it at the same size each time; once restored it stayed visible under observation. The trigger was not isolated, so this is an observation, not a filed defect. `HideDashboard()` is reachable from `AppWindow.Closing` (`windows-native/Muesli.Windows.WinUI/MainWindow.xaml.cs:48-53`), `ComputerUse.CaptureStarted` (`windows-native/Muesli.Windows.WinUI/App.xaml.cs:130`) and the tray menu. Prompt 8 should bisect this — a dashboard that disappears on its own would be a blocker if reproducible. |
| UNVERIFIED-09 | Whether Insights has a "most-used words" section below the Streaks card (P7-09) | Not scrolled that far this run. |
| UNVERIFIED-10 | The exact trigger for P3-05/P3-06 (folder counts and time-range value disappearing) | Reproduced end-to-end, but the responsible step within "visit Settings → Save → navigate back" was not isolated. Prompt 3 should bisect it. |

---

## 7. Missing backend capabilities (record, do not fix)

| ID | What | Note |
|---|---|---|
| BACKEND-01 | Private text sync / iPhone history | iCloud-backed on macOS, no Windows backend. Currently surfaced as a whole Settings tab (P5-04). |
| BACKEND-02 | Quill global shortcut | No Windows global shortcut is registered; the UI says so truthfully but in macOS-facing wording (P5-02). |
| BACKEND-03 | Meeting-recording global shortcut | Same — no registered gesture; the page points at the Meetings page/tray instead. |
| BACKEND-04 | Computer Use screenshots / UI-text capture | Reports "Unavailable until scoped masking is qualified" on the Settings General permissions list and Computer Use tab. Truthful; the capability itself is unbuilt. |
| BACKEND-05 | "From iPhone" / cross-device source filter | The `This PC` segment is present but permanently disabled on Timeline/Dictations/Meetings because there is no second source. |
| BACKEND-06 | Per-row source/app attribution | macOS rows carry the originating app icon (WhatsApp, Chrome, Claude…). Windows has no equivalent capture, so rows show only a `PC` chip. |

---

## 8. Test results (this run, exact)

See §8.1 and §8.2. Nothing below is inferred from an earlier green run.

### 8.1 Managed test suite — `windows-native/Muesli.Windows.Tests`

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;" + $env:PATH
dotnet test .\windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj -c Debug --no-restore
```

```
Passed!  - Failed: 0, Passed: 834, Skipped: 5, Total: 839, Duration: 5 m 1 s - Muesli.Windows.Tests.dll (net10.0)
```

The 5 skips are the environment-gated real-audio / real-inference / operator-profile cases:
`Phase5FinalizationTests.RealMultiSpeakerAudioProducesDistinctAndStableRemoteIdentities`,
`Phase8MediaImportTests.DecodingIsDeterministicAcrossRepeatedReads`,
`Phase8MediaImportTests.EveryAdvertisedFormatDecodesRealSpeechToTheSourceDuration`,
`PersistenceCutoverImplementationTests.OperatorClonedProfileMigratesWithMatchingCountsRollbackAndSecondLaunchIdempotence`,
`Phase4LiveTranscriptionTests.QualifiedArtifactPerformsRealInferenceWhenQualificationPathIsProvided`.

### 8.2 WinUI UI automation — `WinUiShellQualificationTests`

```powershell
$env:MUESLI_UI_AUTOMATION = '1'
dotnet test .\windows-native\Muesli.Windows.UITests\Muesli.Windows.UITests.csproj -c Debug --no-build `
  --filter "FullyQualifiedName~WinUiShellQualificationTests" --logger "console;verbosity=normal"
```

```
Test Run Failed.
Total tests: 11
     Passed: 8
     Failed: 3
 Total time: 8.7022 Minutes
```

Nothing was skipped — the packaged host, the WinApp CLI and the unpackaged build were all present, so both `RequireUnpackagedBuild` tests ran for real.

**Passed (8)**

| Test | Time |
|---|---|
| `Meetings_page_hides_capture_and_recovery_panels_while_idle` | 31 s |
| `Second_process_redirects_and_the_primary_shell_stays_attached` (unpackaged) | 2 m 6 s |
| `Insights_share_preview_renders_the_generated_card` | 23 s |
| `Startup_registration_is_reported_unavailable_without_package_identity` (unpackaged) | 1 m 19 s |
| `Startup_registration_is_available_with_package_identity` | 17 s |
| `Shell_navigates_every_destination_and_publishes_its_controls` | 23 s |
| `Search_box_routes_the_shell_to_matching_local_history` | 23 s |
| `Theme_setting_switches_the_shell_between_light_and_dark` | 24 s |

**Failed (3)**

| Test | Time | Exact error | Diagnosis |
|---|---|---|---|
| `Populated_profile_captures_all_reference_states_and_secondary_surfaces` | 46 s | `Automation id 'MeetingModelPicker' was not found in the WinUI shell.` at `WinUiShellQualificationTests.cs:122` | **TEST-02** — the preceding `SelectAccessibleName("Meetings")` does not hit the Settings → Meetings tab. Test bug, not a product defect. |
| `Populated_profile_captures_all_reference_states_in_dark_and_light` | 37 s | `Refusing to capture 'winui-populated-dark-settings-meetings': page 'settings-meetings' did not publish sentinel 'MeetingModelPicker'.` at `WinUiShellQualificationTests.cs:184` | Same root cause (**TEST-02**). |
| `First_run_profile_opens_the_onboarding_window` | 1 m 26 s | `System.Runtime.InteropServices.COMException : Operation timed out. (0x80131505)` in `AutomationElement.FindAll` → `MuesliWinUiSession.RequireSecondaryWindow`, at `WinUiShellQualificationTests.cs:362` | **TEST-04** — desktop-wide UIA enumeration timed out. The onboarding window *does* open; it was captured by hand this run ([onboarding-step1-light.png](ui-reference/winui-baseline-2026-09-10/onboarding-step1-light.png)). Harness bug, not a product defect. |

Because all three failures are harness-selector/robustness bugs rather than product regressions, **the two `Populated_profile_*` tests never reached their later steps**, so the `winui-populated-*` and `winui-secondary-*` artifacts they normally emit are incomplete for this run. The screenshots in `docs/ui-reference/winui-baseline-2026-09-10/` were produced by the by-hand path in §1.3–§1.5 instead, and each was opened and visually inspected.

### 8.3 Not run

`MuesliUiSession`-based WPF tests (`MeetingDetailProductionTests`, `ProductionStartupSmokeTests`, `PhasePreviewIsolationTests`, `MuesliCleanProfileTests`) were not run: they drive the WPF `Muesli.exe`, which is outside this prompt's WinUI scope, and running them would have required stopping the WinUI shell repeatedly during capture. Prompt 9 should include them.

---

## 9. Final state

Muesli (WinUI, packaged host, isolated `%TEMP%\muesli-baseline-populated` profile) is **open and foregrounded** at 1280×820 DIP / 1600×1025 px on the Timeline page: [final-state-foregrounded-1280x820.png](ui-reference/winui-baseline-2026-09-10/final-state-foregrounded-1280x820.png). `winapp ui list-windows` reports `Muesli 1600×1025 isForeground=True`.

Fresh log slice — `%TEMP%\muesli-baseline-populated\logs\muesli-2026-09-10.log`, complete contents for this session:

```
[2026-09-10 00:17:04.821] INFO Muesli WinUI starting. AppVersion=0.3.0.0. Profile=…\muesli-baseline-populated.
[2026-09-10 00:38:09.466] INFO Muesli WinUI starting. AppVersion=0.3.0.0. Profile=…\muesli-baseline-populated.
[2026-09-10 00:58:12.320] INFO Muesli WinUI starting. AppVersion=0.3.0.0. Profile=…\muesli-baseline-populated.
```

No `ERROR`, no `Unhandled UI exception`, no `XamlParseException`. The developer's real `%APPDATA%\muesli` library was never opened or written by this run.

The WPF `Muesli.exe` was **not** running when this session started and was not started by it, so nothing needed restoring there. WPF remains intact as the fallback host; no `UseWPF` was added and packaging was not disabled anywhere.

Working tree: only additions. `docs/WINDOWS_WINUI_UI_PARITY_BASELINE_2026-09-10.md` (this file) and `docs/ui-reference/winui-baseline-2026-09-10/`. **No product or test source file was modified**, nothing was staged, committed, pushed or reverted.

### Recommended order for Prompts 1–8

1. **P2-01** (three different WPM/word totals) and **P3-01** (record `ToString()` as accessible name) are the two blockers.
2. **P1-02** (split accent) and **P1-04** (page gutters) unblock every page prompt, so Prompt 1 should land first.
3. **P3-05/P3-06** (vanishing folder counts, blank time-range) and **P5-10** (header scrolls away from Save) are the state bugs most likely to be mistaken for "works on my machine" — reproduce them with the exact sequences above before changing anything.
4. Fix **TEST-02** and **TEST-04** early: until then the two `Populated_profile_*` tests and the onboarding test cannot green, and later prompts will not be able to tell their own regressions from these.
