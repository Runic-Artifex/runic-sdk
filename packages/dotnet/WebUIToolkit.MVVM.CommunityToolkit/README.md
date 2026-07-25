# WebUIToolkit.MVVM.CommunityToolkit

`WebUIToolkit.MVVM.CommunityToolkit` is the first-party, runtime-only bridge for
explicitly generated CommunityToolkit.Mvvm bindings. It references only
`WebUIToolkit.MVVM` and exact `CommunityToolkit.Mvvm` `[8.4.2]`; it performs no member
discovery, reflection, dynamic activation, or options-only JSON serialization.

Use the generated ViewModel surface directly when declaring a binding. Every
property and typed command parameter supplies an application-owned
`JsonTypeInfo<T>`, normally from a `JsonSerializerContext`. The builder creates a
closed `IMvvmBindingAdapter` with deterministic member metadata, snapshots,
property and validation patches, command state, cancellation through
`IAsyncRelayCommand.Cancel()`, and exactly-once event unsubscription.

```csharp
IMvvmBindingAdapter adapter = new CommunityToolkitMvvmAdapterBuilder<MyViewModel>(viewModel)
    .BindProperty(1, nameof(MyViewModel.Title), static vm => vm.Title,
        static (vm, value) => vm.Title = value, MyJsonContext.Default.String)
    .BindCommand(2, static vm => vm.SaveCommand)
    .Build();
```

The shared binding compiler currently exposes only the pre-Wave-C PE proof API.
It does not expose a post-generator semantic-plugin or MSBuild artifact hook, so
automatic recognition and source emission are intentionally not claimed by this
runtime package. Consumers must use generated direct-access declarations until
that shared hook is registered.
