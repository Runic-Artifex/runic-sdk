# Parallel implementation orchestration

This document describes repository coordination, not product priority. The
[current roadmap](../roadmap/README.md) owns work ordering and the user decides
when work starts.

## Active rules

- `eng/ownership.json` is the authoritative path-ownership registry.
- Parallel tasks must have disjoint writable paths or an explicit integration
  owner.
- Shared solution, build, CI, ADR, registry, and release files are integrated
  by the primary task after domain changes are reviewed.
- Agents own discovery, implementation, diagnosis, semantic review, and
  synthesis.
- Deterministic commands own restores, builds, tests, formatting, architecture
  checks, and Git inspection.
- Successful workflows normally leave reviewed changes uncommitted. Commit,
  merge, push, publication, and roadmap activation require explicit user
  authorization.
- A cross-owner change should be expressed through a versioned package, schema,
  corpus, manifest, public API, or ADR rather than an undocumented dependency.

The [workflow operating manifest](../../eng/codex-workflows/workflow-operating-manifest.md)
defines the portable workflow protocol. Task-specific workflows are preferred
for repeated or highly parallel bounded work; they are not a prerequisite for
ordinary repository changes.

## Dependency order

When a change crosses packages, integrate from stable foundations outward:

1. protocol and schema contracts;
2. MVVM runtime and compilers;
3. framework-neutral web client and conformance fixtures;
4. CommunityToolkit, ReactiveUI, cwhtml/HTMX, Flow, and Hosting adapters;
5. CsWebUi application composition;
6. samples and package consumers; and
7. cumulative verification and release evidence.

This order prevents an adapter or sample from silently redefining a lower-level
contract.

## Historical wave model

The former Waves A–G established the repository foundations, core packages,
framework adapters, and release rehearsal. Their readiness documents and
ownership tables are historical evidence, not the current schedule:

- [archived planning material](../roadmap/archive/README.md);
- [workflow history](../../eng/codex-workflows/history/README.md); and
- [release evidence](../release/README.md).

New work should use the current product priorities and current ownership
registry instead of inferring activation from an old wave label.

