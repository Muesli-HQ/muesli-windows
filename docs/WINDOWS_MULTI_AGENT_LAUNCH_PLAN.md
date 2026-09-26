# Muesli Windows multi-agent launch plan

Review baseline: 2026-09-23, `codex/wave0-launch-foundation` at `ad92e43` with a 135-entry collapsed porcelain working tree (67 modified, 68 untracked / 589 expanded untracked files, nothing staged). The exact pinned 10.0.400 SDK is available only from the user-local .NET host; the default `dotnet` on `PATH` cannot resolve it. Through the user-local host, the active WinUI Release ReadyToRun build passes with **0 warnings / 0 errors**, and that baseline suite was **901 passed / 0 failed / 4 explicit prerequisite skips of 905** with the real shared Swift bridge. The retained 2026-09-21 unsigned MSIX still passes package smoke but predates the current dirty tree. Real CUDA/provider evidence now closes the L11 implementation gate, while L48/L49/L52, production signing, clean-machine qualification, broader hardware matrices, streaming coverage, and human quality review remain open.

2026-09-26 merge update: Windows work is in stacked draft PRs #23 and #31–#35; the shared Swift Core commit is in macOS PR #540. Windows CI cannot fetch its pinned Swift revision until #540 lands upstream. A clean checkout passes focused Core, UI, and packaging tests; with the locally cached Swift bridge copied into test output, its full suite passes **1083 / 0 failed / 5 named fixture skips of 1088**. The production-profile packaged app opens the Timeline dashboard and its Models and Meetings pages with a clean startup log. Current-source, source-backed MSIX rehearsal remains required before launch. Merge to main runs unsigned CI without deployment.

Authoritative product status remains `WINDOWS_LAUNCH_LEDGER.md`. Capability IDs and macOS behavioral source mappings remain in `WINDOWS_MACOS_PARITY_MATRIX.md`. This document is the execution and ownership plan: it does not replace either status source.

## Launch thesis

Muesli is not mainly behind because the native transcription engine is absent. The core application, current working-tree Release build, tests, unsigned package, and measured CUDA paths are green. The remaining gap is release qualification and the last product-completeness debt:

1. only 5 capability rows are **Complete and verified**, while 45 are **Implemented with verification debt**;
2. 11 rows are **Partial** and 1 is **Missing**;
3. the active exact-SDK Release path is green through the user-local host, but default-host resolution, clean two-checkout reproducibility, signed clean-machine install/upgrade/uninstall, and the streaming prerequisite contract remain open;
4. physical audio, real conferencing apps, paste targets, multi-monitor DPI, live providers, clean-machine installation, and signing cannot be proven by unit tests;
5. the highest-collision integration surfaces are now WinUI `App.xaml.cs`, `MainWindow.xaml*`, shared settings/runtime composition, and the page/view-model pairs for Meetings, Models, Settings, Shortcuts, Timeline, and Insights;
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

- `windows-native/Muesli.Windows.WinUI/App.xaml` and `App.xaml.cs`
- `windows-native/Muesli.Windows.WinUI/MainWindow.xaml` and `MainWindow.xaml.cs`
- `windows-native/Muesli.Windows.WinUI/MainPage.xaml` and `MainPage.xaml.cs`
- `windows-native/Muesli.Windows.WinUI/Pages/MeetingsPage.xaml*` and `MeetingDetailPage.xaml*`
- `windows-native/Muesli.Windows.WinUI/Pages/ModelsPage.xaml*`
- `windows-native/Muesli.Windows.WinUI/Pages/SettingsPage.xaml*`
- `windows-native/Muesli.Windows.WinUI/Pages/ShortcutsPage.xaml*`
- `windows-native/Muesli.Windows.WinUI/Pages/TimelinePage.xaml*`
- `windows-native/Muesli.Windows.WinUI/Pages/InsightsPage.xaml*`
- corresponding WinUI page view models under `windows-native/Muesli.Windows.WinUI/ViewModels/`
- `windows-native/Muesli.Windows.WinUI/Services/WinUiSettingsContext.cs`
- `windows-native/Muesli.Windows.Core/Services/SettingsStore.cs`
- `windows-native/Muesli.Windows.Core/Services/NativeSherpaRuntime.cs`
- `windows-native/Muesli.Windows.Platform/Services/MeetingDetectionService.cs`
- `windows-native/Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj`

