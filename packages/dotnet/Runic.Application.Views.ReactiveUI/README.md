# ReactiveUI integration for Runic Views

`Runic.Application.ReactiveUI` is the default ReactiveUI 26 adapter for Runic
Views. It references ReactiveUI's `ReactiveUI.Primitives` flavor and has no
CommunityToolkit dependency. It provides:

- `ReactiveRunicView<T>` and `ReactiveRunicWindow<T>`, which implement
  `IViewFor<T>` over a typed Runic `DataContext`;
- `ReactiveRunicViewLocator`, adapting explicit ReactiveUI view mappings and
  contracts;
- `ReactiveRoutedRegion<T>`, projecting `RoutingState.CurrentViewModel` into
  a generated content property, logging an incompatible route or failed
  router through an optional `ILoggerFactory` (Views events 1040-1041);
- experimental observables and a back command for `RunicNavigator` regions,
  provided by `Runic.Navigation.ReactiveUI`
  (see [Navigation](#navigation-experimental));
- mount-owned `IActivatableViewModel` leases; and
- typed command and interaction adapters used by
  the compiled-model generator.

One browser presentation gets one logical Runic View and activation lease. A
shared ViewModel stays activated while any presentation remains mounted.
`WindowContentSession.AttachPresentation` creates those logical views for
generated content routes. The [Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/examples/notes-reactive-views/README.md)
example covers multiple views over one ViewModel, routed content, explicit view
contracts, and activation lifetimes.
New projects can start with ReactiveUI ViewModels:
`dotnet new runic-app --view-models reactiveui`.

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

`RunicReactiveSchedulerProvider.For(context)` returns a context-backed
`ISequencer`, one per context. It serializes scheduled notifications with short
Runic model turns and deliberately does not set ReactiveUI's process-global
scheduler. The provider, `AddRunicReactiveModelContext()` and the navigation
adapter are in the `Runic.Navigation.ReactiveUI` namespace of the
[Runic.Navigation.ReactiveUI](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
package, which this package references, so add
`using Runic.Navigation.ReactiveUI;`. Use normal host dispatchers for native UI
and explicitly marshal background state changes through the model context.

Call `AddRunicReactiveModelContext()` before the ViewModel creates its commands
and inject the sequencer. Bind the root and every independently
presented child to that same context. `IRunicModelContext` and
`RunicModelContextRegistry` are in the `Runic.Navigation` namespace:

```csharp
services.AddRunicReactiveModelContext();

public EditorViewModel(
    EditorSession session,
    IRunicModelContext modelContext,
    ISequencer scheduler)
{
    _scheduler = scheduler;
    Workspace = new EditorWorkspaceViewModel(session, this, _scheduler);
    _contextLease = RunicModelContextRegistry.Shared.Bind(modelContext, this, Workspace);
}

protected ReactiveCommand<string, RxVoid> CreateCommand(Func<string, Task> work) =>
    ReactiveCommand.CreateFromTask<string>(work, _scheduler);
```

`Execute` may complete before its scheduled `IsExecuting` and `CanExecute`
notifications arrive. The context-backed scheduler orders those notifications
with bridge replies and state publication. A default headless scheduler may
deliver them later; do not solve that by changing ReactiveUI's global scheduler.
Bind dynamically created or independently presented children to the same
context and retain their leases until their presentation is removed.

The extension uses `TryAdd` for the scoped `IRunicModelContext`, singleton
`IRunicReactiveSchedulerProvider`, and transient `ISequencer` (which returns
the provider's scheduler for the resolved context), preserving custom
application registrations. Dispose a scope asynchronously to drain its default
owned context. Each queued context/scheduler item captures its own
`ExecutionContext`, so a trusted interaction scope follows its own deferred
work without leaking to another queued operation.

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
package, which this package references. It depends on `Runic.Navigation` and
`ReactiveUI` only. Use a `NavigationRegion<TContent>` for new navigation, and
keep `ReactiveRoutedRegion<T>` for existing `RoutingState` code. Expose the
region as a get-only property and the generator presents its `Current` like any
content slot. Suppress `RUNICNAV001` to use the adapter.

In a Views app, observe the back command's `ThrownExceptions` with
`ObserveBridgeExceptions` (see above):

```csharp
using Runic.Navigation.ReactiveUI;

var scheduler = new RunicReactiveSchedulerProvider().For(context);
BackCommand = Main.CreateBackCommand(scheduler).DisposeWith(disposables);
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
[`Runic.Application.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/README.md).
The two packages expose distinct ReactiveUI namespaces and must not be mixed in
one application. If a generic interface command does not reveal its flavor to
the compiled-model generator, set
`RunicBridgeReactiveUiFlavor=reactive` in the project that generates the
bridge.

See the upstream [Binding migration guide](https://www.reactiveui.net/documentation/reactiveui/upgrading/reactiveui-binding-migration/)
for ReactiveUI's application-level migration details.
