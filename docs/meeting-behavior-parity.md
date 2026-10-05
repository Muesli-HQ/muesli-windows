# Meeting behavior parity audit

Audited on 30 September 2026 against the local macOS reference at `C:\Users\madha\Downloads\muesli-main\muesli-main`. This is a source-level comparison of macOS and a source-level plus real packaged-app check of Windows. The macOS application was not run on this Windows machine.

## What macOS does

An active meeting has **Notes** and **Live** tabs. Notes is the initial selection. You can type your own notes while recording and retain them separately from the recognized speech and generated summary. Switching tabs or navigating elsewhere does not discard either view's state. Live shows the microphone as **You** and captured meeting audio as **Others**, including partial speech when the selected backend supports it, and supports copying speech.

Ordinary meeting models also produce captions during recording: they do not require the optional streaming model. macOS rotates audio at speech boundaries, processes completed chunks, and flushes the last chunk on stop. Its capture pipeline includes neural echo cancellation. Streaming and final-transcript ownership vary by backend.

Manual Quick Note starts open the meeting document in front. Accepting an automatically detected meeting can start with a background indicator. Losing a meeting's window or media signal does **not** authorize stopping capture. macOS waits for a missing-source grace period and a 45-second transcript quiet period, then warns that recording continues and offers an explicit stop action. Dismissing the warning keeps recording.

Typed notes inform summary and title generation. Discarding a recording with notes offers Keep Notes, Delete Draft, and Cancel. Keeping notes produces a notes-only meeting without inventing recognized speech.

Primary reference files:

- [MeetingDetailView.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MeetingDetailView.swift:9): recording tabs, rich notes editor, save behavior, and resume controls.
- [LiveTranscriptView.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/LiveTranscriptView.swift:1): channel presentation, copy, partials, and scrolling.
- [MeetingSession.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MeetingSession.swift:188): audio chunks, VAD, echo cancellation, pause, stop, and finalization.
- [MeetingStartPresentation.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MeetingStartPresentation.swift:1): foreground notes versus background indicator.
- [MeetingCandidateResolver.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MeetingCandidateResolver.swift:1): app, URL, media, and calendar evidence.
- [MuesliController.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MuesliController.swift:9241): signal-loss warnings and explicit actions.
- [MeetingSummaryClient.swift](C:/Users/madha/Downloads/muesli-main/muesli-main/native/MuesliNative/Sources/MuesliNativeApp/MeetingSummaryClient.swift:391): written notes as first-class summary context.

## Windows flow and changes

Windows meeting detection scans native windows and supported browser URLs, corroborates them with Windows microphone/camera evidence, and passes the observations through a shared candidate resolver. A confirmed candidate prompts the user; accepting starts the capture coordinator. The coordinator records microphone and system audio independently, journals recoverable sessions, and runs the final transcription pipeline when explicitly stopped. Process-targeted system capture is used when available; otherwise the existing endpoint-loopback fallback can include other system sounds. SQLite remains the authoritative Windows library.

| Behavior | Before this update | Current implementation |
|---|---|---|
| Recording-time notes | No editor in the recording workspace | Notes/Live selector, editable notes while recording or paused, automatic save and explicit retry |
| Default model captions | Live text required an optional streaming model | Selected meeting model decodes native Silero speech segments during capture; optional streaming remains available |
| Speaker display | Short plain-text preview | Scrollable You/Others captions, partials, copy all and copy individual caption |
| Navigation and pause | Recording notes absent; paused Stop rejected by coordinator | Notes restored when returning to Meetings; Stop can finalize a paused recording |
| Final notes persistence | Captured result could overwrite the active draft | Finalization/recovery merge preserves manual notes, manual titles, folder, and speaker aliases |
| Summary context | Only recognized transcript supplied | Written notes supplied separately as high-priority context to every existing provider; local fallback also receives them |
| Title context | First substantive transcript sentence | Written notes take priority; manually chosen titles remain authoritative |
| Discard with notes | Immediate discard | Keep notes / Delete draft / Cancel; notes-only persistence contains no transcript or audio |
| Lost detection signal | Automatically stopped recording | Warns and continues; stopping requires the user's action; dismissal is respected |
| Idle desktop meeting client | Could qualify solely from the process | Requires attributed input and output activity, or foreground camera and microphone evidence |
| Candidate identity | Included mutable window title | Uses meeting URL or process identity, so title changes do not create a new meeting |
| Window presentation | Floating live transcript appeared automatically | Manual Quick Note opens the dashboard; detected recording keeps its background presentation; compact transcript appears beside the pill when the hover preference is enabled; an explicit action also opens it |
| Live scrolling | Rebuilt/scrolled on every partial update | Committed captions trigger scrolling; floating preview also scrolls when a partial first appears |

