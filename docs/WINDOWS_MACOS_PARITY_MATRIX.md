# Muesli macOS-to-Windows parity matrix

Inventory date: 2026-09-23.

**Status authority:** [`WINDOWS_LAUNCH_LEDGER.md`](WINDOWS_LAUNCH_LEDGER.md). This file keeps capability IDs and macOS/Windows source mapping. If a status here disagrees with the ledger, the ledger wins.

Windows target: `windows-native/Muesli.Windows.WinUI` (`net10.0-windows10.0.26100.0`, packaged WinUI 3, x64), with shared behavior in `Muesli.Windows.Core` / `Muesli.Windows.Platform`. WPF is a retired reference at `windows-native/Muesli.Windows.Wpf.Legacy`.

macOS reference: `C:/Users/madha/Downloads/muesli-main/muesli-main`.

The macOS implementation is the behavioral reference, not a library compatibility promise. CoreAudio process taps, ScreenCaptureKit, CoreML/ANE, EventKit, CloudKit, AppKit menu-bar APIs, macOS Accessibility/TCC, and Sparkle do not have drop-in Windows equivalents.

Status meanings match the launch ledger: **Complete and verified**, **Implemented with verification debt**, **Partial**, **Missing**, **Excluded**, **Externally blocked**.

Owner modules are launch-program Phase 0–13. They are not the historical P0–P8 labels. Pinned SDK 10.0.400 is available through the user-local .NET host, not the default `dotnet` on `PATH`. On 2026-09-23 the active exact-SDK WinUI Release ReadyToRun build passes with 0 warnings / 0 errors. With the shared Swift package supplied, current discovery is **905 tests** and the Release suite is **901 passed, 0 failed, 4 explicit skips**; all four skips name their prerequisite and the bounded shared-project scan completes, but the streaming fixture test/skip remains absent. Historical combined UI automation remains 20/20 and was not rerun. L52 owns the remaining streaming/current-UI/clean-environment debt. The tracked cutover ends at `ad92e43`; production signing, clean-profile/clean-VM, physical, human, clean two-checkout, and current packaged WinUI evidence remain open.

## Models and model lifecycle

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| MOD-01 | Offline Parakeet TDT with CPU/accelerator selection | `Models.swift`, `TranscriptionRuntime.swift`, `FluidAudioBackend.swift` | Packaged CPU provider plus an optional SHA-256-pinned CUDA pack. `CudaAccelerationPack.cs`, `ExecutionProviderService.cs`, and real per-model reports under `qualification/gpu-qualification` verify provider selection/fallback; Automatic keeps Parakeet v3 on CPU where measured CUDA offers no gain. Public MSIX remains truthfully CPU-only | Implemented with verification debt | Phase 1 / 13 |
| MOD-02 | Multiple offline ASR models | `Models.swift`, `ModelsView.swift`; WhisperKit/FluidAudio/Qwen/Cohere/SenseVoice backends | Twelve sherpa-onnx choices in `TranscriptionModelCatalog.cs`, with truthful per-model language choices in `ModelLanguageSupport.cs`. CoreML/LiteRT absent | Implemented with verification debt | Phase 1 |
| MOD-03 | Download, progress, cancel, delete, switch, recovery | `ModelsView.swift`, `Models.swift` | `TranscriptionModelLifecycleService.cs`; `TranscriptionModelPlatformTests.cs` | Implemented with verification debt | Phase 1 |
| MOD-04 | Opt-in live Nemotron 3.5; Parakeet Realtime EOU | `Models.swift`, `MeetingStreamingPartialSession.swift` | `StreamingModelPlatform.cs`; CoreML EOU absent | Implemented with verification debt | Phase 4 |
| MOD-05 | Optional local Qwen cleanup lifecycle | `TranscriptCleanupClient.swift` | `NativeTextCleanupService.cs`; manual GGUF only | Partial | Phase 1 |
| MOD-06 | No silent engine fallback | Runtime policies/tests | Strict role routing; no Python/hidden engine | Complete and verified | Phase 1 |

