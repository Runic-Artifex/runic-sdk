# ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI` connects [Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26 (the `ReactiveUI.Primitives` flavor). It depends on
`Runic.Navigation` and `ReactiveUI` only: no `Runic.Application.Views`, generator or
host, so it works in a WPF or console app. It provides, in the
`Runic.Navigation.ReactiveUI` namespace, `WhenCurrentChanged()`,
`WhenEntryChanged()`, `WhenCanGoBackChanged()`, `WhenIsTransitioningChanged()`
and `CreateBackCommand()` for `NavigationRegion<TContent>` (experimental,
`RUNICNAV001`).

Apps that use the System.Reactive distribution of ReactiveUI reference
[`Runic.Navigation.ReactiveUI.Reactive`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI.Reactive/README.md)
instead. Runic Views apps reference this package next to
`Runic.Application.Views.ReactiveUI`, which no longer brings it, so an app without
navigation has no experimental package.

```sh
dotnet add package Runic.Navigation.ReactiveUI --prerelease
```

## Scheduling

The adapter neither sets nor needs a scheduler. Like any `ReactiveCommand`, the
back command delivers its output and `IsExecuting` on ReactiveUI's
`RxSchedulers.MainThreadScheduler` unless you pass an `outputScheduler`:

- In a Runic Views app, `AddRunicReactiveModelContext()` from
  `Runic.Application.Views.ReactiveUI` makes that scheduler the model context of the
  turn that executes the command. See its
  [model-context scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#model-context-scheduling).
- In a WPF app, it is the dispatcher.
- Elsewhere, such as a console app or a test, pass a scheduler that runs work on
  the model context when the notifications must be ordered with model state.

`RunicReactiveSchedulerProvider`, `IRunicReactiveSchedulerProvider` and
`AddRunicReactiveModelContext()` moved from this package to
`Runic.Application.Views.ReactiveUI` (same namespace) in W250, with no type
forwards.

## Navigation (experimental)

`RunicNavigator` regions replace `RoutingState` rather than wrap it: a region
has awaited departure guards, stable entry ids, owned content and supersession,
which `RoutingState`'s mutable stack and synchronous `Navigate` cannot enforce.
The adapter is experimental, like the navigator: suppress `RUNICNAV001` to use it.

```csharp
BackCommand = Main.CreateBackCommand().DisposeWith(disposables); // holds a region handler

Main.WhenCurrentChanged()          // TContent?, distinct by instance, on the model turn
    .Select(current => current is DocumentViewModel)
    .Subscribe(isDocument => IsDocumentOpen = isDocument)
    .DisposeWith(disposables);
```

- `WhenCurrentChanged()` emits the current content (`null` when the region is
  empty) on subscription and then each different instance.
  `WhenEntryChanged()` emits each `NavigationEntry<TContent>`, also when two
  entries present the same borrowed instance.
- `WhenCanGoBackChanged()` emits whether the region has Back history.
  `WhenIsTransitioningChanged()` emits whether it has an unsettled transition,
  including one started by another caller. They emit initial values and then
  distinct values observed at region change notifications. Neither includes
  an ancestor region's state. Back history availability stays true while a
  guard waits; combine it with transition state when composing a command.
- All four emit the initial value on the subscribing thread, inside `Subscribe`,
  and later values on the model turn that raises the change. Content changes
  arrive on the navigation's commit turn. Use `ObserveOn` to deliver elsewhere.
  They never complete, and stop when the subscription is disposed.
- `CreateBackCommand()` returns a `ReactiveCommand<RxVoid, NavigationOutcome>`;
  pass an `outputScheduler` to choose where it delivers. `NavigationOutcome` is
  the result's `Kind` (`Committed`, `Rejected`, `Superseded` or `Failed`) and,
  for a rejection, its `Rejection` reason, without .NET content, so a Runic View
  can bind the command. For the full `NavigationResult<TContent>`, such as a
  failure's exception, await `BackAsync` in your own command. The command can
  execute while `CanGoBack` is true and `IsTransitioning` is false, including
  transitions it did not start. `CanExecute` turns false in the same
  notification that starts a transition, without a scheduler hop.
  It reflects this region only: a transition of
  an ancestor region does not disable it. The command observes the region
  until it is disposed, so dispose it with its owner. A rejected, superseded
  or failed Back is its output, not an exception, so `ThrownExceptions`
  carries only defects and cancellation. Subscribe to `ThrownExceptions`;
  Runic Views apps can use `ObserveBridgeExceptions` from
  `Runic.Application.Views.ReactiveUI`.
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

## Native command composition

The adapter supplies observable state for ReactiveUI pipelines; it does not
replace ReactiveUI's command factories. A ViewModel can compose its own
application conditions, `ReactiveCommand.CreateFromTask`, observable properties
and navigation outcomes:

```csharp
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

var canOpen = Main.WhenIsTransitioningChanged()
    .CombineLatest(this.WhenAnyValue(vm => vm.HasSelection),
        (busy, selected) => !busy && selected);

OpenCommand = ReactiveCommand.CreateFromTask(async token =>
{
    var result = await Main.PushAsync<DocumentViewModel>(cancellationToken: token);
    HandleNavigationOutcome(result); // resumes on the model context
}, canOpen).DisposeWith(disposables);

_isNavigatingHelper = Main.WhenIsTransitioningChanged()
    .ToProperty(this, vm => vm.IsNavigating)
    .DisposeWith(disposables);
```

The `IsNavigating` getter reads `_isNavigatingHelper.Value` (or use ReactiveUI's
`[ObservableAsProperty]` generator convention).

The command resumes on the model context after `await` because it started
there: the Runic bridge starts commands in a model-context turn, and a WPF
binding starts them on the dispatcher of a `DispatcherModelContext`. A command
started elsewhere, such as directly from a test, commits its state with
`context.InvokeAsync`.

`HandleNavigationOutcome` is application policy: a guard veto, cancellation,
supersession or failure is a typed core result, and must not be treated as a
successful navigation merely because the command's task completed. Command
availability is advisory; the engine still validates every request. Forward
the token for cancellation during guards or preparation. For result requests,
forward it to `PushForResult` to also dismiss a committed prompt on cancellation.

Deliver model state on the model context (the default in a Runic Views app) and
native UI state on the presentation's dispatcher. Own command/property subscriptions with the ViewModel;
subscriptions specific to a mounted View belong in `WhenActivated`.
This command/property shape works in WPF and through Runic's web bridge. The
example exposes a completion-only command and handles navigation results inside
the ViewModel, so the web boundary does not need to encode navigation's content
and ownership objects. To return an outcome to the View, return
`result.Outcome`, a `NavigationOutcome`.

Runic's `ReactiveRoutedRegion<T>` in `Runic.Application.Views.ReactiveUI` is a separate
interoperability path for an externally owned `RoutingState`. It presents that
router's content; it does not apply RunicNavigator's guards and entry semantics
or implement another Runic navigation engine.

## Moving from Runic.Application.ReactiveUI

These types moved from the `Runic.Application.Views.ReactiveUI` namespace of
`Runic.Application.ReactiveUI` (now `Runic.Application.Views.ReactiveUI`) to
`Runic.Navigation.ReactiveUI` in 0.7.0-preview.4, without type forwards. Replace
the `using` and recompile.

In W250 the model-context scheduling moved back to
`Runic.Application.Views.ReactiveUI` (see [Scheduling](#scheduling)), and
`CreateBackCommand` outputs a `NavigationOutcome` instead of a
`NavigationResult<TContent>`: compare
`outcome.Kind` with `NavigationOutcomeKind.Committed` where you matched
`NavigationResult<TContent>.Committed`.

## Logging

The adapter's category is `Runic.Navigation.ReactiveUI`. Events 1044-1049 are
reserved for it; it logs none today. Event 1043 is the Views
`ReactiveSchedulerOutsideModelTurn`.
