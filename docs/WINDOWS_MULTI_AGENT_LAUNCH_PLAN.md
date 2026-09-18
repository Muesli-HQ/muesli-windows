# Muesli Windows multi-agent launch plan

Review baseline: 2026-09-14, `codex/wave0-launch-foundation` at `a5414b3` with a 251-entry porcelain working tree (nothing staged). The pinned 10.0.400 SDK is still absent, so the mandatory commands cannot start. A fallback 10.0.401 WPF Release build is 0/0. The current 45-second blame-hang run completes **834 passed / 0 failed / 5 explicit prerequisite skips** before aborting in `SharedProjectsContainNoWpfReferences`; the retained 2026-09-13 final-source run completed **840 passed / 0 failed / 5 skips of 845** in 4m14s and the combined WPF/WinUI UI suite passed 20/20. L41 now passes its required 8 rounds × 12 concurrent exports and closes. CPU Parakeet dictation passes at RTF 0.117 and mic-only meeting at RTF 0.118; WER/CER remain unavailable. L48, L49, and new WinUI crash-hardening modules L50/L51 are open.

Authoritative product status remains `WINDOWS_LAUNCH_LEDGER.md`. Capability IDs and macOS behavioral source mappings remain in `WINDOWS_MACOS_PARITY_MATRIX.md`. This document is the execution and ownership plan: it does not replace either status source.

## Launch thesis

Muesli is not mainly behind because the native transcription engine is absent. The core application is broad, but the current working-tree Release suite is red and the latest short-audio CPU dictation benchmark narrowly misses the RTF gate. The gap is the final third of product work:

1. only 5 capability rows are **Complete and verified**, while 45 are **Implemented with verification debt**; the current working-tree Release suite does not finish under the two-minute per-test hang guard;
2. 11 rows are **Partial** and 1 is **Missing**;
3. several user-facing workflows still run through the legacy JSON `AppDataStore`, while the new SQLite repositories already contain stronger folder, search, follow-up, migration, and transactional behavior;
4. physical audio, real conferencing apps, paste targets, multi-monitor DPI, live providers, clean-machine installation, and signing cannot be proven by unit tests;
5. the highest-collision WPF integration files remain large, especially `FeatureRuntime.Meetings.cs`, `FeatureRuntime.xaml.cs`, `FeatureRuntime.Dictations.cs`, `FeatureRuntime.Models.cs`, and `FeatureRuntime.Settings.cs`;
6. the PerMonitorV2 manifest and CPU-only public-package disclosures are corrected in source; fixture-crypto updater verification is present; the retained package and prior clean rehearsals remain historical, and L48 still owns cross-run digest stability plus artifact retention.

The plan therefore separates implementation, integration, qualification, and release evidence. An agent does not get to mark a module complete merely because a service or button exists.

## Explicitly excluded work

Do not create modules, branches, UI promises, or placeholder implementations for:

- CAL-01 / API-02: Google Calendar OAuth, EventKit, Outlook, or MAPI calendar integration;
- STORE-01: Microsoft Store packaging;
- SUM-03: ChatGPT subscription OAuth;
- SYNC-01: CloudKit/iPhone/iCloud sync or an unapproved replacement backend;
- SYNC-02: audio synchronization;
- ARM-01: ARM64;
- OOS-01: Python, Electron, or web wrappers;
- OOS-03: literal ports of CoreML, ScreenCaptureKit, AppKit, Sparkle, or TCC.

Keep AUD-03 media pause/ducking parked until the product decision is made. Keep contribution telemetry parked under D4. Observation text/screenshots for CU-01 remain disabled until masking is separately approved and qualified.

## Definition of done

Every implementation module must deliver all of the following:

1. production code with no placeholder or fake success path;
2. success, failure, cancellation, and recovery tests proportional to the change;
3. honest UI state and diagnostic text;
4. no new secret, transcript, title, or path leakage in logs;
5. Debug and Release build/test passes with zero warnings and errors;
6. an updated ledger/parity row only after evidence exists;
7. a visible post-change Muesli launch and clean fresh log slice.

Qualification modules additionally require dated machine/provider/device evidence, exact commands, artifact hashes, and human reviewer identity where the qualification contract requires it.

## Multi-agent operating rules

### Shared-file lock

Only the designated integration owner for a wave may edit these collision-prone files:

- `Features/Runtime/FeatureRuntime.xaml.cs`
- `Features/Runtime/FeatureRuntime.About.cs`
- `Features/Runtime/FeatureRuntime.ComputerUse.cs`
- `Features/Runtime/FeatureRuntime.Dialogs.cs`
- `Features/Runtime/FeatureRuntime.Meetings.cs`
- `Features/Runtime/FeatureRuntime.Dictations.cs`
- `Features/Runtime/FeatureRuntime.Dictionary.cs`
- `Features/Runtime/FeatureRuntime.MeetingDetail.cs`
- `Features/Runtime/FeatureRuntime.MeetingsList.cs`
- `Features/Runtime/FeatureRuntime.Models.cs`
- `Features/Runtime/FeatureRuntime.Navigation.cs`
- `Features/Runtime/FeatureRuntime.Search.cs`
- `Features/Runtime/FeatureRuntime.Settings.cs`
- `Features/Runtime/FeatureRuntime.Shortcuts.cs`
- `Services/AppServices.cs`
- `Services/SettingsStore.cs`
- `MainWindow.xaml` and `MainWindow.xaml.cs`
- `App.xaml` and `App.xaml.cs`
- `Muesli.Windows.csproj`

The lock covers every `FeatureRuntime.*` partial. The L23 review crossed `FeatureRuntime.MeetingDetail.cs`, `FeatureRuntime.Meetings.cs`, and `FeatureRuntime.xaml.cs` for transcript editing and meeting-detail composition; the L27 review crossed the runtime composition boundary for the gated persistence cutover. Those reviews proved that a narrower list could permit conflicting wiring. This expands the shared-file boundary only; lane assignments remain unchanged.

Feature agents should first add focused services, models, views, and tests in owned files. The integration owner then wires them into shared runtime composition. This keeps four or five agents productive without repeatedly merging the same 1,000–2,000-line files.

### Branch and handoff rules

- One branch and one module ID per agent.
- No wholesale merges from the obsolete branches listed in the launch ledger.
- Rebase or merge the current integration branch before requesting review.
- Keep commits small enough that one behavioral claim can be reviewed independently.
- Every handoff includes: changed files, capability IDs, commands/results, remaining physical gates, screenshots when UI changed, and the fresh log byte range.
- Do not silently broaden a module. Open a new module ID when work crosses ownership boundaries.

### Review cadence

- Agent self-check after each coherent slice.
- Integration review after at most 3–5 changed production files or one new workflow.
- Full Debug/Release suite at the end of each wave.
- Package smoke at the end of waves that touch runtime assets, project metadata, installer, manifests, privacy copy, or release scripts.
- Human/physical qualification evidence is reviewed separately from code review.

### Behavioral parity packet

Before implementing a capability that exists on macOS, its owner must attach a short parity packet to the module handoff. Use the macOS paths already named in `WINDOWS_MACOS_PARITY_MATRIX.md`; do not rediscover or port Apple APIs. Record:

1. entry points and when the control is visible/enabled;
2. success flow and persisted state;
3. cancellation, timeout, denial, missing-resource, and retry behavior;
4. ownership rules for user edits versus generated data;
5. behavior across close/reopen, restart, crash recovery, and deletion;
6. progress, empty, degraded, and error UI;
7. keyboard/accessibility behavior and privacy/logging constraints;
8. the Windows-native equivalent and any deliberate divergence.

The highest-value achievable parity gaps are already known and are represented below: transcript edit/retranscribe qualification (L23), physical playback/device-loss qualification (L24), LM Studio/custom summary contract (L25), automatic PDF export (L31), linked follow-up meetings (L32), structured support export (L08), clean-profile folder qualification (L27/L28), and production signing/channel qualification (L05/L06). Nested folder persistence/move/delete, repository-backed search, asynchronous cached waveform loading, and fixture-crypto update-manifest verification have crossed their implementation gates. Literal Sparkle, ScreenCaptureKit, CoreML, AppKit, TCC, and EventKit designs remain excluded even when the user-visible behavior has a Windows equivalent.

## Portfolio overview

The modules below are deliberately smaller than phases. Most are one focused pull request or a short sequence of tightly related pull requests.

