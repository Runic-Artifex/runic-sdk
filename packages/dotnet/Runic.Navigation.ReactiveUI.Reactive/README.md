# System.Reactive ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI.Reactive` connects
[Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26's System.Reactive distribution (`ReactiveUI.Reactive`).
It is the counterpart of
[`Runic.Navigation.ReactiveUI`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
with `System.Reactive.Unit` and `IScheduler` in place of `RxVoid` and
`ISequencer`; that page describes the API and lifetimes. The package depends on
`Runic.Navigation` and `ReactiveUI.Reactive` only, with no `Runic.Application.Views`.
The types are in the `Runic.Navigation.ReactiveUI.Reactive` namespace.

```sh
dotnet add package Runic.Navigation.ReactiveUI.Reactive --prerelease
```

Runic Views apps reference this package next to `Runic.Application.Views.ReactiveUI.Reactive`,
which no longer brings it. Choose one flavor per app: the Runic.Application.Views
adapters reject a direct reference to both. The two navigation adapters add
extension methods with the same names in different namespaces, so referencing
both without Runic.Application.Views is not guarded.

```csharp
BackCommand = Main.CreateBackCommand().DisposeWith(disposables);
```

`WhenCurrentChanged()`, `WhenEntryChanged()`, `WhenCanGoBackChanged()`,
`WhenIsTransitioningChanged()` and `CreateBackCommand()` are experimental
(`RUNICNAV001`). The Back command returns a
`ReactiveCommand<Unit, NavigationOutcome>` and, without an `outputScheduler`,
delivers on ReactiveUI's main-thread scheduler: the model context in a Runic
Views app that calls `AddRunicReactiveModelContext()` from
`Runic.Application.Views.ReactiveUI.Reactive`, the dispatcher in WPF. The scheduler
provider and `AddRunicReactiveModelContext()` moved to that package in W250;
see [Scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#scheduling).

The [native command composition recipe](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#native-command-composition)
also applies here. Use `ReactiveUI.Reactive`, `System.Reactive.Linq` and
`Runic.Navigation.ReactiveUI.Reactive` instead of the default flavor's namespaces,
and `Unit`/`IScheduler` instead of `RxVoid`/`ISequencer`. Both flavors compose over
the same core engine and work independently of the WPF or web presentation choice.

The package targets ReactiveUI.Reactive 26.0.1 and brings System.Reactive 7.0.0
transitively. Its category is `Runic.Navigation.ReactiveUI.Reactive`, with events
1044-1049 reserved.
