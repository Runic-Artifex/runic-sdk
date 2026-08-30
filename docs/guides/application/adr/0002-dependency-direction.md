# ADR 0002: Dependency direction and integration ownership

- Status: Accepted
- Updated: 2026-08-05

## Decision

Dependencies point from adapters and composition toward neutral contracts.
Toolkit core does not reference an independent RunicArtifex product.

The product owns its official Application integration. Therefore packages such
as the headless `RunicFlow.ApplicationBridge` operation integration live and
release with their product, depend on the public Application package boundary,
and may evolve without adding product history or implementation details to this
repository. Runic Assets integrates through the neutral `Runic.Assets` contract
and its host-specific adapters rather than a Toolkit-named package.

The frontend portion of this ADR is superseded by ADR 0015. Framework renderers
consume Application Bridge without Toolkit-owned protocol adapters; hosting adapters depend
on hosting abstractions; CS-WebUI packages are the only packages that may depend
on the `CsWebUi` package. `eng/verify-architecture.ps1` enforces the allowed source graph.
