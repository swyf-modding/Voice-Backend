"""
Local model server for ScamWYF.VoiceBackend.

Two endpoints are all the mod needs:

    POST /asr    WAV body                     -> {"text": "...", "confidence": 0.0}
    POST /tts    JSON {text, language, ...}    -> 24 kHz mono WAV

Parakeet runs through onnx-asr so it works on CPU as well as GPU without the NeMo toolkit.
Chatterbox is optional: without the `tts` extra the ASR half still works, and /health says so.
"""

from __future__ import annotations

import io
import logging
import os
import re
import tempfile
import threading
import time
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import numpy as np
import soundfile as sf
from fastapi import Body, FastAPI, HTTPException, Query, Response
from pydantic import BaseModel

LOG = logging.getLogger("scamwyf.voicebackend")

SAMPLE_RATE = 24_000
ASR_SAMPLE_RATE = 16_000
PARAKEET_V2 = "nemo-parakeet-tdt-0.6b-v2"
PARAKEET_V3 = "nemo-parakeet-tdt-0.6b-v3"

DEVICE_MODE = os.environ.get("VOICE_DEVICE_MODE", "auto").strip().lower()
if DEVICE_MODE not in ("auto", "cpu", "gpu"):
    DEVICE_MODE = "auto"

# In priority order. A build only has the providers its onnxruntime wheel was compiled with;
# onnxruntime-gpu has CUDA, onnxruntime-rocm has ROCm, onnxruntime-directml has DML.
# TensorRT is deliberately not in the default list: onnxruntime-gpu advertises it even without the
# TensorRT libraries, and selecting it produces a long EP error before falling back. Set
# PARAKEET_PROVIDERS=TensorrtExecutionProvider,CPUExecutionProvider after installing TensorRT.
ONNX_GPU_PROVIDERS = [
    "CUDAExecutionProvider",
    "ROCMExecutionProvider",
    "DmlExecutionProvider",
    "OpenVINOExecutionProvider",
    "CoreMLExecutionProvider",
    "WebGpuExecutionProvider",
    "MIGraphXExecutionProvider",
]

# Tags Chatterbox Turbo treats as paralinguistic. Anything else in [brackets] is a game tag
# (pauses, transcript markup) and is removed before the model sees it.
SPOKEN_TAGS = re.compile(
    r"\[(laugh|chuckle|cough|sigh|gasp|groan|sniff|shush|clear\s+throat)\]",
    re.IGNORECASE,
)
BRACKET_TAG = re.compile(r"\[[^\]]*\]")


def _env_bool(name: str, default: bool) -> bool:
    value = os.environ.get(name)
    if value is None:
        return default
    return value.strip().lower() not in ("0", "false", "no", "off", "")


