# Application Bridge

The Application Bridge is RunicToolkit’s official boundary between a browser UI
and an application host.

1. Author the encoded wire contract with Effect Schema.
2. Generate and commit JSON Schema plus the canonical bridge manifest.
3. Let the C# analyzer generate wire records, handler interfaces, typed event
   publishers, and exhaustive dispatch.
4. Implement the generated handler with domain services.
5. Host a fresh `ApplicationBridgeSession` through
   `UseApplicationBridge(...)` and the CsWebUi adapter.
6. Bootstrap one `CsWebUiApplicationBridgeLive` or `MockApplicationBridge`
   `Layer`, then expose one `createApplicationBridgeController(...)` to the UI.

The host owns sessions, authoritative revisions, operation lifetimes,
cancellation, privileged resources, and sanitized failures. The frontend owns
presentation and transient interaction state. Long-running commands return an
operation ID promptly and publish progress through the Effect Stream.

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
