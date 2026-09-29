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

## Health and automatic stop

Each channel reports bounded PCM metrics to `MeetingAudioHealthMonitor` off the
WPF dispatcher. It detects unavailable channels, sustained silence, and
clipping. Silence is only treated as suspicious when the peer channel is
active, which avoids labelling an ordinary quiet meeting as capture failure.
Warnings identify the affected channel without transcript text, window titles,
URLs, or local paths.

Manual recordings never auto-stop. A recording started from meeting detection
can auto-stop only after its exact source was observed during recording and is
then absent for at least 20 seconds and three observations. Other visible
meeting sources do not satisfy the match.

## Interruption and ownership

Every session is journaled under
`%APPDATA%\muesli\captures\in-progress\<session-id>\session.json`. Capture
parts use session-relative paths and atomic copies. On suspend, shutdown,
capture exhaustion, cancelled finalization, or a finalization failure after
audio exists, Muesli checkpoints the available tracks and records
`RecoverableInterruption`.

On startup, the journal store discovers nonterminal sessions, adopts live WAV
files, repairs their RIFF/data sizes when possible, rejects path traversal, and
offers **Recover interrupted** in Meetings. Recovery combines normalized
16-kHz mono parts and reruns the selected final-meeting transcription path.
Header-only and sub-100 ms tracks are treated as missing channels and never
reach native ASR or diarization. Recovery never invents a completed record
from empty or corrupt input.

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

Automated tests cover every declared transition plus invalid transitions,
health thresholds, repair/auto-stop policies, route classification, WAV
repair/adoption, migration, path rejection, track selection, and process
loopback capability gating. These tests do not replace physical qualification
with Zoom, Teams, and Meet; Bluetooth/default-route changes; device removal;
suspend/resume; forced interruption; real remote speakers; and playback on the
supported Windows hardware matrix. `PHASE3_QUALIFICATION.md` defines the
fail-closed manual evidence.
