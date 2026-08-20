# Muesli Windows multi-agent launch plan

Review baseline: 2026-08-20, `codex/wave0-launch-foundation` at `2972192`; L41 fixes the concurrent auto-export race and the final clean isolated package rehearsal passed.

Authoritative product status remains `WINDOWS_LAUNCH_LEDGER.md`. Capability IDs and macOS behavioral source mappings remain in `WINDOWS_MACOS_PARITY_MATRIX.md`. This document is the execution and ownership plan: it does not replace either status source.

## Launch thesis

Muesli is not mainly behind because the native transcription engine is absent. The core application is broad, the current integrated Release suite has 742 discovered cases, and native CPU dictation is fast. The gap is the final third of product work:

1. only 5 capability rows are **Complete and verified**, while 41 are **Implemented with verification debt**; the current integrated Release suite is 738 passed, 0 failed, and 4 explicit prerequisite skips (742 discovered cases);
2. 13 rows are **Partial** and 2 are **Missing**;
3. several user-facing workflows still run through the legacy JSON `AppDataStore`, while the new SQLite repositories already contain stronger folder, search, follow-up, migration, and transactional behavior;
4. physical audio, real conferencing apps, paste targets, multi-monitor DPI, live providers, clean-machine installation, and signing cannot be proven by unit tests;
5. the highest-collision WPF integration files remain large, especially `FeatureRuntime.Meetings.cs`, `FeatureRuntime.xaml.cs`, `FeatureRuntime.Dictations.cs`, `FeatureRuntime.Models.cs`, and `FeatureRuntime.Settings.cs`;
6. the PerMonitorV2 manifest and CPU-only public-package disclosures are corrected in source, but the retained 2026-08-17 package predates those fixes; the prior clean two-build L04 rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`, and the final clean isolated rehearsal at L41 commit `2972192` matched digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`.

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

The highest-value achievable parity gaps are already known and are represented below: transcript edit/retranscribe (L23), playback waveform (L24), LM Studio/custom summary contract (L25), nested folder UI (L28), manual-note/full-content search (L29), automatic PDF export (L31), linked follow-up meetings (L32), structured support export (L08), and a signed update channel (L06). Literal Sparkle, ScreenCaptureKit, CoreML, AppKit, TCC, and EventKit designs remain excluded even when the user-visible behavior has a Windows equivalent.

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

Exit gate: two clean builds produce matching content inventories and all CI artifacts needed for review. The prior clean rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; after L41 commit `2972192` fixed the concurrent auto-export race, the final clean isolated rehearsal matched digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e`. L04 is complete as a module; PKG-01 remains Partial until the unsigned-package signing and clean-VM gates close.

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

Exit gate: local signed-fixture tests for upgrade, downgrade rejection, tamper rejection, cancellation, offline behavior, and rollback. Production completion waits for L05.

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

Exit gate: macOS-equivalent behavioral flow, destructive-failure tests, and meeting-detail GUI automation. Current focused live flow is 1/1 and the full elevated Windows UI suite is 6/6; retained-audio quality and broader meeting qualification remain verification debt.

### L24 — Playback waveform and device-loss behavior

Scope:

- generate/cache waveform data off the UI thread;
- seek from waveform, preserve track selection, handle missing/corrupt audio and output-device loss;
- delete orphaned waveform caches with recording deletion.

Exit gate: waveform accuracy/cache tests, playback GUI test, and physical output-device loss recovery.

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

Exit gate: prepared real-profile clone migrates with equal counts/digests, the second launch performs no duplicate migration, and forced failures retain the old data intact. The gated implementation and rollback fix are in `f0dcef3` and `53dc577`; focused L23/L27 tests currently pass 42/42, while cloned-profile evidence remains open.

### L28 — Nested folder product UI

Scope:

- expose repository `ParentId`, child listing, ancestry, subtree moves, cycle rejection, reordering, breadcrumbs, and safe delete/reparent behavior;
- support moving meetings and searching within a subtree;
- preserve one-level migrated folders as roots.

Exit gate: nested-folder GUI automation and repository tests pass; ORG-01 becomes **Complete and verified** only when the user-facing tree is present.

### L29 — Full-content search

Scope:

- route product search through `ISearchRepository` rather than the in-memory filter;
- index title, transcript, generated notes, manual notes, aliases, follow-ups, dictionary text where intended, and folder ancestry;
- add snippets/highlights, type and folder filters, stable sorting, large-history latency, and transactional freshness.

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

### L41 — Concurrent automatic Markdown export publication

Scope:

- make destination claim, temporary-file publication, and manifest ownership atomic across concurrent exporters;
- continue after a collision without reusing or clearing another run's temporary path;
- preserve exactly-once destination ownership, no user-file overwrite, and cleanup of service-owned temporary files;
- stress 8 rounds × 12 concurrent exports and retain the focused result with the release evidence.

Exit gate: the focused concurrency stress publishes exactly one Markdown with no temporary files, the integrated Debug/Release suite is green, and the final release rehearsal/package digest is recorded. Commit `2972192` passes the 96-run focused stress, full Release 738/4/0, Debug 0/0, package smoke, and clean isolated two-build digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`. L41 is Complete and verified; remaining SIGN-01/L05 and clean-VM PKG-01/L07 gates belong to their owning modules.

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
- Agent D: L01 + L27 cutover design and production behavior characterization.

