import argparse
import base64
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any


MODEL_MAP = {
    "tiny": "tiny",
    "base": "base",
    "small": "small",
    "medium": "medium",
    "large-v3-turbo": "turbo",
}
MODEL_CACHE: dict[str, Any] = {}
POST_PROCESSOR_CACHE: dict[str, Any] = {}
PARAKEET_CACHE: dict[str, Any] = {}
DIARIZATION_CACHE: dict[str, Any] = {}
MODEL_RUNTIME_INFO: dict[str, str] = {}
_NVIDIA_RUNTIME_AVAILABLE: bool | None = None


def cache_dir() -> Path:
    directory = Path(
        os.environ.get("MUESLI_MODEL_CACHE", Path.home() / ".cache" / "muesli")
    )
    directory.mkdir(parents=True, exist_ok=True)
    return directory


def has_nvidia_runtime() -> bool:
    global _NVIDIA_RUNTIME_AVAILABLE
    if _NVIDIA_RUNTIME_AVAILABLE is not None:
        return _NVIDIA_RUNTIME_AVAILABLE

    try:
        completed = subprocess.run(
            ["nvidia-smi", "-L"],
            capture_output=True,
            text=True,
            timeout=3,
            check=False,
        )
        _NVIDIA_RUNTIME_AVAILABLE = completed.returncode == 0 and bool(
            completed.stdout.strip()
        )
    except (FileNotFoundError, OSError, subprocess.SubprocessError):
        _NVIDIA_RUNTIME_AVAILABLE = False
    return _NVIDIA_RUNTIME_AVAILABLE


def device_candidates() -> list[tuple[str, str]]:
    configured_device = os.environ.get("MUESLI_DEVICE", "auto").strip().lower()
    configured_compute = os.environ.get("MUESLI_COMPUTE_TYPE", "").strip()
    if configured_device and configured_device != "auto":
        default_compute = "float16" if configured_device == "cuda" else "int8"
        return [(configured_device, configured_compute or default_compute)]
    if has_nvidia_runtime():
        return [("cuda", configured_compute or "float16"), ("cpu", "int8")]
    return [("cpu", configured_compute or "int8")]


def get_whisper_model(model_profile: str) -> Any:
    from faster_whisper import WhisperModel

    model_name = MODEL_MAP.get(model_profile, "base")
    if model_name in MODEL_CACHE:
        return MODEL_CACHE[model_name]

    last_error: Exception | None = None
    for device, compute_type in device_candidates():
        try:
            model = WhisperModel(
                model_name,
                device=device,
                compute_type=compute_type,
                download_root=str(cache_dir()),
            )
            MODEL_CACHE[model_name] = model
            MODEL_RUNTIME_INFO[model_name] = f"{device} ({compute_type})"
            return model
        except Exception as exc:
            last_error = exc
            continue

    raise RuntimeError(f"Unable to load Whisper model '{model_name}'.") from last_error


def cleanup_prompt(text: str, context: str, system_prompt: str = "") -> list[dict[str, str]]:
    default_prompt = (
        "You clean up speech-to-text transcripts for a local dictation app. "
        "Preserve the speaker's meaning. Do not add facts. Do not answer the user. "
        "Fix obvious grammar, punctuation, casing, and transcription errors. "
        "Remove filler words only when they are not meaningful. "
        "If the speaker dictated a list, format it as a clean bullet or numbered list. "
        "Return only the cleaned transcript."
    )
    return [
        {
            "role": "system",
            "content": system_prompt.strip() or default_prompt,
        },
        {
            "role": "user",
            "content": f"Context: {context or 'dictation'}\n\nTranscript:\n{text}",
        },
    ]


