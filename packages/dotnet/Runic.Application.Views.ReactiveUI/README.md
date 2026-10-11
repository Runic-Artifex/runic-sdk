# ReactiveUI integration for Runic Views

`Runic.Application.Views.ReactiveUI` is the default ReactiveUI 26 adapter for Runic
Views. It references ReactiveUI's `ReactiveUI.Primitives` flavor and has no
CommunityToolkit dependency. It provides:

- model-context scheduling: `AddRunicReactiveModelContext()` makes commands
  created without a scheduler deliver on the Window's model context (see
  [Model-context scheduling](#model-context-scheduling));
- `ReactiveRunicView<T>`, which implements `IViewFor<T>` over a typed Runic
  `DataContext`. Windows derive from `RunicWindow<T>` in
  `Runic.Application.Views` with ReactiveUI ViewModels too: the
  `ReactiveRunicWindow<T>` base was removed in 0.7.0-preview.7, so the same Window
  class opens on every host with either MVVM library;
- `ReactiveRunicViewLocator`, adapting explicit ReactiveUI view mappings and
  contracts;
- `ReactiveRoutedRegion<T>`, projecting `RoutingState.CurrentViewModel` into
  a generated content property, logging an incompatible route or failed
  router through an optional `ILoggerFactory` (Views events 1040-1041);
- mount-owned `IActivatableViewModel` leases; and
- typed command and interaction adapters used by
  the compiled-model generator.

One browser presentation gets one logical Runic View and activation lease. A
shared ViewModel stays activated while any presentation remains mounted.
`WindowContentSession.AttachPresentation` creates those logical views for
generated content routes. The [Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-reactive-views/README.md)
example covers multiple views over one ViewModel, routed content, explicit view
contracts, and activation lifetimes.
The `runic-app` project template uses ReactiveUI ViewModels with this adapter by
default (`dotnet new runic-app`; `--view-models toolkit` selects CommunityToolkit.Mvvm).

## Primitives, not System.Reactive

ReactiveUI 26 has two flavours. This package targets the default one, built on
`ReactiveUI.Primitives`. If you know ReactiveUI from System.Reactive:

- `RxVoid` replaces `System.Reactive.Unit`: a command without input or output is
  a `ReactiveCommand<RxVoid, RxVoid>`.
- `ISequencer` replaces `IScheduler`. `RxSchedulers.MainThreadScheduler`,
  formerly `RxApp.MainThreadScheduler`, is an `ISequencer`.
- Awaiting a command needs `using ReactiveUI.Primitives.Signals;`, which
  provides the awaiter. Without it, `await command.Execute()` fails with CS1061.
- `command.Execute().ToTask()` from the `ReactiveUI.Primitives` namespace also
  works and returns the last value. Both throw `InvalidOperationException` when
  the command completes without a value, which a `CreateFromObservable` command
  can do; `Create` and `CreateFromTask` commands always produce one.

Both await styles resume on the model context when they start in a turn. For
System.Reactive, use [`Runic.Application.Views.ReactiveUI.Reactive`](#systemreactive-flavor).

## ReactiveUI 26

This package targets ReactiveUI **26.0.1**, Binding **9.1.0**, Primitives
**9.0.0**, and SourceGenerators **4.2.0**. ReactiveUI 26 and Binding 9 keep the
25/8.x public API; they are majors because their .NET 11 assemblies no longer
use runtime-async, so they also work on Mono. Runic's adapter API and generated
contracts are unchanged; rebuild the app against the 26 packages.

Primitives 9 changes `SubscribeSafe`: a single lambda is now the `onNext`
handler. Rename error-only calls, such as `source.SubscribeSafe(ex => Log(ex))`,
to `SubscribeSafeErrors`. Binding 9 also makes `BindCommandUnsafe` and
`BindInteractionUnsafe` dispose the binding they replace when the control chain
changes.

When moving from ReactiveUI 24, note that ReactiveUI 25 moved `IViewFor<T>` and
`IViewLocator` to `ReactiveUI.Binding`, so upgrade the Runic adapter, app, and
generated web client together. An adapter binary compiled against ReactiveUI
24 can fail at runtime against ReactiveUI 25 or later even if a dependency
override compiles.

ReactiveUI now includes shared source generators. Remove old explicit generator
pins or update them to 4.2.0 or later. The SDK centrally pins 4.2.0; the same
generator package supports the System.Reactive flavor, so there is no separate
`ReactiveUI.Reactive.SourceGenerators` package.

Create explicit mappings with the Binding locator:

```csharp
var locator = new DefaultViewLocator();
locator.CreateMappingBuilder()
    .Map<EditorViewModel>(() => new EditorView())
    .Map<EditorViewModel>(() => new CompactEditorView(), "compact");
```

For global lookup, replace `ReactiveUI.ViewLocator.Current` with
`ReactiveUI.Binding.ViewLocator.GetCurrent()`.

## Read-only and settable properties

As with any ReactiveUI View, the property's setter decides what the web
frontend may set. A public setter gets a `set<Property>` client method; give
status a private setter so it stays read-only to every View:

```csharp
[Reactive]
public partial string Title { get; set; } = "";       // client: setTitle

[Reactive]
public partial bool IsDirty { get; private set; }     // read-only: no setIsDirty
```

A hand-written property with `private set => this.RaiseAndSetIfChanged(ref _isDirty, value)`
works the same way. See
[read-only and settable state](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application.Views#read-only-and-settable-state).

## Generated ReactiveUI contracts

Public `IReactiveCommand<TInput, TResult>` properties are inspected through
their interface contract, so `ReactiveCommand`, a base type, and a combined
command use the same generated input/result bridge. `RxVoid` is a no-result
operation and can complete with zero observable values; a non-void result must
produce exactly one observable value unless the property opts into `Last` or
`Stream` with `[RunicCommandResult(...)]`.
Generated TypeScript exposes operation start, request-ID recovery, status,
cancellation, retained results, and cursor reads for streams.

Use `[RunicCommandInput(typeof(TInput))]` on a plain `ICommand` property to
generate a typed argument. That command remains synchronous fire-and-snapshot
work and has no generated operation result or cancellation handle.

Public getter-only `Interaction<TInput, TOutput>` properties become typed
browser surfaces. The generated client uses this shape:

```ts
const unregister = view.interactions.confirmDiscard.handle(
  async (request, { signal }) => confirmInFrontend(request, signal),
);
```

The disposer, replacement handler, view disposal, unmount, and disconnect
abort the handler signal as appropriate. Requests go only to a selected mounted
endpoint through a pull route. With no eligible browser handler, the adapter
does not consume the interaction, preserving normal ReactiveUI .NET handler
precedence and unhandled behavior.

## Model-context scheduling

Call `AddRunicReactiveModelContext()` (namespace
`Runic.Application.Views.ReactiveUI`) when you register the ViewModels, and
create commands without a scheduler:

```csharp
using Runic.Application.Views.ReactiveUI;

services.AddRunicReactiveModelContext();

public CounterViewModel() =>
    IncrementCommand = ReactiveCommand.Create(() => { Count += Step; });
```

The extension installs a model-context main-thread scheduler as
`RxSchedulers.MainThreadScheduler`, the scheduler a `ReactiveCommand` uses when
none is passed. Work scheduled inside a turn of a `RunicModelContext` runs on
that context, so a bridged command delivers its results and `IsExecuting` on
the model context of the Window that runs it, ordered with Bridge replies and
state publication. `CanExecute` follows its source synchronously, in the turn
that changes the state it observes. Routing follows the running turn, so two
Windows never share notifications. Work scheduled outside a turn, for example
from a timer thread or after `ConfigureAwait(false)`, runs on the scheduler the
extension replaced, ReactiveUI's task pool; Runic logs that once as
`ReactiveSchedulerOutsideModelTurn` (Views event 1043, Warning). Pass an
`ILoggerFactory` to `RunicReactiveSchedulerProvider.InstallMainThreadScheduler`
to log it there; otherwise it goes to `Trace`.

The extension replaces only ReactiveUI's default schedulers. A scheduler that an
application or UI platform set, such as WPF's dispatcher or a test scheduler, is
kept, and `InstallMainThreadScheduler()` returns `false`. To opt out, set
`RxSchedulers.MainThreadScheduler`, or call ReactiveUI's builder
`WithMainThreadScheduler`, before `AddRunicReactiveModelContext()`, or replace it
afterwards. Commands read the scheduler when they are created, so register the
model context before the ViewModels create their commands.

The scheduler only delivers the command's notifications. The task body runs in
the context because the Bridge starts it in a turn. Set ViewModel state directly
in a `CreateFromTask` body, also after `await`, as with any MVVM library. Use
`InvokeAsync` only for code that left the context, for example after
`ConfigureAwait(false)` or in `Task.Run`. A bridged execution carries its turn's
context to the command's notifications, so a `CreateFromTask(async
cancellationToken => ...)` command, which ReactiveUI completes outside the turn,
and a body whose last `await` uses `ConfigureAwait(false)` still deliver on the
Window's context. That does not apply when a ViewModel calls
`command.Execute().Subscribe(...)` itself: such a command delivers in the turn
only if its body resumes in the turn and it does not take a cancellation token.
Otherwise its notifications run on the fallback scheduler. The
[threading rule](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#threading-state-after-await)
has the details.

For an explicit scheduler, for example an `ObserveOn` in a DynamicData
pipeline, inject `ISequencer` or call `RunicReactiveSchedulerProvider.For(context)`.
Both return the context's own sequencer, one per context, which the main-thread
scheduler also uses. The extension registers, with `TryAdd`, the scoped
`IRunicModelContext`, the singleton `IRunicReactiveSchedulerProvider` and a
transient `ISequencer` for the resolved context, preserving custom application
registrations. Dispose a scope asynchronously to drain its default owned
context. Each queued item captures its own `ExecutionContext`, so a trusted
interaction scope follows its own deferred work without leaking to another
queued operation.

The ViewModels need no `RunicModelContextRegistry.Bind`. The CS-WebUI, Desktop
and WPF hosts give the window the scope's context and bind the root ViewModel
to it. The window binds each child ViewModel it presents, including one a
ViewModel creates later, for as long as it presents it, and a navigator binds
the region entries it owns. A ViewModel already bound to a different context
fails when presented, with an `InvalidOperationException` naming its type. When
you build a `WindowContentSession` or `RunicWindowTestHost` yourself, pass the
context the ViewModels run on as its `ModelContext`; otherwise the window
creates a context of its own and commands and replies are not ordered with each
other.

`AddRunicReactiveModelContext()` moved from `Runic.Navigation.ReactiveUI` to this
package in W250, with `IRunicReactiveSchedulerProvider` and
`RunicReactiveSchedulerProvider`. Replace `using Runic.Navigation.ReactiveUI;`
with `using Runic.Application.Views.ReactiveUI;`.

The [ReactiveUI reference guide](https://docs.runic-artifex.eu/guides/application/reference/reactiveui/)
defines the supported data shapes, operation semantics, interaction targeting,
and model-context ownership.

## Declared failures and ThrownExceptions

A `ReactiveCommand` reports every exception on `ThrownExceptions`, including a
declared `RunicFailureException` (`[RunicFailure]`) that the Bridge already sent
to the client as `domain-failed`. Without a subscriber, ReactiveUI routes it to
`RxState.DefaultExceptionHandler`, which breaks into the debugger and throws
`UnhandledErrorException`. Every bridged `ReactiveCommand` therefore needs a
`ThrownExceptions` subscriber: this helper, or the application's own.

```csharp
_saveExceptions = SaveCommand.ObserveBridgeExceptions(logger);
_discardExceptions = DiscardCommand.ObserveBridgeExceptions(error => status.Report(error));
```

`ObserveBridgeExceptions` ignores `RunicFailureException` and
`OperationCanceledException` (ReactiveUI also reports a cancelled operation on
`ThrownExceptions`; the client already received both) and reports every other
exception: the `ILogger` overload logs it at Error as `ReactiveCommandFailed`
(event 1042) with the command's expression, such as `SaveCommand`, as `Source`
(pass `sourceName` to choose another), and the callback overload passes it on.
Both overloads, including the logger, also ignore an `OperationCanceledException`
that is not the Bridge's, such as an `HttpClient` timeout when the command runs
outside the Bridge; subscribe to `ThrownExceptions` yourself to report those. While it is attached, no
exception of that command reaches
`RxState.DefaultExceptionHandler`. An exception thrown by the callback surfaces
from `ThrownExceptions.OnNext`, inside ReactiveUI. Dispose the returned
subscription with the ViewModel. An unexpected exception from a Bridge call is
also logged by the Views runtime (event 1000 or 1004). The helper works on any
`IHandleObservableErrors`, such as a `ReactiveObject`.

## Navigation (experimental)

The observables and back command for `RunicNavigator` regions
(`WhenCurrentChanged`, `WhenEntryChanged` and `CreateBackCommand`) are in the
`Runic.Navigation.ReactiveUI` namespace of the
[Runic.Navigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
package. It depends on `Runic.Navigation` and `ReactiveUI` only. This package
no longer references it, so an app without navigation has no experimental
package; add a `PackageReference` to `Runic.Navigation.ReactiveUI` to navigate.
Use a `NavigationRegion<TContent>` for new navigation, and keep
`ReactiveRoutedRegion<T>` for existing `RoutingState` code. Expose the region as
a get-only property and the generator presents its `Current` like any content
slot. Suppress `RUNICNAV001` to use the adapter.

`CreateBackCommand()` needs no scheduler: with `AddRunicReactiveModelContext()`
its notifications arrive on the Window's model context. Its output is a
`NavigationOutcome`, so a View can bind it. In a Views app, observe its
`ThrownExceptions` with `ObserveBridgeExceptions` (see above):

```csharp
using Runic.Navigation.ReactiveUI;

BackCommand = Main.CreateBackCommand().DisposeWith(disposables);
BackCommand.ObserveBridgeExceptions(logger).DisposeWith(disposables);
```

ReactiveUI activation follows a mounted View, not a navigation entry's lifetime:
put per-presentation subscriptions in `WhenActivated`.

## System.Reactive flavor

For DynamicData changesets, place `BatchBridgeSnapshots(model)` after
`ObserveOn(modelSequencer)` and before `Bind` or `SortAndBind`. The downstream
delivery owns the batch even when scheduling is deferred. Annotate a read-only
DTO collection with `[RunicCollection(nameof(Row.Id))]` to publish indexed
updates. See the [DynamicData guide](https://docs.runic-artifex.eu/guides/application/guides/dynamicdata/).

Applications using `ReactiveUI.Reactive`, `ReactiveUI.Binding.Reactive`,
`System.Reactive.Unit`, or `IScheduler` should instead reference
[`Runic.Application.Views.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/README.md).
The two packages expose distinct ReactiveUI namespaces and must not be mixed in
one application. If a generic interface command does not reveal its flavor to
the compiled-model generator, set
`RunicApplicationFrontendReactiveUiFlavor=reactive` in the project that generates the
bridge.

See the upstream [Binding migration guide](https://www.reactiveui.net/documentation/reactiveui/upgrading/reactiveui-binding-migration/)
for ReactiveUI's application-level migration details.
