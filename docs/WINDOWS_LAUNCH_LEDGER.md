# Muesli Windows launch ledger

Inventory date: 2026-08-20.

This is the **authoritative product-status source** for Windows Muesli. It replaces status claims in older phase plans, the 2026-08-01 parity matrix, and `ROADMAP.md`. Code under `windows-native/Muesli.Windows` and tests under `windows-native/Muesli.Windows.Tests` are the evidence. The macOS tree at `C:\Users\madha\Downloads\muesli-main\muesli-main` is the behavioral reference, not a library to port.

If this ledger disagrees with any other document, this ledger wins.

Operational model catalog (engines, hashes, roles): `WINDOWS_TRANSCRIPTION_MODELS.md`.
Capability IDs and macOS file mapping: `WINDOWS_MACOS_PARITY_MATRIX.md` (statuses copied from here).

## Status vocabulary

| Status | Meaning |
|---|---|
| **Complete and verified** | Production behavior exists. Hardware-free tests cover the contract. No remaining implementation or named physical gate blocks calling the contract done. |
| **Implemented with verification debt** | Production behavior exists with proportionate automated tests, but physical, human, live-provider, packaging, or UI qualification named below has not been re-proven on this tree. |
| **Partial** | A useful subset exists; material behavior is still missing. |
| **Missing** | No production Windows implementation. |
| **Excluded** | Intentionally out of launch scope. Do not implement. |
| **Externally blocked** | Cannot finish until a named external decision, certificate, backend, or product contract exists. |

Implemented never means macOS parity. A source file, button, or historical benchmark is not hardware verification.

## Current baseline (2026-08-20)

