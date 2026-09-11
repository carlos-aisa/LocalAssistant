from __future__ import annotations

import argparse
from pathlib import Path

from .config import ServiceConfig
from .dpapi import DpapiSharedSecretProvider, WindowsKokoroProcessLock
from .engine import KokoroSpeechEngine
from .service import KokoroTtsService


def main() -> int:
    parser = argparse.ArgumentParser(description="LocalAssistant Kokoro TTS service")
    parser.add_argument("--config", required=True, type=Path)
    arguments = parser.parse_args()
    ServiceConfig.load(arguments.config)
    config = ServiceConfig.load(arguments.config)
    process_lock = WindowsKokoroProcessLock()
    if not process_lock.acquire():
        parser.error("Another Kokoro service or secret rotation already owns the shared process lock.")

    try:
        from aiohttp import web

        service = KokoroTtsService(
            config,
            DpapiSharedSecretProvider(),
            KokoroSpeechEngine(str(config.weights_directory), config.cpu_threads),
        )
        web.run_app(service.create_app(), host="127.0.0.1", port=config.port)
        return 0
    finally:
        process_lock.close()


if __name__ == "__main__":
    raise SystemExit(main())
