# Roadmap

Authoritative status: [`docs/WINDOWS_LAUNCH_LEDGER.md`](WINDOWS_LAUNCH_LEDGER.md).
Capability IDs: [`docs/WINDOWS_MACOS_PARITY_MATRIX.md`](WINDOWS_MACOS_PARITY_MATRIX.md).
Multi-agent execution and ownership plan: [`docs/WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md`](WINDOWS_MULTI_AGENT_LAUNCH_PLAN.md).
This file is a short remaining-work view. It is not a second status matrix.

## Native launch (implemented)

- Seven pinned offline sherpa-onnx choices; independent Dictation and Final meeting/import roles; prepare does not activate a recognizer.
- Opt-in Nemotron 3.5 live meeting role, default Off.
- Native Qwen/GGUF cleanup through LLamaSharp when a GGUF is placed in cache; disabled by default; no guided download yet.
- Dual-track meeting capture, session journal, recovery, in-app playback, diarization, speaker aliases.
- Dictation hold/hands-free, paste, filler filter, personal dictionary.
- Detection prompts with Join & Record / Join Only / Record Only; detected auto-stop; no calendar source.
- Manual notes, templates, local/OpenAI/OpenRouter/Ollama summaries, title ownership.
- Import of wav/mp3/m4a/aac/mp4/mov/mkv/webm with cancellation; ogg rejected with guidance.
- Manual Markdown/PDF export; optional post-meeting hook and auto Markdown export.
- Resumable onboarding, tray from real history, themes, startup-at-login, Computer Use (disabled by default).
- Configurable dictation/model feedback sounds with speaker-route and setup-session suppression.
- Wave 0 integration suite: 634 expanded cases on .NET 10; 630 pass and 4 real-fixture qualification tests are explicitly skipped when their prerequisites are absent.

## Remaining implementation

Map every item to the launch-ledger module. Do not start excluded work.

| Module | Work |
|---|---|
| Phase 1 | Guided Qwen cleanup download after an approved GGUF (MOD-05). |
| Phase 2 | Media ducking stays a product decision (AUD-03). |
| Phase 5 / 7 | Transcript editing and safe retranscribe (MTG-03 remainder). |
| Phase 8 | Nested folders; search indexing of manual notes; optional playback waveform. |
| Phase 9 | Automatic PDF export is implemented but release-gated on the QuestPDF eligibility decision (EXP-01); follow-up workflows only after a destination contract. |
| Phase 12 | Local insights analyzer if approved without contribution telemetry. |
| Phase 13 | App-side updater workflow is implemented and fail-closed; production signing and signed install/upgrade evidence wait on decision D5. Version-matched CUDA-provider packaging and NVIDIA hardware qualification. |

## Remaining qualification (not missing features)

- Phase 2 human dictation corpus and four paste targets (`PHASE2_DICTATION_QUALIFICATION.md`).
- Phase 3 physical Zoom/Teams/Meet, Bluetooth/route, suspend/recovery (`PHASE3_QUALIFICATION.md`).
- Phase 4 long live meeting, CUDA-live, multilingual review.
- Phase 5 multi-speaker diarization quality.
- Phase 6 live detection matrix.
- Phase 7 live summary providers.
- Phase 8 per-format import ASR/diarization and human export open.
- Phase 12 DPI/multi-monitor/clean-profile onboarding.
- Phase 13 signed package, clean-VM install/upgrade/uninstall.

## Excluded from this launch

Google Calendar/OAuth, Microsoft Store, ChatGPT subscription OAuth, CloudKit/iPhone sync, audio sync, ARM64, and literal macOS framework or model ports.
