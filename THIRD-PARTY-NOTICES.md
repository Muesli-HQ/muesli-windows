# Third-Party Notices

Muesli redistributes or downloads the components below. This notice is informational and
does not replace the applicable license texts in `licenses/` or upstream terms.

## Application libraries and runtimes

### QuestPDF 2026.5.0

- Copyright: Marcin Ziabek / QuestPDF contributors.
- Source: https://github.com/QuestPDF/QuestPDF
- License: the license shipped in the 2026.5.0 NuGet package; this build selects
  `LicenseType.Community`.
- Use: PDF meeting export.
- Important: the Community license is not universally available. The legal entity that
  builds or distributes Muesli must confirm eligibility under QuestPDF's current License
  Selection Guide or obtain the appropriate paid license. No eligibility conclusion is
  made here.
- Included text: `licenses/QuestPDF-2026.5.0.md`.

QuestPDF's redistributed native/content dependencies in this package include:

- Skia: Copyright Google Inc. and contributors; BSD 3-Clause license;
  https://skia.org/; text in `licenses/BSD-3-Clause.txt` and
  `licenses/QuestPDF-native/skia.txt`.
- qpdf: Copyright Jay Berkenbilt and contributors; Apache License 2.0;
  https://github.com/qpdf/qpdf; text in `licenses/Apache-2.0.txt` and
  `licenses/QuestPDF-native/qpdf.txt`.
- zlib: Copyright Jean-loup Gailly and Mark Adler; zlib license;
  text in `licenses/Zlib.txt`.
- MinGW-w64 winpthread (`libwinpthread-1.dll`): MIT;
  text in `licenses/MinGW-w64-winpthread-MIT.txt`.
- GCC runtime support (`libgcc_s_seh-1.dll`, `libstdc++-6.dll`): GPLv3 with
  the GCC Runtime Library Exception 3.1;
  text in `licenses/GCC-Runtime-Library-Exception-3.1.txt`.
- Additional Skia/qpdf bundled notices (harfbuzz, libpng, libjpeg-turbo,
  libwebp, expat, wuffs, libgrapheme, emsdk): `licenses/QuestPDF-native/`.
- Lato font: Copyright 2010-2014 Łukasz Dziedzic and contributors; SIL Open Font
  License 1.1; https://www.latofonts.com/; the upstream `LatoFont/OFL.txt` is
  also retained verbatim in the published package.

QuestPDF community-license eligibility is a Phase 13 / EXP-01 release-owner
check. This repository records that the build selects `LicenseType.Community`
and does not declare that the distributing entity is eligible.

### NAudio 2.2.1

- Copyright: Mark Heath and contributors.
- Source: https://github.com/naudio/NAudio
- License: MIT.
- Use: WASAPI microphone and system-audio capture.
- Included text: `licenses/MIT.txt`.

### SharpCompress 0.48.1

- Copyright: Adam Hathcock and contributors.
- Source: https://github.com/adamhathcock/sharpcompress
- License: MIT.
- Use: reading model archives.
- Included text: `licenses/MIT.txt`.

### LLamaSharp 0.27.0 and llama.cpp native backend

- Copyright: LLamaSharp and llama.cpp contributors.
- Sources: https://github.com/SciSharp/LLamaSharp and
  https://github.com/ggml-org/llama.cpp
- License: MIT.
- Use: optional local GGUF transcript cleanup (disabled by default).
- Included text: `licenses/MIT.txt`.

### sherpa-onnx 1.13.4

- Copyright: The Next-gen Kaldi development team and contributors.
- Source: https://github.com/k2-fsa/sherpa-onnx
- License: Apache License 2.0.
- Use: native Parakeet ASR and speaker diarization.
- Included text: `licenses/Apache-2.0.txt`.

### ONNX Runtime

