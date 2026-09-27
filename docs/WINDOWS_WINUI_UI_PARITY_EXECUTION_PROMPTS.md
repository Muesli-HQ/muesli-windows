# Remaining WinUI 3 UI parity: execution prompts

Updated 2026-09-08. Execute prompts 0–9 in order. This replaces the **current-state diagnosis and sequencing** in `WINDOWS_WINUI_UI_PARITY_PLAN.md`; that older document remains historical reference.

## Objective and stopping point

Finish the Windows presentation and interaction parity pass, then return to core functionality. Match the macOS information hierarchy, density, typography, states, and workflows wherever Windows supports them. Keep Windows window controls, Snap, accessibility, and platform conventions. Do not recreate unsupported Apple integrations or add fake capabilities to make screenshots match.

UI sign-off is separate from removing WPF or certifying a production release. Signing, real-device transcription accuracy, cloud integrations, and other unfinished backend capabilities remain separate work. A broken UI invocation of an existing capability is in scope; implementing a missing engine is not.

## Current evidence

- WinUI already has Inter assets, shared theme resources, a custom navigation rail, grouped history, model cards, settings, and secondary surfaces. Do not restart the migration or repeat the old plan's initial styling work.
- Latest WinUI x64 build succeeded with zero warnings and errors. Earlier full managed tests passed 830, skipped 5; a subsequent focused run passed 96. Neither result certifies all later changes or current UI behavior.
- Most committed screenshot evidence is from August 28–29. Refresh it before judging parity.
- Confirmed targets: Settings fixed columns can shrink without expanding again; Models subscribes to change events with no matching disposal; shortcut/preview copy needs reconciliation with current integration; the shell uses a generic waveform mark; folder hierarchy and counts need consistency across surfaces.
- Recent working-tree changes include production-profile integration, credentials, meeting detection, computer-use integration, Markdown/PDF export, meeting automation controls, and nested folders. Treat their UI as unqualified until exercised. Do not assume a successful build proves these flows.

## Shared contract for every prompt

Read `AGENTS.md` and this document first. Work in the existing dirty tree without reverting, staging, committing, or pushing other changes. Load `$winui-design` and `$winui-dev-workflow`; use `$winui-wpf-migration` for migration boundaries, `$winui-ui-testing` for UI verification, and `$winui-code-review` before completion. Inspect relevant existing controls and `winapp find-ui` samples before authoring XAML. Use `$winui-packaging` only if packaging actually changes.

Use `docs/ui-reference/macos-current-2026-08-27/` screenshots as visual truth. Consult the original app at `C:\Users\madha\Downloads\muesli-main\muesli-main` for interactions absent from screenshots. WPF is a useful Windows comparison, not a substitute for the macOS reference. Keep existing service contracts and real data paths; use isolated fixtures only in a designated test profile, never in the user's library.

Keep shared visual resources in `windows-native/Muesli.Windows.WinUI/Themes/MuesliTheme.xaml`. Preserve automation IDs, accessible names, keyboard navigation, semantic theme brushes, and explicit binding update behavior. Use framework-neutral view models where practical. Do not migrate the custom rail to another navigation control solely for architectural preference.

For each prompt: inspect first; fix only evidenced gaps; build with `--no-restore`; test affected interactions; capture and actually inspect screenshots; report changed files, evidence, and remaining limitations. Do not claim unavailable tests passed. Do not change backend behavior or rewrite unrelated pages to finish a visual task. Record newly found backend defects separately unless a small fix is essential to an existing UI flow.

Use actual window sizes of 1280×820, 1008×800, and 720×720 DIPs when the display work area permits. Test narrow → wide restoration. Capture Light and Dark; qualify High Contrast and actual Windows scaling separately. Never simulate DPI by resizing a screenshot or accept screenshots clipped by the display/taskbar as layout evidence. If a display cannot support a case, record it as unverified.

After updates, visibly launch/foreground Muesli and inspect the fresh log slice, as AGENTS requires. Run the WinUI host through its supported packaged launch path; do not add `UseWPF` or disable packaging to bypass launch failures. Preserve the WPF fallback. No commit or push without explicit authorization.

## Prompt 0 — Establish the current baseline

> Finish the remaining WinUI UI parity work starting with qualification only. Read `docs/WINDOWS_WINUI_UI_PARITY_EXECUTION_PROMPTS.md` and follow its shared contract. Own the baseline evidence and UI test launch support; do not restyle pages yet. Inspect the existing working tree and current build outputs. Build WinUI x64 with no restore and launch the packaged host using `winapp run windows-native/Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj --no-restore --detach --json --property Platform=x64`. Ensure the automation harness uses the same supported host and an isolated profile: existing tests that only launch an unpackaged executable do not establish packaged behavior. Preserve explicit unpackaged coverage where relevant rather than weakening assertions. Capture every main route, settings tab, meeting detail, onboarding, and secondary window with deterministic test data. Compare against full-size reference images. Produce a dated, concise UI gap checklist with severity, screenshot links, and reproduction steps. Separate confirmed defects from unverified states and missing backend capabilities. Run existing relevant tests and record exact results. Finish with the app visibly open and a clean fresh startup log, or explicitly report the failure. Do not claim parity yet.

