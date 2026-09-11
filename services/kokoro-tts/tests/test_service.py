from __future__ import annotations

import asyncio
import io
import logging
import threading
import wave

from aiohttp import ClientSession
from aiohttp.test_utils import TestServer
import pytest

from localassistant_kokoro_tts.config import ServiceConfig
from localassistant_kokoro_tts.service import KokoroTtsService, StaticSecretProvider


SECRET = bytes(range(32))
AUTHORIZATION = "Bearer AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8"


class FakeEngine:
    def __init__(self) -> None:
        self.load_started = threading.Event()
        self.allow_load = threading.Event()
        self.synthesis_started = threading.Event()
        self.allow_synthesis = threading.Event()
        self.load_error: Exception | None = None
        self.synthesis_error: Exception | None = None
        self.calls: list[tuple[str, str, str, float, int]] = []

    def load(self) -> None:
        self.load_started.set()
        self.allow_load.wait(timeout=2)
        if self.load_error is not None:
            raise self.load_error

    def synthesize(self, text: str, voice: str, language: str, speed: float, volume: int) -> bytes:
        self.calls.append((text, voice, language, speed, volume))
        self.synthesis_started.set()
        self.allow_synthesis.wait(timeout=2)
        if self.synthesis_error is not None:
            raise self.synthesis_error
        return _wav()


async def _start(
    engine: FakeEngine | None = None,
    *,
    load_timeout: int = 60,
    synthesis_timeout: int = 15,
    espeak_version: str = "eSpeak NG 1.52",
    secret: bytes = SECRET,
) -> tuple[ClientSession, TestServer, FakeEngine]:
    selected_engine = engine or FakeEngine()
    config = ServiceConfig.from_mapping(
        {
            "port": 57321,
            "weights_directory": "C:/weights",
            "cpu_threads": 1,
            "model_load_timeout_seconds": load_timeout,
            "synthesis_timeout_seconds": synthesis_timeout,
            "profiles": {
                "jarvis-es": {"voice": "em_alex", "language": "es"},
            },
        }
    )
    service = KokoroTtsService(
        config,
        StaticSecretProvider(secret),
        selected_engine,
        espeak_version=lambda: espeak_version,
    )
    server = TestServer(service.create_app())
    await server.start_server()
    return ClientSession(), server, selected_engine


async def _close(session: ClientSession, server: TestServer) -> None:
    await session.close()
    await server.close()


async def _wait_ready(session: ClientSession, server: TestServer, engine: FakeEngine) -> None:
    engine.allow_load.set()
    for _ in range(40):
        response = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
        body = await response.json()
        if body["status"] == "ready":
            return
        await asyncio.sleep(0.01)
    raise AssertionError("The fake service did not become ready.")


async def test_health_reports_loading_then_ready_and_contract_version() -> None:
    session, server, engine = await _start()
    try:
        response = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
        assert response.status == 200
        assert (await response.json()) == {"status": "loading", "apiVersion": "v1"}

        await _wait_ready(session, server, engine)
    finally:
        engine.allow_load.set()
        await _close(session, server)


async def test_health_remains_responsive_and_second_speech_is_busy() -> None:
    session, server, engine = await _start()
    try:
        await _wait_ready(session, server, engine)
        request = session.post(server.make_url("/v1/speech"), headers={"Authorization": AUTHORIZATION}, json=_speech())
        first_response_task = asyncio.create_task(request)
        assert await asyncio.to_thread(engine.synthesis_started.wait, 1)

        health = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
        assert (await health.json())["status"] == "busy"

        busy = await session.post(server.make_url("/v1/speech"), headers={"Authorization": AUTHORIZATION}, json=_speech())
        assert busy.status == 503
        assert busy.headers["Retry-After"] == "1"
        assert (await busy.json())["code"] == "service_busy"

        engine.allow_synthesis.set()
        first = await first_response_task
        assert first.status == 200
        assert first.headers["Content-Type"].startswith("audio/wav")
    finally:
        engine.allow_load.set()
        engine.allow_synthesis.set()
        await _close(session, server)


async def test_voices_returns_only_registered_profiles() -> None:
    session, server, engine = await _start()
    try:
        await _wait_ready(session, server, engine)
        response = await session.get(server.make_url("/v1/voices"), headers={"Authorization": AUTHORIZATION})

        assert response.status == 200
        assert (await response.json()) == {"voices": [{"id": "jarvis-es", "language": "es"}]}
    finally:
        engine.allow_load.set()
        await _close(session, server)


@pytest.mark.parametrize(
    "authorization",
    [None, "Bearer invalid", "Bearer " + "a" * 42, "Basic anything"],
)
async def test_invalid_authorization_is_indistinguishable(authorization: str | None) -> None:
    session, server, engine = await _start()
    try:
        headers = {} if authorization is None else {"Authorization": authorization}
        response = await session.get(server.make_url("/health"), headers=headers)
        assert response.status == 401
        assert (await response.json())["code"] == "unauthorized"
    finally:
        engine.allow_load.set()
        await _close(session, server)