| Item | Fact |
|---|---|
| App | Native WPF `.NET 10` x64, `windows-native/Muesli.Windows`. SDK `10.0.400` is pinned in `global.json`. No Python worker, Electron, or web wrapper. |
| Settings schema | 8 |
| Meeting record schema | 5 |
| Session journal schema | 3 |
| Onboarding progress schema | 1 |
| Offline ASR | Seven pinned sherpa-onnx choices; independent Dictation and Final meeting/import roles. |
| Live ASR | Opt-in Nemotron 3.5; default Off; prepare does not select. CoreML Parakeet Realtime EOU absent. |
| Hardware-free integration suite | The integrated Release evidence expands to **742** discovered xUnit cases in `Muesli.Windows.Tests`: **738 passed, 0 failed, 4 explicitly skipped** on .NET 10. The four skips still name `MUESLI_MEDIA_FIXTURE_DIR` (two tests), `MUESLI_STREAMING_QUALIFICATION_MODEL`, and `MUESLI_MULTISPEAKER_FIXTURE_SOURCE` instead of silently returning as passes. |
| Current integration re-run | The reviewed integrated line is `codex/wave0-launch-foundation` through `2972192`. SDK `10.0.400`; integrated Debug and Release app builds each completed with **0 warnings, 0 errors**. The integrated Release test evidence is **738 passed, 0 failed, 4 explicitly skipped**. |
| Focused and UI evidence | The focused L23/L27 run is **42 passed, 0 failed**. The full elevated Windows UI suite is **6 passed, 0 skipped, 0 failed** after the L23 navigation/accessibility fixes (`437f046`, `c1168e7`, `1829702`). |
| Qualification evidence | Three-run Parakeet v3 CPU dictation benchmark: 8.33 s audio, warm 1,227 ms, RTF 0.147, deterministic with model reuse; WER/CER unavailable without a reviewed reference. Mic-only meeting benchmark: warm 1,283 ms, RTF 0.154, deterministic with model reuse; no diarization input. Twelve real dictation traces: 12/12 success, median release-to-paste 1,296 ms, p95 6,066 ms, 0 paste failures, foreground rate 1.0. L14/L15 rehearsal passed negative controls but remains `qualified=false` and human-blocked. CPU catalog smoke fails closed because `qualification/cpu-catalog/audio/english-speech.wav` is absent. |
| Package evidence | The retained 2026-08-17 ZIP/installer remains historical. The final clean isolated rehearsal at `2972192` passed with two-build content-inventory digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`; version `0.3.0`, SDK `10.0.400`, `dirtyTree=false`, tracked models, CPU included, CUDA false, unsigned. Package smoke/native CPU, Inno 6.7.1, and PerMonitorV2 manifest checks passed. ZIP is 109,464,301 bytes; installer is 73,650,464 bytes. PKG-01 remains Partial because clean-VM and signing gates remain. |
| Release rehearsal gate | L41 commit `2972192` fixes the concurrent auto-export race. Its regression stress is **8 rounds × 12 concurrent exports (96 runs), exactly one Markdown, no temp files**, with focused tests, full Release 738/4/0, and Debug 0/0 green. The final clean isolated rehearsal and package smoke passed; remaining report gates are SIGN-01/L05 and clean-VM PKG-01/L07. |
| L03 package truth (this branch) | Public notices, package metadata, and the generated native-runtime inventory now agree that Wave 0 ships the CPU Sherpa provider only. The false “primary package includes CUDA provider” sentence was removed. CUDA remains Partial / not in the public package (L11). QuestPDF 2026.5.0 still selects `LicenseType.Community`; EXP-01 is not complete. |
| Current visible shell check | Release `Muesli.exe` launched with one foreground Dictations dashboard on 2026-08-20. The 4,865-byte fresh slice beginning `01:20:01.231` contained no `ERROR`, `Unhandled UI exception`, or `XamlParseException`; Parakeet v3 warmed on CPU (2,071 ms load + 311 ms warmup). Process was responsive at about 956 MB working set / 882 MB private. |
| Historical CUDA/package/UI evidence from 2026-08-01–02 | Retained under `artifacts/` and older PHASE docs. **Not re-run for this ledger.** It does not promote any hardware-dependent row to Complete and verified. |
| Architecture | x64 only. ARM64 packaging is excluded. |

## Evidence commits and review packet

| Scope | Commit(s) | Evidence recorded for this review |
|---|---|---|
| Baseline | `08c4b3f` | Launch-readiness baseline and status vocabulary established. |
| L40 tracked-source reproducibility | `353ba52` | Required Windows model/view source is tracked; clean-checkout evidence closes the L40 gate. |
| L10 CPU catalog | `c9278ee` | Seven-model inventory, manifests, and fail-closed smoke tooling are present; real reviewed speech is still missing. |
| L11 CUDA provenance | `8c93279` | CUDA provenance/qualification gap is explicit; public package remains CPU-only. |
| L04 reproducible release | `4f408f4` | Rehearsal, package inventory, installer, manifest, and CI parity tooling; prior clean two-build digest is recorded above and the final rerun remains open. |
| L14/L15 qualification setup | `f67584f`, `bf04aa2` | Locale-safe L14 parser and exactly-four-report L15 suite; negative controls pass, but human/device qualification remains debt. |
| L27 persistence cutover | `f0dcef3`, `53dc577` | Feature-gated migration, digest/rollback coverage, and transaction-scoped rollback fix; focused L23/L27 run passes 42/42. |
| L09 UI automation | `74a0a97` | Out-of-process production UI harness and clean-profile setup. |
| L23 transcript editing | `24c14ae`, `437f046`, `c1168e7`, `1829702` | Runtime wiring, meeting navigation, accessible card, and tab fixes; focused live flow 1/1 and elevated UI 6/6. |
| L41 concurrent auto-export | `2972192` | Destination/manifest race fixed; 8 rounds × 12 concurrent exports (96) publish exactly one Markdown with no temporary files. Focused tests, full Release 738/4/0, Debug 0/0, and final clean isolated package rehearsal pass; two-build digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a`. |

## Launch program modules

The launch program is Phase 0–13. It is **not** the older P0–P8 schedule in historical `WINDOWS_EXECUTION_PLAN.md`. Do not equate those numbers.

