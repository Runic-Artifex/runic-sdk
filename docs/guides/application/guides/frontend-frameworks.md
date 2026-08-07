# Frontend frameworks

All browser renderers use `@runic-artifex/application-bridge` as the only
protocol runtime. Framework integrations may own idiomatic presentation state
and lifecycle, but never transport, reconnect, revision, cancellation, schema
validation, or command semantics.

## Svelte 5

`@runic-artifex/svelte`, owned by
[`runic-svelte`](https://github.com/Runic-Artifex/runic-svelte), is the official
Svelte integration. It requires Svelte 5, projects bridge snapshots and events
into runes, provides typed component-tree context, starts on mount, and performs
idempotent teardown. There is deliberately no Svelte 4 or legacy-store build.

SvelteKit applications additionally use `@runic-artifex/sveltekit`. Its static
adapter records a deterministic native-host manifest and supports either fully
prerendered output or an SPA fallback. The adapter does not create a second
Application Bridge runtime.

## Vite 8 and DevTools

`@runic-artifex/vite-plugin-runic-toolkit`, owned by
[`runic-vite`](https://github.com/Runic-Artifex/runic-vite), is the official
Vite 8 integration. It preserves the bridge resource across HMR, exposes a
sanitized diagnostics endpoint and virtual client module, and contributes a
Runic Toolkit panel to the official experimental `@vitejs/devtools` API. The
plugin pins the experimental DevTools packages; applications should update
those versions through the integration package's tested release line.

The `dotnet runic-toolkit dev` command launches the application's normal Vite
configuration. It no longer generates a synthetic wrapper configuration, so
SvelteKit and other Vite frameworks retain full control of their plugins and
routing.

React, Vue, and Angular currently consume the controller directly. Their future
integration packages belong to their own integration repositories and must keep
the same protocol boundary.

Use `CsWebUiApplicationBridgeLive` in the native application and
`MockApplicationBridge` for frontend-only development. Both implement the same
semantic service, and the packaged templates are executable examples.
