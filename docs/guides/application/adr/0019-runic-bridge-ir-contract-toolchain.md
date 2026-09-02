# ADR 0019: Runic Bridge IR contract toolchain

- Status: Implemented
- Updated: 2026-09-02
- Supersedes: the JSON Schema and bridge-manifest projection in ADR 0015

## Context

Effect Schema is already the executable frontend contract. Reconstructing a
second frontend schema from JSON Schema discards transformations and annotations,
duplicates ownership, and makes normal schema edits require a manual generation
step. JSON Schema also cannot carry all semantics needed by a strict,
reflection-free .NET projection.

## Decision

A handwritten `application.bridge.ts` is the sole contract source. It uses
`defineApplicationBridgeContract` and references actual command, receipt,
event, snapshot, and domain-error schemas. The frontend imports a generated
facade that adds only the canonical wire fingerprint; it continues to execute
the original Effect schemas.

`@runic-artifex/application-bridge-tooling` lowers Effect's public encoded AST
to versioned Runic Bridge IR. The committed `Contract/bridge.ir.json` owns
portable wire nodes, constraints, command semantics, C# projection metadata,
and the canonical wire fingerprint. Unsupported or contextual semantics fail
generation. JSON Schema is neither generated nor consumed by the V1 toolchain.

The CLI and Vite plugin share one compiler. Vite generates at startup and build,
watches imported schema modules, retains last-good output on errors, and treats
fingerprint changes as full reload boundaries. Direct and Angular builds invoke
the CLI.

Roslyn consumes only Bridge IR, verifies its fingerprint, and emits strict,
reflection-free DTOs, handlers, dispatch, event helpers, and codecs. The
application manifest generator derives bridge identity and fingerprint from the
same IR.

## Consequences

There is one executable frontend schema graph and one portable compiler
projection. C# names and documentation can evolve without changing the protocol
fingerprint. Adding JSON Schema later is a one-way interoperability exporter,
not another source of truth. V1 is a clean cut: legacy manifests, per-schema
JSON files, copied fingerprints, and compatibility readers are removed.

