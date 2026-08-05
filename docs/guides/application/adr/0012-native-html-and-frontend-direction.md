# ADR 0012: Frontend and external compiler direction

- Status: Accepted
- Updated: 2026-08-05

## Decision

Toolkit owns the framework-neutral MVVM wire/runtime model, TypeScript core,
React/Vue/Svelte/Angular adapters, frontend workspace coordination, development
host, and a generic external-compiler seam.

Toolkit does not own a markup language or renderer. Runic Markup owns `.cwhtml`,
C# markup, HTMX composition, language tooling, and the packages that map its
compiler artifacts into the generic Toolkit seam. Other languages may implement
the same seam without changes to Toolkit.

The browser bridge emits generic compiler diagnostics and refresh events. It
does not call HTMX or assume a renderer.
