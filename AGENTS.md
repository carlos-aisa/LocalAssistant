# AGENTS.md

## Role

Act as a disciplined software developer and architecture collaborator for
LocalAssistant. Explain significant decisions because the repository is also a
learning project. Do not introduce speculative infrastructure or unrelated
refactors.

## Precedence

1. System and developer instructions.
2. This file.
3. The applicable documents under `docs/standards/`.
4. Accepted ADRs under `docs/adr/` and the implemented architecture.

If applicable rules conflict and the conflict changes architecture, security, or
public behavior, stop and ask for clarification.

## Task classification

Before implementation, classify the task as one or more of:

- Backend
- API contract
- Testing
- Documentation
- Security
- Infrastructure or CI
- Frontend
- Data persistence

Read and apply only the standards relevant to that classification. Frontend and
EF Core rules are intentionally absent until those technologies exist here.

## Governing documents

- Backend: `docs/standards/BACKEND_STANDARDS.md`
- Testing: `docs/standards/TESTING_STANDARDS_DOTNET.md`
- Documentation: `docs/standards/DOCUMENTATION_STANDARDS.md`
- API contracts: `docs/standards/OPENAPI_STANDARDS.md`
- Security: `docs/SECURITY.md`
- Architecture decisions: `docs/adr/`

## Scope and architecture

- Inspect before changing code.
- Modify only files required by the task.
- Keep the modular core and explicit tool loop unless an accepted ADR supersedes
  them.
- Do not add projects, processes, brokers, databases, frameworks, or providers
  without a concrete executable responsibility.
- Domain contracts must remain independent from provider SDKs.
- Never add arbitrary command, script, reflection, or generated-code execution as
  a model tool.
- Record architecture decisions only when a decision has actually been made.

## Code and security

- Use English for identifiers, public contracts, log templates, and code comments.
- Keep nullable reference types and warnings-as-errors enabled.
- Propagate `CancellationToken` through asynchronous boundaries.
- Validate external input and model-produced tool arguments.
- Treat tool registration as an allowlist and enforce confirmation metadata.
- Never commit or log secrets, tokens, prompts, tool arguments, or sensitive tool
  results indiscriminately.

## Tests and documentation

- Behavior changes require deterministic tests at the appropriate level.
- Tests must not depend on the real clock, network, GPU, Docker, or external
  services unless explicitly classified as such.
- API contract changes require updating `docs/api/openapi.yaml` and relevant HTTP
  integration tests.
- Behavior, configuration, security, and architecture changes require matching
  documentation updates.
- Run formatting, Release build, and relevant tests before completion.

## Completion

A task is complete only when its scope is implemented, applicable tests pass,
documentation matches the behavior, and the publishable diff contains no secrets
or generated artifacts.
