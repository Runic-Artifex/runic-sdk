# Frontend frameworks

All browser renderers consume `@runic-artifex/application-bridge` directly.
There are no Toolkit-owned React, Vue, Svelte, or Angular protocol adapters.

Create one bridge `Layer` and one controller at application bootstrap. A
component subscribes to validated domain events, projects them into its native
state primitive, and calls named commands through the controller. The
controller owns the single `ManagedRuntime`; components never call
`Effect.runPromise`.

Use `CsWebUiApplicationBridgeLive` in the native application and
`MockApplicationBridge` for frontend-only development. Both implement the same
semantic service. The four packaged project templates are executable examples.
