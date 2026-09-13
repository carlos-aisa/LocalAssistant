# Kokoro local TTS validation runbook

Status: **executed and passed** (2026-09-12, Windows 11, PowerShell 5.1). This
document is the reproducible runbook and the record of the real execution; it does
not replace the automated tests. All steps in section 5 passed, including
segmentation, `/stop`, `/repeat`, mute, the SAPI switch, the service being stopped
and restarted, and `/exit`/Ctrl+C/EOF. No `.wav` file, secret, or process was left
behind.

Two findings surfaced during this execution, neither a Kokoro defect:

- Re-running `--kokoro-provision-secret` after it already succeeded once
  correctly fails (`Provision()` refuses to overwrite an existing secret file by
  design) — this is not an error condition; skip straight to starting the
  service. Use `--kokoro-rotate-secret` (service stopped) to replace an existing
  secret instead.
- On Windows PowerShell 5.1, `Invoke-WebRequest` throws on a non-2xx status, and
  separately its underlying `HttpWebRequest` can leave an error response's body
  stream already drained by the time it is read back — an unauthenticated
  `/health` call can misreport a real JSON body as empty even inside a correct
  `try`/`catch`. Step 3 below uses `curl.exe` instead, which shows the actual
  wire response (confirmed live: `401`, `X-Kokoro-Correlation-Id` header, and
  `{"code": "unauthorized", "correlationId": "..."}`).
- Step 11's `/exit` can report `persistence_unavailable` and correctly refuse to
  close if the running `LocalAssistant.Api` instance was started without
  `LocalAssistant__ConversationPersistence__Enabled=true` — this is pre-existing
  completion-on-exit behavior unrelated to Kokoro (an uncertain result must not
  be treated as a successful close). Use Ctrl+C or EOF to close in that case, or
  restart the API with persistence enabled to exercise a clean `/exit`.

Run on Windows Terminal + PowerShell only. **Do not record or publish** the shared
secret, the contents of `shared-secret.v1.dpapi`, generated WAV files, local file
paths, or spoken conversation content. Record only pass/fail per step, versions,
the CPU confirmation, and sanitized correlation IDs (the `X-Kokoro-Correlation-Id`
value alone, never a full response body).

> **Hash-locking status.** `requirements-prod.lock` and `requirements-test.lock`
> are now fully hash-locked (regenerated with `pip-compile --generate-hashes
> --allow-unsafe --index-url https://pypi.org/simple`) and were verified on
> 2026-09-12 by installing each with `--require-hashes` into a **clean** venv —
> from `https://pypi.org/simple` only, no extra index — and confirming the
> result matches the evaluated environment exactly: `torch==2.14.0+cpu`,
> `torch.cuda.is_available() == False`, `kokoro==0.9.4`, `numpy==2.4.6`,
> `aiohttp==3.14.3`. `services\kokoro-tts\tests` (44 tests) also passed
> unmodified against that clean, hash-verified install. Step 1 below uses
> `--require-hashes`. This closes Lote 6, item 7 of the implementation plan.

## 0. What you need before starting

- Windows, PowerShell, Windows Terminal.
- The evaluated Python 3.11.9 environment at `D:\IA\Kokoro\.venv` (from the prior
  experimental evaluation), with `kokoro==0.9.4`, `torch==2.14.0+cpu`,
  `numpy==2.4.6`, `misaki==0.9.4`, `espeakng-loader==0.2.4`,
  `phonemizer-fork==3.3.2` already installed. This is the environment the design
  says to extend with the new service dependencies, not replace.
- eSpeak NG installed (the default Windows install path is
  `C:\Program Files\eSpeak NG\espeak-ng.exe`).
- Ollama running `qwen3.5:9b` if you also want to repeat the coexistence
  observations; not required for the Kokoro-only checks.
- The repository at `D:\Programacion\LocalAssistant`, on the commit that contains
  the `services\kokoro-tts` service and the `.NET` Kokoro client.