- Copyright: Microsoft Corporation and contributors.
- Source: https://github.com/microsoft/onnxruntime
- License: MIT.
- Use: native ONNX model execution redistributed through sherpa-onnx runtime assets.
- Included text: `licenses/MIT.txt`.

### Microsoft.Data.Sqlite 10.0.11 and SQLitePCLRaw 2.1.12

- Copyright: Microsoft Corporation and contributors; Eric Sink / SourceGear, LLC
  (SQLitePCLRaw); SQLite authors (native amalgamation).
- Sources: https://github.com/dotnet/efcore and https://github.com/ericsink/SQLitePCL.raw
- Licenses: MIT (`Microsoft.Data.Sqlite`), Apache License 2.0 (`SQLitePCLRaw.lib.e_sqlite3`),
  and the SQLite public-domain blessing for `e_sqlite3.dll`.
- Use: local SQLite persistence.
- Included text: `licenses/MIT.txt`, `licenses/Apache-2.0.txt`,
  `licenses/SQLite-blessing.txt`.

### Microsoft .NET runtime and managed support libraries

- Copyright: .NET Foundation and contributors; Microsoft Corporation and contributors.
- Sources: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf
- License: MIT for the redistributed .NET runtime and the Microsoft managed packages used
  by this build. Individual notices embedded in Microsoft binaries remain applicable.
- Use: self-contained Windows runtime, WPF, JSON, logging abstractions, and related support.
- Included text: `licenses/MIT.txt`.

### CommunityToolkit.HighPerformance and Ix.Async support libraries

- Copyright: .NET Foundation, Microsoft, and contributors.
- Sources: https://github.com/CommunityToolkit/dotnet and
  https://github.com/dotnet/reactive
- License: MIT.
- Use: transitive managed dependencies of the application libraries.
- Included text: `licenses/MIT.txt`.

### Swift shared core (MuesliCoreABI) 6.4

- Copyright: the Swift project authors; Apple Inc. and contributors (swift-crypto, Swift runtime);
  the `liblzfse` authors; Jean-loup Gailly and Mark Adler (zlib); SQLite authors.
- Sources: https://github.com/swiftlang/swift, https://github.com/apple/swift-crypto,
  https://github.com/lzfse/lzfse, https://www.sqlite.org, built for Windows x64 from
  `native/MuesliNative` (bridge ABI 1).
- License: Apache License 2.0 with the Swift Runtime Library Exception for the Swift runtime and
  swift-crypto; BSD 3-Clause for `liblzfse`; zlib for `z.dll`; the SQLite public-domain blessing
  for `sqlite3.dll`.
- Use: canonical transcript normalization, word count and related shared text processing, loaded
  by the packaged application from its own directory.
- Included texts: `licenses/Apache-2.0.txt`, `licenses/BSD-3-Clause.txt`, `licenses/Zlib.txt`,
  `licenses/SQLite-blessing.txt`, `licenses/MIT.txt`.

### Microsoft Visual C++ 2022 runtime

- Copyright: Microsoft Corporation.
- License: the Microsoft Visual C++ Redistributable terms; redistributed beside the application
  because the shared Swift bridge and native dependencies link against the C++ runtime.
- Use: `MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`.
- Included text: `licenses/Microsoft-VCpp-Runtime.txt`.

## Fonts

### Inter

- Copyright: The Inter Project Authors.
- Source: https://github.com/rsms/inter
- License: SIL Open Font License 1.1.
- Use: application typography.
- Included text: `licenses/OFL-1.1.txt`.

## Downloaded model artifacts

The following models are downloaded on demand into the user's model cache. They are not
embedded in the primary ZIP or installer, but their attribution and terms apply after
download.

### NVIDIA Parakeet TDT 0.6B v3

- Creator and attribution: NVIDIA Corporation, `nvidia/parakeet-tdt-0.6b-v3`.
- Model card: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3
- License: Creative Commons Attribution 4.0 International (CC BY 4.0).
- Use: multilingual speech recognition. Muesli uses sherpa-onnx-converted ONNX artifacts
  from the pinned k2-fsa release.