def fallback_cleanup(text: str) -> str:
    clean = " ".join(text.strip().split())
    if not clean:
        return ""
    lower = clean.lower()
    list_markers = ("grocery list", "shopping list", "todo list", "to do list")
    separators = [", and ", ", ", " and then ", " then ", " and "]
    if any(marker in lower for marker in list_markers):
        for marker in list_markers:
            if marker in lower:
                start = lower.index(marker) + len(marker)
                clean = clean[start:].strip(" :.-")
                break
        parts = [clean]
        for separator in separators:
            if separator in clean:
                parts = [part.strip(" .") for part in clean.split(separator)]
                break
        if len(parts) == 2 and len(parts[0].split()) >= 3:
            parts = parts[0].split() + [parts[1]]
        parts = [part for part in parts if part]
        if len(parts) > 1:
            return "\n".join(f"- {part[:1].upper()}{part[1:]}" for part in parts)
    return clean[:1].upper() + clean[1:]


def get_post_processor(model_id: str) -> Any:
    if model_id in POST_PROCESSOR_CACHE:
        return POST_PROCESSOR_CACHE[model_id]

    local_only = os.environ.get("MUESLI_ALLOW_MODEL_DOWNLOAD", "").strip() != "1"
    if local_only and not has_local_post_processor_files(model_id):
        raise RuntimeError(
            f"Qwen post-processor model is not downloaded locally: {model_id}. "
            "Set MUESLI_ALLOW_MODEL_DOWNLOAD=1 once, or install the model into the Muesli cache."
        )

    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, pipeline

    device = 0 if torch.cuda.is_available() else -1
    dtype = torch.float16 if device == 0 else torch.float32
    tokenizer = AutoTokenizer.from_pretrained(
        model_id,
        cache_dir=str(cache_dir()),
        local_files_only=local_only,
    )
    model = AutoModelForCausalLM.from_pretrained(
        model_id,
        cache_dir=str(cache_dir()),
        torch_dtype=dtype,
        device_map="auto" if device == 0 else None,
        local_files_only=local_only,
    )
    generator = pipeline(
        "text-generation",
        model=model,
        tokenizer=tokenizer,
        device=device if device >= 0 and not hasattr(model, "hf_device_map") else None,
    )
    POST_PROCESSOR_CACHE[model_id] = (tokenizer, generator, "cuda" if device == 0 else "cpu")
    return POST_PROCESSOR_CACHE[model_id]


def has_local_post_processor_files(model_id: str) -> bool:
    def has_weight_file(path: Path) -> bool:
        names = ("*.safetensors", "*.bin")
        for pattern in names:
            for file_path in path.rglob(pattern):
                if ".no_exist" in file_path.parts or file_path.name.endswith(".incomplete"):
                    continue
                try:
                    if file_path.stat().st_size > 1_000_000:
                        return True
                except OSError:
                    continue
        return False

    model_path = Path(model_id)
    if model_path.exists():
        return has_weight_file(model_path)

    cache_name = "models--" + model_id.replace("/", "--")
    model_cache = cache_dir() / cache_name
    return model_cache.exists() and has_weight_file(model_cache)


def postprocess_text(text: str, context: str, model_id: str, system_prompt: str = "") -> dict[str, Any]:
    started_at = time.perf_counter()
    if not text.strip():
        return {
            "transcriptText": "",
            "detectedLanguage": "unknown",
            "durationMs": 0,
            "segments": [],
            "warnings": ["Post-processing skipped: empty transcript."],
        }

    try:
        model_started_at = time.perf_counter()
        tokenizer, generator, device = get_post_processor(model_id)
        model_ms = (time.perf_counter() - model_started_at) * 1000
        messages = cleanup_prompt(text, context, system_prompt)
        if hasattr(tokenizer, "apply_chat_template"):
            prompt = tokenizer.apply_chat_template(
                messages,
                tokenize=False,
                add_generation_prompt=True,
            )
        else:
            prompt = "\n\n".join(message["content"] for message in messages)

        infer_started_at = time.perf_counter()
        output = generator(
            prompt,
            max_new_tokens=384,
            do_sample=False,
            temperature=0.0,
            return_full_text=False,
        )
        infer_ms = (time.perf_counter() - infer_started_at) * 1000
        cleaned = output[0].get("generated_text", "").strip()
        if not cleaned:
            cleaned = fallback_cleanup(text)
        total_ms = (time.perf_counter() - started_at) * 1000
        return {
            "transcriptText": cleaned,
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                "Post-processor: qwen",
                f"Post-processor model: {model_id}",
                f"Post-processor backend: {device}",
                f"Timing postprocess model ms: {model_ms:.1f}",
                f"Timing postprocess infer ms: {infer_ms:.1f}",
                f"Timing postprocess total ms: {total_ms:.1f}",
            ],
        }
    except ModuleNotFoundError as exc:
        return {
            "transcriptText": fallback_cleanup(text),
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                "Post-processor fallback cleanup used.",
                f"Qwen dependencies missing: {exc}",
            ],
        }
    except Exception as exc:
        return {
            "transcriptText": fallback_cleanup(text),
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                "Post-processor fallback cleanup used.",
                f"Qwen post-processing failed: {exc}",
            ],
        }


