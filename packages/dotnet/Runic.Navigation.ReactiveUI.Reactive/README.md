# System.Reactive ReactiveUI adapter for Runic.Navigation

`Runic.Navigation.ReactiveUI.Reactive` connects
[Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
regions to ReactiveUI 26's System.Reactive distribution (`ReactiveUI.Reactive`).
It is the counterpart of
[`Runic.Navigation.ReactiveUI`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md)
with `System.Reactive.Unit` and `IScheduler` in place of `RxVoid` and
`ISequencer`; that page describes the API and lifetimes. The package depends on
`Runic.Navigation` and `ReactiveUI.Reactive` only, with no `Runic.Application`.
The types are in the `Runic.Navigation.ReactiveUI.Reactive` namespace.

```sh
dotnet add package Runic.Navigation.ReactiveUI.Reactive --prerelease
```

Runic Views apps get this adapter through `Runic.Application.ReactiveUI.Reactive`.
Choose one flavor per app: the Runic.Application adapters reject a direct reference
to both. The two navigation adapters add extension methods with the same names in
different namespaces, so referencing both without Runic.Application is not guarded.

```csharp
services.AddRunicReactiveModelContext();

var scheduler = new RunicReactiveSchedulerProvider().For(context);
SaveCommand = ReactiveCommand.Create(Save, outputScheduler: scheduler);
BackCommand = Main.CreateBackCommand(scheduler).DisposeWith(disposables);
```

`RunicReactiveSchedulerProvider.For(context)` returns one `IScheduler` per
context for as long as the context is alive. `AddRunicReactiveModelContext()`
registers a scoped context, the singleton provider and a transient `IScheduler`
that returns the provider's scheduler for the resolved context, so every
resolution for one context shares it. `WhenCurrentChanged()`,
`WhenEntryChanged()`, `WhenCanGoBackChanged()`, `WhenIsTransitioningChanged()`
and `CreateBackCommand(IScheduler)` are experimental (`RUNICNAV001`). The Back
command returns a `ReactiveCommand<Unit, NavigationResult<TContent>>`.

The [native command composition recipe](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation.ReactiveUI/README.md#native-command-composition)
also applies here. Use `ReactiveUI.Reactive`, `System.Reactive.Linq` and
`Runic.Navigation.ReactiveUI.Reactive` instead of the default flavor's namespaces,
and `Unit`/`IScheduler` instead of `RxVoid`/`ISequencer`. Both flavors compose over
the same core engine and work independently of the WPF or web presentation choice.

The package targets ReactiveUI.Reactive 26.0.1 and brings System.Reactive 7.0.0
transitively. Its category is `Runic.Navigation.ReactiveUI.Reactive`, with events
1043-1049 reserved.
