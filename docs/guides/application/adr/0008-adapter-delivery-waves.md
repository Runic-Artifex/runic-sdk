# ADR 0008: Defer ecosystem adapters to Waves E and F

- Status: Accepted
- Date: 2026-07-22

## Context

Gate G2 produced a stable framework-neutral MVVM protocol, compiler/build kernel,
TypeScript SDK, browser conformance kit, template engine, and supporting runtime
packages. The original schedule placed Angular, React, Vue, Svelte,
CommunityToolkit.Mvvm, ReactiveUI, and HTMX in one Wave C fan-out.

Maintaining that breadth while the first-party vertical integration is still being
assembled would multiply framework-version, lifecycle, build-tool, and package
matrices. It would also put ecosystem-specific feedback on the critical path for
hardening the core packages.

## Decision

- Wave C is limited to first-party integration: CommunityToolkit.Mvvm, HTMX,
  Hosting, Flow, and shared projection edges.
- Wave D completes the core vertical slice, packaging, documentation, security,
  compatibility, performance, and release-candidate gates.
- Wave E implements React, Vue, and Svelte against the frozen G4 SDK and
  conformance contracts.
- Wave F implements Angular and ReactiveUI. Their broader dependency, generated-
  member, activation, scheduler, and lifecycle concerns receive dedicated gates.

No deferred adapter may require a silent breaking change to a G4 contract. A needed
contract revision must be versioned and reviewed independently.

## Consequences

- The core release candidate is no longer blocked by five ecosystem adapters.
- HTMX and CommunityToolkit still provide early end-to-end pressure on the compiler,
  runtime, templates, validation, commands, disposal, and Native-AOT boundaries.
- React, Vue, and Svelte share one later browser matrix instead of evolving beside
  the core contracts.
- Angular and ReactiveUI can adopt deliberate version ranges and lifecycle policies
  after the relevant ecosystems and toolkit APIs are frozen.
- Standalone planning specifications retain their adapter designs, but this ADR and
  `webuitoolkit-orchestration.html` are authoritative for delivery order.
