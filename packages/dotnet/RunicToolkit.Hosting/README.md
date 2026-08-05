# RunicToolkit.Hosting

`RunicToolkit.Hosting` is the framework-neutral, BCL-only composition and lifecycle
kernel for a single-use RunicToolkit application. It classifies one launch, validates
one immutable composition, selects exactly one mode runner, publishes sanitized
lifecycle events, and performs bounded teardown. It depends only on
`RunicToolkit.Hosting.Abstractions`.

The complete declared surface is recorded in [PUBLIC-API.md](PUBLIC-API.md).

## Composition

`RunicToolkitApplicationBuilder` explicitly registers the neutral host, common and
mode-specific validators, startup participants, mode runners, failure policy,
timeouts, clock, and optional event sink. `Build()` freezes defensive snapshots and
returns a single-use `RunicToolkitApplication`. There is no assembly scanning,
reflection discovery, service locator, or dynamic-code path.

The build snapshot also captures each runner's `Kind` and each participant's `Phase`
in delegating wrappers. Later mutation of those collaborator properties cannot change
route selection or startup order, while execution remains delegated to the originally
registered collaborators.

The builder can build only once. Singleton collaborators can be configured only once,
and a host is required. `ApplicationCompositionDescriptor` exposes the frozen
registrations, route table, validation pipeline, timeout snapshot, clock, and event
sink without exposing mutable builder collections.

Application validation completes before host startup. When a decision enters
`ApplicationCompositionValidator`, its deterministic order is:

1. Preflight the `LaunchDecision` shape: a defined kind, command-name rules, and a
   safe diagnostic for `Invalid` decisions.
2. For a defined non-`Invalid` kind, require exactly one registered runner. An
   `Invalid` decision intentionally requires no runner.
3. Run common validators in registration order.
4. Run validators for the selected launch kind in registration order.

The lifecycle itself treats `LaunchKind.Invalid` as a usage failure before host
startup, so an invalid application launch does not require a registered runner.

All deterministic validation errors are collected. Individual routing-cardinality and
decision-shape errors use `RTKHOST1201` and never contain a consumer type name or launch
argument; the lifecycle aggregates any reported validation errors into its terminal
`RTKHOST1001` configuration failure.

## Launch classification and routing

`DefaultLaunchIntentResolver` classifies only the first root token, using ordinal
matching and no service resolution:

| Arguments | Launch kind |
|---|---|
| none or `--ui` | `UserInterface` |
| `--help` or `-h` | `Help` |
| `--version` | `Version` |
| first token is a non-option | `Command`; remaining tokens belong to that command |
| unknown option, empty command, or a reserved root option with extra tokens | `Invalid` |

`LaunchDecision` snapshots every argument. Safe invalid-launch diagnostics describe
the classification rule but never echo input. `ApplicationModeRouteTable` snapshots
registrations and their `LaunchKind` values, then returns the unique matching runner
or deterministic registration indexes for zero/multiple matches.

This separation is the CLI/UI routing seam: a command launch selects only its command
runner. Concrete command parsing and UI/native activation belong to later adapters.

## Deterministic lifecycle

The lifecycle validates, starts the host, starts participants by phase and registration
order, then runs the selected mode. Only successfully started participants are stopped,
in reverse completion order. Competing stop requests share one
`IApplicationStopController` and one completion task. `DisposeAsync` is idempotent and
initiates stop when execution is active.

The first terminal non-success result or failure remains primary. A teardown failure
may replace success, but never an earlier non-zero result or failure; later cleanup
failures remain available through `SecondaryFailures`. Every bounded wait uses the
injected `TimeProvider`. Aggregate startup and total-shutdown deadlines are finite;
per-operation waits remain capped by those aggregate deadlines.

`ApplicationLifecycleStateMachine` exposes the legal transition graph for contract
verification. The application kernel owns a separate instance and does not expose
mutable transition control.

## Structured lifecycle events

An optional `IApplicationLifecycleEventSink` receives immutable events serialized by
one kernel. Sequences are per-kernel and strictly increasing for delivered events;
timestamps come from the same `TimeProvider` used for lifecycle deadlines. Concurrent
publishers enqueue into one ordered, best-effort drain. Sink callbacks run on the
thread pool outside lifecycle state locks and may finish after lifecycle completion.
Slow, blocking, reentrant, or throwing sinks cannot delay stop signaling, consume a
shutdown deadline, or change lifecycle state, failure precedence, or completion.

The event payloads are deliberately bounded: launch events contain only `LaunchKind`;
failure events contain category, exact `RTKHOST` plus four-digit code when valid,
expected status, and
primary/secondary status; completion events contain the mapped exit code, success
status, and secondary-failure count. Arguments, exception objects/messages, asset
content, and adapter payloads are never included.

| Event ID | Constant | Meaning |
|---:|---|---|
| 11000 | `StateTransition` | A legal state transition completed |
| 11001 | `LaunchSelected` | A launch kind was selected |
| 11002 | `StopRequested` | One stop reason won exact-once selection |
| 11003 | `PrimaryFailure` | A failure became the primary outcome |
| 11004 | `SecondaryFailure` | A later failure was retained |
| 11005 | `Timeout` | A bounded operation timed out |
| 11006 | `Completion` | The stable terminal result was selected |

The Hosting family reserves event range 11000-11999. Registration of this allocation
in the shared contracts registry is an integration handoff; this package does not edit
that shared registry.

## Assets and browser seams

The companion Abstractions package defines validated frontend-asset metadata and
provider contracts plus browser host, window, and dispatcher contracts. These types do
not expose native handles, an HTTP implementation, an MVVM session, or `cs-webui`.
`RunicToolkit.Hosting.Build` constructs deterministic manifests against those asset
contracts; runtime asset serving remains an adapter responsibility.

## Dependency manifest

| Component | Authored/runtime dependency | Runtime role |
|---|---|---|
| `RunicToolkit.Hosting.Abstractions` | .NET BCL only | Frozen contracts and immutable data |
| `RunicToolkit.Hosting` | `RunicToolkit.Hosting.Abstractions` | Composition, routing, validation, lifecycle, events |
| `RunicToolkit.Hosting.GenericHost` | `RunicToolkit.Hosting`, `Microsoft.Extensions.Hosting` | Generic Host lifetime bridge and structured logging sink |
| `RunicToolkit.Hosting.Build` | `RunicToolkit.Hosting.Abstractions` | Deterministic manifest construction; no runtime-project dependency |
| `RunicToolkit.Hosting.Generators` | .NET BCL only | Non-packable dependency-neutral generation contract/diagnostic model; no runtime-project dependency |

All projects target a repository-selected framework. SDK-supplied ILLink build tooling
is not a runtime package dependency. The core kernel does not reference MVVM,
CommandLine, Microsoft.Extensions Hosting, a native runtime, or external lowercase
`cs-webui`; framework integrations remain in inward-depending adapter packages.

## Wave C adapters

`RunicToolkit.Hosting.GenericHost` owns the Generic Host lifetime bridge and structured
logging sink. The other Wave C packages own the concrete MVVM/root-session, WebUi/browser,
CommandLine parser/runner, and runtime asset integrations. Every adapter depends inward
on this kernel and preserves its frozen classification, validation, event,
failure-precedence, timeout, and teardown behavior.

`SessionCloseTimeout` remains reserved for the root-session adapter. This kernel does
not load native UI or frontend assets for command-only execution because it has no
concrete adapter capable of doing so.

The package is MIT licensed. Publication still requires package identity and
release-readiness review.