| ID | Module | Phase / capability | Type | Depends on | Primary owner lane |
|---|---|---|---|---|---|
| L00 | Ledger and baseline reconciliation | Phase 0 | Program control | — | Integration |
| L01 | Shared runtime boundary and persistence cutover plan | Phase 0 / continuous | Architecture | L00 | Integration |
| L02 | PerMonitorV2 manifest and window placement | Phase 12 / SHELL-01, FLOAT-01/02 | Fix + qualification | — | Shell/release |
| L03 | Package truth, native provenance, and license notices | Phase 13 / MOD-01, PKG-01, EXP-01 | Fix + release | — | Shell/release |
| L04 | Reproducible package and CI parity | Phase 13 / PKG-01, TEST-01 | Release engineering | L03 | Shell/release |
| L05 | Authenticode signing integration | Phase 13 / SIGN-01 | External-gated release | L04, D5 | Shell/release |
| L06 | Signed updater/channel implementation | Phase 13 / UPD-01, API-04 | Implementation | L04, L05 | Shell/release |
| L07 | Clean-machine install/upgrade/uninstall | Phase 13 / PKG-01 | Qualification | L04–L06 | Quality |
| L08 | Structured support bundle | Phase 13 / DIAG-01 | Implementation | L03 | Shell/release |
| L09 | GUI automation and accessibility harness | Continuous / TEST-01 | Test infrastructure | L01 | Quality |
| L10 | Seven-model CPU catalog qualification | Phase 1 / MOD-02 | Qualification | — | Models/runtime |
| L11 | CUDA provider packaging and NVIDIA qualification | Phase 1/13 / MOD-01, QUAL-01 | Implementation + qualification | L03 | Models/runtime |
| L12 | Model lifecycle destructive/UI qualification | Phase 1 / MOD-03 | Qualification | L10 | Models/runtime |
| L13 | Guided Qwen cleanup lifecycle | Phase 1 / MOD-05 | Implementation | approved GGUF | Models/runtime |
| L14 | Dictation human corpus and latency gates | Phase 2 / DIC-01, TXT-01, TXT-02 | Qualification | L10 | Dictation/audio |
| L15 | History, paste, and hotkey application matrix | Phase 2 / DIC-02, DIC-03, HOT-01, HOT-02, API-03 | Qualification | L14 | Dictation/audio |
| L16 | Microphone, Bluetooth, unplug, and privacy recovery | Phase 2/3 / AUD-01, AUD-02 | Qualification + fixes | — | Dictation/audio |
| L17 | Shared text-normalization policy | Phase 2/5/8 / TXT-03 | Implementation | product choice | Dictation/audio |
| L18 | Meeting capture and process attribution | Phase 3 / MTG-01, API-01 | Qualification + fixes | L16 | Meetings |
| L19 | Meeting lifecycle and crash recovery stress | Phase 3 / MTG-02 | Qualification + fixes | L18 | Meetings |
| L20 | Live ASR, VAD, gap recovery, and floating window soak | Phase 4 / MOD-04, LIVE-01, LIVE-02, LIVE-04, FLOAT-02 | Qualification + fixes | L16, L18 | Meetings/models |
| L21 | Finalization, diarization, and aliases | Phase 5 / DIA-01, DIA-02 | Qualification + fixes | L18 | Meetings |
| L22 | Detection, prompts, join actions, and auto-stop | Phase 6 / DET-01, DET-02, JOIN-01 | Qualification + fixes | L18 | Meetings |
| L23 | Transcript editing and safe retranscription | Phase 5/7 / MTG-03 | Implementation | L19, L21 | Meetings |
| L24 | Playback waveform and device-loss behavior | Phase 3/8 / PLAY-01 | Implementation + qualification | L18 | Meetings/library |
| L25 | Summary-provider parity | Phase 7 / SUM-01, SUM-02 | Implementation + qualification | — | Knowledge/workflows |
| L26 | Notes, templates, title ownership UX | Phase 7 / SUM-04, TPL-01, NOTE-01 | Qualification + fixes | L25 | Knowledge/workflows |
| L27 | SQLite runtime cutover and migration | Phase 8 / data foundation | Integration | L01 | Integration/data |
| L28 | Nested folder product UI | Phase 8 / ORG-01 | Implementation | L27 | Library/data |
| L29 | Full-content search and repository-backed results | Phase 8/12 / SEARCH-01 | Implementation | L27, L28 | Library/data |
| L30 | Media import format and cancellation qualification | Phase 8 / IMP-01 | Qualification + fixes | L10, L21 | Library/data |
| L31 | Export parity and automatic PDF export | Phase 8/9 / EXP-01, AUTO-01 | Implementation + qualification | L26 | Knowledge/workflows |
| L32 | Linked follow-up meeting workflow | Phase 9 / FOLLOW-01 | Implementation | L23, L27 | Knowledge/workflows |
| L33 | Post-meeting executable hook smoke | Phase 9 / HOOK-01 | Optional qualification | L31 | Knowledge/workflows |
| L34 | Onboarding, tray, and startup qualification | Phase 12 / ONB-01, TRAY-01, START-01 | Qualification + fixes | L02, L07 | Shell/release |
| L35 | Floating indicator and feedback-sound routes | Phase 2/12 / FLOAT-01, SOUND-01 | Qualification + fixes | L02, L16 | Shell/audio |
| L36 | Privacy deletion, retention, and cache cleanup | Every / PRIV-01 | Qualification + fixes | L12, L27 | Quality/security |
| L37 | Computer Use sandbox qualification | Phase 10 / CU-01 | Qualification | L09 | Quality/security |
| L38 | Local-only insights analyzer | Phase 12 / INSIGHT-01 | Optional implementation | D4 scope | Library/data |
| L39 | Final release-candidate qualification | Phase 13 / QUAL-01 | Release gate | all required modules | Integration/quality |
| L40 | Tracked Windows source reproducibility | Phase 0/13 / TEST-01, PKG-01 | Release integrity | L00 | Shell/release |
| L41 | Concurrent automatic Markdown export publication | Phase 9 / AUTO-01 | Fix + qualification | L31 | Knowledge/workflows |
| L42 | Per-meeting retranscription admission and deterministic candidates | Phase 5/7 / MTG-03 | Fix + qualification | L23 | Meetings |
| L43 | Automatic export manifest/current-render integrity | Phase 9 / AUTO-01 | Fix + qualification | L41 | Knowledge/workflows |
| L44 | Immutable persistence migration snapshot plan | Phase 8 / data foundation | Integration | L27 | Integration/data |
| L45 | Exact-SDK release reproducibility | Phase 13 / PKG-01, TEST-01 | Release integrity | L04 | Shell/release |
| L46 | Recovered retranscription stale-flight ownership | Phase 5/7 / MTG-03 | Fix + qualification | L42 | Meetings |
| L47 | Protected cutover staging and coherent multi-file capture | Phase 8 / DATA-01, ORG-01, QUAL-01 | Fix + qualification | L27 | Integration/data |
| L48 | Cross-environment release digest stability and rehearsal-artifact retention | Phase 13 / PKG-01, TEST-01 | Release integrity | L04, L45 | Shell/release |
| L49 | Shared Core/Platform extraction and WinUI shipping-shell qualification | Phase 12/13 / SHELL-01, ONB-01, TRAY-01, START-01, TEST-01, PKG-01 | Migration + qualification | L01, L02, L09, L34, L45 | Shell/release |
| L50 | Settings deserialization normalization | Phase 12 / SHELL-01, TEST-01 | Crash fix + qualification | L49 | Shell/release |
| L51 | WinUI unhandled-exception containment policy | Phase 12 / SHELL-01, TEST-01 | Crash policy + qualification | L49 | Shell/release |
| L52 | WinUI test migration and Release-build parity | Continuous / TEST-01, PKG-01, MOD-04 | Test/release integrity | L49 | Shell/release |

## Module specifications

### L00 — Ledger and baseline reconciliation

Scope:

- record `2972192` as the current committed review baseline and retain `efa961c` as the Wave 0 integration point;
- remove stale “uncommitted” and obsolete-current-branch wording;
- keep ORG-01/SEARCH-01 explicit about the stronger SQLite substrate versus the production JSON-backed UI;
- record PerMonitorV2 as implemented while leaving SHELL-01 at **Implemented with verification debt** until physical DPI proof exists;
- keep statuses synchronized between the ledger and parity matrix;
- record the integrated Debug/Release build, Release suite, focused L23/L27, elevated UI, benchmark, and release-rehearsal evidence without promoting human/provider/package gates.

Exit gate: document-only diff passes status-vocabulary checks and names the exact evidence commit.

### L01 — Shared runtime boundary and persistence cutover plan

Scope:

- freeze shared-file ownership for each wave;
- define adapters between `FeatureRuntime` and repositories instead of injecting more behavior into the central runtime files;
- write the JSON-to-SQLite cutover sequence, rollback path, backup ownership, and schema-version rules;
- identify which existing `AppDataStore` calls must move to dictation, meeting, folder, template, and search repositories.

Exit gate: an integration design plus characterization tests that prove current data-loading, saving, ordering, and selection behavior before L27 changes persistence.

### L02 — PerMonitorV2 manifest and window placement

Owned files: `app.manifest`, `Muesli.Windows.csproj`, `WindowPlacementService.cs`, `verify-phase12-ui.ps1`, DPI-focused tests and documentation.

Scope:

- embed `dpiAwareness=PerMonitorV2, PerMonitor` and the legacy `dpiAware=true/pm` fallback;
- extract and assert the built executable manifest in an automated test;
- verify dashboard, onboarding, toast, meeting prompt, and live transcript placement;
- capture 100/125/150/200% evidence and at least two physical monitors where available.

Exit gate: extracted manifest assertion, 352-cell validator pass, physical DPI captures, no clipping, no off-screen restore, and clean fresh logs.

### L03 — Package truth, native provenance, and licenses

Owned files: `THIRD-PARTY-NOTICES.md`, `licenses/`, package metadata generation, package structure tests.

Scope:

- correct the false CUDA-included statement;
- inventory every native DLL and identify the source NuGet/upstream version and applicable license;
- assert public CPU-only packaging and reject incomplete/unmanifested CUDA bundles;
- confirm QuestPDF community-license eligibility with the release owner;
- ensure package README, metadata, UI diagnostics, and notices say the same thing.

Exit gate: a generated native-runtime inventory in the release report, complete license files, and package tests that fail on disclosure/runtime disagreement.

### L04 — Reproducible package and CI parity

Scope:

- make local and CI build the same Release package from the pinned SDK and release identity;
- upload ZIP, installer, TRX, package-smoke report, manifest extraction, hashes, and native inventory;
- build the Inno installer in CI or explicitly move it to a separate signed-release workflow;
- reject dirty/uncommitted release inputs unless deliberately overridden and recorded;
- add a one-command non-signing release rehearsal.

