# ReactiveUI 25 support

Runic supports **ReactiveUI 25.0.1**, Binding **8.6.0**, and SourceGenerators
**4.2.0**. The default integration uses `ReactiveUI.Primitives`; applications
using the System.Reactive distribution select
`Runic.Application.ReactiveUI.Reactive`. Select one flavor for an application,
then rebuild the application and its generated clients together when moving
from ReactiveUI 24. The [adapter migration notes](../../../../packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#reactiveui-25)
cover the namespace changes. If an interface-typed generic command leaves the
flavor ambiguous, set `RunicBridgeReactiveUiFlavor=reactive` in the generating
project for the System.Reactive flavor.

ReactiveUI remains the .NET ViewModel API. Runic generates a typed bridge for
selected public state, commands, interactions, and known view content. It does
not turn arbitrary observables, methods, CLR object graphs, or ReactiveUI APIs
into browser APIs.

## Supported contract surface

| ReactiveUI feature                                               | Generated bridge behavior                                                                                                                                                                                                         |
| ---------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ReactiveObject`, `[Reactive]`, OAPH / `ToProperty`              | Public model state is observed through `INotifyPropertyChanged`. The generated property is exported after ReactiveUI's source generator has run; observables themselves are not serialized.                                       |
| `WhenAnyValue`, operators, validation                            | Use them normally in .NET. Export their resulting supported state. `INotifyDataErrorInfo` property errors are published; ReactiveUI.Validation alone is not an exported validation contract.                                      |
| `IReactiveCommand<TInput, TResult>`                              | Discovers interface, base, concrete, and combined command shapes independently of the command factory. Generated input/result codecs determine whether its values are bridgeable.                                                 |
| Plain `ICommand`                                                 | Add `[RunicCommandInput(typeof(TInput))]` to generate a typed argument. It remains synchronous fire-and-snapshot work: no retained result, operation handle, or invented cancellation contract.                                   |
| `CanExecute` and `IsExecuting`                                   | No-input commands publish `canX`; typed ReactiveUI commands also publish `isXExecuting`. Parameterized availability is checked from the decoded input at admission and execution time.                                            |
| `Interaction<TInput, TOutput>` / `IInteraction<TInput, TOutput>` | A getter-only interaction property becomes a typed browser handler surface. Requests use a selected mounted endpoint and preserve normal ReactiveUI .NET-handler/unhandled fallback when no browser handler is eligible.          |
| `IViewFor<T>`, locating views                                    | `ReactiveRunicView<T>` and `ReactiveRunicWindow<T>` implement `IViewFor<T>`. `ReactiveRunicViewLocator` adapts explicit ReactiveUI mappings and contracts.                                                                        |
| `RoutingState`                                                   | `ReactiveRoutedRegion<T>` projects `CurrentViewModel` to observable generated content. Navigation remains .NET code; Runic does not generate URL history or an Avalonia routed host.                                              |
| `WhenActivated`                                                  | Every mounted Runic presentation holds an activation lease. A shared ViewModel remains active until its final presentation unmounts.                                                                                              |
| `Bind`, `BindTo`, `BindCommand`, converters                      | These are still useful for .NET views. They do not bind DOM elements or frontend components; use the generated TypeScript client for that.                                                                                        |
| Schedulers                                                       | `IRunicModelContext` owns short serialized model turns. The default adapter supplies a context-backed `ISequencer`; the System.Reactive flavor supplies an `IScheduler`, without changing ReactiveUI's process-global schedulers. |

## Data contracts

The compiled-model generator builds one closed type graph for state, checked
writes, command input/results, and interaction input/results. It emits direct
C# codecs plus TypeScript types and decoders, so normal bridge serialization
does not need reflection metadata and remains suitable for trimming and Native
AOT.

Supported values include strings, booleans; signed/unsigned 8-, 16-, 32-, and
64-bit integers; `BigInteger`; finite `float`/`double`; `decimal`; enums;
`Guid`; `DateOnly`; `TimeOnly`; `DateTime`; `DateTimeOffset`; `TimeSpan`;
nullable values; public DTO records/classes; arrays; standard read-only/list
interfaces; `ImmutableArray`, `ImmutableList`, `ObservableCollection`, and
`ReadOnlyObservableCollection`; and string-keyed dictionaries.

The wire format is intentionally exact:

| CLR value                        | TypeScript value and wire form                                                                           |
| -------------------------------- | -------------------------------------------------------------------------------------------------------- |
| 64-bit integers and `BigInteger` | `bigint`, encoded as an invariant decimal JSON string                                                    |
| `decimal`                        | string, encoded as an invariant exact decimal string                                                     |
| finite floats                    | `number`; NaN and infinities are rejected                                                                |
| enum                             | declared stable name string                                                                              |
| GUID and date/time values        | validated round-trip string; `DateTime` preserves its CLR kind and `DateTimeOffset` is normalized to UTC |
| `TimeSpan`                       | invariant `c` duration string                                                                            |
| collection                       | snapshot array; collection changes and supported nested DTO/item notifications publish a new snapshot    |

DTO members include inherited application members and stop before framework
base classes. A public constructor must be usable to reconstruct a decoded DTO.
`[RunicIgnore]` excludes a member, `[RunicInclude]` makes an intentional
inclusion explicit, and `[RunicAlias("wireName")]` provides a stable wire name.
Aliases may contain characters such as hyphens. Generated TypeScript quotes the
declaration and uses bracket access, for example `state["exact-id"]`.
Hidden-member and wire-name collisions are diagnostics.

Use `[RunicUnion(typeof(CaseA), typeof(CaseB))]` on an interface or base class
for a closed polymorphic boundary. Each case may use
`[RunicUnionCase("case-name")]`; the wire value has a `$case` discriminator.
Flags enums require an explicit codec. Unsupported values, cycles, open-ended
polymorphism, non-string dictionary keys, and arbitrary `object` graphs are
rejected with a member path rather than falling back to reflection JSON.

For a type outside the built-in graph, implement static
`IRunicBridgeCodec<T>.Read` and `.Write`, then pair
`[RunicBridgeCodec(typeof(MyCodec))]` with a `[RunicCodecShape]` on the codec.
The shape gives the generated TypeScript type, validating decoder expression,
and optional encoder expression (identity is the default); a .NET-only JSON
converter cannot describe a safe frontend contract.

## Typed command operations

Existing ordinary command calls preserve their fire-and-snapshot behavior.
Every discovered ReactiveUI command additionally has an idempotent operation
API. Its input is decoded before the command is admitted, and the same request
ID plus canonical input recovers the original work. Reusing an ID for a
different command or input is rejected.

`RxVoid`/`Unit` commands have no result and complete successfully even when
their observable yields zero values. A non-void command defaults to an
exactly-one result. Mark a property when its observable intentionally has a
different cardinality:

```csharp
[RunicCommandResult(BridgeCommandResultCardinality.Last)]
public IReactiveCommand<SaveRequest, SaveResult> SaveCommand { get; }

[RunicCommandResult(BridgeCommandResultCardinality.Stream)]
public IReactiveCommand<Query, Row> SearchCommand { get; }
```

The generated client exposes `startSave(input)`,
`startSaveWithRequestId(requestId, input)`, and
`recoverSaveWithRequestId(requestId)`. Each returns an operation with
`status()`, `completion`, `wait()`, and `cancel()`. A stream operation also
has `stream(cursor?)`, returning ordered `{ sequence, value }` items and the
next cursor. Results, operation count, and stream replay are bounded. A
delivery error such as `result-too-large`, `result-encoding-failed`, or
`stream-overflow` is visible in the status; it does not make a completed effect
safe to retry. `BridgeOperationUncertainError` means admission/status could
not be observed, so recover that request ID instead of automatically starting
the action again.

Runic cancels the subscription for an individual command execution. Task work
must honor its cancellation token. Continue observing `ThrownExceptions` in
the application: the bridge's rejected/failed reply does not replace
ReactiveUI's command error policy.

Use a plain synchronous command where its fire-and-snapshot contract is the
right fit:

```csharp
[RunicCommandInput(typeof(SaveRequest))]
public ICommand SaveCommand { get; }
```

## Browser interactions

An interaction property is application-owned and must be a public getter with
no public setter. Its browser surface is typed from the same graph:

```ts
const stop = view.interactions.confirmDiscard.handle(
  async (request, { signal }) => showConfirmation(request, signal),
);

// Call on component unmount when `view.dispose()` is not already its owner.
stop();
```

`handle` accepts a synchronous or asynchronous output and returns a disposer.
Replacing a handler, disposing it, or disposing the view aborts its
`AbortSignal`. Requests are delivered by an authenticated pull route to one
already-mounted handler; Runic never broadcasts interaction input in state.
The selected endpoint unmounting, disconnecting, timing out, or cancellation
ends that request. A user answer such as `false` is a normal output and differs
from cancellation or handler failure.

The adapter registers one handler for each Interaction object even if several
views or windows show the same model. It selects the browser only inside the
trusted bridge command/operation scope. Background .NET work has no implicit
"last mounted window" target: use a normal .NET handler or explicitly enter a
`RunicInteractionInvocation` scope with the intended session and route.

## Execution contexts

`RunicModelContext` queues short synchronous reads and mutations. Await I/O or
an interaction outside a turn, then use a later turn to commit state:

```csharp
await context.InvokeAsync(() => viewModel.IsSaving = true, cancellationToken);
var result = await repository.SaveAsync(request, cancellationToken);
await context.InvokeAsync(() => viewModel.Apply(result), cancellationToken);
```

`WindowContentSession` acquires a default context for its root model. Compose
an app-shared graph deliberately with `RunicModelContextRegistry.Bind` or
`.Acquire`; bind its root and independently presented children together. A
second, different context for the same object is rejected. Mounts lease views,
not context ownership. Snapshot delivery is ordered after its model turn so a
synchronous host callback cannot run while the model gate is held.

The default adapter's `RunicReactiveSchedulerProvider.For(context)` produces
an `ISequencer`; the System.Reactive flavor provides the analogous
`IScheduler`. They are delivery tools, not a replacement for the async command
body, host UI dispatcher, browser event loop, or application-wide ReactiveUI
main-thread scheduler.

Create and bind the context before constructing commands, then pass its
scheduler as the positional scheduler argument to the ReactiveUI factory:

```csharp
services.AddScoped<IRunicModelContext, RunicModelContext>();

public EditorViewModel(EditorSession session, IRunicModelContext modelContext)
{
    _scheduler = new RunicReactiveSchedulerProvider().For(modelContext);
    Workspace = new EditorWorkspaceViewModel(session, this, _scheduler);
    _contextLease = RunicModelContextRegistry.Shared.Bind(modelContext, this, Workspace);
}

protected ReactiveCommand<string, RxVoid> CreateCommand(Func<string, Task> work) =>
    ReactiveCommand.CreateFromTask<string>(work, _scheduler);
```

Bind every independently presented child, including dynamically routed document
models, to that same context; keep each lease for as long as it is presented.
Command `Execute` completion is separate from scheduled `IsExecuting` and
`CanExecute` notifications. The context scheduler serializes those
bridge-visible notifications with model turns, so state publication and bridge
replies retain their ordering. A default headless scheduler can notify later;
that is a normal scheduling choice, not a global scheduler setting to change.

## Avalonia comparison

[ReactiveUI.Avalonia 12.1.3](https://github.com/reactiveui/ReactiveUI.Avalonia/tree/v12.1.3/src/ReactiveUI.Avalonia)
also targets ReactiveUI 25. It supplies Avalonia controls, property binding,
visual-tree activation, routed hosts, and dispatcher integration. Runic's
equivalents are logical .NET presentations, acknowledged browser mounts,
explicit content maps, generated frontend contracts, and a host-neutral model
context. It intentionally does not add Avalonia as a dependency or copy its
control/template/converter APIs.

The implementation boundaries are the [type graph](../../../../tools/Runic.Application.Views.Codegen/BridgeTypeGraph.cs),
[operation emitter](../../../../tools/Runic.Application.Views.Codegen/OperationTypeScriptEmitter.cs),
[interaction emitter](../../../../tools/Runic.Application.Views.Codegen/InteractionCodeEmitter.cs),
[bridge runtime](../../../../packages/dotnet/Runic.Application.Views/BridgeRuntime.cs),
and [ReactiveUI adapter](../../../../packages/dotnet/Runic.Application.Views.ReactiveUI/ReactivePresentation.cs).

The ReactiveUI-specific Native AOT fixture exercises the direct generated
codecs and command bridge without warnings. It is focused contract coverage;
it does not claim full host, platform, or frontend-matrix coverage.

Upstream: [ReactiveUI 25.0.0](https://github.com/reactiveui/ReactiveUI/releases/tag/25.0.0),
[25.0.1](https://github.com/reactiveui/ReactiveUI/releases/tag/25.0.1),
and the [Binding migration guide](https://www.reactiveui.net/documentation/reactiveui/upgrading/reactiveui-binding-migration/).
