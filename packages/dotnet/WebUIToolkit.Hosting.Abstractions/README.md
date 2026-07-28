# WebUIToolkit.Hosting.Abstractions

`WebUIToolkit.Hosting.Abstractions` contains the dependency-neutral vocabulary shared
by the Hosting lifecycle, composition kernel, build tooling, and adapters. The
assembly targets the repository's `net10.0` policy and references only the BCL plus
the frontend-neutral `WebUIToolkit.Desktop` contracts. It does not reference MVVM,
CommandLine, Microsoft.Extensions Hosting/DI/logging, a native runtime, or external
lowercase `cs-webui`.

The complete declared surface is recorded in [PUBLIC-API.md](PUBLIC-API.md).

## Contract groups

- Launch and routing: `LaunchDecision`, `ILaunchIntentResolver`, mode runners, route
  selection, and stable runner-cardinality errors.
- Validation and lifecycle: immutable validation inputs/errors, host and participant
  seams, stop convergence, states, phases, timeout options, stable failures/results,
  and exit-code policy.
- Assets: normalized manifest-relative metadata, deterministic manifests, and a
  manifest/validate/open-only provider seam.
- Browser hosting: validated host/window options, factory/host/window lifetime seams,
  close signaling, dispatcher-affine asynchronous work, and an optional desktop
  adapter seam without native handles.
- Observability: ordered sanitized lifecycle events and a non-owning sink boundary;
  the kernel queues delivery so sink latency and failures are isolated from lifecycle work.

All concrete implementations must preserve explicit registration. These contracts do
not authorize runtime assembly discovery or dynamic activation.

## Deterministic and security guarantees

Contract data snapshots consumer-owned collections where ownership crosses into
Hosting. Asset paths are normalized to forward-slash application-relative paths and
reject rooted, drive-qualified, empty, current-directory, parent-directory, query,
fragment, colon, control-character, encoded-separator, and encoded-traversal forms.
Media types reject control characters. SHA-256 values are exactly 64 hexadecimal
characters and normalize to lowercase. Compressed variants must be distinct from their
source and from each other.

Browser application/window identifiers accept only ASCII letters, digits, periods,
hyphens, and underscores. Window titles reject empty values and control characters;
window dimensions must be positive. Browser interfaces expose neither a native handle
nor an implementation-specific runtime type. Close callbacks are signaling boundaries,
not application-logic execution contexts.

Stable validation/failure messages and lifecycle events do not echo launch arguments,
exception messages, asset content, native payloads, or authorization data.

## Diagnostic allocation

The Hosting family owns `WUTHOST0001`-`WUTHOST9999`. Wave A/B allocates the following
exact identities; shared registry changes remain an orchestrator handoff:

| Identity | Meaning |
|---|---|
| `WUTHOST0001` | Missing or ambiguous WebUi runtime adapter (error) |
| `WUTHOST0002` | Missing UI root view or session (error) |
| `WUTHOST0003` | Duplicate command or launch token (error) |
| `WUTHOST0004` | Inaccessible generated factory target (error) |
| `WUTHOST0005` | Reflection fallback in an AOT application (warning) |
| `WUTHOST0006` | Missing or ambiguous frontend entry point (error) |
| `WUTHOST0007` | Async lifecycle callback without cancellation (warning) |
| `WUTHOST1001` | Validation or invalid-launch failure |
| `WUTHOST1101` | Host start failure |
| `WUTHOST1102` | Participant start failure |
| `WUTHOST1103` | Startup timeout |
| `WUTHOST1201` | Mode-runner selection failure |
| `WUTHOST1202` | Mode-runner execution failure |
| `WUTHOST1301` | External cancellation |
| `WUTHOST1401` | Participant stop failure |
| `WUTHOST1402` | Teardown operation timeout |
| `WUTHOST1403` | Host stop failure |
| `WUTHOST1404` | Host disposal failure |
| `WUTHOST1405` | Total shutdown timeout |

Exceptions may be retained in-process on `ApplicationFailure`, but their messages are
not promoted to stable diagnostics or lifecycle events.

## Lifecycle event allocation

The Hosting family reserves event IDs 11000-11999. Wave B allocates:

| Identity | Event type |
|---:|---|
| 11000 | `ApplicationStateTransitionEvent` |
| 11001 | `ApplicationLaunchEvent` |
| 11002 | `ApplicationStopRequestedEvent` |
| 11003 | Primary `ApplicationFailureEvent` |
| 11004 | Secondary `ApplicationFailureEvent` |
| 11005 | `ApplicationTimeoutEvent` |
| 11006 | `ApplicationCompletionEvent` |

Sequences and timestamps are assigned by the Hosting kernel, not event constructors.
Consumers may construct the immutable records for testing, but must not infer global
ordering across application instances from their per-kernel sequence values. A failure
event retains only an exact `WUTHOST` plus four-ASCII-digit code; foreign or malformed
codes become `null`.

## Frozen lifecycle decisions

- One immutable lifecycle selects exactly one `LaunchKind` and mode runner.
- Validation completes before host startup. Composition validation checks decision
  shape, non-`Invalid` route cardinality, common validators, then selected-mode
  validators; an invalid launch requires no runner.
- Startup is phase-then-registration order. Only completed participants stop, in
  reverse completion order.
- The first terminal non-success result or failure remains primary. A later teardown
  failure can replace success, but cannot replace an earlier non-zero result/failure.
- Competing stop sources converge on one request and completion; disposal is
  idempotent.
- Every bounded wait and event timestamp uses an injected `TimeProvider`; aggregate
  startup and shutdown deadlines cap individual waits.

## Dependency manifest and Wave C boundary

`WebUIToolkit.Hosting.Abstractions` depends only on the authored
`WebUIToolkit.Desktop` contract package. Its remaining shipping-project lock entries
are SDK-supplied ILLink build tooling. The runtime kernel and deterministic manifest
builder depend inward on it; generator/build tooling is not a runtime dependency of
this assembly.

Wave C supplies the Generic Host, MVVM/root-session, WebUi/external `cs-webui`,
CommandLine, structured logging, and runtime asset-provider implementations. No
concrete adapter type belongs in this package, and lower-level MVVM/CommandLine/native
packages must not reference Hosting abstractions merely to participate in Hosting;
their adapter packages translate into these seams.