**Acceptance:** reproducible current screenshots and test launch path; prioritized differences; clear baseline that later prompts can compare against.

## Prompt 1 — Finish shared shell and visual resources

> Read the execution-prompts document and follow its shared contract. Own `MainPage.xaml`, its presentation code, and shared theme resources. Using Prompt 0 evidence and all macOS references, finish the navigation rail, brand mark using existing legitimate assets or code-native geometry, selected/hover/focus states, page header alignment, content widths, footer placement, typography, icons, cards, buttons, and input styles. Preserve the current rail architecture and Windows title-bar behavior. Confirm Inter actually renders. Resolve inconsistent accent and surface use through semantic resources, including Light, Dark, system theme, and High Contrast; inspect resource lookup before changing Default/Light/Dark dictionaries. Keep labels readable and navigation reachable at all target sizes. Test theme changes without restart, keyboard navigation, window dragging, caption controls, and narrow → wide restoration. Capture comparable shell screenshots. Stop when shared visual decisions are stable; do not restyle every page in this prompt.

**Acceptance:** one consistent visual foundation; no clipped navigation/footer or inaccessible focus; page prompts can reuse resources without inventing local palettes.

## Prompt 2 — Timeline, dictations, and search

> Read the execution-prompts document and follow its shared contract. Own TimelinePage, DictationsPage, SearchPage and their presentation view models. Compare `01-timeline.png` and `02-dictations.png`, and inspect original search behavior. Finish metric sizing, date group headers, row density, timestamps/duration, truncation, selected states, filters, and search result hierarchy. Preserve actual ordering, filtering, and service actions. Cover empty library, no matches, long content, multiple dates, selection, and large lists. Verify search activation and clearing, opening the correct item, copy/retry/delete where supported, and confirmation/cancellation with isolated test data. Keep actions keyboard accessible and ensure compact layouts do not hide essential metadata. Fix UI event lifetime issues only within these surfaces. Do not add analytics or search backend features.

**Acceptance:** history and search are visually consistent and existing actions operate on the intended real item; empty and populated states both qualify.

## Prompt 3 — Meetings list and folder navigation

> Read the execution-prompts document and follow its shared contract. Own MeetingsPage and folder presentation in the sidebar and relevant view models; coordinate any shared-resource changes with the existing design. Compare `03-meetings.png` and original folder interactions. Finish meeting row hierarchy, status/action placement, empty states, and folder creation/rename/move controls. Make nesting, labels, selection, and counts consistent between sidebar and page; explicitly define whether each count/filter includes descendants and use the existing hierarchy helper. Test nested folders, uncategorized items, long names, duplicate-name rules, move/rename cancellation, and the selected folder after navigation or refresh. Verify existing import/start controls show real progress and errors without adding recording-engine functionality. Use isolated data for destructive tests.

**Acceptance:** folder organization is coherent across surfaces; existing meeting actions remain reachable at narrow sizes and affect the intended items.

## Prompt 4 — Meeting detail, templates, and export dialogs

> Read the execution-prompts document and follow its shared contract. Own MeetingDetailPage, MeetingTemplatesPage, associated presentation view models, and export/dialog presentation. Use the original app and current evidence where reference PNGs do not cover detail. Finish transcript/summary hierarchy, title editing, metadata, speaker presentation, processing/error states, template selection/editor, and action grouping. Verify current Markdown and PDF export selections produce the requested format, cancellation leaves the meeting unchanged, and failures remain visible and recoverable. Exercise long transcripts, absent summaries, playback controls when audio exists, no-audio states, copy, and navigation back to the selected meeting. Verify focus restoration after dialogs. Do not introduce summarization, playback, or export backend features beyond fixes necessary for existing exposed actions.

**Acceptance:** detail and templates are readable, accessible, and stable with long/partial content; offered export formats are tested rather than inferred from picker labels.

## Prompt 5 — Settings and shortcut truthfulness

> Read the execution-prompts document and follow its shared contract. Own SettingsPage, ShortcutsPage and their presentation view models. Compare `05-shortcuts.png` and `09`–`11` settings references. Finish compact aligned setting rows, sections, toggles, selectors, password fields, and destructive-action grouping. Integrate recent provider-key, default-template, export-folder, and post-meeting hook controls into the same layout. Fix the confirmed Settings responsive-column bug so shrinking and expanding repeatedly restores the correct width. Reconcile stale shortcut and preview copy with the actual host and current registration state, including computer-use Ctrl+Shift+F8; do not present missing shortcuts as functional. Keep genuine platform limitations concise and contextual; move technical detail out of the normal flow where appropriate. Test edits followed immediately by Save without a focus change, persistence on reopening, masking of secrets, failed saves, selector state, packaged startup availability, disabled explanations, and keyboard operation. Do not run arbitrary user hooks or expose secrets in evidence.