Implementation entry points:

- [MeetingsPage.xaml](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.WinUI/Pages/MeetingsPage.xaml:175)
- [LibraryPageViewModels.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.WinUI/ViewModels/LibraryPageViewModels.cs:501)
- [WinUiMeetingContext.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.WinUI/Services/WinUiMeetingContext.cs:65)
- [MeetingRollingTranscriber.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.Platform/Services/MeetingRollingTranscriber.cs:1)
- [MeetingCaptureSession.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.Platform/Services/MeetingCaptureSession.cs:101)
- [MeetingCandidateResolver.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.Core/Services/MeetingCandidateResolver.cs:85)
- [MeetingCapturePolicies.cs](C:/Users/madha/projects/muesli/windows-native/Muesli.Windows.Core/Services/MeetingCapturePolicies.cs:91)

The default-model preview uses the existing native Silero detector, with 0.5 seconds of trailing silence and a 15-second maximum speech segment. Silent input never reaches the preview recognizer. The small, pinned VAD model is prepared independently of the optional streaming recognizer; failure falls back to the earlier quiet-boundary chunks with a visible capture warning. A single decoder processes four queued chunks, and a slow recognizer can skip preview chunks without dropping retained audio. Stop flushes the last speech segment. Preview text never replaces the final transcript owned by the retained-audio pipeline. The existing unified streaming mode continues to own its final transcript and gap recovery; fallback preview text is not incorrectly checkpointed as that mode's authoritative transcript.

## Additional non-calendar parity work

Following the user's request to exclude calendar integration, Windows now supports these additional behaviors:

- **Resume a completed meeting:** More actions → Resume recording reopens the same document, independent of its age. Stop appends new speech with the macOS `— Resumed —` separator and adds the captured duration. An empty recognition result preserves the existing transcript, title, and summary. Written notes, manual titles, folder, and aliases remain intact. Discard removes only the appended capture.
- **Recover a resumed capture:** journal schema 4 stores an immutable original-document baseline. Recovery builds the document from that baseline rather than repeatedly appending to an already saved result. A deleted original is never silently recreated. The recovery panel also offers Discard, preserving saved writing and the original meeting.
- **Retained audio:** appended capture uses separate filenames under the original meeting's recordings directory. Recognition consumes only new audio; afterward, retained playback tracks combine the prior and new audio without modifying the original files. Playback also exposes the retained source tracks. Owned-audio validation recognizes these generated filenames.
- **Follow-up meetings:** More actions → Start follow-up creates a new linked meeting, inherits the predecessor's folder, and avoids stacking `Follow-up:` title prefixes. Existing SQLite follow-up records store the link, so there is no database schema change or shadow store. Related meetings are navigable from detail. Previous generated notes are capped at 6,000 characters and supplied to summary providers as background context, separately from current speech and current written notes.
- **Notes formatting:** recording and saved-document notes have Bold, Italic, Heading, Bullets, and Checklist commands. Bold/italic also support keyboard shortcuts. Changes preserve Markdown and whitespace, and recording notes continue to autosave.
- **Generated titles:** each existing configured summary provider can generate a concise title with written notes taking priority. Manual titles bypass generation. Missing/unavailable providers or unusable title responses retain the deterministic local fallback.
- **Browser detection:** unlisted browser meetings can qualify through attributed microphone activity, including browser renderer processes. This matches macOS's generic browser fallback. Native clients use attributed input/output activity, with the existing foreground microphone/camera alternative. Generic fallback does not invent a join URL or require an expensive background URL walk.