def transcribe_whisper(
    title: str,
    input_path: str,
    source: str,
    model_profile: str,
    transcription_mode: str,
    language_hint: str,
    audio_base64: str,
) -> dict[str, Any]:
    from faster_whisper.audio import decode_audio

    started_at = time.perf_counter()
    model_started_at = time.perf_counter()
    model = get_whisper_model(model_profile)
    model_ms = (time.perf_counter() - model_started_at) * 1000

    audio_input: Any = input_path
    if audio_base64.strip():
        audio_input = decode_audio(
            io.BytesIO(base64.b64decode(audio_base64)),
            sampling_rate=16000,
        )

    is_preview = transcription_mode == "preview"
    is_microphone_final = source == "microphone" and not is_preview
    options: dict[str, Any] = {
        "vad_filter": not is_microphone_final,
        "vad_parameters": {"min_silence_duration_ms": 220 if is_preview else 500},
        "beam_size": 1 if is_preview else 5,
        "best_of": 1 if is_preview else 3,
        "condition_on_previous_text": not is_preview,
        "temperature": 0,
    }
    if is_microphone_final:
        options["beam_size"] = 1
        options["best_of"] = 1
        options["condition_on_previous_text"] = False
        options["without_timestamps"] = model_profile in {"tiny", "base"}
    if language_hint.strip():
        options["language"] = language_hint.strip()

    infer_started_at = time.perf_counter()
    segments, info = model.transcribe(audio_input, **options)
    segment_list = list(segments)
    infer_ms = (time.perf_counter() - infer_started_at) * 1000

    raw_text = " ".join(segment.text.strip() for segment in segment_list).strip()
    duration_ms = int(getattr(info, "duration", 0) * 1000)
    total_ms = (time.perf_counter() - started_at) * 1000
    segment_diagnostics = [
        {
            "text": segment.text.strip(),
            "avg_logprob": getattr(segment, "avg_logprob", None),
            "no_speech_prob": getattr(segment, "no_speech_prob", None),
            "compression_ratio": getattr(segment, "compression_ratio", None),
        }
        for segment in segment_list
    ]
    text = (
        ""
        if is_silence_hallucination(raw_text, source, segment_diagnostics)
        else raw_text
    )

    return {
        "transcriptText": text,
        "detectedLanguage": getattr(info, "language", "unknown"),
        "durationMs": duration_ms,
        "segments": [
            {
                "id": f"seg_{index}",
                "speaker": "Speaker ?",
                "startMs": int(segment.start * 1000),
                "endMs": int(segment.end * 1000),
                "text": segment.text.strip(),
            }
            for index, segment in enumerate(segment_list, start=1)
        ],
        "warnings": [
            "Speaker diarization is not enabled yet.",
            "ASR engine: whisper",
            f"ASR backend: {MODEL_RUNTIME_INFO.get(MODEL_MAP.get(model_profile, 'base'), 'unknown')}",
            f"Model cache directory: {cache_dir()}",
            f"Input path: {input_path}",
            f"Input exists: {Path(input_path).exists() if not audio_base64.strip() else 'memory'}",
            f"Input bytes: {Path(input_path).stat().st_size if Path(input_path).exists() else 0}",
            f"Detected duration ms: {duration_ms}",
            f"Segment count: {len(segment_list)}",
            f"Raw transcript: {raw_text}",
            f"Silence hallucination filtered: {text != raw_text}",
            f"Segment diagnostics: {segment_diagnostics}",
            f"Decode options: {options}",
            f"Timing worker model ms: {model_ms:.1f}",
            f"Timing worker infer ms: {infer_ms:.1f}",
            f"Timing worker total ms: {total_ms:.1f}",
        ],
    }


