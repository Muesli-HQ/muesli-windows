# Windows meeting session lifecycle

Phase 3 gives recorded meetings one explicit lifecycle and one durable audio
owner. It ports the observable macOS behavior while using Windows audio and
power APIs; it does not port CoreAudio, ScreenCaptureKit, or AVFoundation
types.

## State contract

`MeetingSessionStateMachine` is the authority for these states:

| State | Meaning |
|---|---|
| `Idle` | No meeting session is owned. |
| `Preparing` | The journal exists and microphone/system capture are starting concurrently. |
| `Recording` | Both requested channels are healthy. |
| `DegradedRecording` | One channel is unavailable or a health warning is active; the healthy channel remains owned. |
| `Stopping` | Capture is being checkpointed and stopped. |
| `Finalizing` | Durable tracks are being normalized, transcribed, diarized, merged, and persisted. |
| `Completed` | A non-empty final transcript and meeting record were durably saved. |
| `Failed` | The operation reached a terminal failure that cannot be represented as success. A no-speech result is `Failed`. |
| `Cancelled` | The user cancelled and Muesli attempted to remove session-owned temporary audio. |
| `RecoverableInterruption` | Audio parts or live WAV data are retained for an explicit recovery attempt. |

Invalid transitions throw in the state-machine layer. Transition history is
bounded to 64 redacted operational entries. The coordinator serializes start,
stop, finalization, suspend/resume, cancellation, recovery, and shutdown so a
late operation cannot dispose resources still in use.

## Capture and routing

Microphone and meeting audio start concurrently and remain separate tracks.
The microphone path uses the configured capture endpoint and follows endpoint
notifications. The system path first attempts Windows process-tree loopback
when a detected meeting supplies a live process ID and the OS supports process
loopback (Windows build 20348 or newer). That mode includes the target process
and its child processes.

If targeted activation is unsupported, no process is available, or activation
fails, Muesli explicitly reports that it is using endpoint loopback. Endpoint
loopback can contain unrelated sounds played through the selected Windows
render endpoint; it is not presented as process-isolated capture. There is no
cross-engine transcription fallback.

Default endpoint changes, device removal, and Bluetooth/profile transitions
rotate the affected channel into a finalized part before reopening it. Repair
is bounded to three attempts with delays of 0, 2, and 5 seconds. When repair is
exhausted, the other channel may continue in degraded mode; if neither channel
is available, captured parts are checkpointed as recoverable.

## Health and signal-loss warning

Each channel reports bounded PCM metrics to `MeetingAudioHealthMonitor` off the
UI dispatcher. It detects unavailable channels, sustained silence, and
clipping. Silence is only treated as suspicious when the peer channel is
active, which avoids labelling an ordinary quiet meeting as capture failure.
Warnings identify the affected channel without transcript text, window titles,
URLs, or local paths.

Manual recordings never track a detected source. A recording started from
meeting detection warns only after its source is absent for at least 20 seconds
and three scans. If speech was recognized, at least 45 seconds must also have
passed since the last transcript activity. The warning says recording continues;
only an explicit Stop action ends capture.
Dismissal suppresses the warning for that recording.

Source tracking accepts the original detection key or a newly confirmed
meeting from the same platform. For example, a Zoom join link in Chrome and
the resulting Zoom desktop window have different keys but are one continuous
meeting. A different platform does not satisfy that match.

## Interruption and ownership

Every session is journaled under
`%APPDATA%\muesli\captures\in-progress\<session-id>\session.json`. Capture
parts use session-relative paths and atomic copies. On suspend, shutdown,
capture exhaustion, cancelled finalization, or a finalization failure after
audio exists, Muesli checkpoints the available tracks and records
`RecoverableInterruption`.

When Meetings loads, the journal store discovers nonterminal sessions, adopts
live WAV files, repairs their RIFF/data sizes when possible, rejects path
traversal, and offers **Finalize** for recoverable recordings. Recovery combines
normalized 16-kHz mono parts and reruns the selected final-meeting transcription
path.
Header-only and sub-100 ms tracks are treated as missing channels and never
reach native ASR or diarization. Recovery never invents a completed record
from empty or corrupt input.

The selected final-meeting model must be downloaded and verified before a new
recording opens its audio channels. A detected-meeting Start action opens Models
with an actionable warning if the model is missing. During recovery, a failed
finalization retains the adopted microphone and system parts for another
explicit Finalize attempt.

When **Save meeting recording** is enabled, finalized `microphone.wav` and
`system.wav` files live under
`%APPDATA%\muesli\captures\recordings\<meeting-id>\` and are owned by the
persisted meeting. When it is disabled, finalization uses session-owned files
and removes them only after the meeting record has been saved. Imported source
media remains user-owned.

The meeting detail surface has an in-process NAudio player with microphone and
system-track selection, play/pause, seek, and deterministic close on meeting
switch, refresh, and app shutdown. Waveform rendering is not part of this
bounded phase.

## Qualification boundary

Meeting-end suggestions (7 November 2026) remain advisory. The detector follows the
recording's own window and audio sessions before foreground candidates, including
minimized dedicated meeting clients. Missing-source prompts allow a minute for a
reconnect and require 45 seconds without audio or transcript activity.

Manual and detected recordings also evaluate a bounded tail of committed English
transcript segments locally. Two different closing cues (wrap-up, group thanks,
farewell), fresh audible activity, and 25 seconds of subsequent quiet can suggest
finishing after the first two minutes. Questions, continued discussion, stale or
backlogged text, capture stalls, partial text, and isolated thanks/goodbyes do not
establish an ending. This deliberately conservative phrase vocabulary is not a
multilingual semantic classifier or an authoritative Zoom connection-status API.

The existing non-activating card says **All done?** / **Recording is still on.**
with **Stop & save**. It has no countdown bar, fades after 15 seconds (hover pauses
the timeout), and closes if activity resumes. Either end prompt is offered at most
once per recording; ignoring or dismissing it continues capture. Pause/resume
preserves suppression. Only an explicit Stop action saves and ends capture.

Automated tests cover every declared transition plus invalid transitions,
health thresholds, repair/auto-stop policies, route classification, WAV
repair/adoption, migration, path rejection, track selection, and process
loopback capability gating. These tests do not replace physical qualification
with Zoom, Teams, and Meet; Bluetooth/default-route changes; device removal;
suspend/resume; forced interruption; real remote speakers; and playback on the
supported Windows hardware matrix. `PHASE3_QUALIFICATION.md` defines the
fail-closed manual evidence.

On 27 October 2026, a packaged production-profile Zoom recording exposed a
Chrome-join-link to native-Zoom key change. Its interrupted microphone and
system WAV files were adopted by recovery, then successfully transcribed and
saved after the selected Parakeet v3 model was verified and long-audio
inference was bounded. Both retained playback tracks and a nonempty transcript
were confirmed in the saved record. This verifies that recovery path on this
machine; a fresh Zoom join still needs a live check of the warning behavior.
