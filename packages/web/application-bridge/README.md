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
