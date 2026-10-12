# System.Reactive ReactiveUI integration for Runic Views

`Runic.Application.Views.ReactiveUI.Reactive` is the optional Runic adapter for
ReactiveUI 26's System.Reactive-compatible distribution. It provides the same
Runic View and Window mount lifecycle, typed view location and routing
projection as `Runic.Application.Views.ReactiveUI` (Views derive from
`ReactiveRunicView<T>`; Windows from `RunicWindow<T>`, since
`ReactiveRunicWindow<T>` was removed in 0.7.0-preview.7), while using types from
`ReactiveUI.Reactive`, `ReactiveUI.Binding.Reactive`, `System.Reactive.Unit`,
and `System.Reactive.Concurrency.IScheduler`.

Choose this package only when the application's ReactiveUI-facing APIs need
System.Reactive's scheduler and unit types:

```xml
<PackageReference Include="ReactiveUI.Reactive" />
<PackageReference Include="Runic.Application.Views.ReactiveUI.Reactive" />
```

Use the default `ReactiveUI` and `Runic.Application.Views.ReactiveUI` pair for the
Primitives flavor. Do not reference both Runic adapters in one application.
The System.Reactive distribution has distinct ReactiveUI namespaces and type
identities, so ViewModels and generated clients must be built for the selected
pair. The package includes a build-time guard for a direct reference to both
Runic adapters.

`AddRunicReactiveModelContext()`, `IRunicReactiveSchedulerProvider` and
`RunicReactiveSchedulerProvider` are in this package's
`Runic.Application.Views.ReactiveUI.Reactive` namespace; they moved from
`Runic.Navigation.ReactiveUI.Reactive` in W250. As in the default flavor,
`AddRunicReactiveModelContext()` installs a model-context
`RxSchedulers.MainThreadScheduler` when ReactiveUI's is still a System.Reactive
default, so commands created without an `outputScheduler` deliver their results
and `IsExecuting` on the model context of the turn that runs them. Work
scheduled outside a turn runs on the replaced scheduler and is logged once
(Views event 1043). A scheduler the application or platform set is kept. See
[Model-context scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#model-context-scheduling)
for the rules and the opt-out.

```csharp
using Runic.Application.Views.ReactiveUI.Reactive;

services.AddRunicReactiveModelContext();

SaveCommand = ReactiveCommand.Create(Save);
```

For an explicit `IScheduler`, for example in an `ObserveOn`, inject it or call
`RunicReactiveSchedulerProvider.For(context)`. Generated command and interaction
contracts use this flavor's adapters, with the same typed data, operation
results, cancellation, and browser interaction support as the default flavor.

The package targets ReactiveUI.Reactive 26.0.1 with
ReactiveUI.Binding.Reactive pinned to 9.1.0. It brings
ReactiveUI.Primitives.Reactive 9.0.0 and System.Reactive 7.0.0 transitively.
ReactiveUI 26 keeps the 25 public API; see the
[default adapter notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#reactiveui-26)
for the Primitives 9 `SubscribeSafe` change.
It also brings the shared ReactiveUI.SourceGenerators 4.2.0 package; use the
normal `ReactiveUI.SourceGenerators` attributes with
`ReactiveUI.Reactive.ReactiveObject`. There is no separate Reactive-flavor
source-generator package.

The navigation adapter (`WhenCurrentChanged`, `WhenEntryChanged` and
`CreateBackCommand()`) is in the separate
`Runic.Navigation.ReactiveUI.Reactive` package, with `System.Reactive.Unit` as
the back command's input. This package no longer references it; add it to
navigate.
`BatchBridgeSnapshots(model)` and `ObserveBridgeExceptions` are also available in
this adapter's namespace; this flavor's commands implement the same
`IHandleObservableErrors`, and a generated Bridge logs their unexpected
exceptions in the same way. See
[Declared failures and ThrownExceptions](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#declared-failures-and-thrownexceptions).
Place it after `ObserveOn(modelScheduler)` and before collection binding; see
the [DynamicData guide](https://docs.runic-artifex.eu/guides/application/guides/dynamicdata/).
