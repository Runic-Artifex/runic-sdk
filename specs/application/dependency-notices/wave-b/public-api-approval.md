# Public API approval checklist

No Wave B API or package is approved for publication until every applicable item is checked by the owning review. The repository license/publication hold overrides this checklist.

## Identity and architecture

- [ ] Assembly, project, namespace, and prospective package identities use `WebUIToolkit.DependencyNotices.*`.
- [ ] Public types retain the `WebUIToolkit.DependencyNotices` parent namespace.
- [ ] Dependency directions match `package-manifest.md`; Runtime is independent of generator/adapters.
- [ ] All projects target `net10.0`; Core/Engine/Runtime remain BCL-only as contracted.
- [ ] No public API exposes an implementation-only JSON DOM, HTTP handler, filesystem path rooted in a developer machine, or mutable global registry.

## Contracts and compatibility

- [ ] Wave A v1 schemas, public API meanings, diagnostic meanings, PURL/SPDX/evidence semantics, and ordering are unchanged.
- [ ] Document schema v2 is reviewed as an additive complete model; v1 is not relabeled or silently migrated.
- [ ] Unsupported schema majors fail with `WUTNOTICE6003`.
- [ ] All serialized property names, enum tokens, null/empty distinctions, ordering, encodings, and limits are documented and covered by golden fixtures.
- [ ] Every new public diagnostic uses the reserved `WUTNOTICE` range and is present in `diagnostics.md`.
- [ ] API compatibility/baseline tooling reports no unreviewed break.

## API design

- [ ] Requests/results are immutable or expose read-only collections with defensive ownership.
- [ ] Async I/O accepts `CancellationToken`; synchronous APIs do not hide blocking network I/O.
- [ ] Streams/readers remain caller-owned unless ownership is explicitly documented.
- [ ] Errors have stable exception/diagnostic mapping; libraries never exit the process.
- [ ] Culture, current directory, environment, clocks, random values, and static mutable state do not affect results.
- [ ] Adapters and renderers are explicitly composed; no assembly scanning or dynamic activation is required.

## Safety, determinism, and delivery

- [ ] Hostile JSON/XML/path/PURL/SPDX/HTML/redirect/cache/output fixtures cover all documented limits and boundaries.
- [ ] Network-denied empty-cache runs are byte-identical across repeated roots/cultures.
- [ ] Local packed-package consumers prove no project-reference-only assumptions.
- [ ] Committed package locks are portable/RID-free and ordinary `--locked-mode` restore succeeds.
- [ ] RID/AOT publish uses ignored `obj/aot.packages.lock.json`; actual native binaries execute successfully with zero owned warnings.
- [ ] Public XML docs identify security-sensitive inputs, ownership, limits, and mutation behavior.
- [ ] Package license, repository metadata, notices/SBOM, signing/provenance, and API-review evidence are approved.

## Publication decision

- [ ] A later approved ADR explicitly lifts the license publication hold.
- [ ] Package names are reserved/approved by the orchestrator and no conflicting artifact exists.
- [ ] Release notes identify document v2, supported adapter/SBOM subsets, CLI exit codes, security limitations, and migration behavior.

Until all three publication items and all applicable preceding items are checked, artifacts remain local G2 verification packages only.
