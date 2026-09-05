# Accessible Terminal TUI Evaluation and Minimal Implementation Design

## Context

Phase 5 already provides a .NET terminal client with one authoritative owner of
operational state: `TerminalClientApplication`. It publishes immutable
`TerminalClientStateSnapshot` values to an injected sink. The textual interface
remains the dependable fallback and command processor.

This increment evaluates three terminal presentation approaches and, in the same
increment, implements a minimal accessible TUI using the selected approach. The
evaluation is not a separate phase and cannot end only with documentation.

## Goals

- Select one concrete terminal UI approach from small, disposable prototypes.
- Render operational state from `TerminalClientStateSnapshot` without moving state
  authority, credential handling, or command parsing into the TUI.
- Preserve explicit, line-based commands, including `approve`, `reject`, `/exit`,
  `/new`, and `/conversations`.
- Use a deterministic startup decision: TUI for a suitable interactive terminal;
  existing textual mode for redirected or unsuitable input/output, and when
  `--plain` is specified.
- Deliver a minimal TUI with public conversation history, line input, provider,
  conversation identity, current activity or safe error, and a prominent pending
  confirmation area.

## Non-goals

- Audio, TTS, visual audio waves, or decorative animation.
- Global keyboard shortcuts, including one-key approval.
- A second command parser, a second application state machine, or direct access to
  private server internals.
- Automatic recovery from a failed TUI initialization after application startup.
- Screen-reader claims that the terminal host itself cannot provide.

## Candidates and disposable prototypes

The evaluation compares no more than these three approaches:

1. **Spectre.Console.** Prototype a snapshot panel plus line input and asynchronous
   state refreshes. It must demonstrate whether live rendering can coexist safely
   with interactive input. Spectre.Console's live display documentation explicitly
   says it is not thread-safe and does not support use with other interactive
   components, making this a likely rejection for this client shape.
2. **Terminal.Gui.** Prototype a conventional event-loop UI with a status region,
   confirmation panel, history area, and one-line input. It must demonstrate resize
   handling, event-loop-safe snapshot delivery, `Ctrl+C` shutdown, and deterministic
   rendering through a fake driver or equivalent test seam.
3. **Minimal custom renderer and textual baseline.** Prototype only the smallest
   screen composition necessary to consume a snapshot, redraw on resize, and avoid
   ANSI when output is redirected. It is the dependency-free comparison and the
   fallback reference, not a commitment to hand-build a general TUI toolkit.

Each prototype is disposable. It may live outside the production client or in a
temporary, unreferenced evaluation fixture and must not become an alternate client
implementation.

## Evaluation criteria and decision rule

Every candidate is assessed against the following evidence:

| Criterion | Required evidence |
| --- | --- |
| Snapshot rendering | A synthetic `TerminalClientStateSnapshot` updates the visible provider, conversation, activity/error, and confirmation state. |
| Asynchronous changes | A state published while an operation is pending updates the display without data races or concurrent terminal writes. |
| Input and shutdown | The input accepts a complete line; `Ctrl+C`, EOF, and `/exit` close cleanly and restore the terminal. |
| Resize | Width and height changes retain a usable layout and do not leave stale screen content. |
| Confirmations | The confirmation area is visually distinct, but only the typed commands `approve` and `reject` resolve it. |
| Testability | Rendering and input dispatch can be tested without a physical terminal. |
| Accessibility | The prototype has a no-color mode, does not depend on animation, preserves readable textual status, and is compatible with the normal accessibility behavior of Windows Terminal and PowerShell. |
| Redirection | Redirected output emits no ANSI control sequences and never starts the TUI. |
| Operations | Maintenance state, license, package size/dependency cost, and Windows Terminal/PowerShell behavior are recorded from authoritative sources and local evidence. |