| Module | Owns | Old P0–P8 name (do not use for status) |
|---|---|---|
| Phase 0 | Inventory and this ledger | P0 |
| Phase 1 | Offline model platform, lifecycle, strict routing | P1 |
| Phase 2 | Dictation, hotkeys, paste, mic routes, filler/dictionary, floating indicator | P2 |
| Phase 3 | Meeting capture, ten-state lifecycle, recovery, playback ownership | P3 capture slice |
| Phase 4 | Live transcription, VAD, ownership modes, live window | P4 |
| Phase 5 | Meeting finalization: timeline, merge, diarization, aliases, health | Not old P5 |
| Phase 6 | Detection, prompts, Join/Record (calendar excluded) | Old P5 minus calendar |
| Phase 7 | Notes, titles, summary providers, templates, secrets | Part of old P6 |
| Phase 8 | Import, export, library, search, playback extras | Was folded into old P3 |
| Phase 9 | Post-meeting hooks and auto Markdown export | Rest of old P6 |
| Phase 10 | Optional Computer Use | Old P7A |
| Phase 11 | Cross-platform text sync | Old P7B |
| Phase 12 | Onboarding, tray, shell, DPI, startup | Part of old P8 |
| Phase 13 | Diagnostics/support UX, packaging, signing, updater, release qualification | Rest of old P8 |

Test anchors: `TranscriptionModelPlatformTests`, `Phase2DictationTests` … `Phase10ComputerUseTests`, `Phase12ProductExperienceTests`. There is no `Phase11*` or `Phase13*` test class.

**Filename collisions:** `PHASE4_MEETING_IMPORT_QUALIFICATION.md` is **not** launch Phase 4. `PHASE5_RELEASE_QUALIFICATION.md` is **not** launch Phase 5. Both are historical Parakeet-era qualification notes.

## Explicit exclusions

Do not implement these on the Windows launch path:

| ID | Item | Reason |
|---|---|---|
| CAL-01 / API-02 | Google Calendar OAuth, EventKit, Outlook/MAPI | Excluded. Tray already states there is no calendar source. |
| STORE-01 | Microsoft Store packaging | Excluded. |
| SUM-03 | ChatGPT subscription OAuth | Excluded until a supported contract exists; UI must not fake it. Currently blocked in `SummaryProviderDisclosure`. |
| SYNC-01 | CloudKit / iPhone / iCloud sync | CloudKit cannot be the Windows answer. Any other backend stays on Phase 11 + decision D3. |
| SYNC-02 | Audio recording sync | Product never syncs audio. |
| ARM-01 | ARM64 runtime/package | Launch is win-x64. Package tests reject `win-arm64` in the x64 zip. |
| OOS-01 | Python worker, Electron, web wrapper | Repository rule. |
| OOS-02 | Silent engine fallback or fake success | Repository rule. |
| OOS-03 | Literal macOS framework/model ports | CoreML, ScreenCaptureKit, AppKit, Sparkle, TCC, etc. are not Windows APIs. |

## Capability ledger

Remaining class: **None** (done at the stated status), **Implementation**, **Qualification**, **Decision**, **Excluded**.

### Models and runtime

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| MOD-01 | Offline Parakeet TDT with CPU/CUDA provider selection | Partial | Phase 1 / 13 | CPU provider is packaged; public notices/inventory now say CPU-only. CUDA selection exists for an externally staged matching bundle, but the public package must not claim NVIDIA until L11 / Wave 5 stages and qualifies the provider |
| MOD-02 | Seven pinned offline ASR families | Implemented with verification debt | Phase 1 | Qualification: seven-family CPU matrix; retained CUDA smoke is historical |
| MOD-03 | Prepare/cancel/retry/verify/delete/recovery lifecycle | Implemented with verification debt | Phase 1 | Qualification: Models UI during real downloads; destructive cache delete against user caches not repeated here |
| MOD-04 | Opt-in Nemotron 3.5 live model; no CoreML EOU | Implemented with verification debt | Phase 4 | Qualification: long meeting, Bluetooth/route, CUDA-live, multilingual human review |
| MOD-05 | Optional Qwen/GGUF cleanup lifecycle | Partial | Phase 1 | Implementation: guided download/hash/cancel only after an approved GGUF. Today: manual cache placement, fail closed to raw text |
| MOD-06 | No silent transcription-engine fallback | Complete and verified | Phase 1 | None |
| TXT-03 | Shared filler/dictionary/cleanup ordering across dictation, meeting, import | Partial | Phase 2 / 5 / 8 | Implementation: meetings/imports apply cleanup + dictionary with speaker-prefix preservation, not dictation's filler-then-dictionary order |

