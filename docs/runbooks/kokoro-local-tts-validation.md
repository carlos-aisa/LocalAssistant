# Kokoro local TTS validation runbook

Run on Windows Terminal + PowerShell only. Do not record or publish the shared
secret, generated WAV files, local paths, or spoken conversation content.

1. Install the pinned production and test dependencies from the documented
   Windows x64/Python 3.11 indexes and install operator-provided eSpeak NG.
2. Verify the installed eSpeak NG executable, `torch.cuda.is_available() == False`,
   and that the prepared `hexgrad/Kokoro-82M` cache is present. The service first
   uses `espeak-ng` from `PATH`, then detects the standard Windows eSpeak NG
   installation under Program Files, so adding its directory to `PATH` is not
   required. For the default installation, run
   `& "C:\Program Files\eSpeak NG\espeak-ng.exe" --version`.
3. Temporarily block outbound traffic for the selected Python interpreter using
   Windows Firewall, start the service with `config.toml`, then call authenticated
   `/health` and one `em_alex` synthesis. Always remove the firewall rule in a
   `finally` block.
4. Check concurrent synthesis returns `503`, `Retry-After: 1`, and health still
   responds while the first synthesis runs.
5. Test a short and a segmented long response, `/stop`, mute, SAPI fallback,
   service stopped, and terminal restoration after `/exit` and Ctrl+C.
6. Run `scripts/Invoke-KokoroDpapiInteropTests.ps1 -PythonPath
   D:\IA\Kokoro\.venv\Scripts\python.exe`. It creates and removes its own
   temporary directory, and must test .NET-to-Python and Python-to-.NET DPAPI
   envelopes without touching the operational shared-secret path.

Record only pass/fail, versions, CPU confirmation, and sanitized correlation IDs.
