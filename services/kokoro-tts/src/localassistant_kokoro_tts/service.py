from __future__ import annotations

import asyncio
import base64
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
import hmac
import logging
import os
import re
import shutil
import subprocess
import uuid

from aiohttp import web

from . import SERVICE_API_VERSION
from .config import ServiceConfig, VoiceProfile
from .engine import SpeechEngine


MAX_REQUEST_BYTES = 4 * 1024
MAX_TEXT_CHARACTERS = 320
MIN_SPEED = 0.8
MAX_SPEED = 1.2
_CANONICAL_BEARER = re.compile(r"^[A-Za-z0-9_-]{43}$")


class SharedSecretProvider:
    """Retrieves the unencoded 32-byte secret without exposing it to HTTP code."""

    def get_secret(self) -> bytes:
        raise NotImplementedError


@dataclass(frozen=True)
class StaticSecretProvider(SharedSecretProvider):
    """Test-only secret provider; production DPAPI composition is introduced in lot 2."""

    secret: bytes

    def get_secret(self) -> bytes:
        return self.secret


class KokoroTtsService:
    def __init__(
        self,
        config: ServiceConfig,
        secret_provider: SharedSecretProvider,
        engine: SpeechEngine,
        espeak_version: Callable[[], str] | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        self._config = config
        self._secret_provider = secret_provider
        self._engine = engine
        self._espeak_version = espeak_version or _get_espeak_version
        self._logger = logger or logging.getLogger(__name__)
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="kokoro-tts")
        self._state = "loading"
        self._state_code: str | None = None
        self._load_started = False
        self._synthesis_lock = asyncio.Lock()

    def create_app(self) -> web.Application:
        application = web.Application(client_max_size=MAX_REQUEST_BYTES)
        application.router.add_get("/health", self.health)
        application.router.add_get("/v1/voices", self.voices)
        application.router.add_post("/v1/speech", self.speech)
        application.on_startup.append(self._start_loading)
        application.on_cleanup.append(self._cleanup)
        return application

    async def _start_loading(self, _: web.Application) -> None:
        if self._load_started:
            return

        self._load_started = True
        try:
            if len(self._secret_provider.get_secret()) != 32:
                raise ValueError("The shared secret has an invalid length.")
        except Exception:
            self._state = "degraded"
            self._state_code = "secret_unavailable"
            self._logger.warning("Kokoro shared secret is unavailable.")
            return
        asyncio.create_task(self._load_engine(), name="kokoro-model-load")

    async def _cleanup(self, _: web.Application) -> None:
        self._executor.shutdown(wait=False, cancel_futures=False)

    async def _load_engine(self) -> None:
        loop = asyncio.get_running_loop()
        load_future = loop.run_in_executor(self._executor, self._load_with_espeak_check)
        try:
            await asyncio.wait_for(asyncio.shield(load_future), self._config.model_load_timeout_seconds)
        except TimeoutError:
            self._state = "degraded"
            self._state_code = "load_timeout"
            load_future.add_done_callback(_consume_background_exception)
        except Exception:
            self._state = "degraded"
            self._state_code = "load_failed"
            self._logger.warning("Kokoro model load failed.")
        else:
            self._state = "ready"
            self._state_code = None

    def _load_with_espeak_check(self) -> None:
        version = self._espeak_version()
        if not _is_supported_espeak_version(version):
            raise RuntimeError("eSpeak NG is not available.")
        self._engine.load()

    async def health(self, request: web.Request) -> web.Response:
        authorization_error = self._validate_authorization(request)
        if authorization_error is not None:
            return authorization_error

        state = "busy" if self._synthesis_lock.locked() and self._state == "ready" else self._state
        body: dict[str, str] = {"status": state, "apiVersion": SERVICE_API_VERSION}
        if self._state_code is not None:
            body["code"] = self._state_code
        return self._json(request, body)

    async def voices(self, request: web.Request) -> web.Response:
        authorization_error = self._validate_authorization(request)
        if authorization_error is not None:
            return authorization_error

        if self._state != "ready":
            return self._error(request, 503, "service_unavailable")

        voices = [
            {"id": profile.identifier, "language": profile.language}
            for profile in self._config.profiles.values()
        ]
        return self._json(request, {"voices": voices})

    async def speech(self, request: web.Request) -> web.Response:
        authorization_error = self._validate_authorization(request)
        if authorization_error is not None:
            return authorization_error

        if self._state != "ready":
            return self._error(request, 503, "service_unavailable")
        if self._synthesis_lock.locked():
            return self._error(request, 503, "service_busy", retry_after="1")

        if request.content_type != "application/json":
            return self._error(request, 400, "invalid_request")
        try:
            raw_body = await request.read()
        except web.HTTPRequestEntityTooLarge:
            return self._error(request, 413, "request_too_large")
        if len(raw_body) > MAX_REQUEST_BYTES:
            return self._error(request, 413, "request_too_large")
        try:
            payload = _strict_json_loads(raw_body.decode("utf-8"))
        except (UnicodeDecodeError, ValueError):
            return self._error(request, 400, "invalid_request")

        request_error, profile, text, speed, volume = _validate_speech_request(payload, self._config.profiles)
        if request_error is not None:
            return self._error(request, 400, request_error)
        assert profile is not None
        assert text is not None
        assert speed is not None
        assert volume is not None

        await self._synthesis_lock.acquire()
        loop = asyncio.get_running_loop()
        synthesis_future = loop.run_in_executor(
            self._executor,
            self._engine.synthesize,
            text,
            profile.voice,
            profile.language,
            speed,
            volume,
        )
        synthesis_future.add_done_callback(self._release_synthesis_lock)

        try:
            wav = await asyncio.wait_for(asyncio.shield(synthesis_future), self._config.synthesis_timeout_seconds)
        except TimeoutError:
            return self._error(request, 504, "synthesis_timeout")
        except Exception:
            self._logger.warning("Kokoro synthesis failed.")
            return self._error(request, 502, "synthesis_failed")

        return web.Response(
            body=wav,
            content_type="audio/wav",
            headers={"X-Kokoro-Correlation-Id": _correlation_identifier(request)},
        )

    def _release_synthesis_lock(self, _: object) -> None:
        if self._synthesis_lock.locked():
            self._synthesis_lock.release()

    def _validate_authorization(self, request: web.Request) -> web.Response | None:
        supplied = request.headers.get("Authorization")
        try:
            expected_secret = self._secret_provider.get_secret()
        except Exception:
            return self._error(request, 503, "service_unavailable")
        if len(expected_secret) != 32:
            self._logger.error("The shared-secret provider returned an invalid secret length.")
            return self._error(request, 503, "service_unavailable")

        expected = base64.urlsafe_b64encode(expected_secret).rstrip(b"=").decode("ascii")
        if supplied is None or not supplied.startswith("Bearer "):
            return self._error(request, 401, "unauthorized")

        candidate = supplied[7:]
        if not _CANONICAL_BEARER.fullmatch(candidate):
            return self._error(request, 401, "unauthorized")
        if not hmac.compare_digest(candidate.encode("ascii"), expected.encode("ascii")):
            return self._error(request, 401, "unauthorized")
        return None

    def _json(self, request: web.Request, body: dict[str, object]) -> web.Response:
        return web.json_response(body, headers={"X-Kokoro-Correlation-Id": _correlation_identifier(request)})

    def _error(self, request: web.Request, status: int, code: str, retry_after: str | None = None) -> web.Response:
        headers = {"X-Kokoro-Correlation-Id": _correlation_identifier(request)}
        if retry_after is not None:
            headers["Retry-After"] = retry_after
        return web.json_response({"code": code, "correlationId": headers["X-Kokoro-Correlation-Id"]}, status=status, headers=headers)


