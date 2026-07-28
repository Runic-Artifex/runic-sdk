# Frontend contracts

WebUIToolkit supports C#-first and JSON-first contract authoring. Both paths
produce `webuitoolkit.mvvm.frontend-contract/1`, and the same frontend
generator consumes that canonical artifact to create the direct TypeScript
contract plus optional React, Vue, Svelte, and Angular façades.

## C#-first authoring

C#-first is the default for new framework templates and the recommended path
when migrating a WPF ViewModel. Enable it in the application project:

```xml
<PropertyGroup>
  <WebUIToolkitFrontendContractCSharpFirst>true</WebUIToolkitFrontendContractCSharpFirst>
  <WebUIToolkitFrontendContractTypeScriptOutput>$(MSBuildProjectDirectory)/Frontend/src/counter-contract.g.ts</WebUIToolkitFrontendContractTypeScriptOutput>
  <WebUIToolkitFrontendContractReactOutput>$(MSBuildProjectDirectory)/Frontend/src/counter-bindings.g.ts</WebUIToolkitFrontendContractReactOutput>
</PropertyGroup>
```

Declare the contract and exported surface on the ViewModel:

```csharp
[WebUiFrontendContract(
    "example.counter",
    "Counter",
    typeof(CounterJsonContext),
    GeneratedClassName = "CounterContracts")]
public sealed partial class CounterViewModel : ObservableValidator
{
    [ObservableProperty]
    [WebUiFrontendProperty(1, "count", SourceMember = "Count", ReadOnly = true)]
    private int _count;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [WebUiFrontendProperty(
        2,
        "step",
        SourceMember = "Step",
        IncludeValidation = true)]
    private int _step = 1;

    [WebUiFrontendCollection(3, "history")]
    public ObservableCollection<int> History { get; } = [0];

    [RelayCommand]
    [WebUiFrontendCommand(10, "increment")]
    private void Increment() { /* ... */ }
}
```

Every declaration requires an explicit positive wire ID. Renaming a C# or
TypeScript member while retaining its ID preserves wire identity; changing or
reusing an ID is a protocol change. Duplicate IDs/names, invalid clients,
unsupported collection shapes, and conflicting generated containers produce
`WUTFE` compiler diagnostics at the declaration.

Property and collection types, command arguments, sync/async command shape,
TypeScript structural types, serializer-context members, and conventional
source member names are inferred. Override `SourceMember`, `TypeScriptType` or
`TypeScriptArgument`, `JsonTypeInfoProperty`, `ReadOnly`, and `IsAsync` for
non-standard ViewModel or serializer conventions.

The compiler emits the reflection-free CommunityToolkit adapter directly into
the compilation. It also writes the canonical, inspectable artifact under
`obj`; that artifact contains project-relative file, line, and column metadata
and is consumed by the same framework generator as JSON-first projects. No
generated C# or intermediate JSON needs to be checked into source control.

## JSON-first authoring

Set `WebUIToolkitFrontendContractSource`,
`WebUIToolkitFrontendContractCSharpOutput`, and the TypeScript/framework output
properties to keep a language-neutral JSON file as the source of truth. This
mode remains useful when several backend languages share the contract or the
contract is managed independently from a ViewModel.

Do not configure C#-first and a separate JSON source in the same application.
When C#-first is enabled, the SDK deliberately replaces
`WebUIToolkitFrontendContractSource` with the compiler-produced canonical
artifact so the two authoring paths cannot silently diverge.