@pytest.mark.parametrize(
    "payload",
    [
        {},
        {"text": "hello", "voice": "unknown", "language": "es", "speed": 1.0, "volume": 50},
        {"text": "hello", "voice": "jarvis-es", "language": "en", "speed": 1.0, "volume": 50},
        {"text": "hello", "voice": "jarvis-es", "language": "es", "speed": 2.0, "volume": 50},
        {"text": "hello", "voice": "jarvis-es", "language": "es", "speed": 1.0, "volume": 101},
        {"text": "x" * 321, "voice": "jarvis-es", "language": "es", "speed": 1.0, "volume": 50},
    ],
)
async def test_speech_validates_exact_request_contract(payload: dict[str, object]) -> None:
    session, server, engine = await _start()
    try:
        await _wait_ready(session, server, engine)
        response = await session.post(server.make_url("/v1/speech"), headers={"Authorization": AUTHORIZATION}, json=payload)
        assert response.status == 400
        assert (await response.json())["code"].startswith("invalid_")
        assert engine.calls == []
    finally:
        engine.allow_load.set()
        engine.allow_synthesis.set()
        await _close(session, server)


async def test_speech_uses_profile_and_volume_without_logging_text(caplog: pytest.LogCaptureFixture) -> None:
    session, server, engine = await _start()
    secret_text = "Texto que no debe aparecer en el log"
    try:
        await _wait_ready(session, server, engine)
        engine.allow_synthesis.set()
        with caplog.at_level(logging.WARNING):
            response = await session.post(
                server.make_url("/v1/speech"),
                headers={"Authorization": AUTHORIZATION, "X-Kokoro-Correlation-Id": "untrusted"},
                json=_speech(text=secret_text, volume=0),
            )
        assert response.status == 200
        assert engine.calls == [(secret_text, "em_alex", "es", 1.0, 0)]
        identifier = response.headers["X-Kokoro-Correlation-Id"]
        assert identifier != "untrusted"
        assert len(identifier) == 36
        assert secret_text not in caplog.text
    finally:
        engine.allow_load.set()
        engine.allow_synthesis.set()
        await _close(session, server)


async def test_synthesis_failure_never_logs_submitted_text(caplog: pytest.LogCaptureFixture) -> None:
    session, server, engine = await _start()
    submitted_text = "Texto sensible que no debe llegar al registro"
    try:
        await _wait_ready(session, server, engine)
        engine.synthesis_error = RuntimeError("synthetic engine failure")
        engine.allow_synthesis.set()
        with caplog.at_level(logging.WARNING):
            response = await session.post(
                server.make_url("/v1/speech"),
                headers={"Authorization": AUTHORIZATION},
                json=_speech(text=submitted_text),
            )
        assert response.status == 502
        assert submitted_text not in caplog.text
    finally:
        engine.allow_load.set()
        engine.allow_synthesis.set()
        await _close(session, server)


async def test_load_failure_and_timeout_are_degraded() -> None:
    engine = FakeEngine()
    engine.load_error = RuntimeError("missing weights")
    engine.allow_load.set()
    session, server, started_engine = await _start(engine)
    try:
        for _ in range(40):
            response = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
            body = await response.json()
            if body["status"] == "degraded":
                assert body["code"] == "load_failed"
                break
            await asyncio.sleep(0.01)
        else:
            raise AssertionError("The service did not report load failure.")
    finally:
        started_engine.allow_load.set()
        await _close(session, server)


async def test_unsupported_espeak_version_degrades_service() -> None:
    session, server, engine = await _start(espeak_version="espeak 1.49")
    try:
        engine.allow_load.set()
        for _ in range(40):
            response = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
            body = await response.json()
            if body["status"] == "degraded":
                assert body["code"] == "load_failed"
                break
            await asyncio.sleep(0.01)
        else:
            raise AssertionError("The service did not report an eSpeak validation failure.")
    finally:
        engine.allow_load.set()
        await _close(session, server)


async def test_load_timeout_is_degraded_without_waiting_for_the_worker() -> None:
    session, server, engine = await _start(load_timeout=1)
    try:
        for _ in range(140):
            response = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
            body = await response.json()
            if body["status"] == "degraded":
                assert body["code"] == "load_timeout"
                break
            await asyncio.sleep(0.01)
        else:
            raise AssertionError("The service did not report load timeout.")
    finally:
        engine.allow_load.set()
        await _close(session, server)


async def test_synthesis_timeout_keeps_service_busy_until_worker_finishes() -> None:
    session, server, engine = await _start(synthesis_timeout=1)
    try:
        await _wait_ready(session, server, engine)
        response = await session.post(server.make_url("/v1/speech"), headers={"Authorization": AUTHORIZATION}, json=_speech())
        assert response.status == 504
        assert (await response.json())["code"] == "synthesis_timeout"

        health = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
        assert (await health.json())["status"] == "busy"

        engine.allow_synthesis.set()
        for _ in range(40):
            health = await session.get(server.make_url("/health"), headers={"Authorization": AUTHORIZATION})
            if (await health.json())["status"] == "ready":
                break
            await asyncio.sleep(0.01)
        else:
            raise AssertionError("The service stayed busy after synthesis completed.")
    finally:
        engine.allow_load.set()
        engine.allow_synthesis.set()
        await _close(session, server)


def _speech(text: str = "Hola", volume: int = 50) -> dict[str, object]:
    return {"text": text, "voice": "jarvis-es", "language": "es", "speed": 1.0, "volume": volume}


def _wav() -> bytes:
    with io.BytesIO() as stream:
        with wave.open(stream, "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(24_000)
            wav.writeframes(b"\x00\x00" * 24)
        return stream.getvalue()