def get_parakeet_model() -> Any:
    if "parakeet-v3" in PARAKEET_CACHE:
        return PARAKEET_CACHE["parakeet-v3"]

    if not has_nvidia_runtime():
        raise RuntimeError("NVIDIA runtime not detected. Parakeet requires an NVIDIA CUDA machine.")

    from nemo.collections.asr.models import ASRModel

    model = ASRModel.from_pretrained("nvidia/parakeet-tdt-0.6b-v3")
    try:
        model = model.cuda()
    except Exception:
        pass
    PARAKEET_CACHE["parakeet-v3"] = model
    return model


def transcribe_parakeet(
    title: str,
    input_path: str,
    source: str,
    audio_base64: str,
) -> dict[str, Any]:
    started_at = time.perf_counter()
    model_started_at = time.perf_counter()
    model = get_parakeet_model()
    model_ms = (time.perf_counter() - model_started_at) * 1000

    temp_path: Path | None = None
    audio_path = Path(input_path)
    if audio_base64.strip():
        temp_path = cache_dir() / f"parakeet-{int(time.time() * 1000)}.wav"
        temp_path.write_bytes(base64.b64decode(audio_base64))
        audio_path = temp_path

    infer_started_at = time.perf_counter()
    output = model.transcribe([str(audio_path)])
    infer_ms = (time.perf_counter() - infer_started_at) * 1000
    if temp_path is not None:
        try:
            temp_path.unlink()
        except OSError:
            pass

    first = output[0] if output else ""
    text = getattr(first, "text", first)
    if isinstance(text, list):
        text = " ".join(str(part) for part in text)
    text = str(text).strip()
    total_ms = (time.perf_counter() - started_at) * 1000

    return {
        "transcriptText": text,
        "detectedLanguage": "en",
        "durationMs": 0,
        "segments": [
            {
                "id": "seg_1",
                "speaker": "Speaker ?",
                "startMs": 0,
                "endMs": 0,
                "text": text,
            }
        ] if text else [],
        "warnings": [
            "Speaker diarization is not enabled yet.",
            "ASR engine: parakeet-v3",
            "ASR backend: NVIDIA NeMo / CUDA",
            f"Model cache directory: {cache_dir()}",
            f"Input path: {input_path}",
            f"Source: {source}",
            f"Timing worker model ms: {model_ms:.1f}",
            f"Timing worker infer ms: {infer_ms:.1f}",
            f"Timing worker total ms: {total_ms:.1f}",
        ],
    }
def is_silence_hallucination(text: str, source: str, diagnostics: list[dict[str, Any]]) -> bool:
    if source != "microphone":
        return False
    normalized = " ".join(
        text.lower().strip().strip(".!?").replace("’", "'").split()
    )
    silence_phrases = {
        "",
        "the end",
        "thank you",
        "thanks",
        "thanks for watching",
        "thank you for watching",
        "bye",
        "goodbye",
        "transcribe the user's spoken dictation exactly do not add captions closings or phrases",
        "transcribe the user's spoken dictation exactly do not add captions closings or phrases that were not spoken",
    }
    if normalized in silence_phrases:
        return True
    if len(diagnostics) == 1:
        no_speech_prob = diagnostics[0].get("no_speech_prob")
        avg_logprob = diagnostics[0].get("avg_logprob")
        if isinstance(no_speech_prob, (int, float)) and no_speech_prob >= 0.6:
            return True
        if (
            isinstance(no_speech_prob, (int, float))
            and isinstance(avg_logprob, (int, float))
            and no_speech_prob >= 0.5
            and avg_logprob < -0.6
        ):
            return True
    return False