Wave exit: tracked source is reproducible from a clean checkout, ledger corrected, DPI/package-truth defects fixed, persistence cutover approved, shared-file ownership enforced, full suite/package smoke green.

### Wave 2 — Prove the native core on real hardware

- A: L10, L11, L12.
- B: L14, L15, L16, then L17.
- C: L18, L19, L21.
- D: begin L27 migration implementation behind an adapter/feature gate.
- E: L04 and L08; support qualification evidence collection.

Wave exit: CPU catalog, dictation, paste, devices, meeting capture/lifecycle/diarization have current evidence; SQLite migration passes cloned-profile tests.

### Wave 3 — Close meeting and library parity

- A/C jointly but sequentially: L20 live stack.
- C: L22, L23, L24.
- D: finish L27, then L28, L29, L32.
- B: regression support for audio/text/paste.
- E: expand L09 automation around the new flows.

Wave exit: safe transcript editing/retranscription, waveform, detection, nested folders, full search, and linked follow-ups work through production persistence.

### Wave 4 — Finish knowledge workflows and product shell

- D: L25, L26, L30, L31, L33, L41.
- E: L34, L35, L36, L37.
- A/B/C: provider, audio, import, export, and meeting regression support.

Wave exit: all achievable parity workflows are implemented; remaining work is current-candidate qualification or explicit external signing dependency.

### Wave 5 — Release channel and candidate

- E: L05, L06, L07.
- All agents: L39 evidence in their stable lanes.
- D: L38 only if the core candidate is already green and D4 permits local-only scope.

Wave exit: signed, upgradeable, clean-machine-qualified release candidate or a precise external blocker report naming only D5.

## First five assignments to start now

1. **Agent D — L27 qualification:** run `dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PersistenceCutoverImplementationTests"` with `MUESLI_SQLITE_HISTORY_CUTOVER=1` against a cloned profile; close on equal counts/digests, forced-failure rollback, and second-launch idempotence. Runtime composition and the focused 42/42 flow are already wired and green.
2. **Agent A — L10 qualification:** provide the reviewed WAV corpus, then run `scripts/smoke-transcription-models.ps1 -Configuration Release -AudioPath <reviewed-wav> -CatalogPath qualification/cpu-catalog/advertised-cpu-models.json -Prepare`; close with all seven CPU model identities and WER/CER/RTF reports.
3. **Agent B — L14/L15 qualification:** run `scripts/qualify-dictation-corpus.ps1` for the reviewed human corpus and `scripts/qualify-dictation-target-suite.ps1` over one passed report each for Notepad, Chrome, Office, and Other; close only when reviewer/device/UIPI evidence sets `qualified=true`.
4. **Agent E — L08 implementation:** add the user-visible redacted support bundle and `SupportBundleServiceTests` covering preview, cancellation, large logs, file errors, and no-network behavior; close with `dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SupportBundleServiceTests"` plus human redaction review.
5. **Agent C — L24 implementation:** add waveform generation/cache, seek integration, and output-device-loss recovery; close with `dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PlaybackWaveformTests"` and the meeting-detail playback UI test.

No lane assignment or shared-file lock changed in this review. L40, L04, L09, and L41 are evidenced in the dashboard; L23 is implemented with verification debt and its focused/UI flow is recorded. The five assignments above are unblocked next work; remaining physical/current-package gates ride with L02/L07/L34/L35/L39.

## Review dashboard