- `services\kokoro-tts\config.toml` present (copy `config.example.toml` if it is
  missing) with a real `weights_directory` pointing at your prepared
  `hexgrad/Kokoro-82M` Hugging Face cache and a `port` you are free to use
  (default `57321`).

Open two PowerShell windows for this runbook: **Window A** (the Kokoro service)
and **Window B** (everything else — provisioning, the terminal client, tests).

## 1. Install dependencies into the evaluated environment

In **Window B**:

```powershell
cd D:\Programacion\LocalAssistant\services\kokoro-tts
D:\IA\Kokoro\.venv\Scripts\python.exe -m pip install --require-hashes --index-url https://pypi.org/simple -r requirements-prod.lock
D:\IA\Kokoro\.venv\Scripts\python.exe -m pip install --require-hashes --index-url https://pypi.org/simple -r requirements-test.lock
```

Do not add an extra index or `--find-links`; only `https://pypi.org/simple` is
the reviewed source. Every package installs from a verified hash. This installs
`aiohttp==3.14.3` (new; absent from the original evaluation) plus
`pytest==8.3.5` and `pytest-asyncio==0.25.3`, on top of the already-evaluated
`kokoro`/`torch`/`numpy`/`misaki`/`espeakng-loader`/`phonemizer-fork` — already
present in this venv and reported as "Requirement already satisfied" at the
exact pinned version, since they match what pip-compile resolved.

**Pass:** both commands finish with exit code 0 and report the exact pinned
versions above (`pip list` if you want to double-check). **Record:** the
resolved version of `torch` (must still read `2.14.0+cpu`, not a CUDA build) and
of `aiohttp`.

Then install eSpeak NG if it is not already present (operator-provided, not part
of this repository), and confirm it runs:

```powershell
& "C:\Program Files\eSpeak NG\espeak-ng.exe" --version
```

**Pass:** prints a version string. The service looks for `espeak-ng` on `PATH`
first, then falls back to this default install location automatically — you do
not need to add it to `PATH` yourself.

## 2. Verify the environment before starting the service

Still in Window B, with the same interpreter:

```powershell
D:\IA\Kokoro\.venv\Scripts\python.exe -c "import torch; print('cuda available:', torch.cuda.is_available())"
```

**Pass:** prints `cuda available: False`. If it prints `True`, stop — you have a
CUDA-enabled `torch` installed and must reinstall the CPU-only wheel before
continuing; Kokoro must never load on the GPU in this increment.

Confirm the prepared model cache exists at the `weights_directory` configured in
`services\kokoro-tts\config.toml` (it should already contain the
`hexgrad/Kokoro-82M` snapshot from the earlier evaluation — the service does not
download it):

```powershell
Get-ChildItem "C:\Users\<you>\.cache\huggingface" -Recurse -Filter "*Kokoro*" | Select-Object -First 5
```

**Pass:** at least one match. If empty, do not proceed to Step 3 — the service
will report `degraded` and never reach `ready`.

## 3. Offline smoke test with outbound network blocked

This proves Kokoro starts and serves a real synthesis **without any network
access** — no weight download, no telemetry, no accidental call out.

In **Window A** (PowerShell as the same user that will run the service; elevate
only if your Windows Firewall policy requires it for rule creation):

```powershell
$pythonExe = (Resolve-Path "D:\IA\Kokoro\.venv\Scripts\python.exe").Path
$ruleName = "kokoro-offline-smoke-block"

New-NetFirewallRule -DisplayName $ruleName -Direction Outbound -Program $pythonExe -Action Block | Out-Null
try {
    cd D:\Programacion\LocalAssistant\services\kokoro-tts
    $env:PYTHONPATH = "src"
    & $pythonExe -m localassistant_kokoro_tts --config .\config.toml
}
finally {
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
}
```

**Do not skip the `finally` block** — if you interrupt the run with Ctrl+C before
reaching this script's own `finally`, remove the rule manually right after:
`Remove-NetFirewallRule -DisplayName kokoro-offline-smoke-block`.