- Included text: `licenses/CC-BY-4.0.txt`.

### NVIDIA Nemotron 3.5 ASR Streaming 0.6B

- Creator and attribution: NVIDIA Corporation, `nvidia/nemotron-3.5-asr-streaming-0.6b`.
- Source/model card: https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b
- Converted artifact source: https://github.com/k2-fsa/sherpa-onnx/releases/tag/asr-models
- Use: opt-in local live meeting transcription through sherpa-onnx. The artifact downloads only after an explicit user action and is not bundled in the installer. The upstream model-card license terms apply.

### Silero VAD

- Creator and attribution: Silero Team, `snakers4/silero-vad`.
- Source and license: https://github.com/snakers4/silero-vad (MIT).
- Use: opt-in local speech-boundary detection for live meeting transcription. The pinned ONNX artifact downloads only with the live model and is not bundled in the installer.

### pyannote segmentation 3.0

- Copyright: 2023 CNRS and contributors.
- Model card: https://huggingface.co/pyannote/segmentation-3.0
- License: MIT.
- Use: speaker segmentation via a sherpa-onnx-converted ONNX artifact from the pinned
  k2-fsa release.
- Included text: `licenses/MIT.txt`.

### NVIDIA TitaNet speaker embedding model

- Creator and attribution: NVIDIA Corporation,
  `nvidia/speakerverification_en_titanet_large`.
- Model card: https://huggingface.co/nvidia/speakerverification_en_titanet_large
- License: Creative Commons Attribution 4.0 International (CC BY 4.0).
- Use: speaker embeddings via a sherpa-onnx-converted ONNX artifact from the pinned
  k2-fsa release.
- Included text: `licenses/CC-BY-4.0.txt`.

## Optional NVIDIA acceleration pack (not in the public package)

Public package includes the CPU Sherpa provider only. NVIDIA CUDA and DirectML are
not included. The optional NVIDIA CUDA acceleration pack is a separate,
SHA-256-verified download from the pinned sherpa-onnx 1.13.4 release and is never
**shipped** inside this package.

The public package does **not** include the version-matched sherpa-onnx CUDA
provider, the NVIDIA CUDA Toolkit, cuDNN, `onnxruntime_providers_cuda.dll`, or
`onnxruntime_providers_shared.dll`. Those files are also **not shipped** in the ZIP
or installer; Muesli downloads the Apache-2.0 sherpa-onnx CUDA binaries into
`%LOCALAPPDATA%\muesli\native-sherpa-cuda\<pack version>` only after the user asks
for NVIDIA acceleration from Models, and only after the download matches the pinned
archive SHA-256 and every installed file matches its pinned SHA-256.

NVIDIA CUDA Toolkit and cuDNN remain user-supplied. Muesli never downloads, stages,
or redistributes them. Rules that stay in force:

- do not describe the public or primary package as CUDA-capable or NVIDIA-accelerated;
- an incomplete or unmanifested CUDA directory must be rejected at runtime;
- NVIDIA DLLs must not appear in the public package inventory;
- a GPU provider may only be reported as active after a real warm-up inference on
  this machine proves which provider executed the graph.

`scripts/install-parakeet-cuda-runtime.ps1` remains the opt-in helper that copies
selected CUDA 12 DLLs from a user-installed CUDA Toolkit and can download the pinned
official cuDNN 9.10.2.21 archive from NVIDIA over HTTPS. That path is not a
public-package feature.

CUDA Toolkit and cuDNN are governed by NVIDIA's applicable license terms, not by an
open-source license in this repository. Users and distributors must review and accept the
current NVIDIA terms before installing or redistributing those components. See:

- https://docs.nvidia.com/cuda/eula/index.html
- https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html

Muesli makes no legal conclusion about eligibility to use or redistribute NVIDIA software.
