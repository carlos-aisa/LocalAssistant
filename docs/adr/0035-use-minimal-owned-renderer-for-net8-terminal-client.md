# ADR 0035: Use a minimal owned renderer for the .NET 8 terminal client

## Context

The terminal client needs an interactive, accessible presentation without moving command
or state authority from `TerminalClientApplication`. Spectre.Console Live Display does
not support this combination with interactive controls. Current Terminal.Gui v2 packages
target .NET 10, while the client intentionally remains on .NET 8.

## Decision

Use a minimal owned terminal renderer for this increment. It is selected only for a
suitable interactive terminal; `--plain`, redirection and unsupported environments use
the existing textual presenter. The renderer owns terminal I/O, while the application
runs separately and communicates through structured input/output and immutable state
snapshots.

## Consequences

No dependency or framework upgrade is introduced. The renderer intentionally provides
only the required transcript, input, status, confirmation and safe error regions. A
future framework upgrade may trigger a new evidence-based evaluation of Terminal.Gui v2;
it does not automatically replace this renderer.
