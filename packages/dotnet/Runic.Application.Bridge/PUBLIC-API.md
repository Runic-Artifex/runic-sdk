# Public API

Applications annotate snapshot providers and command methods on partial classes.
Effect-authored contracts instead expose a generated handler interface. Each
logical `ApplicationBridgeSession` owns a DI scope. Commands receive `BridgeCommandContext`, which
provides only session metadata, a safe event publisher, and an operation factory.
Raw transport frames and native callbacks never cross the handler boundary.

Every client and host envelope carries the generated manifest's SHA-256 contract
fingerprint and a reconnect epoch. A reconnect must perform initialization again;
commands admitted before a disconnect are terminally discarded and must not be
replayed against the resynchronized snapshot.

Transport implementations can encode one envelope with
`ApplicationBridgeCodec.EncodeHost`, or write envelopes directly into an owned
bounded `Utf8JsonWriter` with `ApplicationBridgeCodec.WriteHost` to avoid an
intermediate byte array when constructing a correlated batch.

`ApplicationBridgeContractAttribute` declares the root. `BridgeSnapshotAttribute`,
`BridgeCommandAttribute`, `BridgeEventAttribute` and `BridgeErrorAttribute`
declare behavior and payloads. `BridgeTagAttribute` and `BridgeNameAttribute`
stabilize wire identity. `BridgeSnapshotContext` exposes session ID and revision.
The `Bridge*` constraint attributes express portable value/collection limits.
`IApplicationBridgeDispatcher.GetSnapshotAsync` returns the encoded snapshot
without routing initialization through an application command.

`ApplicationBridgeSessionFactory.Create(IServiceProvider)` resolves the generated
dispatcher in an owned async scope. `BridgeModuleAttribute`, `BridgeModuleRegistry`
and `MemberApplicationBridgeDispatcher` support statically generated module
registration and dispatch; they do not discover handlers at runtime.

Scoped services that must stop before native owner destruction register the same
instance as `IApplicationPresentationLifetime`. `StopAsync()` rejects new work,
requests cancellation and awaits native release without blocking the owner thread.
The session factory resolves these hooks in the generated feature scope; reconnect
does not invoke them. `HasPresentationLifetimes` identifies this requirement.
`StopPresentationAsync()` is idempotent and lets transports drain these services
before acquiring teardown gates. Session disposal then drains dispatch and disposes
the owned scope. Concurrent disposal waits for the same completion, including faults.