The selected candidate must pass every functional and safety criterion. If more than
one passes, select the one with the smallest production surface that still supplies
resize, input, and deterministic testability. If no library candidate passes,
implement the minimal custom renderer only if it passes the same criteria; otherwise
the increment stops with a documented technical blocker rather than shipping a TUI
that cannot be used safely.

The expected preference is Terminal.Gui, subject to prototype evidence. Spectre.Console
is retained for evaluation because it has an active MIT-licensed package and terminal
capability detection, but its documented live-display interaction restriction is a
direct risk. Terminal.Gui is MIT-licensed and offers a terminal UI event loop, but its
stable/next-major maintenance position must be verified at the time of the decision.

## Startup and fallback policy

The composition root chooses the presenter before `TerminalClientApplication` starts:

1. `--plain` selects the existing textual console unconditionally.
2. Redirected standard input or output selects the existing textual console
   unconditionally.
3. An interactive terminal that meets the selected TUI's compatibility probe selects
   the TUI presenter.
4. All other environments select the existing textual console and use no ANSI control
   sequences.

This is capability selection, not error recovery. The application must not start a
TUI, fail, and then try to switch renderers after stateful interaction has begun.

## Architecture

`TerminalClientApplication` remains the only command and state authority. The TUI is
an adapter composed from three bounded concerns:

- **Terminal capability probe:** makes the startup-only textual-versus-TUI decision
  from `--plain`, redirection, and the selected library's supported environment.
- **TUI state sink and view model:** receives complete immutable snapshots, schedules
  their rendering on the TUI event loop, and maps only safe snapshot fields to visible
  text. It never receives credentials, tokens, challenges, complete messages, prompts,
  or tool arguments.
- **Line-input console adapter:** obtains a line from the TUI input control and passes
  it through the existing `ITerminalConsole`/application command path. It does not
  parse or execute commands itself.

Public history is supplied through the existing terminal-client conversation flow. The
TUI owns only the in-memory visual transcript needed for this session. It must never
display hidden system prompts, internal context, administrative challenges, tokens, or
sensitive tool arguments/results.

Future non-sensitive keyboard navigation can be added behind a separate input-routing
boundary. This increment does not register global shortcuts.

## TUI behavior

The initial layout uses simple regions that may stack vertically in a narrow terminal:

- A status line with local-server lifecycle, provider, and current conversation ID in
  safe abbreviated form.
- A scrollable public transcript/history region.
- A highlighted confirmation region when `PendingConfirmation` exists. It names the
  tool and expiration and states that the user must type `approve` or `reject`.
- A safe activity/error region. Uncertain results remain explicitly uncertain.
- A single editable input line.

The TUI does not animate spinners, synthesize voice state, or imply an operation was
cancelled remotely. `PlayingVoice` remains inactive in the state model. No-color and
reduced-motion operation renders the same information with labels and structure rather
than color or movement alone.

`Ctrl+C`, EOF, and `/exit` flow through clean application shutdown. The TUI restores
the terminal in a `finally` path even when a captured cancellation or application error
occurs.

## Tests and acceptance evidence

Tests will cover:

- The pre-start presenter selection matrix: `--plain`, redirected input, redirected
  output, unsuitable terminal, and suitable interactive terminal.
- No ANSI output in textual/redirection paths.
- Snapshot-to-view mapping for lifecycle, activity, safe errors, provider,
  conversation, and pending confirmations.
- Input forwarding to the same application command path used by textual mode.
- Explicit approval/rejection only, including a proof that non-sensitive future input
  routing cannot resolve a confirmation in this increment.
- Resize and asynchronous snapshot dispatch through a fake terminal/TUI driver.
- `Ctrl+C`, EOF, and `/exit` terminal restoration and clean shutdown.
- The chosen prototype's dependency/version/license evidence and the rejected
  alternatives' concrete failure against the criteria.

The decision record, implemented selection, source references, and exact prototype
results are added to the accompanying implementation plan and a concise ADR only if
the adopted UI dependency constitutes a durable architecture decision.