Snapshot date: 2026-08-20. “Current suite” means the integrated Release run recorded in `WINDOWS_LAUNCH_LEDGER.md`: 738 passed, 0 failed, 4 explicit prerequisite skips (742 discovered cases). Integrated Debug and Release app builds each have 0 warnings and 0 errors; focused L23/L27 tests are 42/42, the L41 concurrency stress is 96/96, and the elevated Windows UI suite is 6/6. The prior clean L04 rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; final clean isolated package rehearsal at `2972192` matched digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`. Branch-only evidence is not trusted or merged.

| Module | Capability IDs | State | Owner | Integration owner | Base commit | Changed shared files | Automated evidence | Human/physical evidence | Remaining gate | Ledger update |
|---|---|---|---|---|---|---|---|---|---|---|
| L00 | Phase 0 | Complete and verified | Agent E | Agent E | `2972192` | `docs/WINDOWS_LAUNCH_LEDGER.md`, matrix, this plan | Integrated Debug/Release builds 0 warnings/0 errors; Release 738/0/4; status-count and skip-message audit | Current foreground dashboard and evidence packet recorded | Preserve the reconciled authoritative statuses | included |
| L01 | data foundation | Complete and verified | Agent D | Agent E | `a43ff1a` | none | `PersistenceCutoverCharacterizationTests`; current suite green | Not required for design gate | Design, rollback, adapters, and characterization exist; implementation is L27 | not warranted |
| L02 | SHELL-01, FLOAT-01, FLOAT-02 | Implemented with verification debt | Agent E | Agent E | `e2c0709` | `Muesli.Windows.csproj` | `DpiManifestAndPlacementTests`; built manifest assertion passes | Foreground dashboard pass; no full 100/125/150/200% two-monitor capture | Physical DPI matrix and clean-log captures | not warranted |
| L03 | MOD-01, PKG-01, EXP-01 | Implemented with verification debt | Agent E | Agent E | `9975786` | none | `PackageDisclosureAndNativeInventoryTests`; CPU-only notices/inventory agree | QuestPDF eligibility not approved; retained package is stale/unsigned | Current-tree package inventory/smoke and release-owner license approval | included |
| L04 | PKG-01, TEST-01 | Complete and verified | Agent E | Agent E | `4f408f4` | none in focused commits | Prior clean two-build rehearsal matched digest `9fc70dc5ceb10daf279d5ef0559ff503e60aa7334c3656ba76687fa57882289e`; final clean isolated rehearsal at `2972192` matched digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`; package smoke and PerMonitorV2 checks passed | No L04-specific gate remains; unsigned-package signing and clean-VM gates remain under PKG-01 | Preserve module evidence; PKG-01 retains Partial status | included |
| L05 | SIGN-01 | Externally blocked | Agent E | Agent E | `3bff598` | none | Unsigned rehearsal scripts only | No production certificate (D5) | Valid timestamped app and installer signatures | not warranted |
| L06 | UPD-01, API-04 | Externally blocked | Agent E | Agent E | `3bff598` | none | No production signed-channel implementation | D5 absent | Signed fixture update/rollback implementation, then production signing | not warranted |
| L07 | PKG-01 | Implemented with verification debt | Agent E | Agent E | `4f408f4` | none | Final clean isolated package smoke passed; ZIP/installer and CPU-only inventory are reproducible | No current Win10/Win11 clean-VM run | Install/upgrade/uninstall reports on supported clean VMs | not warranted |
| L08 | DIAG-01 | Missing | Agent E | Agent E | `1829702` | none | Redaction/runtime status unit coverage only | No user-visible support bundle | Previewable redacted export with cancellation and file-error tests | not warranted |
| L09 | TEST-01 | Complete and verified | Agent E | Agent E | `74a0a97` | none | Out-of-process harness plus meeting-detail fixture; elevated UI suite 6 passed, 0 skipped, 0 failed | Human accessibility/DPI gates remain with L02/L34/L35 | Preserve stable production-startup/navigation smoke | included |
| L10 | MOD-02 | Implemented with verification debt | Agent A | Agent E | `c9278ee` | none | Seven-model inventory/manifests and fail-closed smoke tooling | No reviewed real-speech run; CPU smoke names missing WAV | Run all seven CPU models with WER/CER/RTF and provider identity | pending |
| L11 | MOD-01, QUAL-01 | Partial | Agent A | Agent E | `8c93279` | none | CUDA provenance-gap docs/tests; current runtime reports CPU | No packaged NVIDIA run or matching public bundle | Stage version-matched provider and complete NVIDIA qualification | not warranted |
| L12 | MOD-03 | Implemented with verification debt | Agent A | Agent E | `3bff598` | none | Lifecycle unit tests in integrated suite | No destructive Models-page run | Recorded all-state UI/destructive cache matrix | not warranted |
| L13 | MOD-05 | Externally blocked | Agent A | Agent E | `3bff598` | none | Manual-placement fail-closed tests only | Approved GGUF/hash/license/prompt contract absent | Product-approved cleanup model contract and lifecycle | not warranted |
| L14 | DIC-01, TXT-01, TXT-02 | Implemented with verification debt | Agent B | Agent E | `f67584f` | none | Locale-safe corpus parser; benchmark traces report RTF 0.147 and 12/12 dictation success | No reviewed references; WER/CER unavailable; human qualification false | Reviewed corpus at WER <=0.15, CER <=0.08, RTF <=0.20 | pending |
| L15 | DIC-02, DIC-03, HOT-01, HOT-02, API-03 | Implemented with verification debt | Agent B | Agent E | `bf04aa2` | `FeatureRuntime.Dictations.cs` | Exactly-four-report suite and clipboard-safe direct-input tests pass; 12 trace deliveries had 0 failures | Four current target reports, hotkey/UIPI matrix, and human target review remain open | Four target reports plus hotkey/UIPI matrix | pending |
| L16 | AUD-01, AUD-02 | Implemented with verification debt | Agent B | Agent E | `3bff598` | none | Recovery/unit coverage in integrated suite | No current physical route matrix | Default/unplug/Bluetooth/privacy/sleep matrix | not warranted |
| L17 | TXT-03 | Externally blocked | Agent B | Agent E | `3bff598` | none | Existing workflow-specific tests | Product ordering decision absent | One approved cross-workflow pipeline and goldens | not warranted |
| L18 | MTG-01, API-01 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Capture/process-attribution unit coverage; mic-only benchmark deterministic at RTF 0.154 | No live Zoom/Teams/Meet/Webex run | Retained four-app listening/attribution matrix | not warranted |
| L19 | MTG-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Lifecycle/recovery unit coverage | No forced-kill/disk-full physical run | Fault injection plus real recovery proof | not warranted |
| L20 | MOD-04, LIVE-01, LIVE-02, LIVE-04, FLOAT-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Streaming contract tests pass; real fixture still skips explicitly | No long meeting/noisy-room/route/DPI soak | Run streaming fixture and long-soak limits | not warranted |
| L21 | DIA-01, DIA-02 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Finalization tests pass; multi-speaker test still skips explicitly | No reviewed multi-speaker/alias UI run | Multi-speaker test must run and UI alias round-trip pass | not warranted |
| L22 | DET-01, DET-02, JOIN-01 | Implemented with verification debt | Agent C | Agent E | `3bff598` | none | Detection policy tests pass | No live conferencing false-positive/negative matrix | Live prompt/join/leave/rejoin evidence | not warranted |
| L23 | MTG-03 | Implemented with verification debt | Agent C | Agent E | `24c14ae` + `437f046`/`c1168e7`/`1829702` | `FeatureRuntime.MeetingDetail.cs`, `FeatureRuntime.Meetings.cs`, `FeatureRuntime.xaml.cs` | Focused live flow 1/1; elevated UI suite 6/6; transcript save/cancel/candidate tests pass | Retained-audio quality and broader meeting qualification remain open | Preserve shared lock and add physical retained-audio review | included |
| L24 | PLAY-01 | Partial | Agent C | Agent E | `3bff598` | none | Existing playback service tests | No waveform/output-device-loss run | Waveform UI/cache plus physical device-loss recovery | not warranted |
| L25 | SUM-01, SUM-02 | Partial | Agent D | Agent E | `3bff598` | none | Existing provider/mock tests | No LM Studio/custom contract or live-provider run | Implement adapter contract and redacted live qualification | not warranted |
| L26 | SUM-04, TPL-01, NOTE-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Notes/title/template unit coverage | No GUI/live-provider matrix | GUI ownership/cancel/timeout/retry evidence | not warranted |
| L27 | data foundation | Implemented with verification debt | Agent D | Agent E | `f0dcef3` + `53dc577` | `Services/AppServices.cs`, persistence adapters, and FeatureRuntime-facing composition behind `MUESLI_SQLITE_HISTORY_CUTOVER` | Runtime composition is wired behind the cutover flag; focused L23/L27 run 42/42 and elevated UI suite 6/6; migration digest/rollback tests pass | No cloned-real-profile count/digest, forced-failure rollback, or second-launch idempotence report | Run the cloned-profile count/digest/failure/rollback/second-launch evidence with `MUESLI_SQLITE_HISTORY_CUTOVER=1` | pending |
| L28 | ORG-01 | Partial | Agent D | Agent E | `3bff598` | none | SQLite `ParentId`/subtree tests pass | Production UI remains one-level | Production tree, breadcrumbs, subtree moves/search | included |
| L29 | SEARCH-01 | Partial | Agent D | Agent E | `3bff598` | none | SQLite FTS tests include manual notes | Production UI still uses in-memory filter | Repository-backed UI with notes, filters, snippets, latency | included |
| L30 | IMP-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Import unit tests pass; two real-media tests explicitly skip | No reviewed eight-format speech set | Run real-media tests and manifest across all advertised formats | not warranted |
| L31 | EXP-01, AUTO-01 | Partial | Agent D | Agent E | `3bff598` | none | Manual MD/PDF and auto-MD tests pass; L41 atomic publication stress and final package rehearsal are green | Auto-PDF remains missing; QuestPDF eligibility and human open remain evidence debt | Auto-PDF, file failure cases, human open, and license decision | not warranted |
| L32 | FOLLOW-01 | Missing | Agent D | Agent E | `3bff598` | none | Persistence link substrate tests only | No user-facing linked workflow | User-facing linked follow-up workflow through production store | not warranted |
| L33 | HOOK-01 | Implemented with verification debt | Agent D | Agent E | `3bff598` | none | Hook success/failure/timeout tests pass | No packaged benign executable smoke | Packaged real `.exe` smoke with redaction review | not warranted |
| L34 | ONB-01, TRAY-01, START-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Current product-experience unit coverage; elevated UI 6/6 | No clean-profile VM/startup upgrade run | Clean-profile onboarding/tray/startup/install evidence | not warranted |
| L35 | FLOAT-01, SOUND-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Indicator/sound contract tests pass | No physical DPI/audio-route confirmation | Multi-monitor captures and audible route matrix | not warranted |
| L36 | PRIV-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Path-boundary/redaction/cleanup tests pass | No destructive cloned prepared-data run | Full deletion/cache/retention disclosure review | not warranted |
| L37 | CU-01 | Implemented with verification debt | Agent E | Agent E | `3bff598` | none | Planner/allowlist/redaction tests pass | No sandboxed local/browser workflow | Two live sandbox workflows with clean logs | not warranted |
| L38 | INSIGHT-01 | Partial | Agent D | Agent E | `3bff598` | none | Dashboard stat tests only | Analyzer absent; D4 contribution decision absent | Schedule only after blockers; no launch delay | not warranted |
| L39 | QUAL-01 | Partial | Agent E | Agent E | `2972192` | all release-owned files at freeze | Debug/Release builds 0 warnings/errors; Release 738/0/4; CPU benchmark, UI, L41 stress, package smoke, and digest recorded | Signing, clean-VM, hardware, provider, human WER/CER, and accessibility evidence remain absent | Freeze a commit after all required module gates close | not warranted |
| L40 | Phase 0/13, TEST-01, PKG-01 | Complete and verified | Agent E | Agent E | `353ba52` | `.gitignore`, required Windows model/view source, this plan | Clean isolated checkout contains all required source; ignored-source audit has no required source outside `bin/`/`obj`; Debug/Release builds 0 warnings/errors; Release 738/0/4 | Not required for source reproducibility | No L40 gate remains; preserve focused commit before integrating other modules | included |
| L41 | AUTO-01 | Complete and verified | Agent D | Agent E | `2972192` | `Services/PostMeetingAutomationService.cs`, `Muesli.Windows.Tests/Phase9AutomationTests.cs` | 8 rounds × 12 concurrent exports (96) publish exactly one Markdown with no temp files; full Release 738/4/0; Debug 0/0; package smoke and clean isolated two-build digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a` | No remaining L41-specific human/physical gate | Preserve the fixed exporter and candidate evidence | included |

Review priority is always: data loss/security/privacy, crashes and false success, release truth/signing/update, core dictation and meeting correctness, then parity polish. Test count alone is never a priority signal.