Exit gate: two clean builds produce matching content inventories and all CI artifacts needed for review. The prior clean rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; the historical L41 rehearsal matched digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`; the historical exact-SDK rehearsal at `d432a26` matched digest `049c3c34f71dc71b60447743d897e6105255d67cc83cc584c8e6c0d526173de4`; and the final clean exact-SDK rehearsal at `a5414b3` matches digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf`. L04 is complete as a module with no L04-specific gate; PKG-01 remains Partial until the unsigned-package signing and clean-VM gates close. 2026-08-22 re-review: an isolated clean-checkout re-run of the rehearsal at `a5414b3` passed end-to-end but produced two-build content digest `637a3433821f48f2754751bc6af3d26e22f749c1268510c0d7ccba8377349e24`, not the recorded `ee860840…`, so the recorded digest is not reproducible on re-run. The written L04 gate compares two builds in one directory and cannot detect cross-path or restore-drift inputs; that stability gap and final-artifact retention are tracked as L48.

### L05 — Authenticode signing integration

Scope achievable before D5:

- harden certificate selection, timestamping, verification, and failure reporting;
- sign both packaged app and installer in the correct order;
- keep secrets out of repository, logs, and artifacts;
- add an unsigned rehearsal mode that validates every step except certificate use.

External exit gate: production certificate available, both signatures valid, timestamp chain valid on a clean machine.

### L06 — Signed updater/channel implementation

Scope:

- define a signed update manifest with version, channel, package hash, minimum supported version, and release notes;
- download to a controlled temporary location, verify signature and hash before execution, fail closed, and preserve rollback/install recovery;
- replace the About-only GitHub link with honest “check”, “available”, “downloaded”, “install”, and error states;
- never emulate Sparkle or use macOS APIs.
- fixture-crypto verification now covers signed manifests, package hashes, tamper rejection, rollback, and channel metadata; production signing remains owned by L05/D5.

Exit gate: local signed-fixture tests for upgrade, downgrade rejection, tamper rejection, cancellation, offline behavior, and rollback. The implementation gate is satisfied with verification debt; production completion waits for L05.

### L07 — Clean-machine install/upgrade/uninstall

Scope:

- test ZIP and installer on supported Windows 10 and Windows 11 x64 images;
- first run, onboarding once, data locations, model preparation, startup registration, upgrade with retained data, uninstall with explicit user-data policy;
- confirm no Python/runtime prerequisite;
- verify repair/reinstall and locked-file behavior.

Exit gate: dated VM reports with screenshots, hashes, OS build, install/upgrade/uninstall outcome, and fresh logs.

### L08 — Structured support bundle

Scope:

- add a user-visible export containing redacted logs, app/runtime versions, model/provider status, package identity, OS/audio-device categories, and recent incident categories;
- exclude transcripts, meeting titles, window titles, credentials, raw audio, and Computer Use values by default;
- preview exactly what will be exported and let the user cancel.

Exit gate: redaction tests, large-log tests, file-lock/error tests, human inspection, and no network transmission.

### L09 — GUI automation and accessibility harness

Scope:

- add out-of-process UI Automation coverage for launch, navigation, themes, essential buttons, dialogs, keyboard traversal, accessible names, and single-instance activation;
- keep phase-preview tests separate from production-startup automation;
- provide screenshot-on-failure and deterministic clean-profile setup.

Exit gate: stable CI smoke for Dashboard, Dictations, Meetings, Models, Settings, About, onboarding, and one meeting detail fixture.

Current evidence: the full elevated Windows UI suite is 6 passed, 0 skipped, 0 failed, including the L23 meeting-detail flow. Keep physical accessibility/DPI and clean-profile qualification as evidence debt where those gates are named by the owning modules.

### L10 — Seven-model CPU catalog qualification

Scope:

- run every advertised offline family on approved real speech;
- verify exact model identity, hashes, timestamps, deterministic reuse, RTF, WER/CER, cancellation, and failure on missing/corrupt files;
- publish results by model, language, machine, and provider.

Exit gate: `smoke-transcription-models.ps1` plus provider-specific gates pass for all advertised CPU models. Models that cannot pass are removed or honestly scoped before launch.

### L11 — CUDA provider packaging and NVIDIA qualification

Scope:

- acquire/stage the version-matched sherpa-onnx CUDA runtime under a documented provenance process;
- package the provider only when every required DLL and manifest entry is present;
- qualify supported NVIDIA hardware, driver/CUDA compatibility, memory, cold/warm latency, deterministic output, and CPU fallback disclosure;
- never silently claim or select CUDA from a partial bundle.

Exit gate: packaged NVIDIA build passes native startup, model, dictation, meeting, diarization, stress, and release gates. Until then public metadata remains CPU-only.

### L12 — Model lifecycle destructive/UI qualification

Scope:

- exercise prepare, progress, cancel, retry, verify, delete, corrupt archive, corrupt model, insufficient disk, offline, read-only cache, and restart recovery;
- verify role selection never downloads or activates a recognizer;
- verify deletion cannot cross the owned cache root.

Exit gate: recorded Models-page run plus destructive prepared-cache tests for all lifecycle states.

### L13 — Guided Qwen cleanup lifecycle

Scope:

- proceed only after one GGUF, source, license, size, hash, and prompt contract are approved;
- add explicit download, progress, cancellation, verification, disk-space checks, delete, and disabled/raw-text fallback;
- benchmark cleanup latency and transcript preservation.

Exit gate: clean-machine lifecycle and quality cases pass; otherwise keep manual placement and status **Partial**.

### L14 — Dictation human corpus and latency

Scope:

- build the human-reviewed short-command, paragraph, dictionary, numbers/punctuation, accent, silence, and noise corpus;
- run CPU and qualified CUDA against explicit model IDs;
- enforce WER ≤ 0.15, CER ≤ 0.08, RTF ≤ 0.20 unless the release owner approves stricter targets;
- track release-to-paste separately from inference.

Exit gate: `test-transcription-corpus.ps1` and `qualify-dictation-corpus.ps1` pass with reviewer provenance.

### L15 — History, paste, and hotkey application matrix

Scope:

- dashboard history copy/delete/date filter/search, then Notepad, Chrome, Office, and another editor;
- alternate keyboard layouts, custom modifiers, F-key conflicts, elevation/UIPI mismatch, target closure, clipboard-only mode, clipboard restoration, Escape cancellation, and hands-free double tap;
- confirm the original target regains focus and failure leaves recoverable text.

Exit gate: four fresh `qualify-dictation-target.ps1` reports plus `qualify-dictation-target-suite.ps1` pass.

### L16 — Microphone and route recovery

Scope:

- system default and explicit devices, privacy denial, default-device change, unplug/replug, Bluetooth hands-free loss/reappearance, sleep/resume, exclusive-mode conflict, and device-enumerator transient failure;
- cover dictation and meeting capture without modal-error storms;
- verify temporary file ownership and cleanup.

Exit gate: physical device matrix, recovery tests, no uncaught device errors, and honest fallback diagnostics.

### L17 — Shared text-normalization policy

Scope:

- decide whether meeting/import should share dictation filler removal;
- codify ordering for filler removal, cleanup, dictionary correction, punctuation, speaker-prefix preservation, and user edits;
- prevent reprocessing from corrupting aliases or manual transcript edits.

Exit gate: one documented pipeline per workflow with cross-workflow golden tests.

### L18 — Meeting capture and process attribution

Scope:

- real Zoom, Teams, Meet, and Webex runs;
- Windows 11 process-tree loopback and truthful endpoint fallback; Windows 10 endpoint-only behavior;
- simultaneous mic/system alignment, unrelated-system-audio leakage checks, late-start tracks, and device health.

Exit gate: `PHASE3_QUALIFICATION.md` capture matrix passes with retained diagnostic reports and human listening review.

### L19 — Meeting lifecycle and recovery stress

Scope:

- suspend/resume, cancel, shutdown, forced kill, crash journal, disk full, corrupt journal, missing part, app restart, and repeated finalize;
- prove exactly-once persistence and that manual recordings never auto-stop;
- preserve recoverable audio and never fabricate a completed meeting.

Exit gate: `qualify-meeting-session-lifecycle.ps1`, fault-injection tests, and a real forced-kill recovery pass.

Review state: L19 remains Implemented with verification debt. The model-reuse gate now requires ASR reuse only for present mic/system streams and diarization reuse only when system-audio diarization is requested; forced-kill, disk-full, and physical recovery evidence remain open.

### L20 — Live ASR, VAD, gap recovery, and floating window

Scope:

- long meeting, silence/noise, rapid turns, multilingual advertised speech, route change, suspend, live-model crash, ownership modes, gap recovery, and final reconciliation;
- measure memory growth and UI responsiveness;
- verify hover waveform and window placement across DPI.

Exit gate: streaming qualification fixture test runs rather than skips; long-soak report meets latency/memory limits and transcript ownership is unambiguous.

### L21 — Finalization, diarization, and aliases

Scope:

- human-reviewed two-, three-, and overlapping-speaker fixtures;
- CPU and packaged CUDA if available;
- You/Others attribution, stable identities, channel gaps, degraded mic/system tracks, alias rename persistence, copy, notes, and export surfaces;
- publish speaker coverage and quality limitations.

Exit gate: multi-speaker qualification test runs rather than skips and UI alias round-trip is manually confirmed.

### L22 — Detection, prompts, join actions, and auto-stop

Scope:

- live positive and negative cases across conferencing apps and ordinary browser/media windows;
- foreground app, URL, title, microphone, camera, dedupe, dismiss, rejoin, leave, crash, and muted/browser-call cases;
- verify Join & Record, Join Only, and Record Only without implying calendar support.

Exit gate: false-positive/negative matrix and prompt-action recordings pass; scan logging is rate-limited and privacy-safe.

### L23 — Transcript editing and safe retranscription

Scope:

- editable transcript with explicit save/cancel and optimistic backup;
- prompt to re-summarize when an edit invalidates generated notes, without altering manual notes;
- retranscribe from retained owned audio into a new candidate result;
- compare/accept/reject so the prior transcript is never destroyed by failure or cancellation;
- preserve title ownership and aliases.

Exit gate: macOS-equivalent behavioral flow, destructive-failure tests, and meeting-detail GUI automation. Transcript service tests remain green and the retained 2026-09-13 combined UI suite is 20/20, including WPF production navigation; retained-audio quality and broader meeting qualification remain verification debt.