## Dictation, paste, hotkeys, audio, text

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DIC-01 | Hold-to-talk, local transcription, persistence | `AppScopedDictationRecorder.swift`, `DictationStore.swift` | `DictationHotkeyStateMachine.cs`, `DictationCoordinator.cs`, `AudioCaptureService.cs` | Implemented with verification debt | Phase 2 |
| DIC-02 | Insert at original cursor without replacing the clipboard; clipboard-safe fallback | Hotkey/paste path | `ActiveAppPasteService.cs`; `HotkeyAndPasteTests.cs` | Implemented with verification debt | Phase 2 |
| DIC-03 | History copy, delete, filter, search | `DictationsView.swift`, `DictationStore.swift` | WinUI timeline/library view models plus shared SQLite/JSON repository search and persistence contracts | Implemented with verification debt | Phase 2 |
| HOT-01 | Configurable hold hotkey | `HotkeyMonitor.swift`, `ShortcutHotkeyPolicy.swift` | `GlobalHotkeyService.cs`, `HotkeyGesture.cs` | Implemented with verification debt | Phase 2 |
| HOT-02 | Double-tap hands-free | Hotkey controller | `DictationHotkeyStateMachine.cs`; `Phase2DictationTests.cs` | Implemented with verification debt | Phase 2 |
| AUD-01 | Mic enumeration, selection, fallback | `AudioRouteController.swift` | `AudioCaptureService.cs` | Implemented with verification debt | Phase 2 |
| AUD-02 | Route/Bluetooth/unplug recovery | Route-aware recorders | IMMNotificationClient + meeting health | Implemented with verification debt | Phase 2 / 3 |
| AUD-03 | Media pause / ducking | `MediaPlaybackController.swift`, `AudioDuckingController.swift` | None | Externally blocked | Decision |
| TXT-01 | Filler removal | `FillerWordFilter.swift` | `FillerWordFilter.cs` | Implemented with verification debt | Phase 2 |
| TXT-02 | Personal dictionary | `CustomWordMatcher.swift` | `DictionaryCorrectionService.cs` | Implemented with verification debt | Phase 2 |
| TXT-03 | Shared cleanup ordering | `TranscriptFormatter.swift` | `TranscriptionPipelineService.cs` now shares cleanup → filler → dictionary across dictation, meeting, and import; meeting/import filler runs per line body with speaker prefixes preserved and honours `RemoveFillerWords`. `Txt03SharedTextOrderingTests` cover the golden order | Implemented with verification debt | Phase 2 / 5 / 8 |
| API-03 | Permissions (not macOS TCC) | Onboarding/TCC | `WindowsMicrophoneAccessService.cs`, paste UIPI diagnostics | Implemented with verification debt | Phase 2 / 12 |

