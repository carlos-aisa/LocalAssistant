from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import tomllib


MAX_CPU_THREADS = 16
DEFAULT_MODEL_LOAD_TIMEOUT_SECONDS = 60
DEFAULT_SYNTHESIS_TIMEOUT_SECONDS = 15


@dataclass(frozen=True)
class VoiceProfile:
    identifier: str
    voice: str
    language: str


@dataclass(frozen=True)
class ServiceConfig:
    port: int
    weights_directory: Path
    cpu_threads: int
    model_load_timeout_seconds: int
    synthesis_timeout_seconds: int
    profiles: dict[str, VoiceProfile]

    @staticmethod
    def load(path: Path) -> "ServiceConfig":
        with path.open("rb") as config_file:
            raw = tomllib.load(config_file)

        return ServiceConfig.from_mapping(raw)

    @staticmethod
    def from_mapping(raw: object) -> "ServiceConfig":
        if not isinstance(raw, dict):
            raise ValueError("Configuration must be a TOML object.")

        port = _require_int(raw, "port", minimum=1, maximum=65535)
        weights_directory = Path(_require_string(raw, "weights_directory"))
        available_processors = os.cpu_count() or 1
        maximum_threads = min(MAX_CPU_THREADS, available_processors)
        cpu_threads = _require_int(raw, "cpu_threads", minimum=1, maximum=maximum_threads)
        load_timeout = _optional_int(
            raw,
            "model_load_timeout_seconds",
            DEFAULT_MODEL_LOAD_TIMEOUT_SECONDS,
            minimum=1,
            maximum=300,
        )
        synthesis_timeout = _optional_int(
            raw,
            "synthesis_timeout_seconds",
            DEFAULT_SYNTHESIS_TIMEOUT_SECONDS,
            minimum=1,
            maximum=60,
        )
        profiles = _parse_profiles(raw.get("profiles"))

        return ServiceConfig(
            port=port,
            weights_directory=weights_directory,
            cpu_threads=cpu_threads,
            model_load_timeout_seconds=load_timeout,
            synthesis_timeout_seconds=synthesis_timeout,
            profiles=profiles,
        )


def _require_string(raw: dict[str, object], key: str) -> str:
    value = raw.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{key} must be a non-empty string.")

    return value


def _require_int(raw: dict[str, object], key: str, minimum: int, maximum: int) -> int:
    value = raw.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise ValueError(f"{key} must be an integer between {minimum} and {maximum}.")

    return value


def _optional_int(
    raw: dict[str, object],
    key: str,
    default: int,
    minimum: int,
    maximum: int,
) -> int:
    if key not in raw:
        return default

    return _require_int(raw, key, minimum, maximum)


def _parse_profiles(raw: object) -> dict[str, VoiceProfile]:
    if not isinstance(raw, dict) or not raw:
        raise ValueError("profiles must be a non-empty TOML table.")

    profiles: dict[str, VoiceProfile] = {}
    for identifier, profile in raw.items():
        if not isinstance(identifier, str) or not identifier:
            raise ValueError("A profile identifier is invalid.")
        if not isinstance(profile, dict):
            raise ValueError(f"Profile {identifier} must be a table.")

        voice = profile.get("voice")
        language = profile.get("language")
        if not isinstance(voice, str) or not voice:
            raise ValueError(f"Profile {identifier} has an invalid voice.")
        if language not in {"es", "en"}:
            raise ValueError(f"Profile {identifier} has an invalid language.")

        profiles[identifier] = VoiceProfile(identifier, voice, language)

    return profiles