Named reason for the 2026-09-20 lock rewrite: commits `8f0c867`–`ad92e43` made WinUI the tracked shipping shell and archived the WPF `FeatureRuntime.*` integration surface. The replacement list follows the active files that now carry shell composition, navigation, settings, native-provider selection, and meeting detection. Lane assignments remain unchanged.

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
| L10 | Twelve-model CPU catalog qualification | Phase 1 / MOD-02 | Qualification | — | Models/runtime |
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

Status: implemented in `SignedUpdateVerifier`/`SignedUpdateService`/`UpdateRollbackJournal` with `UpdateWorkflowTests`; the writer/verifier now emit and validate `minimumSupportedVersion`/release notes, About exposes honest check/status states, and `WindowsProductionUpdateInstaller` fails closed until D5. Remaining: production signing and signed install/upgrade evidence.

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

Status: implementation is present in `SupportBundleService` with the About page preview/save/cancel action; redaction, large-log bounding, locked-log tolerance, incident aggregation, and the no-network source contract are covered by `SupportBundleTests`. Human inspection of a real exported bundle and current UI automation remain open.

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

Status: `TranscriptionPipelineService` now shares cleanup → filler → dictionary across dictation, meeting, and import, with meeting/import filler applied per line body so speaker prefixes survive. `Txt03SharedTextOrderingTests` cover the cross-workflow golden order and opt-out default. Remaining: human corpus qualification (TXT-01/TXT-02).

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

Status: LM Studio and a documented custom OpenAI-compatible HTTP provider now share one chat-completions contract with Ollama, OpenAI, OpenRouter, and local behavior for timeout, cancellation, invalid response, disclosure, and redaction. `Phase7NotesTests` cover the mock contract; opt-in live-provider runs and log-redaction review remain open (SUM-01/SUM-02).

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

Status: automatic PDF export is implemented on the shared format-aware atomic exporter, gated by `MeetingDocumentWriter.PdfExportApproved` (EXP-01). `AutoPdfExportTests` cover fail-closed, atomic publish, independent Markdown+PDF artifacts, collision safety, and manifest reuse, and the restored active `MeetingExportTests` re-prove manual export content per mode, manual notes, aliases, determinism, filename safety, and the PDF gate. Remaining: the QuestPDF eligibility decision, human PDF open, and path/permission/locked-file evidence.

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

Review state: L40 remains Partial. Commits `8f0c867`–`ad92e43` now track the selected Core/Platform/WinUI solution, migrated tests, and archived WPF reference. The source-tracking half of the gate is repaired, but no clean isolated checkout has passed the exact-SDK default Release/test/package path with the pinned shared core, so reproducibility is not yet proven.

### L41 — Concurrent automatic Markdown export publication

Scope:

- make destination claim, temporary-file publication, and manifest ownership atomic across concurrent exporters;
- continue after a collision without reusing or clearing another run's temporary path;
- preserve exactly-once destination ownership, no user-file overwrite, and cleanup of service-owned temporary files;
- stress 8 rounds × 12 concurrent exports and retain the focused result with the release evidence.

Exit gate: met on 2026-09-14. The focused concurrency test passed 8 consecutive rounds × 12 concurrent exports with exactly one Markdown and no service-owned temporary files; the current 2026-09-23 exact-SDK suite is 901/0/4 and the historical release digest remains recorded. L41 is Complete and verified; remaining automatic PDF, SIGN-01/L05, and clean-VM PKG-01/L07 gates belong to their owning modules/capabilities.

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

Review state: L52 remains Partial but materially advanced. The active assembly now discovers **1080 tests** and completes **1075 passed / 0 failed / 5 explicitly skipped**. The stale repository-root/WPF-source assertions, unbounded `SharedProjectsContainNoWpfReferences` scan, ReadyToRun failure, warnings, and source-backed Swift link failure are repaired. The live-streaming real-fixture qualification test and its `MUESLI_STREAMING_QUALIFICATION_MODEL` skip contract are restored via the ported `Phase4LiveTranscriptionTests`, along with the Phase 10/12 anchors and the L41/L43/AUTO-01/EXP-01/HOOK-01 export/automation safety nets. Every skip names its media, multi-speaker, cloned-profile, or streaming prerequisite. The 2026-09-26 UI rerun is current (packaged shell 13/14, full UI project 17/24), so current UI automation is no longer only historical; the loose `bin/unpackaged` layout still has no build script and had to be regenerated by hand. Default-PATH pinned-host discovery, clean checkout, and current-source package proof remain open, so the module exit gate is not met.

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