def transcribe(
    title: str,
    input_path: str,
    source: str,
    asr_engine: str,
    model_profile: str,
    transcription_mode: str,
    language_hint: str,
    audio_base64: str,
) -> dict[str, Any]:
    if asr_engine == "parakeet-v3":
        try:
            return transcribe_parakeet(title, input_path, source, audio_base64)
        except Exception as exc:
            raise RuntimeError(f"Parakeet transcription failed: {exc}") from exc

    try:
        return transcribe_whisper(
            title,
            input_path,
            source,
            model_profile,
            transcription_mode,
            language_hint,
            audio_base64,
        )
    except ModuleNotFoundError as exc:
        raise RuntimeError(f"Whisper transcription dependency is unavailable: {exc}") from exc
    except Exception as exc:
        raise RuntimeError(f"Whisper transcription failed: {exc}") from exc


def get_diarization_pipeline() -> Any:
    if "diarization" in DIARIZATION_CACHE:
        return DIARIZATION_CACHE["diarization"]

    # Monkey-patch for newer torchaudio versions that removed audio backend APIs
    import torchaudio
    import sys
    import types

    if not hasattr(torchaudio, "set_audio_backend"):
        torchaudio.set_audio_backend = lambda backend: None
    if not hasattr(torchaudio, "get_audio_backend"):
        torchaudio.get_audio_backend = lambda: "soundfile"
    if not hasattr(torchaudio, "list_audio_backends"):
        torchaudio.list_audio_backends = lambda: ["soundfile"]
    if not hasattr(torchaudio, "backend"):
        backend_mod = types.ModuleType("torchaudio.backend")
        backend_mod.soundfile = types.ModuleType("torchaudio.backend.soundfile")
        backend_mod.sox = types.ModuleType("torchaudio.backend.sox")
        backend_mod.common = types.ModuleType("torchaudio.backend.common")
        from collections import namedtuple
        backend_mod.common.AudioMetaData = namedtuple(
            "AudioMetaData",
            ["sample_rate", "num_frames", "num_channels", "bits_per_sample", "encoding"]
        )
        torchaudio.backend = backend_mod
        sys.modules["torchaudio.backend"] = backend_mod
        sys.modules["torchaudio.backend.soundfile"] = backend_mod.soundfile
        sys.modules["torchaudio.backend.sox"] = backend_mod.sox
        sys.modules["torchaudio.backend.common"] = backend_mod.common

    # Monkey-patch for NumPy 2.0+ compatibility (pyannote.audio 3.1.1 uses np.NaN, some deps use np.NAN)
    import numpy as np
    if not hasattr(np, "NaN"):
        np.NaN = np.nan
    if not hasattr(np, "NAN"):
        np.NAN = np.nan

    # Monkey-patch for huggingface-hub 1.0+ compatibility
    # pyannote.audio 3.1.1 passes use_auth_token, which was renamed to token
    import huggingface_hub
    _orig_hf_hub_download = huggingface_hub.hf_hub_download
    def _patched_hf_hub_download(*args, **kwargs):
        if "use_auth_token" in kwargs:
            kwargs["token"] = kwargs.pop("use_auth_token")
        return _orig_hf_hub_download(*args, **kwargs)
    huggingface_hub.hf_hub_download = _patched_hf_hub_download

    # Monkey-patch for PyTorch 2.6+ weights_only default change
    # pyannote.audio 3.1.1 checkpoint files need weights_only=False
    import torch
    import functools
    _orig_torch_load = torch.load
    @functools.wraps(_orig_torch_load)
    def _patched_torch_load(*args, **kwargs):
        kwargs["weights_only"] = False
        return _orig_torch_load(*args, **kwargs)
    torch.load = _patched_torch_load

    from pyannote.audio import Pipeline

    pipeline = Pipeline.from_pretrained(
        "pyannote/speaker-diarization-3.1",
        use_auth_token=os.environ.get("HF_TOKEN"),
    )
    DIARIZATION_CACHE["diarization"] = pipeline
    return pipeline


