# System.Reactive ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI.Reactive` connects
[Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26's System.Reactive distribution (`ReactiveUI.Reactive`).
It is the counterpart of
[`Runic.Navigation.ReactiveUI`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
with `System.Reactive.Unit` and `IScheduler` in place of `RxVoid` and
`ISequencer`; that page describes the API and lifetimes, and its
[walkthrough](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#walkthrough)
applies to this flavor with the substitutions below. The package depends on
`Runic.Navigation` and `ReactiveUI.Reactive` only, with no `Runic.Application.Views`.
The types are in the `Runic.Navigation.ReactiveUI.Reactive` namespace.

```sh
dotnet add package Runic.Navigation.ReactiveUI.Reactive --prerelease
```

Runic Views apps reference this package next to `Runic.Application.Views.ReactiveUI.Reactive`,
which no longer brings it. Choose one flavor per app: with a Runic.Application.Views
ReactiveUI adapter, a mix of flavors fails with `RUNICBRIDGE023`. The two
navigation adapters add extension methods with the same names in different
namespaces; a project that references both without a Views adapter, directly or
through another package or project, gets build warning
[`RUNICNAV002`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicnav002)
(since 0.11).

```csharp
BackCommand = Main.CreateBackCommand(); // on this ViewModel's own region: no disposal
```

The Back command returns a
`ReactiveCommand<Unit, NavigationOutcome>` and, without an `outputScheduler`,
delivers on ReactiveUI's main-thread scheduler: the model context in a Runic
Views app that calls `AddRunicReactiveModelContext()` from
`Runic.Application.Views.ReactiveUI.Reactive`, the dispatcher in WPF; see
[Scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#scheduling).

The entry API is the same source as the default flavor's
([entry lifecycle and result commands](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#entry-lifecycle-and-result-commands),
[leave confirmation through an interaction](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#leave-confirmation-through-an-interaction)),
with these differences:

- `WhenNavigatedTo()` passes a `ContainerDisposable` scope, as `WhenActivated`
  does in this flavor; it converts to `CompositeDisposable`.
- `WhenNavigatedFrom()` returns an `IObservable<NavigationExit>`.
- `CreateCompleteCommand()`, `CreateDismissCommand()` and `CreateCloseCommand()`
  return `ReactiveCommand<Unit, NavigationOutcome>` (`ReactiveCommand<TResult,
  NavigationOutcome>` for the overload that completes with the command's
  parameter) and take an optional `IScheduler`.
- `ReactiveLeaveConfirmation.WithInteraction()` and
  `ReactiveNavigationRegionGuard.WithInteraction()` take a
  `ReactiveUI.Binding.Reactive.IInteraction<TPrompt, bool>`. An unanswered question
  fails the departure and is logged as `NavigationGuardFailed` (event 1060) with
  `ErrorType` `ReactiveUI.Binding.Reactive.UnhandledInteractionException`2`.

The [native command composition recipe](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#native-command-composition)
also applies here. Use `ReactiveUI.Reactive`, `System.Reactive.Linq` and
`Runic.Navigation.ReactiveUI.Reactive` instead of the default flavor's namespaces,
and `Unit`/`IScheduler` instead of `RxVoid`/`ISequencer`. Both flavors compose over
the same core engine and work independently of the WPF or web presentation choice.

The package targets ReactiveUI.Reactive 26.0.1 and brings System.Reactive 7.0.0
transitively. Its category is `Runic.Navigation.ReactiveUI.Reactive`, with events
1044-1049 reserved.
