# System.Reactive ReactiveUI integration for Runic Views

`Runic.Application.ReactiveUI.Reactive` is the optional Runic adapter for
ReactiveUI 26's System.Reactive-compatible distribution. It provides the same
Runic View and Window mount lifecycle, typed view location and routing
projection as `Runic.Application.ReactiveUI`, while using types from
`ReactiveUI.Reactive`, `ReactiveUI.Binding.Reactive`, `System.Reactive.Unit`,
and `System.Reactive.Concurrency.IScheduler`.

Choose this package only when the application's ReactiveUI-facing APIs need
System.Reactive's scheduler and unit types:

```xml
<PackageReference Include="ReactiveUI.Reactive" />
<PackageReference Include="Runic.Application.ReactiveUI.Reactive" />
```

Use the default `ReactiveUI` and `Runic.Application.ReactiveUI` pair for the
Primitives flavor. Do not reference both Runic adapters in one application.
The System.Reactive distribution has distinct ReactiveUI namespaces and type
identities, so ViewModels and generated clients must be built for the selected
pair. The package includes a build-time guard for a direct reference to both
Runic adapters.

`RunicReactiveSchedulerProvider` adapts a supplied `IRunicModelContext` to an
`IScheduler`. Pass it to ReactiveUI.Reactive command factories as their
`outputScheduler` when command state must be delivered in that model context:

```csharp
var scheduler = new RunicReactiveSchedulerProvider().For(context);
SaveCommand = ReactiveCommand.Create(Save, outputScheduler: scheduler);
```

It keeps scheduled notifications in that model context and does not set a
process-global ReactiveUI scheduler. Generated command and interaction
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

`BatchBridgeSnapshots(model)` is also available in this adapter's namespace.
Place it after `ObserveOn(modelScheduler)` and before collection binding; see
the [DynamicData guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/application/guides/dynamicdata.md).