Continuation and formatting policies live in Core; capture integration remains in Platform. These macOS policies live in the app target, not in the portable MuesliCore package, so there is no canonical shared Swift ABI implementation being bypassed.

## Meeting UI parity follow-up — 30 September 2026

Compared `FloatingMeetingTranscriptPanel.swift`, `LiveTranscriptView.swift`, `MarkdownRichTextEditor.swift`, and the recording/document paths in `MeetingDetailView.swift` against the shipping WinUI screens.

- The floating transcript uses the reference 360 × 320 DIP size, 42 DIP header, raised surface, 8 DIP outer corners, 6 DIP bubbles, and 13/10 DIP body/speaker typography. Normal capture diagnostics stay in the dashboard; a compact accessible warning icon exposes the full warning in the panel.
- Indicator hover honors the existing preference, positions the panel beside the pill, preserves dashboard focus, supports moving the pointer into the transcript, and hides after leaving both surfaces. Screen-frame hit testing handles missed pointer events in inactive WinUI windows. Clicking the header or a bubble opens recording notes; dismiss and copy remain separate actions. Explicit opening remains available when hover is disabled.
- Dashboard, floating, and saved-document transcripts share the speaker-bubble feed. Partial captions use italic secondary text and dashed outlines; copy buttons are available on hover and keyboard focus. Copy includes current partials in Others/You order without inserting “partial” labels. Clipboard failures stay visible, and successful copy shows a checkmark for 1.2 seconds.
- Committed messages append instead of recreating the feed. Native ListView containers virtualize layout; partial revisions do not force scrolling until a partial first appears. Bubbles are constructed once per utterance; a compiled item template is the documented upgrade if very long histories stress memory.
- Recording and saved manual notes use a native RichEditBox that displays headings, emphasis, code, links, bullets, and checklists while persisting Markdown. Native selection, toolbar actions, keyboard shortcuts, and undo remain available. Typed Markdown promotes inline; Enter continues a list, and Enter on an empty item exits it. Saved manual notes now autosave across navigation.
- Notes-only and failed documents show their written-notes editor without an empty generated-summary section. Completed summaries render Markdown. Saved transcripts have a bubble reading view and a separate editor. Resumed recording shows previous notes above the editor and includes the previous transcript in its dashboard Live feed.

Verification: zero-warning/error x64 WinUI build; 494 meeting/indicator/capture checks passed, one optional native streaming qualification check skipped. The final parser change also passed its focused check. Packaged production-profile UI checks exercised rich editing, selected formatting and native undo, exact Markdown persistence across navigation/relaunch, checklist continuation/exit, speaker bubbles and timestamps, copy controls, hover placement/focus/exit, and light/dark appearance. Original appearance and hover preferences were restored, smoke meetings were removed through the UI, and the dashboard was left open. macOS runtime and acoustic parity remain outside this Windows/source comparison.

Review checkpoint: the meeting behavior, UI, dictation capture, and indicator changes are included in the October 2026 publication checkpoint. Local recordings, screenshots, test outputs, and personal profile files are excluded from Git.

## Differences that remain

This update closes the core recording, live-view, written-notes, and detection-safety gaps. It does **not** establish complete parity across every meeting feature.

| Remaining difference | Practical effect |
|---|---|
| Neural echo cancellation and exact speech-detector tuning | Windows now uses native VAD for default-model captions, but does not run macOS's neural echo-cancellation pipeline. Acoustic quality and exact chunk timing remain unproven across platforms. |
| Calendar and Contacts integrations | Calendar integration is explicitly excluded by the user. EventKit/Contacts-derived participants and account-linked context are not implemented. |
| Platform-specific detection | Windows attribution depends on audio-session/process evidence and browser URL accessibility. FaceTime is macOS-specific; runtime evidence differs by OS. |

