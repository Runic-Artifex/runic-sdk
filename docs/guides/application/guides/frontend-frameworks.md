# Frontend frameworks

All browser renderers use `@runic-artifex/application-bridge` as the only
protocol runtime. Framework integrations may own idiomatic presentation state
and lifecycle, but never transport, reconnect, revision, cancellation, schema
validation, or command semantics.

A frontend receives one structural `FrameChannel` from application bootstrap:
Runic Desktop supplies the default local presentation transport, CS-WebUI may
supply its compatibility binding, and the local Hosting boundary uses a binary
WebSocket. In every case the C# host owns the generated contract, session,
lifecycle, revisions, origin gate, and refresh authority. Framework adapters
must not turn that local boundary into a remote-service or deployment contract.

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

`@runic-artifex/vite-plugin-runic`, owned by
[`runic-vite`](https://github.com/Runic-Artifex/runic-vite), is the official
Vite 8 integration. It preserves the bridge resource across HMR, exposes a
sanitized diagnostics endpoint and virtual client module, and contributes a
Runic panel to the official experimental `@vitejs/devtools` API. The
plugin pins the experimental DevTools packages; applications should update
those versions through the integration package's tested release line.

The `dotnet runic dev` command launches the application's normal Vite
configuration. It no longer generates a synthetic wrapper configuration, so
SvelteKit and other Vite frameworks retain full control of their plugins and
routing.

React and Vue consume the controller directly. Angular uses the official
`@runic-artifex/angular` controller projection; every framework integration
must keep the same protocol boundary.

Use `ApplicationBridgeLive` over the selected Desktop, compatibility, or
WebSocket frame channel and `MockApplicationBridge` for frontend-only
development. Both
implement the same semantic service, and the packaged templates are executable
examples.

## Local host-boundary compatibility evidence

The current package-consumer fixtures prove one generated local contract, not
parallel framework host models:

| Boundary | Owner | Contract or responsibility | Independent receipt |
| --- | --- | --- | --- |
| C# host | Runic Application | `runic.artifex.setup` / `1` / generated fingerprint from `bridge.ir.json`; session, revision, lifecycle, and local FrameChannel/WebSocket admission | `eng/current-host-transport/` |
| Svelte projection | `@runic-artifex/svelte` | Supplied controller projection, component lifecycle, and presentation state | `eng/current-svelte-controller/` |
| Angular projection | `@runic-artifex/angular` | Supplied controller DI, signals, and presentation state | `eng/current-angular-controller/` |

All three receipts verify exact local candidates and bind the same generated
manifest identity and fingerprint; `eng/current-host-boundary/` independently
links their repeat receipts. This is evidence for the local W20 boundary only.
Authentication, remote service transport, deployment, SSR, hydration, and
rollout remain W30 gates; native/platform certification remains a W70 gate.
