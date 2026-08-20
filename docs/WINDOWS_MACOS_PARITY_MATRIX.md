# Muesli macOS-to-Windows parity matrix

Inventory date: 2026-08-18.

**Status authority:** [`WINDOWS_LAUNCH_LEDGER.md`](WINDOWS_LAUNCH_LEDGER.md). This file keeps capability IDs and macOS/Windows source mapping. If a status here disagrees with the ledger, the ledger wins.

Windows target: `windows-native/Muesli.Windows` (`net10.0-windows`, WPF, x64).

macOS reference: `C:/Users/madha/Downloads/muesli-main/muesli-main`.

The macOS implementation is the behavioral reference, not a library compatibility promise. CoreAudio process taps, ScreenCaptureKit, CoreML/ANE, EventKit, CloudKit, AppKit menu-bar APIs, macOS Accessibility/TCC, and Sparkle do not have drop-in WPF equivalents.

Status meanings match the launch ledger: **Complete and verified**, **Implemented with verification debt**, **Partial**, **Missing**, **Excluded**, **Externally blocked**.

Owner modules are launch-program Phase 0–13. They are not the historical P0–P8 labels. The reviewed 2026-08-20 Release tree expands to **709** cases: 705 hardware-free passes and 4 explicit real-fixture qualification skips. This inventory does not re-claim 2026-08-01 CUDA/package/UI evidence.

## Models and model lifecycle

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| MOD-01 | Offline Parakeet TDT with CPU/accelerator selection | `Models.swift`, `TranscriptionRuntime.swift`, `FluidAudioBackend.swift` | Packaged CPU provider; public notices/inventory must not claim NVIDIA. `NativeSherpaRuntime.cs` can select an externally staged matching CUDA bundle, but the public package does not ship one | Partial | Phase 1 / 13 |
| MOD-02 | Multiple offline ASR models | `Models.swift`, `ModelsView.swift`; WhisperKit/FluidAudio/Qwen/Cohere/SenseVoice backends | Seven sherpa-onnx choices in `TranscriptionModelCatalog.cs`. CoreML/LiteRT absent | Implemented with verification debt | Phase 1 |
| MOD-03 | Download, progress, cancel, delete, switch, recovery | `ModelsView.swift`, `Models.swift` | `TranscriptionModelLifecycleService.cs`; `TranscriptionModelPlatformTests.cs` | Implemented with verification debt | Phase 1 |
| MOD-04 | Opt-in live Nemotron 3.5; Parakeet Realtime EOU | `Models.swift`, `MeetingStreamingPartialSession.swift` | `StreamingModelPlatform.cs`; CoreML EOU absent | Implemented with verification debt | Phase 4 |
| MOD-05 | Optional local Qwen cleanup lifecycle | `TranscriptCleanupClient.swift` | `NativeTextCleanupService.cs`; manual GGUF only | Partial | Phase 1 |
| MOD-06 | No silent engine fallback | Runtime policies/tests | Strict role routing; no Python/hidden engine | Complete and verified | Phase 1 |

