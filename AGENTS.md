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

## Code readability and formatting

Generated code must prioritize human readability and maintainability over
compactness.

### Formatting

- Follow standard C# formatting conventions.
- Opening and closing braces must normally be placed on their own lines.
- Do not compress multiple statements onto the same line.
- Do not write single-line `if`, `for`, `foreach`, `try`, `catch`, `using`,
  or similar control-flow blocks when their body contains more than one
  statement.
- Avoid excessively long lines. Break method calls and argument lists across
  multiple lines when this improves readability.
- Nested calls should be formatted so that their structure is visually clear.
- Code should remain easy to scan, debug with breakpoints, and review in a
  normal IDE window without horizontal scrolling.

### Maintainability

- Prefer clear intermediate variables over deeply nested expressions when they
  make intent easier to understand.
- Prefer descriptive names over compact expressions.
- Methods should expose their high-level control flow clearly.
- Extract private methods when a block represents a distinct responsibility
  and extraction improves readability.
- Do not extract methods merely to reduce line count.
- Do not introduce abstractions solely for stylistic reasons.

### Before completing a change

Review all modified code as if it were being submitted for human code review.
If formatting or expression density makes the code unnecessarily difficult to
read, refactor it before considering the task complete.

### Existing code

When modifying an existing file, preserve or improve its readability.
Do not introduce compressed formatting even if compressed formatting already
exists elsewhere in the file.

If a touched section contains clearly compressed or poorly formatted code,
reformat that section when it can be done safely without changing behavior.

## Tests and documentation

- Behavior changes require deterministic tests at the appropriate level.
- Tests must not depend on the real clock, network, GPU, Docker, or external
  services unless explicitly classified as such.
- API contract changes require updating `docs/api/openapi.yaml` and relevant HTTP
  integration tests.
- Behavior, configuration, security, and architecture changes require matching
  documentation updates.
- Run formatting, Release build, and relevant tests before completion.

## Pre-PR review

Before creating a pull request for a behavior, API contract, security,
persistence, filesystem, or CI change, run the available `review` skill against
the branch diff. Treat it as a complement to tests: it must inspect scope drift,
trust boundaries, concurrency, contracts, documentation staleness, and other
structural risks that tests may not reveal.

- Apply clear mechanical corrections found by the review, then repeat the affected
  verification commands before committing and publishing.
- Escalate findings that change public behavior, authorization, security policy,
  data semantics, or architecture for explicit user approval.
- Use the frontend design checklist only when the diff changes frontend source.
- Use external-review triage only when a pull request has external review comments.
- A review is optional for documentation-only changes with no behavior or workflow
  effect; record the reason when it is skipped.

## Completion

A task is complete only when its scope is implemented, applicable tests pass,
documentation matches the behavior, and the publishable diff contains no secrets
or generated artifacts.