**Acceptance:** settings reliably save and resize; every shortcut/capability label reflects actual behavior; new controls look integrated rather than appended.

## Prompt 6 — Models and operation states

> Read the execution-prompts document and follow its shared contract. Own ModelsPage and ModelsPageViewModel. Compare `04-models.png`. Finish card spacing, installed/selected indicators, metadata hierarchy, action placement, progress, failure, and cancellation states using the real model catalog. Fix the confirmed anonymous ModelChanged subscription lifetime so revisiting the page does not accumulate handlers or dispatch work to disposed views. Verify model selection is reflected after returning to the page and in settings, and that unavailable actions are explained truthfully. Test existing state transitions without unnecessary large downloads or deleting the user's models; use controlled isolated fixtures for visual states and distinguish these from actual download validation. Do not add new providers, model families, or inference engines.

**Acceptance:** consistent readable cards, honest operation states, stable repeated navigation, and persistent real selection behavior.

## Prompt 7 — Dictionary, insights, and informational pages

> Read the execution-prompts document and follow its shared contract. Own DictionaryPage, InsightsPage, AboutPage, LibraryInfoPage and their presentation view models. Compare `06-dictionary.png`, `07-insights-overview.png`, and `08-insights-detail.png`. Finish dictionary row/edit/validation layout and insights hierarchy, spacing, labels, chart or metric readability, and empty-history states. Verify add/edit/delete/cancel using isolated entries and confirm displayed metrics derive from existing calculations; do not invent unsupported metrics or dummy production values. Make About and library information consistent with the shell and current identity/profile behavior. Keep technical diagnostics accessible but secondary. Test long words, validation errors, keyboard-only use, theme contrast, and narrow layouts. Defer changes to metric definitions or data collection as core work.

**Acceptance:** secondary pages match the same visual system, remain useful with no data, and display accurate existing information.

## Prompt 8 — Onboarding and secondary windows

> Read the execution-prompts document and follow its shared contract. Own onboarding, recording/dictation overlays, detected-meeting prompts, computer-use confirmation, and other secondary-window presentation. Inspect actual implementations and original-app behavior before choosing controls. Finish sizing, alignment, status language, theme inheritance, accessible names, focus order, and screen-edge placement. Reconcile stale preview or isolated-profile wording with actual launch context. Verify cancel/close/back behavior, dialog focus restoration, dashboard reopening from the tray, and display scaling/monitor boundaries where available. Test existing meeting detection and computer-use UI with controlled inputs; do not trigger cloud requests, external joins, or computer actions merely to obtain screenshots. Distinguish a simulated presentation state from an end-to-end service test. Preserve close-to-tray and explicit exit behavior, and verify they do not strand visible modal windows. Do not redesign the underlying audio/detection engines.

**Acceptance:** no orphaned windows or inaccessible prompts; all secondary surfaces share the app theme and accurately communicate current operation state.

## Prompt 9 — Final UI qualification and freeze

> Read the execution-prompts document and follow its shared contract. Own final UI regression coverage and the parity report. Review the completed diff with `$winui-code-review`, resolve remaining UI blockers, and rerun the appropriate current build and tests. Exercise all routes and critical existing interactions in the supported packaged host, repeat narrow → wide resizing and page navigation, and inspect fresh screenshots against the references. Cover Light/Dark, keyboard-only operation, focus visibility, long/empty/loading/error states, and actual available DPI/High Contrast environments. Record untested environments honestly. Check fresh logs for errors and unhandled exceptions. Update the parity checklist with evidence and explicitly separate Windows platform differences, deferred core defects, optional visual polish, and release qualification. Do not keep polishing once the UI exit criteria below are met. Preserve WPF as a fallback, leave changes uncommitted, and finish with Muesli visibly foregrounded. Report whether UI is ready to freeze and name any concrete blockers.

## UI exit criteria

1. No confirmed blocking UI defects: inaccessible essential actions, wrong-item operations, lost settings edits, unreadable content, clipped essential controls, broken navigation, or unhandled UI exceptions.
2. Every listed surface has current inspected screenshot evidence and its relevant interaction checks; required unavailable environment checks are clearly identified and assessed before sign-off.
3. Shared typography, colors, spacing, control hierarchy, and responsive behavior are consistent; macOS differences are either resolved or explicitly justified by Windows behavior.
4. Exposed features reflect actual availability. Missing backend features are tracked separately with no placeholders or false-success UI.
5. Build and relevant regression checks pass, or a documented independent failure prevents an unconditional sign-off. Old green runs are not substituted for current verification.

Once these gates are met, freeze UI scope. Move remaining cosmetic preferences to a nonblocking list and resume core functionality. Keep WPF retirement, installer/signing qualification, and real-device/backend acceptance as separate milestones.
