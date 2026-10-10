# Launch blocker review — 2026-10-10

Full review of `codex/meeting-dictation-parity` (after `16c06d8`) against the signed-installer
goal and the macOS reference (`C:\Users\madha\Downloads\muesli-main\muesli-main`). Statuses in
`WINDOWS_LAUNCH_LEDGER.md` still govern; this is the current blocker list feeding it.

Severity: **P0** = cannot ship (data loss, privacy, core flow broken, no installable artifact).
**P1** = must fix before a public release. **P2** = should fix.

## Fixed in this review

| ID | Problem | Fix |
|---|---|---|
| P0-ESC | After the first dictation, `_operationCancellation` was never cleared, so `DictationCancellationPolicy.CanCancel` stayed true and the low-level hook swallowed **Escape in every app**. | `StopAsync`'s `finally` releases the token (`WinUiDictationContext.cs`). |
| P0-DISCARD | Right-click/Cancel on the meeting pill called `CancelAsync()` → `DeleteMeeting`, deleting audio **and written notes** without asking. macOS confirms (`discardMeetingWithConfirmation`). | Pill and Meetings page share `WinUiMeetingContext.ConfirmAndDiscardAsync`; the pill raises the dashboard and shows the same dialog. |
| P0-PREWARM | `16c06d8` initialized a WASAPI client at every launch (including startup-task and pre-onboarding launches), before capture was explained. | Pre-warm only after `OnboardingCompleted`; the coordinator re-prepares only once the shell opted in. Prepared-client disposal moved off the MMDevice notification callback and under the capture gate (no stale client after a default-device change). |

Pinned by `LaunchBlockerRegressionTests`. Suite: 1098 passed / 0 failed / 31 skipped.

## Signed installer blockers