### L24 — Playback waveform and device-loss behavior

Scope:

- generate/cache waveform data off the UI thread;
- seek from waveform, preserve track selection, handle missing/corrupt audio and output-device loss;
- delete orphaned waveform caches with recording deletion.

Exit gate: waveform accuracy/cache tests, playback GUI test, and physical output-device loss recovery.

Review state: L24 is Implemented with verification debt. Asynchronous cached waveform loading and recording-deletion cleanup are implemented and covered by the current Release suite; retained GUI playback and physical output-device-loss recovery remain open.

### L25 — Summary-provider parity

Scope:

- implement LM Studio and/or documented custom HTTP using a single explicit contract;
- keep OpenAI, OpenRouter, Ollama, and local behavior consistent for timeout, cancellation, invalid response, disclosure, and redaction;
- never treat ChatGPT subscription OAuth as available.

Exit gate: mock contract tests and opt-in live-provider runs with log-redaction review.

### L26 — Notes, templates, and title ownership UX

Scope:

- template create/edit/delete/select and re-summary;
- manual notes never overwritten by re-summary or retranscription;
- generated titles update only while user ownership remains false;
- errors preserve the prior notes/title.

Exit gate: GUI automation plus live provider/local passes across success, cancellation, timeout, and retry.

### L27 — SQLite runtime cutover and migration

Scope:

- replace production `AppDataStore` reads/writes with the tested repository interfaces;
- migrate JSON atomically with backup, digest comparison, restart idempotence, schema-forward rejection, and rollback instructions;
- preserve ordering, IDs, timestamps, folders, notes, aliases, automation results, settings references, and visible selection;
- do not delete JSON backup until a separately defined retention point.

Review state: L27 is Implemented with verification debt. Nested migration and folder persistence now preserve `ParentId`, explicit root moves, and rollback semantics in the green Release suite; cloned-profile counts/digests, second-launch idempotence, and forced-failure evidence remain open.

Exit gate: prepared real-profile clone migrates with equal counts/digests, the second launch performs no duplicate migration, and forced failures retain the old data intact. The implementation gate is satisfied; cloned-profile qualification remains open.

### L28 — Nested folder product UI

Scope:

- expose repository `ParentId`, child listing, ancestry, subtree moves, cycle rejection, reordering, breadcrumbs, and safe delete/reparent behavior;
- support moving meetings and searching within a subtree;
- preserve one-level migrated folders as roots.

Review state: L28 is Implemented with verification debt. Explicit move-to-root/move-within-tree, delete/reparent, nested UI, cycle rejection, and subtree behavior are implemented and green; retained GUI tree/breadcrumb/move/delete evidence remains open.

Exit gate: nested-folder GUI automation and repository tests pass; ORG-01 remains **Implemented with verification debt** until retained UI and cloned-profile evidence prove the user-facing behavior.

### L29 — Full-content search

Scope:

- route product search through the interface-backed JSON/SQLite search adapter rather than the in-memory-only filter;
- index title, transcript, generated notes, manual notes, aliases, follow-ups, dictionary text where intended, and folder ancestry;
- add snippets/highlights, type and folder filters, stable sorting, large-history latency, and transactional freshness.

Review state: L29 is Implemented with verification debt. Ordered paged results, manual-note/snippet coverage, and histories beyond 500 results are green in the current tree; retained GUI and large-profile latency evidence remain open.

Exit gate: manual-note searches work in the actual UI, updates are immediately searchable, and large-history p95 meets an approved target.

### L30 — Media import qualification

Scope:

- real speech in wav/mp3/m4a/aac/mp4/mov/mkv/webm;
- duration correctness, progress, cancellation, ASR, diarization, large/chunked media, corrupt/truncated files, unsupported codec guidance, source ownership, and no fake success;
- keep ogg rejected unless a decoder is deliberately added and qualified.

Exit gate: both real-media qualification tests run rather than skip and `test-media-imports.ps1` passes the reviewed manifest.

### L31 — Export parity and automatic PDF

Scope:

- human-open manual Markdown/PDF with transcript, generated notes, manual notes, aliases, and metadata modes;
- add collision-safe atomic automatic PDF export if retained in scope;
- preserve existing Markdown behavior and expose per-format errors;
- close QuestPDF release-license review.

Exit gate: automated content tests, human open on clean machine, path/permission/locked-file cases, and explicit license approval.

### L32 — Linked follow-up meeting workflow

Scope:

- use the existing persistence follow-up/link substrate to create a new meeting linked to a completed predecessor;
- show predecessor/successors, thread order, title policy, and optional summary context;
- distinguish follow-up from resume and from generated “Follow-ups” bullets;
- do not require an external calendar or SaaS destination.

Exit gate: local linked workflow matches the macOS behavior, survives restart/migration, and is searchable/exportable.

### L33 — Post-meeting executable hook smoke

Scope:

- run one benign real `.exe` fixture through JSON stdin;
- verify timeout, cancellation, Job Object descendant termination, output bounds/redaction, retry, and auto-export path ownership;
- keep hooks disabled by default.

Exit gate: packaged-app smoke and no sensitive payload in logs or captured output.

### L34 — Onboarding, tray, and startup qualification

Scope:

- clean-profile onboarding, pause/resume, real mic/model/hotkey gates, close/reopen, and already-configured migration;
- tray open/recent items/detected meeting/resume setup/tour/settings/about/quit;
- startup install, disable, upgrade, uninstall, elevation, and background behavior;
- upcoming calendar remains honestly unavailable.

Exit gate: clean-profile VM walkthrough plus GUI automation and installer/startup evidence.

### L35 — Floating indicator and feedback-sound routes

Scope:

- indicator drag/click/stop/cancel/levels on multiple monitors and DPI values;
- start, insert, model-ready, setup-session, disabled-setting, speaker, headphone, and route-change sound behavior;
- no sound during privacy-sensitive setup tests where suppression is required.

Exit gate: physical route matrix, DPI captures, and audible human confirmation.

### L36 — Privacy deletion, retention, and cache cleanup

Scope:

- delete individual and bulk dictations/meetings with owned audio, journals, aliases, waveform caches, and search documents;
- prepared model/cache deletion remains contained to owned roots;
- imported originals are never deleted;
- privacy document, Settings copy, package README, and actual network behavior agree.

Exit gate: destructive tests against cloned prepared data, path-boundary tests, recovery behavior, and human disclosure review.

### L37 — Computer Use sandbox qualification

Scope:

- exercise one approved local-app workflow and one approved browser workflow;
- verify allowlists, confirmation boundaries, foreground/process identity, changed-context rejection, trace redaction, cancellation, and disabled-by-default behavior;
- keep window text and screenshots disabled until a separate masking module is approved.

Exit gate: sandboxed live workflow with no values, commands, screenshots, or secrets in logs.

### L38 — Local-only insights analyzer

Scope:

- optional local statistics/word analysis with no contribution telemetry;
- explain source data and deletion behavior;
- keep sharing/contribution absent until D4.

Exit gate: only schedule after launch blockers and core parity modules; otherwise leave INSIGHT-01 **Partial** without delaying launch.

### L39 — Final release-candidate qualification

Scope:

- freeze a commit and version;
- run Debug/Release tests, PowerShell syntax, package/installer, native inventory, signing, package smoke, clean VM, CPU model matrix, qualified CUDA matrix if advertised, dictation targets/corpus, meeting capture/lifecycle/live/diarization/detection, imports, exports, onboarding, DPI/accessibility, privacy deletion, and fresh logs;
- archive exact commands, TRX, reports, screenshots, hashes, signatures, OS/hardware/provider identities, and approved waivers;
- no status promotion based on historical artifacts.

Exit gate: zero unresolved launch blockers, every shipped claim backed by current-candidate evidence, rollback prepared, and release owner approval.

### L40 — Tracked Windows source reproducibility

Scope:

- narrow the root model-cache ignore rule so required Windows source cannot be hidden by a broad `models/` pattern;
- track all required `Muesli.Windows/Models/*.cs` files and `Features/Models/ModelsView.xaml` plus its code-behind;
- audit every `.cs`, `.xaml`, and `.csproj` under `windows-native`, excluding generated `bin/` and `obj/`, for ignored required source;
- prove the committed tree is independently buildable without copying locally present source files.

Exit gate: a clean isolated checkout from the focused commit contains every required source file, has no ignored source outside generated `bin/`/`obj/`, and passes the Release build and test commands with exact results recorded in this dashboard.

Review state: L40 is reopened to Partial. Its focused WPF-era commit met the gate for the then-shipping tree, but the selected Core/Platform/WinUI solution and archived WPF reference are now untracked in the 312-entry working tree. A clean checkout at HEAD cannot reproduce the selected app, so the gate is no longer true for current launch input. L49 owns the migration boundary and L52 owns its build/test repair; L40 closes again only when that source is tracked and a clean checkout is independently buildable.

### L41 — Concurrent automatic Markdown export publication

Scope:

- make destination claim, temporary-file publication, and manifest ownership atomic across concurrent exporters;
- continue after a collision without reusing or clearing another run's temporary path;
- preserve exactly-once destination ownership, no user-file overwrite, and cleanup of service-owned temporary files;
- stress 8 rounds × 12 concurrent exports and retain the focused result with the release evidence.

Exit gate: met on 2026-09-14. The focused concurrency test passed 8 consecutive rounds × 12 concurrent exports with exactly one Markdown and no service-owned temporary files; the retained 2026-09-13 final-source suite is 840/0/5 of 845 and the historical final release rehearsal/package digest remains recorded. L41 is Complete and verified; remaining automatic PDF, SIGN-01/L05, and clean-VM PKG-01/L07 gates belong to their owning modules/capabilities.

### L42 — Per-meeting retranscription admission and deterministic candidates

Scope:

