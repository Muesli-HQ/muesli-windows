# Muesli Windows launch ledger

Inventory date: 2026-09-23.

This is the **authoritative product-status source** for Windows Muesli. It replaces status claims in older phase plans, the 2026-08-01 parity matrix, and `ROADMAP.md`. The shipping shell is under `windows-native/Muesli.Windows.WinUI`; the retired WPF source is preserved under `windows-native/Muesli.Windows.Wpf.Legacy`. Tests live under `windows-native/Muesli.Windows.Tests`. The macOS tree at `C:\Users\madha\Downloads\muesli-main\muesli-main` is the behavioral reference, not a library to port.

## Merge readiness update (2026-09-26)

The tracked Windows work is split into draft PRs [#23](https://github.com/Muesli-HQ/muesli-windows/pull/23) and #31–#36, in order. The proposed macOS shared-core PR #540 was closed and its pushed fork branch deleted at the user's request. `windows-native/shared-core.lock.json` therefore has an intentionally empty revision; Windows release CI must remain red until an approved upstream shared-core ABI source is available. The current packaged WinUI app opened with the Timeline dashboard foregrounded; Models and Meetings were inspected through UI Automation, and its fresh launch log had no startup error signatures. A clean checkout with the locally cached shared Swift bridge copied into test output passes **1083 tests / 0 failures / 5 named fixture skips**; the focused tests also pass. This is test evidence, not a source-backed release build. A full source-backed MSIX rehearsal, signing, clean-machine install/upgrade, physical accessibility and long-form dictation qualification remain open. Main merges run unsigned CI and do not deploy.

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

## Current baseline (2026-09-23)

| Item | Fact |
|---|---|
| App | Packaged WinUI 3 `.NET 10` x64 is the selected shipping shell at `windows-native/Muesli.Windows.WinUI`; WPF is archived at `windows-native/Muesli.Windows.Wpf.Legacy` and excluded from the active solution/default build. Shared behavior lives in `Muesli.Windows.Core` / `Muesli.Windows.Platform`. SDK `10.0.400` remains exactly pinned in `global.json`. No Python worker, Electron, or web wrapper. |
| Settings schema | 11 |
| Meeting record schema | 5 |
| Session journal schema | 3 |
| Onboarding progress schema | 1 |
| Offline ASR | Twelve pinned sherpa-onnx choices; independent Dictation and Final meeting/import roles. |
| Live ASR | Opt-in Nemotron 3.5; default Off; prepare does not select. CoreML Parakeet Realtime EOU absent. |
| Hardware-free integration suite | The exact pinned SDK **10.0.400** is available only from the user-local host at `%LOCALAPPDATA%\Microsoft\dotnet`; the default `dotnet` on `PATH` sees 10.0.401/8.0.425 and cannot resolve the pin. Using the user-local host with `MUESLI_SHARED_CORE_PACKAGE` pointed at the macOS reference, the active WinUI Release ReadyToRun build passes with **0 warnings / 0 errors** and the full suite passes **901 / 0 failed / 4 explicitly skipped of 905** with the real Swift bridge staged. Each skip still reports its missing `MUESLI_MEDIA_FIXTURE_DIR` (two tests), `MUESLI_MULTISPEAKER_FIXTURE_SOURCE`, or `MUESLI_CLONED_PROFILE_DIR`; the `MUESLI_STREAMING_QUALIFICATION_MODEL` streaming fixture was subsequently restored with the ported `Phase4LiveTranscriptionTests` and now reports its own named skip. TEST-01 stays Partial for current UI automation and clean-checkout/release-environment evidence owned by L48/L52. |
| Current integration re-run | The reviewed line is `codex/wave0-launch-foundation` at `ad92e4360648e454cc7c464d29be804b29f4c031`, 29 commits ahead of upstream. The working tree is **135 collapsed porcelain entries**: 67 modified and 68 untracked (589 expanded untracked files), nothing staged. Current in-flight work spans native execution-provider/runtime changes, optional CUDA delivery, meeting notifications, model/language UX, branding/assets, and broader WinUI parity. Obsolete branches were enumerated only; none was merged wholesale. |
| Focused and UI evidence | The active suite discovers 1086 tests and is green apart from five named fixture skips. UI automation was rerun on 2026-09-26 with the pinned SDK on `PATH`: the packaged shell qualification passed **13/14** (including the current support-bundle, theme, onboarding, populated-profile, diagnostics, and search cases), and the whole UI project passed **17/24**, with the failures confined to floating-indicator secondary-window automation and one unpackaged second-instance dashboard-return case. L49 remains Partial pending packaged identity/real workflows, reliable visible activation, and physical accessibility/DPI. |
| Qualification evidence | 2026-09-21 real CUDA inference passes for two independently qualified offline models on the NVIDIA RTX 4070 Laptop GPU: Whisper Tiny English at **451 ms warm / RTF 0.072** and Parakeet Unified English INT8 at **548 ms warm / RTF 0.077**, both deterministic. The schema-v2 qualification cache contains separate entries for both exact model IDs, each with CUDA graph-node evidence; one model can no longer authorize another. The pinned acceleration pack and all NVIDIA dependencies verify successfully. The broader retained provider matrix is recorded in `WINDOWS_GPU_QUALIFICATION.md`; external NVIDIA/AMD matrices, long-meeting soak, and human WER/CER remain open. |
| Package evidence | The unsigned x64 MSIX produced from the 2026-09-21 dirty tree remains under `artifacts/msix`; its manifest/native-runtime smoke was rerun successfully on 2026-09-23 with `-SkipLaunch`. It does **not** contain the subsequent 135-entry working tree and is not current-source package evidence. Historical clean-build digests remain recorded below. PKG-01 remains Partial because production signing, clean-VM install/upgrade/uninstall, a current package, and clean two-checkout reproducibility remain open. |
| Release rehearsal gate | The active exact-SDK Release ReadyToRun build and 905-test suite are green through the user-local .NET host. The literal requested legacy command `dotnet build windows-native\Muesli.Windows\Muesli.Windows.csproj -c Release` is obsolete because that WPF project path no longer exists, and the default `dotnet` host cannot resolve 10.0.400. The retained MSIX smoke is green but stale relative to the current dirty tree. Production SIGN-01/L05, clean-VM PKG-01/L07, clean two-checkout L48, WinUI activation/qualification L49, and restored streaming/full release parity L52 remain open. |
| 2026-08-22 rehearsal re-run | An isolated clean-checkout re-run of the release rehearsal at `a5414b3` passed end-to-end (Release suite 757 passed / 0 failed / 4 explicitly skipped of 761, pinned SDK 10.0.400, `dirtyTree=false`, two-build content inventories matched, Inno installer built, package smoke green), but the two-build content digest was `637a3433821f48f2754751bc6af3d26e22f749c1268510c0d7ccba8377349e24` (entryCount 372, ZIP 109,499,479 bytes, installer 73,666,874 bytes), **not** the recorded `ee860840…`. The recorded `a5414b3` digest and byte sizes are therefore historical, not reproducible on re-run, and the artifacts retained under `artifacts/` are the earlier `d432a26` rehearsal (`049c3c34…`). Cross-run digest stability and final-artifact retention are tracked as launch module L48 in `WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md`. |
| L03 package truth (this branch) | Public notices, package metadata, and the generated native-runtime inventory now agree that Wave 0 ships the CPU Sherpa provider only. The false “primary package includes CUDA provider” sentence was removed. CUDA remains Partial / not in the public package (L11). QuestPDF 2026.5.0 still selects `LicenseType.Community`; EXP-01 is not complete. |
| Current visible shell check | On 2026-09-23 `scripts/run-windows.ps1 -SkipBuild` launched packaged Debug WinUI on the production profile as a responsive process with a nonzero main-window handle and title `Muesli`; the fresh app log identifies `Muesli.Windows.WinUI` as foreground at startup. The fresh launch slice contains no `ERROR`, `Unhandled UI exception`, `Unhandled WinUI exception`, or `XamlParseException`, and the staged Swift bridge is active. Native Computer Use returned no app/window inventory after reset, so dashboard contents were not screenshot-certified. SHELL-01/L49 stay Partial pending current automated UI and physical workflow qualification. |
| Historical CUDA/package/UI evidence from 2026-08-01–02 | Retained under `artifacts/` and older PHASE docs. **Not re-run for this ledger.** It does not promote any hardware-dependent row to Complete and verified. |
| Architecture | x64 only. ARM64 packaging is excluded. |

## Evidence commits and review packet

| Scope | Commit(s) | Evidence recorded for this review |
|---|---|---|
| Baseline | `08c4b3f` | Launch-readiness baseline and status vocabulary established. |
| L40 tracked-source reproducibility | `8f0c867`–`ad92e43` + current working tree | The selected Core/Platform/WinUI source, solution, migrated tests, and archived WPF reference are now tracked. L40 remains Partial because no clean isolated checkout has passed the default Release build and current suite under the exact SDK/shared-core pin. |
| L10 CPU catalog | `c9278ee` | Seven-model inventory, manifests, and fail-closed smoke tooling are present; real reviewed speech is still missing. |
| L11 CUDA provenance | `8c93279` | CUDA provenance/qualification gap is explicit; public package remains CPU-only. |
| L04 reproducible release | `4f408f4`, `a5414b3` | Rehearsal, package inventory, installer, manifest, and CI parity tooling; the final clean exact-SDK two-build digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf` passed. No L04-specific gate remains; PKG-01 remains Partial for signing and clean-VM evidence. |
| L14/L15 qualification setup | `f67584f`, `bf04aa2` | Locale-safe L14 parser and exactly-four-report L15 suite; negative controls pass, but human/device qualification remains debt. |
| L27 persistence cutover | `f0dcef3`, `53dc577` | Feature-gated migration, digest/rollback coverage, and transaction-scoped rollback fix; focused L23/L27 run passes 42/42. |
| L09 UI automation | `74a0a97` | Out-of-process production UI harness and clean-profile setup. |
| L23 transcript editing | `24c14ae`, `437f046`, `c1168e7`, `1829702` | Runtime wiring, meeting navigation, accessible card, and tab fixes; focused live flow 1/1 and elevated UI 6/6. |
| L41 concurrent auto-export | `2972192` + current working tree | Destination/manifest race fixed; the re-ported `PostMeetingAutoExportTests.ConcurrentAutoExportsPublishExactlyOneMarkdownFile` reruns 8 rounds × 12 concurrent exports (96) publishing exactly one Markdown with no temporary files on the active tree. Historical L41 rehearsal digest `779a9d67b8d71c18556fe95074b8cc8cf95ce740edc7f02352aa15f5dc47e35a` is retained; the final current rehearsal is recorded at `a5414b3`. |
| L42 per-meeting retranscription admission | `619adc3` | Per-meeting candidate admission inspects retained audio/metadata before scratch, rejects invalid admission deterministically, serializes concurrent candidates, preserves prior transcript, and cleans up. Deterministic concurrent focused tests pass; MTG-03 remains Implemented with verification debt for retained-audio quality and broader meeting qualification. |
| L43 automatic export manifest integrity | `be293f3` + current working tree | Manifest filename/path/SHA/destination/current-render integrity is checked before reuse; user-modified output is preserved and a collision-free candidate is published. The retired export-integrity suite is restored into the active tree as `PostMeetingAutoExportTests` (modified-destination preservation, changed-content invalidation, user collision, unowned-control-directory fail-closed, temp-collision preservation, stale-claim crash recovery, JSON round-trip). AUTO-01 remains Implemented with verification debt only for the QuestPDF eligibility decision and human/failure-case evidence. |
| L44 immutable migration snapshot plan | `0922af2` | One immutable JSON, `.bak`, and dictionary snapshot is captured and passed as one MigrationPlan through import, fingerprint, and retained-backup paths; mutation regression passes. L27/data capability remains Implemented with verification debt until a cloned real profile is qualified. |
| L45 exact-SDK release reproducibility | `d432a26`, `a5414b3`, `ad92e43` + current working tree | `global.json` pins SDK `10.0.400` with `rollForward=disable`; the release helper rejects mismatches. The user-local 10.0.400 SDK now passes Debug and Release ReadyToRun builds with 0 warnings/errors, and the current suite is 896/0/4 with the shared Swift bridge. Historical exact-SDK digest evidence remains non-reproducible across clean runs, so PKG-01/TEST-01 remain Partial and L48 owns the clean two-checkout rehearsal. |
| L46 recovered retranscription stale-flight ownership | `818fab2` | Recovered stale retranscription flights can no longer restore an older transcript over a newer accepted transcript. Focused L46 tests are 27/27 in Debug and Release; independent review found no P0–P3 findings. MTG-03 remains Implemented with verification debt for retained-audio quality and broader meeting qualification. |
| L47 protected cutover staging and coherent capture | `a5414b3` | Profile-local protected staging, stale cleanup, coherent capture/retry, cross-process lock through authority, and no-follow validation are covered by focused L47 tests 30/30 in Debug and Release; independent review found no P0–P3 findings. DATA-01 remains Implemented with verification debt because cloned-real-profile qualification has not run. |
| L08 structured support bundle | current working tree | WinUI About previews and exports a bounded, redacted support bundle; `SupportBundleTests` cover redaction, large-log bounding, locked-log tolerance, incident aggregation, and the no-network contract. DIAG-01 remains Implemented with verification debt for human inspection of a real bundle and current UI automation. |
| L17 shared text ordering (TXT-03) | current working tree | `TranscriptionPipelineService` now applies cleanup → filler → dictionary for dictation, meeting, and import; meeting/import filler runs per line body so speaker prefixes survive and is controlled by `RemoveFillerWords`. `Txt03SharedTextOrderingTests` cover the cross-workflow golden order and prefix preservation. TXT-03 remains Implemented with verification debt for the human corpus. |
| L25 summary-provider parity (SUM-02) | current working tree | `MeetingSummaryService` routes Ollama, LM Studio, and a documented custom OpenAI-compatible endpoint through one chat-completions contract; `SummaryProviderDisclosure` adds both providers with loopback/remote disclosure, and settings/UI expose endpoints, models, and a Credential Manager-backed optional key (`custom-llm-api-key`, schema 10). `Phase7NotesTests` cover the mock contract. SUM-02 remains Implemented with verification debt for opt-in live-provider runs and log-redaction review. |
| L31 automatic PDF export (AUTO-01) | current working tree | `PostMeetingMarkdownAutoExporter` is format-aware with a per-format control key/manifest; `MeetingDocumentWriter.GeneratePdfBytes` renders behind the EXP-01 gate, and `PostMeetingAutomationResult.PdfExport` records independent diagnostics. `AutoPdfExportTests` cover fail-closed while the gate is closed, atomic real-PDF publish, independent Markdown+PDF artifacts, collision safety, and manifest reuse. AUTO-01 remains Implemented with verification debt for the QuestPDF eligibility decision, human PDF open, and file failure cases. |
| L06 app-side update workflow (UPD-01) | current working tree | `SignedUpdateVerifier`/`SignedUpdateService`/`UpdateRollbackJournal` cover manifest verification, pinned publisher key, downgrade and minimum-supported rejection, staged download with hash/size checks, offline/cancellation, and interrupted-install recovery; the update-manifest writer/verifier now emit and validate `minimumSupportedVersion`/release notes, and `WindowsProductionUpdateInstaller` fails closed until D5. Settings schema is 11. `UpdateWorkflowTests` cover the fixture-crypto contract. UPD-01 remains Implemented with verification debt for production signing and signed install/upgrade evidence. |
| L41/L43 automatic-export safety net restored | current working tree | The export-integrity/concurrency suite deleted with the WPF test project is re-ported to the active tree as `PostMeetingAutoExportTests` (11 tests), including the 8×12 concurrency stress, manifest/current-render integrity, collision safety, unowned-control-directory fail-closed, temp-collision preservation, and stale-claim recovery. TEST-01/L52 retains the remaining broad suite gap. |
| EXP-01/HOOK-01 safety net restored | current working tree | `MeetingExportTests` (16 tests) restore manual export mode content, manual notes, aliases, determinism, filename safety, Markdown write, the EXP-01 PDF gate, and write-failure reporting; `PostMeetingHookTests` (21 tests) restore the Hook-01 payload, redaction/bounded output, timeout, cancellation, retry bound, descendant Job Object cleanup, and independent-export-failure contract using the real `Muesli.Automation.TestHost`. |
| Phase 4/10/12 anchors restored | current working tree | `Phase4LiveTranscriptionTests` (68 tests, 1 named streaming-fixture skip) restores live ownership/VAD/gap-recovery/dedupe/sample-rate coverage and the `MUESLI_STREAMING_QUALIFICATION_MODEL` skip contract; `Phase10ComputerUseTests` restores planner/validator/confirmation/executor/status coverage; `Phase12ProductExperienceTests` restores the portable onboarding progress, reconciliation, microphone HRESULT, and secret-free serialization coverage. The active tree now discovers 1086 tests (1081 pass / 0 fail / 5 named skips). WPF-only visual-preview/placement/XAML cases stay retired. |
| Meeting automation diagnostics surface | current working tree | The WinUI meeting detail renders the persisted `PostMeetingAutomationResult` in an "After-meeting automation" card plus a retained-output disclosure, backed by the shared `PostMeetingAutomationDiagnostics` formatter (`PostMeetingAutomationDiagnosticsTests`), closing the per-meeting-diagnostics contract in `POST_MEETING_AUTOMATION.md`. |
| Current UI automation rerun (L09/L49) | 2026-09-26, current working tree | With the pinned SDK prepended to `PATH`, `Muesli.Windows.UITests` runs end-to-end: packaged `WinUiShellQualificationTests` pass 13/14 (including the new support-bundle action, theme switch, onboarding first-run, populated-profile reference states, About diagnostics disclosure, and search routing); the full project passes 17/24. Failures are `FloatingIndicatorAutomationTests` secondary-window discovery and `Second_process_activation_returns_the_primary_shell_to_the_dashboard`. The loose `bin/unpackaged` layout is not regenerated by any script and was rebuilt manually for this run; that script gap is L52 debt. |

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
| MOD-01 | Offline Parakeet TDT with CPU/CUDA provider selection | Implemented with verification debt | Phase 1 / 13 | CPU provider is packaged; public notices/inventory stay CPU-only. CUDA delivery is an optional SHA-256-pinned acceleration pack plus a measured Automatic policy: real CUDA inference and cross-provider transcript equality are verified for Parakeet transducer, Whisper, SenseVoice, Qwen3-ASR, and Cohere on this machine (`docs/WINDOWS_GPU_QUALIFICATION.md`). The 2026-09-23 auto-provider benchmark verified the pack/runtime but correctly kept Parakeet v3 on CPU at 305 ms warm / RTF 0.055. DirectML is a proven blocker. External NVIDIA hardware matrices and long-meeting CUDA-live soak remain verification debt |
| MOD-02 | Twelve pinned offline ASR families | Implemented with verification debt | Phase 1 | Qualification: twelve-family CPU matrix; retained CUDA smoke is historical |
| MOD-03 | Prepare/cancel/retry/verify/delete/recovery lifecycle | Implemented with verification debt | Phase 1 | Qualification: Models UI during real downloads; destructive cache delete against user caches not repeated here |
| MOD-04 | Opt-in Nemotron 3.5 live model; no CoreML EOU | Implemented with verification debt | Phase 4 | Live provider selection honours the provider setting; measured CPU/CPU-vs-CUDA comparison recorded (CPU RTF 0.759 vs CUDA 0.904), so Automatic keeps CPU. EOU stays absent with a documented artifact blocker. Long meeting, Bluetooth/route, and multilingual human review remain open |
| MOD-05 | Optional Qwen/GGUF cleanup lifecycle | Partial | Phase 1 | Implementation: guided download/hash/cancel only after an approved GGUF. Today: manual cache placement, fail closed to raw text |
| MOD-06 | No silent transcription-engine fallback | Complete and verified | Phase 1 | None |
| TXT-03 | Shared filler/dictionary/cleanup ordering across dictation, meeting, import | Implemented with verification debt | Phase 2 / 5 / 8 | Dictation, meeting, and import now share the cleanup → filler → dictionary order; meeting/import filler removal runs per line body so timestamp/speaker prefixes are never rewritten and is gated by the same `RemoveFillerWords` setting as dictation. `Txt03SharedTextOrderingTests` covers the cross-workflow golden order, the opt-out default, and prefix preservation. Human microphone/name/accent corpus qualification stays under TXT-01/TXT-02 |

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
| MTG-03 | Edit title/transcript/notes; retranscribe or re-summarize | Implemented with verification debt | Phase 7 / 5 | L23 now wires transcript edit/cancel/save and candidate retranscription through meeting detail; L42 adds per-meeting retained-audio admission and deterministic concurrent candidate handling, and L46 prevents a recovered stale flight from restoring an older transcript over a newer accepted transcript. Focused L23/L27 flow is 1/1, L46 is 27/27 in Debug and Release, and the elevated UI suite is 6/6. Physical retained-audio quality and broader meeting qualification remain evidence debt |
| LIVE-01 | Live meeting transcription and floating window | Implemented with verification debt | Phase 4 | Qualification: simultaneous meeting + UI soak |
| LIVE-02 | Silero VAD natural-boundary commits (no fixed-duration cut) | Implemented with verification debt | Phase 4 | Qualification: noisy-room |
| LIVE-03 | Explicit live-preview vs unified final ownership | Complete and verified | Phase 4 | None at contract level. Physical live soak stays on LIVE-01 |
| LIVE-04 | Gap recovery, dedupe, sample-rate mapping | Implemented with verification debt | Phase 4 | Qualification: injected native crash in a physical meeting |
| DIA-01 | Offline remote-speaker diarization and You attribution | Implemented with verification debt | Phase 5 | Qualification: multi-speaker CPU/CUDA quality |
| DIA-02 | Speaker alias persistence on transcript, notes, copy, export | Implemented with verification debt | Phase 5 / 8 | Qualification: UI rename round-trip |
| API-01 | Windows process-tree loopback with disclosed endpoint fallback | Implemented with verification debt | Phase 3 | Qualification: real conferencing-app attribution |
| PLAY-01 | In-app play/pause/seek/track select | Implemented with verification debt | Phase 3 / 8 | `MeetingRecordingPlaybackService` now samples waveform peaks and the meeting detail renders them. Qualification: human playback/waveform exercise plus physical output-device-loss recovery |

### Detection (calendar excluded)

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| DET-01 | App/window/URL detection plus mic/camera evidence, false-positive policy | Implemented with verification debt | Phase 6 | Native observation/scan bounding and the dedicated WinUI meeting-notification path are implemented with suppression/action tests. Qualification still requires live Zoom/Teams/Meet/Webex false-positive/negative evidence |
| DET-02 | Prompt dedupe, dismiss, detected auto-stop, recovery | Implemented with verification debt | Phase 6 / 3 | Qualification: join/leave/rejoin. Manual recordings never auto-stop |
| JOIN-01 | Join & Record, Join Only, Record Only for detected URLs | Implemented with verification debt | Phase 6 | Qualification: live prompt actions. Calendar URLs remain excluded with CAL-01 |
| CAL-01 | Google Calendar | Excluded | — | Excluded |
| API-02 | EventKit equivalent | Excluded | — | Excluded |

### Notes, providers, organization

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| SUM-01 | Local deterministic summary plus explicit OpenAI/OpenRouter | Implemented with verification debt | Phase 7 | Qualification: live keys, timeout, log redaction |
| SUM-02 | Ollama and LM Studio/custom HTTP | Implemented with verification debt | Phase 7 | Ollama, LM Studio, and a documented custom OpenAI-compatible HTTP endpoint all run through one chat-completions contract with the same timeout, cancellation, invalid-response, disclosure, and redaction behavior. LM Studio needs no credential; the custom endpoint reads an optional bearer token from `MUESLI_CUSTOM_LLM_API_KEY` or Windows Credential Manager (`custom-llm-api-key`), never from `windows-settings.json`. `Phase7NotesTests` cover endpoint/path construction, keyless and bearer requests, missing-model/invalid-endpoint, HTTP/malformed/empty failures, and loopback-vs-remote disclosure. Opt-in live-provider runs and log-redaction review remain debt |
| SUM-03 | ChatGPT subscription OAuth | Excluded | — | Excluded / remains D1 if product later reverses |
| SUM-04 | Provider-based automatic titles with manual ownership | Implemented with verification debt | Phase 7 | Qualification: UI title ownership |
| SEC-01 | Credential Manager keys; plaintext migration | Complete and verified | Phase 7 | None |
| TPL-01 | Built-in/custom templates and re-summary | Implemented with verification debt | Phase 7 | Qualification: UI CRUD |
| NOTE-01 | Manual notes separate from generated summary | Implemented with verification debt | Phase 7 | Qualification: editor UX. Re-summarize must not overwrite manual notes (unit-covered) |
| ORG-01 | Nested meeting folders | Implemented with verification debt | Phase 8 | `ParentId` survives nested migration and save; the UI exposes move-to-root/move-to-parent, delete/reparent is recoverable across the two JSON files through a crash journal, and SQLite/JSON missing-row semantics are aligned. Retained GUI tree/breadcrumb/move/delete evidence and cloned-profile qualification remain open |
| DATA-01 | Profile-scoped protected cutover staging and coherent multi-file capture | Implemented with verification debt | Phase 8 | L47 covers profile-local protected staging, stale cleanup, coherent capture/retry, cross-process lock through authority, and no-follow validation; focused L47 tests are 30/30 in Debug and Release with no P0–P3 findings. Cloned-real-profile qualification has not run, so retain verification debt |
| SEARCH-01 | Search dictations and meetings | Implemented with verification debt | Phase 8 / 12 | JSON and SQLite paths share an interface-backed ordered, paged search contract with manual-note/snippet support beyond 500 results. The 2026-09-12 focused 10k-history Release slice passes 14/14, including all <=300 ms fastest-of-five assertions. Retained GUI and large real-profile latency evidence remain open under L29 |

### Import, export, automation

| ID | Capability | Status | Owner | Remaining |
|---|---|---|---|---|
| IMP-01 | Import supported media with cancel, progress, no fake success | Implemented with verification debt | Phase 8 | Qualification: ASR+diarization on each advertised format. Advertised set is wav/mp3/m4a/aac/mp4/mov/mkv/webm. **ogg is rejected with guidance** |
| EXP-01 | Manual Markdown/PDF export with aliases and manual notes | Implemented with verification debt | Phase 8 | Deterministic content per mode, manual notes, aliases, determinism, filename safety, Markdown write, and the PDF release gate are covered by the active `MeetingExportTests`; QuestPDF 2026.5.0 is used with `LicenseType.Community`; community-license eligibility remains a Phase 13 release-owner check. Do not treat EXP-01 as complete |
| HOOK-01 | Post-meeting `.exe` hook, JSON stdin, timeout, Job Object | Complete and verified | Phase 9 | None at contract level; the restored active `PostMeetingHookTests` re-prove the versioned payload, redaction/bounded output, timeout, cancellation, retry bound, and descendant Job Object cleanup. The WinUI meeting detail now renders the persisted automation result (status/attempts/exit/export/PDF/failure and retained redacted output behind a disclosure) through the shared `PostMeetingAutomationDiagnostics` formatter, closing the documented per-meeting-diagnostics contract. Real third-party executables are optional later smoke |
| AUTO-01 | Automatic Markdown/PDF export, atomic collision-safe | Implemented with verification debt | Phase 9 | L41 commit `2972192` adds atomic destination/manifest collision handling; L43 verifies manifest filename/path/SHA/destination/current-render integrity, preserves user edits, and publishes collision-free candidates. Automatic PDF now shares the same format-aware atomic exporter with a per-format control key/manifest and honors the EXP-01 QuestPDF gate (fail-closed with a named reason while the gate is closed); `AutoPdfExportTests` cover fail-closed, atomic publish, independent Markdown+PDF artifacts, collision safety, and manifest reuse. AUTO-01 retains verification debt for the QuestPDF eligibility decision, human PDF open, and locked-file/permission evidence |
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
| SHELL-01 | Light/dark dashboard and navigation | Partial | Phase 12 | WinUI is selected, PerMonitorV2 is embedded, settings are centrally normalized, and targeted unhandled-exception containment is wired. The last retained production-profile capture still showed only a title bar, so L49 must prove reliable visible activation before keyboard, 100–200% DPI, and multi-monitor qualification can close |
| START-01 | Launch at login | Implemented with verification debt | Phase 12 | Qualification: install/uninstall/elevation |
| INSTANCE-01 | Single-instance activation | Implemented with verification debt | Phase 12 | The 2026-09-26 current UI run re-proved second-process redirect/attach on the unpackaged host, but the stronger "activation returns the primary shell to the dashboard" assertion still fails there. `App.xaml.cs` now also signals the desktop activation pipe from the AppInstance redirect branch so a host whose `AppInstance.Activated` does not fire still raises the dashboard; the packaged path already passed. Reliable visible activation remains L49 debt |
| INSIGHT-01 | Insights analyzer and contribution | Partial | Phase 12 / D4 | Local analyzer exists. The shipping WinUI shell implements transcript-free PNG preview/copy/save/Windows sharing (`windows-native/Muesli.Windows.WinUI/ViewModels/InsightsPageViewModel.cs:71-136`). Contribution remains D4-gated; do not equate local image sharing with telemetry. |
| SOUND-01 | Configurable feedback sounds | Implemented with verification debt | Phase 12 | Qualification: hear start/insert/model-ready cues on speaker, headphone, setup-test, and disabled-setting routes |
| DIAG-01 | Redacted logs, runtime status, support bundle | Implemented with verification debt | Phase 13 | The WinUI About page now previews and exports a bounded, redacted support bundle through `SupportBundleService` (app/runtime versions, package identity, model roles, provider configuration without keys or endpoints, audio-device categories, runtime diagnostics, aggregated incident categories, and a byte-bounded log slice) with an explicit preview, save picker, and cancel. `SupportBundleTests` cover redaction, large-log bounding, locked-log tolerance, incident aggregation, and the no-network source contract. Human inspection of a real exported bundle and current UI automation remain evidence debt |
| PRIV-01 | Local-first audio, explicit network, deletion/retention | Implemented with verification debt | Every module / 13 | Qualification: destructive deletion against prepared caches; privacy copy vs live Settings |
| UPD-01 | Signed automatic updates | Implemented with verification debt | Phase 13 | App-side workflow exists: `SignedUpdateVerifier` fails closed unless a manifest verifies as RSA-SHA256-signed by the pinned publisher key, `SignedUpdateService` checks (downgrade and minimum-supported rejection, offline/cancel states) and downloads to a controlled staging directory with hash/size verification, and `UpdateRollbackJournal` recovers an interrupted install. `WindowsProductionUpdateInstaller` fails closed with `production-signing-not-configured` until the Authenticode certificate and signing environment exist (L05/D5), and About exposes honest check/status states. `UpdateWorkflowTests` cover fixture-crypto verification, tamper, downgrade, minimum-supported, offline, cancellation, tamper-download, rollback, and fail-closed install. Remaining: production signing and signed install/upgrade evidence |
| SIGN-01 | Authenticode signed app and installer | Externally blocked | Phase 13 / D5 | Decision: production certificate |
| PKG-01 | Self-contained x64 zip and Inno installer | Partial | Phase 13 | `a5414b3` exact-SDK rehearsal and package smoke pass; the recorded digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf` did not reproduce on the 2026-08-22 isolated re-run (`637a3433…`; see the baseline table), so cross-run digest stability is open as L48. Artifacts remain unsigned and upgrade/clean-VM gates remain open. Public package inventory and notices now record CPU-only; CUDA packaging is L11 |
| TEST-01 | Automated success/failure/cancel/recovery coverage | Partial | Continuous | The active WinUI assembly now discovers **1080** tests and completes **1075 passed / 0 failed / 5 explicitly skipped** with each skip naming its real prerequisite (`MUESLI_MEDIA_FIXTURE_DIR` two, `MUESLI_MULTISPEAKER_FIXTURE_SOURCE`, `MUESLI_CLONED_PROFILE_DIR`, `MUESLI_STREAMING_QUALIFICATION_MODEL`). The streaming prerequisite test is restored via `Phase4LiveTranscriptionTests`, and the Phase 4/10/12 anchors plus the L41/L43/AUTO-01/EXP-01/HOOK-01 safety nets are re-ported to the active tree. L52 owns the remainder; the 2026-09-26 UI rerun is current (shell 13/14, full UI 17/24) but live OS detection, four-app paste, mic routes, Narrator/High Contrast/non-125%-DPI/multi-monitor, and other physical coverage remain incomplete |
| QUAL-01 | Repeatable CPU/CUDA/hardware/package gates | Partial | Phase 13 | Current 5.56 s CPU Parakeet dictation and mic-only meeting pass at RTF 0.055 and 0.063. The optional CUDA pack is version/provenance verified and has real per-model evidence, but WER/CER, system-audio/diarization, broader hardware, current signed/installable package, clean two-checkout, and clean-VM gates remain open. The only retained active-app delivery trace is one 7,897 ms ChatGPT sample from 2026-09-09 |
| STORE-01 | Microsoft Store | Excluded | — | Excluded |
| ARM-01 | ARM64 | Excluded | — | Excluded |
| API-04 | Sparkle/AppKit/codesign equivalents | Partial | Phase 13 | Windows tray, release-side signed-manifest verification, and an app-side check/verify/download/rollback workflow exist; automatic installation is fail-closed until the production Authenticode signing environment exists, and signing itself is externally blocked under L05/SIGN-01. |
| OOS-01 | Python/Electron/web | Excluded | — | Excluded |
| OOS-02 | Silent fallback / fake data | Excluded | — | Excluded |
| OOS-03 | Literal macOS ports | Excluded | — | Excluded |

## WinUI shipping cutover (2026-09-16)

| Item | Fact |
|---|---|
| Shipping decision | WinUI 3 is the only active shipping shell. WPF is archived under `windows-native/Muesli.Windows.Wpf.Legacy` and removed from the solution/default dependency graph. |
| Default launch | `scripts/run-windows.ps1` uses packaged `winapp run`, x64, and the real `%APPDATA%\muesli` profile unless an explicit isolated root is supplied. Direct executable launch and `WindowsPackageType=None` are retired. |
| Default package | `scripts/package-windows-v1.ps1` now forwards to the WinUI MSIX packager; the old WPF implementation is preserved under `scripts/archive/wpf`. |
| Current proof | The shipping extraction/cutover is tracked through `ad92e43`. Through user-local SDK 10.0.400, the migrated Release suite is 901/0/4 and the bounded no-WPF scan completes. The retained dirty-tree unsigned MSIX passes payload smoke, but it predates the current tree and install/launch qualification remains unavailable because the package lacks a production signature and retains placeholder publisher identity. |
| Remaining gates | Exact-SDK/default Release completion, restored streaming fixture coverage, reliable visible activation/current UI automation, clean committed two-checkout rehearsal, signed/installed-MSIX qualification, physical accessibility/DPI, and real end-to-end capture/paste/meeting workflows remain open. L50/L51 implementation exists but retains live verification debt. |

## Historical WinUI 3 shell snapshot (2026-08-28)

At this point in the migration, WinUI ran as a preview and WPF was still the shipping fallback. The
entries below are retained as historical evidence and are superseded by the 2026-09-16 cutover above.

| Item | Fact |
|---|---|
| How it runs | **Unpackaged.** `scripts/run-winui-preview.ps1` builds with `WindowsPackageType=None` into `bin/unpackaged/` and launches against an isolated `MUESLI_PROFILE_ROOT`. The bootstrapper binds the already-installed `Microsoft.WindowsAppRuntime.2` 2.4.0.0 x64 framework package, so no elevation is needed. |
| Packaged registration | Still blocked. `Add-AppxPackage -Register` on the loose build now reaches the licensing check and fails `0x80073CFF` — Developer Mode or a sideloading policy is required, and both need an elevated session. This is an environment gate, not a manifest defect. |
| Manifest defects fixed | `windows.startupTask` was declared under `desktop6:Extension`, which does not accept that category; deployment rejected the manifest outright (`0xC00CE169`). It now uses `uap5:Extension`. The same extension used `$targetnametoken$.exe`, a token only substituted for `Application/@Executable`; `MakeAppx` failed payload validation on it. Both are pinned by `WinUiPackagingContractTests`. |
| MSIX | `scripts/package-winui-msix.ps1` produces `Muesli.Windows.WinUI_1.0.0.0_x64.msix` (**106.46 MB**, Release x64, unsigned) plus the four `Microsoft.WindowsAppRuntime.2` dependency packages. The only remaining build warning is the optional `mspdbcmf.exe` symbols tool. The script does **not** install or qualify the package and says so. |
| Trimming | `PublishTrimmed` was on for Release from the project template. The trimmer reported 21 warnings against reflection-based `System.Text.Json` in the persistence layer (`AtomicJsonFile`, `WaveformPeakCache`, the model verification stamps) and COM marshalling in `WindowsProcessLoopbackCapture`. Trimming is now off; re-enabling it requires a `JsonSerializerContext` for every persisted type. |
| Native payload | `LLamaSharp.Backend.Cpu`'s props run before the RID-neutral `Muesli.Windows.Core` has a `RuntimeIdentifier`, so its fallback glob emitted `runtimes\\<rid>\...` for **every** platform. The doubled separator is an invalid package path and blocked `MakeAppx`; the linux/musl/osx binaries could never load on Windows. `NormalizeLLamaSharpNativeRuntimes` in `Muesli.Windows.Core.csproj` keeps only the Windows natives and repairs the link. The WPF host still loads its CPU Sherpa runtime from `runtimes\win-x64
ative` after the change. |
| Package assets | The tile, logo, and splash assets were the Visual Studio template placeholders. `scripts/generate-winui-assets.ps1` now regenerates them from the active WinUI `Assets/AppIcon.ico`, so package branding no longer depends on the archived WPF tree. |
| Crash fixed | `InsightsPage` bound a `string` view-model property to `Image.Source`. On a fresh profile the feature tour navigates to Insights, the empty string failed `ImageSource` conversion, and the shell died with a stowed exception before the user saw the page. The view-model now exposes a `Uri?` and the page code-behind performs the conversion, which also keeps the view-model free of UI framework types per `CoreAndViewModelsContainNoUiFrameworkTypes`. |
| Theme | The theme was applied to `MainPage` only, so the custom title bar and every secondary window stayed on the system theme. `App.TrackTheme` now applies it to each window root and keeps it in sync. |
| Startup truthfulness | The unpackaged shell has no package identity, so `StartupTask.GetAsync` throws. The toggle was merely disabled; it now publishes `StartupAvailabilityNotice` explaining that launch-at-sign-in needs the packaged build. |
| Automation | `MuesliWinUiSession` + `WinUiShellQualificationTests` (`[WinUiAutomationFact]`, gated on `MUESLI_UI_AUTOMATION=1` **and** the unpackaged build being present). **7 passed, 0 failed** on 2026-08-28: all nine navigation destinations with a page-specific automation id each, the meetings idle contract, search routing on both a matching and a non-matching term, the Insights share render, startup unavailability, light-vs-dark repaint measured by mean luminance, and second-instance redirect. Per-page captures land in `artifacts/ui-automation/`. |
| Suite impact | `Muesli.Windows.Tests` is **830 passed, 0 failed, 5 explicitly skipped of 835** on the Debug build after these changes; the solution builds with **0 warnings, 0 errors**. |

Open WinUI debt, unchanged by this pass: packaged-identity behavior (MSIX startup task, `AppNotifications`), dictation and meeting capture end to end against real audio, models/offline behavior, clipboard restoration, tray and startup from a registered package, Narrator/keyboard-only/High Contrast/DPI scaling, and the full visual comparison against `docs/ui-reference`. None of these is claimed as passing.

One WPF UI automation case, `MeetingDetailProductionTests.Sqlite_meeting_detail_edit_cancel_save_candidate_and_restart_persist`, fails in this environment: the seeded meeting card is found on-screen, but clicking it does not navigate to the detail view, so `Meeting title` never appears. The other five WPF UI cases pass and a direct WPF launch is clean. The failure is in the card click-through path, which commits `c1168e7` and `1829702` have already patched twice; it is not attributable to any change in this pass.

## Remaining implementation work

Ordered by launch module. Calendar/OAuth, Store, ChatGPT OAuth, CloudKit, audio sync, ARM64, and macOS framework ports are omitted because they are excluded.

| Module | Work |
|---|---|
| Phase 1 | Guided Qwen cleanup catalog/download only after an approved GGUF (MOD-05). |
| Phase 2 | AUD-03 stays decision-gated. |
| Phase 8 | L29's 10k-history search paths are back within the <=300 ms automated budget; retain GUI/large-real-profile evidence. Nested folders and playback waveform are implemented and retain only named GUI/device/cloned-profile qualification debt (ORG-01, PLAY-01). |
| Phase 9 | FOLLOW-01 only after a destination contract. AUTO-01 PDF stays release-gated on the QuestPDF eligibility decision (EXP-01). |
| Phase 10 | Observation masking if text/screenshots are ever enabled (CU-01 residual). |
| Phase 11 | Nothing until D3. |
| Phase 12 | Local analyzer exists and WinUI has local image sharing. Contribution stays D4-gated. L49 owns reliable activation and shipping-shell qualification. L50 null-bearing settings normalization and L51 targeted WinUI unhandled-exception containment are implemented; retain live fault/navigation verification debt. |
| Phase 13 | Production certificate and signed install/upgrade evidence after D5 (SIGN-01/UPD-01/L06). Clean-VM upgrade/uninstall (PKG-01). |

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
| Phase 13 | Signed zip/installer; clean VM install/upgrade/uninstall; fresh-log packaged launch; human inspection of a real exported support bundle. |

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

Current line of work: `codex/wave0-launch-foundation` (`a5414b3` at this review). `main` is an ancestor at `ba38e56` (“Checkpoint launch-ready Python pipeline”) and must not be used as a merge source for native work.

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
| 46 / 56 / 74 / 128 / 165 / 195 / 337 / 484 / 634 / 709 / 761 / 778 / 792 / 804 / 807 / 870 / 900 tests | Current active-tree evidence is **1080 discovered, 1075 passed, 0 failed, 5 explicit prerequisite skips** (the 2026-09-23 exact-SDK run recorded 901/0/4 of 905 with the shared Swift package). The 31 stale WPF-path failures and unbounded source scan are fixed, and the streaming prerequisite test is restored (`Phase4LiveTranscriptionTests`) rather than missing. SDK 10.0.400 is available only through the user-local .NET host, not the default `dotnet` on `PATH` |
| Manual notes missing | `PersistedMeeting.ManualNotes`, editor, export, Phase 7 tests |
| Ollama missing | `MeetingSummaryService` Ollama path + settings. LM Studio and a documented custom OpenAI-compatible HTTP adapter now share the same chat-completions contract |
| Live transcription missing | `MeetingLiveTranscriptionSession` + window + Phase 4 tests; default Off |
| Detection lifecycle missing auto-stop | `MeetingAutoStopTracker`; dismiss/suppress in detection tests |
| Onboarding is only a completion flag | `OnboardingProgressStore` with step, deferred, verification flags, resume |
| Import cancellation missing | `Phase8ImportCancellationTests` + UI cancel |
| Exports untested | `Phase8ExportTests` covers MD/PDF, aliases, manual notes, failure |
| ogg advertised | Rejected with conversion guidance; not in `MediaImportFormats.Supported` |
| Tray is Open/Quit only | Recent items, detected now, resume setup, tour, settings, about. Upcoming calendar honestly disabled |
| Search cannot index notes because notes do not exist | Notes exist; current production search includes them through SQLite FTS or the in-memory fallback |
| Phase 5 = calendar | Launch Phase 5 = finalization. Calendar is excluded |
| Nested-folder saves flatten `ParentId` / folder deletion is inferred from snapshots | Current JSON/SQLite adapters preserve `ParentId`; explicit move/delete/reparent operations and nested migration coverage are green. Cloned-profile and retained GUI qualification remain open |
| Search is capped at 500 and wired only through concrete SQLite behavior | JSON and SQLite implement the shared interface-backed ordered, paged search contract beyond 500 results. The 2026-09-12 focused 10k-history slice is green 14/14, so SEARCH-01/L29 now retain only GUI/real-profile verification debt |
| Waveform sampling is synchronous and uncached | Playback waveform loading is asynchronous, cached, invalidated, and cleaned up with recording deletion; physical playback/device-loss qualification remains open |
| Signed updater is entirely missing | Release-side signed-manifest/hash/publisher-pin verification plus an app-side check/verify/download/rollback workflow now exist, covered by `UpdateWorkflowTests`. Automatic installation remains fail-closed until production signing exists (D5), so UPD-01/API-04 stay Implemented with verification debt. |
| L06 is Implemented with verification debt | Historically false in the 2026-09-12 dashboard (release tooling is not an app updater). The app-side workflow now exists, so L06/UPD-01/API-04 are Implemented with verification debt pending production signing and signed install/upgrade evidence. |
| No retained all-green WPF/WinUI UI report exists | Superseded by the 2026-09-13 20/20 combined run and re-tested on 2026-09-26: packaged shell qualification is 13/14 and the full UI project is 17/24, with the failures named (floating-indicator secondary-window automation and one unpackaged second-instance dashboard-return case). L09 is Implemented with verification debt, while L49 remains Partial for reliable visible activation, packaged identity/real workflows, and physical accessibility/DPI gates. |
| The WinUI cutover preserved the 845-test release baseline | The 2026-09-17 claim was false, but the stale-path failures and hang have since recovered: the active tree now discovers 1080 tests with 1075 passing, 0 failing, and 5 named fixture skips. The streaming fixture test and the Phase 4/10/12 anchors are restored. L52 remains Partial because current UI automation is historical and clean-checkout/current-package proof is open. |

## Concurrent work notice

Wave 0 integration is committed at `efa961c`; the reviewed line now ends at `a5414b3` with focused Wave 1 source/tooling slices and L42–L47 fixes. The build and test facts above describe that committed source plus this review's document edits. Do not merge stale feature or divergent module branches wholesale on top of it.

2026-08-29 re-review note: HEAD remains `a5414b3` with a much larger dirty working tree containing the untracked shared Core/Platform extraction and WinUI shell. The required WPF Release build is **0 warnings, 0 errors**, but the full Release suite is **827 passed / 3 failed / 5 explicitly skipped of 835**; all skips name their prerequisites. SEARCH-01/L29 is reopened to Partial for the 10k-history performance failures, L09 is reopened for the WPF meeting-detail click-through failure, and new L49 owns committing and qualifying the dual-shell migration. The current CPU dictation benchmark narrowly misses RTF <=0.20 at 0.205; mic-only meeting passes at 0.159; WER/CER remain unmeasured. Historical WPF release artifacts remain unsigned and predate the shared extraction; the WinUI MSIX is also unsigned and has not been installed/qualified. A foreground WPF Dictations dashboard launch produced a clean fresh log slice. No commit, merge, staging, or push occurred.

2026-09-12 re-review note: HEAD is unchanged at `a5414b3`, while the working tree grew to 243 porcelain entries / 636 expanded untracked files. Pinned SDK 10.0.400 is absent; fallback 10.0.401 build passes, but the guarded suite completes 828/0/5 with two unfinished before the shared-project source scan hangs. L29 search is green 14/14 and returns to Implemented with verification debt. L41 is reopened to Partial for an intermittent manifest-read failure. CPU dictation passes at RTF 0.178; mic-only meeting fails at 0.852; the latest one-sample ChatGPT delivery is 7,897 ms. No package, signing, clean-VM, CUDA, human WER/CER, or physical meeting/device qualification was run.

2026-09-14 re-review note: HEAD remains `a5414b3`; the dirty tree grew to 251 porcelain entries and remains unstaged/uncommitted. Exact SDK 10.0.400 is absent. Fallback 10.0.401 Release build is 0/0; the 45-second guarded suite records 834/0/5 before aborting in the unbounded shared-project scan, while retained 2026-09-13 evidence is 840/0/5 of 845 and UI 20/20. L41 closes after 8×12 focused stress. CPU dictation and mic-only meeting pass at RTF 0.117/0.118; WER/CER, system audio, diarization, physical routes, signing, clean VM, current package, and exact-SDK evidence remain open. New L50/L51 own the settings-null crash class and WinUI global exception policy.

2026-09-17 re-review note: HEAD remains `a5414b3` and the uncommitted WinUI cutover expanded the tree to 312 porcelain entries. Exact SDK 10.0.400 remains absent. The default fallback WinUI Release build fails `NETSDK1094`; only the diagnostic `PublishReadyToRun=false` compile is 0/0. Current discovery fell from 845 to 706 tests; excluding the known unbounded scan, the result is 670/31/4 of 705. All four surviving skips explain their prerequisite, but the streaming qualification skip/test disappeared. L52 owns restoring the release build, lost/misdirected tests, bounded scan, and streaming prerequisite contract. CPU dictation/meeting RTF is 0.149/0.181; WER/CER and the named physical/external gates remain open. No obsolete branch was merged and no commit/push occurred.

2026-09-20 re-review note: HEAD advanced to `ad92e43`, tracking the Core/Platform/WinUI cutover and migrated tests; the dirty tree contracted to 53 collapsed entries before this review's three document edits. Exact SDK 10.0.400 remains absent. The default fallback WinUI Release build still fails `NETSDK1094` and emits six warnings, but with the shared Swift package supplied the current suite completes 866/0/4 of 870; every skip names its media, multi-speaker, or cloned-profile prerequisite. The streaming qualification test is still absent. A final Debug build succeeds only with the bridge quarantined; the source-backed bridge rebuild fails for missing `sqlite3.lib`/`lzfse.lib`, and the final clean launch logs the managed fallback. L50/L51 advance to Implemented with verification debt; L40/L49/L52 remain Partial. CPU dictation/meeting RTF is 0.072/0.051; WER/CER, current active-app latency, system audio/diarization, CUDA, signing, clean-VM, and physical gates remain open. Packaged launch produced a responsive titled window and clean 885-byte log slice, but the unavailable native computer-control surface prevented dashboard-content capture. No obsolete branch was merged wholesale and no commit/push occurred in this review.

2026-09-23 re-review note: HEAD remains `ad92e43` (29 ahead), while the unstaged dirty tree expanded to 135 collapsed entries / 589 expanded untracked files. The literal requested legacy WPF build path is gone and the default `dotnet` host cannot resolve the pin, but user-local SDK 10.0.400 builds the active WinUI Release project with 0 warnings / 0 errors and runs 901 passed / 0 failed / 4 explicit prerequisite skips of 905. All four skips name their fixture; the streaming prerequisite gate remains absent. MOD-01/L11 advances to Implemented with verification debt on the pinned optional CUDA pack, measured provider policy, and real per-model graph evidence. Current CPU Parakeet dictation/meeting RTF is 0.055/0.063; WER/CER remain unavailable and the only active-app delivery trace is the 7,897 ms ChatGPT sample from 2026-09-09. The retained 2026-09-21 unsigned MSIX still passes smoke but predates the current tree. Final Debug build and packaged production-profile launch are clean; the process is responsive with a titled window, the log confirms Muesli was foreground at startup, and no fresh error signature appears. Computer Use exposed no native app inventory after reset, so dashboard contents were not screenshot-certified. No obsolete branch was merged and no staging, commit, or push occurred.
