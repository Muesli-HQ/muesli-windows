# WinUI UI parity plan — 2026-08-28

> Historical plan. For current scope, evidence, and copy-ready remaining-work prompts, use [the September 8 execution plan](WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md). The diagnosis below predates substantial WinUI implementation.

Goal: the WinUI shell matches `docs/ui-reference/macos-current-2026-08-27` to the standard the WPF
host already reaches. This document is the working spec. Compare every screen against its full-size
PNG, not against the summaries here.

## Diagnosis

The WinUI shell is not "a few screens off". It was built on stock WinUI controls with a small brush
palette, while the WPF host carries a full design system that is what actually produces its macOS
resemblance.

| | WPF host | WinUI shell |
|---|---|---|
| `App.xaml` | **752 lines** | **107 lines** |
| Typography | Bundled **Inter** (`Assets/fonts/Inter-{Regular,Medium,SemiBold,Bold}.ttf`, ~1.6 MB) via `MuesliFont` | Stock Segoe UI Variable |
| Palette | 11 named colors + 20 brushes, layered `BackgroundDeep/Base/Raised/Hover` and `SurfacePrimary/Selected` | 15 flat brushes, different hex values |
| Retemplated controls | `MuesliTextBox`, `MuesliCheckBox`, `MuesliComboBox`, `MuesliTabControl`/`MuesliTabItem`, `MuesliListBoxItem`, ScrollBar | none |
| Button hierarchy | `PrimaryButton`, `SecondaryButton`, `GhostButton`, `ChromeButton` | none — stock WinUI gray |
| Shell chrome | Hand-built sidebar (`SidebarButton`, `SidebarChildButton`, `NavIcon`), custom window chrome | stock `NavigationView` + stock `TitleBar` |
| Component styles | `StatCard`/`StatValue`/`StatLabel`, `PageTitle`/`PageSubtitle`, `ContentCard`, `SectionLabel`, `SettingsRowLabel`, `EmptyState*` | `MuesliPageTitleStyle`, `MuesliSectionLabelStyle`, `MuesliBodyStyle`, `MuesliCardStyle` |

The WinUI shell's structure and workflows are largely complete; what is missing is the presentation
layer. That is good news for sequencing: styling is mostly leverage, not rewrite.

**Control inventory in the WinUI shell** (why styles beat per-page edits):

| Control | Instances |
|---|---|
| `Button` | 87 |
| `TextBox` | 25 |
| `ComboBox` | 20 |
| `ToggleSwitch` | 16 |
| `ListView` | 11 |
| `InfoBar` | 11 |
| `RadioButton` | 4 |
| `ProgressBar` | 4 |
| `GridView` | 3 |
| `Slider` | 2 |
| `NavigationView`, `SelectorBar`, `TitleBar` | 1 each |

One correct `Button` style closes 87 gaps. This is why Phase A comes first and everything else waits.

## Observed gaps

Recorded from the 2026-08-28 captures in `artifacts/ui-automation/` against the reference PNGs.

### Shell (every screen)

| # | macOS reference | WinUI today |
|---|---|---|
| S1 | Waveform brand mark beside "muesli" | Generic `FontIcon` glyph in a blue rounded square |
| S2 | Selected nav item is a **filled pill** with accent icon and primary-weight label | Stock `NavigationView` gray highlight with a left accent bar |
| S3 | Inter throughout; large light page titles | Segoe UI Variable |
| S4 | Search field with a leading magnifier glyph inside a rounded well | Stock bordered `TextBox`, no glyph |
| S5 | Meetings expands to folder children with right-aligned counts | Present, but stock `NavigationViewItem` children |
| S6 | "Spread the Word" group (Tweet, LinkedIn) | Absent |
| S7 | Nav split into primary group / share group / utility group (Models, Shortcuts, Settings, About) | Two groups via `FooterMenuItems` |
| S8 | No visible title bar text over the content | `TitleBar` showing "Muesli · WinUI preview" |

### Per screen

