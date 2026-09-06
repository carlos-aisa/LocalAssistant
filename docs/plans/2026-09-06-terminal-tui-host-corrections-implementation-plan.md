# Implementation plan: terminal TUI host corrections

## Objective

Correct the TUI blockers in PR 67: cancel pending input, visibly deliver errors
and confirmations, adapt the viewport to terminal size, isolate I/O behind a fake
driver, and normalize untrusted content.

## Step 1: internal driver and cancellable lifecycle

**Files:** `TerminalClientTui.cs`; new `TerminalDriver.cs`; TUI tests.

Extract an internal `ITerminalDriver` from `TerminalClientTuiHost` for size,
keys, rendering, and restoration. The production adapter is the only code with
access to `Console`; the fake records frames and can schedule keys, EOF, resize,
and failures without a real terminal. Register each pending input request with the
host token: cancellation completes it as EOF and unblocks the worker before waiting
for `applicationTask`. Preserve `finally` cleanup and restore once.

## Step 2: priority frames, viewport, and safe scrolling

**Files:** `TerminalClientTui.cs`; TUI tests.

Have the sink deliver priority confirmation/error snapshots and render each before
the subsequent coalesced snapshot. Detect width and height changes on every loop.
Calculate a viewport that reserves state, error/confirmation, and input, shows the
most recent transcript by default, and clips long lines to a safe width. Implement
only transcript scrolling keys; they do not send commands or resolve confirmations.
Show the expiry in UTC in the confirmation region.

## Step 3: presentation text normalization

**Files:** new `TerminalTextSanitizer.cs`; `TerminalConsole.cs`;
`TerminalClientTui.cs`; unit tests.

Normalize public messages, operational messages, and errors before any rendering.
Preserve new lines and render tabs, ESC, ANSI/OSC, and other controls as visible
escapes. Apply it to the textual presenter and the TUI without changing API payloads
or secrets.

## Step 4: tests and honest documentation

**Files:** TUI tests; `README.md`; `docs/SECURITY.md`; `docs/ROADMAP.md`;
evaluation and specification documents.

Test the real host with the fake driver: Ctrl+C/cancellation while waiting for input,
EOF, restoration, resize without a snapshot, clipping, scrolling, priority frames,
expiry, paste input, and normalization. Keep parser-flow tests. Keep roadmap point 5
unchecked until the automated checks and manual Windows Terminal + PowerShell check
are complete.

## Verification

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Before updating the PR, review the diff under `AGENTS.md`, repeat affected tests,
and confirm that no `testhost` or API process remains.
