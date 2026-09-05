# ADR 0019: Member-based Application Bridge and shared IR

- Status: Implemented
- Updated: 2026-09-05
- Supersedes: the contract ownership and initialization decisions in ADR 0015

## Context

Application behavior and state already exist in .NET. Requiring a separate
frontend schema and generated handler implementation for every command duplicates
work and obscures dependency injection. Some applications, including the
Translations Editor, deliberately own their entire contract in Effect instead.

## Decision

C# members are the default authority. One assembly-level
`ApplicationBridgeContract` identifies the application. Partial classes expose one
`BridgeSnapshot` property/provider and `BridgeCommand` methods; immutable DTOs
define requests and receipts. `BridgeEvent` and `BridgeError` identify outbound
payloads. Tags and definition names can be stabilized independently of CLR names.

A shared Roslyn lowerer discovers the entry project and its ProjectReference
graph. Each module emits deterministic metadata, private-member adapters, strict
codecs and a registrar. Generated constructor factories use built-in DI without
reflection. Bridge parts use `TryAddScoped`, so application registrations win.
Each logical session owns an asynchronous scope; reconnects reuse that scope.
The built application owns its root provider. NuGet assemblies are not scanned.

Effect is an explicit whole-contract alternative. Its original schemas remain the
runtime objects; the facade adds the fingerprint. Observable transformations are
rejected. Brands and normalization can be composed above the transport boundary.
Generated C# includes DTOs, codecs, handlers, a typed snapshot provider and scoped
composition. Mixed authority is outside V1.

Initialization is protocol plumbing. An empty-object initialize payload invokes
the snapshot provider directly on initial connection and accepted reconnection.
There is no application initialization command or receipt. Provider failures use
built-in bridge errors.

The Node tooling package alone writes the committed IR and facade. For C# it
invokes the managed Roslyn inspector shipped beside `dotnet runic`; source
generators never write artifacts. Vite, MSBuild, Angular and direct CLI generation
use this pipeline. Invalid candidates retain the last-good files. Valid wire
changes rebuild the host and Vite waits for its matching fingerprint before one
full reload.

IR V1 is a clean pre-release cut. Authority, language bindings, documentation and
DI details are outside the fingerprint. The fingerprint covers protocol identity,
built-in initialization, canonical encoding, snapshot, definitions, constraints,
UUID format, command semantics and limits. Definitions, properties and
order-independent unions are canonical. Moving authority requires stable tags,
definition names and identical wire constraints. JSON Schema and legacy readers
are not part of V1.

## Consequences

Normal C# application startup needs no bridge registration. Behavior can move
between partial classes or source modules without changing its wire identity.
The frontend consumes generated Effect schemas in C# mode and handwritten Effect
schemas in Effect mode. One IR and one facade are committed in either mode.
Roslyn remains a development-time dependency; shipping dispatch, codecs and
module activation support trimming and NativeAOT.