- admit a retranscription candidate only after inspecting the meeting's retained audio and metadata, before creating scratch state;
- reject missing or invalid per-meeting admission deterministically, serialize concurrent candidates for one meeting, preserve the prior transcript, and clean up service-owned scratch state;
- keep MTG-03 capability status at **Implemented with verification debt** until retained-audio quality and broader meeting qualification are reviewed.

Exit gate: deterministic concurrent admission/regression tests pass and the combined focused L42–L45 result is 100/100. Commit `619adc3` provides the evidence. L42 is Complete and verified as a module; physical retained-audio quality remains capability evidence debt.

### L43 — Automatic export manifest and current-render integrity

Scope:

- validate manifest filename, path, SHA, destination, and current-render integrity before reusing an automatic Markdown output;
- preserve a user-modified file and publish a collision-free candidate when the prior destination no longer matches its manifest;
- keep AUTO-01 capability status at **Implemented with verification debt** because automatic PDF export is missing.

Exit gate: deterministic manifest-integrity, user-modification, and collision-free publication tests pass with the integrated Release and package smoke evidence. Commit `be293f3` provides the evidence. L43 is Complete and verified as a module.

### L44 — Immutable persistence migration snapshot plan

Scope:

- capture one immutable JSON snapshot, `.bak` snapshot, and dictionary snapshot before migration mutates any source;
- pass one MigrationPlan through import, fingerprint, and retained-backup paths so all decisions use the same captured inputs;
- retain L27/data capability at **Implemented with verification debt** until a cloned real profile proves counts, digests, rollback, and second-launch idempotence.

Exit gate: mutation regression and the focused L44 tests pass, with the combined focused L42–L45 result at 100/100. Commit `0922af2` provides the evidence. L44 is Complete and verified as a module.

### L45 — Exact-SDK release reproducibility

Scope:

- enforce SDK `10.0.400` from `global.json` with `rollForward=disable`;
- make the release helper reject an SDK mismatch and retain exact-SDK package rehearsal evidence;
- keep PKG-01 and TEST-01 at **Partial** because signing, clean-VM, and live physical coverage remain open.

Exit gate: focused L45 tests, Debug/Release builds, the integrated Release suite, package smoke, and a clean isolated exact-SDK rehearsal pass. Commit `d432a26` pins the exact SDK; its historical rehearsal matched digest `049c3c34f71dc71b60447743d897e6105255d67cc83cc584c8e6c0d526173de4`. The final rehearsal at `a5414b3` matches digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf`, with ZIP 109,499,342 bytes and installer 73,677,646 bytes. L45 is Complete and verified as a module. 2026-08-22 re-review: the stated L45 gate (SDK pin enforcement, focused tests, Debug/Release builds, integrated Release suite, package smoke, clean isolated rehearsal pass) was re-verified end-to-end at `a5414b3`, but the re-run's content digest was `637a3433821f48f2754751bc6af3d26e22f749c1268510c0d7ccba8377349e24` (entryCount 372, ZIP 109,499,479 bytes, installer 73,666,874 bytes), so the recorded digest and byte sizes are historical, not reproducible; cross-run stability is L48.

### L46 — P1 recovered retranscription stale-flight ownership

Scope:

- prevent a recovered stale retranscription flight from restoring an older transcript over a newer accepted transcript;
- define ownership for recovery, acceptance, cancellation, and final persistence so a stale completion cannot overwrite current meeting state;
- keep MTG-03 at **Implemented with verification debt** because retained-audio quality and broader meeting qualification remain open after the deterministic recovered-stale-versus-new-accept ownership test.

Review state: L46 is Complete and verified. Commit `818fab2` passed the focused L46 suite **27/27 in Debug and Release**, and independent review found no P0–P3 findings.

Exit gate: deterministic recovered-stale-versus-new-accept ownership test passes with explicit transcript-version/flight ownership assertions, then the integrated suite and release rehearsal are refreshed. This gate is satisfied; no L46-specific implementation gate remains.

### L47 — P2 protected cutover staging and coherent multi-file capture

Scope:

- stage cutover inputs in a profile-scoped protected location, clean stale staging safely, and prevent one profile from reading another profile's migration inputs;
- capture all migration files coherently under concurrent mutation so JSON, backup, dictionary, and fingerprints describe one logical profile state;
- keep DATA-01 at **Implemented with verification debt** after the protected staging, stale cleanup, and concurrent coherent-capture evidence; ORG-01 is now **Implemented with verification debt**, and QUAL-01 remains **Partial**.

Review state: L47 is Implemented with verification debt. Commit `a5414b3` passed the focused L47 suite **30/30 in Debug and Release**; independent review found no P0–P3 findings. Real cloned-profile qualification has not run.

Exit gate: profile-scoped protected staging, stale cleanup, and coherent multi-file capture under concurrent mutation pass deterministic tests, followed by cloned-profile migration qualification. The deterministic implementation gate is satisfied; cloned-profile qualification remains open.

### L48 — Cross-environment release digest stability and rehearsal-artifact retention

Scope:

- explain why the recorded 2026-08-20 exact-SDK rehearsal digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf` did not reproduce on the 2026-08-22 isolated clean-checkout re-run at the same commit `a5414b3` (both re-run builds matched each other at `637a3433821f48f2754751bc6af3d26e22f749c1268510c0d7ccba8377349e24`, entryCount 372, ZIP 109,499,479 bytes, installer 73,666,874 bytes; the retained repo artifacts are the earlier `d432a26` rehearsal at digest `049c3c34…`);
- identify the moving input. Direct and transitive NuGet inputs are now represented by four `packages.lock.json` files; checkout-path-sensitive content remains to be ruled out because the L04 two-build compare runs both builds in one directory;
- retain the lockfiles and extend the rehearsal to compare digests across two different checkout paths;
- retain every final rehearsal's artifacts under `artifacts/` so recorded digests remain re-provable from the tree. The 2026-08-22 re-run reports are retained as `artifacts/release-rehearsal-report-20260822-a5414b3-rerun.json`, `artifacts/release-hashes-20260822-a5414b3-rerun.json`, `artifacts/package-content-inventory-comparison-20260822-a5414b3-rerun.json`, and `artifacts/package-smoke-report-20260822-a5414b3-rerun.json` (`artifacts/` is gitignored, so they are review evidence, not tracked source).

Review state: L48 remains Partial. The cross-path inventory comparator and tests are implemented, and restore inputs are locked; a clean committed two-checkout rehearsal and retained final artifacts are still missing.

Exit gate: two rehearsals of the same commit from different checkout paths produce the same content digest, the moving input is named and pinned or eliminated, and the final recorded rehearsal's artifacts are retained. PKG-01 remains Partial regardless; L04/L45 module states are unaffected because their written gates did not require cross-run digest stability.

### L49 — Shared Core/Platform extraction and WinUI shipping-shell qualification

Scope:

- preserve the retired WPF source as recoverable historical reference while moving framework-neutral services into tracked `Muesli.Windows.Core` and Windows adapters into tracked `Muesli.Windows.Platform`;
- land the WinUI shell, solution, command host, tests, lockfiles, packaging scripts, assets, and source mappings as one reviewable migration boundary rather than leaving tracked WPF services deleted with untracked replacements;
- prove capability parity for navigation, onboarding, tray, startup, dictation/paste, meetings/live, models, search, settings, single-instance activation, theme, DPI, accessibility, and honest unavailable-state copy;
- qualify both unpackaged development behavior and registered packaged-identity behavior; keep the unsigned MSIX and Developer Mode/sideloading requirement explicit and do not treat template/startup-notification stubs as shipping proof;
- route the active solution, developer launcher, and package entry point to the selected WinUI shipping shell.

Review state: L49 is Partial. On 2026-09-16 the release owner selected WinUI, moved WPF to `Muesli.Windows.Wpf.Legacy`, produced the unsigned Debug x64 MSIX, and retained a clean production-profile screenshot. The 2026-09-17 relaunch regressed: the process stayed responsive and second activation exposed a nominal 1600×1020 dashboard window, but UI Automation/capture saw only its 40-pixel title bar over the previously foreground game; the dashboard content was not verifiable and the window later hid until reactivation. The migration remains uncommitted; default Release/full-suite recovery (L52), signed/installed package qualification, real workflows, physical accessibility/DPI evidence, and L50/L51 remain open.

Exit gate: one reviewed commit contains the complete shared extraction, shipping WinUI shell, and archived WPF reference; the required Release and WinUI UI suites are green; registered MSIX startup/notification/single-instance behavior passes; real dictation/paste, meeting capture/live/finalization, model lifecycle, tray/startup, keyboard/Narrator/High Contrast and 100/125/150/200% multi-monitor evidence is retained.

### L50 — Settings deserialization normalization

Scope:

- normalize every non-nullable collection/string in `MuesliSettings` after JSON deserialization instead of relying on record initializers;
- recover safely from explicit JSON `null` values without per-page guards or fabricated settings;
- cover WPF and WinUI startup/navigation against a corpus of null-bearing and legacy settings documents.

Review state: L50 is Partial. A Dictionary-page call-site guard prevented one observed crash, but `SettingsStore.Load` still returns deserialized nulls for other non-nullable members (`windows-native/Muesli.Windows.Core/Services/SettingsStore.cs:31-54`, `:264-326`).

Exit gate: centralized normalization and regression tests cover every non-nullable settings member; both shells open all settings-dependent routes from malformed/legacy fixtures without an unhandled exception; fresh logs are clean.

### L51 — WinUI unhandled-exception containment policy

Scope:

- define which UI-thread exceptions are recoverable and which must fail closed;
- prevent expected transient OS failures from terminating the shell while avoiding blanket suppression of corrupted state;
- add targeted tests around the global handler and every known asynchronous UI boundary.

