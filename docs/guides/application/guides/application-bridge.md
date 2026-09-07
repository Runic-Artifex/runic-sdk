# Application Bridge

The Application Bridge is Runic Application's official boundary between a
frontend and an application host.

C# members define new application contracts. Declare the root once:

```csharp
[assembly: ApplicationBridgeContract("example.counter", 1, ContractName = "Counter")]

public sealed partial class CounterState
{
    private int count;
    [BridgeSnapshot] private CounterSnapshot Snapshot => new(count);
    public int Increment(int amount) => count += amount;
}

public sealed partial class CounterCommands(CounterState state)
{
    [BridgeCommand(AdvancesRevision = true)]
    private CounterIncremented Increment(IncrementCounter command) =>
        new(state.Increment(command.Amount));
}

public sealed record CounterSnapshot(int Count);
public sealed record IncrementCounter([property: BridgeMinimum(1)] int Amount);
public sealed record CounterIncremented(int Count);
```

The four desktop templates split state and commands this way. No handler
interface, manual registry, copied fingerprint or handwritten JSON is needed.
The frontend imports the generated facade and creates one bridge controller.

## Members and wire models

A command has exactly one request DTO, an optional `BridgeCommandContext` and
optional `CancellationToken`. Returns are a receipt DTO, `Task<T>` or
`ValueTask<T>`. Snapshot properties or methods return the snapshot directly;
methods can accept `BridgeSnapshotContext` (session ID and current revision) and
a cancellation token, synchronously or asynchronously. Private members work
through generated adapters in the same partial class. Bridge parts in V1 must
be concrete, nongeneric, top-level partial classes.

Records and sealed init-only classes are supported. Wire properties use camel
case or `JsonPropertyName`; codecs synthesize and validate `_tag` on tagged DTOs.
Use `BridgeTag` and `BridgeName` to stabilize tags and definition names. Nullable
properties are required and accept null. `BridgeOptional<T?>` distinguishes
missing, null and a present value. Bounds, multiples, patterns, string/collection
length and structural uniqueness use the `Bridge*` attributes. `long` and `ulong`
require `BridgeSafeInteger`; `Guid` uses canonical lowercase UUID strings. Enums
use string member names, optionally `JsonStringEnumMemberName`. Mutable setters,
custom converters, decimal, float, date/time, URI and polymorphic models are
outside the portable V1 core. Use double for JSON numbers.

Declare events with `[BridgeEvent]` and errors with `[BridgeError]`. Generated
extensions in `Runic.Application.Bridge.Generated` provide
`context.Events.Publish<Tag>Async(value, cancellationToken)` and
`throw value.ToBridgeError()`. Events retain transactional ordering and
backpressure. Cancellable commands must start an operation and their receipt must
include a required operation identifier.

Cancellation addresses an operation in the admitted session and connection. An
older expected revision is allowed because progress can advance the revision
while cancellation is in transit; a future revision is still rejected. Wait for
the new operation's receipt before enabling its cancel action so a previous
operation identifier cannot be reused accidentally.

## Dependency injection and modules

Each bridge part is registered with `TryAddScoped` using a generated constructor
factory. Register dependencies and overrides through `builder.Services` before
building the application. Each logical session owns one asynchronous scope;
reconnections retain its state and independent sessions receive separate state.
Closing the session disposes scoped services, including `IAsyncDisposable`.
The application disposes its root service provider.

Put bridge parts in referenced class libraries with the bridge analyzer enabled.
Their generated metadata and registrars participate through `ProjectReference`;
arbitrary package assemblies do not. Keep one contract root in the entry project
and exactly one snapshot across the graph. Duplicate tags or conflicting module
definitions fail compilation. Constructor dependencies must be registered before
session activation.

## Initialization and frontend ownership

The runtime sends the built-in initialize envelope with `{}`. The host invokes
the snapshot provider for the first connection and each accepted reconnection,
and returns its snapshot directly. Initialization failures use built-in errors;
there are no application initialization DTOs.

[Frontend contracts](frontend-contracts.md) describes generated Effect schemas,
CLI/Vite generation and the explicit whole-contract Effect alternative. The
latter preserves handwritten Effect schema objects and generates a typed C#
snapshot provider alongside command handlers.

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
