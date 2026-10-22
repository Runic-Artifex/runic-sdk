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

`AddRunicReactiveModelContext()`, `ReactiveSchedulers` and
`IReactiveSchedulerProvider` are in this package's
`Runic.Application.Views.ReactiveUI.Reactive` namespace. As in the default flavor,
`AddRunicReactiveModelContext()` installs a model-context
`RxSchedulers.MainThreadScheduler` when ReactiveUI's is still a System.Reactive
default, so commands created without an `outputScheduler` deliver their results
and `IsExecuting` on the model context of the turn that runs them, also for a
plain `Execute` in hand-written code started in a turn. Work that a turn's async
flow schedules after it left the turn, such as after `ConfigureAwait(false)` or
in `Task.Run`, runs on that turn's context too (since 0.10). Work whose flow
never ran in a turn, or whose context has closed, runs on the replaced scheduler
and is logged once per ViewModel type (Views event 1043). A scheduler the application or platform set
is kept. An application that builds ReactiveUI with `RxAppBuilder` adds
`WithRunicModelContext()` (namespace `ReactiveUI.Reactive.Builder`) to that
builder. To opt out, set `RxSchedulers.MainThreadScheduler`, or call
`WithMainThreadScheduler` on ReactiveUI's builder, before
`AddRunicReactiveModelContext()`, or replace it afterwards. See
[Model-context scheduling](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#model-context-scheduling)
for the rules.

```csharp
using Runic.Application.Views.ReactiveUI.Reactive;

services.AddRunicReactiveModelContext();

SaveCommand = ReactiveCommand.Create(Save);
```

For an explicit `IScheduler`, for example in an `ObserveOn`, inject it or call
`ReactiveSchedulers.For(context)`, or `ReactiveSchedulers.For(this)` in a
ViewModel, which schedules on the context the ViewModel is bound to when the
work is scheduled (since 0.11). Generated command and interaction
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

The navigation adapter (`WhenCurrentChanged`, `WhenCurrentEntryChanged` and
`CreateBackCommand()`) is in the separate
`Runic.Navigation.ReactiveUI.Reactive` package, with `System.Reactive.Unit` as
the back command's input. This package no longer references it; add it to
navigate.
`ObserveBridgeExceptions` is also available in this adapter's namespace, and
`BatchBridgeSnapshots(model)` in `Runic.Application.Views`; this flavor's commands implement the same
`IHandleObservableErrors`, and a generated Bridge logs their unexpected
exceptions in the same way. See
[Declared failures and ThrownExceptions](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#declared-failures-and-thrownexceptions).
Place it after `ObserveOn(modelScheduler)` and before collection binding; see
the [DynamicData guide](https://docs.runic-artifex.eu/guides/application/guides/dynamicdata/).

Hand-written code that runs a command on behalf of a Bridge call or inside a
model turn calls `command.RunAsync(input, cancellationToken)` from this
adapter's namespace (since 0.11). It completes after zero or more values,
disposes the execution when cancelled, and rethrows a failure without it being
logged as event 1042. See
[Which call to use](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#which-call-to-use).
