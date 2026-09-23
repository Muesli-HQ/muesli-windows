# Roadmap

Status date: 2026-09-23. See [`PROJECT_STATUS.md`](PROJECT_STATUS.md) for the current verified implementation.

## Completed Foundation

- Native WPF shell, tray, single-instance activation, startup registration, and floating state indicator.
- Global hold-to-talk and double-tap dictation with active-app, clipboard, and history-only delivery.
- Local Whisper worker with bundled CPython packaging, model/language settings, and optional Parakeet/Qwen components.
- Atomic/versioned JSON persistence, protected API keys, dictionary portability, editable meetings, summaries, exports, playback, retention, and hooks.
- Stable meeting detection prompts, explicit end confirmation, process-targeted remote audio with qualified fallback, interruption journaling, truthful partial/failed records, and temporary-audio cleanup.
- Chronological mic/remote transcript merge, persisted timed text segments, explicit diarization status, and microphone recovery with timeline-preserving gaps.
- Windows automated test project; current baseline is 38 passing tests.

## Next: Reliability and Release Qualification

- Add conservative Whisper repetition/hallucination detection without deleting legitimate repeated speech.
- Exercise Zoom, Teams, Meet, and Webex join/leave/rejoin behavior on real calls.
- Qualify Bluetooth changes, device removal, CPU-only systems, supported NVIDIA systems, and long meetings.
- Add broader target-app dictation/paste testing for Notepad, Chromium, Office, and elevated windows.
- Run clean install/update/uninstall and data-preservation qualification for Velopack.
- Obtain an Authenticode certificate and sign the application and installer.

## Next: Meeting Experience

- VAD-driven live chunks, live transcript display, gap recovery, and crash-resumable processing beyond the current journal.
- More robust remote-speaker diarization setup and model lifecycle UX.
- Re-transcription from explicitly retained audio and model provenance.
- Nested folders, richer recording playback, and stronger export golden tests.

## Later: Platform Parity

- Private Insights views and shareable statistics.
- Calendar/upcoming-meeting integration after OAuth, recurrence, dismissal, and token-storage design is complete.
- Cross-device sync after backend, identity, encryption, tombstone, and conflict semantics are selected.
- A stable CLI/agent contract aligned with Windows persistence.
- Native multi-model ASR only after an explicit decision about replacing the shipping Python/Velopack architecture.
- Computer Use only after consent, observation, cancellation, and threat-model review.

## Deferred / Decision-Gated

- ChatGPT subscription OAuth without a supported stable contract.
- Apple-only frameworks and models that have no Windows equivalent.
- Cloud sync or calendar implementations that compromise the local-first privacy model.
