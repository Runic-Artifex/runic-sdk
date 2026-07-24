# ADR 0010: Workflow-first agent orchestration

- Status: Accepted
- Date: 2026-07-24
- Supersedes: ADR 0003 execution topology; ADR 0009 operating policy

## Context

The first dynamic-workflow pilot proved fan-out, model routing, viewing, and resume,
but the follow-up remediation workflow accumulated an approved JSON Plan, immutable
baseline and preparation bindings, offline restoration, duplicated schemas and
contracts, and a large PowerShell launcher.

That ceremony exceeded ordinary subagent coordination and created a false blocker:
the compiler candidate could not restore a required SDK package because the
workflow disabled normal developer network and package access.

## Decision

WebUIToolkit adopts the portable
[`workflow-operating-manifest.md`](../../eng/codex-workflows/workflow-operating-manifest.md).

For bounded multi-agent work:

- workflows are preferred over manually coordinating separate Codex tasks or
  subagents;
- planning remains a user responsibility and is not an enforced workflow phase;
- task-specific, one-off TypeScript workflows are the default;
- Bun runs deterministic operations and records their authoritative results;
- agents perform discovery, implementation, diagnosis, semantic review, and
  synthesis;
- model and reasoning choices are made per context rather than fixed permanently
  to builder/reviewer/lead roles;
- trusted-local workflows inherit the invoking task's normal development access;
- successful changes return uncommitted for ordinary review and integration;
- JSON is reserved for real interchange, runtime state, and durable artifacts;
- workflows are promoted to permanent infrastructure only after repeated use
  demonstrates a stable recurring shape.

Exclusive path ownership in `eng/ownership.json`, repository quality gates, and
explicit user control over wave activation remain authoritative. Those product
constraints do not require one long-lived task or worktree per owner.

## Implementation direction

Create a small TypeScript/Bun toolkit for agent routing, deterministic checks, Git
and optional worktree helpers, telemetry, journals, resume, and cleanup. Generated
one-off scripts and run state live under ignored `.codex-workflow/` directories.
Only deliberately promoted workflows live in the committed saved-workflow
directory.

Freeze the 2026-07-22 remediation workflow as a historical experiment. Do not
incrementally extend its approval and validation architecture. Extract only useful
runtime/viewer primitives and implement the next remediation attempt as a small
one-off workflow.

The toolkit does not implement an untrusted-input mode. A future concrete untrusted
execution requirement should use real process isolation such as a disposable VM,
container, or CI worker.

## Consequences

- Workflow creation should approach the ceremony of ordinary subagent delegation.
- Automated checks run consistently after agent edits and can drive fix loops.
- Local journals support monitoring and resume without becoming committed
  evidence.
- Task-specific workflow code may be discarded after completion.
- Legacy workflow scripts and approval artifacts are removed from the active tree;
  Git history and curated reports preserve the experiment.
- Wave C remains inactive until the user explicitly starts it.