### Dictation, audio, text

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| DIC-01 | Hold-to-talk capture, local transcription, persistence | Implemented with verification debt | Phase 2 | Qualification: human microphone corpus WER/CER |
| DIC-02 | Insert at original cursor without replacing the clipboard; clipboard-safe fallback and failure disclosure | Implemented with verification debt | Phase 2 | Qualification: Notepad, Chrome, Office, other editor |
| DIC-03 | History copy/delete/date filter/search | Implemented with verification debt | Phase 2 | Qualification: dashboard UI exercise |
| HOT-01 | Configurable hold hotkey, exact modifiers, Escape cancel | Implemented with verification debt | Phase 2 | Qualification: layouts, elevation, real hook |
| HOT-02 | Double-tap hands-free state machine | Implemented with verification debt | Phase 2 | Qualification: real global hook |
| AUD-01 | Mic enumeration, selection, fallback, shared-mode capture | Implemented with verification debt | Phase 2 | Qualification: device/privacy matrix |
| AUD-02 | Route change, Bluetooth, unplug, recovery while recording | Implemented with verification debt | Phase 2 / 3 | Qualification: physical default/unplug/Bluetooth |
| AUD-03 | Media pause / audio ducking | Externally blocked | Decision, later | Decision: pause vs duck vs no interference. No Windows implementation |
| TXT-01 | Deterministic filler removal | Implemented with verification debt | Phase 2 | Qualification: human corpus |
| TXT-02 | Personal dictionary / Jaro-Winkler | Implemented with verification debt | Phase 2 | Qualification: names/accent corpus |
| API-03 | Windows privacy/UIPI paste diagnostics (not macOS TCC) | Implemented with verification debt | Phase 2 / 12 | Qualification: elevated-target and Settings deep-link review |

### Meetings, live, finalization

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| MTG-01 | Simultaneous You/Others capture, retained local audio | Implemented with verification debt | Phase 3 | Qualification: Zoom/Teams/Meet process-target vs endpoint-loopback |
| MTG-02 | Suspend/resume, cancel, shutdown, crash recovery | Implemented with verification debt | Phase 3 | Qualification: physical suspend, forced kill, disk-full |
| MTG-03 | Edit title/transcript/notes; retranscribe or re-summarize | Implemented with verification debt | Phase 7 / 5 | L23 now wires transcript edit/cancel/save and candidate retranscription through meeting detail; focused L23/L27 flow is 1/1 and the elevated UI suite is 6/6. Physical retained-audio quality and broader meeting qualification remain evidence debt |
| LIVE-01 | Live meeting transcription and floating window | Implemented with verification debt | Phase 4 | Qualification: simultaneous meeting + UI soak |
| LIVE-02 | Silero VAD natural-boundary commits (no fixed-duration cut) | Implemented with verification debt | Phase 4 | Qualification: noisy-room |
| LIVE-03 | Explicit live-preview vs unified final ownership | Complete and verified | Phase 4 | None at contract level. Physical live soak stays on LIVE-01 |
| LIVE-04 | Gap recovery, dedupe, sample-rate mapping | Implemented with verification debt | Phase 4 | Qualification: injected native crash in a physical meeting |
| DIA-01 | Offline remote-speaker diarization and You attribution | Implemented with verification debt | Phase 5 | Qualification: multi-speaker CPU/CUDA quality |
| DIA-02 | Speaker alias persistence on transcript, notes, copy, export | Implemented with verification debt | Phase 5 / 8 | Qualification: UI rename round-trip |
| API-01 | Windows process-tree loopback with disclosed endpoint fallback | Implemented with verification debt | Phase 3 | Qualification: real conferencing-app attribution |
| PLAY-01 | In-app play/pause/seek/track select | Partial | Phase 3 / 8 | Implementation: no playback waveform. Device-loss is qualification debt on the existing player |

