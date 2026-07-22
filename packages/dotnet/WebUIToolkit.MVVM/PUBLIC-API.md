# Public API contract — v1 runtime

Status: review baseline for `WebUIToolkit.MVVM` and wire identity
`webuitoolkit.mvvm/1`. This baseline is not a publication grant; ADR 0004 keeps
publication blocked.

## Identity and limits

- `MvvmProtocol` exposes identity and major version constants.
- `MvvmFaultCodes` exposes the closed eight-code v1 catalog and `IsDefined`.
- `MvvmContract`, `MvvmSessionId`, and `MvvmRequestId` are the validated logical
  and UUID identities. Public entry points also reject invalid `default` values.
- `MvvmLimits` exposes v1 hard ceilings and configurable effective values. Its
  default is the normative v1 effective-limit set; `Validate` rejects zero,
  negative, infinite, or above-ceiling settings. Effective limits include UTF-8
  property-name bytes rather than silently falling back to the hard ceiling.
  `MaximumRequestLedgerEntries` is a fixed, non-negotiated lifetime safety cap.
  `MaxShutdownDuration` is a local, non-negotiated cooperative teardown grace;
  its hard ceiling is `MaximumShutdownDuration`.

## Requests, changes, and results

- `MvvmRequest` is externally closed and has four public variants:
  `MvvmMutationRequest`, `MvvmSnapshotRequest`, `MvvmAcknowledgeRequest`, and
  `MvvmCancelRequest`.
- Mutations use `MvvmMutationKind.SetProperty` or `ExecuteCommand`, a positive
  generated numeric member ID, a non-negative base revision, and detached JSON.
- `MvvmPatch` is externally closed and has five lossless v1 variants:
  property, collection range, collection move, command state, and validation.
- `MvvmBindingResult.Success`, `Rejected`, and `CommittedFailure` distinguish
  terminal success from whether observable consumer state committed. Every
  committed outcome advances exactly one revision, including a consumer fault
  completed atomically with its commit.
- `MvvmResponse` exposes the authoritative revision, detached result JSON,
  immutable ordered patches, an optional safe fault, and the cancel result's
  accepted/not-accepted terminal-race value.

## Wire codec and transport boundary

- `MvvmMessageCodec` strictly decodes client-to-host and host-to-client v1 frames,
  rejects invalid UTF-8/JSON/schema/semantics/direction, and deterministically
  encodes validated `MvvmWireMessage` documents under effective limits.
- `MvvmMessageDirection`, `MvvmProtocolException`, and
  `MvvmValidationErrorCodes` expose direction and safe validation outcomes for a
  host adapter. They do not implement a connection state machine.
- The host adapter owns connection-scoped handshake and selected capabilities,
  authenticated view binding, wire-message-to-session routing, its bounded output
  writer, wire close tombstones/idempotence, and reconnect retention/expiry.
- Before forwarding, the host/state adapter must enforce session/view identity and
  every negotiated capability gate. `IMvvmSession.Authorizes` supplies only the
  fixed-time session-token comparison; this runtime does not claim transport-level
  authentication or capability enforcement.

## Binding and projection helpers

- `MvvmBindingMember`, `MvvmBindingVocabulary`, and `MvvmBindingMemberKind` define
  a closed generated property/collection/command vocabulary with one principal
  kind per positive numeric member ID.
- `IMvvmBindingVocabularyProvider` exposes the complete vocabulary used for
  session-level projection validation. Official builder adapters implement it.
- `MvvmBindingAdapterBuilder` binds explicit snapshot, property, command, and
  cleanup delegates into an `IMvvmBindingAdapter` without runtime discovery. Use
  its `(snapshot, vocabulary)` constructor when the snapshot includes read-only
  properties or collections; the compatibility constructor infers only registered
  setters and commands.
- The closed `MvvmProjectionMember` hierarchy models property, collection,
  command, and validation snapshot members. `MvvmProjectionSnapshotBuilder`
  rejects duplicate member-kind/ID pairs and emits deterministic order.
- `MvvmProjectionPatchBuilder` preserves patch transaction order and applies the
  v1 change/item ceilings before producing an immutable result.
- `MvvmValue` creates detached JSON from primitives, an explicit UTF-8 writer,
  or source-generated `JsonTypeInfo<T>` metadata suitable for trimming and AOT.

## Lifetime APIs

- `IMvvmBindingAdapter` is the only generated/manual binding seam. It must return
  a completed committed outcome atomically with every projected state commit. A
  consumer fault after commit uses `CommittedFailure`; cancellation that wins before
  result completion forbids later mutation. A non-cooperative adapter is detached
  after the shutdown grace, its late result is discarded, and the session is
  quarantined rather than waiting indefinitely or starting overlapping cleanup.
- For `IMvvmBindingVocabularyProvider` adapters, the session validates snapshots
  and committed patches before publication. Invalid output produces a sanitized
  `request.invalid`, advances no revision, and poisons the session. A custom
  adapter that omits the provider interface is an explicitly trusted compatibility
  seam whose member kinds cannot be validated by the session.
- `MvvmSessionActivation` records adapter plus disposable resources in creation
  order and defensively copies the resource list.
- `MvvmSessionRegistry.Map` performs explicit reflection-free registration and
  rejects duplicate ordinal contracts. Registration and factory snapshots are
  safe when configuration is assembled concurrently.
- `IMvvmSessionFactory` owns session admission and close; factory disposal drains
  cooperative activation before it completes. An activator or disposer that
  ignores cancellation is detached after `MaxShutdownDuration`; its task remains
  observed, and dependent resources are quarantined rather than disposed while
  consumer code may still use them. Its admission slot is released only after safe
  ordered cleanup completes, so repeated cancellation cannot create unbounded
  activator work.
- `IMvvmSession` owns per-session serialization, capability, revision,
  acknowledgement, cancellation/timeout winner selection, and completion-idempotent
  asynchronous disposal. `Authorizes` supplies the fixed-time capability check a
  transport adapter must perform before forwarding any session-bound invocation.

## Diagnostics

- `MvvmDiagnostics.InstrumentationName` is the stable shared BCL ActivitySource and
  Meter name, `WebUIToolkit.MVVM`.
- `MvvmDiagnostics.SessionOpenActivityName` and `RequestActivityName` expose the
  two stable activity operation names without exposing the instrument instances.
- Runtime metrics cover successful/open/closed sessions, activation failures,
  terminal requests and faults, active sessions and requests, durations, and
  backpressure rejections. Metric dimensions are bounded identifiers only.
- Diagnostics never contain contract/session/request identities, capabilities,
  JSON, projected values, consumer exception data, paths, or stack traces.
  Throwing listeners are contained. A logging adapter can add integration policy
  without becoming a runtime dependency.

The wire specification and language-neutral schema/corpus remain the source of
truth when this runtime API is projected onto a `cs-webui` transport.
