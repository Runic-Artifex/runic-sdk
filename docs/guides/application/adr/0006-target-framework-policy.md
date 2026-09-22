# ADR 0006: Target-framework policy

> Historical policy record. The `RunicToolkit*` MSBuild property names below
> belong to the pre-shipped topology; consult the current build files and
> package READMEs for active property and package identities.

- Status: Accepted
- Date: 2026-07-22

## Decision

- Shipping runtime projects target `net10.0` by default through `$(RunicToolkitDefaultTargetFramework)`.
- Compatibility-sensitive Toolkit projects use `$(RunicToolkitCompatibilityTargetFramework)`, currently fixed to `net10.0` with the rest of the repository.
- Roslyn analyzers, incremental generators, and compiler components use `$(RunicToolkitGeneratorTargetFramework)`, also fixed to `net10.0`. Consuming builds therefore require a .NET 10-capable compiler host.
- Tests and executables choose their target explicitly.

Shipping libraries opt into trim and Native-AOT analyzers by setting `<RunicToolkitShippingProject>true</RunicToolkitShippingProject>`. Exceptions require an ADR and a packed publish-and-run regression test.
