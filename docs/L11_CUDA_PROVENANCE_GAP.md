# L11 CUDA provenance gap analysis

Status: **gap analysis only**. This document does not package, ship, or claim
NVIDIA CUDA. The public Wave 0 package remains CPU-only. Ledger rows MOD-01 and
QUAL-01 stay Partial. Do not treat an incomplete bundle, a local CUDA Toolkit
install, or this write-up as a packaged NVIDIA provider.

Companion CPU inventory: `docs/L10_CPU_CATALOG_INVENTORY.md`.
Public package contract: `windows-native/Muesli.Windows/NativeRuntime/public-cpu-native-catalog.json`.
Runtime acceptance: `NativeSherpaRuntime` plus `PublicNativePackageContract.ValidateCudaBundle`.

## Version match that L11 must keep

| Layer | Pin today | Notes |
|---|---|---|
| Managed C# bindings | NuGet `org.k2fsa.sherpa.onnx` **1.13.4** | `Muesli.Windows.csproj`. `OfflineRecognizer` assembly version must start with `1.13.4`. |
| Packaged CPU native | Transitive NuGet `org.k2fsa.sherpa.onnx.runtime.win-x64` **1.13.4** | Supplies public `sherpa-onnx-c-api.dll` and CPU `onnxruntime.dll`. |
| CUDA runtime manifest | `runtimeVersion`: **1.13.4** | `NativeRuntime/SherpaOnnxCuda/native-sherpa-cuda-runtime.json` |
| CUDA ONNX Runtime recorded in that manifest | `onnxRuntimeVersion`: **1.24.4** | Not verified against a hashed upstream archive in this slice. |
| Upstream NuGet latest | `org.k2fsa.sherpa.onnx` **1.13.5** exists | Must not silently float. A CUDA bundle built for 1.13.5 is a mismatch. |

`NativeSherpaRuntime` rejects a CUDA directory unless:

1. `native-sherpa-cuda-runtime.json` exists,
2. `runtimeVersion` equals `1.13.4`,
3. managed `SherpaOnnx` version starts with `1.13.4`,
4. every required runtime DLL exists,
5. every required NVIDIA DLL is resolvable,
6. `sherpa-onnx-c-api.dll` actually loads.

Partial bundles stay unselected. CPU is then used only as the packaged
provider, and diagnostics must keep saying the public package does not include
CUDA.

## How a matching CUDA runtime would be acquired

There is **No CUDA NuGet runtime package** for this stack. Queried
`org.k2fsa.sherpa.onnx.runtime.win-x64-cuda` on nuget.org: **404**. The managed
package's dependencies are CPU RID packages only (`runtime.win-x64`,
`runtime.win-x86`, `runtime.win-arm64`, plus non-Windows RIDs). Adding a CUDA
`PackageReference` is not an available L11 path at 1.13.4.

Honest acquisition, when L11 packaging later happens, is therefore **upstream
GitHub release assets for tag `v1.13.4`**, not NuGet:

| Candidate asset | Size (GitHub listing) | Role |
|---|---|---|
| `sherpa-onnx-v1.13.4-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2` | 296.4 MB | Best candidate for a CUDA 12 + cuDNN 9 Windows GPU tree that can include Sherpa/ORT CUDA binaries. |
| `sherpa-onnx-v1.13.4-win-x64-cuda.tar.bz2` | 211.6 MB | Smaller CUDA Windows build. Likely Sherpa/ORT CUDA without the full NVIDIA redistributable set. |

URL pattern:

`https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.4/<asset>`

This slice did **not** download those archives and therefore has **no SHA-256**
for them. L11 packaging cannot start until one asset is chosen, hashed, extracted,
and shown to contain the files below at versions that match managed 1.13.4 and
manifest `onnxRuntimeVersion` 1.24.4 (or the manifest is updated to the proven
ORT version). Linux GPU extras that mention ONNX Runtime 1.27.1 must not be
mixed into the Windows 1.13.4 managed load path.

Python wheels such as `sherpa_onnx-1.13.4+cuda12.cudnn9-cp314-cp314-win_amd64.whl`
are not a .NET packaging source.

### What the existing helper actually stages

`scripts/install-parakeet-cuda-runtime.ps1` is optional **external NVIDIA
dependency staging**. It is not part of the public package. It:

- copies CUDA 12 Toolkit DLLs from a local `CUDA_PATH` (`cublasLt64_12.dll`,
  `cublas64_12.dll`, `cufft64_11.dll`, `cudart64_12.dll`);
- may download pinned cuDNN **9.10.2.21**
  (`C1A4567D822EBDA7373FA1F19255DFF4942302DE741F830160B6C7D1FB31AF23`);
- writes `muesli-parakeet-cuda-dependencies.json` under
  `%LOCALAPPDATA%\muesli\native-sherpa-cuda-dependencies\cuda12-cudnn9`.

It does **not** download or copy `sherpa-onnx-c-api.dll`,
`onnxruntime_providers_cuda.dll`, or `onnxruntime_providers_shared.dll`. It
cannot by itself make `NativeSherpaRuntime.IsCudaCapable` true.

Staging locations the runtime already searches:

