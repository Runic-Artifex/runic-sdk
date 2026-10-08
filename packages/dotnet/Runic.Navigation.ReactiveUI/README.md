# ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI` connects [Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26 (the `ReactiveUI.Primitives` flavor). It depends on
`Runic.Navigation` and `ReactiveUI` only: no `Runic.Application`, generator or
host, so it works in a WPF or console app. It provides, in the
`Runic.Navigation.ReactiveUI` namespace:

- `WhenCurrentChanged()`, `WhenEntryChanged()` and `CreateBackCommand(scheduler)`
  for `NavigationRegion<TContent>` (experimental, `RUNICNAV001`);
- `RunicReactiveSchedulerProvider`, which returns an `ISequencer` that runs
  scheduled work through an `IRunicModelContext`; and
- `AddRunicReactiveModelContext()`, which registers the context and its
  scheduler.

Apps that use the System.Reactive distribution of ReactiveUI reference
[`Runic.Navigation.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/README.md)
instead. Runic Views apps get the adapter of their flavor through
`Runic.Application.ReactiveUI` and need no extra reference.

```sh
dotnet add package Runic.Navigation.ReactiveUI --prerelease
```

## Model-context scheduler

`RunicReactiveSchedulerProvider.For(context)` returns a context-backed
`ISequencer`. It serializes scheduled notifications with short model turns and
deliberately does not set ReactiveUI's process-global scheduler. Use the host's
dispatcher for native UI, and marshal background state changes through the
model context.

The sequencer keeps ordering state, so the provider returns **one sequencer per
context** for as long as the context is alive.

`AddRunicReactiveModelContext()` uses `TryAdd`, so it preserves custom
registrations:

| Service | Lifetime |
| --- | --- |
| `IRunicModelContext` | scoped (`RunicModelContext`) |
| `IRunicReactiveSchedulerProvider` | singleton |
| `ISequencer` | transient; returns the provider's scheduler for the resolved context |

Every resolution for one context therefore gets the same scheduler. That holds
for a scoped context (each scope has its own) and for a singleton context that
a host registers instead, and a singleton ViewModel that injects `ISequencer`
passes `ValidateScopes`. Dispose a scope asynchronously to drain its owned
context. Each queued item captures its own `ExecutionContext`, so deferred work
does not leak an ambient scope to another operation.

```csharp
services.AddRunicReactiveModelContext();

public EditorViewModel(IRunicModelContext modelContext, ISequencer scheduler)
{
    SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, outputScheduler: scheduler);
}
```

## Navigation (experimental)

`RunicNavigator` regions replace `RoutingState` rather than wrap it: a region
has awaited departure guards, stable entry ids, owned content and supersession,
which `RoutingState`'s mutable stack and synchronous `Navigate` cannot enforce.
The adapter is experimental, like the navigator: suppress `RUNICNAV001` to use it.

```csharp
var scheduler = new RunicReactiveSchedulerProvider().For(context);
BackCommand = Main.CreateBackCommand(scheduler).DisposeWith(disposables); // holds a region handler

Main.WhenCurrentChanged()          // TContent?, distinct by instance
    .Select(current => current is DocumentViewModel)
    .ObserveOn(scheduler)
    .Subscribe(isDocument => IsDocumentOpen = isDocument)
    .DisposeWith(disposables);
```

- `WhenCurrentChanged()` emits the current content (`null` when the region is
  empty) on subscription and then each different instance.
  `WhenEntryChanged()` emits each `NavigationEntry<TContent>`, also when two
  entries present the same borrowed instance.
- Both emit the initial value on the subscribing thread, inside `Subscribe`,
  and later values on the model turn that raises the change: the commit turn
  for a navigation. Use `ObserveOn` to deliver elsewhere. They never
  complete, and stop when the subscription is disposed.
- `CreateBackCommand(scheduler)` returns a
  `ReactiveCommand<RxVoid, NavigationResult<TContent>>`. It can execute
  while `CanGoBack` is true and `IsTransitioning` is false, including
  transitions it did not start. It reflects this region only: a transition of
  an ancestor region does not disable it. The command observes the region
  until it is disposed, so dispose it with its owner. A rejected, superseded
  or failed Back is its output, not an exception, so `ThrownExceptions`
  carries only defects and cancellation. Subscribe to `ThrownExceptions`;
  Runic Views apps can use `ObserveBridgeExceptions` from
  `Runic.Application.ReactiveUI`.
  Overlapping executions to the same destination share one Back and confirmation.
  Each caller cancels independently; the shared Back cancels only after every
  caller cancels before commit.
- **Activation is not entry lifetime.** ReactiveUI activation follows a mounted
  View. A navigation entry lives from its push until it retires: a retained
  entry stays alive and keeps its state while nothing presents it, and its View
  deactivates and activates again when the entry returns. Put per-presentation
  subscriptions in `WhenActivated`. Use the navigator's hooks
  (`INavigationInitialize`, `INavigationResume`, `INavigationDepartureGuard`) and
  the entry's `Retirement` token for per-entry work, and `Dispose` for owned
  content.

## Moving from Runic.Application.ReactiveUI

These types moved from the `Runic.Application.Views.ReactiveUI` namespace of
`Runic.Application.ReactiveUI` to `Runic.Navigation.ReactiveUI` in
0.7.0-preview.4, without type forwards. Replace the `using` and recompile.

## Logging

The adapter's category is `Runic.Navigation.ReactiveUI`. Events 1043-1049 are
reserved for it; it logs none today.