### Detection (calendar excluded)

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| DET-01 | App/window/URL detection plus mic/camera evidence, false-positive policy | Implemented with verification debt | Phase 6 | Qualification: live Zoom/Teams/Meet/Webex. Unit tests cover URL parse, badges, mic/camera-alone rejection |
| DET-02 | Prompt dedupe, dismiss, detected auto-stop, recovery | Implemented with verification debt | Phase 6 / 3 | Qualification: join/leave/rejoin. Manual recordings never auto-stop |
| JOIN-01 | Join & Record, Join Only, Record Only for detected URLs | Implemented with verification debt | Phase 6 | Qualification: live prompt actions. Calendar URLs remain excluded with CAL-01 |
| CAL-01 | Google Calendar | Excluded | — | Excluded |
| API-02 | EventKit equivalent | Excluded | — | Excluded |

### Notes, providers, organization

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| SUM-01 | Local deterministic summary plus explicit OpenAI/OpenRouter | Implemented with verification debt | Phase 7 | Qualification: live keys, timeout, log redaction |
| SUM-02 | Ollama and LM Studio/custom HTTP | Partial | Phase 7 | Implementation: Ollama exists (endpoint/model, loopback disclosure). LM Studio/custom HTTP is missing |
| SUM-03 | ChatGPT subscription OAuth | Excluded | — | Excluded / remains D1 if product later reverses |
| SUM-04 | Provider-based automatic titles with manual ownership | Implemented with verification debt | Phase 7 | Qualification: UI title ownership |
| SEC-01 | Credential Manager keys; plaintext migration | Complete and verified | Phase 7 | None |
| TPL-01 | Built-in/custom templates and re-summary | Implemented with verification debt | Phase 7 | Qualification: UI CRUD |
| NOTE-01 | Manual notes separate from generated summary | Implemented with verification debt | Phase 7 | Qualification: editor UX. Re-summarize must not overwrite manual notes (unit-covered) |
| ORG-01 | Nested meeting folders | Partial | Phase 8 | Implementation: SQLite repositories support tested `ParentId`, ancestry, subtree moves, and cycle rejection, but the production JSON-backed UI remains one-level and cannot expose nesting |
| SEARCH-01 | Search dictations and meetings | Partial | Phase 8 / 12 | Implementation: SQLite FTS indexes manual notes, but the production in-memory UI filter still searches only title/summary/transcript/metadata and does not use that repository |

### Import, export, automation

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| IMP-01 | Import supported media with cancel, progress, no fake success | Implemented with verification debt | Phase 8 | Qualification: ASR+diarization on each advertised format. Advertised set is wav/mp3/m4a/aac/mp4/mov/mkv/webm. **ogg is rejected with guidance** |
| EXP-01 | Manual Markdown/PDF export with aliases and manual notes | Implemented with verification debt | Phase 8 | Qualification: human open of PDF/MD. QuestPDF 2026.5.0 is used with `LicenseType.Community`; community-license eligibility remains a Phase 13 release-owner check. Do not treat EXP-01 as complete |
| HOOK-01 | Post-meeting `.exe` hook, JSON stdin, timeout, Job Object | Complete and verified | Phase 9 | None at contract level. Real third-party executables are optional later smoke |
| AUTO-01 | Automatic Markdown export, atomic collision-safe | Implemented with verification debt | Phase 9 | L41 commit `2972192` adds atomic destination/manifest collision handling; focused 96-run concurrency stress and final package rehearsal are green. PDF auto-export remains missing, so AUTO-01 retains verification debt |
| FOLLOW-01 | Configurable follow-up/linked workflow | Missing | Phase 9 | Implementation after a destination contract. Summary “Follow-ups” bullets are not this feature |

### Computer Use and sync

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| CU-01 | Optional voice Computer Use, allowlists, confirmation, redacted trace | Implemented with verification debt | Phase 10 | Qualification: sandboxed live workflow. Window/page text and screenshots stay disabled until masking (D2 residual) |
| SYNC-01 | Private text sync / iPhone bridge | Externally blocked | Phase 11 / D3 | Decision: backend. CloudKit itself is excluded |
| SYNC-02 | Sync audio | Excluded | — | Excluded |