| # | Screen | macOS reference | WinUI today |
|---|---|---|---|
| P1 | Timeline | Segmented control (All / This Mac / From iPhone), Apps dropdown, sort icon | **Radio buttons** and a stock "Newest first" `ComboBox` |
| P2 | Timeline | `TODAY` / `YESTERDAY` date group headers; one card with divided rows; mono timestamp, title, `Completed`/`Mac` pills, right-aligned duration, summary line, hover row actions | Flat list, no grouping, no pills, no mono column |
| P3 | Meetings | Folders live in the **left nav**; content is title + inline action row | A **Folders panel inside the content area** with New/Save/Delete |
| P4 | Meetings | `+ Quick Note` is a blue filled primary; Import Audio / Manage Templates are secondary with icons; sort is `↑↓ Newest first` | All buttons identical stock gray |
| P5 | Meetings | Source segmented control | Absent |
| P6 | Models | Family cards: vendor logo, name, badge (`Recommended: Unified`), status chip (`Active` green / `Available` gray), **blue border on active**, variant dropdown + size, description, text-button action, red trash | Plain list with stock buttons |
| P7 | Models | `Model category` segmented control (Dictation / Live Meetings / Cleanup) | Stock role `ComboBox` |
| P8 | Insights | Hero card with blue-tinted gradient: huge total, then a 4-column divided strip (MEETINGS / AVERAGE PACE / CURRENT STREAK / LONGEST STREAK) | Plain stat tiles |
| P9 | Insights | Daily-activity heatmap with month columns, Mon/Wed/Fri row labels, QUIET→LOUD legend, Words/Meetings toggle | `GridView` heatmap without axis labels or legend |
| P10 | Insights | `‹ Back to Timeline` + `INSIGHTS / Private and on-device` eyebrow; range segmented; Share button | Range `ComboBox`, no eyebrow |
| P11 | Dictionary | Header row carries "Dictionary suggestions" toggle + Import + Export + `+ Add new`; one card with header block and divided rows; `from → to` with arrow glyph, `Seen 1x \| ChatGPT` secondary, blue ✓ / gray ✕ | Buttons stacked, separate suggestion list, stock editors |
| P12 | Settings | Centered segmented tab bar | `SelectorBar` (closest stock match; needs restyle) |
| P13 | Settings | Grouped **rows inside a card**: label left, control right, hairline dividers | `ToggleSwitch` with `Header` above the control |
| P14 | Settings | Permission rows: status dot, name, green `Granted` or blue `Grant` button, external-link icon | Plain list |
| P15 | Settings | Destructive pair: red text on tinted red fill, side by side | Stock buttons |

## Corrections found while implementing Phase A (2026-08-28)

Two assumptions in the plan below were wrong. Both are corrected here; the phase text keeps the
original shape so the reasoning stays visible.

**Overrides must be merged after `XamlControlsResources`, not declared alongside it.** Declaring
WinUI's own brush keys in `Application.Resources.ThemeDictionaries` compiles, runs, and silently
does nothing — the controls keep their stock chrome. Merged dictionaries resolve last-wins, so the
overrides live in `Themes/MuesliTheme.xaml`, merged immediately after `XamlControlsResources`. This
failure is invisible by eye on a dark theme; it was caught by sampling pixels out of a capture, and
that is the check to repeat whenever a brush override appears not to take.

**`NavigationView` and `SelectorBar` do not need replacing.** Both expose the theme resource keys
that carry the macOS look:

| Need | Keys |
|---|---|
| Filled nav pill, no left indicator bar | `NavigationViewItemBackgroundSelected`, `NavigationViewItemForegroundSelected`, `NavigationViewSelectionIndicatorForeground`, `NavigationViewSelectionIndicatorWidth/Height` = 0 |
| Filled segment, no underline pill | `SelectorBarItemBackgroundSelected`, `SelectorBarItemForegroundSelected`, `SelectorBarItemPillFill` = Transparent, `SelectorBarItemPillHeight/Width` = 0, `SelectorBarItemSpacing` = 0 |

Restyling keeps every automation id, the keyboard model, UI Automation, and High Contrast behaviour
that a hand-built sidebar would have had to re-implement. Phase B is therefore a restyle, and the
`SelectorBar` is how the segmented control is built rather than a custom pill switcher.

**Phase A status.** Inter bundled and applied app-wide; WPF palette ported; platform text, fill,
stroke, surface and accent brushes overridden; type ramp, button hierarchy, badge, settings-row and
segmented-container styles added; Timeline's radio buttons replaced with the segmented control
(P1). Verified by pixel sample — selected segment `#2F6FE0`, nav pill `#2E3340`, card `#1C1D20`.
Solution builds 0 warnings / 0 errors; `Muesli.Windows.Tests` 830 passed / 0 failed / 5 skipped;
WinUI qualification 7 passed / 0 failed.

**Still open from Phase A:** the `Default` theme dictionary key is an alias for Light, not a
fallback — the shell currently ships both `Default` and `Light` and one of them is redundant.

## Plan

Five phases. Phase A gates everything else.

### Phase A — Design system foundation

Port the WPF system rather than inventing a second one, so the two hosts cannot drift while both ship.

- **A1 Typography.** Add `Assets/fonts/Inter-*.ttf` to the WinUI project as `Content`; define
  `MuesliFontFamily` as `ms-appx:///Assets/fonts/Inter-Regular.ttf#Inter`; set it as the app-wide
  default so all 87 buttons and 25 text boxes inherit it. Verify the font actually resolves in the
  **unpackaged** run, which uses a different `ms-appx` root than the packaged one.
