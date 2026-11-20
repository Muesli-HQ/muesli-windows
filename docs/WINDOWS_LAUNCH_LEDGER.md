# Muesli Windows launch ledger

Inventory date: 2026-09-23.

This is the **authoritative product-status source** for Windows Muesli. It replaces status claims in older phase plans, the 2026-08-01 parity matrix, and `ROADMAP.md`. The shipping shell is under `windows-native/Muesli.Windows.WinUI`; the retired WPF product shell was removed. Tests live under `windows-native/Muesli.Windows.Tests`. The macOS tree at `C:\Users\madha\Downloads\muesli-main\muesli-main` is the behavioral reference, not a library to port.

## Merge readiness update (2026-09-26)

The tracked Windows work is split into draft PRs [#23](https://github.com/Muesli-HQ/muesli-windows/pull/23) and #31–#36, in order. The proposed macOS shared-core PR #540 was withdrawn, and no approved `Muesli-HQ/muesli` commit contains the `MuesliCoreABI` (capabilities 0x3), so `windows-native/shared-core.lock.json` records `bridgeRequired=false` / `shippingMode=managed-fallback`: Windows release packaging ships the parity-tested managed text processor and must not require the Swift bridge. The `unsigned-release` CI job therefore skips the Swift/vcpkg/shared checkout in that mode and rehearses a clean managed-fallback MSIX; a pinned ABI commit still requires an immutable revision. The managed-fallback suite passes **1062 / 0 failed / 30 skipped** (five named fixtures plus the 25 bridge-optional tests), and the packaged WinUI app opens the Timeline dashboard and logs `fallback=parity-safe-managed` with no startup error signatures. Signing, clean-machine install/upgrade, physical accessibility, and long-form dictation qualification remain open. Main merges run unsigned CI and do not deploy.

If this ledger disagrees with any other document, this ledger wins.

**Launch status (2026-10-14):** the code blockers in `LAUNCH_BLOCKERS_2026-10-10.md` are fixed on `codex/computer-use` (PR #42). See that file's "Status on 2026-10-14" section. What remains:
- release-owner decisions: channel and certificate, publisher DN, privacy/support URLs, updates, low-memory fallback, mic pre-warm;
- a signed build from a tagged commit;
- clean-VM and physical qualification.

The draft-PR paragraph above is historical.

**Original blocker list (2026-10-10):** `LAUNCH_BLOCKERS_2026-10-10.md`. #38 and #39 are merged and the draft stack above is closed. That review fixed three product P0s (global Escape swallowed after the first dictation, meeting pill discarding without confirmation, microphone pre-warm before onboarding) and records the signed-installer blockers (S1–S13) and open product P1s.

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
| App | Packaged WinUI 3 `.NET 10` x64 is the selected shipping shell at `windows-native/Muesli.Windows.WinUI`; the retired WPF product shell was removed, while `Muesli.Windows.Indicator.Wpf` remains the active companion. Shared behavior lives in `Muesli.Windows.Core` / `Muesli.Windows.Platform`. SDK `10.0.400` remains exactly pinned in `global.json`. No Python worker, Electron, or web wrapper. |
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
| EXP-01 | Manual Markdown/PDF export with aliases and manual notes | Implemented with verification debt | Phase 8 | Deterministic content per mode, manual notes, aliases, determinism, filename safety, Markdown write, and the PDF release gate are covered by the active `MeetingExportTests`; QuestPDF 2026.5.0 is used with `LicenseType.Community`; community-license eligibility approved 2026-10-11 (open-source project). Do not treat EXP-01 as complete |
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
| Shipping decision | WinUI 3 is the only active shipping shell. The retired WPF product shell was removed from the repository and solution/default dependency graph; the WPF companion remains active. |
| Default launch | `scripts/run-windows.ps1` uses packaged `winapp run`, x64, and the real `%APPDATA%\muesli` profile unless an explicit isolated root is supplied. Direct executable launch and `WindowsPackageType=None` are retired. |
| Default package | `scripts/package-windows-v1.ps1` forwards to the WinUI MSIX packager; the old WPF packaging implementation was removed. |
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

## macOS parity work queue (reviewed 2026-10-31, macOS `2f10b23d`)

This is the standing backlog for macOS parity. An agent works through it one item at a time until no
row is open.

**Sources:**
- `MACOS_CHANGES_2026-10-30.md` has every macOS commit:
  - `main`: 276 commits, `cdcd9c6..2f10b23d`;
  - active branches: 7 branches, 40 commits;
  - older branches: 72 branches, 198 unreleased commits.
- Every `main` commit appears in exactly one row below. The test, docs and process commits are
  included too.
- Reference checkout: `C:\Users\madha\projects\muesli-macos`.

### Loop protocol

1. **Pick the next item.** Take the first open row in the priority order below. Inside a row, take
   the first unchecked done criterion.
2. **Read the macOS code before writing any Windows code.**
   - Run `git show <hash>` for every commit in the row, and read the files it touches.
   - Port the behaviour and its invariants, not the Swift structure.
   - When Windows can't do something macOS does, write the deviation down in the row.
3. **Build in the right project.** Shared behaviour goes in `Muesli.Windows.Core` or
   `Muesli.Windows.Platform`. UI goes in `Muesli.Windows.WinUI`.
4. **Prove it.**
   - Add xUnit tests that fail without the change, and run the targeted suite:
     `dotnet test Muesli.Windows.Tests --no-restore -p:MuesliSkipSwiftBridge=1 -p:Platform=x64`.
   - Kill `Muesli.Windows.WinUI.exe`, rebuild, and relaunch with `scripts\run-windows.ps1 -SkipBuild`.
   - Check the newest `%APPDATA%\muesli\logs\muesli-*.log` for ERROR, Unhandled or XamlParse lines.
5. **Update the row.** Set the new status, list the Windows anchors (`path:line`), and record the
   tests and any remaining gap. Never commit or push unless the user says "push to git".
6. **Keep to the product rules.**
   - Local-first, and fail closed when a model is unavailable.
   - Capture is always disclosed. No fake or placeholder data.
   - `%APPDATA%\muesli` stays compatible.
   - Excluded rows (CAL-01, OOS-03 Apple frameworks) remain excluded.

### Status values

| Status | Meaning |
|---|---|
| **Done** | Behaviour ported with tests; physical check recorded |
| **Built** | Code exists and is accepted; listed gaps remain |
| **Partial** | Some layers exist; the row lists the missing layers |
| **Missing** | No Windows code |
| **Verify** | Windows has nearby code; compare it with the invariants and either close the row or turn it into work |
| **Artifact** | Needs a model Windows can run (macOS uses CoreML/MLX); port the contract once a GGUF/ONNX artifact exists |
| **N/A-mac** | Apple hardware or framework only. Any portable invariant is listed under another row |
| **Process** | macOS repository process (CI, DCO, README). No product work |
| **Watch** | Unmerged macOS branch. Re-check when it lands on `main` |
| **Excluded** | Out of scope (calendar, see CAL-01) |
| **Reference (abandoned)** | Old unmerged macOS branch. Background reading only; not work |

**Rule: port the final code at `2f10b23d`, not individual commits.** Some listed commits were later reverted or superseded on macOS. A commit list is history; the spec is the code at the tip.

### Priority order

| Priority | Rows |
|---|---|
| **P0: critical list** | MAC-01 Ask Meetings UI → MAC-02 screen recording → MAC-03 in-room speakers → MAC-33 Cotypist |
| **P1: correctness, data and capture safety** | MAC-35 mic pipeline; MAC-32 live-dictation gaps; MAC-17 recovery; MAC-07 summaries; MAC-06; MAC-08; MAC-10; MAC-16; MAC-19 (all Done 2026-10-31 except MAC-35: Built, physical Zoom check pending) |
| **P2: features** | MAC-15 per-app formatting; MAC-05 Granola; MAC-11 storage; MAC-24 languages; MAC-23 Bodhan naming; MAC-04; MAC-09; MAC-31 indicator (all Done 2026-10-31; MAC-24 Nemotron locales are Watch) |
| **P3: models and polish** | MAC-25; MAC-27; MAC-28; MAC-36; MAC-37; MAC-12; MAC-13; MAC-14; MAC-18; MAC-20; MAC-26; MAC-30; MAC-21; MAC-22; MAC-34 |
| **Last (kept by user decision 2026-10-31)** | MAC-38 Claude Code results (only if a Claude Code provider is added); MAC-29 CLI (decide how a Windows CLI should look, then build it) |
| **Ongoing** | Watch rows (W-01..W-07, older branches) on every macOS review |

---

### Meetings

#### MAC-01 Ask Meetings (P0). Status: Built (physical hosted-provider check pending)

**Commits:**
- Core feature: `d671fcb4` `0e51ed7d` `c2670d1f` `d9e59ecd` `c1ec613c` `1ae8e461` `d29918b6` `7e0f0104` `3d3816e0`.
- Follow-ups: `e92ccc2f` `8eae522b` (design/plan); `03e3891f` `a7f9fb2e` `c28e77f8` `b952cffd` (retrieval);
  `cb8f850b` `855749cc` `a8ea30da` `0b04ca77` `a4bd271e` `00768616` `38ea11c0` `fedb5ea2` `01fe34bc` `2575276e`
  (lifecycle and UI); `940e3fa0` `e1121e05` (harness and AIElementsSwift UI); `d81968c2` `e80f1d6b` `f3b117b6`
  (budgets and caps); `0bee39e1` `cb68f286` `a1e84426` (docs and agent invariants); `c7eed1a4` (UI simplification).

**macOS files to read for the UI:**
- `MeetingChatView.swift`, `MeetingChatCoordinator.swift`, `MeetingChatCitationView.swift`,
  `MeetingChatScopePicker.swift`, `MeetingChatModelPicker.swift`, `MeetingChatResponseView.swift`,
  `MeetingChatDraftView.swift`, `MeetingChatQuickAction.swift`, `MeetingChatExporter.swift`.
- The chat entry points in `MeetingDetailView.swift`, `SidebarView.swift` and `DashboardRootView.swift`.
- The chat components in `native/AIElementsSwift`.
- `docs/meeting-chat-architecture.md` and `database-schema.md`, section "Chat threads".

**Windows today:**
- Already ported, in `Muesli.Windows.Core/Meetings/Chat/`:
  - schema 3 (`MeetingChatSchema.cs`); store and retrieval (`MeetingChatStore`);
  - compatibility loop (`MeetingChatClient.cs`); native Responses harness (`MeetingChatNativeHarness.cs`,
    `MeetingNativeToolTransport.cs`);
  - budget (`MeetingChatContextBudget.cs`); engine (`MeetingChatEngine.cs`);
  - transport (`MeetingTextGenerator.cs`).
- Tests: `MeetingChatStoreTests`, `MeetingChatClientTests`, `MeetingChatNativeHarnessTests`.
- Coordinator: `Muesli.Windows.Core/Meetings/Chat/MeetingChatCoordinator.cs` (sessions, per-chat drafts,
  attempt ownership, Stop, titles, source invalidation). Presentation helpers (quick actions, mentions,
  source groups, waiting phrases, export text, scope labels): `MeetingChatPresentation.cs`.
  Tests: `Muesli.Windows.Tests/MeetingChatCoordinatorTests.cs` (10).
- App-wide coordinator: `Muesli.Windows.WinUI/App.xaml.cs` `App.MeetingChat`. It is null on a JSON-only
  profile, and the page then fails closed with an upgrade message.
- Shared view: `Muesli.Windows.WinUI/Controls/MeetingChatView.xaml(.cs)` (macOS `MeetingChatView`).
  - The page `Pages/AskMeetingsPage.xaml` hosts it standalone. The sidebar entry "Ask Meetings" sits after
    Meetings (`MainPage.xaml`, tag `askmeetings`) and calls `ShowIndex`, like macOS `showMeetingChatIndex`.
  - `MeetingDetailPage.xaml` hosts it embedded (`EmbeddedMeetingId`), pinned below the document and hidden
    while recording or finalizing. Embedded mode has:
    - a resting composer: "Ask about this meeting…", "Ask your meetings…" or "Continue conversation…";
    - expanding into a framed panel (20 px radius, 260 px turns) with open-full, collapse and New chat
      scoped to the meeting;
    - `PrepareMeetingDraft` on load and again when the chat goes idle (`01fe34bc`).
  - Citations: "Open in meeting" calls `MainPage.ShowMeetingDocument(id, target)`, which sets
    `App.MeetingChatDocumentTarget` (port of `MeetingChatDocumentTarget` in `MeetingChatPresentation.cs`).
    It fails with "This source meeting is no longer available." when the meeting is gone. Meeting detail
    then:
    - opens the Transcript or Notes tab by citation kind;
    - shows the banner "Referenced passage · kind" with the excerpt, plus "The exact passage has changed…"
      when `Locate` misses;
    - highlights and scrolls to the cited transcript bubble (`LiveTranscriptFeed.Highlight`);
    - turns Back into "Back to Chat".
  - Any sidebar navigation clears the target.
- The view covers:
  - the index (welcome, composer, quick-prompt chips, recent chats) and a header with back, a history
    flyout with Rename/Delete, and New chat;
  - turns with scope-change captions, rotating waiting phrases and research-step expanders;
  - Markdown answers where only `[[S#]]` citations stay links; sources grouped by meeting;
  - a citation flyout with change and availability checks and "Open in meeting";
  - Copy, and Export answer / edited draft through `MeetingDocumentWriter` (Markdown or PDF), then opening
    the file;
  - Try again; the deleted-source notice;
  - the composer: `@` mentions with keyboard navigation, chips, the scope dialog (all, folder or meetings,
    plus a date range), a provider menu, Stop/Send, Enter to send, Shift+Enter for a new line;
  - the send guard "Connect your meeting AI provider…".
- Model and thinking (slice 4):
  - `Muesli.Windows.Core/Meetings/SummaryModels.cs` ports `SummaryModelPreset` (OpenAI and ChatGPT
    lists), `ReasoningEffortPolicy` (per-model levels and defaults, label text) and
    `MeetingSummaryModels` (`Model`, `PolicyModel` with the OpenRouter `openai/` prefix stripped, `Effort`).
  - New settings `ChatGptSummaryModel` (empty means the first preset, like macOS `chatGPTModel`) and
    `MeetingSummaryReasoningEffort` (macOS `meetingSummaryReasoningEffort`), in `SettingsStore.cs`.
  - Both are used by:
    - the native transport (`MeetingNativeToolTransport.RequestBody` takes the effort, and reserves the
      32K budget only when the effort isn't "none");
    - OpenAI summaries;
    - ChatGPT summaries (`CompleteWithChatGptAsync` model and effort; Quill keeps "low").
  - Summary request JSON omits null fields, so models without reasoning send no parameter.
  - Picker: `MeetingChatView.Model_Click` — the provider menu with "Connect a provider…", the preset
    model menu (or the configured model as text), a thinking slider for ChatGPT/OpenAI, "Shared with
    meeting summaries", and "AI Settings…". The composer label reads "model · thinking".
  - Test: `SummaryThinkingFollowsTheModelsLevelsAndDefaults`, plus the reasoning assertions in
    `RequestBodyRegistersSchemaToolsAndReasoningBudget`.
- Still missing: the reasoning disclosure. ChatGPT streams reasoning-summary events when asked for
  `summary: auto`; the Windows stream reader doesn't surface them yet.
- `MeetingChatDraftView.swift` exists on macOS at `2f10b23d` but nothing presents it. Windows keeps the
  draft menu items, which only appear when `EditableDraft` is set, and has no editor (parity).

**Invariants: harness (ported; keep them)**
- **Loop:** inference → retrieval → inference.
  - Native transports: up to 12 tool calls, 512 KB serialized input, 200 KB cumulative evidence.
  - Compatibility transport: 5 inferences and 3 tools.
  - Exactly one final-answer repair. Native finalization sends `tool_choice: none`.
- **No silent downgrade.** A native-provider error does not fall back to the compatibility path.
  Native call IDs are never reused. Compatibility mode rejects a repeated tool signature.
- **Citations must resolve** to the current evidence packet. Never invent citations or attach them
  automatically during a repair. A citation proves where text came from, not that the claim is true.
- **History is not evidence.** History is labelled untrusted conversation, and old markers are
  stripped before history is sent.
- **Answer status.** `conversation` requires usable history. `answered` means newly cited meeting
  facts. Otherwise the status is `insufficient_evidence`.
- **Budgets.**
  - Context window: native 64K, other cloud 32K, local or custom 8K.
  - Caps: input 32K, recent history 8K, rolling memory 2K. Output is reserved separately.
  - Estimate: ASCII bytes / 3, plus non-ASCII UTF-8 bytes, plus envelope overhead.
- **Compaction.** At most 16 batches, with one size repair each. Revalidate sources before and after
  each call. If compaction fails, keep the history and report an error the user can recover from.
- **Selection, anchor and evidence stay separate.**
  - An empty explicit selection never widens to the whole archive.
  - Removing a meeting excludes it from new retrieval only.
  - When a source is deleted or edited, everything derived from it is scrubbed: answers, excerpts,
    drafts, rolling summary and generated title. Inherited dependencies are scrubbed too.
- **Attempt ownership.** A stopped or superseded attempt never publishes. Send and Retry with an
  empty explicit selection and no conversation are rejected without reserving busy state.

**Invariants: UI (to build)**
- **Ask Meetings index page.**
  - The composer and recent conversations are in the main view.
  - The history panel stays collapsed until a conversation has a submitted question (`2575276e`).
    Blank "New chat" sessions don't open it. Deleting the last conversation collapses it again.
    History existence comes from a bounded query.
- **Embedded panel in meeting detail.**
  - The panel is collapsible, with a distinct header and surface, and shares conversation state with
    the full page.
  - Collapsing keeps the editor and its undo history.
  - Opening a saved meeting keeps the shared selected conversation. With no selection and an empty
    draft, it seeds an unpersisted draft for that meeting. A non-empty draft keeps its text and scope.
  - Preparing an empty draft is retried when the coordinator goes idle (`01fe34bc`).
  - Ordinary meeting navigation never auto-selects an anchored chat.
- **`ShowMeetingChat(meetingId)`.** Resumes the newest anchored chat that still explicitly includes
  that meeting, or prepares a draft. It refuses while a response or chat update is running. Chats
  that only cited the meeting are not matched.
- **Composer.**
  - Grows from 34 to 144 px, and a bottom inset keeps the last answer and source rows reachable.
  - Newly mounted editors take focus (`fedb5ea2`).
  - Each chat keeps its own unsent composer text. Undo boundaries survive retries (`00768616`,
    `38ea11c0`).
  - Composer edits are kept while the prompt is being prepared. A cleared or superseded setup never
    restores the question (`a4bd271e`).
- **`@` mentions.**
  - Caret-aware search over saved meeting titles, with dated results, keyboard navigation and
    removable chips.
  - Meeting choices load from saved metadata, without waiting for the passage index.
  - Attaching a meeting switches scope to explicit meetings and clears date filters. Further
    attachments accumulate.
  - Removing the last chip leaves an empty scope; it does not switch to the archive.
  - Earlier chats are not selectable as sources.
- **Citations.**
  - Grouped by meeting, keeping each numbered passage, its source type, its timestamp and an excerpt
    preview.
  - Clicking opens the exact passage.
  - Display, copy, edit and save all use the same written-note or generated-note source. Editing a
    written-note citation keeps the generated notes (`0b04ca77`).
  - An empty meeting with no citation target still opens (`a8ea30da`).
- **Activity.**
  - Show only tool operations that actually ran.
  - While waiting on the provider, neutral status phrases rotate every 5 s. Retrieval, validation and
    repair keep their own labels.
  - A reasoning disclosure appears only when the provider sends reasoning-summary events (ChatGPT,
    `summary: auto`). It is transient: cleared on stop, retry or source invalidation, and never saved.
- **Model picker.** Shows the configured provider's model catalog and thinking levels, using the same
  setting definitions as meeting summaries.
- **Provider errors.** Give specific guidance for configuration, authentication, rate limits or
  connectivity, without raw server details. With the `local` provider, ask the user to connect a
  hosted provider.
- **Scope.** When the saved scope differs from the current selection, show the saved scope
  (`a4bd271e`).
- **Quick prompts.** "My next steps" uses the saved participant name.
- **Export.** Copy, Markdown and PDF, with citations. PDF lays out every page before printing.
- **Titles.**
  - After the first successful answer, a background request generates a title with the same
    provider, model and reasoning.
  - A failure leaves the provisional title.
  - Manual renames, newer turns and scope changes win.
  - Legacy titles that match the old default can be auto-updated.
- **Escape never stops a meeting.** Only one thinking indicator is shown.

**Done when:**
- [x] WinUI `AskMeetingsPage` plus a coordinator with attempt ownership (`AskMeetingsPage.xaml.cs`,
  `MeetingChatCoordinator.cs`). The idle retry belongs to the embedded panel (below).
- [x] Embedded panel in meeting detail, with shared state and the idle draft retry (`01fe34bc`)
  (`MeetingChatView.ChatChanged`, `MeetingDetailPage.UpdateMode`).
- [x] `@` mentions with chips, and a scope picker (archive, folder, dates) (`AskMeetingsPage.xaml.cs`
  `RenderMention`, `Scope_Click`).
- [x] Citation view with exact-passage navigation (`MeetingChatView.OpenSource`,
  `MeetingDetailPage.ShowCitationTarget`). Test: `DocumentTargetsFindMovedPassagesAndTheirTranscriptBubble`.
- [x] Activity rows and rotating status phrases.
- [x] Conditional reasoning disclosure (ChatGPT `summary: auto` events; transient, never saved).
  `ReasoningSummaryAccumulator` (`Muesli.Windows.Core/Meetings/Chat/MeetingNativeToolTransport.cs:242`)
  reads only `reasoning_summary_*` events and the final item's `summary_text` parts; raw and
  encrypted reasoning never reach it. `MeetingTextGenerationRequest.OnReasoningSummary`
  (`MeetingTextGenerator.cs:11`) adds `reasoning.summary=auto` on the native and plain ChatGPT
  streams. The coordinator keeps one summary per iteration (`MeetingChatCoordinator.cs:66,457`),
  re-checks `CanPublishSummary` before showing a delta, and stops on invalidation. The view shows
  "Thinking…"/"Reasoning" and hides the waiting label while it streams
  (`Muesli.Windows.WinUI/Controls/MeetingChatView.xaml.cs:818`). Test:
  `ReasoningSummariesStreamOnlyPublicSummaryText`.
- [x] Model and thinking picker (`MeetingChatView.Model_Click`, `SummaryModels.cs`).
- [x] Quick prompts; export (copy, Markdown, PDF) (`PrepareAsync`, `AnswerActions`, `ExportAsync`).
- [x] Background title generation hooked to the engine (`MeetingChatCoordinator.ScheduleTitle`).
- [x] Navigation-entry tests and presentation tests (`MeetingChatCoordinatorTests`:
  `MeetingEntryResumesOnlyAnchoredChatsThatStillIncludeTheMeeting`,
  `PreparingAMeetingDraftNeverRetargetsAConversationOrTypedDraft`,
  `ExportsRenumberCitationsAndGroupSourcesByMeeting`, `DocumentTargetsFindMovedPassagesAndTheirTranscriptBubble`).
- [ ] Physical check with a hosted provider on real meetings (needs the user's ChatGPT/OpenAI sign-in; the library uses Local).

#### MAC-02 Meeting screen recording (P0). Status: Built (storage entry deferred to MAC-11; physical recording check pending)

**Commits:** `6a343f41` `a19ff38c` `fe7f8802` `6ad20c9d` `73f2342f` `ca73406a` `f6330b80` `2cdb7b63`, plus
`996e6c2e` (README).

**macOS files to read:**
- `docs/meeting-screen-recording.md`.
- `ScreenVideoRecorder.swift`, `MeetingScreenSourcePicker.swift`, `MeetingMediaView.swift`.
- `MeetingVideoStore.swift`, `MeetingVideoAudio.swift`, `VisualMemoryController.swift`.
- `LocalStorageUsageView.swift`, `scripts/test_meeting_video_encoding.py`.

**Windows mapping:**
- Capture: `Windows.Graphics.Capture` (`GraphicsCaptureItem` picker or `TryCreateFromWindowId`/`FromMonitor`).
- Encoding: Media Foundation `IMFSinkWriter` (HEVC hardware encoder, falling back to H.264) with AAC.
- Playback: `MediaPlayerElement`.

**Invariants:**
- **Start.**
  - Only by an explicit user action during a meeting: "Record Screen…" → preview grid of windows
    and displays → confirm.
  - Previews are transient. System utility windows and Muesli's own windows are excluded.
- **Announcement.** An optional local announcement plays on the selected output only, never into the
  call. If it fails, show a warning and keep recording.
- **Format.**
  - One MP4 per uninterrupted interval.
  - Video: at most 2560 px wide, 15 fps, about 2 Mbps.
  - Audio: AAC 16 kHz mono at 32 kbps, taken from the existing cleaned mic and system feeds.
- **Pause and resume.**
  - Screen pause and stop are independent controls.
  - Pausing the meeting pauses video. Resuming the meeting doesn't override an explicit screen pause.
  - Sleep or lock pauses capture, for privacy.
- **Retention.**
  - Ending the meeting saves the video automatically.
  - A meeting with screen recording skips the separate "keep audio?" prompt. An explicit never-keep-audio
    preference still applies. The bypass applies only if screen recording started in that capture
    session.
  - Playback doesn't need a separate audio file.
- **Storage.**
  - Files go in `meeting-recordings/videos/`, next to the retained audio.
  - Table `meeting_video_clips`: text id, kind, meeting FK with cascade, capture time, JSON body.
  - Table `meeting_video_cleanup`: a durable outbox with no meeting FK.
  - Deletion triggers queue cleanup before rows disappear. A background sweep retries file removal.
  - Restoring a tombstone doesn't restore deleted media.
- **Disk.** Capture needs at least 2 GB free.
- **Recovery.**
  - Clip ownership is journaled before capture starts. A completed video is journaled before the
    database write.
  - Recovery publishes a file only after checking that it contains playable video. Active writers
    are excluded.
  - A stop failure keeps finalized clips and pending recovery media.
  - Discard queues cleanup even if finalization failed. A corrupt journal doesn't block the other
    clips.
  - Discarding a resumed session removes only its new clips.
- **Audio timing.**
  - Mic positions use the host clock and are kept through AEC, with a 3.5 s encoding buffer for
    delayed AEC output. System samples are timestamped when received.
  - Delayed mic samples are flushed before every clip finalization.
  - Late samples are never shifted forward.
  - Audio drain has a 2 s monotonic deadline; after that, drop the unwritten tail and finalize.
- **Finishing.**
  - Meeting completion waits at most 8 s for screen finalization. After a timeout, finalization keeps
    going in the background and the journal stays until the real completion callback.
  - The quit deadline covers screen discard and audio cleanup (`2cdb7b63`).
- **Playback controls.** Clips play one at a time below Notes and Transcript (`fe7f8802`, simplified
  controls). There is no seeking linked to the transcript.
- **Legacy.** A legacy visual-memory catalog is migrated off the UI thread and survives individual
  failures. A failed legacy store doesn't block new recordings (`ca73406a`).

**Done when:**
- [x] Persistence migration for both tables and the triggers. Schema v4
  (`windows-native/Muesli.Windows.Core/Services/Persistence/PersistenceSchema.cs`, `MeetingVideoSchema`):
  `meeting_video_clips` (meeting FK, cascade) and the `meeting_video_cleanup` outbox (no FK), and a
  `BEFORE DELETE` trigger that queues files first. Windows hard-deletes meetings, so the macOS
  soft-delete trigger and tombstone sweep have no counterpart. `MeetingVideoStore`
  (`windows-native/Muesli.Windows.Core/Meetings/Video/MeetingVideoStore.cs`) ports save, the
  `.recording.json`/`.pending.json` journals, `Maintain` recovery and sweep, `Delete` and `HasMedia`.
  There is no legacy visual-memory catalog on Windows, so `migrateLegacyVideos` is N/A. Tests:
  `MeetingVideoStoreTests` (4).
- [x] Recorder (WGC + Media Foundation) with pause, resume and sleep/lock handling.
  - [x] Clip encoder: `windows-native/Muesli.Windows.WinUI/Services/ScreenVideoRecorder.cs`.
    Windows.Graphics.Capture feeds a `MediaStreamSource` into `MediaTranscoder`: HEVC, falling back
    to H.264; at most 2560 px wide; 15 fps; 2 Mbps; AAC 16 kHz mono at 32 kbps. Audio has a 3.5 s
    encode delay, a 160 000-sample fall-behind guard and a 2 s drain deadline. The 8 s stop timeout
    releases only the waiter. Capture stops when free space drops below 2 GB. The capture pool keeps
    its first size (a resized window is cropped or padded). A static screen repeats the last frame,
    so audio keeps flowing. The system capture border is the visible disclosure.
  - [x] Timestamped mix: `windows-native/Muesli.Windows.Core/Meetings/Video/MeetingVideoAudio.cs`
    (`MeetingVideoAudioBuffer`, `MeetingVideoAudioSpans`). Tests: `MeetingVideoAudioTests`.
    Windows has no AEC stage, so the spans map is unused until one exists.
  - [x] Session controller: `windows-native/Muesli.Windows.WinUI/Services/MeetingScreenRecording.cs`,
    a port of `VisualMemoryController` without the legacy catalog. It writes one clip per
    uninterrupted interval. `ScreenRecordingPauseState` keeps an explicit screen pause across a
    meeting resume. `SuspendForPrivacy` runs on `SessionSwitch` lock or disconnect and on
    `PowerManager` suspend (`App.xaml.cs`). Recovery and sweep run every 30 s, and
    `GetVideoPropertiesAsync` decides whether a recovered file is playable. Session clip IDs are
    removed on discard. Audio tap: `MeetingCaptureSession.ScreenAudioSink`, on the meeting clock
    (part offset × 16). `WinUiMeetingContext` saves the draft row before clips reference it
    (`EnsureDraftSaved`), and `ScreenRecordingFinishing` finishes (stop) or discards the clips
    before capture stops or is cancelled. Deviation: clips live in `capturesideos`, not in the
    per-meeting `recordings` tree, so the journal recovery scan never sees them. Not yet
    physically verified (no picker yet).
- [x] Journal and recovery. `MeetingVideoStore.BeginRecording` journals ownership before capture,
  `PersistCompleted` journals before the database write, and `Maintain` publishes a recovered file
  only when it is playable, skips active writers and skips unreadable journals. A stop failure keeps
  the clip and its journal. Discard queues cleanup even when finalization failed.
  `MeetingScreenRecording.DiscardSessionAsync` removes only that capture session's clips. Tests:
  `RecoveryPublishesOnlyPlayableUnfinishedClipsOfLiveMeetingsAndSkipsActiveWriters`,
  `DeletingClipsIncludesJournalsNotYetSaved`.
- [x] Picker UI with visible disclosure. `windows-native/Muesli.Windows.WinUI/Controls/MeetingScreenSourcePicker.cs`
  is a `ContentDialog` with Windows / Entire displays tabs. Its preview grid uses transient
  in-memory thumbnails (`Win32Desktop.CaptureWindow` and `CaptureScreen`). It lists only visible,
  non-minimized windows larger than 100 px, never tool windows or Muesli's own. The announcement
  checkbox and "Record Screen" confirm are on the same dialog. The entry point is the recording
  toolbar's screen button (`MeetingDetailPage.Screen_Click`: Record Screen…, Pause/Resume Screen
  Recording, Stop Screen Recording). The `ScreenStatus` line shows "Recording screen · source",
  the paused and saving states, and the last screen-recording message. The capture border is the
  system disclosure. Announcement: macOS plays a bundled clip, while Windows speaks the same
  sentence with `SpeechSynthesizer` on the default output; a failure warns and recording continues
  (`MeetingScreenRecording.PlayAnnouncementAsync`).
- [x] Playback in meeting detail. `MeetingDetailPage.RenderVideo`
  (`windows-native/Muesli.Windows.WinUI/Pages/MeetingDetailPage.xaml.cs`) ports `MeetingMediaView`:
  `VideoPanel` sits above the audio card. It has a "Part n · time" picker when there are several
  clips, and one `MediaPlayerElement` with inline transport controls. A Video/Audio `SelectorBar`
  appears only when a separate audio recording exists, and it swaps `AudioHost`. A missing file shows
  "Screen recording unavailable". Playback stops on unload or when the selection changes. There is
  no seeking linked to the transcript, as on macOS.
- [x] Deletion cleanup and retention-prompt bypass.
  - Deleting a meeting (one or all) fires the schema v4 trigger, which queues the clip files in
    the outbox; `MeetingScreenRecording`'s 30 s sweep then removes them. Recovery discard keeps a
    draft that owns screen video (`WinUiMeetingContext.DiscardRecoveryAsync`, `HasScreenMedia`;
    a read error keeps the draft). Tests: `DeletingAMeetingQueuesItsClipsAndTheSweepRemovesTheFiles`,
    `DeletingAllMeetingsQueuesEveryClip`.
  - Retention-prompt bypass: N/A on Windows. The audio setting is a boolean
    (`SaveMeetingRecordings`, always or never) with no "ask" prompt to bypass. "Never keep audio"
    still applies with screen recording, as on macOS.
- [ ] Storage breakdown entry (MAC-11). Deferred to MAC-11 (P2): `MeetingScreenRecording.Clips` and `MeetingVideoClip.Bytes` provide the totals.
- [x] Encoding regression test. `windows-native/Muesli.Windows.MediaProbe` is a port of
  `scripts/test_meeting_video_encoding.py`. It is not in the solution or the release path. It compiles
  the production `ScreenVideoRecorder.cs` and encodes 2 s of synthetic PCM through
  `ScreenVideoRecorder.AudioInput`/`AudioOutput`/`AudioSample` with `MediaTranscoder`, then checks
  the duration is at least 1.9 s. Run: `dotnet run --project windows-native\Muesli.Windows.MediaProbe -p:Platform=x64`
  (PASS recorded). The macOS writer-stall and delayed-finalization probes target `AVAssetWriter`
  internals that have no `MediaTranscoder` counterpart; the bounded drain and waiter timeout live in
  `ScreenVideoRecorder.StopAsync`.
- [ ] Physical recording, then reopen the clip.

#### MAC-03 In-room speakers (P0). Status: Built (physical two-person check pending)

**Commits:** `4070432d` `bf72a0f6` `8206adba` `4545d80c` `d4c2c9e4` `231f9f16` `e4f47b71` `0a53a83c` `4cd2f91b` `5c78a109`.

**Invariants:**
- **Setting.** Opt-in `diarize_microphone_audio`, off by default. It lives in the meeting
  transcription settings, with short help text and the toggle aligned with its subtitle.
- **Mic track.**
  - Record the AEC-cleaned mic stream to a temporary WAV, from the same path that feeds the mic
    chunks, so the timestamps share the mic segment timeline.
  - The temp directory is named after the app identity, so one identity's launch sweep can't delete
    another's track.
  - The track is deleted after processing and on discard.
  - Leftover tracks are swept at launch.
  - A repeated start keeps the existing recorder; it never orphans it. An empty WAV is removed if its
    handle fails to open.
- **Diarization pass.**
  - Runs after the meeting, over the mic track, with bounded PCM windows streamed straight to both
    diarization passes (`d4c2c9e4`).
  - Shows an "Identifying Speakers" processing stage.
- **Significance.**
  - A diarized ID counts only with at least 5 s, applied once in `SpeakerAttribution.localDiarization`.
    The final macOS code (`4545d80c`) dropped the 5 % share, and Windows follows it.
  - Brief local speakers in long meetings are kept (`4545d80c`).
  - One significant speaker, a failure, or the setting off → keep "You".
- **Attribution.**
  - Split mic turns into sentences before attributing them.
  - Attribute by total overlap per speaker, not the single longest stretch.
  - Refuse a merge whose combined range would resolve to a different speaker.
  - The reconciler never merges adjacent mic turns across a change of local speaker.
- **Labels.**
  - "Local speaker N", numbered separately from remote "Speaker N".
  - The transcript chat parser recognises "Local speaker N" and "Local speaker".
- **Diagnostics.** Record the significant speaker count from the raw diarization (one speaker counts
  as 1, not 0), plus setup and diarization errors.
- **Rotation and discard.** The diarization recorder is torn down inside the chunk-rotation barrier on
  discard. The track matches the cleaned mic chunks across rotation, pause/resume and the final flush.

**Windows build:**
- **Setting.** `DiarizeMicrophoneAudio`, off by default (`windows-native/Muesli.Windows.Core/Services/SettingsStore.cs:538`).
  The toggle is "Identify speakers in the room" under Settings › Meetings
  (`windows-native/Muesli.Windows.WinUI/Pages/SettingsPage.xaml:747-761`). It was checked over UIA after relaunch: it renders, and it is Off.
- **Mic track.** Windows has no AEC and already writes the whole mic track (`tracks.MicrophonePath`) for every
  meeting. The journal owns that file's sweep, discard and retention, so no second recorder is needed. Rotation,
  pause/resume and the final flush are already covered by `BuildFinalTracks` and `BuildTrackTimeline`.
- **Diarizer pass.** `MeetingFinalizationPipeline.DiarizeMicrophoneAsync`
  (`windows-native/Muesli.Windows.Core/Services/MeetingFinalizationPipeline.cs:55-78`) runs DIA-01's
  `NativeDiarizationClient` over the mic track after the system pass. Its result is normalised onto the mic timeline.
  Any failure other than cancellation leaves the mic as "You".
- **SpeakerAttribution.** `windows-native/Muesli.Windows.Core/Services/SpeakerAttribution.cs`:
  - `LocalDiarization` (:28), with a 5 s significance threshold.
  - `SpeakerId` (:37): total overlap per speaker, ties broken by ID, then the nearest speaker within 2 s.
  - `SentenceSegments` (:60): durations weighted by visible length.
- **Formatter.** `TranscriptFormatter.Merge` takes `micDiarizationSegments`
  (`windows-native/Muesli.Windows.Core/Services/TranscriptFormatter.cs:12-35`). With several local speakers it splits
  mic turns into sentences and labels them "Local speaker N", numbered separately from remote "Speaker N". The remote
  side now uses the same `findSpeaker`, including the macOS rule that a single label maps directly. Labels are set
  before consolidation, so turns are never merged across a change of local speaker.
- **Chat parser.** It recognises "Local speaker N" and "Local speaker"
  (`windows-native/Muesli.Windows.Core/Services/TranscriptChatMessage.cs:21`).
- **Processing stage.** "Identifying Speakers" runs from the pipeline through `MeetingRecordingCoordinator.ProcessingStage`
  (`windows-native/Muesli.Windows.Platform/Services/MeetingRecordingCoordinator.cs:62-69,585-590`) and
  `WinUiMeetingContext` (:278) to the banner (`windows-native/Muesli.Windows.WinUI/ViewModels/LibraryPageViewModels.cs:693`).
- **Diagnostics.** The log records "Local speaker diarization completed. speakers=N" with the significant count from
  the raw result (one speaker counts as 1), or "...failed. category=..."
  (`MeetingFinalizationPipeline.cs:63,68`).
- **Tests.** `windows-native/Muesli.Windows.Tests/SpeakerAttributionTests.cs` has 5 tests. Phase5's no-overlap fallback
  test now uses two remote speakers, because of the single-label rule. 76/76 targeted tests pass.

**Done when:**
- [x] Setting.
- [x] Mic-track recorder: N/A on Windows. The retained full mic track already exists, its sweep and identity
  scoping come from the journal, and there is no AEC.
- [x] Diarizer pass using the Windows diarization runtime from DIA-01.
- [x] SpeakerAttribution port with tests.
- [x] Reconciler and formatter labels.
- [x] Processing stage.
- [x] Diagnostics.
- [ ] Physical check with two people on one mic.

#### MAC-04 Template choice while recording; simpler recording layout. Status: Done (2026-10-31; picker not yet clicked during a real recording)

**Commits:** `14617391` `0eda9473` `23c3fe69`.

**Invariants:**
- [x] The template can change mid-recording. The recording keeps its template fallback.
- [x] Empty-note formatting is preserved.
- [x] Contextual controls, and notes with a border.

**Done when:** WinUI meeting detail during recording offers a template picker that is applied at
finalization, with a test for the fallback. ✔

**Windows build (2026-10-31):**
- **Runtime** (`windows-native/Muesli.Windows.WinUI/Services/WinUiMeetingContext.cs`):
  - `SelectRecordingTemplate` (:154) ports macOS `selectRecordingTemplate`. It saves the choice to the draft
    meeting's `TemplateName` without writing notes or changing the default. It refuses with the macOS messages
    once Stop begins or when the template no longer exists.
  - `LockRecordingTemplate` (:168) ports `lockMeetingTemplateForProcessing`: the saved choice, else the last
    one saved this session if the library can't be read. Stop calls it before capture stops (:325).
  - `FinalizeMeetingAsync` (:607) summarizes with the locked template. Recovery uses the meeting's own
    template, then the default. Notes are stamped with it (:652).
  - The recorded-meeting merge keeps the draft's template (`Muesli.Windows.Core/Services/MeetingNotesComposer.cs:118`).
- **UI:** a "Template: <name>" drop-down under the recording title
  (`Muesli.Windows.WinUI/Pages/MeetingDetailPage.xaml:140`, `MeetingDetailPage.xaml.cs:469`,
  `ViewModels/LibraryPageViewModels.cs:569`). It is disabled once Stop begins, like macOS.
- **Empty-note formatting and layout:** already covered. The Heading, Bold, Bullet and Checkbox bar is always
  shown while notes are editable, `Format` publishes the marker at once
  (`Muesli.Windows.WinUI/Controls/MarkdownNotesEditor.cs:161`), and the notes sit in a bordered editor.
- **Tests:** `Muesli.Windows.Tests/RecordingTemplateTests.cs` covers the merge and the lock-before-stop order.
- **Owed:** choosing a template during a real recording, which per the rules I don't start myself.

#### MAC-05 Granola importer. Status: Done (2026-10-31; no real Granola export has been imported yet)

**Commits:** `0504c515` `0c7f5187` `eef6ac8a`.

**macOS files to read:** `docs/plans/granola-csv-import.md`, `GranolaImport.swift`, `GranolaImportView.swift`,
`SyncOriginDisplay.swift`.

**Invariants:**
- ZIP and CSV import.
- Table `meeting_imports` is created lazily. Its key is `(provider, document_id)`, it has a meeting FK
  with cascade, and it stores workspace, owner email and import time locally.
- Import metadata is committed with its meeting and folder in one transaction.
- Re-importing keeps existing records, including tombstones; it never overwrites user edits.
- Imported notes survive autosave. Word counts are refreshed.
- The origin is shown in the list and in detail.
- ZIP library licence notice (ZIPFoundation on macOS; use `System.IO.Compression` on Windows, so no
  notice is needed).

**Done when:**
- [x] Parser with fixtures.
- [x] Transactional import with dedup.
- [x] Import UI.
- [x] Origin display.
- [x] Autosave-preservation test.

**Windows build (2026-10-31):**
- **Parser** (`windows-native/Muesli.Windows.Core/Services/GranolaImport.cs`):
  - `Read` (:40) applies the macOS limits: 50 MB input and uncompressed CSV, 10,000 meetings, 1,000 ZIP entries,
    128 columns.
  - `ReadZip` (:68) reads entries in memory with `System.IO.Compression`. It ignores `__MACOSX` and dot files,
    rejects absolute, `..`, backslash and encrypted entries, and verifies each entry's length and CRC32.
  - `Parse` (:102) accepts strict UTF-8 only and requires the expected columns. Every row needs a UUID and an ISO
    date with an offset. A malformed row rejects the whole file.
  - `CsvRows` (:143) is RFC 4180 with a BOM, multiline fields and CRLF.
  - Error strings are the macOS ones.
- **Plan** (`Plan`, :214):
  - Meeting id = sync identity `meeting-granola-<uuid>`. Existing ids are skipped and never overwritten.
  - The top-level "Granola" folder is reused, or created only when a meeting is new.
  - Duration is 0. The summary goes to generated notes and the notes to manual notes.
  - Word count = transcript + manual notes.
- **Library** (`Muesli.Windows.WinUI/Services/WinUiLibraryContext.cs:118`):
  - Meetings are written in one SQLite transaction (or one JSON document write). A folder created for them is
    removed if that write fails.
  - Provenance (workspace, owner email, import time) is local, in `meeting-imports.json`
    (`Services/MeetingImports.cs`), and is removed with its meeting (:166).
- **UI:** "Import Granola" on Meetings (`Pages/MeetingsPage.xaml:188`; `ImportGranolaAsync`,
  `ViewModels/LibraryPageViewModels.cs:908`).
  - The tooltip carries the Granola export steps.
  - Parsing runs off the UI thread. A preview dialog shows the count, workspaces and the 5 newest meetings, then
    "Import". The status reads "Imported N meetings from Granola".
- **Origin:**
  - The list badge is "Imported from Granola", with the macOS help text (`LibraryPageViewModels.cs:1289`).
  - Detail shows "Created <date>" and "Imported from Granola · <workspace>" in place of duration
    (`ViewModels/MeetingDetailViewModel.cs:650`).
- **Autosave:** `MeetingNotesComposer.ApplyManualNotes` keeps summary and transcript, and refreshes the word count
  as transcript + notes (`Muesli.Windows.Core/Services/MeetingNotesComposer.cs:159`).
- **Tests:** `windows-native/Muesli.Windows.Tests/GranolaImportTests.cs` (12; synthetic fixtures; the library test
  uses an isolated profile). The 429 meeting/notes tests pass.
- **Verified 2026-10-31:** the button is on the Meetings page (`winapp ui`) and the launch log is clean. The real
  library was not imported into.

**Deviations:**
- The folder write and the meetings write are two steps: a compensating delete replaces macOS's single transaction.
- There are no tombstones on Windows (meetings are hard-deleted), so a deleted import comes back on re-import.
- Single-file pick. macOS allows several CSVs at once; a ZIP covers the multi-part export.
- No sanitized export-guide image; the steps are in the tooltip and plan text.
- The badge isn't hidden inside the Granola folder view.
- Ordering inside the JSON library is by append. The meetings list sorts by date.

#### MAC-06 An empty recording is marked empty. Status: Done (P1)

**Commits:** `6bb10e8e` `32790f6c`.

**Invariants:**
- No transcript and no typed notes → skip the title and summary calls, keep the existing title, and
  write the empty-recording notes. At 2f10b23d the sentinel is "No transcript available"; the earlier
  "No speech was captured in this recording." was replaced.
- When re-summarizing, that exact sentinel counts as blank input for `existingNotes` and
  `previousMeetingNotes` only.
- Retranscription already refuses an empty transcript.

**Windows build (2026-10-31):**
- **The check.** `MeetingSummaryService.HasNothingToSummarize` and `EmptyRecordingNotes`
  (`windows-native/Muesli.Windows.Core/Services/MeetingSummaryService.cs:271-279`) port the final macOS check.
  - The transcript and typed notes must both be blank.
  - Earlier notes must be blank, or hold only the sentinel.
  - Windows sends no visual context or `existingNotes` to the provider, so those two inputs aren't needed.
- **Summary.** `CreateSummaryResultAsync` returns the sentinel before choosing a provider (`:215`).
  - Before, a blank transcript returned blank notes even when typed notes existed. Typed notes alone now count as
    content.
- **Title.** `CreateTitleAsync` keeps the existing title for an empty recording (`:1459`), so the title prompt
  can't receive the sentinel. Both the finalize and Generate Notes paths go through these two methods.
- **Verified:** `Muesli.Windows.Tests/Phase7NotesTests.cs` covers both commits.
  - `AnEmptyRecordingIsMarkedEmptyAndNeverCallsAProvider` checks blank and sentinel earlier notes, and that the
    title is kept.
  - `AnyRealContentStillReachesTheProvider` checks typed notes, earlier notes and a transcript.
  - 414 meeting and summary tests pass. The app relaunched and the log is clean.

**Done when:**
- [x] A Windows summary-pipeline test covers both commits.

#### MAC-07 Summary output caps and truncation. Status: Done (Anthropic half: Watch, no Windows Anthropic provider) (P1)

**Commits:** `7d2c1ff0` `f3771ce9` `675bb4b1` `eff55b36`.

**Invariants:**
- **Output length.** The provider controls output length by default. There is an optional advanced
  token limit, shared with the CLI.
- **Anthropic.**
  - Uses the model maximum, with discovery for unknown models.
  - Streams above 21,333 tokens.
  - Rejects a `max_tokens` stop, in JSON and SSE, with guidance on what to do.
  - Requires a `message_delta` stop reason before `message_stop`.
- **Retries.**
  - `model_context_window_exceeded` → reject with context guidance, no retry.
  - SSE error types are kept. Rate limit, server error, timeout and overloaded may retry; permanent
    and unknown errors stop.
  - Token exhaustion doesn't auto-retry. Manual retries are independent.
- **Redaction.** Custom-provider messages stay redacted.

**Windows build (2026-10-31):**
- **No default cap.** The hardcoded `max_tokens = 2500` is gone from every summary request. The user's optional
  limit is the only cap:
  - `MeetingSummaryOutputPolicy` is at `windows-native/Muesli.Windows.Core/Services/MeetingSummaryService.cs:1558`,
    with the macOS presets and normalization.
  - It is passed only by the summary path (`:260`), so dictation cleanup through `CompleteAsync` stays
    provider-controlled, as on macOS.
- **Each API's own field:**
  - OpenAI Responses: `max_output_tokens` (`:371`).
  - Ollama: `num_predict`, with -1 when unlimited (`:516`).
  - Chat completions (LM Studio, Custom): `max_tokens`, or `max_completion_tokens` for an openai.com host (`:647`).
  - OpenRouter: `max_tokens` (`:732`).
  - ChatGPT manages its own limit.
- **Setting.** `MeetingSummaryMaxOutputTokens` (JSON `meetingSummaryMaxOutputTokens`, null by default, normalized) is
  at `windows-native/Muesli.Windows.Core/Services/SettingsStore.cs:541`, normalized at `:252`.
  - The Settings › Meetings row "Output token limit" (`Muesli.Windows.WinUI/Pages/SettingsPage.xaml:1023`) uses the
    macOS caption and choices. It shows only for API backends (`ViewModels/SettingsPageViewModel.cs:226`), and a saved
    custom value stays in the list (`:358`).
- **CLI.** N/A: the Windows command host doesn't generate summaries.
- **Anthropic.** Watch. Windows has no Anthropic summary provider, and its only streamed summary is the ChatGPT
  Responses stream, which manages its own limit. When an Anthropic provider is added, port these from
  `MeetingSummaryOutputPolicy.swift` at 2f10b23d:
  - the model maximums and the `/models` discovery;
  - streaming above 21,333 tokens;
  - rejecting a `max_tokens` or `model_context_window_exceeded` stop, in JSON and SSE, with the macOS guidance;
  - requiring a `message_delta` stop reason before `message_stop`;
  - the SSE error-type retry classes, with these permanent and never retried.
- **Verified:** `Muesli.Windows.Tests/MeetingSummaryOutputLimitTests.cs` checks the request body for every Windows API
  provider, with no limit and with a limit, plus the openai.com custom endpoint and normalization. 108 summary and
  settings tests pass. The app relaunched and the log is clean.

**Done when:**
- [x] A parser, retry and truncation test matrix exists for every Windows hosted provider. Truncation-stop parsing is
  Anthropic-only on macOS, so it is tracked under the Watch item above.

#### MAC-08 Escape and repeated start. Status: Done (P1)

**Commits:** `fd1b68f0` `6a49dcf9`.

**Invariants:**
- **Escape.** The meeting hotkey has no cancel path. Escape during a toggled meeting keeps the session,
  fires nothing, and isn't consumed. A pending second press is cancelled silently. Dictation, Computer
  Use and Quill keep cancel.
- **Repeated start.** `start()` claims a one-shot flag and requires the preparing phase before it
  touches chunk timing, recorders or AEC. Otherwise mic turns restart at 0 and are misattributed.

**Windows build (2026-10-31):**
- **Escape (`fd1b68f0`).** Windows already behaved this way.
  - `App.RegisterMeetingShortcut` registers the meeting key with no cancel predicate (`Muesli.Windows.WinUI/App.xaml.cs:482`).
    The hook only takes Escape for a registration that can cancel. That decision is now
    `GlobalHotkeyService.OwnsEscape` (`Muesli.Windows.Platform/Services/GlobalHotkeyService.cs:158`), so Escape during
    a meeting fires nothing and reaches the focused app.
  - The meeting key toggles on key down and has no hold timer, so there's no pending second press to cancel.
  - Dictation keeps its cancel through `DictationCancellationPolicy`.
- **Repeated start (`6a49dcf9`).** Windows already behaved this way.
  - `MeetingRecordingCoordinator.StartAsync` takes the lifecycle gate, then refuses while recording or busy (busy
    includes Preparing). It refuses before `Prepare`, chunk timing, recorders or live transcription are touched
    (`Muesli.Windows.Platform/Services/MeetingRecordingCoordinator.cs:111-119`).
  - The state machine also refuses `Prepare` from any active or ending state.
  - Concurrent starts are serialized by the same gate. Resume only runs from RecoverableInterruption.
- **Verified:**
  - `Muesli.Windows.Tests/MeetingHotkeyEscapeTests.cs` covers the hotkey.
  - Five new rows in `Phase3MeetingLifecycleTests.InvalidTransitionsFailWithoutChangingState` cover a repeated
    `Prepare` from Preparing, Recording, DegradedRecording, Stopping and Finalizing.
  - 81 hotkey and lifecycle tests pass, 1 skipped. The app relaunched and the log is clean.
  - A coordinator-level double-start test would need real capture devices, so the state-machine rows stand in for it.

**Done when:**
- [x] Tests prove both on the Windows hotkey and meeting coordinator.

#### MAC-09 Quick Note and audio import keep the selected folder. Status: Done (2026-10-31)

**Commits:** `7c3abb66`. **Done when:** a test checks that both creation paths use the current folder. ✔

**Windows build (2026-10-31):** this was missing; both paths saved to the library root.
- `windows-native/Muesli.Windows.WinUI/Services/WinUiMeetingContext.cs`:
  - `StartQuickNoteAsync(…, folderId)` (:224) files the draft into the folder (:227). The recorded-meeting merge
    keeps `FolderId`.
  - `ExistingFolder` (:232) drops a folder deleted meanwhile, like macOS's `SELECT id FROM meeting_folders`.
  - The import sets `FolderId` once transcription finishes (:544).
- `Muesli.Windows.WinUI/ViewModels/LibraryPageViewModels.cs`: Quick Note passes `SelectedFolderId` (:751).
  The import captures it before transcription starts (:888), so navigating away doesn't move it.
- The tray and hotkey Quick Note (`App.xaml.cs:464`) has no folder context and still files to the root, as on
  macOS.
- **Test:** `Muesli.Windows.Tests/MeetingCreationFolderTests.cs` (a source contract; no test builds the
  meeting runtime).

#### MAC-10 Bounded drain and quit deadline. Status: Done (P1)

**Commits:** `f6330b80` `4a36479f` `ef318ff9` `2cdb7b63`, plus tests `e9d4f2a9` `875664ea`.

**Invariants:**
- Quit Anyway waits at most 8 s for shutdown, then exits and logs the step it was stuck on.
- Termination is scheduled outside the UI dispatch queue.
- Shutdown-deadline tests: a release that arrives before the job parks is remembered; a parked job
  is resumed after the deadline.

**Windows build (2026-10-31):**
- **Quit deadline (`4a36479f`).** Before this, `RequestExitAsync` awaited dictation cancel and the meeting
  shutdown checkpoint with no limit. The checkpoint waits on the lifecycle gate, so a stuck meeting operation
  meant the app never quit.
  - These steps now run under `ShutdownDeadline.RunAsync` with the macOS 8 s default
    (`windows-native/Muesli.Windows.Core/Services/ShutdownDeadline.cs`; `Muesli.Windows.WinUI/App.xaml.cs:515`).
  - At the deadline it logs `Shutdown still waiting on step=…` and quits anyway (`:526`). An overrunning step is
    left running.
  - Each step is guarded, so a dictation failure can't skip the meeting checkpoint.
- **Session end.** The `7c99376` session-end checkpoint (`App.xaml.cs` SessionEnded) already had a bound: it waits
  4 s, inside Windows' roughly 5 s allowance.
- **Quit Anyway.** This deviation is kept on purpose. Windows refuses to quit while a meeting is preparing,
  stopping or finalizing (`:509`); it doesn't offer Quit Anyway. The deadline covers everything else.
- **Termination outside the UI dispatch queue (`ef318ff9`).** N/A. It fixed an AppKit `terminateLater`
  run-loop deadlock. WinUI closes the window from an ordinary awaited continuation, and both the deadline and
  shutdown are already running by then.
- **Bounded drain (`f6330b80`).** Already ported. `ScreenVideoRecorder.StopAsync` gives the audio drain a 2 s
  deadline, then finalizes the encoded media (`Muesli.Windows.WinUI/Services/ScreenVideoRecorder.cs:246-251`).
  Clip stop waits at most 8 s (`MeetingScreenRecording.cs:23`).
- **Screen and CoreAudio cleanup (`2cdb7b63`).** Windows' equivalents are the clip stop timeout above and the
  meeting checkpoint, which now runs under the deadline.
- **Verified:** `Muesli.Windows.Tests/ShutdownDeadlineTests.cs` covers:
  - work that finishes in time;
  - work that already finished before the wait began (the "release before park" case);
  - a stuck step abandoned at the deadline and left running;
  - a failed step reported to the caller.

  12 shutdown and coordinator tests pass. The app relaunched and the log is clean.

**Done when:**
- [x] The WinUI shutdown checkpoint (see `7c99376`) is compared against these rules and a deadline test exists.

#### MAC-11 Storage breakdown in General settings. Status: Done (2026-10-31)

**Commits:** `89b12f20`, with video added by `73f2342f`. **macOS files to read:** `LocalStorageUsage.swift`,
`LocalStorageUsageView.swift`.

**Invariants:** [x] a breakdown of models, meeting audio, videos and the database, computed off the UI
thread.

**Done when:** [x] a Settings section exists with a [x] size calculation test.

**Windows build (2026-10-31):**
- **Scanner:** `windows-native/Muesli.Windows.Core/Services/LocalStorageUsage.cs`.
  - `Scan` (:34) is read-only. It never follows reparse points (symlinks or junctions), counts each path once
    across overlapping roots, counts unreadable locations, records drive free space, supports cancellation and
    creates no folders.
  - `CategoryFor` (:86) maps the Windows profile layout:
    - `captures/videos/*.mp4` → video.
    - `captures/recordings|in-progress/` and `captures/meeting-*` → meeting audio.
    - `streaming-models/` → models.
    - `data/muesli.db*` and `data/*.json` → database.
    - `cache/waveforms/` → waveform cache.
    - Everything else → other.
  - `DefaultModelRoots` (:23) is the catalog cache folders (env overrides included), then `~/.cache/muesli`.
- **UI:** a "STORAGE" card in Settings → General (`Muesli.Windows.WinUI/Pages/SettingsPage.xaml:307`), built in
  `Pages/SettingsPage.Storage.cs`.
  - It shows the total, a coloured bar, six category rows (the models row expands per model), "Includes shared
    model caches" with the macOS tooltip, free space, the incomplete-totals note and a refresh button.
  - The scan runs on `Task.Run` and is cancelled on unload.
- Test: `windows-native/Muesli.Windows.Tests/LocalStorageUsageTests.cs` (2 pass).
- Verified 2026-10-31 on the real profile with `winapp ui`: the total is 49.8 GB, mostly downloaded models
  (48.2 GB). The launch log is clean.

**Deviations:** hard links aren't deduplicated by file identity, and sizes are logical lengths rather than allocated
sizes. Windows needs P/Invoke for either; the tooltip mentions NTFS compression.

#### MAC-12 Meeting page and list polish. Status: Done (2026-10-31; live locale refresh is Watch)

**Commits:** `a5ef96d9` `2dd3d4b3` `93c2b25a` `14b92782` `25068680` `3fbfec8e` `bc67cafe` `e14c306d` `0c7ab7f8`,
plus test `aa891157`.

**Invariants:**
- **Long titles.** A long title never widens the page; the title row keeps a fixed height (38).
- **Start times.**
  - Parse each start time once per sort; parsing inside the comparator caused 95 % of main-thread
    time.
  - Cache parsed times across renders.
  - Use one formatter per locale and time zone.
  - Clear formatters on any locale change, including a 12/24 h switch with the same identifier.
  - Cache only offset-bearing strings; local-time strings change meaning after a time-zone change.
  - Redraw rows on a locale change without replacing them; replacing them closed an open
    New Folder prompt.

**Done when:** WinUI meeting list profiling and tests cover these rules.

**Status: Done (2026-10-31; live locale refresh is Watch).**
- [x] **Long titles.** The title editor is now one line at a fixed height of 40
  (`Muesli.Windows.WinUI/Pages/MeetingDetailPage.xaml`, `MeetingTitleEditorStyle`). Before, it wrapped, and a long
  title grew the header by several 26 pt lines. A long title now scrolls inside the box, the Windows version of the
  macOS marquee. It sits in the `*` column (`TitleBlock`, `MinWidth="0"`), so it never widens the page.
  - Checked in the app: a real long title renders at 855×50 px (40 at 125 % scaling) and is cut off at the box's edge.
- [x] **Parse each start time once, and cache parsed times.** N/A by construction. Windows meetings carry a typed
  `DateTime CreatedAt` (`Muesli.Windows.Core/Models/MeetingItem.cs:8`), so sorting compares values
  (`WinUiLibraryContext.cs:310`) and parses no strings.
- [x] **One formatter per locale.** .NET formats with the process culture, which it caches. There are no per-row
  formatter objects.
- [ ] **Clear formatters on a locale change; redraw rows without replacing them.** → Watch. .NET keeps the culture it
  read at startup, so a change to Region settings shows after a relaunch. Applying it live would need a
  `WM_SETTINGCHANGE("intl")` hook plus `CultureInfo.ClearCachedData()` and an in-place row refresh. Low value.
- [x] **Cache only offset-bearing strings.** N/A: timestamps are typed and converted with `ToLocalTime()` when shown,
  so a time-zone change is picked up on the next render.
- **Tests:** `Muesli.Windows.Tests/MeetingTitleLayoutTests.cs` checks the one-line fixed-height `*` column and that
  sorting never parses timestamps.
- **Profiling:** not run. The meeting list has no comparator parsing to profile.

---

### Detection

#### MAC-13 Teams short-form links. Status: Done (2026-10-31)

**Commits:** `0e81d032` `606f9ba7` `daa52934` `395e938e`.

**Invariants:**
- [x] Recognise `teams.microsoft.com/meet/<id>?p=` and `teams.live.com/meet/<id>`.
- [x] The Join link the invite presents wins over the legacy footer. N/A: Windows has no calendar integration. It
  parses one URL at a time (browser address or explicit link), so there's no invite text to scan. Revisit with
  calendar integration.
- [x] Match `teams.live.com` as an exact host, with a `/meet/<id>` path.
- [x] The first character after `/meet/` must not be `?`.
- [x] Reject fragments and empty path segments.

**Windows build (2026-10-31):** `Muesli.Windows.Core/Meetings/MeetingUrlParser.cs`.
- **Before:**
  - `teams.microsoft.com/meet/<id>` didn't parse.
  - Subdomains of `teams.live.com` matched, with any `meet/` path.
  - An operator-precedence slip let `dl/launcher` links through.
- **Teams free:** now matched by exact host only, with a `/meet/<id>` path (:55).
- **Short form:** `ParseTeams` (:96) accepts `meet/<id>` for teams.microsoft.com, plus the existing
  `l/meetup-join/` and desktop-launcher forms.
- **ID check:** `MeetPathId` (:117) reads the ID from the raw path. That rejects `/meet/`, `/meet/?p=`, `/meet/#x` and
  `/meet//x`, which an empty-entry split would otherwise hide.
- **Tests:** `TeamsShortFormLinksAreRecognized` and `TeamsLinksWithoutAMeetingIdAreRejected`
  (`Muesli.Windows.Tests/Phase6DetectionTests.cs:35,54`). They use the macOS cases, plus a look-alike host and a
  subdomain. All 66 detection tests pass.

#### MAC-14 Slack huddle helper attribution. Status: Done (2026-10-31; no live huddle has been run)

**Commits:** `70880f50` `37386261`.

**Invariants:**
- [x] Only Slack helper processes (exact helper or a descendant, case-insensitive) count as Slack audio.
- [x] Reject unrelated dotted children and helper-like prefixes.
- [x] Keep per-process duplex evidence and parent-keyed suppression.

**Windows mapping:** Windows has no bundle IDs. Slack's Electron child processes, including the audio-service utility
process, all run as `slack.exe`. Attribution is an exact, case-insensitive process-name match
(`RefersToSameApp`, `Muesli.Windows.Platform/Services/MeetingPresenceSignals.cs:94`). A look-alike such as
`slackhelper.exe` or `slack-fake.exe` has a different name, so it never matches. That covers the first two
invariants by construction. `slack` is still a dedicated meeting process (:155).

**Windows build (2026-10-31):**
- **Gap fixed:** duplex was tested on the app as a whole: any `slack` process capturing plus any `slack` process
  rendering. Input on one helper and output on another counted as a call. macOS's split case rejects this.
- **Fix:** `HasSameProcessDuplex` (:100) requires one of the app's PIDs to appear in both the capture and the render
  audio sessions. It's used by `Capture` (:91).
- **Suppression:** the key and identity come from the window's main process
  (`MeetingDetectionService.cs:320-322`), not the helper's PID. That's the parent-keyed rule.
- **Tests:** `Duplex_evidence_needs_one_process_of_the_app` (`Muesli.Windows.Tests/MeetingBehaviorParityTests.cs:204`)
  covers same-process, split, input-only, output-only and another app. All 81 detection tests pass.
- **Owed:** a real Slack huddle, to confirm that Slack's audio-service process both captures and renders.

The calendar filter (`8564a0f6`) and its subtitle commits (`20f598ae` `75afc141`): **Excluded** with CAL-01.

---

### Dictation and audio

#### MAC-15 Per-app formatting templates. Status: Done (2026-10-31; the rule card hasn't been checked in the preview profile yet) (P2)

**Commits:** `28adabba` `64fdb1c0`. **macOS files to read:** `DictationAppFormatting.swift`,
`DictationFormattingSettingsView.swift`, `MuesliController+DictationFormatting.swift`, and the
`database-schema.md` entry.

**Invariants:**
- [x] A template chosen by target app is applied at paste.
- [x] Defaults survive a rebase (`64fdb1c0`). Streaming metadata is kept.
- [x] From MAC-17 (`5f1037e4`): live dictation is buffered only when the app active at recording start has an active
  rule (`shouldBufferStreaming`). That mode and the rule snapshots stay fixed for the session; buffered text uses the
  receiving app's rule, and live text is never rewritten after an app switch. Use the macOS settings caption.

**Done when:** [x] model, [x] settings UI, [x] paste hook and [x] tests exist.

**Windows build (2026-10-31):**
- **Model** (`windows-native/Muesli.Windows.Core/Dictation/DictationAppFormatting.cs`):
  - `DictationAppFormatting` (:7): `Apply` removes only a single final ASCII period.
  - `DictationAppFormattingRule` (:39): `Apply`, `ActiveRules`, `ShouldBufferStreaming` (:53).
  - `DictationFormattingTemplate` (:69): built-ins Original, Casual and Lowercase chat.
  - `DictationFormattingSuggestion` (:103).
  - `DictationFormattingEdits` (:132): the macOS controller mutations, with the same error strings.
- **Rule key.** It is the process name, the `AppId` already recorded for each dictation, because Windows has no bundle id.
- **Settings.** Values are the macOS raw strings, so an unknown value can't fail the settings load.
  - `MuesliSettings.DictationAppFormattingRules` / `DictationFormattingTemplates` / `DictationFormattingSetupDismissed`
    are at `Muesli.Windows.Core/Services/SettingsStore.cs:545`.
  - Each entry is normalized on its own (`:253`), so a malformed or unsupported entry is dropped without losing the
    others (macOS `DictationFormattingEntries`).
- **Paste hook** (`Muesli.Windows.WinUI/Services/WinUiDictationContext.cs`):
  - Rules are snapshotted at recording start (:460).
  - A start app with a rule turns off live insertion for that session (:462-470).
  - Text that isn't live is formatted with the receiving app's rule (:636). Live text is never rewritten.
- **Settings UI:**
  - Dictation settings has an "APP FORMATTING" row (`Pages/SettingsPage.xaml:514`) that opens `Pages/DictationFormattingPage.xaml(.cs)`.
  - That page has, per app: template picker, description, preview, Customize style (capitalization, final period,
    Save as Template…) and Remove App.
  - It also has most-used app suggestions from the last 30 days of `dictation-apps.json`, Add App… (an .exe picker),
    My templates, and both macOS captions.
  - Verified 2026-10-31 by navigating there with `winapp ui`: it renders with real usage rows and logs no error.
- Test: `windows-native/Muesli.Windows.Tests/DictationAppFormattingTests.cs`. The targeted tests pass (44).

**Deviations:**
- History stores the formatted text when a rule applied, even if the paste then fails. macOS stores the raw text when
  no paste was dispatched. Windows persists before pasting for data safety.
- Lowercase-chat suggestions use the process names WhatsApp, Telegram, Discord and Signal.

**Watch:**
- The dashboard `DictationFormattingSetupCard` invitation isn't built yet. The settings row carries its copy.
- The rule card and Save-as-Template dialog need a click-through in `scripts/run-winui-preview.ps1`, which avoids
  changing real settings.

#### MAC-16 One clipboard owner. Status: Done (physical paste qualification stays with DIC-02) (P1)

**Commits:** `f5d21107` `784e1a47`. **macOS file to read:** `docs/dictation-paste-pipeline.md`.

**Invariants:**
- One clipboard owner across each paste transaction.
- Losing clipboard ownership is reported separately from a failed paste shortcut. Computer Use uses
  the same distinction.

**Windows build (2026-10-31):**
- **Ownership is the clipboard generation.** It used to be a text comparison. `IClipboardAdapter.Generation`
  (`windows-native/Muesli.Windows.Core/Services/ActiveAppPasteService.cs:259`) is `GetClipboardSequenceNumber`
  in the WinRT adapter (`Muesli.Windows.WinUI/Services/WinRtClipboardAdapter.cs:29`), read right after Muesli's own
  write. Restoring now requires that generation, so another app's identical text is never overwritten.
- **One transaction at a time.** `Transaction` and `_current` are at `ActiveAppPasteService.cs:162`.
  - A newer paste inherits the user's original snapshot only while the older one still owns the clipboard.
    Otherwise it snapshots the new clipboard.
  - A superseded transaction's delayed restore does nothing (`RestoreClipboardSafelyAsync`, `:224`).
  - This fixes overlapping pastes "restoring" Muesli's own earlier text.
- **Dispatch requires ownership.** Ctrl+V is sent only while Muesli still owns what it staged (`:193`).
  Otherwise it throws `ClipboardOwnershipLostException` (`:315`) and nothing is pasted or restored.
- **A failed write is restored.** If the write fails but has already changed the clipboard, the original is put
  back.
- **Ownership loss is reported separately (`784e1a47`).**
  - Dictation reports "Another app changed the clipboard; dictation saved in history" and doesn't copy over the new
    owner's data (`Muesli.Windows.WinUI/Services/WinUiDictationContext.cs:690`). Other paste failures still copy the
    transcript.
  - The Computer Use `paste_text` follows the same rules (`Muesli.Windows.Platform/ComputerUse/WindowsInput.cs:116-125`)
    and returns a Failed step for a lost clipboard (`WindowsDesktop.cs:439`).
- **Scope.** On Windows the clipboard path is only the fallback: Unicode input never touches the clipboard. As on
  macOS, the OS has no atomic compare-and-swap, so a write in the gap between two OS calls can't be ruled out.
- **Verified:** `Muesli.Windows.Tests/ClipboardOwnershipTests.cs` covers:
  - a restore after a paste;
  - overlapping pastes;
  - a later external write, including identical text;
  - ownership lost before dispatch versus a refused shortcut;
  - a failed write.

  55 paste, clipboard and Computer Use tests pass. The app relaunched and the log is clean. DIC-02's physical
  qualification across apps stays with DIC-02.

**Done when:**
- [x] One clipboard owner per paste transaction, with ownership loss reported separately from a failed shortcut, in
  dictation and Computer Use.

#### MAC-17 Partial dictation recovery. Status: Done (the app-scoped buffering half moves to MAC-15) (P1)

**Commits:** `5f1037e4` `9edaca3d`.

**Windows today:** `BufferedDictationRecovery` writes `Dictation Recovery/<id>` (`BufferedDictation.cs:305`).

**Invariants:**
- Streaming buffering is scoped to the app where dictation started.
- History says "recovered" only after the dictation is saved.

**Windows build:**
- **Persistence-gated outcome (`9edaca3d`).**
  - A failed History insert no longer aborts the dictation. It is logged
    (`windows-native/Muesli.Windows.WinUI/Services/WinUiDictationContext.cs:641`) and delivery continues. The
    target app is recorded only for a saved row.
  - Every delivery outcome goes through `DictationPersistenceOutcome.Status`
    (`windows-native/Muesli.Windows.Core/Services/DictationPersistenceOutcome.cs`), with the macOS strings:
    live (`:667`), paste (`:683`), failed paste (`:694`), no target (`:704`).
  - When the text was neither pasted nor saved, it is copied, so nothing is lost.
- **Recovery of the partial (`5f1037e4`).**
  - macOS pastes the buffered partial after a Nemotron streaming runtime failure. Windows dictation has no Nemotron
    streaming runtime.
  - Its live-dictation failure (an incomplete stream) preserves the recording and the partial in
    `Dictation Recovery` and shows "Live transcription could not finish", which matches macOS #677 (MAC-32).
- **Buffering scoped to the starting app (`5f1037e4`).** It depends on per-app formatting rules, which Windows
  doesn't have yet. This moves to MAC-15: buffer only when the app active at recording start has a rule, and keep
  that mode for the session.
- **Verified:** 117 dictation tests run: 102 pass, 15 skipped, none fail. They include
  `Muesli.Windows.Tests/DictationPersistenceOutcomeTests.cs`. The app relaunched and the log is clean.

**Done when:**
- [x] A recovery-to-History flow with a persistence-gated label and a test.

#### MAC-18 Mic follows the system default when the built-in mic disappears. Status: Done (2026-10-31; already true on Windows, now pinned by a test)

**Commits:** `e4d37656` `95816d2e`.

**Insight:** on macOS a closed lid leaves the built-in mic enumerated but silent. The Windows
equivalent is an endpoint that is present but inactive or unplugged. Automatic selection must treat
it as unavailable and refresh on a device-state change.

**Windows build (2026-10-31):** no product change was needed. Each rule already held:
- [x] **Unavailable when inactive.** Automatic selection considers only `DeviceState.Active` capture endpoints
  (`Muesli.Windows.Platform/Services/DefaultCaptureEndpointPolicy.cs:17-29`). A saved Windows default that is present
  but unplugged or disabled isn't in that set, so `ChoosePreferredId` moves on to the best active endpoint.
- [x] **Refresh on a state change.** `IMMNotificationClient.OnDeviceStateChanged` triggers `OnDeviceTopologyChanged`
  (`AudioCaptureService.cs:464`). That drops the prepared client (:483), so the next dictation re-resolves.
  `AudioRouteRecoveryPolicy` (:866) rotates a running recording off its endpoint when it becomes Unplugged,
  Disabled or NotPresent.
- [x] **No CoreAudio event on lid close (the macOS workaround).** N/A: Windows raises endpoint state and default
  changes itself.
- **Tests:** `AutomaticSkipsAnInactiveDefaultAndRotatesOffIt` (`Muesli.Windows.Tests/Phase2DictationTests.cs:261`).
  All 30 dictation-route tests pass.
- **App check:** no app code changed, so the build and launch from MAC-14 still apply. Its log had no errors.

#### MAC-19 Mic engine faults contained and retried. Status: Done (2026-10-31)

**Commits:** `09f318ea` `25d7148e` `9d5755a3` `32eef041`.

**Invariants:**
- [x] Every engine call goes through exception containment.
- [x] Graphs are replaced after a failed native cleanup.
- [x] A replacement mic is promoted only after a successful start (qualifying-audio half: deviation, below).
- [x] A failed start is invalidated before teardown.

**Windows build:**
- `AudioCaptureService.ContainEngineCallAsync` (`windows-native/Muesli.Windows.Platform/Services/AudioCaptureService.cs:790`)
  returns a native fault instead of throwing. Mic `StopRecording`/wait and `Dispose` go through it
  (`AudioCaptureService.cs:758-768`), as do the system source's `StopAsync`/`Dispose`
  (`SystemAudioCaptureService.cs:562-567`). Teardown always finishes and closes the file, so
  `StopAndCleanupUnderGate` and the meeting repair loop (`MeetingCaptureSession.cs:466-495`) can start
  a replacement graph after a failed cleanup.
- Route rotation nulls `_session` before stopping it (`AudioCaptureService.cs:500`,
  `SystemAudioCaptureService.cs:306`).
- A failed start nulls `_session` before `Discard` (`AudioCaptureService.cs:413-425`).
- A repaired channel is marked available only after `StartAsync` returns (`MeetingCaptureSession.cs:470-478`).
- The AVAudioEngine NSException bridge is N/A: .NET COM faults are ordinary catchable exceptions.
- Test: `windows-native/Muesli.Windows.Tests/EngineFaultContainmentTests.cs`.

**Deviation (Watch):** Windows has no overlapping route-aware handoff (`32eef041` candidate graph).
Rotation is stop-then-start inside the capture gate, so there is no concurrent candidate whose
generation or identity could go stale, and no qualifying-audio promotion step. Revisit if an
overlapping handoff is ported from `codex/meeting-mic-health-recovery`.

#### MAC-20 Dictating into Muesli's own windows. Status: Done (2026-10-31; not yet dictated into a Muesli editor)

**Commits:** `c7eed1a4`.

**macOS rule:** Muesli's own text fields are valid dictation targets, and the actual recipient is credited.
macOS used to substitute the last external app.

**Windows build (2026-10-31):**
- **Hotkey:** no change needed. The low-level keyboard hook fires whichever window is in front, and there's no
  "editing text" guard to lift.
- **Paste target:** `ActiveAppPasteService.ResolvePasteTarget`
  (`Muesli.Windows.Core/Services/ActiveAppPasteService.cs:41-50`) used to send text to the start window whenever
  Muesli was in front. Now only Muesli's helper processes (the pill, `Muesli.Windows.Indicator.Wpf`) are skipped (:47).
  Muesli's own app window (notes, Ask Meetings and so on) is a real target.
- **Attribution:** `DictationTargetApps.Capture` (`Muesli.Windows.WinUI/Services/DictationTargetApps.cs:37`) used to
  return null for Muesli's own process. Muesli is now credited when it receives the text, so its per-app formatting
  rule and the app list see the real target.
- **Tests:** `PastesWhereTheUserClickedIncludingMuesliButNeverIntoThePill`
  (`Muesli.Windows.Tests/PasteTargetResolutionTests.cs:42`) covers: clicking into another app, the pill (kept on the
  start target), Muesli's own window (now the target), a closed window, and no foreground window. All 12 paste tests
  pass.
- **Owed:** dictate into the Ask Meetings composer and the notes editor with the real hotkey.
- **Not ported:** macOS's meeting-chat UI simplification in the same commit is MAC-01's scope.

#### MAC-32 Buffered live dictation (#677). Status: Done (gaps closed 2026-10-31; the stopped-cursor pill has not been seen in a live dictation yet) (P1)

**Commits:**
- `ee782f18` `e64cc78d` `d2e843fd` `3db3a449` `37d86737` `62ba9a56` `ac8351ce` `8d93f473` `030e242d` `51819ea7`
- `c117a1e1` `0b2b61b9` `074f56a5` `95996771` `05652627` `9db010b2` `83b6ed7d` `6a0f1c64` `0d2a9a73` `a5b5f6ab`
- `1215f838` `a044cb2a` `b9a1cb40` `5a120827` `9557bc3a` `edc3bdaf` `2f1dd51a` `3b4c8a8c` `8b97a82c` `1f588172`
- `e68a5902` `05b7ea59` `95b6d7b5` `28bee944` `4481c15a` `daf121eb` `3a9ade10` `10a28f8f` `135ae51e` `d3fb1298`
- `92029a36` `ba871125` `2ca15704`

**macOS file to read:** `docs/plans/plan-2026-10-24-buffered-live-dictation.md`.

**Superseded commits:** `5a120827` rolled the feature back to `0eb0f82b`, undoing `0d2a9a73`, `95996771`, `1215f838`, `a044cb2a` and `b9a1cb40`. Only the placeholder fixes (`9557bc3a`) and the word groups (`4481c15a`) came back. Port the tip code.

**Windows today:**
- `BufferedTranscriptionSession.cs` (with continuity), `BufferedDictation.cs`, `LiveDictationTextTarget.cs`,
  `WhisperCppPreviewDecoder.cs`, `UiaLiveDictationTarget.cs`, `WinUiLiveDictation.cs`,
  `TranscriptionEngineArtifactsFilter.cs`.
- Bodhan and Korean handling are present.

**Invariants:**
- **Transcript.**
  - Text comes only from committed groups. No VAD.
  - The pending cap is 45 s.
  - Whisper uses an 8 s window, a 3 s minimum age and two-hypothesis agreement. The last two agreed
    words stay revisable.
  - Previews are trimmed only behind confirmed speech.
- **Recovering alignment.**
  - Revisions to owned context are aligned with a timed LCS (`3a9ade10`), which needs a unique
    overlapping endpoint.
  - Persistent alignment failure switches to 2 s disjoint continuity chunks and keeps the committed
    boundary (`10a28f8f`).
  - Unrecognized rolling audio is kept for bounded recovery (`95b6d7b5`).
  - Anchors whose onset has left the retained audio are retired. A real silence gap is needed before
    nearby context is dropped (`edc3bdaf`).
- **Whisper previews.**
  - A whole preview whose lexical words collapse onto one zero-duration timestamp is deferred.
  - Padded audio is excluded from alignment.
  - Previews don't use temperature retries.
  - Initial words with zero timing are kept (`05b7ea59`).
- **Finishing.**
  - The final text comes from the streaming tail, with no full re-transcription (`3b4c8a8c`).
  - A complete stream is reused only with full WAV coverage, unique ordered word agreement and the
    confidence thresholds. Otherwise run full ASR (`2f1dd51a`).
  - Queued decoding is cancelled when the session closes (`28bee944`).
  - The recovery snapshot is retired at its last valid audio boundary (`d3fb1298`).
- **Bodhan** (`135ae51e` `92029a36`).
  - Shared disjoint ownership.
  - Bounded preview generation that keeps the best non-EOS result.
  - Language spans stay aligned through silence.
  - Displaced models reload from validated cached files only, with no download.
- **Korean.** Word spacing is kept across streaming chunks (`ba871125`).
- **Non-speech cues.** Marked cues are filtered from both live and final text (`8b97a82c`).
- **Cursor ownership.**
  - Every update waits for its acknowledgment (500 ms).
  - Keyboard bursts of 8 graphemes, with word boundaries acknowledged.
  - Async text and caret readbacks are reconciled. No delayed redundant caret writes.
  - Pause during temporary accessibility failures.
  - Codex-style empty placeholders: accept a verified empty decoration only once, with the exact
    payload and caret revalidated. Otherwise fail closed.
  - The final paste happens once, with a leased clipboard that is marked private.
  - One fresh clipboard snapshot is allowed before staging, with every guard revalidated. A staged or
    unacknowledged paste is never retried.
  - Preflight failures are reported separately: target, cancellation, layout, foreground, clipboard.
- **Hands-free.** Finalization stays under explicit user control (`030e242d`).
- **Corrections.** The correction monitor runs after an acknowledged live finalization (`2ca15704`).
- **Indicator.** A stopped cursor update is shown ("Cursor stopped" / "Live text stopped", `4481c15a`).
- **Meetings.** Meeting previews never become durable.
- **Baseline.** `5a120827` rolled back to the `0eb0f82b` baseline and `9557bc3a` re-applied only the
  verified placeholder fixes. When a live regression appears, bisect against a recorded replay first.

**Open gaps:**
- [x] Pill states "Cursor stopped" / "Live text stopped" (`4481c15a`).
  - The reasons and macOS strings (title, compact title, detail) live in
    `windows-native/Muesli.Windows.Core/Dictation/BufferedDictationInsertionStop.cs:37`. `WinUiLiveDictation` sets the
    status to the detail, and a cursor stop can't replace a transcription failure.
  - The host derives the notice from that status only while dictation records, not for meetings
    (`windows-native/Muesli.Windows.WinUI/Services/WinUiIndicatorHost.cs:257`), and sends the detail as the message
    (`:300`). The notice is a structural change (`Muesli.Windows.Core/Services/IndicatorProtocol.cs:78`).
  - The WPF pill replaces the waveform with the yellow 11 pt medium title, with the detail as tooltip and UIA name
    (`windows-native/Muesli.Windows.Indicator.Wpf/IndicatorWindow.xaml.cs:433`). The waveform stops. The width is the
    text width plus 54 (`Muesli.Windows.Core/Services/FloatingIndicatorLayout.cs:215`).
  - Verified: 162 indicator, live-dictation and notice tests pass (`Muesli.Windows.Tests/BufferedDictationInsertionStopTests.cs`);
    the app relaunched and the log is clean. Seeing the pill needs a live dictation that loses the cursor.
- [x] Check the correction monitor after an acknowledged live finalization (`2ca15704`) against
  `DictationCorrectionMonitor.cs`.
  - Already matching: `Pasted` fires only when the final write was acknowledged and only after the cancellation
    re-check (`windows-native/Muesli.Windows.WinUI/Services/WinUiDictationContext.cs:659`). `OnPasted` re-reads the
    setting (`Services/WinUiDictionarySuggestionService.cs:65`).
  - Added: each dictation start raises `Starting` (`WinUiDictationContext.cs:459`), which cancels the previous watch
    (`StopWatching`, `WinUiDictionarySuggestionService.cs:42`). A suggestion already queued to the UI thread is
    dropped once its watch has ended (`:69`). This matches macOS `beginDictationOutput` and the `Task.isCancelled`
    guard.
- [x] Check the timed-LCS revision path (`3a9ade10`) and the clipboard snapshot refresh (`daf121eb`)
  against the Windows session and paste code.
  - `RevisedBoundary` (`windows-native/Muesli.Windows.Core/Dictation/BufferedTranscriptionSession.cs:944`, called at
    `:867`) matches macOS line for line. The macOS regressions are now ported:
    `Muesli.Windows.Tests/BufferedTranscriptionRevisionTests.cs` covers 9 refusal cases and the 4 Daly/daily resume
    cases. 33 buffered-transcription tests pass.
  - `daf121eb` doesn't apply: Windows finishes with acknowledged Unicode keystrokes, not a clipboard paste
    (`Muesli.Windows.Core/Dictation/LiveDictationTextTarget.cs:275`), so there's no clipboard generation to
    re-snapshot. Keystroke failures are already reported one reason at a time (`focus_changed`,
    `keyboard_text_unsupported`, `keyboard_dispatch_failed`).
- [x] Windows deviations stay recorded:
  - append-only keystroke target for fields that UIA can't read back;
  - only Whisper Large Turbo previews are timed;
  - the final write is keystrokes, not a leased clipboard paste, so the clipboard invariants (`daf121eb`, the private
    leased clipboard) apply to the non-live paste path only.

#### MAC-35 Meeting mic capture pipeline hardening (#600/#605/#608). Status: Built (physical Zoom check pending) (P1)

**Commits:** `e8f1d950` `05e87c47` `d4f112c1` `65fc4bc5` `ae3058e8` `ac236510` `a13ce391` `1ee6995e` `7d576378` `d08fd5cf`
`8ca757e5` `40899b33` `f24fc7db` `7df9cc39` `6a8c7cf7` `aa9c76cf`.

**Superseded commits:** the timestamp-splicing handoff (`1ee6995e` and the related parts of `f24fc7db`) was replaced by main-style switching (`aa9c76cf`). Timestamp dedup was removed (`6a8c7cf7`). Port only the final design below.

**Invariants (the WASAPI equivalent of each):**
- **Callbacks do no processing.** Bounded preallocated slots feed one serial worker (`e8f1d950`).
- **Stop.**
  - Drain accepted buffers before finalizing files.
  - Snapshot drain eligibility atomically on the first stop request, so an early UI shutdown can't
    drop queued buffers.
  - Close delivery at the final barrier and on discard.
  - The dictation tail is bounded to its matching recorder stop, independent of ASR.
- **Admission and pause.**
  - Admission is checked per buffer, at the producer, before the asynchronous driver pause.
  - Accepted buffers survive pause and stop.
  - Pause and resume register atomically with the admission closure, drain producer, converter and
    retired-handoff PCM off the UI thread, then fence AEC, rotation and retained recording
    (`7df9cc39`).
- **Handoffs.**
  - Cut once, drain the old prefix, then release bounded replacement audio without overlap.
  - Switching is main-style: ownership moves when the replacement is ready, with no timestamp
    splicing (`aa9c76cf`).
  - Queued replacement delivery is fenced at pause and final stop.
- **Ordering.**
  - Steady-source delivery is FIFO, with no timestamp dedup; overlapping host timestamps never drop
    unique buffers.
  - Clipping happens only at the cross-source cut, and that cut never reverses (`6a8c7cf7`).
- **Converter.** Fractional converter output is drained (`a13ce391`).
- **Native tails.**
  - Freeze the unpaused capture interval at the first Stop. Copy only the pre-stop part of the last
    native buffer. Never accept an unprovable untimed tail (`7d576378`).
  - Live handoff tails use the current unpaused interval (`d08fd5cf`).
  - Buffer recycling is independent of admission. A paused or empty buffer still returns to a live
    queue (`8ca757e5`).
- **Fallback recorders** pass the selected child's admission capability through (`ae3058e8`).
- **Zoom (`05e87c47`).** macOS turned off tap autostart on its aggregate device so Zoom didn't hang
  at start. Windows uses no aggregate device, so the portable rule is: never let system-audio capture
  startup depend on the other app's audio state.

**Windows comparison (2026-10-31).** Before this change, both WASAPI sessions broke two invariants:
- They wrote the WAV file, measured levels and converted live PCM inside `DataAvailable`, on the capture
  thread.
- On stop they disposed the writer right after `StopRecording`. NAudio can still deliver a final packet after
  that call returns, and it was dropped.

**Windows build:**
- **Callbacks do no processing.** `windows-native/Muesli.Windows.Platform/Services/CaptureBufferPump.cs` is the
  port.
  - `Offer` (`:49`) copies each packet into one of 256 preallocated slots and returns. One serial worker
    consumes the packets in FIFO order.
  - Admission is checked per buffer. A stop request refuses later offers, so stale callbacks are rejected.
  - Slots return to the pool whether or not their buffer was consumed.
  - A full queue drops a packet and counts it. The count is recorded on the meeting's capture preparation text.
- **Stop drains; discard closes.** `DrainAsync` (`:66`) consumes every accepted buffer. `Discard` (`:74`) closes
  delivery and drops what's queued. The first request decides, so a discard after the drain has begun can't lose
  queued audio.
- **Microphone** (`AudioCaptureService.cs`):
  - The callback is a single `Offer` (`:717`).
  - Stop (`StopCoreAsync`, `:747`) keeps admission open until NAudio's capture thread has ended
    (`WaitForCaptureThreadAsync`, `:782`, bounded at 2 s). It then drains and only then closes the file.
  - Cancel discards before stopping the device.
  - An unexpected device stop (`:728`) drains and closes the file before repair is told, so the checkpoint reads a
    complete segment.
- **System audio** (`SystemAudioCaptureService.cs`) uses the same structure: `StopAndDisposeAsync` (`:554`) and an
  unexpected stop (`:588`). Endpoint loopback waits for its capture thread; process loopback already awaits its
  loop (`WindowsProcessLoopbackCapture.cs:108`).
- **Handoffs.** Route rotation stops and drains the old segment before it starts the replacement. Each segment is
  its own file, merged in order, so ownership moves without timestamp splicing or overlap, as in main-style
  `aa9c76cf`.
- **Not applicable on Windows:**
  - Pause and resume fencing: Windows meetings have no user pause.
  - Timestamp clipping, untimed native tails and AEC fencing: WASAPI capture here is untimed, file-per-segment
    and has no AEC stage.
  - Fractional converter drain: the converter feeds only the live preview; the final transcript reads the WAV.
- **Zoom.** Process loopback activates without depending on whether the target app is playing audio, so capture
  startup never waits on Zoom.
- **Tests.** `CaptureBufferPumpTests` (8) cover:
  - stop with exact PCM equality through a real WAV file, behind a slow writer, from a reused capture buffer;
  - stale callbacks after stop;
  - discard closing delivery;
  - a discard after stop not dropping audio;
  - overflow keeping accepted buffers exact;
  - oversized packets;
  - a writer fault reported once.

  The full suite passes (1,348, 34 skipped). The app relaunched with a clean log. No real recording was made,
  to keep test data out of the user's library.

**Done when:** a Windows capture-pipeline test matrix covers pause, stop, discard, handoff and stale
callbacks with exact PCM equality, plus a physical check during a Zoom call.
- [x] Test matrix (pause is N/A on Windows; handoff is segment-per-route, covered by drain-before-rotate).
- [ ] Physical check during a Zoom call.

---

### Models

#### MAC-21 Phonon-2.5. Status: Artifact

**Commits:** `6fc98812` `fc3c58c9` `642c2259`, plus test `c672d50b`.

**Invariants:** inference is warmed before readiness is published. It has its own card with Fermion
branding and a CC-BY-4.0 notice. CLI support.

**Artifact audit (2026-10-31): still blocked, nothing to build.**
- **What exists upstream:** `FermionResearch/Phonon-2.5` is a Parakeet-TDT derivative (`parakeet_tdt_five_value`).
  It ships only as an MLX package with a custom low-bit container (`phonon-2.5.bps.tar.zst`, `.fsq`,
  `fermion_container.py`).
- **Community builds:** a Hugging Face search for "phonon-2.5" turns up only MLX, Core ML (`Muesli-HQ/phonon-2.5-coreml`
  and two others), a C port, and English-only WebGPU weights. There is no ONNX or GGUF build that sherpa-onnx or
  LLamaSharp can load.
- **What would unblock it:** dequantize the Fermion container, export the NeMo TDT graph to sherpa-onnx's
  encoder/decoder/joiner ONNX form, then host it under Muesli-HQ with pinned SHA-256 values. That's model-conversion
  work and a hosting decision, not a port.
- **Portable contract, for when it lands:**
  - Readiness is already published only after warm-up: `DictationCoordinator.PreloadModelOnce` (`DictationCoordinator.cs:85`)
    and `NativeParakeetClient` verification run before Ready.
  - The card's branding and CC-BY-4.0 notice go in `ModelFamilyCatalog` and `THIRD-PARTY-NOTICES.md` alongside the
    artifact.
  - CLI support follows MAC-29.

#### MAC-22 Parakeet Redux. Status: Artifact

**Commits:** `6fb831c4` `8d06028b`.

**Invariants:** requires macOS 15 (a CPU encoder avoids ANE specialization failures). Caches and
deletion are isolated. Attribution: Moondream, NVIDIA, FluidInference (from `1b9decc4`).

**Artifact audit (2026-10-31): a runnable candidate exists; the hosting decision is yours.**
- **Candidate:** `Codyfederer/sherpa-onnx-nemo-parakeet-redux` is a single
  `sherpa-onnx-nemo-parakeet-redux.tar.bz2` (169,113,813 bytes, 407 MB extracted, CC-BY-4.0, updated 2026-09-25).
  It's in sherpa-onnx's `nemo_transducer` layout, the same format and interface as the shipping
  `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`.
  - The encoder is eschmidbauer's ONNX export, with ternary weights as `MatMulNBits` 4-bit blocks.
  - The decoder and joiner are split from `decoder_joint`.
  - The author reports en/de/es/fr test clips match the int8 Parakeet TDT exactly on sherpa-onnx 1.13.8.
  - It fits `TranscriptionModelCatalog`'s pinned-archive download unchanged, at about a quarter of v3-int8's size.
- **Why I didn't add it:** it comes from an individual account with 0 downloads. Shipping it in the catalog trusts
  that account's conversion. Muesli's rule for its own conversions is Muesli-HQ hosting (MAC-26).
- **Options:**
  - (a) Mirror the archive to Muesli-HQ, pin its SHA-256 and the per-file hashes, run the CPU qualification, then
    add a catalog entry with Moondream / NVIDIA / eschmidbauer attribution in `THIRD-PARTY-NOTICES.md`.
  - (b) Wait for an official sherpa-onnx release.
- **The macOS invariants on Windows:**
  - The macOS 15 / ANE gate doesn't apply; this is CPU ONNX Runtime with the `MatMulNBits` contrib op. Bundled
    runtime support for that op hasn't been checked.
  - Cache and deletion isolation come for free from the per-model `DirectoryName` and gate (MAC-28).

#### MAC-23 Bodhan tiers named Pro and Lite; transfer progress. Status: Done (2026-10-31)

**Commits:** `9136ebca`.

**Invariants:**
- [x] Rename the INT8/FP16 tiers of Bodhan Core/Flex to Lite/Pro in the UI only; stored model IDs don't change.
- [x] Progress shows phase, bytes, speed and time left (`ModelDownloadDisplayFormatting`). File and files
  remaining → deviation (below).

**Windows build (2026-10-31):**
- **Names:** `windows-native/Muesli.Windows.Core/Services/TranscriptionModelCatalog.cs:289,308,327,346`
  ("Bodhan Core Lite/Pro", "Bodhan Flex Lite/Pro"). IDs `bodhan-{core,flex}-{int8,fp16}` are unchanged.
  The CUDA error now says "The Lite variant runs on any PC" (`Muesli.Windows.Core/Services/NativeOfflineAsrClient.cs:428`).
  `docs/WINDOWS_TRANSCRIPTION_MODELS.md:33-34` updated.
- **Progress:** `Muesli.Windows.Core/Services/ModelDownloadDisplayFormatting.cs`:
  - `ModelDownloadDisplayFormatting` (:4) ports `bytes`, `eta` (one-year cap) and `rate`.
  - `ModelTransferMeter.Describe` (:43) ports the speed from `ModelDownloadCoordinator.emit`: the average
    since the transfer started, excluding resumed bytes. Time left covers the transfer only; extraction and
    verification show their phase text alone.
  - Used by the Models page (`Muesli.Windows.WinUI/ViewModels/ModelsPageViewModel.cs:208`), the cleanup and
    CUDA-pack downloads (`ModelsPageViewModel.ExecutionProvider.cs:150,393`) and onboarding
    (`OnboardingViewModel.cs:490`).
- **Verified:** the Models page shows "Bodhan Core Lite" and "Bodhan Flex Lite" (`winapp ui search`).
  Tests: `Muesli.Windows.Tests/ModelDownloadDisplayFormattingTests.cs`.
- **Deviations:**
  - Each Windows model is one archive, so there is no per-file name or files-remaining count to show.
  - The variant picker lists full names ("Bodhan Core Lite") rather than macOS's bare "Lite"/"Pro".
  - The dictation indicator doesn't show download progress, because Windows never downloads a model on
    first dictation; it fails closed and points to Models (`Muesli.Windows.WinUI/Services/WinUiDictationContext.cs:441-446`).

#### MAC-24 Language pickers. Status: Done (2026-10-31; the Nemotron locale picker is a Watch item, below)

**Commits:** `4f97a3d1` `09c3203b` `ede5701f` `f1c723f0` `0c130df8`, plus branch `e5e72999`.

**Invariants:**
- [x] **Whisper.** All 100 token codes. Cantonese only on large-v3. Ukrainian `uk` and Norwegian `no`.
  `.en` checkpoints ignore the setting.
- [ ] **Nemotron.** 32 locales that work without adaptation. Ukrainian prompt id 19. Existing codes and
  prompt IDs are kept. → Watch (below).
- [x] **Decoding.** Skip special tokens. Strip any `<|...|>` token from each segment, and drop segments
  that end up empty.
- [x] **Filler words.** "er" is not a filler (it is Norwegian and Danish for "is"); "err" still is.
- [x] **Unknown values.** A saved code that isn't supported must not silently become auto-detect.

**Windows build (2026-10-31):**
- **Whisper** (`windows-native/Muesli.Windows.Core/Services/ModelLanguageSupport.cs`):
  - `WhisperMultilingual` (:27) is Automatic plus the 100 macOS `WhisperKitLanguage` codes and labels.
  - `HasCantoneseToken` (:210) keeps `yue` only on the large-v3 model. The catalog's only large Whisper is
    large-v3-turbo.
  - `.en` models still offer English only.
- **Unknown values:**
  - Settings keep an unsupported saved code instead of rewriting it (`Muesli.Windows.Core/Services/SettingsStore.cs:358`).
  - `IsSupported` is at (:189). `LabelFor` (:193) names the code as "not supported by this model; using …".
  - At runtime the model default is used, so a removed or future code isn't silently rewritten.
- **Decoding:**
  - `TranscriptionEngineArtifactsFilter.Apply(TranscriptionResult)` (`Core/Dictation/TranscriptionEngineArtifactsFilter.cs:84`)
    cleans each segment and drops empty ones.
  - Meeting finalization applies it to the mic and system results (`Core/Services/MeetingFinalizationPipeline.cs:132`).
  - The `<|...|>` strip was already in place.
- **Filler words:** "er" is removed from `FillerWordFilter` (`Core/Dictation/FillerWordFilter.cs:19`); "err" stays.
- **Tests:** `windows-native/Muesli.Windows.Tests/ModelLanguageSupportTests.cs` has two new cases and the
  persisted-selection test updated to the new rule. 439 language/filler/settings/meeting tests pass.

**Watch:**
- The Windows streaming Nemotron 3.5 runs through the sherpa-onnx online transducer with automatic language
  detection. Its catalog copy already says "19 transcription-ready locales plus 13 broad-coverage locales".
- The current recognizer config exposes no locale or prompt-id field, so the 32-locale picker (Ukrainian prompt 19)
  waits on runtime support. It must not be faked with a picker that does nothing.
- The Models page picker shows the effective option; the unsupported saved code appears only in the label text.

#### MAC-25 Cleanup models. Status: Done (2026-10-31; no v12 model has been run on this PC yet, see Owed)

**Commits:** `10083e15` `26abac95` `2e313720` `df2f1158` `bff10e64` `754422cf`, plus follow-ups
`c4e50b33` `0511dc2c` `52d39af4` `3eee179a` `6e9bfb99` `7df6eb84` `71f63821` `cd1fdf18` `be104e4f` `31b80f33`
`e2dd5340` `5560e689` `ba646471` `861201bf` `d1385719` `e4107c93` `27722cac`.

**Superseded commits:**
- `c4e50b33`, `7df6eb84`, `cd1fdf18` and `0e56543d` targeted the LiteRT runtime, which MLX has since replaced (MAC-34). Only their policies carry over.
- The "Pro Cleanup beta" wording was removed in `754422cf`.
- The delete cards for retired models in the picker (`71f63821`, `861201bf`) were removed in `d1385719`. Deletion moved to a Storage follow-up that macOS hasn't built yet.

**Invariants:**
- **Preparation.**
  - Prepare cleanup before recording without blocking dictation; optional load failures are
    tolerated.
  - Use cached availability while preparing a recording. Show model setup once if cleanup is enabled
    but unavailable.
  - Warm cleanup during dictation.
- **Filtering.**
  - Reject an interrupted GGUF (check the header) and output that hit the cap.
  - Dictated lines that look like model output are kept when output is filtered.
- **Menus.** Tray menu: enabling cleanup opens the model picker at once. Choices are hidden while
  cleanup is off, and models that aren't downloaded are hidden. Activation labels are unified.
- **Migration.** Older Lite choices are retired by migrating them to the unversioned "Cleanup Lite".
  A retired model's cache can be deleted from Storage only.
- **Metadata.** Release metadata lives in one place.
- **Hashing.** Downloads aren't hashed again on the UI thread (the coordinator verifies SHA-256).
- **Privacy.** Diagnostics keep timings but no prompt or output content.

**Checklist:** [x] Preparation · [x] Filtering · [x] Menus (no Windows tray items; see below) · [x] Migration ·
[x] Metadata · [x] Hashing · [x] Privacy.

**Windows build (2026-10-31):**
- **Catalog: the macOS 2f10b23d models** (`windows-native/Muesli.Windows.Core/Services/CleanupModelCatalog.cs`).
  Windows still listed the retired v3 "Muesli Cleanup" from a personal repo. It now has:
  - "Muesli Cleanup Lite" (`qwen35-postproc-v4`, the default, :49).
  - "Muesli Pro Cleanup — LFM 2.5" and "Muesli Pro Cleanup — Qwen 3.5".
  - S1-mini and Qwen Basic, unchanged.

  The new entries are the Muesli-HQ v12 GGUF builds from macOS `ManagedCleanupModel`, pinned to the same commits
  with the same sizes and SHA-256. All three URLs answered 200 with matching `X-Linked-Size` and ETag. Each
  definition carries its prompt contract (metadata in one place).
- **Migration:** `Normalize` (:150) maps the retired v3 and v2 Lite ids to Cleanup Lite. Their files stay on
  disk, unused and never picked up as a placed model (:199); they can be deleted from Storage (MAC-11).
  A user with v3 downloaded sees "Needs model" until Lite is downloaded, the same as macOS.
- **v12 prompt and guard:** `Muesli.Windows.Core/Services/MuesliCleanupV2Prompt.cs` is a byte-identical port of
  `MuesliCleanupV2Prompt`: the system prompt, the ChatML request with Qwen's empty think block, and LFM's
  `<|startoftext|>`, skipped when llama.cpp already adds BOS (`Vocab.ShouldAddBOS`). It also ports
  `cleanOutput`, which strips leaked `model`/APP-CONTEXT/prompt paragraphs but keeps a dictated leading "model",
  and `guardResult`. `NativeTextCleanupService.RunCleanupPromptAsync` uses it for v12 models (:303). Output
  that ran out of budget (:330), comes back empty, or is more than 3× the input keeps the dictation; the
  existing transcript safety check still runs after.
- **Interrupted GGUF:** `HasGgufHeader` (:174) checks the magic, version and minimum length for the selected
  and placed files. Catalog downloads were already atomic (temp file, SHA-256, then move), and hashing runs in
  `PrepareAsync` off the UI thread.
- **Preparation:**
  - The readiness probe opened the model's vocabulary on every call. It is now cached per file
    (`CanOpenModelVocabularyCached`, :74).
  - `WarmInBackground` (:341) loads the model while the user speaks
    (`Muesli.Windows.WinUI/Services/WinUiDictationContext.cs:462`). It never blocks dictation, and failures
    are tolerated.
- **Privacy:** the rejection log carries a token count and completion flag only (:333).
- **Menus:** the Windows tray has no cleanup items. Cleanup is turned on in Settings and models are picked
  on Models → Cleanup, which already shows the model picker. No tray port is needed.
- **Tests:** `Muesli.Windows.Tests/MuesliCleanupV2PromptTests.cs`; catalog tests updated in
  `ExecutionProviderTests.cs` (five models, the v12 contracts, header rejection, the retired file ignored).
  185 tests pass. The Models → Cleanup page lists Lite and both Pro models (`winapp ui search`).
- **Owed:** download one v12 model and dictate through it. This is a 0.5–1.3 GB download into the user's real
  cache; no cleanup model is installed here.
- **Deviations:**
  - LFM's `LICENSE` sidecar isn't downloaded (single-file catalog); the license is named on the model entry.
  - Completion is detected from the end-of-turn token or a generation shorter than the budget.
    `ponytail:` LLamaSharp yields text pieces, not tokens, so a multi-byte-heavy turn could read as complete.
    The 3× length guard and the transcript safety check still apply.

#### MAC-26 Model downloads from Muesli-HQ on Hugging Face. Status: Done (2026-10-31; Bodhan ONNX hosting is Watch)

**Commits:** `754422cf`.

**Invariants:** saved selections and cached artifacts survive the rename of the repository owner.

**Windows audit (2026-10-31):** no code change was needed.
- **No old-owner URLs:** a `git grep` of every Hugging Face URL in `windows-native/` and `scripts/` finds no `phequals/`
  repository. The only `phequals` string is the donate link, which isn't a model.
- **Muesli's own cleanup models** already download from Muesli-HQ, pinned to the macOS revisions
  (`Muesli.Windows.Core/Services/CleanupModelCatalog.cs:76,89,102`).
- **Third-party cleanup models:** S1-mini (`superwhisper/`, :115) and Qwen Basic (`unsloth/`, :128) stay with their
  upstream owners, as on macOS.
- **Sherpa models:** these ASR models come from k2-fsa GitHub releases, not Hugging Face.
- [x] **Selections and caches survive a rename.** Windows keys model IDs and cache directories by model, never by
  repository owner (`TranscriptionModelCatalog` `DirectoryName`, `CleanupModelCatalog` file names). Changing a URL
  never orphans a download or a saved selection.
- **Watch:** the Bodhan ONNX ports are only on the `Mvkd2k06/indic-transcribe-{core,flex}-onnx` repositories
  (`TranscriptionModelCatalog.cs`), which return 200. `Muesli-HQ/indic-transcribe-*-onnx` returns 401, so it hasn't
  been published. Moving them is a hosting decision; when they move, change only the URL. The archive SHA-256 values
  stay the same.

#### MAC-27 Quill. Status: Done (2026-10-31; the Gemma direct-audio path is Watch; no local rewrite has been run yet)

**Commits:** `9ff74ea3` `02d8e594` `99ddc520` `ea9973d5` `79cb708d` `861201bf`.

**Invariants:**
- [ ] Gemma receives audio directly; the transcript preview is separate. → Watch (below).
- [ ] Long instructions fall back to the full ASR text. → Watch (Gemma route only).
- [x] Quill is gated on its local models, not on hosted dictation credentials.
- [ ] Preview model loading is local-only. → Watch (Gemma route only).
- [x] Quill readiness doesn't depend on dictation ASR readiness. On the text route, both platforms transcribe the
  instruction with dictation ASR; this invariant concerns the Gemma audio route.
- [x] The English-only fallback is an accepted limitation.

**Windows build (2026-10-31):** Windows Quill only rewrote with ChatGPT and refused to turn on without a
sign-in. macOS defaults to an on-device writing model (`quil_backend = local`, Qwen 3.5 0.8B General).
- **Backend setting:** `QuillBackends` (`windows-native/Muesli.Windows.Core/Services/QuillTransformation.cs:7`) and
  `MuesliSettings.QuillBackend`, default local (`Muesli.Windows.Core/Services/SettingsStore.cs:517`). Unknown values,
  including macOS's Gemma backend, normalize to local (:212), as macOS `resolved` does. Only Qwen Basic follows Quill
  instructions (macOS `supportsQuil`), and the local selection limit is 2,500 characters (:23, macOS
  `localMaximumInputCharacters`).
- **Local generation:** `NativeTextCleanupService.GenerateAsync` (`NativeTextCleanupService.cs:344`) runs one ChatML
  turn with thinking suppressed (`QuillPrompt`, :341) on a 4,096-token context. Nothing leaves the PC. It goes through
  the existing `QuillTransformation` validation and corrective retry.
- **Gating:** `WinUiQuillContext.Unavailable` (`Muesli.Windows.WinUI/Services/WinUiQuillContext.cs:49`). Local needs
  the general model downloaded; ChatGPT needs a sign-in. It is checked at start and when Quill is turned on
  (`ViewModels/ShortcutsPageViewModel.cs:153`), with the macOS messages. The run picks the backend at :137.
- **UI:** a "Writing model" picker on the Quill row (`Pages/ShortcutsPage.xaml:208`, `SetQuillBackend` :110).
  Verified in the app: the picker is shown, and the status reads "Quill's on-device model isn't downloaded…" on this
  profile, where Quill is off and Qwen Basic isn't installed.
- **Tests:** `Muesli.Windows.Tests/QuillBackendTests.cs`. 81 Quill, shortcut, settings and cleanup tests pass.
- **Owed:** download Qwen Basic (533 MB) and run one local rewrite.
- **Watch (Gemma direct audio):** macOS feeds the recording to Gemma 4 through LiteRT-LM, with a separate ASR preview
  and a full-ASR fallback for long audio. Windows has no LiteRT or Gemma audio runtime, so this route waits on one. The
  text route covers Quill in the meantime.

#### MAC-28 Model preparation and downloads. Status: Done (2026-10-31; no real parallel download has been run)

**Commits:** `1c9e84b8` `0d70f8d1` `31212b63` `6703bec0`, plus test `b843a3ad`.

**Invariants:**
- [x] Library preparation is serialized while downloads run in parallel. Different models now download in parallel.
  Windows "preparation" is a download plus a SHA-256 check, with no memory-heavy load. The only model load is the
  one-time launch preload (`Muesli.Windows.Platform/Services/DictationCoordinator.cs:85`). Recognizer loads stay behind
  each client's gate.
- [x] Preparation for the same model is shared.
- [x] A superseded preparation is cancelled. N/A by design: Windows preloads only once, at launch, and doesn't
  re-prepare when the dictation model changes. A new model loads on first use, so nothing is left to supersede.
- [x] A cancelled download is drained before a retry.

**Windows build (2026-10-31):**
- **Parallel downloads:** before this change, one static gate serialized every Whisper, SenseVoice, Qwen3, Cohere and
  Bodhan download. Now each model has its own gate (`Muesli.Windows.Core/Services/NativeOfflineAsrClient.cs:19-23`),
  matching macOS's per-model `jobKey`. The per-model cross-process file lock already handles other processes.
  Parakeet keeps its own gate.
- **Fix needed for parallel downloads:** `ModelSetupArtifactCleaner.Owns` (`ModelSetupArtifactCleaner.cs:49`) now
  requires a dot-free token between the prefix and the suffix. Before, cleaning up `sherpa-onnx-whisper-tiny` matched
  and deleted the in-flight `.sherpa-onnx-whisper-tiny.en.{guid}.download`. That was already a risk across processes.
- **Shared preparation:** `TranscriptionModelLifecycleService.PrepareAsync` (`TranscriptionModelLifecycleService.cs:212`).
  A second caller, such as onboarding and then the Models page, joins the running preparation instead of failing with
  "already running". The joiner's cancellation only ends its own wait; the row's Cancel button still cancels the job.
- **Drain before retry:** the lifecycle clears an operation only after the awaited job has finished its `finally`
  cleanup (:350-376). Each client releases its gate only after deleting its archive and staging files
  (`NativeParakeetClient.cs:805-825`, `NativeOfflineAsrClient.cs:690-701`).
- **Tests:** `SecondPrepareJoinsTheRunningOneAndItsCancellationOnlyStopsItsWait`
  (`Muesli.Windows.Tests/TranscriptionModelPlatformTests.cs:222`), `CleanupLeavesASiblingModelsInFlightArtifacts`
  (`ModelAndSingleInstanceTests.cs:95`), and the existing cancel-and-retry test. All 29 pass.
- **Not checked:** progress isn't forwarded to a joining caller (the row status updates through `ModelChanged`).
  Two real downloads haven't been run side by side.

#### MAC-29 CLI parity. Status: Missing (built last; design a Windows CLI first)

**Commits:** `ba2c1d01` `b9cb9ab0` `591142fd`.

**Invariants:**
- Help and model lists come from the app catalog.
- Silent input never produces a saved meeting.
- Platform errors are mapped to clear messages.
- Cleaned segment timing and artifact-only output are verified.

**Blocked on design (2026-10-31):** per the user's decision, the Windows CLI's shape comes before code. Open choices:
- A separate `muesli.exe` console app, or a console subsystem in the packaged app. An MSIX app execution alias
  matters here.
- Whether it shares the running app's profile lock.
- Which commands to include first. macOS has transcribe-file, model list and help.

The existing building blocks (`TranscriptionModelCatalog`, `NativeTranscriptionClient`, `TranscriptionEngineArtifactsFilter`)
already cover the help/model-list, silent-input and artifact rules once a shape is picked.

#### MAC-34 Gemma on a native MLX runtime (LiteRT removed). Status: N/A-mac (2026-10-31; the portable invariants travel with the Gemma Watch under MAC-27)

**Commits:** `da8687f4` `39b0237b` `f9e97eff` `c71af540` `7410d086` `4dd22295` `38685a36` `30273e35` `0e56543d` `6608be75`
`23ca0b63`, plus docs `1ae3794f` `a57b21c6`.

**Portable invariants:**
- One shared policy for MTP drafting, on by default. Fall back to target decoding when the drafter is
  unavailable.
- Recording readiness is invalidated when shared engines unload.
- Blank legacy overrides are normalized, and an override never blocks deleting a managed model.
- Licence notices ship with the runtime.

**Disposition (2026-10-31):**
- **The runtime is Apple-only.** MLX-Swift on Apple Silicon replaced LiteRT on macOS, and Windows has neither.
- **No Windows Gemma path exists.** The MAC-27 Quill row already records the Gemma direct-audio route as Watch,
  waiting on a Windows Gemma runtime or artifact. Every portable invariant here concerns that engine, so they apply
  when that Watch is picked up:
  - drafting policy and fallback;
  - recording readiness invalidated when the engine unloads;
  - override normalization and deletion;
  - shipping licence notices.
- **Override check:** Windows has no model-path override setting today (`git grep` finds no cleanup or ASR path
  override), so "an override blocks deletion" can't arise.

#### MAC-36 Model OS compatibility. Status: Done (2026-10-31; selection repair is a deliberate deviation; not clicked on a CUDA PC)

**Commits:** `1b9decc4` `4f68bb02`.

**Invariants:**
- [x] Unsupported models are hidden from inference choices. On Windows the "Set active" action is hidden instead; the
  row stays visible so you can download it or find out about it.
- [ ] Restored dictation and meeting selections are repaired to a compatible installed model, and the repaired config is
  persisted. **Deviation:** not ported (below).
- [x] Activating an incompatible model directly is rejected.
- [x] Old caches stay deletable. Delete stays available for any model that isn't selected. Windows has no separate
  cleanup section because no rows are hidden.
- [x] Fallbacks are filtered by support. The only Windows fallback is Parakeet under memory pressure
  (`WinUiDictationContext.cs:510`), and Parakeet is supported on every PC.

**Windows mapping:** the requirement is hardware (CPU, Vulkan, RAM), not the OS version.

**Windows build (2026-10-31):**
- **Which models:** the only hardware-gated Windows model is Bodhan Pro (FP16), which runs on CUDA only. Every other
  catalog model runs on the CPU.
- **Support check:** `TranscriptionModelCatalog.IsSupportedOnThisPc`
  (`Muesli.Windows.Core/Services/TranscriptionModelCatalog.cs:386`) asks the same provider policy that
  `EnsureBodhan` uses (`NativeOfflineAsrClient.cs:432`), so the check and the runtime can't disagree. The message
  is shared (:390).
- **Hidden action:** `CanSetActive` hides "Set active" on rows that aren't supported
  (`Muesli.Windows.WinUI/ViewModels/ModelsPageViewModel.cs:682`).
- **Rejected activation:** `WinUiModelsContext.SetActive` rejects an unsupported model
  (`Muesli.Windows.WinUI/Services/WinUiModelsContext.cs:45`), and the page shows the reason (`ModelsPageViewModel.cs:147`).
- **Why selection repair isn't ported:** on macOS, support follows a fixed OS version. On Windows it follows the CUDA
  pack and the GPU setting, which the user can switch off for a while. Persisting a repair would silently and
  permanently replace the user's chosen model. Instead, a Pro selection without CUDA fails closed with the Lite
  guidance, per the product rule.
- **Tests:** `Muesli.Windows.Tests/ModelHardwareSupportTests.cs`. 22 lifecycle, Bodhan and support tests pass.
- **App check:** relaunched and opened the Models page. The log has no errors.

---

### Shell

#### MAC-30 Appearance follows the system. Status: Done (2026-10-31; the toggle wasn't clicked on the real profile)

**Commits:** `54e12876`.

**macOS rule:** a three-way Light / Follow System / Dark appearance setting is shared by Settings and the sidebar.
Following the system is opt-in, so older configs keep their explicit theme.

**Windows build (2026-10-31):**
- [x] **Setting:** `MuesliSettings.Theme` holds `system`, `light` or `dark`. Older files default to `dark`
  (`SettingsStore.cs:660`), matching macOS's opt-in. `App.ResolveTheme` maps `system` to `ElementTheme.Default`
  (`Muesli.Windows.WinUI/App.xaml.cs:539`). `TrackTheme` applies it to every window root and re-applies it when
  settings change (:553). Settings → Appearance already offered "Use Windows setting / Light / Dark".
- [x] **Sidebar gap:** the sidebar toggle offered only Light and Dark, and highlighted the resolved theme, so Follow
  System could never appear selected. It now has the macOS order, Light / Follow System / Dark
  (`MainPage.xaml:359`, `SidebarSystemMode`). It highlights the saved choice (`MainPage.xaml.cs:779`) and refreshes
  on every settings change through `RefreshNavigationVisuals`.
- **Tests:** `Muesli.Windows.Tests/AppearanceToggleTests.cs` checks the three tags in order. 11 theme tests pass.
- **App check:** relaunched; the "Follow System" button is present in the sidebar and the log has no errors. I didn't
  click it, to leave the real profile's theme alone.
- **Known limit:** dictation-target app icons pick their light or dark variant when first drawn
  (`Pages/ComputerUseRowStyle.cs:40`, existing `ponytail:`). After a live Windows theme switch they update once the
  rows rebuild.

#### MAC-31 Indicator: transcript on hover, status routing. Status: Done (2026-10-31; hover DPI review still owed under FLOAT-02); notch: N/A-mac

**Commits:** `f2f3a17e` `49340eb0` `e211052b` `3168aab5` `b55523b7` `c8230769` `9d0ad706` `1e94aefc`.

**Portable invariants:**
- [x] Hovering the indicator shows the live meeting transcript.
- [x] Status feedback goes to whichever indicator style is selected.
- [x] Disclosure is kept, with a fallback when a display has no notch-style surface.
- [x] Background callbacks never collapse the active indicator.
- [x] Notices release loading ownership. Feedback configuration is cached.

**Windows build (2026-10-31):**
- **Hover transcript:** already present. `HoverEnter` from the companion opens the live transcript beside the pill
  during a meeting (`windows-native/Muesli.Windows.WinUI/Services/WinUiIndicatorHost.cs:400`). It is gated by
  the "live transcript on hover" setting.
- **Routing, disclosure, display fallback:** Windows has one indicator surface, the floating pill companion,
  and no notch. All status goes to it, and recording disclosure is always that pill. The notch commits
  (`f2f3a17e` `49340eb0` `e211052b` `1e94aefc` and the notch half of `b55523b7` `c8230769`) are N/A-mac.
- **Ownership:** this holds by construction. `UpdateState` (:147) recomputes state from the live meeting,
  dictation, computer-use and Quill runtimes on every update, with active work taking precedence over outcomes.
  There is no separate loading owner, so a late background callback can't reset an active pill, and a notice
  has nothing to release.
- **Feedback configuration cache (`9d0ad706`):** this was missing. Every update (several a second while a meeting
  runs) read and normalized the settings file three times. `FeedbackSettings()` (:134) now caches it, keyed on
  the settings file's write time so a save from any settings context is seen. The drag-position save still
  reloads first.
- **Test:** `Muesli.Windows.Tests/IndicatorFeedbackSettingsTests.cs`. 133 indicator tests pass.

#### MAC-37 Settings and UI refresh performance. Status: Done (2026-10-31)

**Commits:** `91be7833` `ab4426fe` `f240702c` `03574367`.

**Invariants:**
- [x] A refresh publishes the meeting transcription selection and config only when they change. Bound
  state is written only when it changes. This is #632: a Settings appear-handler feedback loop grew
  memory past 6 GB.
- [x] The sidebar doesn't build the full settings catalog. Windows has no settings catalog or search index; the rail
  is static navigation.
- [x] A UI refresh publishes app state once, with no redundant history sync. Windows has no feedback path:
  `LoadAsync` runs once per page `Loaded` (`Pages/SettingsPage.xaml.cs:45`) and isn't triggered by PropertyChanged.

**Done when:** a test checks that an unchanged refresh raises no PropertyChanged.

**Windows build (2026-10-31):**
- **Gap found:** `SettingsPageViewModel.LoadAsync` rebuilt every bound list as a new instance. That raised
  PropertyChanged and reset ComboBox ItemsSource even when nothing had changed. It also notified the preset names and
  the five section flags without condition, though the section flags depend only on `SectionIndex`.
- **Lists:** `BoundLists.Same` (`Muesli.Windows.Core/Services/BoundLists.cs`) keeps the current instance when the
  sequence is equal, so `SetProperty` does nothing. It covers the templates, output limits, indicator positions,
  microphones, models and live models (`Muesli.Windows.WinUI/ViewModels/SettingsPageViewModel.cs:349-393`), plus
  computer-use efforts (:610) and permissions (:697).
- **Preset names:** they notify only when they change (:365).
- **Section flags:** the redundant `NotifySections()` call was removed from load (:418). It still runs from
  `OnSectionIndexChanged`.
- **Unchanged:** `ReapplyPickerSelections` and `RefreshPermissionPresentation` still re-push on purpose, for a theme
  switch or a hidden tab.
- **Tests:** `Muesli.Windows.Tests/BoundListsTests.cs` checks that an unchanged refresh raises nothing and a change
  raises once, using SetProperty's equality rule. 34 settings tests pass.
- **App check:** relaunched and opened Settings. The microphone group renders and the log has no errors.

#### MAC-38 Claude Code summarizer results. Status: Missing (built last; Windows has no Claude Code CLI provider)

**Commits:** `189b7d4f`.

**Invariants:**
- Accept the final result either as a JSON object or as a verbose message array.
- Show structured error details, with privacy-safe support codes.
- Tell process failures apart from format failures.
- On re-summarize, keep backend errors as they are; don't relabel them as persistence failures.

Applies when a Claude Code provider is added.

**Audit (2026-10-31):** the one rule that doesn't depend on a provider already holds.
- [x] **Re-summarize keeps backend errors as they are.** `MeetingDetailViewModel.GenerateSummaryAsync`
  (`Muesli.Windows.WinUI/ViewModels/MeetingDetailViewModel.cs:435-451`) shows the provider's own message ("Could not
  generate notes: …"). A local fallback names its safe reason. Only a meeting deleted meanwhile becomes the persistence
  error, "The meeting no longer exists" (`WinUiMeetingDetailContext.cs:91`).
- **Remaining rules:** the JSON-object or verbose-array result, structured error details and process-versus-format
  failures all belong to the Claude Code CLI provider. Windows has no such provider, so they wait on your decision to
  add one.

#### MAC-33 Cotypist typeahead (#682). Status: Partial (all slices built; signing and the physical typing check open) (P0 #5). Experimental and dev-builds-only on macOS (`SettingsView.swift:2010`, `MUESLI_INCLUDE_COTYPIST`)

**Windows:** ship it behind an equivalent build switch until it is qualified. The JSONL journal and the helper debug log were dropped on macOS, so port neither.

**Commits:** `6a3c2eac` `a2d78077` `75bb74db` `8a3c5970` `527b94b8` `bc72692b` `4d7ed2de` `77eca619` `d4dc3d40` `c3422be6`
`82c101c7` `1ecb3817` `389fd90a` `7adad267` `2214382c` `989eb616` `1b8cb96f` `e86c3234` `1e47160f` `ea4a23cb` `592020b7`
`e533bace` `8a0d2d20` `2f10b23d`.

**Windows mapping:**
- The macOS IMK input-method helper becomes a TSF text service: an out-of-process COM DLL that
  `ctfmon` loads.
- The Unix socket becomes a named pipe with an ACL for the same user, plus verification of the
  peer's signature or package identity.
- The model is GGUF through LLamaSharp. Qwen 3.5 base GGUF is the artifact.

**Invariants:**
- **Helper.**
  - Owns keys, ghost text and insertion. Needs no permissions.
  - Never blocks a keystroke on Muesli.
  - Reports a caret at the start of a non-empty field.
- **Mutual authentication.**
  - Muesli serves only the signed helper.
  - The helper checks Muesli's signature before sending anything typed. An ad-hoc or unsigned build
    sends nothing.
  - Requests carry the client and keystroke generation; a newer request supersedes older ones before
    and during inference.
  - The least recently active helper is evicted from the request ledger.
- **Keys.**
  - Tab accepts everything. Option-Right accepts one word (Windows: pick an equivalent such as
    Ctrl+Right).
  - The keystroke record is cleared on a click, on editing shortcuts (paste, cut, kill-line), on a
    Tab that isn't an acceptance, and on forward delete, Home, End and Page Up/Down.
  - The keystroke record is capped at the context length that is sent.
- **Context.**
  - Comes from accessibility (UIA) off the UI thread, bounded by `maxDocumentChars`.
  - Screen OCR is used only for a known matching document, refreshed at most every 4 s, and gated on
    retained capture, failing closed.
- **Engine.**
  - Base model, with a prompt cache.
  - Token healing scored as P(rest of word | typed part), preferring tokens that cover the whole
    typed prefix.
  - Byte-level BPE and SentencePiece vocabularies.
  - Line-break masking. Text before a line break in the final token is kept.
  - Emit only whole multibyte characters.
  - A word counts as complete only from the stop reason (EOS or newline), never from the piece count.
  - Never show a first word that the token budget cut off.
  - Half-typed words are also completed at the start of the text or a line.
- **Gates** (first word / mid-word log-probability, calibrated on 600 cut points):
  - Fewer: -1.0 / -1.0
  - Balanced: -1.5 / -2.0
  - More: -2.5 / -3.0
- **Models.**
  - Qwen 3.5 0.8B (the default), Qwen 3.5 2B and Gemma 4 E2B. Pinned revisions, with SHA-256 checked
    per file.
  - Downloads use the shared resumable downloader. Pause stops the transfer and keeps the partial
    file. Progress uses the shared progress view.
  - The model loads only when Cotypist is on. It is freed when Cotypist is off, and stays loaded
    across keyboard switches.
- **History.**
  - Tables `cotypist_suggestions` and `cotypist_sessions`, as in the macOS `database-schema.md`:
    - `context_before`, target app name and bundle ID are stored only for accepted or partial
      suggestions;
    - context source is the literal "keystrokes" (no app name);
    - migration blanks older rows.
  - Prune suggestions with nothing taken after 90 days or past 5,000 rows, at startup and daily.
  - Removing a session hard-deletes its suggestions.
  - Each outcome is tied to its suggestion ID and to the model that made the suggestion.
  - Sessions close after 10 min idle and appear in the timeline under source `cotypist`.
- **Insights.**
  - Words written, suggestions accepted, and acceptance rate.
  - The rate is capped at the 90-day window, and once the 5,000-row cap is reached it starts from the
    oldest untaken suggestion still kept.
- **Settings.**
  - Suggestion length, frequency, half-word completion with a pause, on-screen context, and per-app
    quiet apps from the shared catalog.
  - Options appear only while Cotypist is on.
  - Every `cotypist_*` setting, download and load is refused when the helper isn't bundled (build
    switch `MUESLI_INCLUDE_COTYPIST`).
  - Cotypist reports "on" only once the input source is selected. A setup guide covers adding the
    input source. The icon is a monochrome template.
- **Logging.** The helper keeps no debug log. Log lines name the app only for an accepted suggestion.

**Windows build so far (slice 1: engine):**
- **Shaper.** `windows-native/Muesli.Windows.Core/Cotypist/CotypistShaper.cs` ports `CotypistShaper`:
  - the after-space rule, the first-word gate and trim, `maxWords` and the stop at a sentence end;
  - completeness from the stop reason, never the piece count;
  - heal-forced tokens are excluded from the gate.
- **Settings enums and text helpers.** The same file holds:
  - `CotypistOptions`: the length, frequency and word-pause enums, and the per-frequency `FirstWordGate`
    (-1.0/-1.0, -1.5/-2.0, -2.5/-3.0);
  - `CotypistText.SplitPartialWord`, which completes a half-typed word at the start of the text or a line;
  - `PromptReadyScreenText`.
- **Vocabulary.** `windows-native/Muesli.Windows.Core/Cotypist/CotypistVocabulary.cs`:
  - Healing candidates prefer tokens that cover the whole typed prefix.
  - Line-break tokens are set aside.
  - llama.cpp's `token_to_piece` returns raw bytes for both byte-level BPE and SentencePiece, so the Ġ/▁ tables
    aren't needed. A partial UTF-8 piece decodes to U+FFFD and never matches.
- **Engine.** `windows-native/Muesli.Windows.Core/Cotypist/CotypistEngine.cs` runs on LLamaSharp:
  - prompt-prefix reuse;
  - healing scored as P(rest | typed part);
  - first-token line-break masking, rescaled to the other choices;
  - text before a line break is kept;
  - only whole multibyte characters are emitted, with at most 4 pending tokens;
  - EOS detection through `IsEndOfGeneration`;
  - `isCurrent` checked before and during inference.
  Qwen 3.5 is hybrid recurrent, so its state can't be trimmed partway. A diverging prompt clears the cache,
  which is the same as macOS's fallback for a non-trimmable cache.
- **Models.** `windows-native/Muesli.Windows.Core/Cotypist/CotypistModels.cs`:
  - Qwen 3.5 0.8B base (the default and recommended model): `ggml-org/Qwen3.5-0.8B-Base-GGUF@4d656529…`, Q4_0, 563 MB.
  - Qwen 3.5 2B base: `mradermacher/Qwen3.5-2B-Base-GGUF@85875686…`, Q4_K_M, 1.27 GB.
  - Both are pinned with their published SHA-256. The 0.8B hash was verified on a real download.
  - Gemma 4 E2B has no trusted base GGUF (only `-it` builds exist), so its card has no release ("Coming soon").
    It does not fall back to the instruct model.
- **Tests.** `windows-native/Muesli.Windows.Tests/CotypistShaperTests.cs`:
  - 9 always-on tests: shaper, gates, text helpers, vocabulary and catalog.
  - `CotypistEngineTests`, gated on `MUESLI_COTYPIST_MODEL`. It passed on the real 0.8B GGUF: continuation,
    healing " quic", no line break, cancellation.

**Windows build, slice 2 (history, insights and settings):**
- **Schema v5.** `cotypist_sessions` and `cotypist_suggestions` are STRICT tables with UTC ticks
  (`windows-native/Muesli.Windows.Core/Services/Persistence/PersistenceSchema.cs:234-282`). It was applied to the real
  library on relaunch; the change is additive. Windows never wrote older rows, so the macOS clean-up of older rows
  (blanking the app and the sentence on suggestions that weren't taken, and changing `keystrokes(...)` sources) is
  N/A.
- **`CotypistHistoryStore`** (`windows-native/Muesli.Windows.Core/Cotypist/CotypistHistoryStore.cs`):
  - `Record` (:65): an acceptance joins the app's open session or opens one, and a suggestion that wasn't taken
    only counts toward an open session. The app and the sentence are stored only for accepted or partial
    suggestions. Every write prunes.
  - `Prune` (:109): 90 days or 5,000 rows for suggestions that weren't taken.
  - `Sessions` and `Session` give the timeline records with their acceptances. `DeleteSession` (:156) hard-deletes
    the session and its suggestions.
  - `InsightsTotals` (:164): words written, suggestions accepted, and an acceptance rate capped at the 90-day window
    and the start of the count cap.
- **Settings.** The seven macOS `cotypist_*` values use the same raw values (`SettingsStore.cs:546-554`) and are
  normalised in `NormalizeAfterDeserialization` (:206-210).
- **Build switch.** `-p:MuesliIncludeCotypist=true` defines `MUESLI_INCLUDE_COTYPIST`
  (`windows-native/Muesli.Windows.Core/Muesli.Windows.Core.csproj:2-5`). `CotypistAvailability.IsHelperBundled` also
  requires the TSF DLL next to the app, so every setting, download and load stays refused until the helper exists
  (`windows-native/Muesli.Windows.Core/Cotypist/CotypistAvailability.cs`). The opt-in build compiles.
- **Tests.** `windows-native/Muesli.Windows.Tests/CotypistHistoryTests.cs` has 5 tests: sessions, privacy and
  removal, insights with the count-cap rate, pruning without a write, and settings. The JSON-to-SQLite guard now
  allows the `cotypist_` tables (`PersistenceMigrationTests.cs:285`). 238/238 targeted tests pass.

**Windows build, slice 3 (pipe and request service):**
- **Pipe server.** `windows-native/Muesli.Windows.Platform/Services/CotypistPipeServer.cs` is the Windows counterpart
  of `CotypistSocketServer`:
  - One JSON line in and one out per connection, with a 1 MB cap and a 2 s read timeout.
  - The pipe is `muesli-cotypist-<user SID>`. Its ACL allows this user and denies `NETWORK`.
  - Each client is checked through `GetNamedPipeClientProcessId` and `isTrustedPeer`.
  - Unlike macOS's separate IMK process, the TSF helper runs inside the app being typed in. So the default check
    (`HasHelperLoaded`) is that the client process has the bundled helper DLL loaded from Muesli's directory. Access
    denied, for example an elevated app, fails closed.
  - Open: verifying the helper DLL's Authenticode signature, and the helper checking Muesli's signature. Both come
    with the TSF helper and signing.
- **Request service.** `windows-native/Muesli.Windows.Platform/Services/CotypistService.cs` ports `handle`,
  `suggest` and `record`:
  - `midWordDelay` is sent with every reply.
  - Requests are refused for quiet apps, a caret at the start, a model that isn't downloaded, or completing words
    turned off.
  - Gates and word limits come from settings, and `CotypistRequestLedger` supersedes stale requests.
  - Healing uses `SplitPartialWord`, the result goes through the shaper, and `retryFull` and `complete` are set as
    on macOS.
  - The suggestion id, the sentence before the caret and the model are remembered (the last 32) for the outcome
    record. The sentence comes from `CotypistMessages.Sentence`.
  - The model warms on load, and `Unload` frees it when Cotypist turns off.
  - Context is limited for now to the helper's field text, its keystrokes, or a Claude Code prompt box (`ponytail:`
    marked). UIA document context and OCR need the helper's focused-document identity.
- **Protocol helpers.** `windows-native/Muesli.Windows.Core/Cotypist/CotypistProtocol.cs` has
  `CotypistRequestLedger` (with eviction past 64 clients), `CotypistAgentPrompt`, `CotypistMessages.Sentence` and
  outcome-event parsing.
- **Startup.** `App.StartCotypist` (`windows-native/Muesli.Windows.WinUI/App.xaml.cs`) starts the server and the
  daily prune only when `CotypistAvailability.IsHelperBundled`. In this build it doesn't start, and the relaunch log
  confirms it.
- **Tests.** `CotypistProtocolTests` (4) and `CotypistPipeTests` (2): a real pipe round trip, quiet-app rejection,
  `midWordDelay`, an outcome recorded into a session, refusal of an untrusted client, and a process without the
  helper loaded not being trusted. 20/20 Cotypist tests pass.

**Windows build, slice 4 (TSF helper):**
- **Text service.** `windows-native/Muesli.Cotypist.TextService/TextService.cpp` is a native C++ TSF text input
  processor that ports `CotypistController.m`. It is built with the VS 2022 Build Tools (v143), not dotnet.
  - Keys pass through to the app untouched (`OnKeyDown`, `TextService.cpp:460`).
    - Tab accepts the whole ghost. Ctrl+Right, the Windows counterpart of Option-Right, accepts the next word.
    - Any other key clears the ghost and records an "ignored" outcome.
  - When suggestions are requested:
    - After a boundary key (`" .,;:!?)"`), after 60 ms.
    - Inside a word, after the `midWordDelay` that Muesli sends with every reply.
  - The keystroke record (`TrackKey`, `:544`) is cleared on Backspace/Ctrl+Backspace, Enter, Esc, arrows, Tab, Delete,
    Home/End/PgUp/PgDn, Ctrl/Alt shortcuts (AltGr types), F-keys, and caret moves seen by the text edit sink
    (`OnEndEdit`, `:511`). It is capped at 1,500 characters.
  - Requests (`Send`, `:668`) read up to 1,500 characters of the field before the caret, and `caretAtStart`, in a
    read edit session. They are two-stage as on macOS: `firstWordOnly` first, then the full request unless the
    reply says `complete`, or there was no text and no `retryFull`.
  - The ghost (`ShowGhost`, `:729`) is a composition after the caret, with a grey display attribute and the caret held
    at its start. Ghost text is never committed: `OnCompositionTerminated` (`:495`) empties a composition the app
    ends itself.
  - Outcomes are accepted, partial (words taken one at a time), ignored or dismissed (`FinishShown`, `:829`).
  - Pipe work runs on a detached thread that holds a module reference, so a slow or absent Muesli never stalls a
    keystroke.
- **Helper side of the pipe.** `IsMuesli` (`TextService.cpp:190`) requires the process at the server end to carry
  Muesli's package identity (`Muesli.Windows`), which an ordinary process can't claim, before anything typed is
  written. Writes and reads time out after 3 s (`SendToMuesli`, `:206`).
- **Install.** MSIX can't register a TSF text service, so `scripts/cotypist-register.ps1` (elevated) does it:
  - builds the DLL;
  - installs it to `%ProgramFiles%\Muesli\Cotypist`, where only an administrator can replace it;
  - grants ALL APPLICATION PACKAGES read access so packaged apps can load it;
  - runs `regsvr32` (`DllRegisterServer`, `:914`: CLSID, profile, keyboard/display-attribute/immersive/systray
    categories). `-Unregister` reverses it.
- **Trust path.** Muesli trusts pipe clients that have that installed file loaded
  (`CotypistAvailability.HelperPath`, `windows-native/Muesli.Windows.Core/Cotypist/CotypistAvailability.cs:23`).
  The Cotypist service starts only when that file exists.
- **Tests.** `CotypistTextServiceTests` loads the built DLL without registering it. The class factory makes an
  `ITfTextInputProcessorEx` that also exposes `ITfKeyEventSink`, and refuses other CLSIDs. 21/21 Cotypist tests
  pass (the engine test is skipped without `MUESLI_COTYPIST_MODEL`).
- Not yet run on this machine: registration changes HKLM, so it waits for the user to run it elevated.

**Windows build, slice 5 (setup and Settings):**
- **On and off.** `windows-native/Muesli.Windows.WinUI/Services/CotypistSetup.cs` ports `CotypistSetup.swift`.
  - "On" means the input method is in the user's keyboard list (`IsOn`, `:44`).
  - macOS can't add an input source from code, so it guides the user through Keyboard settings and retries.
    Windows can: `SetOn` (`:61`) calls `InstallLayoutOrTip` and `SetDefaultLayoutOrTip`, so there is no guide
    state.
  - As on macOS, turning on downloads the selected model and loads it; turning off frees it.
- **Model download.** `Download` (`:94`) uses the shared resumable, SHA-256-verified `CleanupModelInstaller`, which
  gained a path override (`windows-native/Muesli.Windows.Core/Services/CleanupModelCatalog.cs:165`). It reports
  Downloading, Failed, Unavailable ("Coming soon") or Ready. `DeleteAsync` (`:149`) never deletes the selected
  model.
- **Settings section.** `windows-native/Muesli.Windows.WinUI/Pages/SettingsPage.xaml:586` and
  `Pages/SettingsPage.Cotypist.cs` port `cotypistSettingsSection` into the Dictation tab, with the macOS strings:
  - the toggle and its status;
  - the model row (size, progress or error, with Download or Models);
  - while on: key hints (Ctrl+Right for Option-Right), the model line with Change, length, frequency, finishing
    words and the pause sub-row.
  - Quiet apps (`RenderQuietApps`, `:147`): chips with remove buttons, an empty state, and Suggested installed
    password managers and editors (`Services/CotypistQuietApps.cs:21`).
  - The Add app menu (`:224`) lists open apps, then "Choose an app…".
  - These controls apply at once, as on macOS. The page's Save draft starts from disk, so it keeps them.
  - The section only appears when `App.Cotypist` exists, which needs this build to include Cotypist and the helper
    to be installed.
- **Left out on purpose:** the "Use on-screen text" row. Windows doesn't read screen context yet (the
  `CotypistService` `ponytail:` note), so a toggle there would do nothing.
- **Verified:** the app relaunched; Settings › Dictation renders with the section hidden; the log is clean. Both builds
  compile (`-p:MuesliIncludeCotypist=true` and the default). 59 Cotypist, cleanup-model and settings tests pass.
  The section hasn't been seen on screen yet: that waits on the elevated registration.

**Done when:**
- [x] TSF text service project.
- [x] Authenticated pipe. Both sides are built. Authenticode checks of the DLL and of Muesli wait on signing.
- [x] Engine with healing and gates, plus tests.
- [x] History schema.
- [x] Insights. The COTYPIST panel (`windows-native/Muesli.Windows.WinUI/Pages/InsightsPage.xaml:410`, data from
  `ViewModels/InsightsPageViewModel.cs:162`) is placed after Streaks as on macOS. It shows words written for you,
  suggestions accepted, and the acceptance rate with the macOS "Taken, last N days" label. It appears only once a
  suggestion has been accepted in the selected range. The range start is now shared through
  `InsightsWordAnalyzer.StartDate`.
- [x] Settings. The store, the build gate, on/off and the Settings section are built. Seeing it on screen waits on
  the helper being registered.
- [x] Model cards. When Cotypist is installed, the Models page gets a Cotypist tab
  (`windows-native/Muesli.Windows.WinUI/Pages/ModelsPage.Cotypist.cs:24`). Each card
  (`CotypistCard`, `:55`) has:
  - the vendor logo (a generic glyph for Gemma), Recommended and size, and the summary;
  - a "Cotypist Active" or "Downloaded" chip;
  - progress or a failure message;
  - Download, Pause, "Use for Cotypist" (`:176`, which loads the model at once), or Delete with the macOS
    confirmation (`:189`), or "Coming soon".

  Settings' Change and Models buttons open this tab. Verified: the Models and Insights pages render after relaunch
  with the Cotypist parts hidden on this machine (no helper, no history); the log is clean; 36 targeted tests pass.
- [ ] Signing and packaging (MSIX COM registration).
- [ ] Physical typing check in Notepad, a browser and Office.

---

### Process and documentation commits (no product work)

| Commits | Content | Status |
|---|---|---|
| `80ef58e5` `105b59a1` `c213050a` `3ab81322` `a1bc187c` `678bd2a0` | CI moved to Blacksmith, maintainer-only and opt-in, keeping three cache snapshots | Process |
| `b0ff14a6` `27722cac` `ee1b5ce7` | DCO sign-off rules and remediation | Process |
| `996e6c2e` `998412e9` `cafe8475` `b02719c8` `1ae3794f` `d4dc3d40` `a57b21c6` `e4107c93` | README and handover docs (video permissions, vocabulary guide, plugin link, Gemma MLX, Cotypist) | Process; feature content is in MAC-02/33/34/25 |
| `a1e84426` `cb68f286` `0bee39e1` | AGENTS.md invariants for SQLite, chat and native UI; schema docs | Folded into MAC-01, MAC-02 and MAC-33 invariants |
| `184460b9` `34ada2eb` | Computer Use planner budget tests isolated from CI scheduling | Verify that the Windows CU planner timeout tests use virtual time |

---

### Active macOS branches (unmerged)

| ID | Branch | Commits | Content and invariants | Status |
|---|---|---|---|---|
| W-01 | `codex/medasr-integration` | `674f6979` `c17e438b` | Google MedASR dictation and CLI; local terms acceptance instead of an HF sign-in | Watch; Artifact |
| W-02 | `codex/vercel-main-only` | `13b46765` | Vercel deploys only from main | Process |
| W-03 | `codex/bodhan-output-quality` | `53704920` | Filter Bodhan placeholder tokens; keep punctuation that overlaps a chunk seam | Watch; port to `BodhanRecognizer.cs` when merged |
| W-04 | `codex/cua-trycua-benchmark` | 30 (`8817838e`..`0d3d70fa`) | Background CUA scoped to a window: one owner and focus; Accessibility-only comparison mode; sessions stay awake through planning; the driver starts before the shutdown deadline; locked sessions told apart from an empty Chrome tree; traces show planner thinking separately from the execution timeout | Watch; compare with the Windows CU contract (CU-01) when merged |
| W-05 | `codex/time-turner` | `48fee9d8` `b8f79340` | Ambient memory timeline and agent CLI | Watch |
| W-06 | `codex/pr-597-language-selectors` | `e5e72999` | Same subject as `4f97a3d1` | See MAC-24 |
| W-07 | `vorflux/macos-ui-rendering-ci` | `23974ec5` `eb8875e1` `bc64b633` | Advisory visual-test contract with trust boundaries | Process; idea: WinUI screenshot tests |

### Older macOS branches (before the history rewrite, unreleased)

These never reached `main` and are abandoned on macOS. **Reference (abandoned)** means: read it only when building the related MAC row. Never build it as work of its own.

| Branch | Commits | Content | Windows status |
|---|---|---|---|
| `codex/CUA-refactor` | 35 | CUA rebuilt around visual control: windows matched by AX identity; scoped paste and OCR evidence; fail closed on unscoped quiet delivery; serialized sessions; blocked during a meeting; OCR redacted from traces | Reference (abandoned): check against Windows CU (most of it later reached main in another form) |
| `codex/cotypist-qwen-text-fim` | 11 | Earlier Cotypist built on FIM: caret overlay; retry-prompt leakage fix; repetition fix | Superseded by MAC-33 (an IMK base model, not FIM) |
| `codex/cua-model-first-harness` | 7 | CUA settings controls; planner history bounded; planner reset around intent tools | Reference (abandoned): check (`CuaMuesliSettings.cs`) |
| `codex/fix-profile-entitlement-arrays` | 1 | CloudKit entitlement injection | N/A-mac |
| `codex/fix-settings-permission-polling` | 2 | Login-item state refreshed on activation, not polled | Reference (abandoned): check the WinUI StartupTask refresh |
| `codex/implement-live-meeting-behavior` | 1 | AGENTS guidance | Process |
| `codex/localvqe-aec-replacement` | 1 | LocalVQE meeting AEC | Watch (AEC parity) |
| `codex/meeting-mic-health-recovery` | 14 | Recover the meeting mic when health degrades: three-way recovery results; user-muted input classified (no recovery episode); cooldown back-pressure; teardown invalidation beats stale handoff workers; episode telemetry; callbacks outside the lock | Watch; strong reference for MAC-35/MAC-19 |
| `codex/pause-meeting-recording` | 10 | Pause and resume meetings; discard confirmation without blocking; resume-safe floating control; markdown removed from previews | Reference (abandoned): check the Windows pause and discard flow |
| `codex/release-signing-profile-guardrails` | 4 | Signing profile validation | N/A-mac (SIGN-01 is the Windows equivalent) |
| `codex/stabilize-vad-release-test` | 1 | VAD boundary test made stable | Process |
| `codex/webrtc-aec-separation` | 16 | WebRTC, then DTLN-aec neural AEC; Apple voice processing reverted (ducking); keep the mic tail when system audio is shorter; transcript health recovery; chunk-rotation re-entry fix; View Notes notification opens the right meeting | Watch (AEC) and Reference (abandoned): check (notification target, rotation re-entry) |
| `cua-tts` | 4 | Spoken CUA feedback (KittenTTS) | Watch |
| `cursor/pr-342-settings-copy-12b4` | 4 | Default meeting join action; calendar event ID carried through Coming Up | Excluded (calendar) |
| `cursor/rebase-meeting-participants-eb39` | 9 | Participants from Contacts and calendar; participant names on export | Excluded (calendar); export naming → Reference (abandoned): check |
| `cursor/rebase-pr-134-d382` | 3 | Detection stays suppressed during finalization; processing-indicator state isolated; dictation allowed while a meeting finishes | Reference (abandoned): check |
| `devin/1782027960-auto-record-scheduled-wakes` | 8 | Calendar auto-record wakes; Safe Links unwrapping | Excluded (calendar); Safe Links unwrapping → check `MeetingUrlParser.cs` |
| `feat/cohere-transcribe-integration` | 15 | Cohere Transcribe 2B: VAD silence check before decoding; overlap dedup with trigram hashes; load-generation race | Artifact; VAD guard for hallucination → Reference (abandoned): check |
| `feature/qwen3-postprocessor` | 15 | Qwen3 post-processor: meetings stay raw; filler cleanup after a rejected LLM output; stale warm-ups discarded; downloaded model required before enabling | Reference (abandoned): check (MOD-05) |
| `feature/ux/sound_and_color_effects` | 5 | Sounds, glass pill, accent colour, menu-bar icon picker | Watch |
| `feature/whisperkit-backend` | 6 | WhisperKit warm-up spinner in the indicator | Reference (abandoned): check the Windows warm-up indication |
| `fix/cmd-shortcuts-text-editing` | 1 | Hotkeys skipped while a text input has focus | Reference (abandoned): check |
| `fix/ui-ux-polish` | 7 | Download retry with backoff and HTTP validation; per-folder count queries; meeting list capped at 200 | Reference (abandoned): check |
| `join-record-notification-pipeline` | 9 | Join & Record notifications | Excluded (calendar) |
| `openhands-fix-issue-225`, `openhands/fix-pause-media-during-dictation`, `revert-227-...` | 1+1+1 | Pause media during dictation; media that was already paused must not be resumed (fix then revert) | Reference (abandoned): check the Windows media-pause feature, if one exists |
| `openhands-fix-issue-74` | 1 | Ollama summary | Done on Windows (`ollama` provider) |
| `openhands-fix-issue-94` | 3 | Copy/paste while editing notes; Edit menu | Reference (abandoned): check that WinUI editors keep Ctrl+C/V |
| `pHequals7-patch-1` | 1 | README | Process |
| `revert-417-codex/dictation-buffer-drain` | 1 | Reverted "drain buffer ring on stop; 512-frame buffers" | Insight: that drain approach was rejected; see MAC-35 for what replaced it |

41 more branches have no unreleased commits. Their content is already on `main` or has been dropped:
`add-claude-github-actions-*`, `cleanup/remove-python-legacy`, `codex/dco-contribution-policy`,
`codex/dictation-buffer-drain`, `codex/dictionary-auto-add-suggestions`,
`codex/fix-browser-audio-meeting-false-positives`, `codex/fix-finalizing-modal-zoom-audio`,
`codex/fix-launch-at-login`, `codex/harden-sparkle-update-flow`, `codex/hotfix-0.6.1-spm-bundle-path`,
`codex/improve-onboarding-flow`, `codex/indic-asr-coreml-integration`, `codex/meet-notification-polish`,
`codex/meeting-mic-degradation-recovery`, `codex/meeting-transcription-recovery`,
`codex/onboarding-reliability-polish`, the `codex/preprod-0.8.2-*` branches, `codex/preprod-update-ux`,
`codex/repo-housekeeping`, `codex/review-standards`, `codex/stable-aggregate-uid`,
`codex/summary-backends-lmstudio-custom-endpoint`, `codex/sync-onboarding-bridge`,
`codex/system-audio-tap-health`, `cursor/sync-pr-134-d382`, `cursor/transcribe-join-default-action-1875`,
`devin/*nested-meeting-folders`, `devin/*upcoming-meetings-window`, `devin/*auto-export-markdown`,
`feat/meeting-folders-and-detection`, `feature/coreaudio-tap-migration`, `feature/meeting-follow-up`,
`landing-page-updates`, `native-swift`, `openhands-fix-issue-154`, `openhands-fix-issue-209`,
`openhands-fix-issue-74-try2`, `trendshift-badge-readme`, `ui-frontend`.

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