### Shell, onboarding, release

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| ONB-01 | Resumable onboarding with durable progress and real gates | Implemented with verification debt | Phase 12 | Qualification: clean-profile first run. Progress is more than a completion flag |
| TRAY-01 | Tray menu from real state | Implemented with verification debt | Phase 12 | Qualification: menu actions. Upcoming meetings row is honestly disabled (no calendar) |
| FLOAT-01 | Draggable indicator with real levels and stop/cancel | Implemented with verification debt | Phase 2 / 12 | Qualification: DPI/multi-monitor/click |
| FLOAT-02 | Optional live waveform on hover | Implemented with verification debt | Phase 4 | Qualification: hover DPI review |
| SHELL-01 | Light/dark dashboard and navigation | Implemented with verification debt | Phase 12 | Qualification: keyboard, 100–200% DPI, and multi-monitor. PerMonitorV2 is now embedded and asserted in the built manifest, but physical DPI evidence remains open |
| START-01 | Launch at login | Implemented with verification debt | Phase 12 | Qualification: install/uninstall/elevation |
| INSTANCE-01 | Single-instance activation | Complete and verified | Phase 12 | Visible second-instance click is optional manual confirmation |
| INSIGHT-01 | Insights analyzer and contribution | Partial | Phase 12 / D4 | Implementation: dashboard stat cards only. Analyzer/share missing. Contribution telemetry is D4 |
| SOUND-01 | Configurable feedback sounds | Implemented with verification debt | Phase 12 | Qualification: hear start/insert/model-ready cues on speaker, headphone, setup-test, and disabled-setting routes |
| DIAG-01 | Redacted logs, runtime status, support bundle | Partial | Phase 13 | Implementation: no structured incident/support-bundle export UI |
| PRIV-01 | Local-first audio, explicit network, deletion/retention | Implemented with verification debt | Every module / 13 | Qualification: destructive deletion against prepared caches; privacy copy vs live Settings |
| UPD-01 | Signed automatic updates | Missing | Phase 13 | Implementation after D5. About currently opens GitHub Releases |
| SIGN-01 | Authenticode signed app and installer | Externally blocked | Phase 13 / D5 | Decision: production certificate |
| PKG-01 | Self-contained x64 zip and Inno installer | Partial | Phase 13 | Implementation/qualification: unsigned; upgrade and clean-VM gates open. Public package inventory and notices now record CPU-only; CUDA packaging is L11 |
| TEST-01 | Automated success/failure/cancel/recovery coverage | Partial | Continuous | The out-of-process GUI automation suite exists and passes 6/6 elevated Windows UI tests; live OS detection, paste, mic, accessibility, and physical coverage remain incomplete |
| QUAL-01 | Repeatable CPU/CUDA/hardware/package gates | Partial | Phase 13 | Qualification: current-tree evidence. Historical Parakeet-era reports are not this catalog |
| STORE-01 | Microsoft Store | Excluded | — | Excluded |
| ARM-01 | ARM64 | Excluded | — | Excluded |
| API-04 | Sparkle/AppKit/codesign equivalents | Partial | Phase 13 | NotifyIcon exists. Signed update channel and Authenticode remain missing/blocked |
| OOS-01 | Python/Electron/web | Excluded | — | Excluded |
| OOS-02 | Silent fallback / fake data | Excluded | — | Excluded |
| OOS-03 | Literal macOS ports | Excluded | — | Excluded |

## Remaining implementation work

Ordered by launch module. Calendar/OAuth, Store, ChatGPT OAuth, CloudKit, audio sync, ARM64, and macOS framework ports are omitted because they are excluded.

| Module | Work |
|---|---|
| Phase 1 | Guided Qwen cleanup catalog/download only after an approved GGUF (MOD-05). |
| Phase 2 | Optional: apply filler filtering to meeting/import with the same settings as dictation (TXT-03). AUD-03 stays decision-gated. |
| Phase 7 | LM Studio or documented custom HTTP summary adapter (SUM-02 remainder). |
| Phase 8 | Nested folders (ORG-01). Index manual notes in search (SEARCH-01). Playback waveform if still desired (PLAY-01 remainder). |
| Phase 9 | PDF auto-export if product still wants AUTO-01 parity. FOLLOW-01 only after a destination contract. |
| Phase 10 | Observation masking if text/screenshots are ever enabled (CU-01 residual). |
| Phase 11 | Nothing until D3. |
| Phase 12 | Insights analyzer if D4 allows local-only. |
| Phase 13 | Support-bundle UI (DIAG-01). Updater (UPD-01) after D5. Signing (SIGN-01) after D5. Clean-VM upgrade/uninstall (PKG-01). |

