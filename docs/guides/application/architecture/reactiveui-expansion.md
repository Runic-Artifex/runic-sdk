# Reactive application contracts

This document records the implemented architecture behind Runic's ReactiveUI
25 bridge. It is an internal contract design, not a second public wire
specification. Application authors normally work with generated C# attachments
and TypeScript clients; the permanent routes described here are transport
details.

## One generated type graph

The compiled-model generator creates a closed `BridgeTypeGraph` for every
exported data boundary. The same graph drives all of these outputs:

- direct C# JSON readers and writers;
- TypeScript types and decoders;
- structural equality for checked field writes;
- contract fingerprint inputs; and
- command and interaction input/output validation.

This keeps property state, command payloads, and interaction payloads aligned.
There is no reflection-based JSON fallback at runtime. Discovery names the
unsupported member path, which makes contracts reviewable before an
application ships.

The graph accepts a deliberately finite set of CLR shapes: scalar primitives,
exact numeric and date/time forms, public DTOs, supported collections,
string-key dictionaries, explicit closed unions, and custom codecs. It rejects
cycles, arbitrary `object` values, non-string dictionary keys, flags enums
without a codec, and open polymorphism. A ViewModel remains a presentation
reference rather than becoming a recursive DTO.

Data subscriptions follow supported nested DTO members and collection items,
disposing removed/replaced subscriptions before publishing the next complete
snapshot. This is observation, not a deep browser patch protocol.
`ObservableCollection` changes provide their own collection events. Plain
`List<T>` and `Dictionary<string, TValue>` remain valid snapshot values, but
their in-place mutations need an owning `PropertyChanged` notification or a
replacement value to become visible.

The codec is canonical: 64-bit and arbitrary integers are decimal strings,
decimals are exact strings, floats must be finite, and dictionaries are written
in ordinal key order. This canonical value is also the input digest for
idempotent operations, so JSON property order cannot turn a retry into a
different request.

## Command operations

Runic separates an ordinary bridge command call from a retained operation.
The former keeps the existing fire-and-snapshot behavior. The latter is the
generated recovery API for ReactiveUI commands and has a request ID:

```ts
const operation = await editor.startSaveWithRequestId(id, request);
const outcome = await operation.completion;
if (outcome.kind === 'succeeded') use(outcome.result);
```

`BridgeOperationRequest` binds that ID to the generated contract, command
member, and canonical input digest. The registry checks for a prior matching
request before availability, so a retry observes accepted work after
`CanExecute` has since changed. A reused ID with another member or input is an
identity conflict.

Operations retain bounded terminal data. Status includes success, failed,
cancelled, expired, or unknown; result delivery failures are separate from the
execution result. Waiters can stop waiting without cancelling work. Explicit
`cancel()` requests the work token, while a later successful completion remains
successful. Closing a session stops admission, asks its owned operations to
cancel, then drains them.

Generated handles attach their command member to every recovery, status, wait,
cancel, and stream request. A request ID is therefore not an accidental shared
namespace for unrelated commands. Scalar results and terminal stream replay
share a 256 KiB window budget. Completing a stream evicts older terminal
operations until its replay fits; a replay larger than the budget is cleared
and reports `stream-retention-too-large`. Running streams separately reserve
their declared maximum against a 256 KiB budget before execution; an admission
that cannot fit is rejected with `stream-capacity`. A default-size stream
therefore occupies that running budget until it completes. Handle `completion`
is lazy: the first read or `wait()` begins one cached terminal observation.

Result cardinality is explicit. `RxVoid`/`Unit` has no result; non-void
ReactiveUI commands require exactly one value by default. The
`[RunicCommandResult]` attribute opts into `Last` or bounded `Stream`. A stream
stores sequenced, cursor-readable values with a bounded replay buffer and
reports overflow. Combined ReactiveUI commands produce a collection as their
single command result unless their property is explicitly marked as a stream.
No-result commands use completion semantics and may publish zero internal
values without turning a successful effect into a cardinality failure.

`[RunicCommandInput(typeof(T))]` supplies the typed payload contract for a
plain synchronous `ICommand`. It generates only fire-and-snapshot execution;
there is no operation result, stream, or cancellation shape to infer from that
interface.

The permanent status, wait, cancel, and stream routes validate contract and
request identity. Generated clients turn an unobservable admission or status
into `BridgeOperationUncertainError`; callers recover the same request ID and
must not casually retry an effect.

## Model execution and delivery

`IRunicModelContext` owns short serialized reads and mutations for a mutable
model graph. `RunicModelContext` provides the default queue. A synchronous
bridge route enters a synchronous turn only to decode, inspect state, or change
state. It never holds that turn across a task, I/O operation, or interaction.

```text
turn: capture input / change local state
await: storage, network, or interaction
turn: apply the result / take the next snapshot
```

`RunicModelContextRegistry` maps reference identities to that owner. A window
root receives an acquired default context. Composition roots can bind an
application-owned context to an application-shared root and its independently
presented children. A model cannot silently move to another context; conflicting
claims fail. Presentation lifetimes do not dispose a shared context.