| ID | Sev | Problem | Evidence | Needed | Kind |
|---|---|---|---|---|---|
| S1 | P0 | Publisher is `CN=AppPublisher`; `MuesliProductionPublisher` is never consumed, so production signing always fails the subject check. `VersionIdentityContractTests` pins the placeholder. | `Package.appxmanifest:19,26`, `Directory.Build.props:15`, `sign-windows-release.ps1:134-136` | Inject the certificate DN into `Identity/@Publisher` + `PublisherDisplayName` at repack time (`package-winui-msix.ps1` `Rebuild-MsixPayload`); update the test. | Code + decision (legal entity) |
| S2 | P0 | No code-signing certificate. Scripts select by `/sha1` thumbprint only (no Azure Trusted Signing `/dlib`). | `sign-windows-release.ps1:94`, `release-common.ps1:260-328` | Buy OV (token/HSM) or adopt Trusted Signing (script work), or choose the Store. | Purchase/decision |
| S3 | P0 | Windows App Runtime dependency not delivered: not self-contained, no `.appinstaller`, install has no `-DependencyPath`. Clean machines fail to install. | `Muesli.Windows.WinUI.csproj` (no `WindowsAppSDKSelfContained`), `build-signed-release.ps1:85-90`, `install-windows.ps1:33` | `WindowsAppSDKSelfContained=true`, or an `.appinstaller` with the runtime dependency, or the Store. | Code + decision |
| S4 | P0 | The MSIX has never been installed/launched as a package (`0x800B0100` in `artifacts/msix-smoke-report.json`). Developer Mode does not make an unsigned MSIX installable. This dev box is Windows 11 Home (no Sandbox/Hyper-V). | smoke report; `package-winui-msix.ps1:224-228`, `install-windows.ps1:38-53` | Rehearse with a throwaway self-signed `CN=AppPublisher` cert on a clean Win11 Pro VM: install, first run, mic consent, model download, dictation, meeting, startup task, upgrade, uninstall. | Qualification |
| S5 | P1 | Newest local MSIX (2026-09-27) is from a commit not in HEAD's history (177 files differ). CI builds an unsigned MSIX on main but has no signed-release job and skips the two-build comparison. | `artifacts/msix/`, `windows-ci.yml:12-16,96` | Signed-release workflow (or documented manual procedure) from a tagged clean commit. | Process |
| S6 | P1 | `package-winui-msix.ps1 -CertificateThumbprint` signs, then unpacks/repacks (destroying the signature), and still reports `Signed=true`. | `package-winui-msix.ps1:78-82,189-191,218` | Remove the parameter or sign after repack. | Code |
| S7 | P1 | `llama.dll`/`ggml-cpu.dll` import MSVCP140/VCRUNTIME140(_1)/VCOMP140; none are packaged and no VCLibs dependency. Opt-in cleanup fails on machines without the VC++ redist. Notices claim the CRT ships "for the Swift bridge", which does not ship. | objdump of packaged DLLs; `THIRD-PARTY-NOTICES.md:131-137` | Ship the four DLLs app-local (or VCLibs dependency); fix notices. | Code |
| S8 | P1 | `%APPDATA%\muesli` writes are subject to MSIX AppData virtualization (no `unvirtualizedResources`). On a clean install history likely lands in package LocalCache and is deleted on uninstall; a publisher change strands it. Hidden here because `%APPDATA%\muesli` pre-existed. | `MuesliProfilePaths.cs:34`, `Package.appxmanifest:60-69`, `MuesliCleanProfile.cs:10` | Decide: disclose "uninstall deletes history" or opt out of virtualization; verify on the clean VM. | Decision + qualification |
| S9 | P1 | Notices incomplete (Windows App SDK, WebView2, CommunityToolkit.Mvvm, Lato, model licences: Whisper, SenseVoice, Qwen3-ASR, Cohere, Parakeet CC-BY-4.0, cleanup GGUF provenance). No in-app Licenses/Privacy surface. No privacy controller/support contact. QuestPDF Community eligibility undecided while the DLLs ship. | `THIRD-PARTY-NOTICES.md`, `WINDOWS_PRIVACY.md`, `MeetingDocumentWriter.cs:15-16,122`, `CleanupModelCatalog.cs:55-87` | Complete notices; About → Licenses/Privacy; publish privacy/support URLs (confirm `muesli.app`); QuestPDF approve or remove. | Legal/decision + code |
| S10 | P2 | `MinVersion` 10.0.17763 but process loopback needs 19041+ (`Directory.Build.props` says 19045); unused `systemAIModels` capability. | `Package.appxmanifest:31-32,62` | Raise MinVersion, drop the capability. | Code |
| S11 | P2 | Only the outer MSIX is signed; inner EXE/DLLs unsigned. Smart App Control / AV may flag them; a new cert has no SmartScreen reputation. | `sign-windows-release.ps1:86-96` | Record SAC/SmartScreen behaviour on the clean VM; sign inner binaries if needed. | Qualification |
| S12 | P2 | Updater: no URL/key built in (manual updates are the v1 policy); `write-update-manifest.ps1` still takes the WPF-era `-PortablePackagePath` and pins a key that changes on cert renewal. | `SettingsStore.cs:552-554`, `write-update-manifest.ps1:11,119-123` | Keep manual for v1, or replace with `.appinstaller` auto-update. | Decision |
| S13 | P2 | ~220 MB package: the indicator is a second self-contained .NET runtime duplicating NAudio/LLamaSharp/onnxruntime/sherpa. Uninstall leaves multi-GB model caches in `%USERPROFILE%\.cache\muesli`. | package inventory | Document; consider framework-dependent companion. | Code/docs |

### Shortest path to a signed installer

1. Decide channel (sideload + purchased cert vs Microsoft Store) and legal entity (exact DN).
2. Code: S1 publisher injection, S3 runtime delivery, S6, S7, S10.
3. Decide S8, S9 (QuestPDF, notices, privacy/support), S12.
4. Rehearse with a self-signed cert on a clean Windows 11 Pro VM (S4).
5. Sign a tagged clean commit (`build-signed-release.ps1 -CertificateThumbprint … -ExpectedPublisherSubject "<DN>"`), publish MSIX + hashes (+ `.appinstaller`), qualify that exact artifact on a clean VM, record SmartScreen/SAC.

## Product blockers still open

### Dictation (`16c06d8` follow-ups and existing)

