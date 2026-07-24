# Workflow Operating Manifest

Status: Working policy
Scope: Portable guidance for trusted-local, agentic development workflows
Adopted by: WebUIToolkit
Last updated: 2026-07-24

## Purpose

This manifest describes how to use scripted agent workflows without losing the
speed and flexibility of ordinary subagent delegation. Its core guidance is
project-neutral and may be copied or adapted by other repositories.

A project may add its own ownership, release, and gate rules, but should distinguish
those product rules from workflow-runtime ceremony.

## Status language

| Marker | Meaning |
| --- | --- |
| **Agreed** | Current operating policy |
| **Implementation choice** | Direction is agreed; the exact mechanism may still change |
| **Historical** | Records an earlier experiment and is not current policy |

Agreement on a workflow policy does not authorize a product milestone, destructive
action, commit, merge, publication, or deployment.

## Core thesis — Agreed

Automated workflows need enough structure for software to coordinate agents,
persist local run state, execute deterministic steps, and report outcomes. They do
not need a stricter trust or approval model merely because JavaScript or TypeScript
coordinates the same agents a user would otherwise spawn directly.

> Workflows should automate the way we already trust Codex to work.

For trusted local development, the rules that apply to ordinary subagents should
also apply to workflow agents. Workflow-specific ceremony must justify itself with
a concrete, plausible failure that it prevents.

## Workflow-first coordination — Agreed

For bounded multi-agent work, workflows are the preferred replacement for manually
coordinating separate Codex tasks or spawning subagents turn by turn. They provide
fan-out, phase ordering, model routing, deterministic checks, monitoring, and
resume behavior without filling the parent context with every intermediate result.

Separate interactive tasks remain useful when work needs frequent user steering,
a long-running independent conversation, or genuinely separate human ownership.
Subagents remain the worker primitive used by workflows. Neither should be the
default coordination layer when a small workflow can express the work directly.

The value of workflows is lost if launching one requires materially more ceremony
than delegating the same work manually.

## Planning belongs to the user — Agreed

No workflow-specific planning phase is required.

The user decides whether a task first needs `/plan`, an HTML orchestration plan, a
Markdown plan, a conversation, or no separate plan. The task-specific workflow
script then expresses the executable control flow. This is the same responsibility
split used for subagent work.

A workflow must not require an approved-plan JSON file, plan hash, preparation
commit, or planning workflow merely to begin ordinary development. Normal approval
rules still apply to destructive and externally consequential actions.

## Prefer one-off workflows first — Agreed

The preferred lifecycle is:

1. describe the concrete task;
2. write a task-specific JavaScript or TypeScript workflow;
3. run and inspect it;
4. keep it disposable by default;
5. save or promote it only after repeated use proves valuable.