class AsrEngine:
    """Loads Parakeet v2/v3 lazily and serialises access to each ONNX session."""

    def __init__(self) -> None:
        self._models: Dict[str, object] = {}
        self._locks = {"v2": threading.Lock(), "v3": threading.Lock()}
        self._load_lock = threading.Lock()
        self._active_providers: List[str] = ["CPUExecutionProvider"]
        self._available_providers: Optional[List[str]] = None
        self._warned_no_gpu = False

    @property
    def quantization(self) -> Optional[str]:
        value = os.environ.get("PARAKEET_QUANTIZATION", "int8").strip().lower()
        return None if value in ("", "none", "off", "fp32") else value

    @property
    def available_providers(self) -> List[str]:
        if self._available_providers is None:
            try:
                import onnxruntime as ort

                self._available_providers = list(ort.get_available_providers())
            except Exception:
                LOG.warning("Could not query ONNX Runtime providers", exc_info=True)
                self._available_providers = ["CPUExecutionProvider"]
        return self._available_providers

    @property
    def providers(self) -> List[str]:
        raw = os.environ.get("PARAKEET_PROVIDERS", "").strip()
        if raw:
            requested = [part.strip() for part in raw.split(",") if part.strip()]
            return self._with_cpu(requested)

        if DEVICE_MODE == "cpu":
            return ["CPUExecutionProvider"]

        available = self.available_providers
        chosen = [provider for provider in ONNX_GPU_PROVIDERS if provider in available]
        if not chosen:
            if DEVICE_MODE == "gpu" and not self._warned_no_gpu:
                self._warned_no_gpu = True
                LOG.warning(
                    "Mode=gpu but this ONNX Runtime has no GPU provider (available: %s); "
                    "Parakeet will run on CPU.",
                    ", ".join(available),
                )
            return ["CPUExecutionProvider"]
        return self._with_cpu(chosen)

    @property
    def active_providers(self) -> List[str]:
        return list(self._active_providers)

    def _with_cpu(self, providers: List[str]) -> List[str]:
        output = []
        for provider in providers:
            if provider not in output:
                output.append(provider)
        if "CPUExecutionProvider" not in output:
            output.append("CPUExecutionProvider")
        return output

    def loaded(self) -> List[str]:
        return sorted(self._models)

    def _load(self, model_key: str):
        model_key = model_key.lower()
        if model_key not in ("v2", "v3"):
            raise ValueError("model must be v2 or v3")
        with self._load_lock:
            if model_key in self._models:
                return self._models[model_key]

            import onnx_asr

            name = PARAKEET_V2 if model_key == "v2" else PARAKEET_V3
            started = time.time()
            providers = self.providers
            LOG.info(
                "Loading %s (quantization=%s, providers=%s)…",
                name,
                self.quantization or "fp32",
                ", ".join(providers),
            )
            kwargs = {}
            if self.quantization:
                kwargs["quantization"] = self.quantization

            try:
                model = onnx_asr.load_model(name, providers=providers, **kwargs)
                self._active_providers = providers
            except Exception as exc:
                if providers == ["CPUExecutionProvider"]:
                    raise
                LOG.warning(
                    "Parakeet could not start with %s (%s); retrying on CPU.",
                    ", ".join(providers),
                    exc,
                )
                model = onnx_asr.load_model(name, providers=["CPUExecutionProvider"], **kwargs)
                self._active_providers = ["CPUExecutionProvider"]

            self._models[model_key] = model
            LOG.info("%s loaded in %.1fs", name, time.time() - started)
            return model

    def warm(self, model_key: str) -> None:
        self._load(model_key)

    def recognize(self, wav_bytes: bytes, language: str, model_key: str) -> Tuple[str, float]:
        model_key = model_key.lower()
        if model_key == "auto":
            model_key = "v2" if language.split("-")[0].lower() == "en" else "v3"

        waveform, rate = self._decode(wav_bytes)
        model = self._load(model_key)

        with self._locks[model_key]:
            result = model.recognize(waveform, sample_rate=rate, channel="mean")

        if isinstance(result, str):
            text = result
        else:
            text = getattr(result, "text", None) or str(result)
        return text.strip(), len(waveform) / float(rate or 1)

    @staticmethod
    def _decode(wav_bytes: bytes) -> Tuple[np.ndarray, int]:
        if not wav_bytes:
            raise ValueError("empty audio body")

        try:
            data, rate = sf.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=True)
        except Exception:
            # A last resort for a body that is not a WAV: let librosa figure it out.
            import librosa

            data, rate = librosa.load(io.BytesIO(wav_bytes), sr=None, mono=False)
            data = np.asarray(data, dtype=np.float32)
            if data.ndim == 1:
                data = data[:, None]
            else:
                data = data.T

        mono = data.mean(axis=1) if data.ndim > 1 else data
        mono = np.asarray(mono, dtype=np.float32)

        if rate != ASR_SAMPLE_RATE:
            import librosa

            mono = librosa.resample(mono, orig_sr=rate, target_sr=ASR_SAMPLE_RATE)
            rate = ASR_SAMPLE_RATE

        peak = float(np.max(np.abs(mono))) if mono.size else 0.0
        if peak > 0:
            mono = mono / max(1.0, peak)
        return mono, rate


class TtsRequest(BaseModel):
    text: str
    language: str = "en"
    voice_path: str = ""
    voice_b64: str = ""
    model: str = "auto"


