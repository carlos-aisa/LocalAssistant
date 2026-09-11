from __future__ import annotations

import numpy as np

from localassistant_kokoro_tts.engine import apply_volume_gain


def test_volume_zero_silences_samples() -> None:
    samples = np.array([-0.8, 0.0, 0.8], dtype=np.float32)

    assert np.array_equal(apply_volume_gain(samples, 0), np.zeros(3, dtype=np.float32))


def test_volume_intermediate_attenuates_without_amplification() -> None:
    samples = np.array([-0.8, 0.0, 0.8], dtype=np.float32)

    assert np.allclose(apply_volume_gain(samples, 50), np.array([-0.4, 0.0, 0.4], dtype=np.float32))


def test_volume_full_preserves_range_and_clamps_invalid_engine_samples() -> None:
    samples = np.array([-1.5, -1.0, 0.0, 1.0, 1.5], dtype=np.float32)

    result = apply_volume_gain(samples, 100)

    assert np.array_equal(result, np.array([-1.0, -1.0, 0.0, 1.0, 1.0], dtype=np.float32))
