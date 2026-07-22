# Public API contract — Wave A

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
  negative, infinite, or above-ceiling settings.

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
  committed outcome advances exactly one revision, including a winning fault.
- `MvvmResponse` exposes the authoritative revision, detached result JSON,
  immutable ordered patches, an optional safe fault, and the cancel result's
  accepted/not-accepted terminal-race value.

## Lifetime APIs

- `IMvvmBindingAdapter` is the only generated/manual binding seam. It must return
  a committed outcome for every mutation that changed projected state, even when
  consumer code then faults or observes cancellation. It must also quiesce after
  cancellation: the runtime records the deadline winner immediately but waits for
  the adapter before terminal publication to preserve serialization and patches.
- `MvvmSessionActivation` records adapter plus disposable resources in creation
  order and defensively copies the resource list.
- `MvvmSessionRegistry.Map` performs explicit reflection-free registration and
  rejects duplicate ordinal contracts.
- `IMvvmSessionFactory` owns session admission and close; factory disposal drains
  concurrent activation before it completes.
- `IMvvmSession` owns per-session serialization, capability, revision,
  acknowledgement, cancellation/timeout winner selection, and completion-idempotent
  asynchronous disposal. `Authorizes` supplies the fixed-time capability check a
  transport adapter must perform before forwarding any session-bound invocation.

The wire specification and language-neutral schema/corpus remain the source of
truth when this runtime API is projected onto a `cs-webui` transport.