## Dictation, paste, hotkeys, audio, text

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DIC-01 | Hold-to-talk, local transcription, persistence | `AppScopedDictationRecorder.swift`, `DictationStore.swift` | `DictationHotkeyStateMachine.cs`, `DictationCoordinator.cs`, `AudioCaptureService.cs` | Implemented with verification debt | Phase 2 |
| DIC-02 | Insert at original cursor without replacing the clipboard; clipboard-safe fallback | Hotkey/paste path | `ActiveAppPasteService.cs`; `HotkeyAndPasteTests.cs` | Implemented with verification debt | Phase 2 |
| DIC-03 | History copy, delete, filter, search | `DictationsView.swift`, `DictationStore.swift` | WPF list + `AppDataStore.cs` | Implemented with verification debt | Phase 2 |
| HOT-01 | Configurable hold hotkey | `HotkeyMonitor.swift`, `ShortcutHotkeyPolicy.swift` | `GlobalHotkeyService.cs`, `HotkeyGesture.cs` | Implemented with verification debt | Phase 2 |
| HOT-02 | Double-tap hands-free | Hotkey controller | `DictationHotkeyStateMachine.cs`; `Phase2DictationTests.cs` | Implemented with verification debt | Phase 2 |
| AUD-01 | Mic enumeration, selection, fallback | `AudioRouteController.swift` | `AudioCaptureService.cs` | Implemented with verification debt | Phase 2 |
| AUD-02 | Route/Bluetooth/unplug recovery | Route-aware recorders | IMMNotificationClient + meeting health | Implemented with verification debt | Phase 2 / 3 |
| AUD-03 | Media pause / ducking | `MediaPlaybackController.swift`, `AudioDuckingController.swift` | None | Externally blocked | Decision |
| TXT-01 | Filler removal | `FillerWordFilter.swift` | `FillerWordFilter.cs` | Implemented with verification debt | Phase 2 |
| TXT-02 | Personal dictionary | `CustomWordMatcher.swift` | `DictionaryCorrectionService.cs` | Implemented with verification debt | Phase 2 |
| TXT-03 | Shared cleanup ordering | `TranscriptFormatter.swift` | Dictation: filler then dictionary. Meeting/import: cleanup then dictionary, no filler | Partial | Phase 2 / 5 / 8 |
| API-03 | Permissions (not macOS TCC) | Onboarding/TCC | `WindowsMicrophoneAccessService.cs`, paste UIPI diagnostics | Implemented with verification debt | Phase 2 / 12 |

## Meeting capture, live, finalization

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| MTG-01 | Simultaneous mic/system capture | `MeetingSession.swift`, `MeetingMicrophoneRecorder.swift`, `CoreAudioSystemRecorder.swift` | `MeetingRecordingCoordinator.cs`, `AudioCaptureService.cs`, `SystemAudioCaptureService.cs` | Implemented with verification debt | Phase 3 |
| MTG-02 | Suspend/resume, cancel, crash recovery | `MeetingResumePolicy.swift`, repair/termination policies | `MeetingSessionStateMachine.cs`, `MeetingSessionJournalStore.cs`; `Phase3MeetingLifecycleTests.cs` | Implemented with verification debt | Phase 3 |
| MTG-03 | Edit title/transcript/notes; retranscribe | `MeetingDetailView.swift`, `MeetingNotesView.swift` | Title edit, manual notes, re-summarize exist. Transcript read-only. No retranscribe UI. L23 service (`TranscriptEditService`) is unwired | Partial | Phase 7 / 5 |
| LIVE-01 | Live transcription + floating UI | `MeetingStreamingPartialSession.swift`, `LiveTranscriptView.swift` | `MeetingLiveTranscriptionSession.cs`, `MeetingLiveTranscriptWindow.xaml`; `Phase4LiveTranscriptionTests.cs` | Implemented with verification debt | Phase 4 |
| LIVE-02 | VAD natural-boundary rotation | `StreamingVadController.swift`, `PCMChunkRecorder.swift` | Native Silero; `MaxSpeechDuration=0` | Implemented with verification debt | Phase 4 |
| LIVE-03 | Explicit final ownership modes | `MeetingSession.swift`, `Models.swift` | `LiveTranscriptOwnershipDescriptor.cs`; settings/journal persistence | Complete and verified | Phase 4 |
| LIVE-04 | Gap recovery and reconciliation | `MeetingTranscriptHealthMonitor.swift`, `TranscriptReconciler.swift` | `MeetingGapRecoveryService.cs` | Implemented with verification debt | Phase 4 |
| DIA-01 | Remote-speaker diarization + You | FluidAudio diarizer | `NativeDiarizationClient.cs`; `Phase5FinalizationTests.cs` | Implemented with verification debt | Phase 5 |
| DIA-02 | Speaker aliases on all surfaces | Meeting detail/store | `SpeakerAliasService.cs`; export/finalization tests | Implemented with verification debt | Phase 5 / 8 |
| API-01 | Process-targeted system capture | CoreAudio / ScreenCaptureKit | `WindowsProcessLoopbackCapture.cs` with disclosed endpoint fallback | Implemented with verification debt | Phase 3 |
| PLAY-01 | Playback, seek, waveform, track select | `MeetingRecordingPlayerView.swift` | `MeetingRecordingPlaybackService.cs` play/pause/seek/tracks. No playback waveform | Partial | Phase 3 / 8 |

