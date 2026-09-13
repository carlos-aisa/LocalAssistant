# ADR 0037: Select Kokoro CPU as the optional local neural TTS provider

## Status

Accepted. The offline smoke test (outbound traffic blocked by a real Windows
Firewall rule) and the manual Windows runbook
(`docs/runbooks/kokoro-local-tts-validation.md`) both passed.

## Decision

Use Kokoro 0.9.4 on CPU as an optional, manually operated loopback service.
The terminal client communicates with `127.0.0.1` only, authenticates with a
separate DPAPI-protected shared secret, and retains SAPI as explicit fallback.

Kokoro runs outside the conversation API and does not add audio to its HTTP
contracts. It has one inference worker, rejects concurrent synthesis with
`503` and `Retry-After: 1`, and must not download models while running.

## Consequences

The service depends operationally on eSpeak NG and GPL components that are not
bundled with the MIT terminal-client publication. The operator provisions the
Python environment, prepared weights and service lifecycle. Chatterbox was
rejected for this hardware because its GPU demand conflicts with Ollama.

This ADR supersedes only the engine-candidate portion of ADR 0036; its textual
conversation boundary and explicit fallback decisions remain in force.
