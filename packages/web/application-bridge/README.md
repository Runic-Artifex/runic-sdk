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
loads. It waits up to ten seconds for CsWebUi to install its native send binding
and reads the settled sender again before use. Correlated responses return
through that binding promise as a sequence-ordered host-frame batch; the named
receiver remains available for later unsolicited events. Fast Vite and
SvelteKit startup therefore cannot lose initialization while CsWebUi completes
its bootstrap. The timeout, polling interval, and one-time 25 ms stabilization
delay can be overridden for unusual hosts or tests.

Returned batches remain one owned byte frame until the Effect runtime parses
them. Each envelope is then schema-validated and processed in sequence without
a transport-level parse/stringify pass. Raw ingress and validated-event PubSubs
are bounded and fail into explicit authoritative recovery instead of silently
dropping protocol state. Pending commands are bounded, and the browser retains
only currently pending command identifiers. The authoritative host owns bounded
duplicate-command rejection.

`ApplicationBridgeOptions` exposes the limits for unusual applications:
`maxFrameBytes`, `maxPendingCommands`, `maxBufferedFrames`,
`maxBufferedEvents`, and `maxBatchFrames`. Defaults align
with the host contract where the same limit exists. Raising a limit should be
backed by the Application Bridge benchmark and the application's own burst
tests.
