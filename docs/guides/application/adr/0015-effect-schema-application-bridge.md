# ADR 0015: Effect Schema-first Application Bridge

- Status: Implemented
- Updated: 2026-08-07
- Tracks: [issue #5](https://github.com/Runic-Artifex/runic-toolkit/issues/5)

The JSON Schema and bridge-manifest projection in this decision is superseded
by [ADR 0019](0019-runic-bridge-ir-contract-toolchain.md).

## Context

The prerelease MVVM bridge proved bounded binary CS-WebUI transport,
NativeAOT-safe hosting, deterministic generation, reconnect, revisions,
cancellation, fixtures, and mock/production parity. Its public contract is too
generic: applications appear as numeric properties and commands instead of
named domain behavior, and handwritten TypeScript protocol types can drift from
runtime validation.

## Decision

Replace the public MVVM projection protocol with a framework-neutral Application
Bridge built around named domain commands, receipts, snapshots, events, and
schema-backed tagged errors.

- Effect Schema is authoritative for encoded TypeScript wire data.
- The contract build emits deterministic JSON Schema plus a canonical bridge
  manifest. Both are committed and checked for staleness.
- C# generators consume only those committed artifacts. They do not invoke Node
  during compilation and emit reflection-free, NativeAOT-compatible dispatch.
- The TypeScript runtime exposes one Effect service and owns its resources in
  one `ManagedRuntime`. Host events are a scoped Stream.
- CS-WebUI retains one bounded binary channel and explicit session, sequence,
  revision, cancellation, reconnect, and teardown behavior.
- Rendering-framework adapters project validated application state. They do not
  parse frames or own connection, retry, revision, or cancellation state.

## Initial boundaries

- NuGet contract kernel: `RunicToolkit.ApplicationBridge`.
- Generator: `RunicToolkit.ApplicationBridge.Generators`.
- CS-WebUI adapter: `RunicToolkit.Hosting.CsWebUi.ApplicationBridge`.
- npm runtime: `@runic-artifex/application-bridge`.
- The first schema subset is deliberately bounded to null, Boolean, string,
  finite number, bounded integer, readonly arrays and objects, explicit optional
  fields, string-literal/tagged unions, supported primitive brands, and declared
  references.
- Generated public types describe application contract data and handler
  signatures. Transport envelopes and dispatch mechanics remain internal.
- TypeScript uses schema inference and mapped types first; source generation is
  reserved for deterministic helpers that materially prevent drift.

The first production vertical is a neutral Setup application in
`runic-toolkit-examples`. It must demonstrate initialization, backend-owned
navigation and resource selection, a long-running operation, progress, explicit
cancellation, reconnect snapshot recovery, NativeAOT, and mock/live Layer parity.

## Migration

1. Build the contract kernel, schemas, manifest, and cross-language fixtures.
2. Generate C# wire types, handler surfaces, dispatch, JSON metadata, and stable
   diagnostics from committed artifacts.
3. Adapt the proven CS-WebUI channel to the new envelopes and implement the
   Effect live, mock, and fault-injection Layers.
4. Prove the design through the Setup vertical.
5. Remove obsolete numeric-member MVVM packages, renderer lifecycle adapters,
   and documentation only after the new vertical and package consumers pass.

No compatibility adapter is planned while all known consumers remain
prerelease. One may be added only for a concrete published consumer.

## Implementation evidence

- [runic-toolkit#7](https://github.com/Runic-Artifex/runic-toolkit/pull/7)
  delivers the Effect runtime, committed schemas and manifest, reflection-free
  generator and dispatch, CS-WebUI integration, migrated framework templates,
  package gates, and removal of the obsolete MVVM surface.
- [runic-toolkit-examples#12](https://github.com/Runic-Artifex/runic-toolkit-examples/pull/12)
  delivers the package-only Setup vertical with authoritative navigation,
  opaque destination selection, operation progress, completion, failure,
  cancellation, recovery, NativeAOT, and a real Windows native-host test.
- [runic-docs#2](https://github.com/Runic-Artifex/runic-docs/pull/2)
  establishes Application Bridge as the documented integration model.
- GitHub Packages preview `0.1.0-preview.13.1` proves isolated NuGet and npm
  consumption before any public release.

## Consequences

Application concepts become visible in schemas, traces, mocks, generated
handlers, and tests. Generator scope becomes smaller and more explicit because
business policy, navigation, authorization, retries, and process transitions
remain application-authored decisions. Applications may implement those
decisions directly or use the optional headless `RunicFlow` process runtime;
neither choice changes Application Bridge protocol ownership. The obsolete generic MVVM protocol,
packages, renderer lifecycle adapters, and documentation have been removed;
Application Bridge is the architecture to extend.
