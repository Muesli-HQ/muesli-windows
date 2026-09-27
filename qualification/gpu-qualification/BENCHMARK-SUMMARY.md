# CPU versus GPU transcription benchmarks

Generated 2026-09-20T21:09:22.4025202+05:30 on the local qualification host.

Each row is one real inference run through the shipping native path with the packaged CPU
runtime or the verified NVIDIA CUDA acceleration pack. TranscriptMatch compares the
SHA-256 of the produced transcript across providers for the same model and audio.

| Model | Provider | Status | Model init ms | Warm inference ms | RTF | Deterministic |
|---|---|---|---:|---:|---:|---|
| whisper-small-en | cpu | ok | 1463 | 1639 | 0.2478 | True |
| whisper-medium-en | cpu | ok | 3969 | 6689 | 1.01 | True |
| whisper-large-turbo-multilingual | cpu | ok | 1936 | 2089 | 0.3156 | True |
| qwen3-asr-0.6b-int8 | cpu | ok | 3323 | 28428 | 0.3225 | True |
| cohere-transcribe-int8-en | cpu | ok | 5150 | 1027 | 0.1786 | True |
| whisper-small-en | cuda | ok | 2072 | 908 | 0.1375 | True |
| whisper-medium-en | cuda | ok | 5257 | 2027 | 0.3063 | True |
| whisper-large-turbo-multilingual | cuda | ok | 2607 | 1443 | 0.2181 | True |
| qwen3-asr-0.6b-int8 | cuda | ok | 4359 | 50710 | 0.5753 | True |
| cohere-transcribe-int8-en | cuda | ok | 9519 | 1000 | 0.1739 | True |

## Transcript equality across providers

| Model | Providers | Transcript match |
|---|---|---|
| whisper-small-en | cpu, cuda | True |
| whisper-medium-en | cpu, cuda | True |
| whisper-large-turbo-multilingual | cpu, cuda | True |
| qwen3-asr-0.6b-int8 | cpu, cuda | False |
| cohere-transcribe-int8-en | cpu, cuda | True |
