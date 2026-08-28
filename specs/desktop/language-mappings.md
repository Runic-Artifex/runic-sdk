# Language mappings

This document is informative about public API shape and normative about the
semantic correspondence. Names may change during M6 and M7; the mapped
behavior must not.

| Contract behavior | .NET mapping | TypeScript+Effect mapping |
| --- | --- | --- |
| Host/surface ownership and shared-listener isolation | Immutable host options, explicit surface handles, keyed ASP.NET routing, and `IAsyncDisposable` ownership | A host `Layer` plus child `Scope`s; surface services are keyed by opaque identity rather than globals |
| Start idempotence and configuration conflict | Async start returns the existing running handle only for equivalent immutable options; otherwise a typed `conflict` failure | Scoped start effect memoized for equivalent configuration; a tagged `Conflict` for changed immutable configuration |
| Close ordering and callback deadlock avoidance | Async close signals linked tokens, awaits leaf resources, and avoids awaiting the initiating callback | Scope finalizers and fiber interruption run leaf-first; a callback fiber never joins itself |
| Window lifetime and platform capabilities | Async handle owned by a surface; unsupported operations return typed capability results | Scoped window service with tagged `Unavailable` results rather than browser globals |
| Request scope, `HEAD`, and response commitment | ASP.NET request scope maps abort and method metadata; response status/headers lock before the first write | Scoped request service; metadata is frozen before a fixed body or `Stream` is run; `HEAD` suppresses the body |
| Streaming and backpressure | Awaited `Stream`, `PipeReader`, or `IAsyncEnumerable<ReadOnlyMemory<byte>>` adapter with bounded writes | `Stream<Uint8Array, DesktopError, Requirements>` with scoped acquisition and bounded demand |
| Request/session cancellation and cause precedence | Linked `CancellationToken` sources record the first contract cause before cancellation | Competing interruption signals resolve through one scoped deferred cause; `AbortSignal` exists only at browser/fetch boundaries |
| Session authentication, origin, and capability admission | Immutable policy records validate canonical origins and credentials before dispatch | Policy service in a `Layer`; tagged denials occur before a handler effect is constructed |
| Frame reassembly, limits, and ordered writes | Pipelines or `ReadOnlySequence<byte>` codec plus a per-direction async send gate | `Uint8Array` codec plus a scoped queue or semaphore preserving per-direction order |
| Reconnect | Transport loss completes pending tasks as `transportClosed`; a new authenticated session handle is created | Connection scope ends and interrupts pending effects; reconnect acquires a fresh session scope before higher-layer resynchronization |
| Invocation correlation and terminal outcome | Opaque correlation value and typed result/exception mapping guarded by one terminal completion | Opaque correlation value and `Effect<Success, DesktopError, Requirements>` guarded by one deferred terminal result |
| Opaque and structured payloads | Opaque memory remains uninterpreted; Desktop envelope schemas use strict source-generated `System.Text.Json` metadata | Opaque `Uint8Array` remains uninterpreted; Desktop envelope schemas use Effect `Schema` with tagged parse failures |
| Path sandbox and bounded policy | Canonical path resolution plus immutable host/surface limits fail before content open or allocation | URL/path normalization and immutable limits fail in the typed error channel before resource acquisition |
| Public errors and redaction | Contract category/code/message/retryability mapped from internal exceptions; diagnostics retain protected causes | Tagged error union exposes the same public fields while protected causes remain in redacted logs/spans |
| Diagnostics | Structured events with opaque identifiers and policy-controlled redaction | Structured logs/spans with the same identifiers and redaction |

## Intended language differences

- .NET exposes `CancellationToken`; TypeScript+Effect exposes interruption and
  scoped fibers. Neither representation crosses the wire.
- .NET may map unexpected failures to exceptions; Effect maps them into the
  typed error channel. Both publish the same contract error category at a
  presentation boundary.
- .NET streams may use framework-native response bodies or pipelines; Effect
  uses `Stream`. Both preserve ordered chunks, bounded demand, cancellation,
  and one terminal outcome.
- Dependency injection uses .NET service scopes and Effect `Layer`/`Scope`
  respectively. Contract scopes remain host, surface, window, session, and
  request—not framework container types.

Future Rust and modern C++ implementations may use ownership, RAII, futures,
coroutines, expected/result types, and native stream abstractions. They are not
v1 supported profiles and receive no package or evidence claims until their own
implementation programs begin.