This matches the model described in the
[Claude Code dynamic workflows documentation](https://code.claude.com/docs/en/workflows):
the workflow moves the plan into code, intermediate results stay in script
variables, and saving a generated workflow is an optional later step. Saved
workflows may accept structured arguments, but their inputs should represent the
small values that vary between invocations, not an external data model of the
workflow itself.

Important or large does not mean permanent. A project-phase migration may remain
one-off even when it coordinates hundreds of agents.

## Responsibility boundary — Agreed

Deterministic automation belongs in the Bun/toolkit layer:

- restore, build, test, format, lint, package, and publish checks;
- Git status, diffs, changed-path checks, and patch application;
- temporary worktrees when isolation is actually needed;
- command output capture and retry loops;
- concurrency, cancellation, journals, resume, telemetry, and cleanup;
- passing failed-check output into a later fix phase.

Model agents provide judgment:

- discovery and interpretation;
- implementation;
- diagnosis of deterministic failures;
- semantic correctness, API, architecture, and security review;
- resolving conflicting findings;
- final synthesis.

The authoritative verification loop is:

```text
agent edits → Bun checks → agent fixes if needed → Bun rechecks → reviewers inspect
```

An agent may run a targeted command while diagnosing a problem, but Bun owns the
recorded acceptance run. Reviewers consume those results instead of rerunning the
same suite.

## Models and reasoning are contextual — Agreed

Model and reasoning selection is made per task and per phase. Luna may be sufficient
for routine work, Terra may be the efficient choice for broader implementation or
analysis, and Sol may be required for difficult construction or judgment.

The toolkit should make routing concise and may offer defaults. It must not encode a
permanent rule that every builder, reviewer, or lead always uses one model.

Use the least expensive model and reasoning level that reliably handles the
specific context, and escalate when evidence shows that the current choice is
insufficient.

## Candidate review and integration — Agreed direction

The invoking task's worktree is the default delivery boundary:

1. a writer changes the worktree;
2. Bun records the diff and runs deterministic checks;
3. reviewers inspect the same changes and check output;
4. a fix iteration runs when needed;
5. the workflow leaves successful changes visible and uncommitted;
6. the user or invoking task reviews and commits normally.

There is no default candidate manifest, candidate hash, approved integration
commit, or custom merge queue.

Parallel writers may share a worktree only when their paths are disjoint and the
runtime supports it safely. Otherwise the toolkit may use temporary worktrees and
apply successful patches sequentially. Automatic commit, merge, push, or deployment
requires explicit authorization.

## Evidence lifetime — Agreed

Transient by default:

- generated one-off scripts;
- prompts, full transcripts, intermediate reviews, and synthesis inputs;
- successful test logs;
- temporary worktrees and invocation arguments;
- raw local result JSON.

These may persist locally long enough for monitoring, diagnosis, and resume, then
be cleaned. Crash persistence does not imply repository permanence.

Durable only when independently useful:

- final source code and tests;
- a concise handoff or workflow summary when needed;
- architectural decisions;
- release, compliance, or publication evidence;
- manifests consumed by another tool or later build;
- intentionally promoted reusable workflows.

## Cost and latency telemetry — Agreed

Telemetry is automatic information, not an approval gate. A workflow viewer should
report:

- wall-clock time;
- tokens by run, phase, agent, model, and reasoning level;
- agent count and fix iterations;
- deterministic-check failures;
- human interventions;
- final outcome.

The useful comparison is whether the workflow finished correctly with less elapsed
time and human coordination than manual delegation. Generous caps and warnings may
prevent runaway loops; detailed up-front token allocation is not required.

## Trusted-local execution — Agreed

Trusted-local mode inherits the invoking Codex task's permissions, network access,
and normal development environment. It should:

- stay within the selected repository and explicit temporary worktrees;
- preserve unrelated user changes;
- follow ordinary approval rules for destructive or external actions;
- bound concurrency and support cancellation;
- keep enough local state for monitoring and resume;
- clearly report changed files, checks, and final status;
- avoid exposing secrets under the same rules as normal Codex work;
- never silently commit, merge, push, deploy, or activate a product wave.

It should not add offline restoration, immutable baseline chains, or a separate
zero-trust security model.

## Untrusted execution — Agreed

The reusable toolkit does not currently provide an untrusted-input mode.

Workflow code from an untrusted pull request, arbitrary external commands, unknown
repositories, production data, or secrets should run in a real disposable VM,
container, CI worker, or operating-system sandbox when such a use case arises.
Complex validation inside a trusted-local workflow toolkit is not a substitute for
process isolation.

Do not build a stricter mode until a concrete requirement exists.

## Small reusable toolkit — Agreed direction

One-off workflows should be cheap to write. A committed toolkit should provide only
stable orchestration primitives, such as:

- concise agent/model configuration;
- parallel map, review, and synthesis patterns;
- deterministic checks;
- Git, patch, and optional worktree helpers;
- telemetry, journals, resume, and cleanup;
- a small final-result type.

Task objectives, paths, checks, acceptance criteria, and branching belong directly
in the task-specific TypeScript workflow.

A suitable project layout is:

```text
eng/codex-workflows/
├── toolkit/        # committed reusable TypeScript
└── saved/          # only workflows deliberately promoted for reuse

.codex-workflow/
├── scripts/        # ignored generated one-off workflows
├── runs/           # ignored local journals and telemetry
└── worktrees/      # ignored temporary candidates when needed
```

TypeScript on Bun is the preferred implementation. PowerShell may remain a thin
platform entry point, but workflow meaning should not be divided between a large
launcher and several data files.

## Keep JSON at real boundaries — Agreed

TypeScript expresses workflow stages, constants, branching, schemas, and internal
state. JSON is appropriate for:

- output consumed by an independent tool;
- local journals and viewer state;
- durable manifests representing produced artifacts;
- configuration intentionally edited independently of workflow code.

JSON is not the default for:

- workflow stages already expressed in code;
- constants owned by one task-specific workflow;
- one-time implementation plans;
- internal launcher state copied into agent prompts;
- approval tokens for workflow code.

Structured agent-result schemas remain useful and should normally live beside the
relevant agent call in TypeScript.

## Promotion rule — Agreed

Use repeated use as the signal:

- first use: create a one-off workflow;
- second similar use: reuse informally and identify stable pieces;
- third use: consider promotion.

Promotion is qualitative, not automatic. The workflow should have succeeded on
multiple separate occasions, retained substantially the same orchestration shape,
accepted small inputs, and become cheaper to maintain than to regenerate.
Promotion is a refactoring after successful use, never a prerequisite for the
first run.

## Evaluation questions

Before adding workflow machinery, ask:

1. Which concrete failure does it prevent?
2. Is that failure plausible in the actual trust model?
3. Could Bun check it directly?
4. Would an ordinary subagent be allowed to do the same thing?
5. Does it improve recovery or merely add approval ceremony?
6. Is the cost proportional to the impact?
7. Is this truly recurring work?
8. Could the value be a typed constant instead of persistent JSON?
9. Does the behavior belong in the small toolkit?

## Remaining implementation choices

The operating model is settled. These narrower implementation choices remain:

- the exact toolkit API;
- whether the current worktree or temporary worktrees are the default for multiple
  simultaneous writers;
- retention and cleanup timing for local journals, scripts, and worktrees;
- the explicit option, if any, for an authorized workflow to create a commit;
- which existing WebUIToolkit JSON artifacts have real downstream consumers.

## WebUIToolkit application

For WebUIToolkit:

- Wave C remains inactive until the user explicitly starts it.
- The 2026-07-22 readiness and remediation workflows are historical experiments.
- Their immutable baselines, approved JSON plan, offline restore boundary, and
  multi-layer validation are not templates for future workflows.
- Do not extend the legacy remediation launcher to implement this manifest.
- Extract only reusable runtime and viewer pieces, then create the next remediation
  attempt as a small one-off TypeScript workflow.
- Bun runs authoritative restores and tests with normal development access.
- The workflow returns reviewed, uncommitted changes to the invoking worktree.
- Existing historical reports remain accurate records of their original runs.

## Decision record

| Date | State | Decision or observation |
| --- | --- | --- |
| 2026-07-24 | Historical observation | The first remediation workflow imposed materially more ceremony than equivalent subagent work. |
| 2026-07-24 | Agreed | Trusted-local workflows use the same practical trust model as ordinary local Codex work. |
| 2026-07-24 | Agreed | Workflows replace manual multi-task and subagent coordination for bounded agentic work. |
| 2026-07-24 | Agreed | Planning is user-owned and is not an enforced workflow phase. |
| 2026-07-24 | Agreed | Task-specific, disposable workflows are the default. |
| 2026-07-24 | Agreed | Bun owns deterministic execution; agents own judgment. |
| 2026-07-24 | Agreed | Model and reasoning selection is contextual. |
| 2026-07-24 | Agreed | Successful changes return uncommitted for ordinary review and integration by default. |
| 2026-07-24 | Agreed | Evidence is transient unless it has independent future value. |
| 2026-07-24 | Agreed | JSON is reserved for real interchange, runtime state, and durable artifacts. |
| 2026-07-24 | Agreed | No untrusted-input mode is implemented without a concrete requirement. |