Review state: L51 is Partial. Known clipboard paths are guarded, but `App.UnhandledException` still only logs and leaves every future unguarded UI exception fatal (`windows-native/Muesli.Windows.WinUI/App.xaml.cs:89-90`).

Exit gate: a documented fail-closed containment policy is implemented; known transient exception classes are handled with honest user feedback; fatal classes terminate once with diagnostic context; focused tests and a live fault-injection pass are retained.

### L52 — WinUI test migration and Release-build parity

Scope:

- migrate or deliberately retire every WPF-source assertion after the shipping-shell cutover instead of leaving tests pointed at the removed `windows-native/Muesli.Windows` tree;
- restore the live-streaming real-fixture qualification test and its explicit `MUESLI_STREAMING_QUALIFICATION_MODEL` skip contract;
- bound source-tree scans by excluding generated `bin`/`obj` content before recursion;
- make the normal `Release` WinUI build succeed with its declared ReadyToRun settings after a clean restore, without using `PublishReadyToRun=false` as release evidence.

Review state: L52 is Partial. The 2026-09-17 assembly discovers **706 tests**, down from the retained 845-test baseline. Excluding the unbounded `SharedProjectsContainNoWpfReferences` test, the current run is **670 passed / 31 failed / 4 explicitly skipped of 705**. Most failures are stale repository-root or direct WPF-source assertions (`CpuCatalogInventoryTests.cs:11-20`, `:179-189`; `PlaybackIntegrationContractTests.cs:6-17`; `SoundFeedbackTests.cs:115-143`; `FolderLaunchParityTests.cs:54-70`). The four surviving skips still name their real media, multi-speaker, or cloned-profile prerequisite, but the streaming qualification skip/test was removed with the WPF Phase 4 test source. The default WinUI Release build also fails `NETSDK1094`; a diagnostic build with `PublishReadyToRun=false` compiles 0/0 but is not release proof.

Exit gate: the default active WinUI Release build passes after a clean restore; the full current suite completes without a hang or stale WPF path, has no unexplained coverage loss, and every retained qualification test either runs or reports its named prerequisite in TRX; restored/migrated tests prove the WinUI implementation rather than the retired shell.

## Recommended five-agent allocation

Keep stable lanes across waves so agents build context and do not repeatedly relearn ownership.

| Agent | Stable lane | Primary files | Must not edit without integration lock |
|---|---|---|---|
| A | Models and native runtime | model catalogs/lifecycle, native ASR/diarization/cleanup, model tests, benchmark scripts | shared runtime composition, package metadata |
| B | Dictation and audio | capture, hotkeys, paste, text normalization, dictation tests/qualification | meeting runtime, Settings shared state |
| C | Meetings | capture session, lifecycle, live/finalization/detection/playback, meeting tests | persistence schema, release scripts |
| D | Data and knowledge workflows | repositories, migration, search, folders, notes/providers/export/follow-up | central runtime wiring unless designated |
| E | Shell, quality, and release integration | manifest, windows, onboarding/tray, diagnostics, automation host, CI/package/installer/docs | native engine internals |

Agent E acts as integration owner by default. Rotate that role only at wave boundaries.

## Wave schedule

### Wave 1 — Correct launch truth and create safe concurrency

Run in parallel:

- Agent E: L40 tracked-source reproducibility first, then L00, L02, L03, and L09 shell navigation skeleton.
- Agent A: L10 design/fixture inventory and L11 CUDA provenance gap analysis; no CUDA claim or packaging yet.
- Agent B: L14 corpus manifest preparation and L15 target-run rehearsal tooling.
- Agent C: L23 transcript-edit/retranscribe service contract and failure tests, without shared runtime wiring.
- Agent D: L01 + L27 cutover design and production behavior characterization; draft L47 protected staging/coherence review.

Wave exit: tracked source is reproducible from a clean checkout, ledger corrected, DPI/package-truth defects fixed, persistence cutover approved, shared-file ownership enforced, full suite/package smoke green.

### Wave 2 — Prove the native core on real hardware

- A: L10, L11, L12.
- B: L14, L15, L16, then L17.
- C: L18, L19, L21.
- C: L46 stale recovered-flight ownership review after the L23/L42 contract.
- D: begin L27 migration implementation behind an adapter/feature gate.
- E: L04 and L08; support qualification evidence collection.

Wave exit: CPU catalog, dictation, paste, devices, meeting capture/lifecycle/diarization have current evidence; SQLite migration passes cloned-profile tests.

### Wave 3 — Close meeting and library parity

- A/C jointly but sequentially: L20 live stack.
- C: L22, L23, L24, L42.
- D: finish L27, then L28, L29, L32.
- B: regression support for audio/text/paste.
- E: expand L09 automation around the new flows.

Wave exit: safe transcript editing/retranscription, waveform, detection, nested folders, full search, and linked follow-ups work through production persistence.

### Wave 4 — Finish knowledge workflows and product shell

- D: L25, L26, L30, L31, L33, L41, L43, L44.
- E: L34, L35, L36, L37.
- E: L49 tracked dual-shell migration boundary and unpackaged parity evidence; keep WPF fallback qualification green.
- E: L50 settings normalization, L51 exception-policy hardening, and L52 WinUI test/release parity before freezing the L49 migration boundary.
- A/B/C: provider, audio, import, export, and meeting regression support.

Wave exit: all achievable parity workflows are implemented; remaining work is current-candidate qualification or explicit external signing dependency.

### Wave 5 — Release channel and candidate

- E: L52, then L05, L06, L07, L45, L48, and L49 packaged-identity qualification.
- All agents: L39 evidence in their stable lanes.
- D: L38 only if the core candidate is already green and D4 permits local-only scope.

Wave exit: signed, upgradeable, clean-machine-qualified release candidate or a precise external blocker report naming only D5.

## First five assignments to start now

1. **Agent E — L52/L49 WinUI recovery:** migrate the 31 stale WPF-path failures, restore the missing streaming prerequisite test, bound `SharedProjectsContainNoWpfReferences`, perform a clean restore, run default Release/full tests, then reproduce and fix the title-bar-only dashboard activation before retaining a fresh screenshot/log.
2. **Agent A — L10 CPU catalog qualification:** run `scripts/smoke-transcription-models.ps1` and `scripts/test-transcription-corpus.ps1` on human-reviewed speech for every advertised CPU model; retain WER <=0.15, CER <=0.08, and RTF <=0.20 evidence.
3. **Agent B — L16 physical route recovery:** run the default/jack/USB/Bluetooth/unplug/privacy/sleep matrix and `Phase2DictationTests`; preserve fresh device-change logs before resuming L14/L15 four-app qualification.
4. **Agent C — L18 physical meeting capture:** retain the now-green `scripts/benchmark-native-meeting.ps1` CPU baseline (RTF 0.181), then run Zoom/Teams/Meet/Webex mic+system attribution with route-loss recovery.
5. **Agent D — L31 automatic PDF export:** implement automatic PDF alongside the now-stable auto-Markdown path, then run `Phase8ExportTests` and `Phase9AutomationTests`; keep QuestPDF eligibility as an explicit release-owner gate.

No lane assignment or shared-file lock changed. Named reason for adding L52 without rewriting either: the selected WinUI shell deleted or invalidated test and release evidence outside L49's product-migration exit gate, while Agent E already owns the test/release collision surface.

## Review dashboard

Snapshot date: 2026-09-17 (previous snapshot 2026-09-14). All L00–L51 rows were re-audited and L52 was added for the newly exposed WinUI cutover test/release regression. L40 reopened because the selected shipping source is untracked and a clean HEAD checkout cannot reproduce it. The exact 10.0.400 SDK gate still cannot start. With installed SDK 10.0.401, the default active WinUI Release build fails `NETSDK1094` at ReadyToRun; the diagnostic `PublishReadyToRun=false` build is 0 warnings / 0 errors. The current assembly discovers **706 tests**, versus the retained 845-test baseline. Excluding the unbounded shared-project scan, the current result is **670 passed / 31 failed / 4 explicit prerequisite skips of 705**; all four surviving skips name their fixture, but the streaming fixture test/skip is absent. Historical UI automation remains 20/20 but was not rerun. CPU Parakeet evidence is RTF 0.149 for 5.56 s dictation and 0.181 for mic-only meeting; WER/CER remain unavailable. The dashboard carries 53 module rows, L00–L52: **9 Complete and verified, 27 Implemented with verification debt, 12 Partial, 2 Missing, and 3 Externally blocked**. Branch-only evidence is not trusted or merged.

