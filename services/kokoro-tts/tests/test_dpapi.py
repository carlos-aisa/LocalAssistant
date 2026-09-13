from __future__ import annotations

import struct

import pytest

from localassistant_kokoro_tts.dpapi import decode_secret_envelope, encode_secret_envelope


def test_strict_envelope_passes_only_the_protected_payload_to_dpapi() -> None:
    protected = b"protected"
    expected = bytes(range(32))
    envelope = b"LASKOK01" + struct.pack("<HBI", 1, 1, len(protected)) + protected

    assert decode_secret_envelope(envelope, lambda actual: expected if actual == protected else b"") == expected


@pytest.mark.parametrize(
    "envelope",
    [
        b"",
        b"LASKOK00" + b"\x00" * 7,
        b"LASKOK01" + struct.pack("<HBI", 2, 1, 1) + b"x",
        b"LASKOK01" + struct.pack("<HBI", 1, 2, 1) + b"x",
        b"LASKOK01" + struct.pack("<HBI", 1, 1, 0),
        b"LASKOK01" + struct.pack("<HBI", 1, 1, 2) + b"x",
        b"LASKOK01" + struct.pack("<HBI", 1, 1, 1_048_577),
    ],
)
def test_strict_envelope_rejects_corruption_before_unprotect(envelope: bytes) -> None:
    with pytest.raises(ValueError):
        decode_secret_envelope(envelope, lambda _: bytes(range(32)))


def test_strict_envelope_rejects_unprotected_secret_with_wrong_length() -> None:
    envelope = b"LASKOK01" + struct.pack("<HBI", 1, 1, 1) + b"x"

    with pytest.raises(ValueError):
        decode_secret_envelope(envelope, lambda _: b"wrong")


def test_encode_and_decode_share_the_strict_binary_envelope() -> None:
    secret = bytes(range(32))

    envelope = encode_secret_envelope(secret, lambda value: value[::-1])

    assert decode_secret_envelope(envelope, lambda value: value[::-1]) == secret


def test_encode_rejects_an_invalid_clear_or_protected_secret() -> None:
    with pytest.raises(ValueError):
        encode_secret_envelope(b"too short", lambda value: value)

    with pytest.raises(ValueError):
        encode_secret_envelope(bytes(range(32)), lambda _: b"")
