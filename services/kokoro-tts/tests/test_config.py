from __future__ import annotations

import os

import pytest

from localassistant_kokoro_tts.config import MAX_CPU_THREADS, ServiceConfig


def test_configuration_accepts_valid_profiles() -> None:
    config = ServiceConfig.from_mapping(_configuration())

    assert config.port == 57321
    assert config.profiles["jarvis-es"].voice == "em_alex"


@pytest.mark.parametrize(
    ("key", "value"),
    [
        ("port", 0),
        ("port", True),
        ("weights_directory", ""),
        ("cpu_threads", 0),
        ("cpu_threads", min(MAX_CPU_THREADS, os.cpu_count() or 1) + 1),
    ],
)
def test_configuration_rejects_invalid_scalar_values(key: str, value: object) -> None:
    configuration = _configuration()
    configuration[key] = value

    with pytest.raises(ValueError):
        ServiceConfig.from_mapping(configuration)


def test_configuration_rejects_invalid_profile_language() -> None:
    configuration = _configuration()
    configuration["profiles"] = {"jarvis-es": {"voice": "em_alex", "language": "fr"}}

    with pytest.raises(ValueError):
        ServiceConfig.from_mapping(configuration)


def _configuration() -> dict[str, object]:
    return {
        "port": 57321,
        "weights_directory": "C:/weights",
        "cpu_threads": 1,
        "profiles": {
            "jarvis-es": {"voice": "em_alex", "language": "es"},
            "jarvis-en": {"voice": "am_adam", "language": "en"},
        },
    }