| Module | Capability IDs | State | Owner | Integration owner | Base commit | Changed shared files | Automated evidence | Human/physical evidence | Remaining gate | Ledger update |
|---|---|---|---|---|---|---|---|---|---|---|
| L00 | Phase 0 | Complete and verified | Agent E | Agent E | `a5414b3` + working tree | `docs/WINDOWS_LAUNCH_LEDGER.md`, matrix, this plan | 2026-09-17 tree/docs/branches audited; benchmark and filtered current-suite evidence retained | Exact SDK absent; L49/L50/L51/L52 remain open without promoting physical/signing evidence | Preserve reconciled statuses; split the dirty tree before release rehearsal | included |
| L01 | data foundation | Complete and verified | Agent D | Agent E | `a43ff1a` + working tree | `PersistenceCutoverCharacterizationTests.cs` | Design/characterization suite remains green; the stale JSON `parentId` assertion was corrected under L27/L28 | Not required for design gate | Preserve characterization coverage; L01 design gate remains closed | not warranted |
| L02 | SHELL-01, FLOAT-01, FLOAT-02 | Implemented with verification debt | Agent E | Agent E | `e2c0709` + working tree | `Muesli.Windows.csproj`, `App.xaml`, `MainWindow.xaml*`, `FeatureTourWindow.xaml*`, shell icon assets/service | `DpiManifestAndPlacementTests` passes; 2 `AppShellIconTests` pass; L09 launch restored | No full 100/125/150/200% two-monitor capture | Physical DPI matrix, keyboard/accessibility review, and clean-log captures | not warranted |
| L03 | MOD-01, PKG-01, EXP-01 | Implemented with verification debt | Agent E | Agent E | `9975786` | none | `PackageDisclosureAndNativeInventoryTests`; CPU-only notices/inventory agree | QuestPDF eligibility not approved; retained package is stale/unsigned | Current-tree package inventory/smoke and release-owner license approval | included |
| L04 | PKG-01, TEST-01 | Complete and verified | Agent E | Agent E | `a5414b3` | none in focused commits | Prior clean two-build rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; historical exact-SDK rehearsal at `d432a26` matched digest `049c3c34f71dc71b60447743d897e6105255d67cc83cc584c8e6c0d526173de4`; final exact-SDK rehearsal at `a5414b3` matched digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf`; package smoke and PerMonitorV2 checks passed | No L04-specific gate remains; unsigned-package signing and clean-VM gates remain under PKG-01. 2026-08-22 isolated re-run at `a5414b3` matched two builds at digest `637a3433…`, not the recorded `ee860840…`; cross-run stability and artifact retention moved to L48 | Preserve module evidence; PKG-01 retains Partial status | included |
| L05 | SIGN-01 | Externally blocked | Agent E | Agent E | `3bff598` + working tree | signing/release scripts | Fixture/unsigned orchestration validates the package path; production certificate remains unavailable | No production certificate or timestamped app/installer signatures (D5) | Valid timestamped app and installer signatures on a clean machine | not warranted |
| L06 | UPD-01, API-04 | Partial | Agent E | Agent E | `3bff598` + working tree | updater manifest/channel and fixture-crypto verification files | Release-side signed-manifest, independent publisher-key pin, package hash/tamper rejection, and stale-installer rejection tests pass | No production app-side check/download/install/recovery workflow; signing and clean install/upgrade evidence remain open | Implement the app updater after L05; do not treat release tooling as product behavior | included |
| L07 | PKG-01 | Implemented with verification debt | Agent E | Agent E | `4f408f4` | none | Final clean isolated package smoke passed; ZIP/installer and CPU-only inventory are reproducible | No current Win10/Win11 clean-VM run | Install/upgrade/uninstall reports on supported clean VMs | not warranted |
| L08 | DIAG-01 | Missing | Agent E | Agent E | `1829702` | none | Redaction/runtime status unit coverage only | No user-visible support bundle | Previewable redacted export with cancellation and file-error tests | not warranted |
| L09 | TEST-01 | Implemented with verification debt | Agent E | Agent E | `74a0a97` + working tree | WPF/WinUI UI sessions and production UI tests; shell startup files | 2026-09-13 final-source combined UI suite is 20/20, including WPF production navigation and packaged/unpackaged WinUI coverage | Keyboard route pass retained; Narrator, High Contrast, non-125% DPI, and multi-monitor remain open | Preserve 20/20 and collect the named physical accessibility/display evidence | included |
| L10 | MOD-02 | Implemented with verification debt | Agent A | Agent E | `c9278ee` + working tree | model catalog/qualification scripts | Seven advertised CPU choices remain; 2026-09-17 Parakeet CPU is deterministic/reused and passes at RTF 0.149 | Current catalog tests are red under L52; reviewed speech corpus and WER/CER remain absent | Restore catalog test path, then run reviewed corpus and record WER/CER/RTF for all advertised models | pending |
| L11 | MOD-01, QUAL-01 | Partial | Agent A | Agent E | `8c93279` | none | CUDA provenance-gap docs/tests; current runtime reports CPU | No packaged NVIDIA run or matching public bundle | Stage version-matched provider and complete NVIDIA qualification | not warranted |
| L12 | MOD-03 | Implemented with verification debt | Agent A | Agent E | `3bff598` + working tree | WPF/WinUI model views, shared lifecycle service | Lifecycle tests pass in the retained 840/0/5 suite; current short guard abort is owned by L49/TEST-01 | No destructive Models-page run after dual-shell edits | Recorded all-state UI/destructive cache matrix | not warranted |
| L13 | MOD-05 | Externally blocked | Agent A | Agent E | `3bff598` | none | Manual-placement fail-closed tests only | Approved GGUF/hash/license/prompt contract absent | Product-approved cleanup model contract and lifecycle | not warranted |
| L14 | DIC-01, TXT-01, TXT-02 | Implemented with verification debt | Agent B | Agent E | `f67584f` + working tree | dictionary, dictation runtime, and qualification contracts | 2026-09-17 5.56 s CPU Parakeet RTF is 0.149; deterministic/model reuse pass | No reviewed references; human WER/CER remains absent | Run reviewed corpus at WER <=0.15, CER <=0.08, RTF <=0.20 | pending |
| L15 | DIC-02, DIC-03, HOT-01, HOT-02, API-03 | Implemented with verification debt | Agent B | Agent E | `bf04aa2` + working tree | `DictationsView.xaml`, `FeatureRuntime.Dictations.cs`, `DictationCoordinator.cs`, `HotkeyTriggerTiming.cs` | Exact four-app qualification gate exists; latest 2026-09-09 ChatGPT-only sample is 7,897 ms release-to-paste with 0 paste failures | Physical Notepad/Chrome/Office/second-editor, real-hook, and UIPI evidence remain open | Run four target reports, hotkey/UIPI matrix, and representative <=3,000 ms median/p95 evidence | pending |
| L16 | AUD-01, AUD-02 | Implemented with verification debt | Agent B | Agent E | `3bff598` + working tree | shared Core/Platform audio services and Phase 2 tests | Route-policy tests pass in the retained 840/0/5 suite and distinguish default/communications behavior | No physical array/jack/USB/Bluetooth/privacy/sleep route matrix | Run default/unplug/Bluetooth/privacy/sleep matrix | not warranted |
| L17 | TXT-03 | Externally blocked | Agent B | Agent E | `3bff598` | none | Existing workflow-specific tests | Product ordering decision absent | One approved cross-workflow pipeline and goldens | not warranted |
| L18 | MTG-01, API-01 | Implemented with verification debt | Agent C | Agent E | `3bff598` + working tree | capture/process attribution and meeting benchmark harness | 2026-09-17 mic-only Parakeet CPU run is deterministic with ASR reuse: 1,004 ms warm, RTF 0.181 (pass) | No system-audio/diarization input or live Zoom/Teams/Meet/Webex run | Retain four-app listening/attribution and route-loss matrix | included |
| L19 | MTG-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` + working tree | lifecycle harness/reuse-gate validation | Model-reuse gate is green for mic-only, system-only, and two-track semantics; Wave 3 qualification slice is 440/0/2 of 442 | No forced-kill/disk-full physical run | Fault injection plus real recovery proof | not warranted |
| L20 | MOD-04, LIVE-01, LIVE-02, LIVE-04, FLOAT-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` + working tree | WinUI live transcript window plus shared live-session services | Implementation remains in Core/WinUI, but the WPF Phase 4 suite and `MUESLI_STREAMING_QUALIFICATION_MODEL` qualification test disappeared from active discovery | No current real streaming fixture, long meeting/noisy-room/route/DPI soak | L52 restores a WinUI/shared streaming fixture gate; then run the fixture and soak | included |
| L21 | DIA-01, DIA-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` + working tree | `MeetingDetailView.xaml`, `MeetingsView.xaml*`, `MeetingItem.cs`, meeting runtime files | Finalization tests pass; multi-speaker test still skips and names `MUESLI_MULTISPEAKER_FIXTURE_SOURCE` | No reviewed multi-speaker/alias UI run | Multi-speaker test must run and UI alias round-trip pass | not warranted |
| L22 | DET-01, DET-02, JOIN-01 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Detection policy tests pass | No live conferencing false-positive/negative matrix | Live prompt/join/leave/rejoin evidence | not warranted |
| L23 | MTG-03 | Implemented with verification debt | Agent C | Agent E | `24c14ae` + `437f046`/`c1168e7`/`1829702` + working tree | WPF/WinUI meeting detail/navigation plus shared transcript-edit services | Transcript save/cancel/candidate tests pass; retained combined UI is 20/20 and includes WPF production navigation | Retained-audio quality and broader physical meeting UI evidence remain open | Preserve shared lock and add physical retained-audio review | included |
| L24 | PLAY-01 | Implemented with verification debt | Agent C | Agent E | `3bff598` + working tree | shared playback/waveform services and both meeting-detail shells | Async waveform sampling/cache/cleanup and playback contract tests pass in the retained 840/0/5 suite | No retained live waveform or physical output-device-loss run | GUI playback/device-loss evidence | included |
| L25 | SUM-01, SUM-02 | Partial | Agent D | Agent E | `3bff598` | none | Existing provider/mock tests | No LM Studio/custom contract or live-provider run | Implement adapter contract and redacted live qualification | not warranted |
| L26 | SUM-04, TPL-01, NOTE-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Notes/title/template unit coverage | No GUI/live-provider matrix | GUI ownership/cancel/timeout/retry evidence | not warranted |
| L27 | data foundation | Implemented with verification debt | Agent D | Agent E | `f0dcef3` + `53dc577` + working tree | persistence adapters, migration/cutover tests | Nested migration/folder persistence, explicit root move, and rollback coverage are green in Release; cloned-profile prerequisite remains an explicit `MUESLI_CLONED_PROFILE_DIR` skip | No cloned-profile counts/digests, second-launch, or forced-failure run | Run cloned-profile qualification and preserve backup/rollback evidence | included |
| L28 | ORG-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` + working tree | `MeetingFolderItem.cs`, folder runtime/UI, persistence adapters | Move-to-root/parent UI, delete/reparent, cycle rejection, SQLite missing-row parity, and crash-journal recovery across JSON folder/meeting files are green | Retained breadcrumb/tree/move/delete GUI exercise remains absent | Run retained GUI folder workflow and cloned-profile nested data review | included |
| L29 | SEARCH-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` + working tree | shared search repositories/adapters plus WPF/WinUI search views | Functional JSON/SQLite ordered paging, manual notes/snippets, >500-result assertions, and the 2026-09-12 focused 10k-history slice pass 14/14 within its fastest-of-five 300 ms assertions | No retained GUI run or large real-profile latency report | Run GUI/real-profile latency evidence | included |
| L30 | IMP-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Import unit tests pass; two real-media tests explicitly skip | No reviewed eight-format speech set | Run real-media tests and manifest across all advertised formats | not warranted |
| L31 | EXP-01, AUTO-01 | Partial | Agent D | Agent E | `3bff598` | none | Manual MD/PDF and auto-MD tests pass; L41 atomic publication stress and final package rehearsal are green | Auto-PDF remains missing; QuestPDF eligibility and human open remain evidence debt | Auto-PDF, file failure cases, human open, and license decision | not warranted |
| L32 | FOLLOW-01 | Missing | Agent D | Agent E | `3bff598` | none | Persistence link substrate tests only | No user-facing linked workflow | User-facing linked follow-up workflow through production store | not warranted |
| L33 | HOOK-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Hook success/failure/timeout tests pass | No packaged benign executable smoke | Packaged real `.exe` smoke with redaction review | not warranted |
| L34 | ONB-01, TRAY-01, START-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` + working tree | WPF/WinUI onboarding/tray/startup files plus shared startup services | Exact-command/icon tests pass; final-source UI is 20/20; WinUI honestly disables startup without package identity | No clean-profile VM/startup upgrade or registered-MSIX run | Land dual-shell boundary, then collect clean-profile onboarding/tray/startup/install evidence | not warranted |
| L35 | FLOAT-01, SOUND-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` + working tree | `ToastNotificationService.cs`, `SoundFeedbackService.cs`, `MeetingLiveTranscriptWindow.xaml*` | Indicator/sound contract tests pass; final Wave 3 qualification includes **352 DPI matrix cells** | No physical audible cue/device-route review | Multi-monitor captures and audible route matrix | not warranted |
| L36 | PRIV-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Path-boundary/redaction/cleanup tests pass | No destructive cloned prepared-data run | Full deletion/cache/retention disclosure review | not warranted |
| L37 | CU-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Planner/allowlist/redaction tests pass | No sandboxed local/browser workflow | Two live sandbox workflows with clean logs | not warranted |
| L38 | INSIGHT-01 | Partial | Agent D | Agent E | `3bff598` + working tree | `InsightsWordAnalyzer.cs`, `FeatureRuntime.xaml.cs`, `OverlayParityTests.cs`, dashboard XAML | Focused tests cover stop words, range totals, streaks, activity, and local word lists; visible dashboard exposes local insights | Source/deletion disclosure has no retained human review; sharing/contribution is absent and D4-gated | Retain local-only behavior; review deletion/source copy and leave contribution absent until D4 | included |
| L39 | QUAL-01 | Partial | Agent E | Agent E | `a5414b3` + working tree | all release-owned files at freeze; 312-entry porcelain tree remains uncommitted | Exact SDK missing; default WinUI Release build fails; filtered current suite is 670/31/4 of 705; CPU dictation/meeting RTF 0.149/0.181 pass | Signing, clean-VM, physical routes/meetings, provider, WER/CER, accessibility, current package, L48/L49/L52, and prerequisite fixtures remain absent | Close L52, freeze a reviewed commit, restore exact-SDK evidence, then rerun full candidate qualification | included |
| L40 | Phase 0/13, TEST-01, PKG-01 | Partial | Agent E | Agent E | `353ba52` + working tree | `.gitignore`, active solution, Core/Platform/WinUI source, archived WPF reference | Historical WPF focused commit was reproducible | Selected shipping source/solution remains untracked; default Release/full tests are red | Track the L49/L52 migration and prove a clean isolated checkout builds/tests without local-only files | included |
| L41 | AUTO-01 | Complete and verified | Agent D | Agent E | `2972192` + working tree | shared `PostMeetingAutomationService.cs`, Phase 9 tests | 2026-09-14 focused stress passes 8/8 rounds × 12 concurrent exports with exactly one Markdown and no temporary files; retained final-source integrated suite is 840/0/5 | Not required for this concurrency gate | Preserve atomic destination/manifest ownership; auto-PDF remains L31 capability work | included |
| L42 | MTG-03 | Complete and verified | Agent C | Agent E | `619adc3` | meeting retranscription admission and candidate test files | Per-meeting retained-audio admission precedes scratch; deterministic concurrent regression passes; combined focused L42–L45 tests are 100/100 | Retained-audio quality and broader meeting qualification remain evidence debt under MTG-03 | Preserve candidate admission boundary and physical qualification debt | included |
| L43 | AUTO-01 | Complete and verified | Agent D | Agent E | `be293f3` | `Services/PostMeetingAutomationService.cs`, `Muesli.Windows.Tests/Phase9AutomationTests.cs` | Manifest filename/path/SHA/destination/current-render integrity, user-modified-file preservation, and collision-free candidate publication tests pass; combined focused L42–L45 tests are 100/100 | Automatic PDF export remains missing; AUTO-01 remains Implemented with verification debt | Preserve manifest integrity and collision-free publication | included |
| L44 | data foundation / L27 | Complete and verified | Agent D | Agent E | `0922af2` | persistence migration snapshot services and tests | Immutable JSON, `.bak`, and dictionary snapshot is passed through one MigrationPlan; mutation regression passes; combined focused L42–L45 tests are 100/100 | Cloned-real-profile count/digest, forced rollback, and second-launch evidence remain open under L27 | Preserve one captured snapshot through migration and retain L27 verification debt | included |
| L45 | Phase 13 / PKG-01, TEST-01 | Complete and verified | Agent E | Agent E | `a5414b3` | `global.json`, release helper, focused release tests | Historical exact-SDK/package-smoke evidence remains; 10.0.400 is absent on the current host, while fallback 10.0.401 build is 0/0 | Current exact-SDK rerun is unavailable; signing, clean-VM, cross-run L48, and dual-shell L49 remain open elsewhere | Preserve exact-SDK guard and rerun after a green committed boundary on a host with 10.0.400 | included |
| L46 | MTG-03 | Complete and verified | Agent C | Agent E | `818fab2` | retranscription recovery/ownership files | Recovered stale flight cannot restore an older transcript over a newer accepted transcript; focused L46 tests are 27/27 in Debug and Release; independent review found no P0–P3 findings | Retained-audio quality and broader meeting qualification remain evidence debt under MTG-03 | Preserve stale-flight ownership boundary and physical qualification debt | included |
| L47 | DATA-01, ORG-01, QUAL-01 | Implemented with verification debt | Agent D | Agent E | `a5414b3` | cutover staging and migration capture files | Profile-local protected staging, stale cleanup, coherent capture/retry, cross-process lock through authority, and no-follow validation; focused L47 tests are 30/30 in Debug and Release; independent review found no P0–P3 findings | Real cloned-profile qualification has not run | Run cloned-profile qualification; keep ORG-01 and QUAL-01 statuses unchanged | included |
| L48 | PKG-01, TEST-01 | Partial | Agent E | Agent E | `a5414b3` + working tree | `Directory.Build.props`, four `packages.lock.json`, cross-path inventory comparator/tests | Lockfiles pin transitive restore and the deterministic cross-path comparator is implemented/tested | Clean committed two-checkout rehearsal and final artifact retention have not been run; dirty-tree packaging is forbidden | Two different-checkout-path rehearsals of one commit produce equal digests; retain artifacts under `artifacts/` | included |
| L49 | SHELL-01, ONB-01, TRAY-01, START-01, TEST-01, PKG-01 | Partial | Agent E | Agent E | `a5414b3` + working tree | active WinUI solution, shared Core/Platform composition, archived WPF source, MSIX/default launch scripts, lockfiles, UI-reference packet | 2026-09-16 production-profile screenshot and unsigned Debug MSIX are retained; 2026-09-17 diagnostic build compiles 0/0 and clean launch logs | Current activation exposed only a title-bar capture and later hid; migration is uncommitted; default Release/full suite are red under L52; signed/installed, real-workflow, and physical accessibility/DPI proof are absent; L50/L51 open | Recover visible dashboard activation; close L50/L51/L52; commit extraction; signed/registered MSIX and real workflow qualification | included |
| L50 | SHELL-01, TEST-01 | Partial | Agent E | Agent E | working tree | `Muesli.Windows.Core/Services/SettingsStore.cs`, settings tests, both shell startup/navigation paths | One Dictionary call-site guard exists; no centralized null-bearing JSON corpus | Live crash was reproduced in the WinUI parity review | Normalize after deserialize; both shells traverse settings-dependent routes from null/legacy fixtures with clean logs | included |
| L51 | SHELL-01, TEST-01 | Partial | Agent E | Agent E | working tree | `Muesli.Windows.WinUI/App.xaml.cs`, async UI command boundaries, focused tests | Known clipboard call sites are guarded; no global policy test | CR-01 proved an unguarded UI exception can terminate the shell | Implement/document targeted containment and retain live fault-injection evidence | included |
| L52 | TEST-01, PKG-01, MOD-04 | Partial | Agent E | Agent E | working tree | test project/source mappings, WinUI project/restore inputs, streaming qualification tests | 706 tests discovered; filtered current run 670/31/4 of 705; four skips explain prerequisites; diagnostic WinUI build 0/0 | No exact-SDK/default Release pass; no current UI suite | Restore/migrate lost coverage, remove stale WPF paths, bound scan, restore streaming skip, and pass default Release/full suite | included |

Review priority is always: data loss/security/privacy, crashes and false success, release truth/signing/update, core dictation and meeting correctness, then parity polish. Test count alone is never a priority signal.
