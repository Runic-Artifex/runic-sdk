# ADR 0012: Frontend direction

> Historical implementation direction. The `Toolkit` package ownership names
> below predate the shipped Application Bridge package graph and are retained
> for decision history; current identities live in the maintained package docs.

- Status: Accepted
- Updated: 2026-08-05

## Decision

The protocol decision in this ADR is superseded by ADR 0015. Toolkit owns the
framework-neutral Application Bridge wire/runtime model, TypeScript core,
React/Vue/Svelte/Angular adapters, frontend workspace coordination, development
host, and a generic external-compiler seam.

Toolkit does not own a UI language or renderer. External authoring systems may
implement the generic frontend seam without changes to Toolkit.

The browser bridge emits generic diagnostics and refresh events. It does not
assume a renderer or transport application semantics through presentation
objects.
