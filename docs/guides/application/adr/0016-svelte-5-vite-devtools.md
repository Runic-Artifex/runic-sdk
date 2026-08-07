# 0016 — Svelte 5 and Vite DevTools integrations

- Status: Accepted and implemented
- Date: 2026-08-07

## Context

ADR 0015 made `@runic-artifex/application-bridge` the single owner of browser
protocol state. That boundary intentionally removed generic renderer-owned MVVM
protocols, but a direct controller is not by itself a complete framework
experience. Svelte applications still need rune projection, component-tree
lifecycle, and SvelteKit output conventions. Vite applications need HMR-safe
resource ownership and diagnostics in the development tool they already use.

The Toolkit CLI previously generated a synthetic Vite configuration around the
application. That approach duplicated framework configuration, obscured plugin
ordering, and could not provide a first-class SvelteKit or official Vite
DevTools experience.

## Decision

1. `@runic-artifex/application-bridge` remains the only protocol runtime.
2. [`runic-svelte`](https://github.com/Runic-Artifex/runic-svelte) owns:
   - `@runic-artifex/svelte`, a Svelte-5-only rune and lifecycle projection;
   - `@runic-artifex/sveltekit`, the static/native SvelteKit adapter and host
     manifest.
3. [`runic-vite`](https://github.com/Runic-Artifex/runic-vite) owns
   `@runic-artifex/vite-plugin-runic-toolkit` for Vite 8, HMR resource
   preservation, sanitized diagnostics, and the official experimental
   `@vitejs/devtools` extension point.
4. Integration packages may own presentation state and framework lifecycle.
   They may not own transport, reconnect, schema validation, revision,
   cancellation, or command semantics.
5. `dotnet runic-toolkit dev` launches the project's normal Vite configuration.
   The CLI does not generate or inject a replacement configuration.
6. Svelte 4 and pre-Svelte-5 compatibility are out of scope.
7. Experimental Vite DevTools dependencies are exact-pinned and advanced only
   after the integration suite passes.

## Consequences

- Svelte and SvelteKit can evolve independently of Toolkit history while
  controlling their outward Toolkit integration.
- The generated Svelte template is an executable contract consumer of the
  separately packed Svelte and Vite integrations.
- Other framework projects can follow the same ownership model without adding
  renderer packages or protocol runtimes to Toolkit core.
- Vite configuration and plugin ordering remain visible and conventional in
  the application repository.
- DevTools observations must stay bounded and sanitized; raw frames, secrets,
  tokens, capabilities, stack traces, and machine paths are not exposed.
