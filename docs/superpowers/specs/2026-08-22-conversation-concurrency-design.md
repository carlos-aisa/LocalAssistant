# Conversation concurrency design

## Goal

Complete the phase 4 concurrency requirement by validating the existing
per-conversation in-process serialization and documenting its boundary.

## Scope

`ConversationOrchestrator` already obtains an
`IConversationExecutionLock` before accessing conversation metadata or history.
This increment does not change that production behavior or the HTTP contract.

Two deterministic orchestrator tests will be added:

1. Two turns for the same conversation are serialized. The second provider call
   cannot start until the first turn releases its provider response, and the
   second call receives the history written by the first turn.
2. A waiting second turn can be cancelled. It does not call its provider and it
   does not append a user message to the conversation.

The tests use task-completion synchronization instead of elapsed-time assertions
so they are deterministic and do not depend on the real clock.

## Non-goals

- No durable, cross-process, or distributed lock.
- No queue, retry, or new persistence schema.
- No change to authorization, ownership, provider selection, or API responses.

## Documentation

The roadmap will mark phase 4 turn concurrency as complete. The README will state
that serialization is scoped to a single application process, so deployment with
multiple processes requires a future coordination mechanism.

## Acceptance criteria

- Release build succeeds without warnings.
- The full deterministic test suite passes.
- The two concurrency behaviors above are covered by tests.
- Documentation matches the in-process boundary.
