from __future__ import annotations

import ctypes
from ctypes import wintypes
from collections.abc import Callable
import os
from pathlib import Path
import struct

from .service import SharedSecretProvider


_MAGIC = b"LASKOK01"
_VERSION = 1
_CURRENT_USER_SCOPE = 1
_CRYPTPROTECT_UI_FORBIDDEN = 0x1
_LOCAL_FREE = ctypes.windll.kernel32.LocalFree if os.name == "nt" else None


class DpapiSharedSecretProvider(SharedSecretProvider):
    """Windows-only strict reader for the shared LocalAssistant Kokoro secret."""

    def __init__(self, path: Path | None = None) -> None:
        self._path = path or default_secret_path()

    def get_secret(self) -> bytes:
        return decode_secret_envelope(self._path.read_bytes(), _unprotect_current_user)


def default_secret_path() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise RuntimeError("LOCALAPPDATA is unavailable.")
    return Path(local_app_data) / "LocalAssistant" / "Kokoro" / "shared-secret.v1.dpapi"


def decode_secret_envelope(data: bytes, unprotect: Callable[[bytes], bytes]) -> bytes:
    if len(data) < 15 or data[:8] != _MAGIC:
        raise ValueError("The Kokoro shared-secret envelope is invalid.")

    version, scope, protected_length = struct.unpack_from("<HBI", data, 8)
    if version != _VERSION or scope != _CURRENT_USER_SCOPE or protected_length == 0:
        raise ValueError("The Kokoro shared-secret envelope is invalid.")
    if protected_length > 1_048_576 or len(data) != 15 + protected_length:
        raise ValueError("The Kokoro shared-secret envelope is invalid.")

    secret = unprotect(data[15:])
    if len(secret) != 32:
        raise ValueError("The Kokoro shared-secret envelope is invalid.")
    return secret


def _unprotect_current_user(protected: bytes) -> bytes:
    if os.name != "nt":
        raise RuntimeError("The shared DPAPI secret is available only on Windows.")

    class DataBlob(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_byte))]

    crypt_unprotect_data = ctypes.windll.crypt32.CryptUnprotectData
    crypt_unprotect_data.argtypes = [
        ctypes.POINTER(DataBlob),
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(DataBlob),
    ]
    crypt_unprotect_data.restype = wintypes.BOOL

    source = (ctypes.c_byte * len(protected)).from_buffer_copy(protected)
    input_blob = DataBlob(len(protected), source)
    output_blob = DataBlob()
    if not crypt_unprotect_data(
        ctypes.byref(input_blob),
        None,
        None,
        None,
        None,
        _CRYPTPROTECT_UI_FORBIDDEN,
        ctypes.byref(output_blob),
    ):
        raise OSError(ctypes.get_last_error(), "CryptUnprotectData failed.")

    try:
        return ctypes.string_at(output_blob.pbData, output_blob.cbData)
    finally:
        ctypes.memset(output_blob.pbData, 0, output_blob.cbData)
        assert _LOCAL_FREE is not None
        _LOCAL_FREE(output_blob.pbData)


class WindowsKokoroProcessLock:
    """Exclusive user-session lock held for the lifetime of the service process."""

    _NAME = "Local\\LocalAssistant.Kokoro.SharedSecret.v1"
    _WAIT_OBJECT_0 = 0
    _WAIT_ABANDONED = 0x80

    def __init__(self) -> None:
        self._handle: int | None = None

    def acquire(self) -> bool:
        if os.name != "nt":
            raise RuntimeError("The Kokoro service is available only on Windows.")
        handle = ctypes.windll.kernel32.CreateMutexW(None, False, self._NAME)
        if not handle:
            raise OSError(ctypes.get_last_error(), "CreateMutexW failed.")
        result = ctypes.windll.kernel32.WaitForSingleObject(handle, 0)
        if result not in {self._WAIT_OBJECT_0, self._WAIT_ABANDONED}:
            ctypes.windll.kernel32.CloseHandle(handle)
            return False
        self._handle = handle
        return True

    def close(self) -> None:
        if self._handle is None:
            return
        ctypes.windll.kernel32.ReleaseMutex(self._handle)
        ctypes.windll.kernel32.CloseHandle(self._handle)
        self._handle = None