## Detection (calendar excluded)

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DET-01 | App/window/URL plus mic/camera evidence | `MeetingDetector.swift`, `MeetingCandidateResolver.swift`, collectors | `MeetingDetectionService.cs`, `MeetingPresenceSignals.cs`, `MeetingCandidateResolver.cs`; `Phase6DetectionTests.cs` | Implemented with verification debt | Phase 6 |
| DET-02 | Dedupe, dismiss, auto-stop, recovery | Notification/auto-stop policies | `MeetingPromptService.cs`, `MeetingAutoStopTracker`; manual recordings never auto-stop | Implemented with verification debt | Phase 6 / 3 |
| JOIN-01 | Join & Record, Join Only, Record Only | `MeetingNotificationController.swift` | Detected-URL split button in `MeetingPromptService.cs`. Calendar URLs excluded | Implemented with verification debt | Phase 6 |
| CAL-01 | Google Calendar | `GoogleCalendarAuthManager.swift`, `CalendarMonitor.swift` | None; tray states no calendar source | Excluded | — |
| API-02 | EventKit notifications | Calendar monitor | No EventKit; Windows calendar OAuth is excluded | Excluded | — |

## Summaries, notes, organization

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| SUM-01 | Local + OpenAI/OpenRouter | `MeetingSummaryClient.swift` | `MeetingSummaryService.cs`; `Phase7NotesTests.cs`, `TextAndSummaryTests.cs` | Implemented with verification debt | Phase 7 |
| SUM-02 | Ollama and LM Studio/custom HTTP | Summary client | Ollama implemented. LM Studio/custom HTTP missing | Partial | Phase 7 |
| SUM-03 | ChatGPT subscription OAuth | `ChatGPTAuthManager.swift` | `SummaryProviderDisclosure.ChatGptSubscriptionBlocker` only | Excluded | — |
| SUM-04 | Automatic titles | Summary/title tests | `MeetingTitleService` + `TitleIsManual` | Implemented with verification debt | Phase 7 |
| SEC-01 | Secret storage and migration | Keychain tests | `SecretStore.cs`, `SecretsAndSettingsTests.cs` | Complete and verified | Phase 7 |
| TPL-01 | Templates and re-summary | `MeetingTemplates.swift` | Built-in/custom templates in store/UI; Phase 7 tests | Implemented with verification debt | Phase 7 |
| NOTE-01 | Manual notes vs generated summary | `MeetingNotesView.swift` | `PersistedMeeting.ManualNotes`, `MeetingNotesComposer.cs`; Phase 7 tests | Implemented with verification debt | Phase 7 |
| ORG-01 | Nested folders | Meetings store/navigation | SQLite repository has tested `ParentId`/subtree support; production JSON-backed UI remains one-level | Partial | Phase 8 |
| SEARCH-01 | Search dictations and meetings | `SearchResultsView.swift` | SQLite FTS indexes manual notes, but production UI still uses title/summary/transcript/metadata filtering | Partial | Phase 8 / 12 |