Both model context and context-backed scheduler queues capture
`ExecutionContext` for each individual item rather than for a whole queue
drain. A trusted interaction invocation therefore follows its deferred work
without becoming ambient state for unrelated queued work; suppressed flow stays
suppressed.

Snapshots are captured in the model turn and then delivered through an ordered
outbound queue. This avoids re-entering model mutation while a native transport
is synchronously publishing JavaScript. Full snapshots can coalesce at the
delivery boundary; operation terminals and interaction requests do not use the
broadcast channel.

The ReactiveUI adapters expose a context-backed scheduler provider. The default
flavor returns `ReactiveUI.Primitives.Concurrency.ISequencer`; the System.Reactive
flavor returns `System.Reactive.Concurrency.IScheduler`. Neither modifies a
process-wide main-thread scheduler. Native UI dispatch and browser rendering
remain owned by their hosts.

`services.AddRunicReactiveModelContext()` is the DI setup for either selected
adapter. It uses `TryAdd` for the scoped `IRunicModelContext`, singleton
scheduler provider, and selected flavor's scoped scheduler, preserving
application overrides. The default context is drained by asynchronous scope
disposal. Application composition still binds each root and independently
presented child through `RunicModelContextRegistry`.

## Interaction ownership and delivery

ReactiveUI interactions are recognized separately from state. A generated
`BridgeInteractionDescriptor<TModel>` attaches one `RegisterHandler` lease for
each Interaction object and shares it across all presentations of that model.
Each `WindowContentSession` owns only its route registration, mounted endpoint
leases, pending requests, and receipts.

When a command or operation calls `Interaction.Handle`, the bridge enters a
trusted `RunicInteractionInvocation` scope. The adapter uses the scope's
session, route, client, and authenticated connection identity to choose an
eligible browser endpoint. It never chooses the most recently mounted view.
Without a current scope or active matching endpoint, the Runic handler returns
without output, so ReactiveUI continues with application-installed .NET
handlers or its usual unhandled behavior.

The selected browser endpoint waits on `__runicInteractionWait`; its own
authenticated request receives the prompt and it replies through
`__runicInteractionReply`. A reply is accepted only if its request ID,
contract, route, presentation, connection, handler generation, and output
codec match the pending request. Duplicate equal replies return the original
receipt; stale or conflicting replies cannot complete another request.

Capability registration is separate from the request pull. An active handler
therefore stays eligible between polls, and each selected presentation has a
bounded pending queue. A request that is no longer eligible for that
presentation is cancelled on delivery; Runic does not reassign it to another
window.

Unmounting, connection loss, scope cancellation, timeout, and session close
terminate a selected pending interaction. The generated TypeScript handler gets
an `AbortSignal` for the same lifecycle. A boolean rejection is ordinary
application output, while cancellation and browser-handler failure remain
distinct outcomes. Browser prompts are never published in a state broadcast.

Background code does not acquire a browser target by accident. It either uses
a .NET handler or deliberately supplies a `RunicInteractionInvocation` with a
chosen `WindowContentSession` and route. ReactiveUI's `Handle(input)` itself
does not take a cancellation token; bridge-scoped calls receive the operation
or command token through the invocation scope.

## Presentation and adapter boundaries

The core package has no ReactiveUI dependency. It owns type codecs, model
contexts, operation retention, session routing, and transport validation. The
optional default and System.Reactive adapters own ReactiveUI command execution,
interaction attachment, view location, activation, and scheduler adaptation.

Each `ReactiveRunicView<T>` or `ReactiveRunicWindow<T>` gets a separate mount
activation lease. The ViewModel's `IActivatableViewModel.Activator` remains
active until the final lease releases it. `ReactiveRoutedRegion<T>` maps
`RoutingState.CurrentViewModel` into generated content. This gives Runic the
same useful activation and routing boundary as a native integration while the
frontend retains responsibility for visual layout and DOM binding.

The two ReactiveUI flavors expose different namespaces and unit/scheduler
types. An application selects exactly one adapter. The generated model tool
uses the property contract to choose an adapter; when an interface-typed
contract is ambiguous, set `RunicBridgeReactiveUiFlavor=reactive` for the
System.Reactive flavor.

The exact generated codec/command path is covered by a ReactiveUI Native AOT
fixture with no warnings. That verifies the bridge contract under AOT; it does
not represent every native host or frontend combination.

The nearest source references are the [type graph](../../../../tools/Runic.Application.Views.Codegen/BridgeTypeGraph.cs),
[operation runtime](../../../../packages/dotnet/Runic.Application.Views/BridgeOperationRegistry.cs),
[interaction router](../../../../packages/dotnet/Runic.Application.Views/BridgeInteractionRouter.cs),
[model context](../../../../packages/dotnet/Runic.Application.Views/RunicModelContext.cs),
and [default adapter](../../../../packages/dotnet/Runic.Application.Views.ReactiveUI/ReactiveInteractionDescriptor.cs).
