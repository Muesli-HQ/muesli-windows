# Windows GPU transcription qualification

Machine: Intel Core i7-13620H (10 cores / 16 threads), 23.6 GB RAM,
NVIDIA GeForce RTX 4070 Laptop GPU 8 GB (driver 610.88, CUDA UMD 13.3), Intel UHD
Graphics, Windows 11 build 26200.

Runtime: packaged CPU sherpa-onnx 1.13.4, or the optional NVIDIA CUDA acceleration pack
(sherpa-onnx 1.13.4 CUDA build, ONNX Runtime `1.24.20260316.3.2d92497`, CUDA 12.x,
cuDNN 9.x) staged from the pinned archive documented in
`docs/L11_CUDA_PROVENANCE_GAP.md`.

Evidence: `qualification/gpu-qualification/*.json` and
`qualification/gpu-qualification/BENCHMARK-SUMMARY.md`, produced by
`scripts/benchmark-gpu-transcription.ps1` and
`Muesli.Windows.CommandHost --benchmark-live`. Local user paths in the committed
JSON evidence are redacted; measurements and transcript hashes are unchanged.

## Real inference results (2026-09-20)

Each row is a real decode through the shipping native path. "Transcript match" compares
the SHA-256 of the transcript produced on CPU and on CUDA for the same model and the same
audio, so it is also a cross-provider determinism check.

| Model | CPU init ms | CUDA init ms | CPU warm ms | CUDA warm ms | CPU RTF | CUDA RTF | Transcript match |
|---|---:|---:|---:|---:|---:|---:|---|
| parakeet-v3 | 1769 | 3057 | 237 | 227 | 0.0629 | 0.0601 | yes |
| parakeet-unified-en-int8 | 2001 | 2787 | 896 | 396 | 0.1209 | 0.0535 | yes |
| parakeet-v2-en-int8 | 1908 | 2507 | 728 | 380 | 0.0982 | 0.0514 | yes |
| whisper-tiny-en | 496 | 720 | 571 | 295 | 0.0868 | 0.0448 | yes |
| whisper-small-en | 1463 | 2072 | 1639 | 908 | 0.2478 | 0.1375 | yes |
| whisper-medium-en | 3969 | 5257 | 6689 | 2027 | 1.0100 | 0.3063 | yes |
| whisper-large-turbo-multilingual | 1936 | 2607 | 2089 | 1443 | 0.3156 | 0.2181 | yes |
| sensevoice-small-int8 | 1056 | 1976 | 147 | 141 | 0.0210 | 0.0201 | yes |
| qwen3-asr-0.6b-int8 | 3323 | 4359 | 28428 | 50710 | 0.3225 | 0.5753 | **no** |
| cohere-transcribe-int8-en | 5150 | 9519 | 1027 | 1000 | 0.1786 | 0.1739 | yes |

Live Nemotron 3.5 Streaming through the real `MeetingLiveTranscriptionSession`
(100 ms capture packets, unified live-and-final ownership, same audio):

| Provider | Audio ms | Processing ms | RTF | Committed segments | Measured gaps | Dropped packets | Transcript match |
|---|---:|---:|---:|---:|---:|---:|---|
| CPU | 3845 | 2919 | 0.759 | 1 | 0 | 0 | reference |
| CUDA | 3845 | 3475 | 0.904 | 1 | 0 | 0 | **no** |

## Conclusions

- **CUDA is a real win for Whisper and Parakeet transducer**: 1.45x–3.3x faster warm
  inference. Whisper Medium English crosses from RTF 1.01 (slower than real time) to
  0.31, which is the difference between usable and unusable on this class of CPU.
- **CUDA is a tie for Parakeet v3, SenseVoice, and Cohere Transcribe.** CPU is kept under
  Automatic because CUDA also adds 0.9–4.4 s of model load for no warm-inference gain.
- **CUDA is a regression for Qwen3-ASR**: 1.8x slower, and the transcript differed from
  the CPU transcript (a `nvrtc64_120_0.dll` runtime-compilation warning appears in the
  CUDA run). Automatic therefore keeps Qwen3-ASR on CPU.
- **CUDA is a regression for live Nemotron streaming** (RTF 0.904 vs 0.759) and the
  committed transcript differed. Automatic keeps live transcription on CPU.
- **Silero VAD stays on CPU in every configuration.** It runs one 512-sample window per
  packet on a single thread; the GPU transfer and context cost for that workload is not
  justified, and it is the latency-critical path that must never wait on a GPU.
- The CUDA attempt is never trusted from a request or a DLL load. `ExecutionProviderService`
  runs a real warm-up decode with ONNX Runtime profiling enabled and reads the execution
  provider out of the profile; the verdict is persisted per pack/ORT/driver fingerprint.
  When it fails, the same model is retried on CPU and the reason is recorded.

## Reproducing

```powershell
# CPU versus GPU matrix (writes qualification/gpu-qualification)
powershell -ExecutionPolicy Bypass -File scripts\benchmark-gpu-transcription.ps1

# Live streaming path on either provider
$exe = "windows-native\Muesli.Windows.CommandHost\bin\Debug\net10.0-windows\Muesli.Windows.CommandHost.exe"
& $exe --benchmark-live --audio <16k-or-any-wav> --provider cuda --output live-cuda.json
& $exe --benchmark-live --audio <16k-or-any-wav> --provider cpu  --output live-cpu.json

# Provider and pack state, including the persisted GPU verdict
& $exe --acceleration-status
```

## Hardware still to qualify externally

- AMD and Intel GPUs: DirectML is not offered (see below), so there is no cross-vendor
  GPU path to qualify.
- Different NVIDIA generations and drivers: persisted verdicts are scoped to each exact model
  or runtime role and fingerprint-keyed on the driver version, so neither a different model nor
  a driver change can inherit another graph's qualification.
- CPU-only machines: `Muesli.Windows.Tests` passes without any GPU; every GPU assertion is
  gated on the pack being installed and the CUDA runtime being loaded.
