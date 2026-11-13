# Roadmap

Authoritative status: [`docs/WINDOWS_LAUNCH_LEDGER.md`](WINDOWS_LAUNCH_LEDGER.md).
Capability IDs: [`docs/WINDOWS_MACOS_PARITY_MATRIX.md`](WINDOWS_MACOS_PARITY_MATRIX.md).
Multi-agent execution and ownership plan: [`docs/WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md`](WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md).
This file is a short remaining-work view. It is not a second status matrix.

## Native launch (implemented)

- Pinned offline sherpa-onnx choices; independent Dictation and Final meeting/import roles; prepare does not activate a recognizer.
- Opt-in Nemotron 3.5 live meeting role, default Off.
- Native Qwen/GGUF cleanup through LLamaSharp when a GGUF is placed in cache; disabled by default; no guided download yet.
- Dual-track meeting capture, session journal, recovery, in-app playback, diarization, speaker aliases.
- Dictation hold/hands-free, paste, filler filter, personal dictionary.
- Detection prompts with Join & Record / Join Only / Record Only; detected auto-stop; no calendar source.
- Manual notes, templates, local/OpenAI/OpenRouter/Ollama summaries, title ownership.
- Import of wav/mp3/m4a/aac/mp4/mov/mkv/webm with cancellation; ogg rejected with guidance.
- Manual Markdown/PDF export, optional post-meeting hook, and automatic Markdown/PDF export.
- Resumable onboarding, tray from real history, themes, startup-at-login, Computer Use (disabled by default).
- Configurable dictation/model feedback sounds with speaker-route and setup-session suppression.
- The .NET 10 product and Store package suites run in CI; see the current build report rather than the historical Wave 0 counts below.

## Remaining implementation

Map every item to the launch-ledger module. Do not start excluded work.

| Module | Work |
|---|---|
| Phase 1 | Guided Qwen cleanup download after an approved GGUF (MOD-05). |
| Phase 3 | Chunk long capture and final WAV output before the 32-bit RIFF size limit. The retired WPF branch segmented capture, but its writer cannot be copied into the current WinUI finalization pipeline without handling the combined output too (MTG-01). |
| Phase 8 | Search indexing of manual notes and optional playback improvements. |
| Phase 9 | External follow-up workflows after a destination contract. |
| Phase 13 | Version-matched CUDA-provider packaging; Store qualification and listing work are tracked below. |

## Remaining qualification (not missing features)

- Phase 2 human dictation corpus and four paste targets (`PHASE2_DICTATION_QUALIFICATION.md`).
- Phase 3 physical Zoom/Teams/Meet, Bluetooth/route, suspend/recovery (`PHASE3_QUALIFICATION.md`).
- Phase 4 long live meeting, CUDA-live, multilingual review.
- Phase 5 multi-speaker diarization quality.
- Phase 6 live detection matrix.
- Phase 7 live summary providers.
- Phase 8 per-format import ASR/diarization and human export open.
- Phase 12 DPI/multi-monitor/clean-profile onboarding.
- Phase 13 Store certification and clean-VM install/upgrade/uninstall.

## Excluded from this launch

Google Calendar/OAuth, ChatGPT subscription OAuth, CloudKit/iPhone sync, audio sync, ARM64, and literal macOS framework or model ports.
