# Application Bridge

The Application Bridge is Runic Application's official boundary between a
frontend and an application host.

1. Author the encoded wire contract with Effect Schema.
2. Generate and commit JSON Schema plus the canonical bridge manifest.
3. Let the C# analyzer generate wire records, handler interfaces, typed event
   publishers, and exhaustive dispatch.
4. Implement the generated handler with domain services.
5. Host one caller-owned `ApplicationBridgeSession` through Runic Desktop or
   the local `Runic.Application.Hosting` WebSocket transport.
6. Bootstrap one `ApplicationBridgeLive` or `MockApplicationBridge`
   `Layer`, then expose one `createApplicationBridgeController(...)` to the UI.

The host owns sessions, authoritative revisions, operation lifetimes,
cancellation, privileged resources, and sanitized failures. The frontend owns
presentation and transient interaction state. Long-running commands return an
operation ID promptly and publish progress through the Effect Stream.

For the local WebSocket boundary, the host maps one binary endpoint over its
existing session, enforces configured origins and `BridgeLimits`, and admits a
replacement connection only after a higher-epoch initialization. The frontend
may reconnect its `FrameChannel`, but it cannot create a session, select a
revision, or bypass authoritative recovery. Asset and translation refreshes are
published by the host through that same session.

`GenericHostApplicationHost` adapts an explicitly supplied C# lifetime; it is
not a second frontend host. Likewise, a Desktop attachment and a WebSocket
attachment must not compete for one session. This boundary deliberately stops
before authentication, authorization, public service routing, deployment, SSR,
hydration, and rollout.

The hosted-service profile is documented separately in
[Hosted service admission](hosted-service.md). It does not promote this local
WebSocket endpoint into a public route.

## Optional Runic Flow orchestration

Applications with non-trivial process policy can use the headless `RunicFlow`
runtime behind generated handlers. `RunicFlow.ApplicationBridge` reuses the
bridge operation identifier while adding concurrency slots, timeout, monitoring,
and typed outcomes. Flow process versions remain process-local; this bridge still
owns wire sessions, revisions, sequences, reconnect, and cancellation.

Keep contracts application-specific. Expose `StartInstallation`,
`DestinationSelected`, and `OperationProgress`, for example—not generic Flow
commands or internal process snapshots.

The committed Setup contract under `protocol/application-bridge/setup` is the
reference contract. The package-only runnable Setup application lives in
`runic-toolkit-examples`.

## Performance and boundedness

The TypeScript core owns one Effect `ManagedRuntime`, processes raw frames in a
scoped Fiber, and exposes validated events through a bounded Effect PubSub.
Correlated native batches cross the transport as one owned byte frame and are
decoded once by the Effect runtime. Buffer overflow is a typed recovery
condition; the bridge never silently drops an event and continues speculative
state.

Use `npm run benchmark:application-bridge` to record transport-batch and full
Effect round-trip observations. CI runs the benchmark's deterministic
structural gate. Wall-clock and retained-heap values are evidence, not portable
release thresholds.
