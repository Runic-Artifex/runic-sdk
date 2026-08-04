# WebUIToolkit.MVVM v1 runtime

`WebUIToolkit.MVVM` is the BCL-only, `net10.0` contract and session kernel for
`webuitoolkit.mvvm/1`. It deliberately does not reference Hosting, Flow, a frontend
framework, an MVVM framework, dependency injection, or the external lowercase
`cs-webui` host. Hosting integrates its transport through these explicit APIs.

The package also owns the C#-first frontend contract attributes.
`WebUiFrontendContract` selects a ViewModel, while
`WebUiFrontendProperty`, `WebUiFrontendCollection`, and
`WebUiFrontendCommand` select explicitly numbered exported members. The
frontend SDK consumes those declarations at compile time and emits a
reflection-free adapter plus the canonical frontend contract.

## Public API shape

- `MvvmSessionRegistry` maps ordinal `MvvmContract` values to closed
  `MvvmSessionActivator` delegates. There is no assembly scanning or reflective
  construction.
- `IMvvmBindingAdapter` is generated or hand-written for one registered ViewModel.
  It owns property/command subscriptions and commits one mutation at a time.
- `MvvmBindingVocabulary` and `MvvmBindingAdapterBuilder` provide a small generated
  vocabulary with explicit numeric property/collection/command members plus
  property-setter and command-execution delegates; runtime discovery and
  reflection-based dispatch are not used. Adapters produced by the builder expose
  their complete vocabulary through `IMvvmBindingVocabularyProvider`.
- `MvvmProjectionSnapshotBuilder` and `MvvmProjectionPatchBuilder` create detached,
  deterministically ordered v1 projections. `MvvmValue` creates detached JSON with
  primitives, a UTF-8 writer, or explicit source-generated `JsonTypeInfo<T>`.
- `IMvvmSession` owns a random 256-bit capability token, the monotonic revision,
  the acknowledgement watermark, pending-request cancellation, timeout, limits,
  and idempotent asynchronous close.
- `IMvvmSessionFactory` owns all sessions it opens and enforces the configured
  session limit.
- `MvvmLimits` supplies lower effective bounds for every configurable v1 runtime
  resource. Values above the protocol ceiling are rejected when the factory is
  built.

## Transport boundary

This package supplies the strict `MvvmMessageCodec`, validated `MvvmWireMessage`
documents, and the local session/binding kernel. The codec rejects malformed or
wrong-direction v1 frames and emits deterministic compact UTF-8, but it is not a
connection host and does not turn wire messages into session calls by itself.

A host transport adapter owns all connection-scoped behavior:

- handshake state, selected protocol version/capabilities, and effective limits;
- authenticated view binding and mapping each `MvvmWireMessage` to its session;
- capability-gating every optional operation before forwarding it to the local
  state adapter or `IMvvmSession`;
- a bounded transport output writer and its backpressure policy;
- wire-level close tombstones and idempotent close replay; and
- reconnect retention, expiry, and snapshot resynchronization policy.

The host must validate session/view/capability identity without disclosing which
credential failed. `IMvvmSession.Authorizes` provides the fixed-time session-token
check, but invoking it and enforcing negotiated capability gates are transport
responsibilities. This package does not claim connection-scoped handshake,
tombstone, output-queue, or reconnect-expiry implementation.

## Observable semantics

1. Requests for one session are serialized. Different sessions have independent
   gates and may execute concurrently.
2. A mutation is admitted only when `BaseRevision == Revision`. A stale mutation
   returns `revision.stale` and never invokes the adapter.
3. Adapter state commit and completion of its `MvvmBindingResult` are atomic. A
   success commits exactly one new revision. Adapter rejection, stale input, and
   pre-commit limit failures commit none. `CommittedFailure` records state committed
   before a consumer fault and advances once with the complete closed patch set.
4. `MvvmSnapshotRequest` is authoritative and does not advance the revision.
   A reconnect requests a snapshot before resuming mutations.
5. A cancellation request bypasses the serialized gate so it can stop the target
   request. One atomic winner selects completion, caller/explicit cancellation,
   timeout, or shutdown; later signals cannot replace that terminal outcome. If
   cancellation wins before result completion, the adapter must not mutate later.
   An adapter that ignores this contract is detached after the configured grace;
   its late result is discarded and the poisoned session stops consumer dispatch.
