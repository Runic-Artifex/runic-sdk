# ADR 0009: Agent execution profiles and dynamic workflow pilot

- Status: Superseded by ADR 0010
- Date: 2026-07-22

This ADR records the original experiment. ADR 0010 and the workflow operating
manifest replace its fixed role-to-model mapping, long-lived-task boundary,
network-disabled default, immutable baseline requirements, approved JSON Plan, and
promotion criteria. The historical pilot and remediation reports remain accurate
for the runs they describe. Runnable experiment files have been removed from the
active tree and remain available through Git history.

## Decision

Agent model and reasoning selection follows a small set of task profiles rather than assigning GPT-5.6 Sol High to every task.

| Profile | Current model and effort | Intended work |
|---|---|---|
| Scout | GPT-5.6 Terra Medium | Read-heavy discovery, inventories, evidence collection, and log or test-result triage |
| Builder | GPT-5.6 Terra High | Bounded implementation against frozen contracts, fixtures, consumers, and routine documentation |
| Reviewer | GPT-5.6 Sol High | Independent correctness, security, API, concurrency, lifecycle, trim, and AOT review |
| Lead | GPT-5.6 Sol High | Architecture, contracts, cross-domain integration, and final synthesis; only the orchestrator role may adjudicate gates |
| Escalation | GPT-5.6 Sol XHigh or Max | Explicit escalation for a hard problem that remains unresolved after normal Lead analysis |

Use the lowest profile and reasoning effort that reliably satisfies the task. Deterministic command execution does not require a Lead. Risky work must use an independent Reviewer rather than relying on the author alone. Max is an escalation, not a default; Ultra is not used inside an already explicit orchestration topology.

The repository will pilot `six-ddc/codex-dynamic-workflows` for bounded fan-out, verification, and synthesis inside an orchestrator-owned task. The runtime is pinned to commit `d686268fc76788f76c064bb682ecc54e97729f59` with Codex SDK `0.145.0`, because the commit's SDK `0.137.0` is rejected by the service for GPT-5.6 Sol and Terra. It is not a root dependency, CI dependency, release dependency, or gate authority.

The first pilot is the read-only Wave C readiness assessment in `eng/codex-workflows/`. It uses five Scouts, five independent Reviewers, and one Lead. Its result is advisory and cannot pass G3 or start Wave C.

The follow-up pre-Wave-C remediation uses a separate read-only Plan run and explicitly approved, dependency-staged Apply runs. Each Apply handles one owner work item with a Terra High builder, three parallel Sol High reviews, and Sol High synthesis inside a fresh isolated baseline. The approved plan is a committed, hash-pinned input; model output cannot approve it. Candidate output cannot be applied, committed, merged, or interpreted as permission to activate Wave C by the workflow runtime.

## Safety and ownership constraints

- Only committed, reviewed workflow and provider files may run.
- Workflow state is project-local and ignored because it contains prompts, results, and session metadata.
- Network and web search are disabled unless a later ADR authorizes a specific research workflow.
- The Codex read-only sandbox prevents writes but is not a filesystem read jail. Live runs remove commonly named secret-bearing environment variables and use a clean detached snapshot, but the pilot must not run in a sensitive repository or on a host containing readable material that must not enter prompts, journals, or reports.
- Readiness agents are read-only and may not build, test, restore, generate, create worktrees, or modify files.
- A future writing workflow must use isolated worktrees and preserve `eng/ownership.json`; the owning long-lived task remains responsible for review, commit, and handoff.
- Writing workflows must run from a disposable immutable snapshot so a failed native worktree setup cannot fall back into the main checkout. The launcher must independently verify registered candidate paths, baseline SHA, changed-path ownership, deliverable hashes, and an unchanged main worktree.
- Workflow concurrency may not exceed the repository ceiling of twelve active agents and begins at six for the pilot.
- Resume caching is not long-lived task continuity. Gate pauses, user decisions, branch ownership, merge order, and handoffs remain outside the workflow runtime.
- Raw workflow output never constitutes gate evidence. A maintainer must review and, if appropriate, curate it into a committed evidence artifact.

## Rationale

Terra is appropriate for efficient, bounded discovery and implementation, while Sol is reserved for ambiguity, high-value judgment, and final review. Dynamic workflows make repeatable fan-out and independent verification visible without filling the orchestrator context with raw exploration output.

The third-party runtime is young and executes workflow/configuration JavaScript with user permissions. A pinned, read-only, network-disabled pilot provides evidence before considering broader adoption without coupling repository verification or release production to it.

The pinned upstream test suite is not Windows-clean. Its Gemini CLI runner test can retain child Node processes, and four otherwise successful cases fail during temporary-directory or detached-worktree cleanup. The Windows pilot therefore quarantines those all-backend/cleanup cases and requires the directly applicable Codex runner, parser, provider routing, strict-schema tests, plus a repository-owned offline workflow smoke. This is sufficient to attempt a read-only Codex-only experiment, but it counts against broader promotion.

## Promotion criteria

Broader use requires all of the following:

- the pinned runtime prepares with a clean dependency audit and passes its typecheck, build, applicable offline tests, and repository-owned workflow smoke; any platform quarantine remains documented;
- the pilot completes against one immutable baseline without repository writes or network access;
- the result contains useful, traceable evidence and materially reduces orchestration noise;
- token and elapsed-time use are acceptable compared with a manually delegated equivalent;
- ownership, cache/resume behavior, and stored-data handling remain reviewable;
- any upgrade is separately pinned, audited, and recorded.