## Meeting capture, live, finalization

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| MTG-01 | Simultaneous mic/system capture | `MeetingSession.swift`, `MeetingMicrophoneRecorder.swift`, `CoreAudioSystemRecorder.swift` | `MeetingRecordingCoordinator.cs`, `AudioCaptureService.cs`, `SystemAudioCaptureService.cs` | Implemented with verification debt | Phase 3 |
| MTG-02 | Suspend/resume, cancel, crash recovery | `MeetingResumePolicy.swift`, repair/termination policies | `MeetingSessionStateMachine.cs`, `MeetingSessionJournalStore.cs`; `Phase3MeetingLifecycleTests.cs` | Implemented with verification debt | Phase 3 |
| MTG-03 | Edit title/transcript/notes; retranscribe | `MeetingDetailView.swift`, `MeetingNotesView.swift` | Meeting-detail transcript edit/cancel/save and candidate retranscription are wired. L42 adds per-meeting retained-audio admission before scratch plus deterministic concurrent candidate handling, and L46 prevents recovered stale retranscription from restoring an older transcript over a newer accepted transcript. Focused live flow is 1/1, L46 focused tests are 27/27 in Debug and Release, and the elevated UI suite is 6/6; retained-audio quality and broader meeting qualification remain evidence debt | Implemented with verification debt | Phase 7 / 5 |
| LIVE-01 | Live transcription + floating UI | `MeetingStreamingPartialSession.swift`, `LiveTranscriptView.swift` | `MeetingLiveTranscriptionSession.cs`, `MeetingLiveTranscriptWindow.xaml`; `Phase4LiveTranscriptionTests.cs` | Implemented with verification debt | Phase 4 |
| LIVE-02 | VAD natural-boundary rotation | `StreamingVadController.swift`, `PCMChunkRecorder.swift` | Native Silero; `MaxSpeechDuration=0` | Implemented with verification debt | Phase 4 |
| LIVE-03 | Explicit final ownership modes | `MeetingSession.swift`, `Models.swift` | `LiveTranscriptOwnershipDescriptor.cs`; settings/journal persistence | Complete and verified | Phase 4 |
| LIVE-04 | Gap recovery and reconciliation | `MeetingTranscriptHealthMonitor.swift`, `TranscriptReconciler.swift` | `MeetingGapRecoveryService.cs` | Implemented with verification debt | Phase 4 |
| DIA-01 | Remote-speaker diarization + You | FluidAudio diarizer | `NativeDiarizationClient.cs`; `Phase5FinalizationTests.cs` | Implemented with verification debt | Phase 5 |
| DIA-02 | Speaker aliases on all surfaces | Meeting detail/store | `SpeakerAliasService.cs`; export/finalization tests | Implemented with verification debt | Phase 5 / 8 |
| API-01 | Process-targeted system capture | CoreAudio / ScreenCaptureKit | `WindowsProcessLoopbackCapture.cs` with disclosed endpoint fallback | Implemented with verification debt | Phase 3 |
| PLAY-01 | Playback, seek, waveform, track select | `MeetingRecordingPlayerView.swift` | `MeetingRecordingPlaybackService.cs` play/pause/seek/tracks plus asynchronous cached `WaveformPeaks` sampling and cleanup; `MeetingDetailView.xaml` renders the waveform | Implemented with verification debt | Phase 3 / 8 |

## Detection (calendar excluded)

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DET-01 | App/window/URL plus mic/camera evidence | `MeetingDetector.swift`, `MeetingCandidateResolver.swift`, collectors | `MeetingDetectionService.cs`, `MeetingPresenceSignals.cs`, `MeetingCandidateResolver.cs`, bounded native scan gate, and dedicated WinUI meeting-notification path; unit/action/suppression tests pass, live conferencing evidence remains open | Implemented with verification debt | Phase 6 |
| DET-02 | Dedupe, dismiss, auto-stop, recovery | Notification/auto-stop policies | `MeetingPromptService.cs`, `MeetingAutoStopTracker`; manual recordings never auto-stop | Implemented with verification debt | Phase 6 / 3 |
| JOIN-01 | Join & Record, Join Only, Record Only | `MeetingNotificationController.swift` | Detected-URL split button in `MeetingPromptService.cs`. Calendar URLs excluded | Implemented with verification debt | Phase 6 |
| CAL-01 | Google Calendar | `GoogleCalendarAuthManager.swift`, `CalendarMonitor.swift` | None; tray states no calendar source | Excluded | — |
| API-02 | EventKit notifications | Calendar monitor | No EventKit; Windows calendar OAuth is excluded | Excluded | — |

