# WebUIToolkit.Hosting

`WebUIToolkit.Hosting` is the BCL-only deterministic lifecycle kernel for a
single-use WebUIToolkit application. It depends on
`WebUIToolkit.Hosting.Abstractions`; it does not reference MVVM, command-line,
native `cs-webui`, or Microsoft.Extensions implementations. Composition
adapters bridge those systems through the neutral contracts.

## Lifecycle

Construct an `ApplicationLifecycleDescriptor` from an `IApplicationHost`,
validators, ordered startup participants, and mode runners. Then construct an
`ApplicationLifecycleKernel`, optionally supplying a `TimeProvider`, and call
`RunAsync` exactly once.

The kernel validates in registration order, starts the host, starts participants
by phase and registration order, selects exactly one mode runner, and tears down
only successfully started participants in reverse completion order. Competing
stop requests share one `IApplicationStopController` and one completion task.
`DisposeAsync` is idempotent and initiates stop when execution is still active.

The first terminal result or failure is primary. A later teardown failure never
replaces an earlier non-success exit code; it is exposed through
`SecondaryFailures`. A teardown failure does replace an otherwise successful
result. Every wait is bounded by immutable timeout snapshots and the injected
clock, so tests do not need wall-clock sleeps. The aggregate startup and total
shutdown timeouts must be finite; per-operation timeouts may be infinite only
because the aggregate deadline still caps them. `SessionCloseTimeout` is
reserved for the root-session adapter, while the Wave A kernel bounds the
selected interactive runner with `WindowCloseTimeout`.

`ApplicationLifecycleStateMachine` exposes the same legal transition graph for
contract verification and custom composition infrastructure. The application
kernel owns a separate state-machine instance and does not expose mutable
transition control.

Publication remains blocked by the repository's pending license decision.