## Remaining qualification debt

This is not missing code. Do not treat it as implementation backlog.

| Module | Physical / human / live gate |
|---|---|
| Phase 1 | Seven-family CPU real-audio; current-tree CUDA; Models UI during prepare/cancel. |
| Phase 2 | Human dictation corpus; four paste targets; device/Bluetooth/unplug; real hook. Spec: `PHASE2_DICTATION_QUALIFICATION.md` (still the fail-closed dictation gate). |
| Phase 3 | Zoom/Teams/Meet dual capture; process-target vs loopback; suspend/kill/disk-full. Spec: `PHASE3_QUALIFICATION.md`. |
| Phase 4 | Long live meeting, route change, CUDA-live, multilingual review. Investigation notes in `WINDOWS_STREAMING_ASR_INVESTIGATION.md` are historical evidence, not a fresh pass. |
| Phase 5 | Multi-speaker diarization quality on CPU and CUDA; retained-audio quality review for the implemented L23 candidate retranscription flow. |
| Phase 6 | Live detection false-positive/negative matrix. |
| Phase 7 | Live OpenAI/OpenRouter/Ollama; no secrets in logs. |
| Phase 8 | Per-format import ASR/diarization; human PDF/MD open. |
| Phase 9 | Optional real hook executable smoke. |
| Phase 10 | Sandboxed Computer Use workflow. |
| Phase 12 | Clean-profile onboarding; DPI 100/125/150/200; multi-monitor; tray/startup. `verify-phase12-ui.ps1 -ValidateOnly` is a contract check, not capture evidence. |
| Phase 13 | Signed zip/installer; clean VM install/upgrade/uninstall; fresh-log packaged launch. |

## External decisions

| ID | Blocks | Exit |
|---|---|---|
| D1 | SUM-03 (excluded for launch) | Supported ChatGPT contract + security review, only if product reverses the exclusion |
| D2 | CU-01 text/screenshots | Masking qualification. Planner contract is already in `COMPUTER_USE.md` |
| D3 | SYNC-01 / Phase 11 | Approved non-CloudKit backend |
| D4 | INSIGHT-01 contribution | Written privacy decision. Local-only insights can proceed without it |
| D5 | SIGN-01, UPD-01, PKG-01 release | Production Authenticode certificate and signing environment |
| D6 | Catalog changes | Already resolved for the seven offline + Nemotron live set. Re-open only to add/remove models |
| AUD-03 | Media ducking | Pause vs duck vs none |

## Obsolete branches — do not merge wholesale

Current line of work: `codex/wave0-launch-foundation` (`2972192` at this review). `main` is an ancestor at `ba38e56` (“Checkpoint launch-ready Python pipeline”) and must not be used as a merge source for native work.

The open Wave 1 module branches currently merge-base with HEAD at `ba38e56` despite containing useful focused tip commits. Rebase or cherry-pick only the reviewed module commits; do not merge those branches wholesale. This applies to `agent-a-l10-l11-inventory`, `agent-d-l27-cutover-gate`, `agent-e2-l09-uia-skeleton`, and `agent-e3-l04-package-ci`.

Feature/refactor/test branches that **are already merged** into HEAD are historical slices. Re-merging them is unnecessary.

These branches are **not merged** into HEAD and look like parallel rewrites of work that later landed under different SHAs. Merging any of them wholesale will duplicate or regress current code:

- `build/native-packaging-and-installer`
- `ci/windows-build-and-test-workflow`
- `feat/rebuild-main-window`
- `test/dictation-and-notification-coverage`
- `test/dictation-qualification-scripts`
- `test/meeting-and-release-qualification-scripts`
- `test/meeting-lifecycle-coverage`
- `test/model-and-runtime-qualification-scripts`
- `test/native-runtime-and-model-coverage`
- `test/regression-suite-scaffold`

