# L11 CUDA provenance and delivery

Status: **delivered as an optional acceleration pack; not shipped in the public package.**
The NVIDIA provider is never part of the MSIX. Muesli downloads the Apache-2.0
sherpa-onnx CUDA binaries from one pinned upstream archive after the user asks for
NVIDIA acceleration, verifies the archive hash and every installed file hash, and only
reports the GPU as active after a real warm-up inference proves which provider executed
the graph. The public package remains CPU-only and `cudaProviderIncluded=false`.

Companion CPU inventory: `docs/L10_CPU_CATALOG_INVENTORY.md`.
Public package contract: `windows-native/Muesli.Windows.Core/NativeRuntime/public-cpu-native-catalog.json`.
Runtime acceptance: `NativeSherpaRuntime` + `PublicNativePackageContract.ValidateCudaBundle`.
Pack definition and lifecycle: `CudaAccelerationPack` / `CudaAccelerationPackInstaller`.
Provider policy: `ExecutionProviderService`.
Benchmarks: `docs/WINDOWS_GPU_QUALIFICATION.md`.

## Pinned provenance

| Layer | Pin | Evidence |
|---|---|---|
| Managed C# bindings | NuGet `org.k2fsa.sherpa.onnx` **1.13.4** | `Muesli.Windows.Core.csproj`; runtime selection requires the managed `OfflineRecognizer` version to start with `1.13.4`. |
| Acceleration-pack archive | `https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.4/sherpa-onnx-v1.13.4-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2` | SHA-256 `11B56076060C109E16D85EBA3ACFB03F6D0C4A738ECD2A99DD45EB689B2051A4`, 310 807 279 bytes. The hash was computed from the downloaded archive and matches the GitHub release digest. |
| sherpa-onnx native | 1.13.4, Git SHA1 `14280725` | `sherpa-onnx-version.exe` from the extracted archive. |
| ONNX Runtime | release `1.24.4`; Win32 file version `1.24.20260316.3.2d92497` | `FileVersionInfo` of the extracted `onnxruntime.dll`; the manifest pins the exact file version. |
| CUDA | `12.x` | Required NVIDIA dependencies listed below. |
| cuDNN | `9.x` | Required NVIDIA dependencies listed below. |

Only four files are extracted from the archive. The CLI toolset and the 275 MB
TensorRT provider are deliberately not installed.

| Installed file | Size | SHA-256 |
|---|---|---|
| `onnxruntime.dll` | 14 430 752 | `3B46571D12A9567791A42A2B2967A79C4E2E957AACDBA09A2DDB4FB391707BAA` |
| `onnxruntime_providers_shared.dll` | 22 040 | `1BCBAD19D14BC8395C1422C752E9E4CDD79E316E2582CDCD767BB8F500CDAE99` |
| `onnxruntime_providers_cuda.dll` | 275 606 552 | `CE1C698CAE708FD4ED9AB36EDDF256213DF40779D0806D20F32132C5D422DA72` |
| `sherpa-onnx-c-api.dll` | 4 544 000 | `BA5097086FDE22FE20C2ACE1AD03E83F2C39D4C0199793C6469F93A48A056D14` |

Install location: `%LOCALAPPDATA%\muesli\native-sherpa-cuda\sherpa-onnx-1.13.4-cuda12-cudnn9-win-x64`,
which is outside the packaged application. A generated `native-sherpa-cuda-runtime.json`
records the runtime version, ONNX Runtime version and file version, CUDA/cuDNN major
versions, the source archive URL and hash, and the per-file SHA-256 map.

## NVIDIA dependency policy

The pack never carries NVIDIA binaries. `cublasLt64_12.dll`, `cublas64_12.dll`,
`cufft64_11.dll`, `cudart64_12.dll`, `cudnn64_9.dll`, and the cuDNN 9 component DLLs
must already be resolvable from one of:

| Location | Purpose |
|---|---|
| `%LOCALAPPDATA%\muesli\native-sherpa-cuda-dependencies\cuda12-cudnn9` | Staging directory written by `scripts/install-parakeet-cuda-runtime.ps1`. |
| `MUESLI_CUDA_PATH`, `MUESLI_CUDNN_PATH`, `CUDA_PATH`, `CUDNN_PATH` (+ `\bin`) | User-installed CUDA Toolkit / cuDNN. |
| `MUESLI_SHERPA_CUDA_RUNTIME` | Explicit complete bundle for developers, which must still carry a pinned manifest. |

A CUDA directory without a manifest, with a wrong runtime version, with a mismatched
ONNX Runtime file version, or with any pinned-hash mismatch is rejected before any
library loads. Dependencies are resolved and their directories added to the trusted DLL
search path; cuDNN component DLLs are not preloaded one by one because cuDNN 9 owns
their initialization order.

## Acceptance rule

`NativeSherpaRuntime` loading the CUDA build is necessary but not sufficient. A GPU
provider is only offered and only reported as active when:

1. the acceleration pack is installed and every pinned hash matches;
2. the CUDA sherpa runtime loads with all NVIDIA dependencies resolved;
3. a real warm-up decode runs on the selected model with ONNX Runtime profiling
   enabled, and the resulting profile shows `CUDAExecutionProvider` nodes.

The profiling verdict is persisted in `%LOCALAPPDATA%\muesli\provider-qualification.json`
and is scoped to the exact model or runtime role, then invalidated by pack version, archive
hash, ONNX Runtime file version, or NVIDIA driver version. A verdict from one architecture
is never reused to claim GPU execution for another. If the provider cannot be proven, the same model is retried on CPU and the
fallback reason is recorded in diagnostics and logs. Models are never substituted.

## What the public package still rejects

`scripts/generate-native-runtime-inventory.ps1` and
`PackageDisclosureAndNativeInventoryTests` fail a public package that:

- contains any forbidden CUDA file name, including `onnxruntime_providers_cuda.dll`,
  `onnxruntime_providers_shared.dll`, and the NVIDIA CUDA/cuDNN DLLs;
- contains a `native-sherpa-cuda` directory;
- uses the false sentence "The primary Muesli package includes the version-matched
  sherpa-onnx CUDA provider";
- records `cudaProviderIncluded=true` in `RELEASE-METADATA.json`.

## DirectML

Not available, and not offered. The pinned sherpa-onnx 1.13.4 Windows build is compiled
without `SHERPA_ONNX_ENABLE_DIRECTML`, so a `provider=directml` request logs
`DirectML is for Windows only. Fallback to cpu!` and runs on CPU. This was reproduced on
2026-09-20 with the pinned CUDA archive's `sherpa-onnx-offline.exe` against a real model:
the request completed with the same CPU real-time factor as `provider=cpu` and no
`DmlExecutionProvider` nodes. No DirectML-enabled sherpa-onnx Windows asset exists in the
v1.13.4 release, and a narrow ONNX Runtime adapter would have to reimplement feature
extraction, transducer/attention decoding, and tokenization for every catalog
architecture — that is a second transcription pipeline, not a provider swap.

## Remaining hardware-dependent work

- Non-NVIDIA hardware and foreign GPU/driver matrices still require external machines.
- macOS-parity models that depend on CoreML/MLX/LiteRT remain platform differences; see
  `docs/WINDOWS_MACOS_MODEL_PARITY.md`.
