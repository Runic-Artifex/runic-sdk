# ADR 0006: Target-framework policy

- Status: Accepted
- Date: 2026-07-22

## Decision

- Shipping runtime projects target `net10.0` by default through `$(WebUIToolkitDefaultTargetFramework)`.
- Text Resources runtime may target `net8.0` through `$(WebUIToolkitCompatibilityTargetFramework)` to preserve its documented independent compatibility boundary.
- Roslyn analyzers, incremental generators, and compiler components that must load in older build hosts target `netstandard2.0` through `$(WebUIToolkitGeneratorTargetFramework)`.
- Tests and executables choose their target explicitly.

Shipping libraries opt into trim and Native-AOT analyzers by setting `<WebUIToolkitShippingProject>true</WebUIToolkitShippingProject>`. Exceptions require an ADR and a packed publish-and-run regression test.