class TtsEngine:
    """Loads Chatterbox Turbo / Multilingual lazily, one model at a time."""

    def __init__(self) -> None:
        self._models: Dict[str, object] = {}
        self._load_lock = threading.Lock()
        self._run_lock = threading.Lock()
        self._device: Optional[str] = None
        self._refs: Dict[Tuple[str, float, int], str] = {}
        self._ref_lock = threading.Lock()
        self._cache_dir = Path(tempfile.gettempdir()) / "scamwyf-voice-backend-refs"
        self._cache_dir.mkdir(parents=True, exist_ok=True)

    def installed(self) -> bool:
        import importlib.util

        return importlib.util.find_spec("chatterbox") is not None

    def loaded(self) -> List[str]:
        return sorted(self._models)

    @property
    def device(self) -> str:
        if self._device:
            return self._device

        explicit = os.environ.get("CHATTERBOX_DEVICE", "").strip().lower()
        if explicit and explicit != "auto":
            self._device = explicit
            return self._device

        if DEVICE_MODE == "cpu":
            self._device = "cpu"
            return self._device

        try:
            import torch

            if torch.cuda.is_available():
                self._device = "cuda"       # CUDA on NVIDIA, HIP/ROCm on AMD
            elif hasattr(torch, "xpu") and torch.xpu.is_available():
                self._device = "xpu"        # Intel XPU
            elif getattr(torch.backends, "mps", None) is not None and torch.backends.mps.is_available():
                self._device = "mps"        # Apple Silicon
            else:
                self._device = "cpu"
        except Exception:
            LOG.warning("Could not query torch accelerators", exc_info=True)
            self._device = "cpu"

        if DEVICE_MODE == "gpu" and self._device == "cpu":
            LOG.warning(
                "Mode=gpu but this torch build sees no CUDA/ROCm/XPU/MPS device; "
                "Chatterbox will run on CPU."
            )
        return self._device

    @property
    def backend(self) -> str:
        device = self.device
        if device == "cuda":
            try:
                import torch

                return "rocm" if getattr(torch.version, "hip", None) else "cuda"
            except Exception:
                return "cuda"
        return device

    def choose(self, requested: str, language: str) -> str:
        requested = (requested or os.environ.get("CHATTERBOX_MODEL", "auto")).strip().lower()
        if requested in ("turbo", "multilingual"):
            return requested
        return "turbo" if language.split("-")[0].lower() == "en" else "multilingual"

    def _from_pretrained(self, kind: str, device: str):
        if kind == "turbo":
            from chatterbox.tts_turbo import ChatterboxTurboTTS

            return ChatterboxTurboTTS.from_pretrained(device=device)

        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

        try:
            return ChatterboxMultilingualTTS.from_pretrained(device=device, t3_model="v3")
        except TypeError:
            # Older packages do not have the v3 selector.
            return ChatterboxMultilingualTTS.from_pretrained(device=device)

    def _load(self, kind: str):
        kind = kind.lower()
        with self._load_lock:
            if kind in self._models:
                return self._models[kind]

            if not self.installed():
                raise RuntimeError(
                    "Chatterbox is not installed. Run `uv sync --extra tts` in server/."
                )

            started = time.time()
            device = self.device
            LOG.info("Loading Chatterbox %s on %s…", kind, device)

            try:
                model = self._from_pretrained(kind, device)
            except Exception:
                if device == "cpu":
                    raise
                LOG.warning(
                    "Chatterbox could not load on %s; retrying on CPU.", device, exc_info=True
                )
                self._device = "cpu"
                model = self._from_pretrained(kind, "cpu")

            self._models[kind] = model
            LOG.info("Chatterbox %s loaded in %.1fs", kind, time.time() - started)
            return model

    def warm(self, language: str = "en") -> str:
        kind = self.choose("auto", language)
        try:
            self._load(kind)
            return kind
        except Exception:
            if kind == "multilingual":
                LOG.warning("Multilingual Chatterbox unavailable; falling back to Turbo.", exc_info=True)
                self._load("turbo")
                return "turbo"
            raise

    def synthesize(self, request: TtsRequest) -> Tuple[bytes, str, float]:
        text = self._clean_text(request.text)
        if not text:
            raise ValueError("text is empty after removing game tags")

        language = (request.language or "en").strip().lower()
        kind = self.choose(request.model, language)
        try:
            model = self._load(kind)
        except Exception:
            if kind != "multilingual":
                raise
            LOG.warning("Multilingual Chatterbox unavailable; using Turbo.", exc_info=True)
            kind = "turbo"
            model = self._load("turbo")

        reference = self._reference(request.voice_path, request.voice_b64)

        kwargs = {}
        if reference:
            kwargs["audio_prompt_path"] = reference
        if kind == "multilingual":
            kwargs["language_id"] = language

        started = time.time()
        with self._run_lock:
            try:
                wav = model.generate(text, **kwargs)
            except TypeError:
                # A checkpoint/version difference: retry with the smallest useful signature.
                kwargs.pop("language_id", None)
                wav = model.generate(text, **kwargs)

        audio = self._to_numpy(wav)
        rate = int(getattr(model, "sr", SAMPLE_RATE) or SAMPLE_RATE)
        if rate != SAMPLE_RATE:
            import librosa

            audio = librosa.resample(audio, orig_sr=rate, target_sr=SAMPLE_RATE)

        audio = self._normalise(audio)
        LOG.info(
            "Chatterbox %s: language=%s chars=%d audio=%.2fs in %.2fs",
            kind,
            language,
            len(text),
            len(audio) / float(SAMPLE_RATE),
            time.time() - started,
        )
        return self._encode(audio), kind, len(audio) / float(SAMPLE_RATE)

    # ---------------------------------------------------------------- helpers

    def _reference(self, voice_path: str, voice_b64: str) -> Optional[str]:
        path: Optional[Path] = None

        if voice_b64:
            try:
                import base64

                raw = base64.b64decode(voice_b64)
                handle, temp_path = tempfile.mkstemp(suffix=".wav", dir=str(self._cache_dir))
                with os.fdopen(handle, "wb") as stream:
                    stream.write(raw)
                path = Path(temp_path)
            except Exception:
                LOG.warning("Could not decode voice_b64; falling back to voice_path.", exc_info=True)

        if path is None and voice_path:
            candidate = Path(voice_path)
            if candidate.is_file():
                path = candidate

        if path is None:
            default = os.environ.get("CHATTERBOX_DEFAULT_VOICE", "").strip()
            if default and Path(default).is_file():
                path = Path(default)

        if path is None:
            return None

        try:
            stat = path.stat()
            key = (str(path.resolve()), stat.st_mtime, stat.st_size)
            with self._ref_lock:
                cached = self._refs.get(key)
            if cached and Path(cached).is_file():
                return cached

            data, rate = sf.read(str(path), dtype="float32", always_2d=True)
            mono = data.mean(axis=1) if data.ndim > 1 else data
            mono = self._trim_silence(np.asarray(mono, dtype=np.float32), rate)

            max_seconds = float(os.environ.get("CHATTERBOX_REFERENCE_SECONDS", "10"))
            if max_seconds > 0 and len(mono) > int(max_seconds * rate):
                mono = mono[: int(max_seconds * rate)]

            if rate != SAMPLE_RATE:
                import librosa

                mono = librosa.resample(mono, orig_sr=rate, target_sr=SAMPLE_RATE)
                rate = SAMPLE_RATE

            target = self._cache_dir / ("ref-" + str(abs(hash(key))) + ".wav")
            sf.write(str(target), mono, rate, subtype="PCM_16")
            with self._ref_lock:
                self._refs[key] = str(target)
            return str(target)
        except Exception:
            LOG.warning("Could not prepare reference voice %s", path, exc_info=True)
            return None

    @staticmethod
    def _trim_silence(audio: np.ndarray, rate: int) -> np.ndarray:
        if audio.size == 0:
            return audio

        frame = max(1, int(rate * 0.02))
        usable = (audio.size // frame) * frame
        if usable < frame:
            return audio

        frames = audio[:usable].reshape(-1, frame)
        rms = np.sqrt(np.mean(frames.astype(np.float32) ** 2, axis=1))
        peak = float(np.max(rms)) if rms.size else 0.0
        threshold = max(0.005, peak * 0.03)
        above = np.where(rms >= threshold)[0]
        if above.size == 0:
            return audio

        start = max(0, int(above[0]) * frame - int(rate * 0.1))
        end = min(audio.size, (int(above[-1]) + 1) * frame + int(rate * 0.15))
        return audio[start:end]

    @staticmethod
    def _to_numpy(wav) -> np.ndarray:
        if isinstance(wav, tuple):
            wav = wav[0]
        if hasattr(wav, "detach"):
            wav = wav.detach().cpu().float().numpy()
        audio = np.asarray(wav, dtype=np.float32)
        audio = np.squeeze(audio)
        if audio.ndim == 0:
            audio = audio.reshape(1)
        elif audio.ndim > 1:
            # [channels, samples] or [samples, channels] -> mono
            axis = 0 if audio.shape[0] <= 8 else 1
            audio = audio.mean(axis=axis)
        return np.ascontiguousarray(audio, dtype=np.float32)

    @staticmethod
    def _normalise(audio: np.ndarray) -> np.ndarray:
        if audio.size == 0:
            return audio
        audio = audio - float(np.mean(audio))
        peak = float(np.max(np.abs(audio)))
        if peak > 0:
            audio = audio / peak * 0.95

        fade = min(int(SAMPLE_RATE * 0.005), audio.size // 4)
        if fade > 0:
            ramp = np.linspace(0.0, 1.0, fade, dtype=np.float32)
            audio[:fade] *= ramp
            audio[-fade:] *= ramp[::-1]
        return np.clip(audio, -1.0, 1.0).astype(np.float32)

    @staticmethod
    def _encode(audio: np.ndarray) -> bytes:
        stream = io.BytesIO()
        sf.write(stream, audio, SAMPLE_RATE, format="WAV", subtype="PCM_16")
        return stream.getvalue()

    @staticmethod
    def _clean_text(text: str) -> str:
        if not text:
            return ""

        def keep_spoken_tag(match: re.Match) -> str:
            return match.group(0) if SPOKEN_TAGS.fullmatch(match.group(0)) else " "

        cleaned = BRACKET_TAG.sub(keep_spoken_tag, text)
        cleaned = cleaned.replace("\r", " ").replace("\n", " ")
        cleaned = re.sub(r"\s+", " ", cleaned).strip()
        return cleaned


app = FastAPI(title="ScamWYF Voice Backend", version="0.1.0")
asr = AsrEngine()
tts = TtsEngine()


@app.get("/health")
def health() -> dict:
    return {
        "ok": True,
        "service": "scamwyf-voice-backend",
        "device": {
            "mode": DEVICE_MODE,
            "torch": tts.device,
            "torch_backend": tts.backend,
            "onnx_active": asr.active_providers,
            "onnx_requested": asr.providers,
            "onnx_available": asr.available_providers,
        },
        "asr": {
            "loaded": asr.loaded(),
            "quantization": asr.quantization or "fp32",
            "providers": asr.active_providers,
            "requested_providers": asr.providers,
            "available_providers": asr.available_providers,
            "models": {"v2": PARAKEET_V2, "v3": PARAKEET_V3},
        },
        "tts": {
            "installed": tts.installed(),
            "loaded": tts.loaded(),
            "device": tts.device,
            "backend": tts.backend,
            "model": os.environ.get("CHATTERBOX_MODEL", "auto"),
        },
    }


@app.post("/asr")
def transcribe(
    body: bytes = Body(...),
    language: str = Query("en"),
    model: str = Query("auto"),
) -> dict:
    try:
        text, duration = asr.recognize(body, language, model)
        return {
            "text": text,
            "confidence": 0.0,
            "language": language,
            "model": model,
            "duration": round(duration, 2),
        }
    except Exception as exc:
        LOG.exception("ASR failed")
        raise HTTPException(status_code=500, detail=str(exc))


@app.post("/tts")
def speak(request: TtsRequest) -> Response:
    try:
        wav, kind, duration = tts.synthesize(request)
        return Response(
            content=wav,
            media_type="audio/wav",
            headers={
                "X-Chatterbox-Model": kind,
                "X-Audio-Seconds": f"{duration:.2f}",
            },
        )
    except ValueError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    except Exception as exc:
        LOG.exception("TTS failed")
        raise HTTPException(status_code=500, detail=str(exc))


@app.post("/warmup")
def warmup(
    asr_model: str = Query("v3"),
    tts_language: str = Query(""),
    do_tts: bool = Query(False),
) -> dict:
    names = [part.strip().lower() for part in (asr_model or "").split(",") if part.strip()]
    if "none" in names:
        names = []

    def worker() -> None:
        for name in names:
            try:
                asr.warm(name)
            except Exception:
                LOG.exception("Could not warm Parakeet %s", name)
        if do_tts or tts_language:
            if not tts.installed():
                LOG.warning("TTS warm-up skipped: Chatterbox is not installed.")
                return
            try:
                tts.warm(tts_language or "en")
            except Exception:
                LOG.exception("Could not warm Chatterbox")

    # Return immediately: a first model load can take minutes, and the game should keep loading
    # rather than holding an HTTP request open for it.
    threading.Thread(target=worker, name="voice-warmup", daemon=True).start()
    return {"started": True, "asr": names, "tts": bool(do_tts or tts_language)}


def main() -> None:
    import uvicorn

    handlers = []
    log_file = os.environ.get("VOICE_LOG_FILE", "").strip()
    if log_file:
        try:
            Path(log_file).parent.mkdir(parents=True, exist_ok=True)
            handlers.append(logging.FileHandler(log_file, mode="w", encoding="utf-8"))
        except Exception:
            # Fall back to the console below.
            pass
    if not handlers:
        handlers.append(logging.StreamHandler())

    logging.basicConfig(
        level=os.environ.get("LOG_LEVEL", "INFO").upper(),
        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
        handlers=handlers,
    )
    host = os.environ.get("VOICE_HOST", "127.0.0.1")
    port = int(os.environ.get("VOICE_PORT", "8010"))
    LOG.info(
        "Device mode=%s torch=%s (%s) onnx=%s",
        DEVICE_MODE,
        tts.device,
        tts.backend,
        ", ".join(asr.providers),
    )
    LOG.info("Listening on http://%s:%d", host, port)
    uvicorn.run(
        app,
        host=host,
        port=port,
        log_level=os.environ.get("LOG_LEVEL", "info").lower(),
        log_config=None,
    )


if __name__ == "__main__":
    main()