| Sev | Problem | Evidence | Fix |
|---|---|---|---|
| P1 | Recording starts even when the selected model is not downloaded; failure appears only after speaking and the audio is discarded. Settings picker lists undownloaded models. macOS blocks at hotkey press. | `DictationCoordinator.cs` `IsModelReady` unused; `NativeTranscriptionClient.cs:253-256`; `SettingsPageViewModel.cs:298`; macOS `MuesliController.swift:10412-10443` | Early return in `WinUiDictationContext.StartAsync` with "Download X in Models"; filter picker by `TranscriptionModelReadiness.IsVerified`. |
| P1 | A stale prepared WASAPI client fails on NAudio's capture thread (`RecordingStopped` → `CaptureFaulted`), which `DictationCoordinator` does not observe: start cue plays, silence is recorded, "No speech detected". | `AudioCaptureService.cs` `StartPreferredSegment` | Subscribe to `CaptureFaulted` and disclose; reopen on demand. |
| P1 | An idle prepared shared-mode client makes other apps' exclusive-mode opens fail (`AUDCLNT_E_DEVICE_IN_USE`, DAWs). Bluetooth guard checks form factor only. Taskbar mic indicator while idle-prepared is **unverified**. | `AudioCaptureService.PrepareAsync` | Verify the indicator on device; add `BTHENUM`/`BTHHFENUM` check; consider releasing after N minutes idle or a setting. |
| P1 | `onCancel` runs `CancellationTokenSource.Cancel()` on the hook thread; continuations can run inside the low-level hook and risk Windows' hook timeout. Hook-thread death (`GetMessage` = -1, timeout removal) is undetected and the UI keeps "Ready · F8". | `WinUiDictationContext.cs:89-95`, `GlobalHotkeyService.cs` `RunHook` | Cancel on the thread pool; raise a faulted event that updates status. |
| P1 | Stopping the onboarding setup test from the pill runs the full `StopAsync`: saved to history and pasted, contrary to `OnboardingStepCatalog.cs:12`. | `WinUiIndicatorHost.cs` Stop → `StopRecordingAsync` | Route Auxiliary sessions to `StopForOnboardingTestAsync`. |
| P1 | Esc in the onboarding window skips setup and cancels downloads (and Esc is the dictation-cancel key). | `OnboardingWindow.xaml.cs:224-235` | Remove binding or confirm. |
| P1 | Pill Stop/Cancel and tray toggle bypass the serialized hotkey chain; an overlapping stop clears `_pasteTarget`, so the transcript is silently not inserted. | `WinUiIndicatorHost.cs:296-309`, `WindowsTrayIconService.cs:83` | Route through `EnqueueHotkeyWork`. |
| P1 | Filler removal deletes real words ("do you know him" → "do him"); macOS removes only comma-delimited fillers. One `[BLANK_AUDIO]` discards the whole transcript while other engine tokens are pasted. No trailing space between consecutive dictations. | `FillerWordFilter.cs:7-13,37` vs macOS `FillerWordFilter.swift:18-21`; `WinUiDictationContext.cs:388-395`; macOS `TranscriptionEngineArtifactsFilter.swift`, `DictationPasteSpacing.swift` | Port macOS rules. |
| P1 | Paste-failure path overwrites the clipboard without restoring it; clipboard snapshot drops custom formats; paste into an elevated window can report success while failing. | `WinUiDictationContext.cs:439-444`, `WinRtClipboardAdapter.cs:14-20`, `ActiveAppPasteService.cs:82-93` | Restore clipboard; compare integrity levels and fall back to copy with disclosure. |
| P1 | Low-memory Cohere → Parakeet fallback is disclosed only in a status the pill hides. (It does revert to the configured model on the next interactive start, `WinUiDictationContext.cs:314`; auxiliary sessions do not.) | `DictationCoordinator.cs:106-114` | Fail closed or make opt-in and show on the pill. |
| P2 | 6 s rolling chunks decode at RTF 0.6–0.8 with backlog on this machine; boundary-split accuracy unqualified. Remaining ~700 ms `captureOpenMs` likely uncached `MMDevice.FriendlyName`/`ID` reads. | app log 2026-10-09/10 | Qualify on the dictation corpus; cache device properties in `PreparedCapture`. |

