# Windows Streaming ASR Investigation

Status: Phase 4 Windows-native implementation and packaged-runtime qualification completed on 2026-08-01.

The packaged `org.k2fsa.sherpa.onnx` 1.13.4 .NET assembly contains `OnlineRecognizer`, `OnlineStream`, and online transducer/CTC/Paraformer configuration types. The upstream C# documentation also provides Windows-capable online file and microphone examples, so a Windows-native streaming implementation is technically viable: [sherpa-onnx C# API](https://k2-fsa.github.io/sherpa/onnx/csharp-api/index.html).

The runnable Windows choice is sherpa-onnx's multilingual Nemotron 3.5 560 ms INT8 transducer. Upstream documents its per-stream automatic language selection and 19 transcription-ready locales: [Nemotron ASR Streaming](https://k2-fsa.github.io/sherpa/onnx/nemo/nemotron-streaming.html). Muesli uses the packaged 1.13.4 .NET `OnlineRecognizer`/`OnlineStream` API and the same package's native `VoiceActivityDetector` with the official Silero artifact: [streaming C API configuration](https://k2-fsa.github.io/sherpa/onnx/c-api/html/online_asr.html), [VAD API](https://k2-fsa.github.io/sherpa/onnx/c-api/html/vad.html).

Phase 4 pins and verifies:

- archive SHA-256 `C6BF…AE3A` plus the encoder, decoder, joiner, and tokens hashes;
- Silero SHA-256 `9E24…1FD6`;
- real multilingual test-audio inference through the exact DLLs emitted by the WPF build;
- real Silero speech-boundary detection through those DLLs;
- independent prepare/cancel/retry/verify/delete/status/diagnostics lifecycle without activation;
- bounded queue pressure, partial/committed state, boundary-only commits, deduplication, cancellation/disposal, persistence, and both ownership modes.

`LiveMeetingModelId` is still `null` by default. Preparing the model does not select it. Parakeet Realtime EOU remains absent because its macOS CoreML path is not a runnable Windows artifact; Nemotron 3.5 can be used for either preview-only or unified live-and-final ownership. The configured offline final model owns the whole final transcript in preview-only mode and only measured gap recovery in unified mode.

## Deliberate divergence from the macOS reference

macOS `StreamingVadController` keeps a `maxChunkDuration` safety timer that force-rotates a chunk after 5 s even when Silero has not reported a speech end. Windows does not port that: `MaxSpeechDuration` is `0`, so a chunk rotates only on an observed speech end or at finalization. A fixed-interval rotation is exactly the arbitrary mid-sentence cut this phase forbids, and the Windows durable recorders — not the streaming session — own the audio that must never be lost, so the latency cap that motivates the macOS timer does not apply here. The cost is that a single uninterrupted utterance stays provisional until the speaker pauses.

## Re-qualification, 2026-08-01

Re-run from an empty `%APPDATA%\muesli\streaming-models` cache to confirm the artifact is genuinely runnable rather than inherited from earlier evidence:

- pinned archive plus Silero downloaded over HTTPS and every SHA-256 verified in 78 s; 685,219,976 bytes installed;
- `test_wavs/ar.wav` (6.55 s) decoded to real Arabic text by `OnlineRecognizer` through the DLLs the WPF Debug build emits;
- native Silero returned a real speech boundary at samples 608–104448;
- a full `MeetingLiveTranscriptionSession` run committed that text only at the VAD boundary, showed a provisional tail beforehand, reported the correct model ID and ownership, and recorded no `streaming-engine-failure`;
- measured CPU real-time factor **0.42** (about 2.4× faster than real time) on this machine's CPU provider. Packets dropped during that run came from the harness feeding roughly twenty times faster than real time to exercise the bounded queue, not from capture.

Open qualification: long physical meetings, Bluetooth/default-route transitions, simultaneous process-target capture, CPU thermal/memory soak, and human multilingual accuracy review. One fixture on one machine is not a hardware matrix, so a broad “fully verified on all Windows hardware” claim remains unsupported.


## CUDA live inference, 2026-09-20

The live recognizer no longer pins `provider = "cpu"`. It resolves its provider through
`ExecutionProviderService` and honours an explicit NVIDIA CUDA selection. The measured
result on the qualification machine (RTX 4070 Laptop, driver 610.88) is that **CPU is
faster**, so Automatic keeps live transcription on CPU:

| Provider | Audio | Committed segments | Gaps | Dropped packets | Processing | RTF |
|---|---:|---:|---:|---:|---:|---:|
| CPU | 3845 ms | 1 | 0 | 0 | 2919 ms | 0.759 |
| CUDA | 3845 ms | 1 | 0 | 0 | 3475 ms | 0.904 |

The CUDA run also produced a different committed transcript for the same audio, and CUDA
adds roughly one second of recognizer initialization. An explicit CUDA selection is still
honoured and is reported truthfully; it is simply not the automatic choice.

Silero VAD remains pinned to CPU in every configuration, including when the recognizer
runs on CUDA. It evaluates one 512-sample window per packet on a single thread; moving
that to the GPU would add transfer and context cost on the latency-critical path for no
measurable benefit. This is a deliberate, measured decision, not an unimplemented path.

Reproduce with:

```powershell
Muesli.Windows.CommandHost.exe --benchmark-live --audio <wav> --provider cpu  --output live-cpu.json
Muesli.Windows.CommandHost.exe --benchmark-live --audio <wav> --provider cuda --output live-cuda.json
```