def _consume_background_exception(future: asyncio.Future[object]) -> None:
    try:
        future.result()
    except Exception:
        pass


def _correlation_identifier(_: web.Request) -> str:
    return str(uuid.uuid4())


def _strict_json_loads(value: str) -> object:
    import json

    return json.loads(value)


def _validate_speech_request(
    payload: object,
    profiles: dict[str, VoiceProfile],
) -> tuple[str | None, VoiceProfile | None, str | None, float | None, int | None]:
    if not isinstance(payload, dict):
        return "invalid_request", None, None, None, None
    if set(payload) != {"text", "voice", "language", "speed", "volume"}:
        return "invalid_request", None, None, None, None

    text = payload.get("text")
    voice = payload.get("voice")
    language = payload.get("language")
    speed = payload.get("speed")
    volume = payload.get("volume")
    if not isinstance(text, str) or not text or len(text) > MAX_TEXT_CHARACTERS:
        return "invalid_text", None, None, None, None
    if not isinstance(voice, str) or voice not in profiles:
        return "invalid_voice", None, None, None, None
    profile = profiles[voice]
    if language != profile.language:
        return "invalid_language", None, None, None, None
    if isinstance(speed, bool) or not isinstance(speed, (int, float)) or not MIN_SPEED <= float(speed) <= MAX_SPEED:
        return "invalid_speed", None, None, None, None
    if isinstance(volume, bool) or not isinstance(volume, int) or not 0 <= volume <= 100:
        return "invalid_volume", None, None, None, None
    return None, profile, text, float(speed), volume


def _get_espeak_version() -> str:
    executable = _find_espeak_executable()
    if executable is None:
        return ""

    result = subprocess.run(
        [executable, "--version"],
        capture_output=True,
        check=False,
        text=True,
        timeout=5,
    )
    if result.returncode != 0:
        return ""
    return result.stdout.strip()


def _find_espeak_executable() -> str | None:
    from_path = shutil.which("espeak-ng")
    if from_path is not None:
        return from_path

    if os.name != "nt":
        return None

    program_files_directories = (
        os.environ.get("ProgramW6432"),
        os.environ.get("ProgramFiles"),
        os.environ.get("ProgramFiles(x86)"),
    )
    for program_files_directory in program_files_directories:
        if not program_files_directory:
            continue

        candidate = os.path.join(program_files_directory, "eSpeak NG", "espeak-ng.exe")
        if os.path.isfile(candidate):
            return candidate

    return None


def _is_supported_espeak_version(version: str) -> bool:
    match = re.search(r"eSpeak NG[^0-9]*(\d+)\.(\d+)", version, flags=re.IGNORECASE)
    if match is None:
        return False

    major, minor = (int(component) for component in match.groups())
    return (major, minor) >= (1, 50)
