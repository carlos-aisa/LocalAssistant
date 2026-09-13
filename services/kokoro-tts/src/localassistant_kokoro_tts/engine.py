from __future__ import annotations

import io
import os
from typing import Protocol
import wave


SAMPLE_RATE_HZ = 24_000
KOKORO_REPOSITORY_ID = "hexgrad/Kokoro-82M"


class SpeechEngine(Protocol):
    def load(self) -> None:
        """Loads all model resources without downloading them."""

    def synthesize(self, text: str, voice: str, language: str, speed: float, volume: int) -> bytes:
        """Returns a complete PCM WAV document."""


class KokoroSpeechEngine:
    """CPU-only adapter for the evaluated Kokoro package.

    The adapter is intentionally constructed by an explicit composition root in a
    later lot. Unit tests use a deterministic fake instead of importing Kokoro.
    """

    def __init__(self, weights_directory: str, cpu_threads: int) -> None:
        self._weights_directory = weights_directory
        self._cpu_threads = cpu_threads
        self._pipelines: dict[str, object] = {}

    def load(self) -> None:
        import torch

        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["HF_HOME"] = self._weights_directory
        torch.set_num_threads(self._cpu_threads)
        torch.set_num_interop_threads(1)

        # The constructor validates that the pre-provisioned local model is usable.
        self._get_pipeline("es")

    def synthesize(self, text: str, voice: str, language: str, speed: float, volume: int) -> bytes:
        pipeline = self._get_pipeline(language)
        audio_parts = []
        for result in pipeline(text, voice=voice, speed=speed):
            audio = result.audio if hasattr(result, "audio") else result[2]
            if audio is not None and audio.numel() > 0:
                audio_parts.append(audio.detach().cpu().numpy())

        if not audio_parts:
            raise RuntimeError("Kokoro produced no audio.")

        import numpy as np

        samples = np.concatenate(audio_parts)
        scaled = apply_volume_gain(samples, volume)
        pcm = (scaled * 32767.0).astype("<i2", copy=False).tobytes()
        return _encode_pcm_wav(pcm)

    def _get_pipeline(self, language: str) -> object:
        existing = self._pipelines.get(language)
        if existing is not None:
            return existing

        from kokoro import KPipeline

        language_code = {"es": "e", "en": "a"}[language]
        pipeline = KPipeline(
            lang_code=language_code,
            repo_id=KOKORO_REPOSITORY_ID,
            device="cpu",
        )
        self._pipelines[language] = pipeline
        return pipeline


def _encode_pcm_wav(pcm: bytes) -> bytes:
    with io.BytesIO() as destination:
        with wave.open(destination, "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(SAMPLE_RATE_HZ)
            wav.writeframes(pcm)
        return destination.getvalue()


def apply_volume_gain(samples: object, volume: int) -> object:
    """Applies attenuation only; the caller validates the public volume range."""
    import numpy as np

    gain = volume / 100.0
    return np.clip(samples * gain, -1.0, 1.0)
