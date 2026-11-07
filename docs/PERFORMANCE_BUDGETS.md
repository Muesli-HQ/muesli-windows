# Performance budgets

Status: **provisional** (proposed 2026-11-07). Targets are a starting point for the Store release and
must be confirmed on the reference hardware below. Treat a regression against a confirmed budget as a
release blocker, the same as a failing test.

Muesli's Windows audience is overwhelmingly CPU-only x86-64 laptops. Budgets are set for the low tier;
anything that only meets them on a fast desktop does not meet them.

## Reference hardware

| Tier | Example CPUs | RAM | Notes |
|---|---|---|---|
| Low (budget tier) | Intel Core i3-1115G4, AMD Ryzen 3 5300U | 8 GB | 4 cores or fewer, AVX2, no AVX-512, integrated graphics, battery power |
| Mid | Intel Core i5-1235U, AMD Ryzen 5 7530U | 16 GB | The common business laptop |

Measure on battery with the default Windows power mode as well as plugged in. Efficiency-mode
throttling (EcoQoS) is a real-world condition, not an edge case.

## Budgets

| Area | Metric | Budget (low tier) | How it is measured |
|---|---|---|---|
| UI | UI-thread work per input or update | No UI-thread work item over 50 ms in steady state; **zero hangs over 2 s, ever** | Windows Performance Recorder (WPR) trace of the flow, UI thread CPU and wait analysis in WPA; field hangs from Windows Error Reporting / Partner Center health reports |
| UI | Responsiveness during model download, import, finalization | Same as above while the work runs | Same, recorded during the operation |
| Dictation | Hotkey press to first captured sample | ≤ 50 ms (pre-roll covers the gap) | Capture start timestamp vs hotkey event |
| Dictation | Hotkey release to text inserted, utterance ≤ 10 s, cleanup off | p50 ≤ 400 ms, p95 ≤ 700 ms | Dictation timing log line (`PasteOperationResult` plus transcription timing) |
| Dictation | Added latency with local cleanup on | ≤ +600 ms p95 | Same, cleanup stage timing |
| Dictation | Captured audio lost | 0 samples; every discontinuity logged | WASAPI discontinuity flags and sample-position gaps |
| Transcription | Real-time factor, Parakeet v3 int8, CPU | ≤ 0.10 (repo measured 0.055 on 2026-09-23) | `Muesli.Windows.CommandHost --benchmark-native` |
| Meetings | Captured audio lost over 60 min | 0 unlogged gaps | Capture health log and journal |
| Meetings | Stop to final transcript, 60 min meeting | ≤ 60 s once live chunks are reused; ≤ 0.15 × duration until then | `scripts/benchmark-native-meeting.ps1` |
| Startup | Launch to interactive window | ≤ 1.5 s warm, ≤ 4 s cold | Process start to first rendered frame (WPR or log timestamps) |
| Idle | Average CPU with app idle in tray | ≤ 0.5 % of one core | Performance Monitor or `Get-Process` sampling over 10 min |
| Memory | Private working set, idle, no model loaded | ≤ 150 MB | Task Manager details / `Get-Process` |
| Memory | Private working set, Parakeet v3 warm | ≤ 900 MB | Same |

## Known violations (2026-11-07)

These were reproduced or found in code during the macOS comparison and Phase 0 review:

- Settings loads read JSON plus three Windows Credential Manager entries on every call, and run on
  hotkey events and UI state changes (`SettingsStore.cs`, `WinUiDictationContext.cs`,
  `WinUiIndicatorHost.cs`).
- Audio callbacks write WAV files, resample, allocate and run Silero VAD on the capture thread
  (`AudioCaptureService.cs`, `MeetingRollingTranscriber.cs`).
- Lists in Dictations, Meetings and the live transcript are not virtualized.
- Model downloads froze onboarding (fixed on `fix/onboarding-model-refresh-freeze`).

## Engineering rules

Responsiveness comes from the architecture, not from detecting stalls after the fact: the UI thread
only applies finished, immutable snapshots that services produced elsewhere.

**UI thread**
- Only applies results: no file, registry, Credential Manager, network, process or COM-to-other-process calls.
- Reads state from in-memory snapshots (settings, library, model status) that services keep current.
- No work proportional to library size in constructors, property getters or event handlers.
- Coalesce background events before they reach the dispatcher; at most one queued refresh per view.
- Long lists use virtualizing controls with incremental updates, not wholesale replacement.

**Audio threads**
- Event-driven WASAPI on an MMCSS-registered thread.
- No allocation, locking against other subsystems, file I/O, logging or inference in the callback:
  copy into a preallocated ring buffer and return.

**Hot paths** (hotkey, capture start, transcription, paste)
- Read settings from the in-memory snapshot, never from disk.
- Read secrets only when a cloud feature is actually invoked.

**Downloads**
- Only immutable sources (content-addressed or commit-pinned); verify every file by SHA-256.
- Report progress at most a few times a second.

## Definition of done for performance-sensitive changes

1. State the budgets the change affects and the numbers before and after, on the low tier.
2. Attach the relevant log excerpt or benchmark output.
3. Run the affected flow in the app; for UI changes, record a WPR trace and confirm no UI-thread work
   item over the 50 ms budget.
