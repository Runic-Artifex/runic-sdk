# ADR 0002: Dependency direction and integration ownership

- Status: Accepted
- Updated: 2026-08-05

## Decision

Dependencies point from adapters and composition toward neutral contracts.
Toolkit core does not reference an independent RunicArtifex product.

The product owns its official Toolkit adapter. Therefore packages such as
`RunicFlow.RunicToolkit`, `RunicAssets.RunicToolkit`, and
`RunicMarkup.RunicToolkit.*` live and release with their product, depend on the
public Toolkit package boundary, and may evolve without adding product history
or implementation details to this repository.

Inside Toolkit, framework adapters depend on MVVM core; hosting adapters depend
on hosting abstractions; CsWebUi packages are the only packages that may depend
on CsWebUi. `eng/verify-architecture.ps1` enforces the allowed source graph.