## Summaries, notes, organization

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| SUM-01 | Local + OpenAI/OpenRouter | `MeetingSummaryClient.swift` | `MeetingSummaryService.cs`; `Phase7NotesTests.cs`, `TextAndSummaryTests.cs` | Implemented with verification debt | Phase 7 |
| SUM-02 | Ollama and LM Studio/custom HTTP | Summary client | Ollama, LM Studio (`LmStudioEndpoint`/`LmStudioModel`), and a documented custom OpenAI-compatible endpoint (`CustomLlmEndpoint`/`CustomLlmModel` + optional `custom-llm-api-key` secret) share one chat-completions contract; `Phase7NotesTests` cover request shape, keyless/bearer, failures, and disclosure | Implemented with verification debt | Phase 7 |
| SUM-03 | ChatGPT subscription OAuth | `ChatGPTAuthManager.swift` | `SummaryProviderDisclosure.ChatGptSubscriptionBlocker` only | Excluded | — |
| SUM-04 | Automatic titles | Summary/title tests | `MeetingTitleService` + `TitleIsManual` | Implemented with verification debt | Phase 7 |
| SEC-01 | Secret storage and migration | Keychain tests | Shared `SettingsStore.cs` secret migration, `windows-native/Muesli.Windows.Platform/Secrets/WindowsCredentialSecretStore.cs:7-64`, and `SecretsAndSettingsTests.cs`; old `Services/SecretStore.cs` mapping was removed by L49 extraction | Complete and verified | Phase 7 |
| TPL-01 | Templates and re-summary | `MeetingTemplates.swift` | Built-in/custom templates in store/UI; Phase 7 tests | Implemented with verification debt | Phase 7 |
| NOTE-01 | Manual notes vs generated summary | `MeetingNotesView.swift` | `PersistedMeeting.ManualNotes`, `MeetingNotesComposer.cs`; Phase 7 tests | Implemented with verification debt | Phase 7 |
| ORG-01 | Nested folders | Meetings store/navigation | Production UI and JSON/SQLite persistence carry `ParentId`, indentation, subtree filtering, nested creation, explicit move-to-root/move-to-parent, and delete/reparent semantics; JSON deletion is crash-journaled across folder and meeting files. Green Release coverage exists, while retained GUI and cloned-profile evidence remain open | Implemented with verification debt | Phase 8 |
| DATA-01 | Profile-scoped protected cutover staging and coherent multi-file capture | Persistence/migration boundary | L47 covers profile-local protected staging, stale cleanup, coherent capture/retry, cross-process lock through authority, and no-follow validation. Focused L47 tests are 30/30 in Debug and Release with no P0–P3 findings; cloned-real-profile qualification has not run | Implemented with verification debt | Phase 8 |
| SEARCH-01 | Search dictations and meetings | `SearchResultsView.swift` | JSON and SQLite implementations share an interface-backed ordered, paged search contract beyond 500 results, with manual-note-aware snippets, counts, tabs, and clear action. The 2026-09-12 focused 10k-history slice passes 14/14; retained GUI and large real-profile latency evidence remain open | Implemented with verification debt | Phase 8 / 12 |

## Import, export, automation

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| IMP-01 | Import media, diarize, cancel | `AudioFileImportController.swift` | `MediaImportFormats.cs` (no ogg); `Phase8MediaImportTests.cs`, `Phase8ImportCancellationTests.cs` | Implemented with verification debt | Phase 8 |
| EXP-01 | Export PDF/Markdown | `MeetingExporter.swift` | `MeetingExportFormatter.cs`/`MeetingDocumentWriter.cs`; restored active `MeetingExportTests.cs`. QuestPDF 2026.5.0 Community selection recorded; eligibility is a Phase 13 owner check | Implemented with verification debt | Phase 8 |
| HOOK-01 | Post-meeting executable hook | `MeetingHookRunner.swift` | `PostMeetingAutomationService.cs`; restored active `PostMeetingHookTests.cs`; WinUI meeting detail renders the persisted result via `PostMeetingAutomationDiagnostics.cs` | Complete and verified | Phase 9 |
| AUTO-01 | Auto Markdown/PDF export | `MeetingMarkdownAutoExporter.swift` | L43 manifest integrity remains implemented and the 2026-09-14 L41 stress passes 8 rounds × 12 concurrent exports with exactly one Markdown and no temporary files. Automatic PDF now shares the format-aware atomic exporter (per-format control key/manifest) and honors the EXP-01 QuestPDF gate; `AutoPdfExportTests` cover fail-closed, atomic publish, collision safety, and manifest reuse. QuestPDF eligibility and human PDF open remain debt | Implemented with verification debt | Phase 9 |
| FOLLOW-01 | Follow-up workflow | `MeetingFollowUpPolicy.swift` | None | Missing | Phase 9 |

## Computer Use and sync

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| CU-01 | Optional Computer Use | Planner/executor/tool registry | `ComputerUsePlannerService.cs` and related; `Phase10ComputerUseTests.cs`; `COMPUTER_USE.md`. Text/screenshots disabled | Implemented with verification debt | Phase 10 |
| SYNC-01 | Private text sync / iPhone | `MuesliICloudSyncEngine.swift` | None. CloudKit excluded; any other backend is D3 | Externally blocked | Phase 11 |
| SYNC-02 | Sync audio | Product: audio never synced | None by design | Excluded | — |

