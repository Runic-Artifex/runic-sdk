# RunicToolkit.MVVM.CommunityToolkit

`RunicToolkit.MVVM.CommunityToolkit` is the first-party, runtime-only bridge for
explicitly generated CommunityToolkit.Mvvm bindings. It references only
`RunicToolkit.MVVM` and exact `CommunityToolkit.Mvvm` `[8.4.2]`; it performs no member
discovery, reflection, dynamic activation, or options-only JSON serialization.

Use the generated ViewModel surface directly when declaring a binding. Every
property and typed command parameter supplies an application-owned
`JsonTypeInfo<T>`, normally from a `JsonSerializerContext`. The builder creates a
closed `IMvvmBindingAdapter` with deterministic member metadata, snapshots,
property, collection, and validation patches, command state, cancellation through
`IAsyncRelayCommand.Cancel()`, and exactly-once event unsubscription.

```csharp
MvvmPropertyReference title = TodoView.HtmxFields.Title;
MvvmCommandReference save = TodoView.HtmxCommands.SaveCommand;

IMvvmBindingAdapter adapter = new CommunityToolkitMvvmAdapterBuilder<MyViewModel>(viewModel)
    .BindProperty(title, static vm => vm.Title,
        static (vm, value) => vm.Title = value, MyJsonContext.Default.String)
    .BindCollection(MvvmCollectionReference.Create(nameof(MyViewModel.Items)),
        static vm => vm.Items,
        MyJsonContext.Default.Item)
    .BindCommand(save, static vm => vm.SaveCommand)
    .Build();
```

Typed references derive deterministic, kind-separated protocol identifiers
from compile-time-checked generated member names. The integer-and-name
overloads remain available for generated contracts and compatibility code.
Generated application adapter factories may pass a source-generated
`JsonSerializerContext` directly. The builder resolves the exact closed
`JsonTypeInfo<T>` and fails during adapter creation when the context does not
contain a declared projected type; no reflection fallback is used.

`BindCollection` accepts an `IReadOnlyList<T>` projection, includes it in
authoritative snapshots, owns an `INotifyCollectionChanged` subscription when
the collection supplies one, and emits an authoritative collection reset after
successful commands. This is the stable WPF/`ObservableCollection<T>` bridge;
granular unsolicited host-push patches can be added without changing binding
declarations.
