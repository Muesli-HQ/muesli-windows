# Phase 4: Meeting and Media-Import Qualification

> Historical evidence note: this document describes the Parakeet-only build qualified at the time. The current role-separated seven-model platform is documented in `WINDOWS_TRANSCRIPTION_MODELS.md`; statements below about one engine are not current product behavior.

Phase 4 hardens the non-dictation transcription path without adding consumer
settings or backend choices. Parakeet v3 remains the single ASR engine. Native
sherpa-onnx diarization is a meeting processing stage, not an alternate ASR
backend.

## Implemented

- `NativeDiarizationClient` now owns one long-lived diarizer and reuses it for
  subsequent meetings instead of loading two ONNX models for every request.
- Diarizer construction and processing run away from the WPF dispatcher.
- Diarization selects CUDA automatically when the packaged Sherpa runtime and
  NVIDIA dependencies are usable, and uses the same implementation on CPU.
  Explicit provider overrides fail clearly rather than silently changing the
  requested qualification configuration.
- On CUDA, diarization overlaps the independent meeting ASR work. CPU keeps the
  stages sequential because local qualification showed that competing CPU
  sessions increased contention without improving the combined latency.
- Production shutdown disposes the persistent diarizer.
- `--benchmark-meeting` and `benchmark-native-meeting.ps1` measure the combined
  ASR, diarization, and speaker-merge wall time. Gates cover total and
  diarization RTF, provider, speaker count/coverage, model reuse, chronological
  segments, and deterministic transcript/segment/diarization/merge hashes.
- `test-media-imports.ps1` provides manifest-driven import qualification with
  decoded-duration, latency, determinism, reuse, transcript length, and optional
  human-reference WER/CER gates.
- Import duration comes from decoded samples when container metadata is absent.
  A file that yields no usable decoded audio now reports a clear Windows codec
  diagnostic instead of appearing to be a successful zero-second transcript.

## Local retained results

The 29.27-second local speech capture passed two-run deterministic meeting gates
on both providers:

| Provider | Warm combined wall | Total RTF | ASR wall | Diarization wall | Model reuse |
| --- | ---: | ---: | ---: | ---: | --- |
| CUDA | 1.66 s | 0.057 | 1.47 s | 1.66 s, overlapped | ASR and diarization yes |
| CPU | 3.25 s | 0.111 | 1.24 s | 2.01 s, sequential | ASR and diarization yes |

Both providers produced the same transcript, timestamp-segment, diarization,
and merged-transcript hashes. Speaker coverage was 97.95% with one speaker in
this single-speaker fixture. The local system-audio meeting capture also passed
the complete CUDA path, but is mostly silence and is retained only as a pipeline
fixture.

The local media manifest passed WAV and WebM decoding. The 29.27-second WAV
produced 491 transcript characters at roughly 0.040 RTF. The 5.64-second WebM
decoded deterministically at roughly 0.049 RTF but contains no detectable
speech, so it is a decoder fixture and not a quality baseline.

Reports are written under `artifacts/benchmarks` and remain local. They include
absolute capture paths and are intentionally not shipped.

## Still required before broad format claims

The manifest harness is ready, but this workstation does not contain approved
human-speech fixtures for MP3, M4A, MP4, MOV, MKV, or OGG. Those formats must be
qualified with redistributable or locally approved fixtures before release
documentation calls them guaranteed. Actual decoding depends on the Windows
Media Foundation codecs installed on the target machine; unsupported media now
fails with an actionable conversion/codec message.