Leave this running (it blocks in the foreground serving HTTP). Watch its console
output for a startup message and no unhandled exception; it must not print any
network error, since it should never attempt one.

In **Window B**, provision the shared secret (this is the only supported way to
create it — it never appears on screen and must never be pasted anywhere):

```powershell
cd D:\Programacion\LocalAssistant
dotnet run --project src\LocalAssistant.TerminalClient -- --kokoro-provision-secret
```

**Pass:** prints `Kokoro shared secret provisioned.` and exits `0`. If it prints
`Kokoro shared-secret provisioning failed.`, stop and check that
`%LOCALAPPDATA%\LocalAssistant\Kokoro\` is writable and that no other Kokoro
service or rotation already holds the shared process lock.

Now call health **without** any credential, to confirm the service actually
requires authentication instead of assuming it does. There is no supported way
to read the raw secret from PowerShell (the .NET client reads the DPAPI file
directly and never prints it), so this call is deliberately unauthenticated —
do not try to attach a header here.

Use `curl.exe` (built into Windows 10/11), not `Invoke-WebRequest`: on Windows
PowerShell 5.1, `Invoke-WebRequest` throws on a non-2xx status instead of
returning it, and — separately — its underlying `HttpWebRequest` can leave the
error response's body stream already drained by the time you try to read it,
so even a correct `try`/`catch` around it can misreport a real JSON body as
empty. `curl.exe` shows the actual wire response with no such quirk:

```powershell
curl.exe -s -i http://127.0.0.1:57321/health
```

**Pass (unauthenticated call):** `HTTP/1.1 401 Unauthorized`, an
`X-Kokoro-Correlation-Id` header, and a small JSON body containing only `code`
and `correlationId` (for example `{"code": "unauthorized", "correlationId":
"..."}`) — never a distinguishable "wrong secret" vs "malformed" message.
**Record** the `correlationId` value only, never anything else the body might
ever contain.

The authenticated health/voices/synthesis path is exercised through the
terminal client in Step 5, which already knows how to read the shared secret;
that is also a more representative check than crafting the bearer header by
hand.

**Pass for this whole step:** the service reached a ready-or-degraded state
with the firewall rule still active (confirm in Window A's log — no outbound
attempt, no stack trace about DNS/connection), and the rule was removed
afterward. **Record:** whether `health` reported `ready` or `degraded` and, if
`degraded`, its reason.

Stop the service in Window A (Ctrl+C) once this step is recorded; Step 4
restarts it cleanly.

## 4. Concurrency: busy-rejection and health-during-synthesis

Restart the service (same command as Step 3, without the firewall rule this
time — normal loopback network is fine from here on):

```powershell
cd D:\Programacion\LocalAssistant\services\kokoro-tts
$env:PYTHONPATH = "src"
D:\IA\Kokoro\.venv\Scripts\python.exe -m localassistant_kokoro_tts --config .\config.toml
```

Using the terminal client (Window B) with `KokoroEndpoint` configured (see
Step 5 for how to set it), trigger one longer synthesis and, while it is still
running, trigger a second one from a **separate** terminal client run or a
manual authenticated request:

**Pass:**

- While the first synthesis is in flight, `GET /health` still responds
  immediately (not delayed until synthesis finishes) and reports `busy`.
- The second, overlapping synthesis attempt is rejected with `503` and header
  `Retry-After: 1` — it is rejected before entering the executor, not queued.
- No response ever waits for the busy one to finish.

**Record:** pass/fail for each of the three assertions above.

## 5. Full functional walkthrough with the real terminal client

Point the terminal client at the running service. Easiest for a one-off manual
run: an environment variable (equivalent to editing `appsettings.json`):

```powershell
$env:LocalAssistant__TerminalClient__KokoroEndpoint = "http://127.0.0.1:57321/"
cd D:\Programacion\LocalAssistant
dotnet run --project src\LocalAssistant.TerminalClient -- --provider=ollama
```

With the client running, walk through each of the following and record
pass/fail plus any safe error code shown (`speech_kokoro_*`):

1. `/speech-provider kokoro` — switches the requested provider. `/speech-info`
   shows requested = `kokoro`; effective provider depends on Kokoro's health.
2. `/voice kokoro jarvis-es` — selects a profile. `/voice` (no provider) still
   operates on the requested provider (Kokoro).
3. Send a short message and confirm audio plays for the first eligible response
   using Kokoro (not SAPI).
4. Send a message long enough to require segmentation (a few hundred characters)
   and confirm: the first segment starts playing before the whole response has
   finished synthesizing, transitions through `BufferingVoice` are not shown as
   spoken text in the TUI, and the full response is heard with no dropped or
   duplicated segment.
5. `/stop` during playback of a segmented response — confirm it stops promptly
   and does not resume with a leftover buffered segment.
6. `/repeat` after a completed segmented response — confirm it resynthesizes
   from the retained text, not from a cached WAV.
7. `/mute` then send a message, then `/unmute` — confirm no audio plays while
   muted and that mute is global (affects Kokoro the same as it would SAPI).
8. `/speech-provider sapi` then send a message — confirm it plays with SAPI, not
   Kokoro, and `/speech-info` reflects the switch.
9. `/speech-provider kokoro` again, then **stop the Python service** (Ctrl+C in
   its window) and send a message — confirm the client reports a safe
   `speech_kokoro_*` code (unavailable, not a crash), the textual response is
   still shown in full, and — only if SAPI is configured as fallback — SAPI
   speaks it instead; otherwise it degrades to text only, without ever
   inventing a retry of the uncertain Kokoro request.
10. Restart the Python service (Step 4's command) and confirm the terminal
    client recovers on the next message without restarting the client itself.
11. `/exit`, then in a fresh run `Ctrl+C`, then in a fresh run close via EOF —
    confirm the terminal is restored correctly each time and no
    `LocalAssistant.TerminalClient` or Python service process is left running
    that you did not intend to keep.

**Pass for this step:** all eleven checks above pass. **Record:** which (if
any) failed, the exact safe error code shown, and whether the terminal was left
in a broken state.

Finally, confirm no WAV file was written anywhere outside `services\kokoro-tts`
(none should exist at all — the design keeps audio in memory only):

```powershell
Get-ChildItem -Recurse -Filter "*.wav" D:\Programacion\LocalAssistant | Select-Object FullName
```

**Pass:** empty result (the `services\kokoro-tts\**\*.wav` gitignore entry
existing is not itself proof — confirm no file is actually present).

## 6. DPAPI interoperability tests (.NET ↔ Python)

From the repository root (Window B), with no Kokoro service running (this test
must not touch the operational shared-secret path or process lock):

```powershell
cd D:\Programacion\LocalAssistant
.\scripts\Invoke-KokoroDpapiInteropTests.ps1 -PythonPath "D:\IA\Kokoro\.venv\Scripts\python.exe"
```

**Pass:** the script exits `0`. It creates and removes its own temporary
directory for every case, and covers both directions: .NET protects a secret
that Python unprotects, and Python protects one that .NET unprotects, plus
atomic replacement and corruption rejection — all against temporary paths, never
`%LOCALAPPDATA%\LocalAssistant\Kokoro\shared-secret.v1.dpapi`.

## 7. Closing checks

Before recording this runbook as executed:

```powershell
cd D:\Programacion\LocalAssistant
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests\LocalAssistant.Tests\LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
dotnet test tests\LocalAssistant.Tests\LocalAssistant.Tests.csproj -c Release --no-build
git diff --check
D:\IA\Kokoro\.venv\Scripts\python.exe -m pytest services\kokoro-tts\tests
```

Confirm no residual process is left:

```powershell
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @("LocalAssistant.TerminalClient", "LocalAssistant.Api", "testhost", "python")
}
```

**Record for the final evidence:** Windows version, versions from Step 1,
pass/fail for every numbered step above, the sanitized correlation IDs you
collected, and — separately and prominently — that the hash-lock gap flagged at
the top of this document is still open and not resolved by this runbook.