The local source comparison establishes intent and control flow. It cannot establish acoustic/model-quality parity, test macOS runtime presentation, or prove a real multi-person remote call's microphone/loopback behavior on every Windows audio driver.

## Validation

- WinUI x64 build succeeds with zero compiler warnings and errors.
- Meeting, detection, lifecycle, recovery, notes, finalization, export, capture privacy, text-processing fallback, continuation, and rolling-transcription checks: **494 passed, 1 skipped**. The skipped check requires an optional native streaming qualification artifact that was not supplied.
- Real packaged production-profile check verified default-model live speech before Stop, Notes/Live switching, writing retained across navigation, editing while paused, Stop while paused, and writing retained in the saved meeting.
- Additional packaged-app checks verified native VAD-backed captions, formatting selected writing, reopening a completed meeting, discarding only resumed capture, saving resumed capture into the same ID with accumulated duration, follow-up link persistence and related-meeting navigation, and resumed capture recovery after relaunch. The original manual title and latest written notes survived recovery. Test entries and their link were removed through the UI, restoring the original 217 dictations and 7 meetings; their recordings were archived outside the production capture directory.
- Additional focused checks verify bounded preview backlog, cancellation of in-flight publications, final audio tails, speaker ordering, note/title preservation, notes-only SQLite round trips, summary-provider request context, and explicit-stop notification semantics.
- Build environment: the repository pins SDK 10.0.400, while this machine has 10.0.401. The prescribed build was run with the absolute project path from outside the repository's SDK-pinning directory. No SDK pin or dependency was changed.
- The build reports that the shared Swift package is unavailable and uses the existing parity-tested managed text fallback. Native bridge qualification is therefore not established by this run.

The production profile initially rejected notes saves because its SQLite notes/search pages failed integrity checks. With the user's explicit approval, the database was repaired from a snapshot taken by Muesli itself. Every authoritative row was compared before replacement: **244 rows, including 217 dictations and 7 meetings**, were retained exactly. The derived search index was rebuilt, integrity and foreign-key checks passed, and an original backup remains at `%APPDATA%\muesli\data\muesli.db.before-actual-approved-repair-20260930`. No permanent repair code was added to the application.

The first repair attempt was redirected by Windows into the command process's private file cache. A comparison inside Muesli caught the different dictation records and rejected that candidate before touching the actual library. The final repair used Muesli's actual file and passed the same comparison. The obsolete command-cache database was archived so subsequent verification reads the real profile.

The packaged application then saved notes successfully, retained a **Notes only** meeting after discarding audio, and reopened that entry with the exact written text after a restart. SQLite confirmed no transcript or audio references on that entry. The smoke entry was deleted through the UI after verification, and the identified test recordings were archived outside the profile. The library again contains the original 217 dictations and 7 meetings.

The publication checkpoint also includes the accumulated dictation work: rolling capture transcription, model warm-up and idle release, low-memory fallback, RMS-driven indicator animation, and the single WPF indicator renderer. Calendar integration remains excluded.

## Publication checkpoint — 2 October 2026

- Reviewed and committed the accumulated meeting, dictation, and indicator changes as `d1b187b` on `codex/meeting-dictation-parity`, based on the current `main`. The earlier WinUI migration is already merged and is not repeated in this PR.
- Pushed the branch and opened [PR #39](https://github.com/Muesli-HQ/muesli-windows/pull/39). Next action: review the PR and its CI results before merging. No merge was performed.
- Full Windows test suite: **1,095 passed, 30 skipped, 0 failed**. Optional native Swift/streaming, real-media, and operator qualification fixtures were unavailable. The shared Swift bridge remains unqualified; the existing managed fallback is active.
- WinUI x64 build and Debug MSIX build passed with zero compiler warnings/errors. MSIX payload smoke passed with `-SkipLaunch`; signed installation was not tested.
- Separately relaunched the packaged development app in the production profile. Verified the visible dashboard, standalone white indicator mark, compact 44 × 28 and hover 220 × 36 DIP bounds, and zero fresh startup errors. Existing recordings, screenshots, private profile data, and local test outputs remain excluded from Git.