### Meetings

| Sev | Problem | Evidence | Fix |
|---|---|---|---|
| P1 | The active session is scanned as "recoverable" on every `Changed` (including live snapshots): its journal is rewritten as `RecoverableInterruption`, live WAV repair fails with sharing violations, disk I/O on the UI thread; after pause/rotation it appears under "Recoverable recordings" with Finalize. | `LibraryPageViewModels.cs:1125`, `WinUiMeetingContext.cs:129`, `MeetingSessionJournalStore.cs:233-366` | Exclude the active session id; scan only at startup / after terminal states. |
| P1 | A failed finalize shows as "Paused · audio retained" with Resume; pill/tray/prompt actions are fire-and-forget; "Meeting saved" shows on Failed. | `MeetingRecordingCoordinator.cs:84,218-227`, `WinUiMeetingContext.cs:66`, `LibraryPageViewModels.cs:672-675,1132`, `App.xaml.cs:588-609` | Distinct failed-finalize state with Retry; one error surface (InfoBar/toast). |
| P1 | No sleep/logoff/shutdown handling; parameterless `SuspendAsync` has no caller. | `MeetingRecordingCoordinator.cs:291`, `App.xaml.cs:355-376` | Subscribe to power/session-ending; call suspend/preserve. |
| P1 | Finalization saves a pre-summary snapshot, overwriting notes/title edited during summarization. | `WinUiMeetingContext.cs:449-478`, `MeetingDetailViewModel.cs:190-200` | Re-read before applying; write only generated fields. |
| P1 | Every notes keystroke rewrites the whole library (all meetings' notes/aliases/transcripts) on the UI thread. | `MarkdownNotesEditor.cs:24`, `LibraryPageViewModels.cs:501-504`, `WinUiLibraryContext.cs:75-84`, `SqliteLibraryHistoryAdapter.cs:103-150` | Single-meeting upsert + debounce. |
| P1 | Summary-provider failure silently yields local keyword notes; macOS writes "Summary failed" + transcript. | `MeetingSummaryService.cs:236-246`, `WinUiMeetingContext.cs:466-478`; macOS `MeetingSummaryClient.swift:311-322` | Persist and show the fallback reason. |
| P1 | If the WPF companion is missing/crashed, nothing on screen discloses capture (static tray tooltip, no fallback pill); companion crash before connect is undetected; packager silently skips a missing companion. | `WinUiIndicatorHost.cs:409-447`, `WindowsTrayIconService.cs:54`, `IndicatorPipe.cs:80`, `package-winui-msix.ps1:146-149` | Recording state on tray icon/tooltip; fallback pill/toast; fail packaging without the companion. |
| P1 | Deleting a meeting leaves its retained audio on disk (dialog promises a cleanup that does not exist). | `WinUiLibraryContext.cs:86-96`, `MeetingDetailViewModel.cs:356-358`; macOS `MuesliController.swift:6450-6461` | Delete Muesli-owned files under `captures/recordings/<id>`. |
| P1 | No disk-full handling: writers do not catch write errors or check free space. | `SystemAudioCaptureService.cs:574-595` | Free-space precheck; treat write `IOException` as a channel fault that checkpoints. |
| P1 | Export is full-meeting Markdown only; macOS offers Notes / Transcript / Full (PDF default). No global meeting hotkey (macOS ⌘⇧R). | `MeetingDetailViewModel.cs:327-346`, `ShortcutsPage.xaml:313` | Three export modes; meeting shortcut. |
| P2 | Title/folder not autosaved; detected starts pop the live panel; live-caption failures only logged; no processing stages/completion prompt; recording UI is a list card, default title "Quick Note {date}" vs "Meeting"; detail layout diverges from macOS (inline template/Generate Notes vs More actions). | see meetings review | Parity polish. |

### Indicator, onboarding, settings, design

| Sev | Problem | Evidence |
|---|---|---|
| P1 | Unsaved Settings edits are lost on navigation; macOS saves per control. | `SettingsPageViewModel.cs:210-230` |
| P1 | Meeting waveform reuses a frozen dictation level; saved pill position clamps to the cursor's monitor with wrong mixed-DPI conversion; mic-denied errors never reach the pill; status clipped at 180 px. | `WinUiIndicatorHost.cs:65,230`, `IndicatorWindow.xaml.cs:251,640-685`, `FloatingIndicatorState.cs:31` |
| P2 | Code-behind brushes ignore in-app Light/Dark choice. | `PermissionBrushConverter.cs:18`, `ShortcutsPage.xaml.cs:209-215` |
| P2 | Light-theme token drift vs macOS design-system: accent `#2364C9` vs `#2563EB`, base `#F6F7F9` vs `#FFFFFF`, title 32 SemiBold vs 26 Bold, radius 12 vs 10. Dark is close; High Contrast dictionary present. | `MuesliTheme.xaml` |
| P2 | Thin tray (no recent dictations/model submenu, static tooltip); `--preview-meeting-notification` shows a fake meeting and is not `#if DEBUG`; startup-task failure blocks the whole Settings save. | `WindowsTrayIconService.cs:78-119`, `App.xaml.cs:314-319`, `SettingsPageViewModel.cs:423` |

## Engineering health

- **PRs/branches:** no open PRs. #38 and #39 merged; the #23/#31–#37 stack is closed and superseded. `16c06d8` and this review's fixes need a new PR to main. ~40 stale remote branches; triage `codex/windows-meeting-reliability` (3 unique commits) and `docs/windows-execution-plan` (6, docs) before deleting.
- **CI:** green on main and this branch (`unsigned-release` only). Not gated: `Muesli.Windows.UITests` (last local 17/24), signing, install/launch, two-build reproducibility. Node 20 action deprecation warnings. `PostMeetingAutoExportTests.ConcurrentAutoExportsPublishExactlyOneMarkdownFile` failed twice historically ("Another auto-export still owns the meeting claim"): flake risk.
- **Secrets:** none tracked; `.gitignore` covers certs, env files, artifacts, models, recordings.
- **Dev friction:** the test project passes `-Require` to the Swift bridge stager by default although `shared-core.lock.json` says `bridgeRequired=false`; local builds need `-p:MuesliRequireSwiftBridge=false`. `QualificationFact` requires the env var to name an existing directory (set `MUESLI_HOTKEY_QUALIFICATION` to any directory to run the real-hook test).
- **Stale docs:** `WINDOWS_V1_RELEASE.md` (WPF shell, zip/`Muesli.exe` QA, HKCU Run, update wording contradicts README); ledger line 9 (draft PRs) and PKG-01 title ("zip and Inno"); `WINDOWS_MEETING_SESSION_LIFECYCLE.md` (auto-stop, no waveform); parity matrix FOLLOW-01; ~15 absolute `C:/Users/madha` links across docs.

## Decisions for the release owner

1. Channel: sideload with purchased certificate, or Microsoft Store.
2. Legal entity / publisher DN and certificate type.
3. QuestPDF: approve Community eligibility or remove for v1.
4. AppData virtualization: accept "uninstall deletes history" or opt out.
5. Privacy/support URLs and controller; confirm `muesli.app`.
6. Updates: manual for v1, or `.appinstaller` auto-update.
7. Low-memory Cohere → Parakeet fallback: fail closed or opt-in.
8. Mic pre-warm: keep (after verifying the idle indicator and exclusive-mode impact) or make it a setting.

## Human / physical qualification still required

Clean-VM install/upgrade/uninstall and SmartScreen/SAC; idle taskbar mic indicator with a prepared client; four paste targets incl. elevated; Bluetooth/unplug/default-switch during dictation and meetings; Zoom/Teams/Meet dual capture and detection false positives; sleep/forced-kill/disk-full; dictation WER/CER corpus incl. 6 s chunk boundaries; Narrator/keyboard/High Contrast/DPI 100–200%/multi-monitor; human inspection of an exported support bundle.