## Import, export, automation

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| IMP-01 | Import media, diarize, cancel | `AudioFileImportController.swift` | `MediaImportFormats.cs` (no ogg); `Phase8MediaImportTests.cs`, `Phase8ImportCancellationTests.cs` | Implemented with verification debt | Phase 8 |
| EXP-01 | Export PDF/Markdown | `MeetingExporter.swift` | `MeetingExporter.cs`; `Phase8ExportTests.cs`. QuestPDF 2026.5.0 Community selection recorded; eligibility is a Phase 13 owner check | Implemented with verification debt | Phase 8 |
| HOOK-01 | Post-meeting executable hook | `MeetingHookRunner.swift` | `PostMeetingAutomationService.cs`; `Phase9AutomationTests.cs` | Complete and verified | Phase 9 |
| AUTO-01 | Auto Markdown/PDF export | `MeetingMarkdownAutoExporter.swift` | Markdown auto-export implemented. PDF auto-export missing | Implemented with verification debt | Phase 9 |
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
| ONB-01 | Resumable onboarding | `OnboardingFlow.swift`, `OnboardingProgress.swift` | `OnboardingWindow`, `OnboardingProgressStore.cs`; `Phase12ProductExperienceTests.cs` | Implemented with verification debt | Phase 12 |
| TRAY-01 | Rich tray menu | `StatusBarController.swift` | `TrayIconService.cs`; upcoming calendar row honestly disabled | Implemented with verification debt | Phase 12 |
| FLOAT-01 | Floating recording indicator | `FloatingIndicatorController.swift` | `ToastNotificationService.cs` | Implemented with verification debt | Phase 2 / 12 |
| FLOAT-02 | Waveform-hover live preview | Floating live transcript | `MeetingLiveTranscriptWindow` + `ShowLiveWaveformOnHover` | Implemented with verification debt | Phase 4 |
| SHELL-01 | Light/dark dashboard | SwiftUI dashboard | `App.xaml`, `MainWindow.xaml` | Implemented with verification debt | Phase 12 |
| START-01 | Launch at login | Login item | `StartupRegistrationService.cs` | Implemented with verification debt | Phase 12 |
| INSTANCE-01 | Single-instance | App lifecycle | `SingleInstanceCoordinator.cs`; `ModelAndSingleInstanceTests.cs` | Complete and verified | Phase 12 |
| INSIGHT-01 | Insights analyzer / share | `InsightsView.swift`, `InsightsWordAnalyzer.swift` | Dashboard stat cards only | Partial | Phase 12 / D4 |
| SOUND-01 | Feedback sounds | Sound settings | `SoundFeedbackService.cs`, Appearance setting, `SoundFeedbackTests.cs` | Implemented with verification debt | Phase 12 |

## Diagnostics, privacy, packaging, tests

| ID | Capability | macOS reference | Windows evidence | Status | Owner |
|---|---|---|---|---|---|
| DIAG-01 | Logs, runtime status, support bundle | `DiagnosticIncident*.swift` | `AppLogService.cs`, `RuntimeDiagnosticsService.cs`. No support-bundle UI | Partial | Phase 13 |
| PRIV-01 | Local-first, explicit network, deletion | Privacy/auth/storage | Runtime + `WINDOWS_PRIVACY.md` + redaction/cleanup tests | Implemented with verification debt | Every / 13 |
| UPD-01 | Signed auto-update | Sparkle | About → GitHub Releases only | Missing | Phase 13 |
| SIGN-01 | Authenticode | codesign/notarize | `sign-windows-release.ps1`; artifacts unsigned | Externally blocked | Phase 13 / D5 |
| PKG-01 | x64 zip + Inno installer | DMG/release scripts | Package/installer scripts exist; unsigned; public inventory is CPU-only; upgrade/VM open | Partial | Phase 13 |
| TEST-01 | Automated coverage | Swift test suite | 709 cases in the reviewed Release tree: 705 pass, 4 explicit qualification skips; no integrated full GUI automation | Partial | Continuous |
| QUAL-01 | Hardware/package gates | macOS release scripts | Scripts exist; current-tree hardware evidence not re-run | Partial | Phase 13 |
| API-04 | Sparkle/AppKit/codesign equivalents | Updater/status-bar | NotifyIcon yes; signed updater no | Partial | Phase 13 |
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
3. Remaining **implementation** is a short list (cleanup download, LM Studio, nested folders, search notes, transcript retranscribe, PDF auto-export, support bundle, updater). Remaining **qualification** is the larger launch risk.
4. Calendar/OAuth, Store, ChatGPT OAuth, CloudKit, audio sync, ARM64, and literal macOS ports are excluded, not “later P5 work.”
5. Signing remains externally blocked. Unsigned zip/installer smoke is not a release.