- **A2 Palette.** Replace the WinUI dark values with the WPF colors verbatim (`BackgroundDeep
  #111214`, `BackgroundBase #161719`, `BackgroundRaised #1C1D20`, `BackgroundHover #232528`,
  `SurfacePrimary #262830`, `SurfaceSelected #2E3340`, `AccentBlue #6BA3F7`, text `#EB/#9E/#66`
  white alphas, `BorderBrushSoft #12FFFFFF`, `BorderBrushMedium #1CFFFFFF`, semantic red/green/orange).
  Keep the existing Light and HighContrast dictionaries and derive light values from the same ramp.
- **A3 Type ramp.** `PageTitle`, `PageSubtitle`, `SectionLabel`, `Body`, `StatValue`, `StatLabel`,
  `RowTitle`, `RowSecondary`, `MonoTimestamp`.
- **A4 Control styles**, keyed and implicit: `PrimaryButton` (blue filled), `SecondaryButton`,
  `GhostButton`, `IconButton`, `DestructiveButton`; `MuesliTextBox` (+ leading-glyph variant),
  `MuesliComboBox`, `MuesliToggleSwitch`, `MuesliListView`/item, `MuesliCard`, scroll bars.
- **A5 New shared components** the WPF host does not have but macOS needs:
  `SegmentedControl`, `Badge` (green/blue/gray pill), `StatusChip`, `SettingsRow`
  (label + right-aligned control + divider), `CardHeader`, `DateGroupHeader`, `StatStrip`
  (divided multi-column stat row).

Exit: the shell renders in Inter on the WPF palette with no stock-gray buttons left, and
`WinUiShellQualificationTests` still passes unchanged.

### Phase B — Shell chrome

- **B1** Replace `NavigationView` with a hand-built sidebar mirroring `SidebarButton` /
  `SidebarChildButton` / `NavIcon`. Retemplating `NavigationView` to reach a filled-pill selection is
  more work than replacing it, and the WPF host already proves the replacement.
- **B2** Real brand mark from `Muesli.Windows/Assets/muesli_app_icon.png`.
- **B3** Search well with leading magnifier.
- **B4** Nav groups: primary / "Spread the Word" / utility.
- **B5** Title bar: match the WPF custom chrome; drop the "WinUI preview" subtitle.

Every existing `AutomationProperties.AutomationId` must survive (`NavTimeline`, `MainNavigation`,
`HistorySearchBox`, …) or the qualification suite breaks. Treat a broken id as a defect, not as
churn to absorb.

### Phase C — Per screen

One screen per change, each verified against its PNG: Timeline (P1, P2) → Dictations → Meetings
(P3–P5) → Meeting detail → Models (P6, P7) → Insights (P8–P10) → Dictionary (P11) → Shortcuts →
Settings (P12–P15) → About / Search / Templates.

Timeline first: it exercises the segmented control, badges, date grouping, and the divided list
card, so it validates most of Phase A's new components before they are reused nine more times.

### Phase D — Verification

- **D1** Extend `WinUiShellQualificationTests` to capture every screen at a fixed window size so
  captures are comparable run to run.
- **D2** Structural comparison against the reference: sidebar width, page-title size, card radius and
  border, accent hue on the selected nav item, segmented-control presence. **Not pixel equality** —
  these are different platforms and the reference is a different window size.
- **D3** Light theme for every screen; the reference set is dark-only, so light is judged on internal
  consistency against the same token ramp.
- **D4** HighContrast and 100/125/150/200 % DPI.

### Phase E — Close-out

Update `WINDOWS_MACOS_PARITY_MATRIX.md` and the ledger with what is verified and what is not. Only
then does WPF removal become discussable.

## Decisions needed before Phase C

1. **`Spread the Word`** — the macOS sidebar links to Twitter/X and LinkedIn. Ship the same two links
   on Windows, or drop the group? This affects B4.
2. **Source filter labels** — macOS shows `All / This Mac / From iPhone`. Windows has no iPhone
   history. The WinUI shell currently shows `All / This PC / Dictations / Meetings`, which mixes a
   source filter with a type filter. Options: keep `All / This PC` only, or keep the type filters and
   accept a structural difference.
3. **Window chrome** — match the WPF custom chrome, or keep the standard WinUI title bar? Custom
   chrome is closer to macOS but costs caption-button, snap, and accessibility work.

## Risks

- **Restyling depth.** WinUI control templates are larger than WPF's. `ComboBox`, `ToggleSwitch`, and
  `ListView` will each need a full template copy, not a setter override. Budget accordingly.
- **Automation ids.** Phase B rewrites the nav; the id contract is the thing that keeps the suite
  honest through the rewrite.
- **The architecture boundary.** New converters and visual helpers belong in pages or a WinUI-local
  namespace. `CoreAndViewModelsContainNoUiFrameworkTypes` fails the build if a view-model reaches for
  `Microsoft.UI.Xaml` — this already caught one regression during the Insights fix.
- **Unpackaged asset paths.** Fonts and images resolve differently unpackaged vs packaged; verify in
  both, not just in the run that is convenient.
