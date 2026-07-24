# Codex workflows

WebUIToolkit uses workflows as the preferred coordination layer for bounded
multi-agent work. The project-neutral policy is the
[workflow operating manifest](workflow-operating-manifest.md); ADR 0010 adopts it
for this repository.

## Current operating model

- Planning is user-owned and is not an enforced workflow phase.
- Generate a small task-specific TypeScript workflow for the work at hand.
- Keep it one-off under ignored `.codex-workflow/scripts/` by default.
- Use a small committed toolkit for agent routing, deterministic checks, Git
  helpers, telemetry, journals, and resume.
- Let Bun run authoritative restores, builds, tests, and other mechanical checks.
- Let agents implement, diagnose, review semantics, and synthesize.
- Choose Luna, Terra, or Sol and their reasoning levels per context.
- Return successful changes uncommitted to the invoking worktree for ordinary
  review.
- Promote a workflow only after repeated use proves a stable recurring process.
- Keep JSON for real interchange, local runtime state, and durable manifests rather
  than describing workflow code.

Trusted-local workflows inherit the invoking Codex task's normal permissions,
network, and package configuration. Wave C, commits, merges, pushes, publication,
and deployment always require the corresponding explicit user authorization.

## Intended layout

```text
eng/codex-workflows/
├── toolkit/        # reusable TypeScript primitives
├── saved/          # deliberately promoted workflows
└── history/        # curated records, never runnable definitions

.codex-workflow/
├── scripts/        # generated one-off workflows
├── runs/           # journals, telemetry, and viewer state
└── worktrees/      # temporary candidates when isolation is useful
```

The toolkit and replacement one-off remediation script have not yet been
implemented. The current acceptance criteria are in the
[pre-Wave-C remediation brief](../../docs/plans/pre-wave-c-remediation.md).

## Historical experiment

The curated 2026-07-22 experiment records live under [`history/`](history/).
Runnable legacy scripts, launchers, provider configuration, approved Plans,
schemas, and machine-readable contracts were removed from the active tree. Git
history preserves them when forensic inspection is necessary.

The readiness pilot established that fan-out, model routing, structured results,
the local viewer, and journal resume can work. The remediation run also showed
that approved-plan hashing, immutable preparation binding, offline restoration,
and extensive cross-file JSON validation imposed more ceremony than ordinary
subagent work and blocked a normal .NET restore.

Historical reports remain accurate descriptions of what happened. Their raw local
state under `.codex-workflow/` is transient and is not a portable dependency.

## Durable artifacts

Feed manifests may remain durable when a later build actually consumes their
package identities and hashes. Raw prompts, full agent output, invocation
arguments, successful test logs, and temporary worktrees remain local and
transient.

The next pre-Wave-C remediation workflow should:

1. create a small one-off TypeScript workflow;
2. allow normal package restoration;
3. have Bun run the authoritative compiler and binding-compiler checks;
4. pass failures back into an agent fix loop;
5. run contextual independent review;
6. leave reviewed changes uncommitted;
7. report readiness without starting Wave C.
