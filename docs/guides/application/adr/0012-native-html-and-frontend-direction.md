# ADR 0012: Frontend direction

- Status: Accepted
- Updated: 2026-08-05

## Decision

Toolkit owns the framework-neutral MVVM wire/runtime model, TypeScript core,
React/Vue/Svelte/Angular adapters, frontend workspace coordination, development
host, and a generic external-compiler seam.

Toolkit does not own a UI language or renderer. External authoring systems may
implement the generic frontend seam without changes to Toolkit.

The browser bridge emits generic diagnostics and refresh events. It does not
assume a renderer or transport application semantics through presentation
objects.