1. **Agent E — L52 streaming/current-suite closure:** restore the `MUESLI_STREAMING_QUALIFICATION_MODEL` shared/WinUI qualification test, make the repository bootstrap select the user-local pinned host deterministically, then rerun the active WinUI Release build, full suite, and `Muesli.Windows.UITests`.
2. **Agent E — L49 current package/visible activation:** build a current-tree MSIX with `scripts/package-winui-msix.ps1`, run `scripts/test-windows-package.ps1`, second-activate it through `scripts/run-windows.ps1 -SkipBuild`, and retain a full dashboard screenshot plus fresh clean log slice.
3. **Agent A — L10 model quality:** run `scripts/smoke-transcription-models.ps1` and `scripts/test-transcription-corpus.ps1` on human-reviewed speech for all twelve advertised models; retain WER <=0.15, CER <=0.08, and RTF <=0.20 evidence.
4. **Agent B — L14/L15 dictation qualification:** run the reviewed WER/CER corpus, then four fresh `qualify-dictation-target.ps1` reports and `qualify-dictation-target-suite.ps1`; the only retained active-app trace is still 7,897 ms.
5. **Agent C — L18/L22 meeting qualification:** retain the green `scripts/benchmark-native-meeting.ps1` CPU baseline (RTF 0.063), then exercise the dedicated meeting-notification window and Zoom/Teams/Meet/Webex mic+system attribution with route-loss recovery.

Lane assignments did not change. The shared-file lock changed for the named reason above: the tracked WinUI cutover replaced the retired WPF `FeatureRuntime.*` collision surface.

## Review dashboard

Snapshot date: 2026-09-23 (previous snapshot 2026-09-21). All L00–L52 rows were re-audited. The shipping WinUI/Core/Platform/test cutover remains tracked at `ad92e43`, but L40 remains Partial until a clean isolated checkout passes the exact Release/test/package path. SDK 10.0.400 is available only through the user-local host; through it, the active WinUI Release ReadyToRun build is **0 warnings / 0 errors** and the current assembly completes **901 passed / 0 failed / 4 explicit prerequisite skips of 905**. Every skip names its fixture, but the streaming fixture test/skip remains absent. Historical UI automation remains 20/20 and was not rerun. CPU Parakeet evidence is RTF 0.055 for 5.56 s dictation and 0.063 for mic-only meeting; WER/CER remain unavailable. L11 advances to Implemented with verification debt on real optional-CUDA pack/provider evidence. No new gap requires L53: provider work remains L11, model/language work remains L10/L12, meeting notifications remain L22, and branding/activation remains L49. The dashboard carries 53 module rows, L00–L52: **9 Complete and verified, 30 Implemented with verification debt, 9 Partial, 2 Missing, and 3 Externally blocked**. Branch-only evidence is not trusted or merged.

