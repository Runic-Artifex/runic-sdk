# ADR 0002: Dependency direction and integration ownership

- Status: Historical — superseded by the shipped package graph
- Updated: 2026-08-05

## Decision

Dependencies point from adapters and composition toward neutral contracts.
The current Application, Assets, and CS-WebUI package READMEs are authoritative
for the shipped graph; this ADR preserves the earlier ownership decision.

The retired headless `RunicFlow.ApplicationBridge` integration described in the
original decision was never a current SDK release product. It must not be
treated as an active package or release target. Runic Assets integrates through
the neutral `Runic.Assets` contract and its host-specific adapters rather than a
Toolkit-named package.

The frontend portion of this ADR is superseded by ADR 0015. Framework renderers
consume Application Bridge without Toolkit-owned protocol adapters; hosting adapters depend
on hosting abstractions; CS-WebUI packages are the only packages that may depend
on the `CsWebUi` package. `eng/verify-architecture.ps1` enforces the allowed source graph.