def diarize_audio(input_path: str) -> dict[str, Any]:
    import soundfile as sf
    import numpy as np

    warnings = []
    if not os.environ.get("HF_TOKEN"):
        warnings.append(
            "HF_TOKEN is not set; pyannote gated models usually require a Hugging Face token with accepted model access."
        )

    started_at = time.perf_counter()
    try:
        pipeline = get_diarization_pipeline()
        model_ms = (time.perf_counter() - started_at) * 1000

        # Load audio with soundfile to bypass torchaudio backend issues
        waveform, sample_rate = sf.read(input_path, dtype="float32")
        if waveform.ndim == 1:
            waveform = waveform.reshape(1, -1)
        else:
            waveform = waveform.T

        import torch
        waveform_tensor = torch.from_numpy(waveform)

        infer_started_at = time.perf_counter()
        result = pipeline({"waveform": waveform_tensor, "sample_rate": sample_rate})
        infer_ms = (time.perf_counter() - infer_started_at) * 1000

        segments = []
        for turn, _, speaker in result.itertracks(yield_label=True):
            segments.append(
                {
                    "id": f"diarize_{len(segments)}",
                    "speaker": speaker,
                    "startMs": int(turn.start * 1000),
                    "endMs": int(turn.end * 1000),
                    "text": "",
                }
            )

        total_ms = (time.perf_counter() - started_at) * 1000
        warnings += [
            "ASR engine: diarization",
            f"Diarization segments: {len(segments)}",
            f"Timing diarization model ms: {model_ms:.1f}",
            f"Timing diarization infer ms: {infer_ms:.1f}",
            f"Timing diarization total ms: {total_ms:.1f}",
        ]
        return {
            "transcriptText": f"Diarized {len(segments)} segments.",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": segments,
            "warnings": warnings,
        }
    except ModuleNotFoundError as exc:
        warnings += [
            "Diarization skipped: pyannote.audio not installed.",
            f"Install with: pip install -r requirements-diarization.txt",
            f"Error: {exc}",
        ]
        return {
            "transcriptText": "",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": warnings,
        }
    except Exception as exc:
        warnings += [
            "Diarization failed.",
            f"Error: {exc}",
        ]
        return {
            "transcriptText": "",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": warnings,
        }


def download_model(kind: str, model: str) -> dict[str, Any]:
    started_at = time.perf_counter()
    if kind == "whisper":
        get_whisper_model(model)
        model_name = MODEL_MAP.get(model, "base")
        return {
            "transcriptText": f"Whisper model '{model}' is ready.",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                f"Downloaded/loaded Whisper model: {model_name}",
                f"Model cache directory: {cache_dir()}",
                f"Timing model ready ms: {(time.perf_counter() - started_at) * 1000:.1f}",
            ],
        }
    if kind == "parakeet":
        get_parakeet_model()
        return {
            "transcriptText": "Parakeet v3 is ready.",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": ["Parakeet v3 loaded through NVIDIA NeMo."],
        }

    if kind == "postprocess":
        os.environ["MUESLI_ALLOW_MODEL_DOWNLOAD"] = "1"
        get_post_processor(model)
        return {
            "transcriptText": f"Post-processor model '{model}' is ready.",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                f"Downloaded/loaded post-processor model: {model}",
                f"Model cache directory: {cache_dir()}",
                f"Timing model ready ms: {(time.perf_counter() - started_at) * 1000:.1f}",
            ],
        }

    if kind == "diarization":
        os.environ["MUESLI_ALLOW_MODEL_DOWNLOAD"] = "1"
        get_diarization_pipeline()
        return {
            "transcriptText": "Diarization pipeline is ready.",
            "detectedLanguage": "en",
            "durationMs": 0,
            "segments": [],
            "warnings": [
                "Diarization model: pyannote/speaker-diarization-3.1",
                f"Model cache directory: {cache_dir()}",
                f"Timing model ready ms: {(time.perf_counter() - started_at) * 1000:.1f}",
            ],
        }

    raise RuntimeError(f"Unsupported model kind: {kind}")


