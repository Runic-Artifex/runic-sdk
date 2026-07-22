# WebUIToolkit.MVVM runtime kernel

`WebUIToolkit.MVVM` is the BCL-only, `net10.0` contract and session kernel for
`webuitoolkit.mvvm/1`. It deliberately does not reference Hosting, Flow, a frontend
framework, an MVVM framework, dependency injection, or the external lowercase
`cs-webui` host. Hosting integrates its transport through these explicit APIs.

## Public API shape

- `MvvmSessionRegistry` maps ordinal `MvvmContract` values to closed
  `MvvmSessionActivator` delegates. There is no assembly scanning or reflective
  construction.
- `IMvvmBindingAdapter` is generated or hand-written for one registered ViewModel.
  It owns property/command subscriptions and commits one mutation at a time.
- `IMvvmSession` owns a random 256-bit capability token, the monotonic revision,
  the acknowledgement watermark, pending-request cancellation, timeout, limits,
  and idempotent asynchronous close.
- `IMvvmSessionFactory` owns all sessions it opens and enforces the configured
  session limit.

## Observable semantics

1. Requests for one session are serialized. Different sessions have independent
   gates and may execute concurrently.
2. A mutation is admitted only when `BaseRevision == Revision`. A stale mutation
   returns `revision.stale` and never invokes the adapter.
3. An adapter success commits exactly one new revision. Adapter rejection,
   stale input, and pre-commit limit failures commit none. `CommittedFailure`
   records changed state even when cancellation, timeout, or a safe fault wins;
   it advances once and carries the closed patch set with that terminal fault.
4. `MvvmSnapshotRequest` is authoritative and does not advance the revision.
   A reconnect requests a snapshot before resuming mutations.
5. A cancellation request bypasses the serialized gate so it can stop the target
   request. One atomic winner selects completion, caller/explicit cancellation,
   timeout, or shutdown; later signals cannot replace that terminal outcome.
   Terminal publication still waits for the adapter to quiesce so consumer code
   never overlaps and any committed patches precede its fault. Generated adapters
   must therefore propagate cancellation into bounded consumer operations; .NET
   cannot safely preempt arbitrary code that ignores its cancellation token.
6. Disposal cancels pending work, waits for the active dispatch, disposes adapter
   subscriptions, then activation resources in reverse creation order. Repeated
   and concurrent close/disposal calls await the same completion and failure.
7. Unexpected adapter exceptions never cross the boundary. They become the fixed
   sanitized `request.invalid` response without an exception type, payload, path,
   or stack trace.
8. The public patch hierarchy is the closed v1 union: property, collection range,
   collection move, command state, and validation. Runtime payload checks enforce
   the protocol's UTF-8, JSON-depth, array/object, patch, collection, session,
   pending-request, and timeout ceilings.

The versioned wire specification, schemas, and cross-language corpus live under
`protocol/mvvm/`. Package publication remains blocked by ADR 0004.