| Module | Capability IDs | State | Owner | Integration owner | Base commit | Changed shared files | Automated evidence | Human/physical evidence | Remaining gate | Ledger update |
|---|---|---|---|---|---|---|---|---|---|---|
| L00 | Phase 0 | Complete and verified | Agent E | Agent E | `ad92e43` + working tree | `docs/WINDOWS_LAUNCH_LEDGER.md`, matrix, this plan | 2026-09-23 tree/docs/branches/artifacts audited; exact-SDK active build 0/0, suite 901/0/4, current CPU benchmarks retained | Current UI/package/clean-checkout/physical/signing evidence remains open under owning modules | Preserve reconciled statuses; split the 135-entry dirty tree before release rehearsal | included |
| L01 | data foundation | Complete and verified | Agent D | Agent E | `a43ff1a` + working tree | `PersistenceCutoverCharacterizationTests.cs` | Design/characterization suite remains green; the stale JSON `parentId` assertion was corrected under L27/L28 | Not required for design gate | Preserve characterization coverage; L01 design gate remains closed | not warranted |
| L02 | SHELL-01, FLOAT-01, FLOAT-02 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | WinUI manifest, `App.xaml*`, `MainWindow.xaml*`, shell/indicator assets and services | PerMonitorV2 manifest contract and current Release suite pass; L49 activation remains open | No full 100/125/150/200% two-monitor capture | Physical DPI matrix, keyboard/accessibility review, reliable activation, and clean-log captures | not warranted |
| L03 | MOD-01, PKG-01, EXP-01 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | CPU native catalog, WinUI package metadata, notices/licenses | `PackageDisclosureAndNativeInventoryTests` pass; CPU-only notices/inventory agree; mismatch rejection remains covered while the separately installed optional CUDA pack now verifies | QuestPDF eligibility not approved; retained MSIX is stale/dirty-tree/unsigned/unqualified for install | Current clean package inventory/smoke and release-owner license approval | included |
| L04 | PKG-01, TEST-01 | Complete and verified | Agent E | Agent E | `a5414b3` | none in focused commits | Prior clean two-build rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; historical exact-SDK rehearsal at `d432a26` matched digest `049c3c34f71dc71b60447743d897e6105255d67cc83cc584c8e6c0d526173de4`; final exact-SDK rehearsal at `a5414b3` matched digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf`; package smoke and PerMonitorV2 checks passed | No L04-specific gate remains; unsigned-package signing and clean-VM gates remain under PKG-01. 2026-08-22 isolated re-run at `a5414b3` matched two builds at digest `637a3433…`, not the recorded `ee860840…`; cross-run stability and artifact retention moved to L48 | Preserve module evidence; PKG-01 retains Partial status | included |
| L05 | SIGN-01 | Externally blocked | Agent E | Agent E | `3bff598` + working tree | signing/release scripts | Fixture/unsigned orchestration validates the package path; production certificate remains unavailable | No production certificate or timestamped app/installer signatures (D5) | Valid timestamped app and installer signatures on a clean machine | not warranted |
| L06 | UPD-01, API-04 | Implemented with verification debt | Agent E | Agent E | current working tree | `SignedUpdateVerifier.cs`, `SignedUpdateService.cs`, `UpdateRollbackJournal.cs`, `WindowsUpdateInstaller.cs`, `UpdateWorkflowTests.cs` | App-side check/verify/download/rollback with downgrade, minimum-supported, offline, cancellation, and tamper coverage; production installer fails closed | Production signing (D5) and signed install/upgrade evidence remain open | Configure production signing, then qualify install/upgrade/rollback on a clean machine | included |
| L07 | PKG-01 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | MSIX install/test scripts and release report | 2026-09-18 dirty-tree MSIX content/smoke checks passed, but signature validation prevented install/launch | No current signed Win10/Win11 clean-VM run | Signed install/upgrade/uninstall reports on supported clean VMs | not warranted |
| L08 | DIAG-01 | Implemented with verification debt | Agent E | Agent E | current working tree | `SupportBundleService.cs`, About page/view model, `SupportBundleTests.cs`, WinUI UI test | Preview and save/cancel export of a bounded, redacted bundle (environment, package identity, model/provider state, audio categories, incident categories, log slice); redaction, large-log, locked-log, incident, and no-network tests pass | Human inspection of a real exported bundle and current UI automation remain open | Preserve the redaction and size-bounding contract | not warranted |
| L09 | TEST-01 | Implemented with verification debt | Agent E | Agent E | current working tree | WinUI UI session/tests, meeting-notification automation, shell startup files | 2026-09-26 current UI rerun: packaged shell qualification 13/14, full UI project 17/24 (failures are floating-indicator secondary-window automation and one unpackaged second-instance dashboard-return case); active unit/integration suite is 1081/0/5 of 1086 | Floating-indicator automation, Narrator, High Contrast, non-125% DPI, and multi-monitor remain open | Fix the indicator/second-instance UI cases and collect named physical accessibility/display evidence | included |
| L10 | MOD-02 | Implemented with verification debt | Agent A | Agent E | `ad92e43` + working tree | model catalog/language/runtime qualification code and scripts | Twelve advertised choices and truthful per-model language contracts pass in the 901/0/4 suite; 2026-09-23 Parakeet CPU is deterministic/reused at RTF 0.055 | Reviewed speech corpus and WER/CER remain absent | Run reviewed corpus and record WER/CER/RTF for all twelve advertised models | pending |
| L11 | MOD-01, QUAL-01 | Implemented with verification debt | Agent A | Agent E | `ad92e43` + working tree | CUDA pack/provenance, native runtime, provider policy/settings/UI, qualification scripts/reports | Pinned optional pack verifies; real per-model CUDA graph evidence exists; Automatic correctly keeps Parakeet v3 on CPU at RTF 0.055 where measured CUDA offers no gain | Only one RTX 4070 Laptop host; no alternate NVIDIA generation/driver or long CUDA meeting soak | Run broader NVIDIA/driver matrix and long meeting soak; keep public MSIX CPU-only | included |
| L12 | MOD-03 | Implemented with verification debt | Agent A | Agent E | `ad92e43` + working tree | WinUI Models page/view model and shared lifecycle/provider/language services | Lifecycle/catalog/language/provider tests pass in the current 901/0/4 suite | No destructive Models-page run after current provider/UI edits | Recorded all-state UI/destructive cache matrix | not warranted |
| L13 | MOD-05 | Externally blocked | Agent A | Agent E | `3bff598` | none | Manual-placement fail-closed tests only | Approved GGUF/hash/license/prompt contract absent | Product-approved cleanup model contract and lifecycle | not warranted |
| L14 | DIC-01, TXT-01, TXT-02 | Implemented with verification debt | Agent B | Agent E | `ad92e43` + working tree | dictionary, dictation runtime, and qualification contracts | 2026-09-23 5.56 s CPU Parakeet warm wall is 305 ms / RTF 0.055; deterministic/model reuse pass | No reviewed references; human WER/CER remains absent | Run reviewed corpus at WER <=0.15, CER <=0.08, RTF <=0.20 | pending |
| L15 | DIC-02, DIC-03, HOT-01, HOT-02, API-03 | Implemented with verification debt | Agent B | Agent E | `ad92e43` + working tree | WinUI timeline/library views and view models, shared `DictationCoordinator.cs`, paste/hotkey services and qualification scripts | Exact four-app qualification gate exists; latest 2026-09-09 ChatGPT-only sample is 7,897 ms release-to-paste with 0 paste failures | Physical Notepad/Chrome/Office/second-editor, real-hook, and UIPI evidence remain open | Run four target reports, hotkey/UIPI matrix, and representative <=3,000 ms median/p95 evidence | pending |
| L16 | AUD-01, AUD-02 | Implemented with verification debt | Agent B | Agent E | `ad92e43` + working tree | shared Core/Platform audio services and Phase 2 tests | Route-policy tests pass in the current 901/0/4 suite and distinguish default/communications behavior | No physical array/jack/USB/Bluetooth/privacy/sleep route matrix | Run default/unplug/Bluetooth/privacy/sleep matrix | not warranted |
| L17 | TXT-03 | Implemented with verification debt | Agent B | Agent E | current working tree | `TranscriptionPipelineService.cs`, `WinUiMeetingContext.cs`, `Txt03SharedTextOrderingTests.cs` | Cleanup → filler → dictionary shared across dictation/meeting/import; meeting/import filler is per line body and prefix-preserving, gated by `RemoveFillerWords`; cross-workflow golden tests pass | Human microphone/name/accent corpus remains under TXT-01/TXT-02 | Preserve the ordering and prefix contract | not warranted |
| L18 | MTG-01, API-01 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | capture/process attribution and meeting benchmark harness | 2026-09-23 mic-only Parakeet CPU run is deterministic with ASR reuse: 348 ms warm, RTF 0.063 (pass) | No system-audio/diarization input or live Zoom/Teams/Meet/Webex run | Retain four-app listening/attribution and route-loss matrix | included |
| L19 | MTG-02 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | lifecycle harness/reuse-gate validation | Model-reuse gate is green for mic-only semantics in the 2026-09-23 benchmark and current lifecycle tests pass | No forced-kill/disk-full or current two-track physical run | Fault injection plus real recovery proof | not warranted |
| L20 | MOD-04, LIVE-01, LIVE-02, LIVE-04, FLOAT-02 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | WinUI live transcript window plus shared live-session services | Implementation and non-fixture tests remain in Core/WinUI; current discovery is 905, but `MUESLI_STREAMING_QUALIFICATION_MODEL` is still absent | No current real streaming fixture, long meeting/noisy-room/route/DPI soak | L52 restores a WinUI/shared streaming fixture gate; then run the fixture and soak | included |
| L21 | DIA-01, DIA-02 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | WinUI meeting detail/list, `MeetingItem.cs`, meeting runtime files | Finalization tests pass; multi-speaker test still skips and names `MUESLI_MULTISPEAKER_FIXTURE_SOURCE` | No reviewed multi-speaker/alias UI run | Multi-speaker test must run and UI alias round-trip pass | not warranted |
| L22 | DET-01, DET-02, JOIN-01 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | bounded scan gate, meeting notification contracts/window/service, detection service | Detection, suppression, action, wiring, layout, and notification automation contracts pass | No live conferencing false-positive/negative matrix; new window not retained in current production run | Live prompt/join/leave/rejoin evidence through the dedicated notification window | included |
| L23 | MTG-03 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | WinUI meeting detail/navigation plus shared transcript-edit services | Transcript save/cancel/candidate/recovery tests pass in the current suite; historical combined UI is 20/20 | Retained-audio quality and current physical meeting UI evidence remain open | Preserve shared lock and add physical retained-audio review | included |
| L24 | PLAY-01 | Implemented with verification debt | Agent C | Agent E | `ad92e43` + working tree | shared playback/waveform services and WinUI meeting-detail shell | Async waveform sampling/cache/cleanup and playback contract tests pass in the current 901/0/4 suite | No retained live waveform or physical output-device-loss run | GUI playback/device-loss evidence | included |
| L25 | SUM-01, SUM-02 | Implemented with verification debt | Agent D | Agent E | current working tree | `MeetingSummaryService.cs`, `SummaryProviderDisclosure.cs`, settings/UI, `Phase7NotesTests.cs` | LM Studio and custom OpenAI-compatible HTTP share the chat-completions contract; endpoint/path, keyless/bearer, missing-model/invalid-endpoint, failure reasons, and loopback/remote disclosure are covered | Opt-in live-provider runs and log-redaction review remain open | Run redacted live qualification per provider | not warranted |
| L26 | SUM-04, TPL-01, NOTE-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Notes/title/template unit coverage | No GUI/live-provider matrix | GUI ownership/cancel/timeout/retry evidence | not warranted |
| L27 | data foundation | Implemented with verification debt | Agent D | Agent E | `ad92e43` + working tree | persistence adapters, migration/cutover tests | Nested migration/folder persistence, explicit root move, and rollback coverage are green in the current Release suite; cloned-profile prerequisite remains an explicit `MUESLI_CLONED_PROFILE_DIR` skip | No cloned-profile counts/digests, second-launch, or forced-failure run | Run cloned-profile qualification and preserve backup/rollback evidence | included |
| L28 | ORG-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` + working tree | `MeetingFolderItem.cs`, folder runtime/UI, persistence adapters | Move-to-root/parent UI, delete/reparent, cycle rejection, SQLite missing-row parity, and crash-journal recovery across JSON folder/meeting files are green | Retained breadcrumb/tree/move/delete GUI exercise remains absent | Run retained GUI folder workflow and cloned-profile nested data review | included |
| L29 | SEARCH-01 | Implemented with verification debt | Agent D | Agent E | `ad92e43` + working tree | shared search repositories/adapters plus WinUI search view/view model | Functional JSON/SQLite ordered paging, manual notes/snippets, >500-result assertions, and current large-history tests pass | No retained GUI run or large real-profile latency report | Run GUI/real-profile latency evidence | included |
| L30 | IMP-01 | Implemented with verification debt | Agent D | Agent E | `ad92e43` | none | Import unit tests pass; both real-media tests explicitly skip and name `MUESLI_MEDIA_FIXTURE_DIR` | No reviewed eight-format speech set | Run real-media tests and manifest across all advertised formats | not warranted |
| L31 | EXP-01, AUTO-01 | Implemented with verification debt | Agent D | Agent E | current working tree | `PostMeetingAutomationService.cs`, `MeetingDocumentWriter.cs`, `AutoPdfExportTests.cs` | Automatic PDF shares the format-aware atomic exporter with a per-format control key/manifest and honors the EXP-01 gate; tests cover fail-closed, atomic publish, independent artifacts, collision safety, and manifest reuse | QuestPDF eligibility and human PDF open remain open; file failure cases pending | Record license decision, human open, and path/permission/locked-file evidence | not warranted |
| L32 | FOLLOW-01 | Missing | Agent D | Agent E | `3bff598` | none | Persistence link substrate tests only | No user-facing linked workflow | User-facing linked follow-up workflow through production store | not warranted |
| L33 | HOOK-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Hook success/failure/timeout tests pass | No packaged benign executable smoke | Packaged real `.exe` smoke with redaction review | not warranted |
| L34 | ONB-01, TRAY-01, START-01 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | WinUI onboarding/tray/startup files plus shared startup services | Exact-command/icon/startup contracts pass; historical UI is 20/20; WinUI honestly disables startup without package identity | No clean-profile VM/startup upgrade or registered-MSIX run | Collect clean-profile onboarding/tray/startup/install evidence | not warranted |
| L35 | FLOAT-01, SOUND-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` + working tree | `ToastNotificationService.cs`, `SoundFeedbackService.cs`, `MeetingLiveTranscriptWindow.xaml*` | Indicator/sound contract tests pass; final Wave 3 qualification includes **352 DPI matrix cells** | No physical audible cue/device-route review | Multi-monitor captures and audible route matrix | not warranted |
| L36 | PRIV-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Path-boundary/redaction/cleanup tests pass | No destructive cloned prepared-data run | Full deletion/cache/retention disclosure review | not warranted |
| L37 | CU-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Planner/allowlist/redaction tests pass | No sandboxed local/browser workflow | Two live sandbox workflows with clean logs | not warranted |
| L38 | INSIGHT-01 | Partial | Agent D | Agent E | `ad92e43` + working tree | `InsightsWordAnalyzer.cs`, WinUI Insights page/view model, share-image service | Focused tests cover stop words, range totals, streaks, activity, local word lists, and transcript-free image sharing | Source/deletion disclosure has no retained human review; contribution is absent and D4-gated | Retain local-only behavior; review deletion/source copy and leave contribution absent until D4 | included |
| L39 | QUAL-01 | Partial | Agent E | Agent E | `ad92e43` + working tree | all release-owned files at freeze; 135-entry collapsed dirty tree | User-local exact-SDK WinUI Release build is 0/0; suite is 901/0/4; CPU dictation/meeting RTF 0.055/0.063 pass; optional CUDA evidence is real | Signing, clean-VM, physical routes/meetings, WER/CER, accessibility, current-source package, L48/L49/L52, and prerequisite fixtures remain absent | Close L52, freeze a reviewed commit, then rerun full candidate qualification | included |
| L40 | Phase 0/13, TEST-01, PKG-01 | Partial | Agent E | Agent E | `8f0c867`–`ad92e43` + working tree | `.gitignore`, active solution, Core/Platform/WinUI source, archived WPF reference, shared-core bridge staging | Selected shipping source/solution and migrated tests are tracked; user-local exact-SDK build is 0/0 and suite is 901/0/4 with the source-backed bridge | No clean isolated exact-SDK Release/test/package pass; default PATH still resolves a host that cannot see the pin | Prove a clean isolated checkout builds/tests/packages with deterministic pinned-host discovery and no local-only files | included |
| L41 | AUTO-01 | Complete and verified | Agent D | Agent E | `2972192` + current working tree | shared `PostMeetingAutomationService.cs`, `PostMeetingAutoExportTests.cs` | The re-ported active `ConcurrentAutoExportsPublishExactlyOneMarkdownFile` reruns 8 rounds × 12 concurrent exports with exactly one Markdown and no temporary files | Not required for this concurrency gate | Preserve atomic destination/manifest ownership | included |
| L42 | MTG-03 | Complete and verified | Agent C | Agent E | `619adc3` | meeting retranscription admission and candidate test files | Per-meeting retained-audio admission precedes scratch; deterministic concurrent regression passes; combined focused L42–L45 tests are 100/100 | Retained-audio quality and broader meeting qualification remain evidence debt under MTG-03 | Preserve candidate admission boundary and physical qualification debt | included |
| L43 | AUTO-01 | Complete and verified | Agent D | Agent E | `be293f3` + current working tree | `Services/PostMeetingAutomationService.cs`, `Muesli.Windows.Tests/PostMeetingAutoExportTests.cs` | The retired export-integrity suite is restored to the active tree: manifest filename/path/SHA/destination/current-render integrity, user-modified-file preservation, collision-free publication, unowned-control-directory fail-closed, temp-collision preservation, and stale-claim recovery | AUTO-01 remains Implemented with verification debt only for the QuestPDF eligibility decision and human/failure-case evidence | Preserve manifest integrity and collision-free publication | included |
| L44 | data foundation / L27 | Complete and verified | Agent D | Agent E | `0922af2` | persistence migration snapshot services and tests | Immutable JSON, `.bak`, and dictionary snapshot is passed through one MigrationPlan; mutation regression passes; combined focused L42–L45 tests are 100/100 | Cloned-real-profile count/digest, forced rollback, and second-launch evidence remain open under L27 | Preserve one captured snapshot through migration and retain L27 verification debt | included |
| L45 | Phase 13 / PKG-01, TEST-01 | Complete and verified | Agent E | Agent E | `ad92e43` | `global.json`, release helper, focused release tests | The exact-SDK guard remains enforced; user-local 10.0.400 active WinUI Release build is 0/0 and suite is 901/0/4 | Default PATH does not discover the user-local pin; signing, clean-VM, cross-run L48, and WinUI L49/L52 remain open elsewhere | Preserve exact-SDK guard and make pinned-host discovery deterministic in release entry points | included |
| L46 | MTG-03 | Complete and verified | Agent C | Agent E | `818fab2` | retranscription recovery/ownership files | Recovered stale flight cannot restore an older transcript over a newer accepted transcript; focused L46 tests are 27/27 in Debug and Release; independent review found no P0–P3 findings | Retained-audio quality and broader meeting qualification remain evidence debt under MTG-03 | Preserve stale-flight ownership boundary and physical qualification debt | included |
| L47 | DATA-01, ORG-01, QUAL-01 | Implemented with verification debt | Agent D | Agent E | `a5414b3` | cutover staging and migration capture files | Profile-local protected staging, stale cleanup, coherent capture/retry, cross-process lock through authority, and no-follow validation; focused L47 tests are 30/30 in Debug and Release; independent review found no P0–P3 findings | Real cloned-profile qualification has not run | Run cloned-profile qualification; keep ORG-01 and QUAL-01 statuses unchanged | included |
| L48 | PKG-01, TEST-01 | Partial | Agent E | Agent E | `ad92e43` + working tree | `Directory.Build.props`, lockfiles, cross-path inventory comparator/tests | A 2026-09-18 dirty-tree MSIX rehearsal matched two content inventories at digest `9228b1fb…`; lockfiles/comparator remain covered | The rehearsal used `a5414b3`, dirty override, local shared-core override, unsigned package, and placeholder publisher; no clean two-checkout current-commit evidence | Two different-checkout-path rehearsals of one clean commit produce equal digests; retain final artifacts under `artifacts/` | included |
| L49 | SHELL-01, ONB-01, TRAY-01, START-01, TEST-01, PKG-01 | Partial | Agent E | Agent E | current working tree | active WinUI solution, shared Core/Platform composition, archived WPF source, MSIX/default launch scripts, lockfiles, UI-reference packet, branding/assets | Migration is tracked; suite is 1081/0/5 of 1086; 2026-09-26 packaged UI qualification is 13/14 (shell) and 17/24 (full project); retained 2026-09-21 MSIX smoke passes | Floating-indicator/second-instance UI cases still fail; retained MSIX predates the current tree; signed install/real workflows/physical accessibility-DPI remain open | Build/smoke/activate a current MSIX, fix the remaining UI cases, and collect physical accessibility/DPI evidence | included |
| L50 | SHELL-01, TEST-01 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | `Muesli.Windows.Core/Services/SettingsStore.cs`, settings tests, WinUI settings/startup paths | Central post-deserialization normalization, malformed/null/legacy corpus, and reflection coverage pass in the current suite | No retained live null-heavy production-profile navigation across all settings-dependent pages | Run the null-heavy profile through current WinUI navigation and retain clean logs | included |
| L51 | SHELL-01, TEST-01 | Implemented with verification debt | Agent E | Agent E | `ad92e43` + working tree | `UiExceptionPolicy.cs`, `Muesli.Windows.WinUI/App.xaml.cs`, focused tests | Explicit recoverable/fatal classification, redacted diagnostics, reentrancy guard, and exactly-once shutdown tests pass | No retained live fault-injection evidence | Run recoverable and fatal WinUI fault injection and retain redacted logs/process outcome | included |
| L52 | TEST-01, PKG-01, MOD-04 | Partial | Agent E | Agent E | current working tree | test project/source mappings, WinUI project/restore inputs, Phase 4/10/12 anchors, streaming qualification, export/automation safety nets, shared-core staging | Active suite discovers 1080 and completes 1075/0/5; the streaming fixture and its skip contract are restored; stale paths are fixed and every skip explains its prerequisite | Default PATH cannot resolve the pin; current UI automation and current-source package remain open | Make pinned-host discovery deterministic and pass current full/UI/package suites | included |

Review priority is always: data loss/security/privacy, crashes and false success, release truth/signing/update, core dictation and meeting correctness, then parity polish. Test count alone is never a priority signal.