## Onboarding, tray, shell

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| ONB-01 | Resumable onboarding | `OnboardingFlow.swift`, `OnboardingProgress.swift` | WinUI `OnboardingWindow` plus shared `OnboardingProgressStore.cs`; the portable `Phase12ProductExperienceTests.cs` coverage (selection invalidation, reconciliation, microphone HRESULT mapping, progress clamping/serialization) is restored to the active tree; retained UI evidence and the WPF preview/placement cases remain under L52 | Implemented with verification debt | Phase 12 |
| TRAY-01 | Rich tray menu | `StatusBarController.swift` | `TrayIconService.cs`; upcoming calendar row honestly disabled | Implemented with verification debt | Phase 12 |
| FLOAT-01 | Floating recording indicator | `FloatingIndicatorController.swift` | `ToastNotificationService.cs` | Implemented with verification debt | Phase 2 / 12 |
| FLOAT-02 | Waveform-hover live preview | Floating live transcript | `MeetingLiveTranscriptWindow` + `ShowLiveWaveformOnHover` | Implemented with verification debt | Phase 4 |
| SHELL-01 | Light/dark dashboard | SwiftUI dashboard | WinUI `App.xaml`/`MainWindow.xaml` is selected. Central settings-null normalization and targeted exception containment are implemented, but the last retained production-profile relaunch exposed only a title-bar capture and later hid until reactivation; L49 still owns reliable activation and physical accessibility/DPI qualification | Partial | Phase 12 |
| START-01 | Launch at login | Login item | `StartupRegistrationService.cs`; uncommitted exact executable/background-command validation has 9 passing tests | Implemented with verification debt | Phase 12 |
| INSTANCE-01 | Single-instance | App lifecycle | `SingleInstanceCoordinator.cs`; `ModelAndSingleInstanceTests.cs` | Complete and verified | Phase 12 |
| INSIGHT-01 | Insights analyzer / share | `InsightsView.swift:131-146`, `InsightsWordAnalyzer.swift` | Shared local analyzer plus WinUI PNG preview/copy/save/Windows sharing in `windows-native/Muesli.Windows.WinUI/ViewModels/InsightsPageViewModel.cs:71-136` and `windows-native/Muesli.Windows.Platform/Services/InsightsShareImageService.cs:18-76`. WPF sharing is absent; contribution stays D4-gated | Partial | Phase 12 / D4 |
| SOUND-01 | Feedback sounds | Sound settings | `SoundFeedbackService.cs`, Appearance setting, `SoundFeedbackTests.cs` | Implemented with verification debt | Phase 12 |