Cherry-pick only a specific missing commit after diffing it against HEAD. Never merge the branch.

## Historical documents

These remain useful as contracts or old evidence. They are **not** status sources.

| Document | Role now |
|---|---|
| `WINDOWS_EXECUTION_PLAN.md` | Historical P0–P8 sequencing and still-valid engineering rules. Status and module IDs live here. |
| `WINDOWS_MACOS_PARITY_MATRIX.md` | Capability ID catalog + macOS mapping. Statuses must match this ledger. |
| `ROADMAP.md` | Short remaining-work view pointing here. |
| `PHASE2_DICTATION_QUALIFICATION.md` | Current fail-closed **qualification** spec for Phase 2. |
| `PHASE3_QUALIFICATION.md` | Current fail-closed **qualification** spec for Phase 3. |
| `WINDOWS_MEETING_SESSION_LIFECYCLE.md` | Current Phase 3 state-machine contract. |
| `PHASE8_CONTEXT_TRANSFER.md` | 2026-08-02 handoff. Test count 337 and schema 5/4 are stale. |
| `PHASE12_PRODUCT_EXPERIENCE.md` | Current Phase 12 preview/DPI contract. |
| `COMPUTER_USE.md` | Current Phase 10 threat model. |
| `POST_MEETING_AUTOMATION.md` | Current Phase 9 contract. |
| `WINDOWS_TRANSCRIPTION_MODELS.md` | Current model/role catalog. |
| `WINDOWS_PRIVACY.md` | Current privacy disclosure. Update if behavior changes; not a status matrix. |
| `WINDOWS_V1_RELEASE.md` | Packaging checklist. Unsigned and not a status matrix. |
| `WINDOWS_NATIVE_PLAN.md` | Directional. Do not cite for feature completeness. |
| `PHASE4_MEETING_IMPORT_QUALIFICATION.md` | **Historical** Parakeet-era import/meeting ASR. Not launch Phase 4. |
| `PHASE5_RELEASE_QUALIFICATION.md` | **Historical** Parakeet-era packaged release. Not launch Phase 5. Closer to Phase 13 evidence of that build. |
| `WINDOWS_STREAMING_ASR_INVESTIGATION.md` | Historical Phase 4 investigation + 2026-08-01 re-qualification notes. |
| `TRANSCRIPTION_BENCHMARKS.md` | Benchmark method. Not current pass/fail status. |

## Corrected stale claims

| Old claim (2026-08-01 matrix / roadmap) | Current fact |
|---|---|
| 46 / 56 / 74 / 128 / 165 / 195 / 337 / 484 / 634 / 709 tests | Current integrated Release evidence is **742** cases (738 passes, 0 failures, 4 explicit prerequisite skips) |
| Manual notes missing | `PersistedMeeting.ManualNotes`, editor, export, Phase 7 tests |
| Ollama missing | `MeetingSummaryService` Ollama path + settings. LM Studio still missing |
| Live transcription missing | `MeetingLiveTranscriptionSession` + window + Phase 4 tests; default Off |
| Detection lifecycle missing auto-stop | `MeetingAutoStopTracker`; dismiss/suppress in detection tests |
| Onboarding is only a completion flag | `OnboardingProgressStore` with step, deferred, verification flags, resume |
| Import cancellation missing | `Phase8ImportCancellationTests` + UI cancel |
| Exports untested | `Phase8ExportTests` covers MD/PDF, aliases, manual notes, failure |
| ogg advertised | Rejected with conversion guidance; not in `MediaImportFormats.Supported` |
| Tray is Open/Quit only | Recent items, detected now, resume setup, tour, settings, about. Upcoming calendar honestly disabled |
| Search cannot index notes because notes do not exist | Notes exist; search still does not index them |
| Phase 5 = calendar | Launch Phase 5 = finalization. Calendar is excluded |

## Concurrent work notice

Wave 0 integration is committed at `efa961c`; the reviewed line now ends at `2972192` with focused Wave 1 source/tooling slices. The build and test facts above describe that committed source plus this review's document edits. Do not merge stale feature or divergent module branches wholesale on top of it.