| Location | Purpose |
|---|---|
| `MUESLI_SHERPA_CUDA_RUNTIME` | Explicit CUDA bundle directory |
| `<app>\native-sherpa-cuda\` | In-layout bundle (forbidden in the public zip) |
| `%LOCALAPPDATA%\muesli\native-sherpa-cuda-dependencies\cuda12-cudnn9` | NVIDIA DLLs only |
| `MUESLI_CUDA_PATH`, `MUESLI_CUDNN_PATH`, `CUDA_PATH`, `CUDNN_PATH` (+ `\bin`) | Additional NVIDIA search paths |

## Required CUDA bundle entries

From `native-sherpa-cuda-runtime.json` and `NativeSherpaRuntime`:

### Manifest

- `native-sherpa-cuda-runtime.json` with `runtimeVersion: "1.13.4"`

### Sherpa / ONNX Runtime CUDA files (`requiredRuntimeFiles`)

| File | Public CPU package |
|---|---|
| `onnxruntime.dll` | **Shipped** (CPU build from NuGet). A CUDA bundle needs the CUDA-capable ORT build, not a silent mix of CPU ORT + foreign EP. |
| `onnxruntime_providers_shared.dll` | **Forbidden** in the public zip (L03 inventory). Required for CUDA. |
| `onnxruntime_providers_cuda.dll` | **Forbidden** in the public zip. Required for CUDA. |
| `sherpa-onnx-c-api.dll` | **Shipped** (CPU build). CUDA selection loads this file from the CUDA directory, which must be the CUDA-built 1.13.4 binary. |

### NVIDIA CUDA 12 / cuDNN 9 files (`requiredNvidiaFiles`)

All of the following are **forbidden** in the public zip:

- `cublasLt64_12.dll`
- `cublas64_12.dll`
- `cufft64_11.dll`
- `cudart64_12.dll`
- `cudnn64_9.dll`
- `cudnn_graph64_9.dll`
- `cudnn_engines_runtime_compiled64_9.dll`
- `cudnn_engines_precompiled64_9.dll`
- `cudnn_heuristic64_9.dll`
- `cudnn_ops64_9.dll`
- `cudnn_adv64_9.dll`
- `cudnn_cnn64_9.dll`

Directory name `native-sherpa-cuda` is also forbidden in the public inventory.

## What L03 already rejects

`scripts/generate-native-runtime-inventory.ps1` and
`PackageDisclosureAndNativeInventoryTests` fail a public package that:

- contains any forbidden CUDA file name above (except the two CPU names
  `onnxruntime.dll` / `sherpa-onnx-c-api.dll`, which are allowed only as the
  CPU catalog components);
- contains a `native-sherpa-cuda` directory;
- still uses the false sentence “The primary Muesli package includes the
  version-matched sherpa-onnx CUDA provider”;
- records `cudaProviderIncluded=true` in `RELEASE-METADATA.json`.

`PublicNativePackageContract.ValidateCudaBundle` additionally rejects:

- missing directory;
- missing or invalid manifest;
- wrong `runtimeVersion`;
- missing any `requiredRuntimeFiles` entry in **that same directory**;
- missing any `requiredNvidiaFiles` entry in **that same directory**.

Gap versus runtime: `NativeSherpaRuntime` may resolve NVIDIA DLLs from the
dependency cache or `CUDA_PATH`, but `ValidateCudaBundle` requires those NVIDIA
files to sit beside the Sherpa CUDA DLLs. An honest NVIDIA package should
satisfy the stricter validator (one complete directory), not the split search
path.

Unmanifested CUDA EP files anywhere in the public layout are inventory
failures. Runtime selection also refuses them without a 1.13.4 manifest.

## What is still missing for an honest NVIDIA package

| Missing item | Why it blocks L11 |
|---|---|
| Chosen v1.13.4 Windows CUDA archive + SHA-256 | No pinned provenance; cannot reproduce or license-review the bits. |
| Extracted file hashes for the four runtime DLLs | CPU NuGet copies must not be relabelled as CUDA. |
| Proof that bundled ORT is 1.24.4 (or a documented, tested replacement) | Manifest currently claims 1.24.4 without archive evidence. |
| Complete NVIDIA set matching that ORT CUDA EP | Toolkit 12.x + cuDNN 9.x from a user machine may not match the EP. The install script’s cuDNN 9.10.2.21 pin is independent of the GitHub GPU archive. |
| Redistribution legal review | Notices already state NVIDIA EULAs apply and Muesli draws no eligibility conclusion. |
| Separate NVIDIA layout / installer | Public zip must stay CPU-only until a dedicated NVIDIA artifact exists. |
| Hardware qualification | Driver, GPU, VRAM, cold/warm latency, deterministic output, CPU fallback disclosure. Historical 2026-08-01 CUDA notes are not this tree. |
| Live ASR CUDA | Meeting live recognizer currently pins `provider = "cpu"`. That is MOD-04 / LIVE, not solved by copying DLLs. |
| Packaging implementation | Out of scope for this slice. |

Do not claim CUDA from:

- a directory that has NVIDIA DLLs but CPU `sherpa-onnx-c-api.dll`;
- `install-parakeet-cuda-runtime.ps1` output alone;
- Python wheels;
- sherpa-onnx 1.13.5 bits loaded against managed 1.13.4;
- any public Wave 0 zip/installer.

## Remaining L11 physical / packaging gates

When a later slice implements packaging (not this one):

1. Pin one `v1.13.4` Windows CUDA archive URL and SHA-256.
2. Stage a complete bundle that passes `ValidateCudaBundle` and
   `NativeSherpaRuntime.IsCudaCapable` without touching the public CPU catalog.
3. Keep public notices, inventory, and `cudaProviderIncluded=false` on the CPU
   zip.
4. Qualify NVIDIA hardware: native startup, seven-family (or honestly scoped)
   model smoke, dictation, meeting, diarization, stress, and release gates from
   the L11 launch-plan exit.
5. Disclose CPU fallback only when a complete matching bundle is absent;
   never announce NVIDIA acceleration from a partial copy.

This slice added analysis and drift tests only. It did not add CUDA DLLs, did
not change package notices to claim NVIDIA, and did not promote MOD-01 or
QUAL-01.