6. Disposal cancels pending work, then disposes adapter subscriptions and
   activation resources in reverse creation order. Cooperative activation and
   teardown are bounded by `MaxShutdownDuration`; after that grace the runtime
   observes and quarantines late consumer work instead of hanging or concurrently
   disposing resources it may still use. Repeated and concurrent disposal calls
   await the same bounded completion and failure.
7. Unexpected adapter exceptions never cross the boundary. They become the fixed
   sanitized `request.invalid` response without an exception type, payload, path,
   or stack trace.
8. The public patch hierarchy is the closed v1 union: property, collection range,
   collection move, command state, and validation. Runtime payload checks enforce
   the protocol's UTF-8, JSON-depth, array/object, patch, collection, session,
   pending-request, and timeout ceilings.
9. Binding vocabularies resolve both operation and numeric member ID. Snapshot
   members are sorted by member ID and kind; patch builders preserve transaction
   order and reject changes beyond v1 hard ceilings before producing a result.
10. A live session retains admitted request IDs for exact-once replay rejection.
    The fixed, non-negotiated lifetime cap is exposed as
    `MvvmLimits.MaximumRequestLedgerEntries`; reaching it closes the session rather
    than evicting IDs and making a replay admissible.

`MvvmLimits.MaxShutdownDuration` is a local runtime safety bound, not a negotiated
wire limit. It defaults to 30 seconds and cannot exceed the five-minute runtime
ceiling. Configure a shorter value for hosts with tighter shutdown budgets. A
detached activation keeps its factory admission slot until activation and ordered
cleanup finish; a permanently stuck activation therefore consumes one of the
bounded `MaxSessions` slots instead of allowing unbounded quarantined work.

## Vocabulary enforcement

When an adapter implements `IMvvmBindingVocabularyProvider`, the session validates
every snapshot and committed patch against its principal member kinds before
publication. Invalid provider output is rejected as `request.invalid`, advances no
revision, and poisons the session so no later consumer dispatch can publish an
inconsistent projection. The official `MvvmBindingAdapterBuilder` always produces
a vocabulary provider.

Use `new MvvmBindingAdapterBuilder(snapshot, vocabulary)` when the authoritative
snapshot contains read-only properties or collections in addition to members with
mutation handlers. The one-argument compatibility constructor can infer only the
property setters and commands registered on that builder. A custom
`IMvvmBindingAdapter` that does not implement `IMvvmBindingVocabularyProvider`
remains an explicitly trusted compatibility seam: normal payload/count limits
still apply, but the session cannot validate its principal member kinds.

## First-party G3 consumption boundary

The core-owned G3 matrix under `protocol/mvvm/g3/` references existing v1 corpus
cases for CommunityToolkit, compiled HTMX, Hosting, and Flow. It adds no
framework-specific protocol vocabulary. Binding-adapter owners must use the
complete-vocabulary builder overload, explicit delegates, `OnDispose` for owned
subscriptions, and a completed `MvvmBindingResult` that represents one atomic
commit. Transport owners retain handshake, routing, capability, output-bound,
tombstone, and reconnect-retention policy; those concerns do not move into this
package. Flow consumes the handoff declaratively and remains independent of
CommunityToolkit runtime code and Hosting.

## Diagnostics

The runtime emits BCL `ActivitySource` and `Meter` instrumentation under the stable
name exposed by `MvvmDiagnostics.InstrumentationName` (`WebUIToolkit.MVVM`). This
works directly with `ActivityListener` and `MeterListener`; OpenTelemetry bridges
can subscribe by the same name without a package dependency.

- Activities: `mvvm.session.open` and `mvvm.request.dispatch`.
- Counters: `mvvm.sessions.opened`, `mvvm.sessions.closed`,
  `mvvm.session.open.failures`, `mvvm.requests`, `mvvm.request.faults`, and
  `mvvm.backpressure.rejections`.
- Current-load instruments: `mvvm.sessions.active` and `mvvm.requests.active`.
- Histograms: `mvvm.session.open.duration` and `mvvm.request.duration`, both in
  seconds.

The only dimensions are the closed, low-cardinality `mvvm.request.kind`,
`mvvm.outcome`, `mvvm.fault.code`, and `mvvm.limit` tags. Contract, session, and
request identities, capability tokens, JSON payloads, member values, consumer
exception types/messages, filesystem paths, and stack traces are never emitted.
Throwing diagnostic listeners and exporters are isolated and cannot change session
outcomes or teardown.

The versioned wire specification, schemas, and cross-language corpus live under
`protocol/mvvm/`. The package has no runtime package dependencies and supports
trimming and Native AOT. The package is MIT licensed; publication still requires
package identity and release-readiness review.