## Diagnostics, privacy, packaging, tests

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DIAG-01 | Logs, runtime status, support bundle | `DiagnosticIncident*.swift` | `AppLogService.cs`, `RuntimeDiagnosticsService.cs`, and `SupportBundleService.cs`, with a preview/save/cancel action on the WinUI About page (redacted environment, package identity, model/provider state, audio-device categories, incident categories, and a bounded log slice). `SupportBundleTests` cover redaction, large-log bounding, locked-log tolerance, incident aggregation, and no network transmission | Implemented with verification debt | Phase 13 |
| PRIV-01 | Local-first, explicit network, deletion | Privacy/auth/storage | Runtime + `WINDOWS_PRIVACY.md` + redaction/cleanup tests | Implemented with verification debt | Every / 13 |
| UPD-01 | Signed auto-update | Sparkle | Release tooling verifies signed manifests/hashes/publisher pins, and the WinUI app now checks/verifies/downloads/rolls back through `SignedUpdateService` (`UpdateWorkflowTests`); `WindowsProductionUpdateInstaller` fails closed until the production certificate exists (L05/D5) | Implemented with verification debt | Phase 13 |
| SIGN-01 | Authenticode | codesign/notarize | `sign-windows-release.ps1`; artifacts unsigned | Externally blocked | Phase 13 / D5 |
| PKG-01 | x64 zip + Inno installer | DMG/release scripts | `a5414b3` exact-SDK rehearsal (`10.0.400`, `rollForward=disable`) and package smoke pass. The recorded two-build digest `ee86084002e96b9698f089994b9053232cd58a573fddb5eeb0e1f64600339edf` (ZIP 109,499,342 bytes, installer 73,677,646 bytes) did not reproduce on the 2026-08-22 isolated re-run (`637a3433…`, ZIP 109,499,479, installer 73,666,874); cross-run digest stability is launch module L48. Artifacts remain unsigned; clean-VM/upgrade evidence remains open | Partial | Phase 13 |
| TEST-01 | Automated coverage | Swift test suite | The active tree discovers 1080 tests: 1075 pass, 0 fail, 5 explicitly skip with named prerequisites (`MUESLI_STREAMING_QUALIFICATION_MODEL` stream fixture restored via `Phase4LiveTranscriptionTests`, plus the media multi-speaker and cloned-profile fixtures). The Phase 4/10/12 anchors and the L41/L43/AUTO-01/EXP-01/HOOK-01 safety nets are re-ported to the active tree; the 2026-09-26 UI rerun is current (packaged shell 13/14, full UI project 17/24), with floating-indicator and unpackaged second-instance failures named | Partial | Continuous |
| QUAL-01 | Hardware/package gates | macOS release scripts | 2026-09-23 CPU Parakeet dictation RTF 0.055 and mic-only meeting RTF 0.063 pass; the optional CUDA pack and per-model provider evidence are real. WER/CER, system audio/diarization, broader hardware, current-source signed package, clean two-checkout/VM, and physical gates remain open | Partial | Phase 13 |
| API-04 | Sparkle/AppKit/codesign equivalents | Updater/status-bar | Native tray plus release-side manifest verification and an app-side check/verify/download/rollback workflow exist; automatic install is fail-closed until production Authenticode signing exists (externally blocked) | Partial | Phase 13 |
| STORE-01 | Microsoft Store | — | — | Excluded | — |
| ARM-01 | ARM64 | — | win-x64 only | Excluded | — |
| OOS-01 | Python/Electron/web | — | Prohibited | Excluded | — |
| OOS-02 | Silent fallback / fake data | — | Prohibited | Excluded | — |
| OOS-03 | Literal macOS ports | Apple frameworks | Windows-native designs only | Excluded | — |

## Required Windows equivalents (design, not status)

| macOS facility | Windows-native behavior in product or excluded |
|---|---|
| CoreAudio process tap / ScreenCaptureKit | Process-tree loopback on supported builds; disclosed render-endpoint fallback otherwise (API-01). |
| CoreML/ANE, Metal, LiteRT-LM | sherpa-onnx / LLamaSharp ONNX/GGUF. CoreML live EOU is absent. |
| EventKit | Excluded with Google Calendar OAuth. |
| CloudKit/iCloud | Excluded. Any later sync is Phase 11 + D3, not CloudKit. |
| macOS Accessibility/TCC | Microphone privacy checks and UIPI-aware paste diagnostics. |
| AppKit status item | WPF/Win32 NotifyIcon. |
| Sparkle, codesign, notarization, DMG | Authenticode + Inno + zip; updater missing; signing blocked on D5. |
| Camera/media attribution | Best-effort `MeetingPresenceSignals`; live app matrix still qualification debt. |

## Authoritative conclusions

1. Windows already has a native local-first product: WPF, WASAPI, sherpa-onnx ASR/diarization, live Nemotron (off by default), notes, detection, import/export, hooks, onboarding, and optional Computer Use.
2. The 2026-08-01 matrix was stale on manual notes, Ollama, live transcription, detection auto-stop, resumable onboarding, import cancellation, export tests, tray richness, and test counts.
3. Remaining **implementation** is a short list (cleanup download, LM Studio/custom HTTP, linked follow-ups, PDF auto-export, support bundle). Nested-folder persistence/move/delete, repository-backed search, async cached waveform loading, and fixture-crypto updater verification are implemented with verification debt; transcript edit/retranscribe is implemented (L23/L42/L46) with qualification debt only. Remaining **qualification** is the larger launch risk.
4. Calendar/OAuth, Store, ChatGPT OAuth, CloudKit, audio sync, ARM64, and literal macOS ports are excluded, not “later P5 work.”
5. Signing remains externally blocked. Unsigned zip/installer smoke is not a release.