def run_server() -> int:
    for line in sys.stdin:
        raw = line.strip()
        if not raw:
            continue
        request = json.loads(raw)
        request_id = request.get("id", "unknown")
        command = request.get("command")
        payload = request.get("payload", {})
        try:
            if command == "transcribe":
                result = transcribe(
                    payload["title"],
                    payload["input_path"],
                    payload["source"],
                    payload.get("asr_engine", "whisper"),
                    payload["model"],
                    payload.get("transcription_mode", "final"),
                    payload.get("language_hint", ""),
                    payload.get("audio_base64", ""),
                )
                print(json.dumps({"id": request_id, "ok": True, "result": result}), flush=True)
                continue
            if command == "postprocess":
                result = postprocess_text(
                    payload.get("text", ""),
                    payload.get("context", "dictation"),
                    payload.get("model", os.environ.get("MUESLI_POST_PROCESSOR_MODEL", "Qwen/Qwen2.5-3B-Instruct")),
                    payload.get("system_prompt", ""),
                )
                print(json.dumps({"id": request_id, "ok": True, "result": result}), flush=True)
                continue
            if command == "download_model":
                result = download_model(
                    payload.get("kind", "whisper"),
                    payload.get("model", "base"),
                )
                print(json.dumps({"id": request_id, "ok": True, "result": result}), flush=True)
                continue
            if command == "diarize":
                result = diarize_audio(payload["input_path"])
                print(json.dumps({"id": request_id, "ok": True, "result": result}), flush=True)
                continue
            print(
                json.dumps(
                    {
                        "id": request_id,
                        "ok": False,
                        "error": f"Unsupported command: {command}",
                    }
                ),
                flush=True,
            )
        except Exception as exc:
            print(json.dumps({"id": request_id, "ok": False, "error": str(exc)}), flush=True)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    transcribe_parser = subparsers.add_parser("transcribe")
    transcribe_parser.add_argument("--input", required=True)
    transcribe_parser.add_argument("--title", required=True)
    transcribe_parser.add_argument("--source", required=True)
    transcribe_parser.add_argument("--asr-engine", default="whisper")
    transcribe_parser.add_argument("--model", required=True)
    transcribe_parser.add_argument("--transcription-mode", default="final")
    transcribe_parser.add_argument("--language-hint", default="")
    transcribe_parser.add_argument("--audio-base64", default="")
    postprocess_parser = subparsers.add_parser("postprocess")
    postprocess_parser.add_argument("--text", required=True)
    postprocess_parser.add_argument("--context", default="dictation")
    postprocess_parser.add_argument(
        "--model",
        default=os.environ.get("MUESLI_POST_PROCESSOR_MODEL", "Qwen/Qwen2.5-3B-Instruct"),
    )
    postprocess_parser.add_argument("--system-prompt", default="")
    download_parser = subparsers.add_parser("download-model")
    download_parser.add_argument("--kind", choices=["whisper", "postprocess", "parakeet"], default="whisper")
    download_parser.add_argument("--model", required=True)
    subparsers.add_parser("server")

    args = parser.parse_args()

    if args.command == "transcribe":
        payload = transcribe(
            args.title,
            args.input,
            args.source,
            args.asr_engine,
            args.model,
            args.transcription_mode,
            args.language_hint,
            args.audio_base64,
        )
        print(json.dumps(payload), flush=True)
        return 0

    if args.command == "postprocess":
        payload = postprocess_text(args.text, args.context, args.model, args.system_prompt)
        print(json.dumps(payload), flush=True)
        return 0

    if args.command == "download-model":
        payload = download_model(args.kind, args.model)
        print(json.dumps(payload), flush=True)
        return 0

    if args.command == "server":
        return run_server()

    return 1


if __name__ == "__main__":
    raise SystemExit(main())
