# Muesli Windows execution plan (historical)

Plan originally dated 2026-08-01. **Status and remaining work now live in** [`WINDOWS_LAUNCH_LEDGER.md`](WINDOWS_LAUNCH_LEDGER.md).

This file keeps the engineering rules and the old P0–P8 numbering so historical commits remain intelligible. Do not use P0–P8 as current module IDs. The launch program is Phase 0–13; Phase 5 is meeting finalization, not calendar.

## Current authority

| Need | Document |
|---|---|
| Status of every capability | `WINDOWS_LAUNCH_LEDGER.md` |
| Capability IDs and macOS mapping | `WINDOWS_MACOS_PARITY_MATRIX.md` |
| Short remaining work | `ROADMAP.md` |
| Model catalog | `WINDOWS_TRANSCRIPTION_MODELS.md` |

## Module remapping (old P* → launch Phase)

| Old ID in this file | Current launch module | Notes |
|---|---|---|
| P0 | Phase 0 | Inventory |
| P1 | Phase 1 | Models |
| P2 | Phase 2 | Dictation |
| P3 | Phase 3 | Capture/lifecycle only |
| P4 | Phase 4 | Live transcription |
| P5 in later patches (“finalization”) | Phase 5 | Timeline/diarization merge |
| P5 “detection/calendar” | Phase 6 | Calendar/OAuth is now **excluded** |
| P6 summaries/hooks | Phase 7 + Phase 9 | Split |
| P3 import/export leftovers | Phase 8 | |
| P7A | Phase 10 | Computer Use |
| P7B | Phase 11 | Sync; CloudKit excluded |
| P8 shell | Phase 12 | |
| P8 release/signing | Phase 13 | |

## Non-negotiable engineering rules

These still apply to every implementation change:

- Native WPF/.NET only. No Python runtime/worker, Electron fallback, or web wrapper.
- Local audio and transcription remain local first. Network-backed summaries, sync, and planner behavior are explicit and opt-in.
- A selected transcription engine/model either runs or reports failure. Never silently substitute another engine.
- Raw capture and raw transcript remain recoverable when cleanup, diarization, summary, hook, export, or sync fails.
- Long operations have an owner, cancellation, progress, failure, retry, and deterministic shutdown behavior.
- Native audio, model, network, process, timer, tray, and WPF resources are disposed deterministically.
- Capture/model/media work stays off the WPF dispatcher; dispatcher work is bounded to state projection and rendering.
- New settings or records use an explicit schema migration with backup, rollback/recovery tests, and preservation of unknown/legacy-safe data.
- Provider keys/tokens remain in Windows Credential Manager or a stronger approved store, never JSON, logs, source, crash data, hooks, or support bundles.
- Existing unrelated working-tree changes remain untouched. No reset, stash, commit, or push unless separately requested.
- macOS API names do not justify a Windows dependency.

## Phase acceptance protocol

Documentation-only changes may reuse a successful build. Source/project changes still require:

1. Record launch-ledger IDs in scope.
2. Inspect current Windows code/tests and the macOS reference; older docs are not evidence.
3. Add or update proportionate automated tests for behavioral changes.
4. Kill running `Muesli.exe` when necessary.
5. `dotnet build windows-native\Muesli.Windows\Muesli.Windows.csproj --no-restore`
6. `dotnet test windows-native\Muesli.Windows.Tests\Muesli.Windows.Tests.csproj --no-restore` with no filter.
7. Launch the Debug `Muesli.exe` with the dashboard visible and foregrounded.
8. Inspect only the fresh log slice for `ERROR`, `Unhandled UI exception`, or `XamlParseException`.
9. Update the launch ledger, not this historical plan, when status changes.

Do not claim hardware or UI verification that did not happen in that change.

## Explicitly out of launch scope

Google Calendar/OAuth, Microsoft Store, ChatGPT subscription OAuth, CloudKit/iPhone sync, audio sync, ARM64, and literal macOS framework or model ports.

## Historical phase narratives

The Phase 0–8 narratives previously in this file described 2026-08-01 work against test counts of 46, 56, 74, 128, 165, and 195. Those counts are obsolete. The committed hardware-free baseline at inventory time is **484** expanded cases. Implementation after that date (notes, Ollama, detection lifecycle, onboarding progress, import cancel, exports, automation, Computer Use, Phase 12) is recorded in the launch ledger, not here.

Do not revive calendar, ChatGPT OAuth, or CloudKit work from the old P5/P6/P7B slices.
