# Phase 3 meeting-session qualification

Phase 3 is not accepted by a build or by synthetic state tests alone. A human
reviewer must exercise the rebuilt WPF app with real meeting applications and
hardware, then record evidence that can be checked by
`scripts\qualify-meeting-session-lifecycle.ps1`.

## Required environment

- Rebuilt Debug or packaged Windows executable and its SHA-256.
- Selected final-meeting model and actual CPU/CUDA provider identity.
- Zoom, Microsoft Teams, and Google Meet, each with a real remote speaker.
- A built-in or USB microphone plus a Bluetooth headset.
- Permission to change the default input/output routes and remove one test
  device during recording.
- A human reviewer who is not generating reference transcripts with a model.

Do not use synthetic transcript text, fake meetings, or a fabricated pass
report. Network-backed summaries are outside this phase and should remain
local/disabled during qualification.

## Scenarios

Each report must identify the app/build, meeting platform, selected model,
runtime provider, microphone/render endpoints, system capture mode, timestamps,
final state, retained-audio choice, and reviewer. Record only operational facts;
do not copy transcript content, window titles, meeting URLs, or meeting codes
into reports or logs.

1. **Baseline dual capture** — record at least five minutes with local and
   remote speech; verify separate microphone/system tracks and no unrelated
   endpoint audio when process-tree capture is active.
2. **Endpoint fallback** — force or use a case where process capture is not
   available; verify the UI explicitly warns that endpoint loopback may include
   unrelated system audio.
3. **Microphone route change** — change the default microphone or reselect a
   device while recording; verify bounded rotation/recovery and continuous
   retained audio.
4. **System route change** — change the render endpoint; verify the system
   channel rotates/restarts without relabelling it as microphone audio.
5. **Bluetooth transition** — connect/disconnect the headset or change its
   Windows profile; verify a visible warning, repair, and usable track parts.
6. **Device removal/degraded mode** — remove one channel while the other is
   active; verify `DegradedRecording`, missing-channel disclosure, and a usable
   final or recoverable result. Exhaust both channels and verify recovery, not
   fake completion.
7. **Silence and clipping** — create qualified peer-active mic silence and a
   clipped source; verify channel-specific warnings. Ordinary two-channel
   silence must not produce a false capture-failure warning.
8. **Suspend/resume** — suspend Windows during capture and resume; verify audio
   is checkpointed, resumed into new parts, and finalizes without losing the
   retained pre-suspend portion.
9. **Conservative auto-stop** — confirm manual recording never auto-stops.
   For a detected source, confirm another meeting window cannot stop it and the
   exact source must first be observed, then remain absent for the grace period.
10. **Forced interruption recovery** — terminate the process during active
    capture, relaunch, use **Recover interrupted**, and verify WAV repair plus a
    completed or visibly failed/recoverable record. Empty data must not become a
    meeting.
11. **Cancellation/failure** — cancel recording and cancel finalization; verify
    cancellation removes session-owned temporary files while interrupted
    finalization retains usable parts for recovery.
12. **Playback and ownership** — with retention enabled, play/pause/seek both
    tracks, switch meetings, and close the app; verify file handles are released.
    With retention disabled, verify session audio is removed only after durable
    meeting persistence. Deleting a retained recording must follow the existing
    user-confirmed meeting deletion flow.

## Gates

- Every scenario is human-marked `Passed: true` and has nonempty evidence notes.
- The final state is truthful (`Completed`, `Failed`, `Cancelled`, or
  `RecoverableInterruption`) and matches the persisted meeting/journal.
- Process-tree and endpoint-fallback runs state their actual capture mode.
- Fresh log slices contain zero `ERROR`, `Unhandled UI exception`, and
  `XamlParseException`, and the reviewer confirms no transcript, title, URL,
  meeting code, or local audio path leakage.
- Playback releases files and devices after meeting switch and shutdown.
- Zoom, Teams, and Meet baseline reports all pass; the Bluetooth, physical
  device, suspend/resume, forced interruption, and ownership scenarios pass.

If any gate is missing, the checker fails closed and Phase 3 remains
implemented but insufficiently verified.
