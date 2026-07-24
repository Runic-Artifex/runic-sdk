# ADR 0003: Parallel task and worktree ownership

- Status: Superseded in execution topology by ADR 0010
- Date: 2026-07-22

ADR 0010 retains exclusive path ownership but replaces the requirement for one
long-lived Codex task, branch, and worktree per implementation stream. This
document records the original Wave A/B coordination model.

## Decision

Every implementation stream uses one long-lived Codex task, one branch, one Git worktree, and an exclusive path set. Parallel agents inside that task may edit only disjoint owned paths; one integrator owns commits.

Only the orchestrator edits root solution files, `Directory.*`, `global.json`, root/shared lockfiles, shared CI/release files, the contract registry, and cross-repository documentation. A domain owns and commits `packages.lock.json` files located inside its registered project directories.

Each handoff contains:

- branch and commit SHA;
- owned-path diff;
- approved public API and package IDs;
- dependency/version manifest;
- schema, protocol, and diagnostic versions;
- packed artifacts in the local feed;
- deterministic fixtures or goldens;
- test, package-consumer, trim, and Native-AOT evidence;
- migration notes and open risks.

Contract-breaking requests merge through the orchestrator first. Consumers then rebase onto the new gated baseline.
