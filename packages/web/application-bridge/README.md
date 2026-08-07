# `@runic-artifex/application-bridge`

The official schema-first boundary between a TypeScript UI and a RunicToolkit
application host. Effect owns validation, services, streams, resource lifetime,
mocking, and fault injection. Rendering frameworks consume the service but do
not own its protocol state.

Application contracts use named domain commands and events. They never expose
generic ViewModel members, `setProperty`, or `execute` operations.

Create one `CsWebUiApplicationBridgeLive`, `MockApplicationBridge`, or
`TestApplicationBridge` Layer at bootstrap and pass it to
`createApplicationBridgeController`. The controller owns one `ManagedRuntime`
and exposes promises plus a validated event subscription at the UI edge.

`createCsWebUiFrameChannel` can be created as soon as the application module
loads. It keeps the host-event receiver ready and waits up to ten seconds for
CsWebUi to install its native send binding, then reasserts the host-event
receiver immediately before the first send. Fast Vite and SvelteKit startup
therefore cannot race or be overwritten by the host bootstrap. The timeout and
polling interval, plus the one-time 25 ms response-channel stabilization delay,
can be overridden for unusual hosts or tests.
