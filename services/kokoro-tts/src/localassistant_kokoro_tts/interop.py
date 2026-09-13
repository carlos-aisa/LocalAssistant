from __future__ import annotations

import argparse
import os
from pathlib import Path

from .dpapi import DpapiSharedSecretProvider, encode_secret_envelope, protect_current_user


def main() -> int:
    parser = argparse.ArgumentParser(description="LocalAssistant Kokoro DPAPI interoperability helper")
    operation = parser.add_mutually_exclusive_group(required=True)
    operation.add_argument("--read", type=Path)
    operation.add_argument("--write", type=Path)
    arguments = parser.parse_args()

    if arguments.read is not None:
        secret = DpapiSharedSecretProvider(arguments.read).get_secret()
        return 0 if len(secret) == 32 else 1

    assert arguments.write is not None
    secret = os.urandom(32)
    arguments.write.write_bytes(encode_secret_envelope(secret, protect_current_user))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
