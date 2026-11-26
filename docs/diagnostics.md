# Diagnostics

Every diagnostic ID that a Runic SDK package reports has an entry here, except
the experimental API IDs, which [Experimental APIs](experimental.md) describes.
The [release policy](../eng/release/README.md#what-counts-as-breaking) lists the
families and what may change in them. Build errors, `dotnet runic`
failures, the asset packer and Runic.Create link to their entry at the release
tag of the package that reported them, so the text matches the version you use.
Change the tag in the address to read another release.

Each entry starts with what the ID means. **Fix** says what to change.

- [Bridge code generator (`RUNICBRIDGE`)](#bridge-code-generator)
- [`dotnet runic` (`RAPPCLI`, `RAPPDEV`, `RAPPSIZE`, `RCLI`)](#dotnet-runic)
- [Asset packer and Runic.Assets build (`RAS`)](#asset-packer)
- [Runic Desktop build (`RUNICDESK`)](#runic-desktop-build)
- [Application icon (`RUNICICON`)](#application-icon)
- [Navigation ReactiveUI build (`RUNICNAV`)](#navigation-reactiveui-build)
- [Runic.Create (`RCREATE`)](#runiccreate)
- [Privileged actions (`RUNICPA`)](#privileged-actions)
- [D-Bus source generator (`RUNICDBUS`)](#d-bus-source-generator)
- [Privileged actions on Windows (`RUNICEL`)](#privileged-actions-on-windows)
- [Runic.Platform analyzers (`RUNICPLAT`)](#runicplatform-analyzers)
- [Vite plugin (`RUNICP`)](#vite-plugin)

## Bridge code generator

`Runic.Application.Views` generates the Bridge and the TypeScript clients after the
model assembly compiles. It reports every ViewModel's first problem in one
build. An error names the type and member, points at its source line when the
assembly has a portable PDB (the default), and ends with the link to its entry.
The `Runic.Application.Views` build targets report the codes that concern the
frontend and the build itself. Warnings don't fail the build unless the project
treats warnings as errors; the warnings the generator reports itself repeat on
builds that reuse the cached generation. Every other code fails the build, except
`RUNICBRIDGE038`, which the runtime throws when an application runs Bridges
built against another Runic release. `RUNICBRIDGE` errors
come from this post-compile step: they appear after a build, link to the source
line, and a warning is suppressed per project with `NoWarn`. Since 0.20 a Roslyn
analyzer in `Runic.Application.Views` also reports the errors that the source
alone shows, while you type, with code fixes for the mechanical ones, and some
warnings, which it then reports in the build too: see
[Errors and warnings while you type](#errors-and-warnings-while-you-type). The
[generator section](../packages/dotnet/Runic.Application.Views/README.md#generator-diagnostics)
of the Runic.Application.Views guide links here.

[`runicbridge-diagnostics.json`](runicbridge-diagnostics.json) lists the same
codes for tools: each with its `id`, `severity` (`error` or `warning`) and
`title`, the Problem column below. The generator writes it from its code table
(`--list-diagnostics`); `schemaVersion` 1 may gain members, and the sets of IDs
and severities are open.

| ID | Severity | Reported by | Problem |
| --- | --- | --- | --- |
| [`RUNICBRIDGE001`](#runicbridge001) | Error | Generator, or the build targets when the generator is missing | Invalid generator invocation or build configuration, generated output that cannot be written, or a missing generator |
| [`RUNICBRIDGE002`](#runicbridge002) | Error | Generator | A CommunityToolkit `ObservableValidator` ViewModel in a Native AOT publish |
| [`RUNICBRIDGE003`](#runicbridge003) | Error | Generator; the analyzer while you type | A value type that is not a supported bridge value |
| [`RUNICBRIDGE004`](#runicbridge004) | Error | Generator; the analyzer while you type | Two generated names collide, or a command's client member is reserved in TypeScript |
| [`RUNICBRIDGE005`](#runicbridge005) | Error | Generator | An assembly, a Runic View or a bridged member's type could not be loaded |
| [`RUNICBRIDGE006`](#runicbridge006) | Error | Generator; the analyzer while you type | No Window or View class, or an invalid class or View contract |
| [`RUNICBRIDGE007`](#runicbridge007) | Error | Generator; the analyzer while you type | A ViewModel class has an unsupported shape |
| [`RUNICBRIDGE008`](#runicbridge008) | Error | Generator; the analyzer while you type | A state property, ViewModel content member or append-only text has an unsupported shape |
| [`RUNICBRIDGE009`](#runicbridge009) | Error | Generator; the analyzer while you type | A command has an unsupported name or shape |
| [`RUNICBRIDGE010`](#runicbridge010) | Error | Generator; the analyzer while you type | A `[RunicCollection]` member is not a keyed collection of DTO rows |
| [`RUNICBRIDGE011`](#runicbridge011) | Error | Generator; the analyzer while you type | A ReactiveUI interaction is not a get-only property |
| [`RUNICBRIDGE012`](#runicbridge012) | Error | Generator; the analyzer while you type | A `[RunicFailure]` declaration is misplaced, repeated or names an unsupported type |
| [`RUNICBRIDGE013`](#runicbridge013) | Warning | The analyzer, while you type and in the build; the generator without it | A ViewModel that a content slot admits has no View |
| [`RUNICBRIDGE014`](#runicbridge014) | Error | Build targets | The frontend directory or its `package.json` is missing |
| [`RUNICBRIDGE015`](#runicbridge015) | Warning | Build targets | A project declares Views, but generation is off |
| [`RUNICBRIDGE016`](#runicbridge016) | Error | Build targets | The frontend install or build command failed without a located error |
| [`RUNICBRIDGE017`](#runicbridge017) | Error | Build targets | A build property or item uses a former name |
| [`RUNICBRIDGE018`](#runicbridge018) | Error | Build targets | A referenced assembly already declares the generated composition type |
| [`RUNICBRIDGE019`](#runicbridge019) | Warning | Build targets | The build cannot tell which host's Views client to copy |
| [`RUNICBRIDGE020`](#runicbridge020) | Warning | Generator | Types were skipped because an assembly they need could not be loaded |
| [`RUNICBRIDGE021`](#runicbridge021) | Error | Build targets | The external `node_modules` could not be linked |
| [`RUNICBRIDGE022`](#runicbridge022) | Error | Build targets | `NoBuild=true` finds no generated Bridge code to reuse |
| [`RUNICBRIDGE023`](#runicbridge023) | Error | Build targets | The application references Runic ReactiveUI adapters of both ReactiveUI flavors |
| [`RUNICBRIDGE024`](#runicbridge024) | Error | Generator, or the build targets when the generator fails without reporting an error | An internal generator error |
| [`RUNICBRIDGE025`](#runicbridge025) | Error | Generator; the analyzer while you type | A ReactiveUI command or interaction without the Runic ReactiveUI adapter of its flavor |
| [`RUNICBRIDGE026`](#runicbridge026) | Error | Build targets | The frontend, or its JavaScript workspace, declares dependencies but has no lock file for the default install |
| [`RUNICBRIDGE027`](#runicbridge027) | Error | Generator; the analyzer while you type | A `[RunicCommandKey]` declaration is misplaced, or its resolver is missing or has an unsupported shape |
| [`RUNICBRIDGE028`](#runicbridge028) | Warning | The analyzer, while you type and in the build; the generator without it | A `[RunicOperationRetirement]` declaration reaches no operation |
| [`RUNICBRIDGE029`](#runicbridge029) | Warning | Build targets | `RunicApplicationFrontendPackageManager` is not `npm`, `pnpm` or `bun` |
| [`RUNICBRIDGE030`](#runicbridge030) | Warning | Build targets | `RunicApplicationFrontendDelivery` is unknown, or Embedded cannot apply |
| [`RUNICBRIDGE031`](#runicbridge031) | Warning | The analyzer, while you type and in the build; the generator without it | A type implements an IValidatableViewModel that is not ReactiveUI.Validation's |
| [`RUNICBRIDGE032`](#runicbridge032) | Warning | The analyzer, while you type and in the build; the generator without it | An observable collection of DTO rows is not a `[RunicCollection]` |
| [`RUNICBRIDGE033`](#runicbridge033) | Warning | Build targets | The .NET SDK or Bun is older than this release supports |
| [`RUNICBRIDGE034`](#runicbridge034) | Warning | The analyzer, while you type and in the build; the generator without it | A `[RunicPresentationScoped]` declaration reaches no Bridge command |
| [`RUNICBRIDGE035`](#runicbridge035) | Error | Generator; the analyzer while you type | A `ViewRequest` or `ViewQuestion` member is not a get-only property |
| [`RUNICBRIDGE036`](#runicbridge036) | Error | Generator | A ViewModel does not publish a member of a `[RunicSharedClient]` interface alike, or the interface cannot have a shared client |
| [`RUNICBRIDGE037`](#runicbridge037) | Error | Build targets | The build has no Runic Translations ESM for the page, or its directory holds other files |
| [`RUNICBRIDGE038`](#runicbridge038) | Error | Runtime | Generated Bridges were built against another Runic release than the running runtime |
| [`RUNICBRIDGE039`](#runicbridge039) | Warning | The analyzer, while you type and in the build; the generator without it | A `[RunicOperationClose]` declaration reaches no operation |
| [`RUNICBRIDGE050`](#runicbridge050) | Error | Generator; the analyzer while you type | A `[RunicRowEditor]` method matches no single keyed collection or has an unsupported signature |
| [`RUNICBRIDGE051`](#runicbridge051) | Error | Generator; the analyzer while you type | A keyed collection has more than one `[RunicRowEditor]` method |
| [`RUNICBRIDGE052`](#runicbridge052) | Error | Generator; the analyzer while you type | The rows of a `[RunicRowEditor]` collection cannot be copied with one cell changed |
| [`RUNICBRIDGE053`](#runicbridge053) | Warning | The analyzer, while you type and in the build; the generator without it | An object inside an editable row cannot be copied, so it has no fields below it |
| [`RUNICBRIDGE060`](#runicbridge060) | Error | Generator; the analyzer while you type | A `RunicQueryCollection` member has an unsupported shape |
| [`RUNICBRIDGE065`](#runicbridge065) | Error | Generator; the analyzer while you type | A RunicBlob is read from the page: a command input, an interaction result or a checked field |
| [`RUNICBRIDGE070`](#runicbridge070) | Error | Generator; the analyzer while you type | A `[RunicRegionCollection]` is not on a get-only NavigationSelection property, or its content type is unsupported |
| [`RUNICBRIDGE071`](#runicbridge071) | Error | Generator; the analyzer while you type | A region collection's select or close client method collides with another client member |
| [`RUNICBRIDGE072`](#runicbridge072) | Warning | Generator | A `[RunicDevelopmentOnly]` declaration on an interaction or view request has no effect |
| [`RUNICBRIDGE073`](#runicbridge073) | Error | Generator | A ReactiveUI interaction's `[RunicInteractionTimeout]` is not a timeout |
| [`RUNICBRIDGE074`](#runicbridge074) | Error | Generator | A `[RunicInteractionTimeout]` is on a view question |
| [`RUNICBRIDGE075`](#runicbridge075) | Warning | Build targets | `RunicDevelopmentOnlyViews` or `RunicBridgeBlobs` is neither auto nor true |
| [`RUNICBRIDGE076`](#runicbridge076) | Warning | Generator | A generated client loses its declaration map or comments because an assembly has no PDB |
| [`RUNICBRIDGE077`](#runicbridge077) | Error | Generator | A ViewModel property is marked both [RunicInclude] and [RunicIgnore] |
| [`RUNICBRIDGE078`](#runicbridge078) | Warning | Generator | A [RunicInclude] declaration on a ViewModel member that the Bridge cannot publish has no effect |
| [`RUNICBRIDGE079`](#runicbridge079) | Error | Generator | A RunicApplicationFrontendClientModule names an assembly that is not referenced or not built as a client library |
| [`RUNICBRIDGE080`](#runicbridge080) | Error | Generator | A mapped client library's ViewModel also has Views in another assembly |
| [`RUNICBRIDGE081`](#runicbridge081) | Error | Generator | A mapped client library's ViewModel presents content of another owner, or a shared client's implementers have different owners |
| [`RUNICBRIDGE082`](#runicbridge082) | Error | Build targets; generator | A RunicApplicationFrontendClientModule item has no Specifier, a relative one, or a duplicate Include |
| [`RUNICBRIDGE083`](#runicbridge083) | Warning | Generator | A mapped client library declares no View, so its RunicApplicationFrontendClientModule item does nothing |
| [`RUNICBRIDGE084`](#runicbridge084) | Error | Build targets | The TypeScript emitter cannot run: no Node.js or Bun, or no emitter in the frontend's `@runic-artifex/views` |
| [`RUNICBRIDGE085`](#runicbridge085) | Error | Build targets | A package of a former ID is in the package graph |
| [`RUNICBRIDGE086`](#runicbridge086) | Error | Build targets | An environment variable of a former name is set |
| [`RUNICBRIDGE087`](#runicbridge087) | Error | Build targets | The frontend is below a JavaScript workspace that does not list it |
| [`RUNICBRIDGE088`](#runicbridge088) | Error | Build targets | A file that a frontend package exports does not exist after its build |
| [`RUNICBRIDGE089`](#runicbridge089) | Warning | Build targets | A workspace member declares another package manager than its workspace |
| [`RUNICBRIDGE090`](#runicbridge090) | Error | Build targets | The frontend's package manager or JavaScript runtime does not run |
| [`RUNICBRIDGE091`](#runicbridge091) | Warning | Build targets | `RunicApplicationFrontendInstallMode` is not Auto, Always or Never |

### Errors and warnings while you type

The generator runs after a build's bootstrap compile and is skipped in the
design-time builds that IDEs run, so on its own it reports these errors only
after a build. Since 0.20, `Runic.Application.Views` also ships Roslyn
analyzers that check the rules the source alone shows. Visual Studio, Rider and
VS Code's C# extension report them as you type, with the generator's ID,
message and help link, and offer a code fix where the fix is mechanical. A
project that references `Runic.Application.Views` through a host adapter gets
them too.

They check the ViewModels of the Windows and Views that the project declares,
in a project whose build generates its own Bridges
(`RunicApplicationFrontendGenerationEnabled`, without
`RunicApplicationFrontendModelAssembly`), as the generator does.

| ID | Reported while you type | Code fix |
| --- | --- | --- |
| `RUNICBRIDGE004` | A command whose client member is reserved in TypeScript, such as `NewCommand` or `ThenCommand`; two ViewModels with the same Bridge name; two Views with the same presentation kind; two generated files whose names differ only in case, or a ViewModel named `TypesViewModel`; two state properties with one wire name (`[RunicAlias]`, `[JsonPropertyName]`); two generated client members of one name, such as `setCount` of a settable `Count` and a `SetCountCommand`, or `canReset` of a `CanReset` property and a `ResetCommand`; a state property named `validation` beside generated validation; two interactions of one name | Rename the command, or its CommunityToolkit `[RelayCommand]` method, to the suggested name |
| `RUNICBRIDGE006` | A Window or View that is not public and top-level; a `[RunicViewContract]` name that is not letters and digits; a second View of a ViewModel with the same contract; a content slot with a contract that a ViewModel it admits has no View for | Make the class public |
| `RUNICBRIDGE007` | A ViewModel that does not implement `INotifyPropertyChanged`, is not a public top-level class, or has no state, command or interaction | Make the class public |
| `RUNICBRIDGE008` | A public setter on a `NavigationRegion<T>`, `NavigationSelection`, ViewModel content or ViewModel collection; a `NavigationRegion<object>`; a region whose content no ViewModel with a View has; a list of ViewModels that no View presents; a `[RunicAppendOnlyText]` member that is not a read-only `string`; a blank `[JsonPropertyName]` | Make the setter private |
| `RUNICBRIDGE009` | A command whose name does not end with `Command`; a plain `ICommand` without `[RunicCommandInput]` or `[RunicCommandKey]`; `[RunicCommandResult]` on a command that is not a ReactiveUI command | Add the `Command` suffix |
| `RUNICBRIDGE003` | A value the generator cannot bridge, in a state property, a command input, result, key or failure, an interaction or a view request, with the generator's path, such as `MainViewModel.Box.link: System.Uri is not an explicitly supported bridge value`: unsupported types, collections and dictionaries, recursive or empty DTOs, `[Flags]` enums, wire-name collisions, unions and codecs, and a DTO the page sends that has no usable constructor | None |
| `RUNICBRIDGE010` | A `[RunicCollection]` with a public setter, of nullable or non-DTO rows, or whose key is not a nonnullable `string`, `Guid`, `Int32` or `Int64` row property | Make the setter private |
| `RUNICBRIDGE011` | A ReactiveUI interaction with a public setter | Make the setter private |
| `RUNICBRIDGE012`, `RUNICBRIDGE027` | `[RunicFailure]` or `[RunicCommandKey]` on a ViewModel member that is not a Bridge command or its `[RelayCommand]` method, or on both the command property and its method | None |
| `RUNICBRIDGE012` | A failure type that is `object`, an exception or `Nullable<T>` | None |
| `RUNICBRIDGE027` | `[RunicCommandKey]` beside `[RunicCommandInput]` or on a command without a parameter; a resolver that is missing, overloaded, private or protected, takes other parameters than the key (and a `CancellationToken` when asynchronous), or does not return the command's parameter | None |
| `RUNICBRIDGE025` | A ReactiveUI command or interaction without a Runic ReactiveUI adapter, or an interaction of the other flavor than the adapter's | None |
| `RUNICBRIDGE035` | A `ViewRequest<T>` or `ViewQuestion<TInput, TOutput>` that is not get-only | Make the setter private |
| `RUNICBRIDGE060` | A `RunicQueryCollection<TRow, TKey>` with a public setter, with `[RunicCollection]` or `[RunicAppendOnlyText]`, with a key that is not a `string`, `Guid` or `Int32`, or with rows that are no DTOs | Make the setter private |
| `RUNICBRIDGE065` | A `RunicBlob` the page would send: a command input, an interaction result or a settable member | None |
| `RUNICBRIDGE050`, `RUNICBRIDGE051`, `RUNICBRIDGE052` | A `[RunicRowEditor]` that matches no single keyed collection, names a query collection or has another signature than `void Edit(TKey key, TRow row)` (and `RunicRowEdit`); two editors of one collection; rows that cannot be copied with one cell changed | None |
| `RUNICBRIDGE070` | `[RunicRegionCollection]` on a property that is not a `NavigationSelection`, on one with a public setter, with the content type `object`, or with content that no ViewModel with a View has | Make the setter private |
| `RUNICBRIDGE071` | A region collection's `select{Name}` or `close{Name}` client method that another client member already uses | None |
| `RUNICBRIDGE013` (warning) | A ViewModel without a View that a content slot or navigation region admits, at that ViewModel | None |
| `RUNICBRIDGE028`, `RUNICBRIDGE039` (warnings) | `[RunicOperationRetirement]` or `[RunicOperationClose]` that reaches no operation, or whose value is not one of the enum's | None |
| `RUNICBRIDGE031` (warning) | The ViewModel or a type of its values implements an `IValidatableViewModel` that is not ReactiveUI.Validation's | None |
| `RUNICBRIDGE032` (warning) | An observable collection of DTO rows that is not a `[RunicCollection]` | Declare `[RunicCollection(nameof(Row.Id))]` when the rows have a nonnullable `Id` or `Key` |
| `RUNICBRIDGE053` (warning) | An object inside an editable row that cannot be copied, so it has no fields below it | None |
| `RUNICBRIDGE034` (warning) | `[RunicPresentationScoped]` on a member that is not a Bridge command or its `[RelayCommand]` method, also in a base class | None |

A private setter keeps the property assignable from the ViewModel and takes it
off the page. A rename of a CommunityToolkit command renames its method, so the
generated command property changes its name too.

The generator stays the authority. It checks everything else after a build,
such as shared clients (`RUNICBRIDGE036`, also their module names), Native AOT
(`RUNICBRIDGE002`), types it cannot load, and the ViewModels of another
assembly, and it reports these errors as before. The analyzer builds each
value's graph from the source as the generator builds it from the assembly; a
message that names a constructed generic type, such as
``HashSet`1[[System.String, System.Private.CoreLib, ...]]``, may name another
assembly for a type argument that the runtime forwards, where the generator
names the one it loaded. A build reports each error once: the generator runs before the compiler
and stops the build at its first error for each ViewModel, so the compiler,
which runs the analyzer, never reaches them. The analyzer reports every problem
it finds at once, where the generator reports a ViewModel's first.

The warnings in the table are the analyzer's in the build too. When the
project's compile runs the analyzer, the generator leaves these warnings about
the project's own declarations to it, so the compiler reports each once, in the
build and while you type. Like any compiler warning they follow `NoWarn`,
`TreatWarningsAsErrors`, `WarningsAsErrors`, `.editorconfig` severities
(`dotnet_diagnostic.RUNICBRIDGE013.severity = none`) and `#pragma warning
disable`, and they are reported only when the compiler runs, not on a build that
finds the project up to date. The generator reports them itself when analyzers
are off (`RunAnalyzers=false`, `RunAnalyzersDuringBuild=false`, or a build that
Visual Studio starts for running or testing), for a `RunicApplicationFrontendModelAssembly`
or a ViewModel of another assembly, and next to an error, which stops the build
before the compiler. Before 0.20 the generator reported them in every build,
and `TreatWarningsAsErrors`, `.editorconfig` and `#pragma` did not apply to
them. The generator's other warnings (`RUNICBRIDGE020`, `072` and `076`) need the compiled
assembly and appear only after a build. They, and the build targets' warnings, are
MSBuild warnings: since 0.20 a project with `TreatWarningsAsErrors` makes them
errors too, unless `WarningsNotAsErrors` or `NoWarn` names them, and
`WarningsAsErrors` makes a named one an error as before. `.editorconfig` and
`#pragma` do not reach them.

### RUNICBRIDGE001

The generator invocation or build configuration is invalid, the generator cannot
write its output, or the generator is missing from the `Runic.Application.Views`
package. An example is a malformed `RunicApplicationFrontendCompositionType`.
Before 0.10 this code also covered internal generator errors, which are now
[`RUNICBRIDGE024`](#runicbridge024).

**Fix:** correct the build property, or make the generated directories
writable. A missing generator means an incomplete restore: restore the project
again.

### RUNICBRIDGE002

A CommunityToolkit `ObservableValidator` ViewModel is in a Native AOT publish.
Its validation is not supported under Native AOT.

**Fix:** publish framework-dependent until its validation is verified under AOT.

### RUNICBRIDGE003

A state, command or interaction value type is not a supported bridge value. The
message names the member path, for example `EditorViewModel.Current.value`.

**Fix:** use a supported scalar, collection, public DTO, `[RunicUnion]` or
`[RunicCodec]` type. Exclude computed DTO properties with
`[RunicIgnore]` or an unconditional `[JsonIgnore]`, or remove `[assembly: RunicIncludeJsonIgnored]`, as described under
[Shared DTO exclusions](../packages/dotnet/Runic.Application.Views/README.md#shared-dto-exclusions).
A `NavigationResult<T>` holds .NET content: return its `Outcome`, a
`NavigationOutcome`, instead.

### RUNICBRIDGE004

Two generated names collide: ViewModel names, presentation kinds, state wire
names, the reserved `validation` field, interactions, view requests, client
members, or generated files (including a hand-written `types.ts`). Since 0.13 (Views
protocol 4) requests name their kind and member, so host route names no longer
collide, and a state member may be named `revision`. A
generated TypeScript module that declares one name twice would collide too.
Since 0.10 the generated C# bridges are in the
`Runic.Application.Views.Generated.Bridges` namespace, so a type of yours named
like one (`CounterBridge` beside `CounterViewModel`) no longer collides.

A command whose client member is reserved in a TypeScript object collides with
the object itself: `new` (a construct signature), `then` (a thenable client),
`constructor`, `__proto__`, `toJSON`, `toString` or `valueOf`.

A command without an argument publishes its availability as `canX` state, so a
state property of that wire name collides with it, unless the command declares
the property as its availability with CommunityToolkit's
`[RelayCommand(CanExecute = nameof(CanX))]`; then the property is written once,
as the availability.

The generator's own TypeScript bindings, such as the client's wire type,
runtime imports and page reference caches, never collide with names derived
from your types: when a ViewModel is named `WireViewModel` or `ViewViewModel`,
the module uses `_WireState` or imports `ViewClient as _ViewClient` instead.

A `[RunicSharedClient]` interface's shared client declares `{Name}PageReference`,
which every implementing ViewModel's module imports. When that is also the page
reference of a presentation kind, such as `OrdersPagePageReference` for the
shared client `OrdersPage` and `OrdersView` with `[RunicViewContract("Page")]`,
or `OrdersPageReference` for an interface `IOrders` beside `OrdersViewModel`,
the error asks for another name: set `[RunicSharedClient(Name = "…")]`.

**Fix:** rename one member or type, or set a wire name with `[RunicAlias]`.
Rename a reserved command; the message suggests a name, such as `CreateCommand`
for `NewCommand`.

### RUNICBRIDGE005

The model assembly or one of its dependencies could not be loaded, a Runic View
could not be loaded, or a ViewModel member that the Bridge exposes has a type
from an assembly the generator cannot load. The generator runs on
Microsoft.NETCore.App and loads the shared frameworks that the build references,
such as Microsoft.AspNetCore.App, but never Microsoft.WindowsDesktop.App: types
that need WPF or Windows Forms are skipped unless they are Views, and a bridged
member with such a type is reported at that member.

**Fix:** mark a WPF- or Windows Forms-typed ViewModel member `[RunicIgnore]`, or
expose a Bridge value instead. Otherwise check the bootstrap output and that
every Runic package has the same version. `dotnet runic doctor` reports packages
from different releases.

### RUNICBRIDGE006

A Window or View class, or its View contract, is invalid. The assembly has no
Window or View class (an error only when generation is required, see
[Build properties](../packages/dotnet/Runic.Application.Views/README.md#build-properties)),
one is not public, top-level, concrete and closed, or a View contract is
invalid, duplicated or missing.

**Fix:** make the class public and top-level. Give each `[RunicViewContract]` a
unique letters-and-digits name. When the assembly has no Window or View, declare
one, or, in a project that only uses another project's generated Bridges, turn
generation off with `<RunicApplicationFrontendGenerationEnabled>false</RunicApplicationFrontendGenerationEnabled>`.
A test project does not need this unless it sets one of the properties that
require generation: generation stays off in a project with `IsTestProject`
(Microsoft.NET.Test.Sdk and TUnit set it) and, since 0.14, in one that
references `Runic.Application.Testing` directly, such as a console test runner,
when it declares no Window or View of its own.

### RUNICBRIDGE007

A ViewModel class has an unsupported shape. It does not implement
`INotifyPropertyChanged`, is not a public top-level class, or has no state,
command or interaction.

**Fix:** change the ViewModel declaration.

### RUNICBRIDGE008

A state property or ViewModel content member has an unsupported shape. A state
property has no public getter or an empty wire name, ViewModel content, a
`NavigationRegion<TContent>` slot or a `NavigationSelection` slot has a public
setter, a ViewModel collection's item type has no registered View, or a region
slot is a `NavigationRegion<object>` or has no ViewModel with a registered View
that is a `TContent`, or a `[RunicAppendOnlyText]` member is not a read-only
`string` or `string?` state property.

**Fix:** add a getter, make content read-only to the web view (the web view
selects a `NavigationSelection`'s region through a command that calls
`SelectAsync`), register a View for the item or content type, give the region a
specific content interface or base class, or make an append-only text a
read-only `string` or `string?`, or remove the attribute.

### RUNICBRIDGE009

A command has an unsupported name or shape. Its name does not end with
`Command`, its shape is unsupported, or a non-ReactiveUI command has
`[RunicCommandResult]`.

**Fix:** rename the command, or use a supported CommunityToolkit, ReactiveUI or
`[RunicCommandInput]` command.

### RUNICBRIDGE010

A `[RunicCollection]` member is not a keyed collection of DTO rows: it is not a
read-only, non-nullable collection of DTO rows, or its key is not a non-nullable
`string`, `Guid` or `Int32` row property.

**Fix:** change the collection or its key.

### RUNICBRIDGE011

A ReactiveUI interaction has no public getter or has a public setter. An
interaction's `[RunicInteractionTimeout]` that is not a timeout is
[`RUNICBRIDGE073`](#runicbridge073).

**Fix:** expose the interaction as a get-only property.

### RUNICBRIDGE012

A `[RunicFailure]` declaration is misplaced, repeated or names an unsupported
type. It is on a member that is not a Bridge command or its `[RelayCommand]`
method, is on both the property and the method, or names `object`, an
exception or `Nullable<T>`.

**Fix:** declare one failure type per command: any Bridge value type, typically
a DTO, an enum or a `[RunicUnion]`. A failure type the Bridge cannot encode is
`RUNICBRIDGE003` at `{Model}.{Command}.failure`.

### RUNICBRIDGE013

A warning. A public, concrete ViewModel in the application assembly fits a
ViewModel content property, ViewModel collection or `NavigationRegion<TContent>`
slot but has no View, and no View of a base class that the slot admits presents
it, so pushing or presenting it there throws `NotSupportedException` naming the
slot and the type. Broad slot types are not checked: framework types (System,
ReactiveUI, CommunityToolkit), a type the slot's owner or a Window's ViewModel
also derives from or implements, and a type that more than eight ViewModels
without a View could fill. The analyzer reports it at that ViewModel while you
type and in the build (see
[Errors and warnings while you type](#errors-and-warnings-while-you-type)).

**Fix:** add a View, such as
`public sealed partial class SettingsView : RunicView<SettingsViewModel>;`, with
the slot's `[RunicViewContract]` if it has one. If the type is never presented
there, suppress the warning with `<NoWarn>$(NoWarn);RUNICBRIDGE013</NoWarn>`.

### RUNICBRIDGE014

The `Runic.Application.Views` build targets report this error, not the generator. A
project declares Runic Views and builds its frontend, but `RunicApplicationFrontendDirectory`
does not exist or, with the default frontend build command, has no
`package.json`. The generated TypeScript client would have no package to build
in.

**Fix:** create the frontend in that directory, set
`RunicApplicationFrontendDirectory` to your frontend package directory, or set
`RunicApplicationFrontendBuildEnabled=false` when another tool builds the frontend.

### RUNICBRIDGE015

A warning from the `Runic.Application.Views` build targets. A project declares a
Runic Window or View, but Bridge generation is off by default because it
references no host adapter (`Runic.Application.Views.Desktop`,
`Runic.Application.Views.CsWebUi` or `Runic.Application.Views.Wpf`) and does not
reference `Runic.Application.Views` directly. No bridge, `AddRunicViews()` or
TypeScript client is generated for it.

**Fix:** set `RunicApplicationFrontendGenerationEnabled=true` to generate in that
project, or `RunicApplicationFrontendGenerationEnabled=false` when another project
generates its Views, which also silences the warning.

### RUNICBRIDGE016

The `Runic.Application.Views` build targets report this error, not the generator. The
frontend install or build command failed without reporting an error in the
canonical `file(line,column): error CODE: text` form (a Vite or package-manager
error, for example), or the install created no `node_modules` for declared
dependencies. The command's own output is printed above the error.

Errors that `tsc` and `vue-tsc` print are logged with their codes and
locations instead, and those of a failed `svelte-check` as
`file(line,column): error : text`, so an IDE links them to the source line.

**Fix:** fix the problem the command reports, or run `dotnet runic doctor` to
check the package manager and lock file.

### RUNICBRIDGE017

An error from the `Runic.Application.Views` build targets. The project, one of
its imports (such as `Directory.Build.props`) or the command line sets a build
property or item by a former name, which no longer applies:

- the `RunicBridge*` and `RunicViewsWindowProject` names of 0.7.0-preview.6 and
  earlier; since 0.7.0-preview.7 every property of these targets starts with
  `RunicApplicationFrontend`. The
  [renamed-names table](../packages/dotnet/Runic.Application.Views/README.md#moved-and-renamed-since-070-preview6)
  lists every rename.
- `RunicApplicationId` without `ApplicationId`, and `RunicApplicationName`
  without `ApplicationTitle`, which the `runic-app` template of 0.8 and earlier
  declared. The application's identity is MSBuild's `ApplicationId` and
  `ApplicationTitle`, which the template sets since 0.9 and which
  `Runic.Platform.MacOS` and `Runic.Platform.Linux.Polkit` read.
- `RunicApplicationFrontendReactiveUiFlavor` (and its former name
  `RunicBridgeReactiveUiFlavor`), removed in 0.10. The build infers the
  ReactiveUI flavor from the Runic ReactiveUI adapter the project references:
  System.Reactive with `Runic.Application.Views.ReactiveUI.Reactive`, otherwise
  ReactiveUI.Primitives.

Until 0.9 this was a warning and the old name still applied. The message names
the new property. Since 0.24 the renamed names are the MSBuild list of the
old-name guards (D079.11, `Runic.Sdk.RenamedNames.targets`, generated from
`packages/dotnet/Runic.Application.Views/rename/old-names.json`), which
`Runic.Desktop` ships too; `RUNICBRIDGE085` and `RUNICBRIDGE086` report old
package IDs and environment variables.

**Fix:** remove `RunicApplicationFrontendReactiveUiFlavor`. Rename any other
property or item, for example
`<RunicBridgeFrontendDir>` to `<RunicApplicationFrontendDirectory>` or
`<RunicApplicationId>` to `<ApplicationId>`. A condition on
`RunicBridgeBootstrap` should read `RunicApplicationFrontendBootstrap`; the
bootstrap pass no longer sets the old name.

### RUNICBRIDGE018

The `Runic.Application.Views` build targets report this error. A project
declares Runic Views and generates its composition
(`RunicApplicationFrontendCompositionType`), but a referenced assembly already
declares a type of the same name. Both would define `AddRunicViews()`, so the
call would be ambiguous (`CS0121`). A typical case is a WPF application with a
separate model project: the WPF adapter generates in the application, whose
default composition name is `<RootNamespace>.RunicViewsComposition`, and the
model project uses that name for its own composition. A project without Views
of its own is not affected; its bootstrap build uses the referenced type.

**Fix:** set `RunicApplicationFrontendCompositionType` to another type name in
one of the two projects, and call `AddRunicViews()` of each composition.

### RUNICBRIDGE019

A warning from the `Runic.Application.Views` build targets. The build cannot
tell which host's Views client to copy to `www/runic-views.js`, the
host-neutral script a page loads, and copies none, so a page that loads it gets
no Bridge. It is reported when an application references more than one host
adapter (`Runic.Application.Views.Desktop` or `Runic.Application.Views.Wpf`, and
`Runic.Application.Views.CsWebUi`) without `RunicApplicationHost` and a page it
copies to `www/` loads `runic-views.js`, or when `RunicApplicationHost` names a
host that the application does not reference. Libraries and test projects
report nothing.

Since 0.23 it is also reported when a page that the build copies to `www/`
loads `runic-desktop-views.js` or `runic-cswebui.js`, the host-named copies of
the client that 0.22 deprecated and 0.23 no longer copies (D072.11): the page
then gets a 404 for the script and no Bridge, whichever hosts the application
references. A frontend that ships its own file under that name reports nothing
for it.

**Fix:** set `RunicApplicationHost` to `desktop` or `cswebui` in the project or
on the command line, for example `dotnet run -p:RunicApplicationHost=desktop`,
to the host that the application runs on. For a page that loads a host-named
copy, load `runic-views.js` instead (`<script src="runic-views.js"></script>`
after `webui.js`).

### RUNICBRIDGE020

A warning. The generator skipped types of the application assembly because an
assembly they need could not be loaded, and that assembly is not a Windows
Desktop (WPF or Windows Forms) one, whose types are skipped without a warning.
None of the skipped types is a Runic View, but the assembly may also be missing
when the application runs. The generator loads the assemblies of the shared
frameworks that the build references, such as Microsoft.AspNetCore.App. With
optional generation and no Views, it reports `RUNICBRIDGE005` instead and keeps
the generated output. The warning repeats on builds that reuse the cached
generation.

**Fix:** reference the shared framework or package that contains the assembly,
or make the assembly available in the build output.

### RUNICBRIDGE021

The `Runic.Application.Views` build targets report this error. The project sets
`RunicApplicationFrontendNodeModulesDirectory` to a `node_modules` that
something else installed, such as the Nix builder, so the build links the
frontend's `node_modules` to it instead of installing. The directory does not
exist, the frontend already has a `node_modules` that is not a symbolic link,
or the link could not be created. An installed `node_modules` is never
replaced.

**Fix:** install the dependencies in that directory, remove the frontend's own
`node_modules`, or unset the property to install with the frontend's package
manager.

### RUNICBRIDGE022

The `Runic.Application.Views` build targets report this error. The project is
built with `NoBuild=true`, as by `dotnet publish --no-build`, which reuses the
Bridge code that the earlier build generated in `runic-bridge/` in the
project's `IntermediateOutputPath` (`obj/<Configuration>/<TargetFramework>/`, or
`obj/<Configuration>/<TargetFramework>/<RuntimeIdentifier>/` with a runtime
identifier) instead of running the generator. That code, or the composition that
`RunicApplicationFrontendCompositionType` names, is not there.

**Fix:** build the project first with the same `Configuration`,
`TargetFramework`, `RuntimeIdentifier` and `PublishAot`, then publish with `--no-build`, or publish
without `--no-build`.

### RUNICBRIDGE023

The `Runic.Application.Views` build targets report this error. The project's
resolved references, direct or through another package or project, combine
Runic ReactiveUI adapters of both flavors: `Runic.Application.Views.ReactiveUI.Reactive`
or `Runic.Navigation.ReactiveUI.Reactive`, which use ReactiveUI's System.Reactive
flavor, with `Runic.Application.Views.ReactiveUI` or `Runic.Navigation.ReactiveUI`,
which use ReactiveUI.Primitives. An application uses one flavor. Until 0.9 the
`Runic.Application.Views.ReactiveUI.Reactive` targets checked only the project's
own references, and not `Runic.Navigation.ReactiveUI.Reactive` with
`Runic.Application.Views.ReactiveUI`.

**Fix:** keep one flavor: with the System.Reactive flavor, reference
`Runic.Application.Views.ReactiveUI.Reactive` and `Runic.Navigation.ReactiveUI.Reactive`;
otherwise `Runic.Application.Views.ReactiveUI` and `Runic.Navigation.ReactiveUI`.
The message names the pair; a reference that arrives through another project
or package is changed there.

### RUNICBRIDGE024

The generator failed internally: it threw an exception that is not one of the
diagnostics above, or it exited with an error without reporting one, which the
`Runic.Application.Views` build targets then report. The message names the
ViewModel the generator was inspecting, when it knows it, and the exception
type; the stack trace follows on the next lines of the build output. Until 0.9
this was part of `RUNICBRIDGE001`.

**Fix:** this is a bug in the generator. Report it with the ViewModel that
triggers it and the build output, including the stack trace. Until it is
fixed, changing the member the stack trace points at may avoid it.

### RUNICBRIDGE025

A ViewModel exposes a ReactiveUI command (`IReactiveCommand<TParam, TResult>`)
or interaction (`ReactiveUI.Binding.IInteraction<TInput, TOutput>` or its
System.Reactive counterpart), but the project the Bridge is generated for
references no Runic ReactiveUI adapter, whose helpers the generated Bridge
calls. The build takes the ReactiveUI flavor from the adapter it references, so
the error also reports an interaction whose interface is of the other flavor
than the adapter. Until 0.9 both were C# compile errors in the generated code.

**Fix:** reference `Runic.Application.Views.ReactiveUI`, or
`Runic.Application.Views.ReactiveUI.Reactive` for ReactiveUI's System.Reactive
flavor, in the project that generates the Bridge. For a flavor mismatch, use
the interaction interface of the adapter's flavor
(`ReactiveUI.Binding.IInteraction` with `Runic.Application.Views.ReactiveUI`,
`ReactiveUI.Binding.Reactive.IInteraction` with
`Runic.Application.Views.ReactiveUI.Reactive`).

### RUNICBRIDGE026

The `Runic.Application.Views` build targets report this error before the
frontend install runs. The frontend's `package.json` declares dependencies, the
build would install them with the default command, and the selected package
manager has no lock file: `package-lock.json` or `npm-shrinkwrap.json` for npm,
`pnpm-lock.yaml` for pnpm, `bun.lock` or `bun.lockb` for Bun. Without a lock
file every build, including CI, would resolve the declared version ranges afresh,
so a newly published version of a dependency, malicious or not, would run in the
frontend build. `--ignore-scripts` stops only install scripts, not a build plugin.
Until 0.10 the build fell back to an unlocked `npm install`.

Since 0.24 a frontend that is a member of a JavaScript workspace installs at the
workspace root, so the lock file is the root's: the message then names the
workspace (`…/App/Frontend is a member of the bun workspace at /repo, which has
no bun.lock`). See
[JavaScript workspaces](../packages/dotnet/Runic.Application.Views/README.md#javascript-workspaces).

**Fix:** run the package manager's install once (`npm install`, `pnpm install`
or `bun install`) and commit the lock file it writes: in the frontend's folder,
or, for a workspace member, in the workspace root. To install without a lock
file anyway, set `RunicApplicationFrontendAllowUnlockedInstall` to `true`, which
runs `npm install`, `pnpm install` or `bun install` with `--ignore-scripts`. A
project that sets `RunicApplicationFrontendInstallCommand`,
`RunicApplicationFrontendNodeModulesDirectory` or
`RunicApplicationFrontendInstallEnabled=false` is not checked.

### RUNICBRIDGE027

A `[RunicCommandKey]` declaration (since 0.11) is misplaced, or its resolver
cannot be called the way the generated Bridge calls it:

- it is on a member that is not a Bridge command or its `[RelayCommand]` method,
  or on both the property and the method;
- the command takes no parameter (a ReactiveUI command whose input is `RxVoid`
  or `Unit`, a non-generic CommunityToolkit command);
- it is combined with `[RunicCommandInput]`;
- the named method does not exist on the ViewModel or its base classes, is
  overloaded, or is `private` or `protected`;
- the method does not take exactly the key (plus, when asynchronous, an optional
  `CancellationToken`), or does not return the command's parameter type, directly
  or as `Task<T>` or `ValueTask<T>`.

**Fix:** name one method of the ViewModel, `public` or `internal`, instance or
static, such as `Item? FindItem(ulong id)` or
`ValueTask<Item?> FindItemAsync(ulong id, CancellationToken cancellationToken)`,
and put the attribute on the command property or on its `[RelayCommand]` method
only. A plain `ICommand` takes `[RunicCommandKey]` instead of
`[RunicCommandInput]`. A key type that is not a Bridge value is
`RUNICBRIDGE003` at `{Model}.{Command}.key`. See
[Commands whose parameter has no wire form](../packages/dotnet/Runic.Application.Views/README.md#commands-whose-parameter-has-no-wire-form).

### RUNICBRIDGE028

A warning from the Bridge generator (since 0.11). A `[RunicOperationRetirement]`
declaration has no effect:

- it is on a ViewModel member that is not a Bridge command that starts an
  operation: a state property, a ReactiveUI interaction, or a synchronous
  command (a non-async CommunityToolkit command, a plain `ICommand`), which
  never runs as an operation;
- it is on a ViewModel class that has no asynchronous Bridge command;
- its value is not a `RunicOperationRetirement` member; the declaration is
  ignored.

The generated Bridge is the same as without the declaration.

**Fix:** put it on an asynchronous command property (for a CommunityToolkit
`[RelayCommand]` method, `[property: RunicOperationRetirement(...)]`), or on
the class whose operations it should cover, or remove it. See
[Operations and navigation entries](../packages/dotnet/Runic.Application.Views/README.md#operations-and-navigation-entries).

### RUNICBRIDGE029

A warning from the `Runic.Application.Views` build targets.
`RunicApplicationFrontendPackageManager` has a value other than `npm`, `pnpm`
or `bun`, compared without case, such as `yarn`. The build then treats it as
npm: the default install and build commands are npm's (`npm ci` and
`npm run build`), and [`RUNICBRIDGE026`](#runicbridge026) looks for
`package-lock.json`. It is reported for a frontend with a `package.json` that
the build installs or builds with a default command; a project that sets both
`RunicApplicationFrontendInstallCommand` and
`RunicApplicationFrontendBuildCommand`, or turns the frontend build off, is not
checked. An empty value selects the default and is not reported. Since 0.11;
earlier releases did not report the value.

**Fix:** set `RunicApplicationFrontendPackageManager` to `npm`, `pnpm` or
`bun`, or remove it to follow `packageManager` in `package.json`, then the lock
file. For another package manager, set `RunicApplicationFrontendInstallCommand`
and `RunicApplicationFrontendBuildCommand` to its commands.

### RUNICBRIDGE030

A warning from the `Runic.Application.Views` build targets.
`RunicApplicationFrontendDelivery` cannot take effect, so the build copies the
frontend to the web root (`www/`), as `Files` does. It is reported when:

- the value is neither `Files` nor `Embedded`, compared without case;
- the value is `Embedded`, but the application runs on no single Runic Desktop
  or CS-WebUI host (a project that references several hosts sets
  `RunicApplicationHost` to `desktop` or `cswebui`); before 0.15 the CS-WebUI
  host was reported too;
- the value is `Embedded`, but the project sets `RunicAssetsDist` or
  `RunicAssetsEmbeddedArchive` itself.

A build that does not copy the frontend (`RunicApplicationFrontendCopyEnabled=false`,
as `dotnet runic dev` sets it for a development server) uses `Files` without a
warning. Since 0.11.

**Fix:** set `RunicApplicationFrontendDelivery` to `Files` or `Embedded`. Use
`Embedded` with `Runic.Application.Views.Desktop` or
`Runic.Application.Views.CsWebUi`, and remove `RunicAssetsDist` and
`RunicAssetsEmbeddedArchive` from the project: `Embedded` sets them itself.
To serve another archive, set `RunicDesktopHostOptions.Assets` or
`RunicCsWebUiHostOptions.Assets`.

### RUNICBRIDGE031

A warning from the generator. A ViewModel, or a type in its state graph,
implements an interface named `IValidatableViewModel` that is not the one of
`RunicArtifex.ReactiveUI.Validation` (`ReactiveUI.Validation.Abstractions`) or
of its System.Reactive flavor (`ReactiveUI.Validation.Reactive.Abstractions`).
The Bridge reads and observes only those flavors' `ValidationContext`, so the
type's validation reaches the page only through `INotifyDataErrorInfo`, and not
at all if it does not implement it. The warning names the ViewModel, the type
and the interface. Since 0.14.

**Fix:** use `RunicArtifex.ReactiveUI.Validation` or
`RunicArtifex.ReactiveUI.Validation.Reactive`, or implement
`INotifyDataErrorInfo` on the type. See
[Structured validation](../packages/dotnet/Runic.Application.Views/README.md#structured-validation).

### RUNICBRIDGE032

A warning from the Bridge generator (since 0.14). A state property is an
observable collection of DTO rows, such as `ObservableCollection<Row>` or
`ReadOnlyObservableCollection<Row>`, but not a `[RunicCollection]`. Such a
member is published by comparison: each change of the collection, or of a row
the Bridge observes, encodes the whole list, compares it with what the page has
and sends it whole when it differs. At thousands of rows every edit costs
milliseconds and megabytes. A `[RunicCollection]` sends only the rows that
were added, removed, moved or replaced. A plain list or array, which the
ViewModel replaces, is not reported.

**Fix:** declare the key of the rows, for example
`[RunicCollection(nameof(Row.Id))]`, with a non-nullable `string`, `Guid` or
`Int32` row property that does not change during a row's lifetime (see
[`RUNICBRIDGE010`](#runicbridge010)). For a list that stays small, suppress the
warning with `<NoWarn>$(NoWarn);RUNICBRIDGE032</NoWarn>`.

### RUNICBRIDGE033

A warning from the `Runic.Application.Views` build targets. The build runs on a
.NET SDK older than 10.0.404, or builds a Bun frontend with a Bun older than
1.4.4: the oldest versions that this release supports, with the November 2026
.NET and October 2026 Bun security fixes, and that `dotnet runic doctor` accepts. The SDK is checked on
every build; Bun when the build runs the frontend build of a project whose
package manager is Bun. Older ones still build, but a self-contained or
NativeAOT package embeds the runtime of the SDK. Under Nix,
`buildRunicApplication` uses your nixpkgs' `dotnetCorePackages.sdk_10_0` and
`bun` unless you pass `dotnet-sdk`, and the warning shows while nixpkgs still
packages older ones. Since 0.14; the SDK floor is 10.0.404 since 0.24 (10.0.403 before).

**Fix:** install .NET SDK 10.0.404 or a newer 10.0 release (runtime 10.0.15),
or the .NET 11 SDK with the .NET 10 runtime, and Bun 1.4.4 or a newer 1.x release, then rebuild and republish. Until you
can, suppress the warning with `<NoWarn>$(NoWarn);RUNICBRIDGE033</NoWarn>`.

### RUNICBRIDGE034

A warning from the Bridge generator (since 0.15). `[RunicPresentationScoped]`
is on a member that is not a Bridge command property or the CommunityToolkit
`[RelayCommand]` method of one, such as a state property, a helper method or a
base-class member the Bridge does not expose. The declaration has no effect: the
generated client sends no presentation epoch for anything it would have scoped.

**Fix:** put the attribute on the command property, or on the `[RelayCommand]`
method whose command the Bridge exposes. See
[Commands scoped to their presentation](../packages/dotnet/Runic.Application.Views/README.md#commands-scoped-to-their-page).

### RUNICBRIDGE035

A `ViewRequest<T>` or `ViewQuestion<TInput, TOutput>` member of a ViewModel
(since 0.16) has no public getter or has a public setter. The generated Bridge
attaches the instance it reads when the ViewModel is presented, so a replaced
instance would never reach the page.

`[RunicInteractionTimeout]` on a view question is
[`RUNICBRIDGE074`](#runicbridge074).

**Fix:** expose the member as a get-only property, such as
`public ViewRequest<FocusTarget> Focus { get; } = new();`. See
[View requests](../specs/application/README.md#view-requests).

### RUNICBRIDGE036

An error from the Bridge generator (since 0.17). A ViewModel with a View
implements an interface marked `[RunicSharedClient]`, and the generator cannot
write the interface's shared client, because:

- the ViewModel does not publish a property of the interface: it implements it
  explicitly, inherits it from a base class, uses the interface's default
  implementation, marks it `[RunicIgnore]`, or publishes it under another
  member, such as the availability of a command;
- the ViewModel's property has other nullability than the interface's, such as
  `string` where the interface declares `string?`;
- its generated TypeScript differs from the other implementing ViewModels':
  another wire name (`[JsonPropertyName]`), command input (`[RunicCommandInput]`,
  `[RunicCommandKey]`) or declared failure (`[RunicFailure]`). The error names
  the ViewModel that differs from the most common declaration and shows both;
  a property of an interface that another `[RunicSharedClient]` interface
  extends is reported once;
- the property is settable in the interface, but the page cannot set it, or it
  is an interaction or a view request, which a shared client does not include;
- the interface is generic, declares a property name twice through the
  interfaces it extends, has a write-only property, or its `Name` is not a
  letter followed by letters and digits.

Without the attribute the build stays the same: an interface that is not marked
is never checked.

**Fix:** declare the property on the ViewModel class as a public property with
the interface's type and nullability and the same attributes as the other
implementing ViewModels, remove the member from the interface, or give a
generic interface a non-generic one for its shared client. See
[Shared clients](../packages/dotnet/Runic.Application.Views/README.md#shared-clients).

### RUNICBRIDGE037

An error from the `Runic.Application.Views` build targets (since 0.17). The
project sets `RunicApplicationFrontendTranslations=true`, which keeps
`RunicApplicationFrontendTranslationsDirectory` (by default
`.runic/translations` in the frontend directory) equal to the ESM that
`Runic.Translations.Build` writes for the project's catalog, and one of these
holds:

- The build wrote no ESM: `Runic.Translations.Build` is not referenced, the
  project declares no `RunicTranslationProject`, or `RunicTranslationsEmitEsm`
  is not `true`. The page would import a missing catalog, or the one of an
  earlier build.
- The directory holds files but no Runic Translations ESM (no
  `web-module-manifest-v*.json`). The build deletes the files there that the
  catalog does not have, so it stops instead.

A catalog with errors is not `RUNICBRIDGE037`: the build reports the catalog's
errors and leaves the directory as it is.

**Fix:** reference `Runic.Translations.Build`, declare the catalog's
`runic.json` as a `RunicTranslationProject` and set
`<RunicTranslationsEmitEsm>true</RunicTranslationsEmitEsm>`; or point
`RunicApplicationFrontendTranslationsDirectory` at a directory of its own. A page
that gets the catalog from the Translations Vite plugin does not need
`RunicApplicationFrontendTranslations`. See
[Translated text](../packages/dotnet/Runic.Application.Views/README.md#translated-text).

### RUNICBRIDGE038

An error of the `Runic.Application.Views` runtime (since 0.19), thrown as
`RunicBridgeContractException`, whose `AssemblyName` names the library. An
assembly's generated Bridges were built against another Runic release than the
running `Runic.Application.Views`, and the generated-code support API they call
has changed in between:

- The generated `AddRunicBridges()` (and `AddRunicViews()`, which calls it)
  checks the generated-code contract version that its generator wrote before it
  registers a Bridge. The version changes only when the support API breaks
  binary compatibility, in a major release (and in 0.19), so Bridges that an
  earlier minor release of the same major generated keep running.
- Bridges from before 0.19 make no such check. When the first Window or View
  attaches one, the host reports the `TypeLoadException` or
  `MissingMethodException` it throws as this error, with that exception as the
  inner exception.

It is common after an upgrade that rebuilt the application but not a library
it references: a separate `RunicApplicationFrontendModelAssembly`, or a
ViewModel library from another solution or a NuGet package.

**Fix:** rebuild the assembly the message names against the Runic release the
message names, which regenerates its Bridges, and use the same version of every
Runic package. A library built against a newer Runic needs the application's
Runic packages updated to that release instead. See
[From 0.18 to 0.19](migration-1.0.md#from-018-to-019).

### RUNICBRIDGE039

A warning from the Bridge generator (since 0.19). A `[RunicOperationClose]`
declaration has no effect:

- it is on a ViewModel member that is not a Bridge command that starts an
  operation: a state property, a ReactiveUI interaction, or a synchronous
  command (a non-async CommunityToolkit command, a plain `ICommand`), which
  never runs as an operation;
- it is on a ViewModel class that has no asynchronous Bridge command;
- its value is not a `RunicOperationClose` member; the declaration is ignored.

The generated Bridge is the same as without the declaration.

**Fix:** put it on an asynchronous command property (for a CommunityToolkit
`[RelayCommand]` method, `[property: RunicOperationClose(...)]`), or on the
class whose operations it should cover, or remove it. See
[Operations that outlive a window](../packages/dotnet/Runic.Application.Views/README.md#operations-that-outlive-a-window).

### RUNICBRIDGE050

An error from the Bridge generator (since 0.16). A `[RunicRowEditor]` method
must edit exactly one `[RunicCollection]` property of its ViewModel and have the
shape `void UpdateLine(TKey key, TRow row)`, optionally with a third parameter
`RunicRowEdit edit`: a public or internal instance method whose first parameter
is the collection's key type and whose second is its row type. A query
collection's rows cannot be edited yet. Without a collection name the attribute finds the collection by
the row type, so two collections of one row type need the name.

**Fix:** name the collection, as in `[RunicRowEditor(nameof(Lines))]`, put the
attribute on a collection that has `[RunicCollection]`, or change the method's
parameters and return type. See
[Editable rows and nested fields](../packages/dotnet/Runic.Application.Views/README.md#editable-rows-and-nested-fields).

### RUNICBRIDGE051

An error from the Bridge generator (since 0.16). Two `[RunicRowEditor]` methods
edit the same collection, so the Bridge cannot tell which one applies a cell's
change.

**Fix:** keep one row editor per collection. See
[Editable rows and nested fields](../packages/dotnet/Runic.Application.Views/README.md#editable-rows-and-nested-fields).

### RUNICBRIDGE052

An error from the Bridge generator (since 0.16). A cell write copies the row with
the one cell changed and passes the copy to the row editor. The copy must keep
everything the row holds, also what the page does not see, such as a
`[RunicIgnore]` database id, and a derived row's own members. The generator
copies a record or a struct with `with`, and a sealed class with its copy
constructor, its `Clone()` method, or, when every settable member is on the
wire, through its codec. A class that is not sealed, or a sealed class with
settable members the page does not see and no copy constructor or `Clone()`,
cannot be copied exactly.

**Fix:** make the row type a record, or seal it and add a copy constructor
(`public InvoiceLine(InvoiceLine other)`) or a `Clone()` method. See
[Editable rows and nested fields](../packages/dotnet/Runic.Application.Views/README.md#editable-rows-and-nested-fields).

### RUNICBRIDGE053

A warning from the Bridge generator (since 0.16). An object member inside an
editable row, such as a line's `Product`, cannot be copied exactly for the
reasons of [`RUNICBRIDGE052`](#runicbridge052), so its members get no fields of
their own. The member itself is still a field: a write replaces its whole value.

**Fix:** make its type a record, or seal it and add a copy constructor or a
`Clone()` method, or mark the member `[RunicNoField]`.

### RUNICBRIDGE060

An error from the Bridge generator (since 0.16). A `RunicQueryCollection<TRow, TKey>`
ViewModel property cannot be bridged: it has a public setter, it also carries
`[RunicCollection]` or `[RunicAppendOnlyText]`, its key is not a `string`,
`Guid` or `Int32`, or its rows are not DTOs. RUNICBRIDGE061 to RUNICBRIDGE064
are reserved for query collections.

**Fix:** expose the collection as a get-only property of DTO rows with a
supported key, and remove the other collection attributes; the page changes
the query with `setQuery`. See
[Query collections](../packages/dotnet/Runic.Application.Views/README.md#query-collections).

### RUNICBRIDGE065

An error from the Bridge generator (since 0.16). A `RunicBlob` is a value that
.NET serves to the page (feature `blobs/1`): it crosses as a URL, never as
bytes, so the page cannot send one back. This code reports a `RunicBlob` where
the page would supply it: a command input, an interaction result, or a member
of a DTO that is one. A state property that holds a `RunicBlob` is read-only
for the page whatever its setter, so it needs no change.

**Fix:** take a `RunicUpload` instead. The page uploads the file with
`client.upload(file)` and passes the resulting `BlobRef` to the command. See
[Blobs and uploads](../packages/dotnet/Runic.Application.Views/README.md#blobs-and-uploads).

### RUNICBRIDGE070

An error from the Bridge generator (since 0.16). `[RunicRegionCollection]` is
on a property that is not a `NavigationSelection`, the property has a public
setter, the attribute's content type is `object`, or no ViewModel with a
registered View is assignable to the content type, so the region collection
could present nothing.

**Fix:** put the attribute on a get-only `NavigationSelection` property, which
its `Add`, `SelectAsync` and `RemoveAsync` change rather than the web view.
Name an interface or base class that the regions' ViewModels share as the
content type, and register a View for at least one ViewModel that implements
it. See
[Region collections](../packages/dotnet/Runic.Application.Views/README.md#region-collections).

### RUNICBRIDGE071

An error from the Bridge generator (since 0.16). The generated client method
`select{Property}` or `close{Property}` of a region collection has the name of
another member of the generated client: a command's method or its `can…`,
`start…` or `recover…` method, a `set…` or `write…` method of a property, or
`snapshot`, `subscribe`, `dispose`, `interactions` or `fieldBaseline`. Without
`PageCanClose`, the collection has no `close…` method, so only its `select…`
method can collide.

**Fix:** rename the region collection property or the other member, such as a
`SelectDocuments` command beside a `Documents` region collection. See
[Region collections](../packages/dotnet/Runic.Application.Views/README.md#region-collections).

### RUNICBRIDGE072

A warning from the Bridge generator (since 0.22). `[RunicDevelopmentOnly]` is on
a `ViewQuestion<TInput, TOutput>`, `ViewRequest<T>` or ReactiveUI interaction
member. Only a ViewModel, a state property or a command can be
development-only: a trimmed or NativeAOT publish leaves those out. The
declaration has no effect, and the member stays in every publish.

**Fix:** mark the whole ViewModel when the interaction belongs to a development
tool, or remove the attribute. See
[Development-only views and members](packaging.md#development-only-views-and-members).

### RUNICBRIDGE073

An error from the Bridge generator (since 0.23). A ReactiveUI interaction's
`[RunicInteractionTimeout]` is not a timeout: neither `Timeout.Infinite` nor 1
to 86 400 000 milliseconds (24 hours). The analyzer does not report it.

**Fix:** give `[RunicInteractionTimeout]` a timeout in that range, or
`Timeout.Infinite` for none. See
[Interaction lifetime](../packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#interaction-lifetime).

### RUNICBRIDGE074

An error from the Bridge generator (since 0.23). A
`ViewQuestion<TInput, TOutput>` member has `[RunicInteractionTimeout]`, which
applies to ReactiveUI interactions only. The analyzer does not report it.

**Fix:** remove the attribute, and give the view question its lifetime in its
constructor: `new ViewQuestion<Request, bool>(timeout: Timeout.InfiniteTimeSpan)`.
See [Interaction lifetime](../packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#interaction-lifetime).

### RUNICBRIDGE075

A warning from the `Runic.Application.Views` build targets (since 0.23).
`RunicDevelopmentOnlyViews` or `RunicBridgeBlobs` has a value other than `auto`
(the default, also when the property is empty) or `true`, compared without case,
such as `false` or a typo. The build goes on as `auto`: a trimmed or NativeAOT
publish leaves development-only views and members, or Bridge blobs and uploads
that no Bridge uses, out, and builds and untrimmed publishes keep them. There is
no `false`: neither can be left out of a build or an untrimmed publish.

**Fix:** remove the property for `auto`, or set `true` to keep them in every
publish. See [MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE076

A warning from the Bridge generator (since 0.24; since the TypeScript emitter
writes the clients, from the build's emitter step). A generated client replaces a
file that has more: the new `<view>.contract.d.ts` has no declaration map, or
fewer members in it, or a generated module has fewer documentation lines, and an
assembly that declares the client's ViewModel or one of its base classes has no
PDB, for example a library with `<DebugType>none</DebugType>` in Release. The
generator finds member locations, and the `<summary>` of a partial property such
as `[Reactive] public partial string Name { get; set; }`, through the assembly's
PDB. Without one, the client is written without them, so clients committed to
source control change between a Debug and a Release build of the same source.
The warning names the file and the assemblies without a PDB. A first
generation, with no file to compare, does not warn.

**Fix:** give every assembly whose ViewModels the application presents a
portable or embedded PDB in every configuration, such as
`<DebugType>portable</DebugType>` in its project or a `Directory.Build.props`,
and build again; the clients then have their maps and comments again. A
project that does not commit its generated clients can suppress the warning with
`NoWarn`. See
[Commit generated clients](../packages/dotnet/Runic.Application.Views/README.md#commit-generated-clients).

### RUNICBRIDGE077

An error from the Bridge generator (since 0.24). A ViewModel property has both
`[RunicInclude]` and `[RunicIgnore]`, on the property itself or on a base
property it overrides, so the generator cannot tell whether to publish it. It is
an error in either publication mode. The analyzer does not report it.

**Fix:** remove `[RunicIgnore]` to publish the member, or `[RunicInclude]` to
keep it on .NET. See
[Which members are published](../packages/dotnet/Runic.Application.Views/README.md#which-members-are-published).

### RUNICBRIDGE078

A warning from the Bridge generator (since 0.24). `[RunicInclude]` is on a
member of a ViewModel class that the Bridge never publishes, in either
publication mode: a field, a static or non-public property, an indexer, the
ReactiveUI.Validation `ValidationContext`, which the Bridge publishes as the
validation state, or a property that a base class declares and the ViewModel
does not override, unless that base class is a generated ViewModel itself. The
declaration has no effect. The warning repeats on builds that reuse the cached
generation; a member of a base class shared by several ViewModels is reported
once. A field has no location in the PDB, so its warning points at the class.

**Fix:** move the attribute to a public instance property that the ViewModel
class declares. For a CommunityToolkit `[ObservableProperty]` field or a
`[RelayCommand]` method, write `[property: RunicInclude]`, which the toolkit
puts on the generated property. Override an inherited property in the
ViewModel, or remove the attribute. See
[Which members are published](../packages/dotnet/Runic.Application.Views/README.md#which-members-are-published).

### RUNICBRIDGE079

An error from the Bridge generator (since 0.24). A
`RunicApplicationFrontendClientModule` item names an assembly that the
application cannot use as a client library: the assembly is not beside the
application's inspected assembly, so the application does not reference it (the
item's `Include` is the assembly's simple name, such as `SharedUi`, not a project
path), it is the application itself, or it was not built with
`RunicApplicationFrontendClientLibrary=true` and so does not carry the metadata
`Runic.Application.Views.ClientLibrary`. Such an assembly generates no module for
the application to import.

**Fix:** add a `ProjectReference` or `PackageReference` to the library, correct
the item's `Include`, or set `RunicApplicationFrontendClientLibrary` to `true` in
the library's project and build it again. See
[MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE080

An error from the Bridge generator (since 0.24). A ViewModel whose Views a client
library declares, and that a `RunicApplicationFrontendClientModule` item maps,
also has Views in the application, or in a second mapped library. The assembly
that declares a ViewModel's Views owns its generated TypeScript, so all of its
Views must be in one assembly. This is typical after mapping a library: the
application still declares the Views that the library now declares, such as
`public sealed partial class AboutDialogView : RunicView<AboutDialogViewModel>;`.

**Fix:** delete the application's View declarations of the library's
ViewModels, or remove the item and keep generating the application's own
clients. See [MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE081

An error from the Bridge generator (since 0.24). The client of a ViewModel that a
mapped client library owns comes from the library's generated module, which types
only the pages the library generates. The error names the case:

- a content slot, content collection or region collection of a library
  ViewModel admits a ViewModel whose client the application or another library
  owns, such as an application ViewModel that implements an interface the slot
  names;
- the ViewModels that implement a `[RunicSharedClient]` interface have clients of
  different owners, such as an application ViewModel that implements a library's
  shared client interface. A shared client's module is generated once.

**Fix:** declare the Views of the presented ViewModel, or of every implementer, in
the library, or present the ViewModel from one of the application's own
ViewModels instead. See [MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE082

An error from the `Runic.Application.Views` build targets, and from the generator
when it is run without them (since 0.24). A `RunicApplicationFrontendClientModule`
item has no `Specifier` or a blank one, a relative one such as
`../shared-ui/src/generated/runic-clients.js`, which each generated module would
resolve from its own folder, or the same `Include` as another item.

**Fix:** set `Specifier` to the module specifier through which the frontend
imports the library's `runic-clients.ts`: a package export such as
`@acme/shared-ui/generated`, or an alias of the frontend's tsconfig `paths` and
bundler. Keep one item per assembly. See
[MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE083

A warning from the Bridge generator (since 0.24). A client library that a
`RunicApplicationFrontendClientModule` item maps declares no Runic Window or
View, so the application imports nothing from it and generates as without the
item. The warning repeats on builds that reuse the cached generation.

**Fix:** declare the Views of the library's ViewModels in the library, or remove
the item. See [MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE084

An error from the `Runic.Application.Views` build targets (since 0.24). Since
0.24 the Views TypeScript emitter, `@runic-artifex/views/codegen`, writes the
generated clients from the contract IR that the generator writes in `obj`, and
Node.js or Bun runs it. The build reports this code when it cannot run the
emitter:

- neither `node` nor `bun` is on `PATH`, or the command that
  `RunicApplicationFrontendCodegenRuntime` names does not run;
- the frontend (a `package.json` in `RunicApplicationFrontendDirectory`) declares
  `@runic-artifex/views` in one of its dependency fields, but it is not installed,
  in its `node_modules` or one above it, after the install step (the install is
  disabled or failed), while the build builds the frontend;
- the frontend's `@runic-artifex/views` is older than 0.24 and has no emitter, or
  cannot read the IR of this `Runic.Application.Views`.

The emitter comes from the frontend's `@runic-artifex/views` when it is
installed, so the clients match the runtime they import. Otherwise the build uses
the copy bundled with `Runic.Application.Views`, which matches the generator's
version: for a project without a frontend, such as a library or a test project, a
frontend whose `package.json` does not declare `@runic-artifex/views`, and a build
that does not build the frontend (`RunicApplicationFrontendBuildEnabled=false`,
as the `runic-app` template's test project sets).

**Fix:** install Node.js or Bun, or set `RunicApplicationFrontendCodegenRuntime`
to a `node` or `bun` executable; add `@runic-artifex/views` of the same version
as `Runic.Application.Views` to the frontend's dependencies and install them,
or let the build install them (`RunicApplicationFrontendInstallEnabled`). Set
`RunicApplicationFrontendGenerationEnabled=false` in a project that another
project generates the Bridges for. See
[MSBuild properties](msbuild.md#runicapplicationviews).

### RUNICBRIDGE085

An error from the build targets of `Runic.Application.Views` and `Runic.Desktop`
(since 0.24): the project's package graph holds a package whose ID was renamed,
referenced by the project (`PackageReference`) or brought in by another package.
Packages of the old and the new IDs do not work together in one application:
the build would load their targets twice, and the Bridge would not recognise a
type that the old package declares under its old name. The message names the
new ID.

It is one of the old-name guards (D079.11), with `RUNICBRIDGE017` for build
properties and items and `RUNICBRIDGE086` for environment variables. They are
reported through 1.0.x and removed in 2.0. The list of old IDs is
`Runic.Sdk.RenamedNames.targets`, generated from
`packages/dotnet/Runic.Application.Views/rename/old-names.json`; 0.24 lists no
package.

**Fix:** run `rax migrate` (`dnx RunicArtifex.Tool migrate` without a tool
manifest), which moves the project's references to the new IDs, or replace the
old `PackageReference` by hand. A package that another package brings in moves
with that package: update it to a release of the new names.

### RUNICBRIDGE086

An error from the build targets of `Runic.Application.Views` and `Runic.Desktop`
(since 0.24): an environment variable that the build read under a former name is
set. The old name no longer applies, and the message names the new one. It is
one of the old-name guards (D079.11), like `RUNICBRIDGE085`; 0.24 lists no
environment variable.

**Fix:** set the variable by its new name and remove the old one from the
environment, for example from a `launchSettings.json`, a CI workflow or a shell
profile.

### RUNICBRIDGE087

An error from the `Runic.Application.Views` build targets (since 0.24), before
the frontend install. The frontend has no lock file of its own and sits below a
folder that declares a JavaScript workspace (a `package.json` with
`workspaces`, or `pnpm-workspace.yaml`), but the workspace's patterns do not
include the frontend's folder. The build would install at the workspace root,
which does not install the frontend's dependencies.

**Fix:** add the frontend's folder to the root's `workspaces` (or to
`packages` in `pnpm-workspace.yaml`) and run the install at the root once, so
that its lock file covers the frontend. To install the frontend on its own
instead, set `RunicApplicationFrontendWorkspaceRoot` to `none` and commit a
lock file in the frontend's folder. See
[JavaScript workspaces](../packages/dotnet/Runic.Application.Views/README.md#javascript-workspaces).
`dotnet runic doctor` reports the same problem without a build, as its
[`frontend-workspace`](../tools/dotnet-runic/README.md#frontend-workspace) check.

### RUNICBRIDGE088

An error from the `Runic.Application.Views` build targets (since 0.24). A
frontend package's `package.json` names a file in `exports`, `main`, `module` or
`types` that does not exist, so a frontend that imports the package would fail
to resolve it. It is reported in two places:

- **After a client library's package build** (or with the package current),
  naming the library project: its build script did not write the file.
- **Before an application's (or library's) own frontend build**, for each
  package that its frontend depends on: a library's package whose build is off
  (`RunicApplicationFrontendBuildEnabled=false`) or did not write the file, or a
  workspace package that no .NET project owns, which the build builds itself
  when it has a `build` script.

**Fix:** make the package's build script write the file, correct `exports` in
its `package.json`, or turn the owner's package build on. See
[Library packages](../packages/dotnet/Runic.Application.Views/README.md#library-packages).
`dotnet runic doctor` checks the packages' exports without a build, as its
[`frontend-dependencies`](../tools/dotnet-runic/README.md#frontend-dependencies) check.

### RUNICBRIDGE089

A warning from the `Runic.Application.Views` build targets (since 0.24): the
frontend is a member of a JavaScript workspace and its `package.json` declares a
`packageManager` other than the workspace's, which comes from the root's
`packageManager` or lock file. The build installs and builds with the
workspace's manager: the member's would not read the root's lock file, as npm
fails on Bun's `workspace:*` protocol.

**Fix:** remove `packageManager` from the member's `package.json`, or make it
the workspace's. To choose the manager yourself, set
`RunicApplicationFrontendPackageManager`. `dotnet runic doctor` checks every
member and the packages that the page depends on, and the root's lock files,
as its [`package-manager-agreement`](../tools/dotnet-runic/README.md#package-manager-agreement) check.

### RUNICBRIDGE090

An error from the `Runic.Application.Views` build targets (since 0.24): the
package manager that the frontend uses does not run, usually because it is not
installed or not on `PATH`. The build checks it before the frontend install
(`<manager> --version`), and when the frontend build exits with the shell's
"command not found" code (127, or 9009 on Windows). The message names the
manager and where it was declared: `RunicApplicationFrontendPackageManager`,
the `packageManager` of a `package.json`, a lock file, or npm as the default.
It is also reported when the JavaScript runtime that resolves a frontend
workspace, Bun for a Bun workspace and Node.js otherwise, does not run. Before
0.24 the same failure was `RUNICBRIDGE016` with exit code 127.

**Fix:** install the package manager (Bun 1.4.4 or later, pnpm, or Node.js,
which includes npm) and make sure the build's environment finds it on `PATH`;
an IDE may start the build with another `PATH` than your shell. To use another
manager, set `RunicApplicationFrontendPackageManager`.

### RUNICBRIDGE091

A warning from the `Runic.Application.Views` build targets (since 0.24):
`RunicApplicationFrontendInstallMode` is neither `Auto`, `Always` nor `Never`
(compared without case). The build installs as with `Auto`: when the lock file,
a `package.json` or the package manager changed, or `node_modules` is
incomplete.

**Fix:** set `Auto`, `Always` or `Never`, or remove the property. See
[Fast iteration](../packages/dotnet/Runic.Application.Views/README.md#fast-iteration).

## dotnet runic

`dotnet runic` reports these codes as the fault of a failed command, except
`RCLI8101` and `RCLI8102`, which mark individual doctor checks. Human
output prints the link to the entry after the fault line; JSON output carries
the code in `fault.code` and, since 0.11, the link in `fault.helpUri`. Since
0.20, `dev` also prints its fault as one line in MSBuild's canonical form,
`<project> : error RAPPDEV1007: <message>`, which IDE problem matchers such as
VS Code's `$msCompile` read like a build error (see
[IDE contract](../tools/dotnet-runic/README.md#ide-contract)). Input
of the wrong shape (a usage or validation error, the same whatever the project)
exits with code 2; a failure on what the command found, such as a missing
project, an invalid project setting or a failed step, exits with code 1. A
cancelled command reports `RCLI4000` and exits with code 130. An
unexpected internal failure reports `RCLI5000`, exits with code 3 and prints the
exception to standard error; include it when you report the problem. See
[Output and exit codes](../tools/dotnet-runic/README.md#output-and-exit-codes).
`dotnet runic support` moved to `runic-translations support` in 0.9; its
`RAPPSUP` codes are in the runic-translations-sdk catalog as `RTRSUP`.

### RAPPCLI1000

The `dev` or `size` command did not complete successfully. The output above the
fault names the step that failed.

**Fix:** correct the failure that the output reports, then run the command
again. `dotnet runic doctor` checks the prerequisites.

### RAPPCLI1003

MSBuild returned project configuration that is not valid JSON.

**Fix:** run `dotnet build` on the project to see the MSBuild error, and check
that every Runic package has the same version.

### RAPPCLI1008

The command could not read or write a required local file, such as the
project, the frontend directory or the report.

**Fix:** check that the path exists and that you can read and write it.

### RAPPCLI1009

Doctor checks reached the `--fail-on` threshold. JSON output maps each check
that did not pass to `fail` or `warn` in `fault.details`.

**Fix:** follow the remediation of each failing check in the report. See
[Doctor JSON output](../tools/dotnet-runic/README.md#doctor-json-output) for
`--fail-on` and exit codes.

### RAPPCLI1010

`--fail-on` has a value other than `never`, `fail` or `warn`.

**Fix:** pass one of those values.

### RAPPCLI1011

`doctor` target options are invalid: the runtime identifier is malformed, or
`--aot`, `--no-aot`, `--self-contained` or `--no-self-contained` is used
without `--runtime`.

**Fix:** pass a runtime identifier such as `linux-x64`, `win-x64` or
`osx-arm64` with `--runtime`.

### RAPPCLI1012

`dotnet runic support`, the former name of `runic-translations support`, found
no Translations tool to forward to: no `RAX_TRANSLATIONS_TOOL`, local tool
manifest that lists `dotnet-runic-translations`, `node_modules/.bin/runic-translations`
or `runic-translations` on `PATH`.

**Fix:** install the tool, for example with `dotnet tool install
dotnet-runic-translations`, and run `runic-translations support
preview|collect|remove`. See
[Support envelopes](../tools/dotnet-runic/README.md#support-envelopes).

### RAPPCLI1013

`dotnet runic translations` found no Translations tool to run: no
`RAX_TRANSLATIONS_TOOL`, local tool manifest that lists
`dotnet-runic-translations`, `node_modules/.bin/runic-translations` or
`runic-translations` on `PATH`. Exit code 1. The message names the command
that installs the tested release.

**Fix:** run the printed `dotnet tool install dotnet-runic-translations
--version …` command, then the command again. See
[Tool extensions](../tools/dotnet-runic/README.md#tool-extensions).

### RAPPCLI1014

`dotnet runic translations` found the Translations tool but could not start
it, for example because `RAX_TRANSLATIONS_TOOL` names a file that does not
exist or is not executable. The message names the program and where it was
found. Exit code 1.

**Fix:** correct or unset `RAX_TRANSLATIONS_TOOL`, or reinstall the tool.

### RAPPDEV1002

The project cannot be selected: the given project does not exist or is not a
`.csproj`, or the directory has no `.csproj` or several. Exit code 1. From a
solution root, `dev` and `doctor` take the solution's one Window project (since
0.24); they report this code when the directory holds several solutions, or
when the solution has no Window project or several, which the message lists.

**Fix:** pass the project with `--project`, for example
`dotnet runic dev --project Acme.App/Acme.App.csproj`.

### RAPPDEV1003

MSBuild could not evaluate the project. Exit code 1.

**Fix:** run `dotnet build` on the project and fix the error it reports.

### RAPPDEV1004

A required program could not be started, for example `dotnet`, Node.js, Bun or
the package manager. Exit code 1.

**Fix:** install the program and make sure it is on `PATH`.
`dotnet runic doctor` lists the runtime and package manager the project needs.
On Windows the tool finds a program through `PATH` and `PATHEXT`, as the shell
and `doctor` do, so `npm.cmd` and `pnpm.cmd` count. A batch file cannot receive
an argument with a line break; the message says so.

### RAPPDEV1005

The project is not a Runic Views Window project that `dev` can run, or its
frontend configuration is invalid. Examples: the project does not set
`RunicApplicationFrontendWindowProject=true`, has no `RunicApplicationFrontendDirectory` with a
`package.json`, names an unsupported package manager or development server, or
a Vite input or development document lies outside the web root. Exit code 1.

**Fix:** set the property or fix the configuration that the message names.
`dev` runs CS-WebUI and Runic Desktop Window projects only.

### RAPPDEV1006

A restore, frontend install, frontend compiler or build step failed. The output
above the fault contains the step's own errors.

**Fix:** fix those errors. `dotnet runic doctor` checks the lock file and that
every package belongs to one release.

### RAPPDEV1007

The Window process or the frontend development server exited unexpectedly,
did not become ready in time, or did not serve a page that the native window
loads. The message names the page and the server's response.

**Fix:** read the process output above the fault. For a development server,
check that the configured dev script starts with the frontend's own command,
such as `npm run dev`.

Since 0.24, `dev` stops waiting at once when Vite answers the application entry
with HTTP 500 twice, such as for an import that Vite cannot resolve, and the
message carries Vite's own error. When the import names a frontend package that the
page depends on, it also says whether that package has been built. A server that
exits before it is ready reports the first error line of its output.

For Vite, the application entry must come back as a JavaScript module. Vite
answers a path that is not below its `root` with its `index.html` fallback, so
`dev` stops at once when the entry's content type is not JavaScript (since
0.24). Set `RunicApplicationFrontendViteDevServerEntry` to the module's path
below Vite's root, and `RunicApplicationFrontendViteRoot` when the Vite
configuration sets `root`; `dotnet runic doctor` then checks the same path.

With `runic()` from `@runic-artifex/vite-plugin-runic` in the Vite
configuration, or for Angular with the plugin in `package.json`,
the server is ready only when `/__runic/state` confirms the session that `dev`
started it with (since 0.11). A message that names the session confirmation
means that the plugin is older than `dotnet-runic`, that an Angular serve
target's `proxyConfig` does not include `runicDevSessionProxy()`, or that
another process answered on the port.

**Fix:** use `@runic-artifex/vite-plugin-runic` from the release of
`dotnet-runic`; `dotnet runic doctor` reports packages from another release.
For Angular, spread `runicDevSessionProxy()` into the proxy configuration, as
the `runic-app` template's `proxy.conf.mjs` does
([`dotnet runic dev`](../tools/dotnet-runic/README.md)).

`dotnet runic dev` picks a free loopback port for the development server, and
another process can take it before the server binds it. When the server exits
reporting that its port is in use, the command starts it again on a new port,
up to three times, and fails with this diagnostic only if every port was taken.

### RAPPDEV1008

`--no-restore` was given but the frontend packages are not installed, or
`--no-build` was given and no build has installed them yet (since 0.24 the build
installs them, and `--no-build` runs no build). A `node_modules` at the
JavaScript workspace root above the frontend counts. Exit code 1.

**Fix:** install them with the frontend's package manager (at the workspace
root in a workspace), build the project once, or omit the option.

### RAPPDEV1009

A Window entry page does not exist: a Vite build input or the configured
development document names a file that is missing. Exit code 1.

**Fix:** create the page or remove it from `build.rolldownOptions.input`. See
[A Window with its own page](../packages/dotnet/Runic.Application.Views/README.md#a-window-with-its-own-page).

### RAPPDEV1010

`dev` could not read the build inputs from the Vite configuration.

**Fix:** check that `vite build` works in the frontend directory.

### RAPPDEV1011

A `dev -e` value is not `NAME=VALUE`: it has no `=`, an empty name or a
name with spaces. Exit code 2. Since 0.11.

**Fix:** pass each variable as `-e NAME=VALUE`; the value may be empty.

### RAPPDEV1012

`dev -e` names a variable that `dev` sets for the Window itself:
`RUNIC_APPLICATION_VITE_*`, `RUNIC_APPLICATION_FRONTEND_*`,
`RUNIC_APPLICATION_DEV_PROJECT` or `RUNIC_APPLICATION_DEVELOPMENT_ASSETS`.
Exit code 2. Since 0.11.

**Fix:** remove the variable; `dev` describes the development server and the
frontend output to the Window with them.

### RAPPDEV1013

The application that `dev` built needs a .NET runtime that is not installed,
such as the .NET 10 runtime of a `net10.0` application built with the .NET 11
SDK. `dev` reads the build's `runtimeconfig.json` and the output of
`dotnet --list-runtimes` before it starts the application, which would
otherwise exit with code 150 ("You must install or update .NET"). The message
names the missing runtime and the installed versions. Exit code 1. Since 0.18.

**Fix:** install that runtime next to the SDK, for example the .NET 10 runtime
with `dotnet-install --runtime dotnet --channel 10.0` (add `--runtime aspnetcore`
for Runic Desktop on Kestrel) or your package manager, or install .NET SDK
10.0.404 or later 10.0, which includes it. `dotnet runic doctor` reports the
same problem in its `dotnet-runtime` check.

### RAPPDEV1014

`dev --no-window` was combined with an option that only describes how `dev`
starts the Window or that stops the frontend from being served:
`--no-frontend-watch`, `--no-dotnet-watch`, `--launch-profile`, `-e` or
application arguments after `--`. With `--no-window` the IDE starts the Window.
Exit code 2. Since 0.20.

**Fix:** drop the option and set the Window's launch profile, environment and
arguments in the IDE's launch configuration. See
[Debug the Window in your IDE](../tools/dotnet-runic/README.md#debug-the-window-in-your-ide).

### RAPPDEV1015

`dev --no-window` found no frontend to run: the project has neither a Vite or
Angular development server nor a frontend watcher (the frontend's `dev` script
or `RunicApplicationFrontendDevWatchTarget`). Exit code 1. Since 0.20.

**Fix:** run `dotnet runic dev` without `--no-window`, or give the frontend a
development server or a `dev` script.

### RAPPDEV1016

`dev --browser` could open no loopback port for the address that sends a browser
to the Window: neither 9224 nor any free port. Exit code 1. Since 0.21.

**Fix:** check that the machine allows listening on `127.0.0.1`, for example in a
sandbox or container. See
[Debug the page in Chrome](../tools/dotnet-runic/README.md#debug-the-page-in-chrome---browser).

### RAPPDEV1017

`dev --no-window` on Linux could not open Runic's CDP adapter on port 9222,
which the IDE's attach configuration names (the template's
`Attach to the page (JavaScript)`): another process holds it, often another
`dotnet runic dev` session or a browser with remote debugging. Without
`--no-window`, `dev` moves the adapter to a free port and names it instead.
Exit code 1. Since 0.21.

**Fix:** stop the process that holds 9222, or run `dev` with
`--no-debug-endpoint`. See
[Debug the page's JavaScript on Linux](../tools/dotnet-runic/README.md#debug-the-pages-javascript-on-linux).

### RAPPDEV1018

A frontend package that the page depends on failed (since 0.24): a client
library's package, or a workspace package that no .NET project owns.

- **At the start**, `dev` stops with exit code 1 when the package's build fails in
  its initial build. The message quotes the build's error, such as
  `shared-ui/src/index.ts(3,1): error TS1005`. It also stops when a compiled
  package's exported file is missing after the build. The message names the file
  and the reason: the package's build is off in its project
  (`RunicApplicationFrontendBuildEnabled=false`), its build did not write the file,
  or `--no-build` ran no build.
- **While `dev` runs**, a rebuild that fails, a located error from the package's
  watch script, or a watch script that exits is a line in MSBuild's form:
  `<owner project> : error RAPPDEV1018: [shared-ui] …`. `dev` keeps running. After a
  failed rebuild it puts the package's last good output folders back, so the page
  keeps working, and the next change rebuilds the package.

**Fix:** fix the package's error, shown in the output above the line. For a missing
export, make the package's `build` script write every file that its `package.json`
`exports`, or turn its build on. See
[Library packages under dev](../tools/dotnet-runic/README.md#library-packages-under-dev).

### RAPPDEV1019

A warning from `dev` (since 0.24): the project's `Runic.Application.Views` build
targets are older than the tool, so they do not read
`RunicApplicationFrontendDevSessionProject`. `dev` falls back to the old
behaviour: it installs the frontend packages itself and passes
`RunicApplicationFrontendBuildEnabled=false` to every project it builds, so a
client library's compiled frontend package is not built and the page can fail
to resolve it.

**Fix:** update the project's Runic packages to the tool's version. See
[Library packages](../packages/dotnet/Runic.Application.Views/README.md#library-packages).

### RAPPPKG1001

`package` found an invalid application identity: `ApplicationId` is not a
reverse-DNS name of ASCII letters and digits with each part starting with a
letter and at most 50 characters, `RunicApplicationUrlSchemes`,
`RunicApplicationFileTypes` or `RunicApplicationWebLinkHosts` has an entry no
platform registers, `RunicPackageName` is not a deb or rpm package name,
`RunicMacApplicationCategory` is not a `public.app-category.*` identifier, or the
`RunicPackageIcon` file does not exist. The message names the value. Exit code
1. Since 0.11.

**Fix:** correct the property in the project file. See
[Package the application](../tools/dotnet-runic/README.md#package-the-application).

### RAPPPKG1002

An external tool the target needs is not installed or not on `PATH`, for
example `makeappx` (Windows SDK), `codesign` (Xcode command line tools),
`flatpak-builder`, `appimagetool`, `dpkg-deb` or `rpmbuild`. The message names
the tool and what provides it. Exit code 1. Since 0.11.

**Fix:** install the named package and run the command again.

### RAPPPKG1003

`dotnet publish` with Native AOT failed for the target's runtime identifier, or
`dotnet msbuild` could not evaluate the project. The publish output is above
the fault. Exit code 1. Since 0.11.

**Fix:** correct the build error; `dotnet runic doctor --runtime <rid> --aot`
checks the Native AOT prerequisites.

### RAPPPKG1004

The target runs on another operating system (`windows` on Windows, `macos` on
macOS, the Linux formats on Linux), `--runtime` names a runtime identifier of
another operating system or one that Runic does not support (`osx-x64`: Intel
Macs are not a Runic target), or the target got several runtime identifiers.
Since 0.12 `macos` also takes one, `osx-arm64`, and makes no universal bundle.
Exit code 2. Since 0.11.

**Fix:** run the target on its operating system, for example in a CI job of that
OS, with one supported runtime identifier of that OS (`osx-arm64` for `macos`).

### RAPPPKG1005

A packaging tool exited with an error. The message names the tool and its exit
code; its output is above the fault. Exit code 1. Since 0.11.

**Fix:** correct what the tool reports, such as a signing certificate that does
not match `RunicApplicationPublisher`, and run the command again.

### RAPPPKG1006

A manifest or package failed validation: a manifest in the packaging directory
uses a `{{Field}}` the target does not fill, or `desktop-file-validate`,
`appstreamcli validate` or `codesign --verify` rejected the result. Since 0.12
also: `macos/entitlements.plist` is not a property list, has a key twice, or
lacks a key that `--sandbox` or `--provisioning-profile` needs (an override
without `{{Entitlements}}`), or the provisioning profile is missing, cannot be
decoded or is for another `ApplicationId` or team. Exit code 1. Since 0.11.

**Fix:** correct the manifest override the message names, or remove it to use
the tool's default; add `{{Entitlements}}` to an entitlements override; use a
macOS provisioning profile for the application's `ApplicationId`.

### RAPPPKG1007

The Flathub manifest or its offline sources cannot be generated: no git commit
or tag was given, a lock file (`packages.lock.json`, the AOT lock files or
`package-lock.json`) is missing, an entry has no hash, or `RuntimeIdentifiers`
does not list both `linux-x64` and `linux-arm64`. Exit code 1, or 2 when the
command line gives neither or both of `--git-commit` and `--git-tag`.
Since 0.11.

**Fix:** pass `--git-commit` or `--git-tag`, restore with lock files
(`RestorePackagesWithLockFile`) and list both Linux runtime identifiers.

### RAPPPKG2001

Warning: the package is unsigned. `windows` without `--certificate` or
`--thumbprint` writes an unsigned MSIX; `flatpak` without `--gpg-sign`, `deb`,
`rpm` and `appimage` never sign; `macos --pkg` without `--installer-sign` writes
an unsigned installer package (since 0.12). The command succeeds; the JSON payload lists
the warning as `{ "code": "RAPPPKG2001", "message": … }` in `warnings`. Since
0.11.

**Fix:** sign the package or the repository that serves it as the message
says, or accept an unsigned package for local testing.

### RAPPPKG2002

Warning: the macOS disk image is not notarized, because no `--notary-profile`
was given; without `--sign` the bundle is also only ad-hoc signed. Gatekeeper
blocks it on other Macs. Since 0.11.

**Fix:** pass `--sign` with a Developer ID and `--notary-profile`.

### RAPPPKG2003

Warning: `desktop-file-validate` (desktop-file-utils) is not installed, so the
desktop entry of a Linux package was not validated. Since 0.11.

**Fix:** install desktop-file-utils; with it, a rejected entry is
`RAPPPKG1006`.

### RAPPPKG2004

Warning: the AppImage loads GTK and WebKitGTK from the system it runs on, so
that system must have them installed. Since 0.11.

**Fix:** none in the AppImage; state the requirement for your users or ship a
Flatpak, which brings its runtime.

### RAPPPKG2005

Warning: `package flathub` found no git checkout around the project, so the
manifest assumes the repository's root is the project directory. Since 0.11.

**Fix:** run it in the application's git repository, or check the manifest's
paths.

### RAPPPKG2006

Warning: `package flathub` without `--build` wrote the manifest only; Flathub
builds and signs the application from it after you submit it. Since 0.11.

**Fix:** submit the manifest from your own account, see
[Flathub](packaging.md#flathub); `--build` builds it locally first.

### RAPPSIZE1001

`size` needs a runtime identifier and got none. Exit code 2. Before 0.9 this was
`RTKSIZE1001`.

**Fix:** pass `--runtime`, for example `linux-x64`. See
[Measure size](../tools/dotnet-runic/README.md#measure-size).

### RAPPSIZE1002

`size` got `--verify-argument` without `--verify`. Exit code 2. Before 0.9 this
was `RTKSIZE1001`.

**Fix:** name the executable that receives the arguments with `--verify`.

### RAPPSIZE1003

The `--report` file already exists; `size` never replaces earlier reports. Exit
code 1. Before 0.9 this was `RTKSIZE1001`.

**Fix:** choose a new report path, or omit `--report` for a new
`runic-size-<runtime>-<UTC time>.json`.

### RAPPSIZE1004

`size` could not evaluate the project's publish settings with `dotnet msbuild`.
The message includes the MSBuild output. This is a command failure, exit code 1.
Before 0.9 this was `RTKSIZE1002`.

**Fix:** check that the project restores and builds for the selected runtime.

### RAPPSIZE1005

A `size --property` value is not `Name=Value` with an MSBuild property name:
letters, digits, `_` and `-`, starting with a letter or `_`. Exit code 2.
Since 0.11.

**Fix:** pass each property as `--property Name=Value`.

### RAPPSIZE1006

`size --property` sets `Configuration`, `RuntimeIdentifier`, `PublishAot` or
`SelfContained`, which `size` sets from its own options. Exit code 2. Since
0.11.

**Fix:** use `--configuration`, `--runtime`, `--aot` or `--no-aot`, and
`--self-contained` or `--no-self-contained`.

### RCLI4000

The command was cancelled, for example with Ctrl+C, and exits with code 130.
Stopping `dev` with Ctrl+C reports it too, after the Window and the frontend
server have stopped.

**Fix:** nothing to fix; run the command again to finish it.

### RCLI8101

A doctor check failed. `doctor --format json` reports one diagnostic with this
code for each failed check, named by its check id.

**Fix:** follow the remediation of the check. See
[Doctor JSON output](../tools/dotnet-runic/README.md#doctor-json-output).

### RCLI8102

A doctor check passed with a warning. `doctor --format json` reports one
diagnostic with this code for each such check.

**Fix:** follow the remediation of the check, or accept the warning.

## Asset packer

`Runic.Assets.Packer`, which the Runic.Assets build runs, and
`dotnet runic assets pack` report the `RAS1xxx` codes. Human output writes the
message and the link to standard error. See the
[packer guide](../tools/Runic.Assets.Packer/README.md#exit-codes-and-errors).
The `Runic.Assets` build targets report the `RAS2xxx` codes.

### RAS1001

The source directory does not exist. Exit code 1.

**Fix:** build the frontend first, or point the archive at its output folder,
such as Vite's `dist`.

### RAS1002

The entry point does not exist in the source directory or was excluded, by
`--exclude` or by a default exclusion (a `.git` path or a `.env*` name). Exit
code 1.

**Fix:** pass the entry document with `--entry-point`, or remove it from the
exclusions.

### RAS1003

The archive could not be packed, for example because of an invalid asset path,
a symbolic link or an I/O error. Exit code 1.

**Fix:** correct the path or file that the message names.

### RAS1004

The pinned directory mode, the default without `--trusted-generated-output`,
requires Linux. Exit code 1.

**Fix:** pass `--trusted-generated-output` for output of the current build, as
the MSBuild integration does. See
[`--trusted-generated-output`](../tools/Runic.Assets.Packer/README.md#--trusted-generated-output).

### RAS1005

A warning: the archive contains source maps (`*.map`). The packer writes their
count and the first path to standard error as `warning RAS1005: …`, so the
`Runic.Assets` build reports it as a build warning. Source maps can expose the
original source code of the frontend. The archive is written and the exit code
is 0. Since 0.11.

**Fix:** leave the maps out of the production build (Vite's `build.sourcemap`)
or exclude them with `--exclude` or `RunicAssetsDistExclude`. If the
application ships them on purpose, add `RAS1005` to `NoWarn`.

### RAS1006

`Runic.Assets.Packer verify` was given an archive that does not exist. Exit
code 1. Since 0.15.

**Fix:** pack the archive first, or correct the path.

### RAS1007

`Runic.Assets.Packer verify` read an archive that is not a valid Runic Assets
archive: it is not a ZIP file, its manifest is missing, invalid or of another
version, it declares an asset it lacks or holds one it does not declare, or an
asset differs from its manifest's length or SHA-256 digest. The reason follows
the message. Exit code 1. Since 0.15.

**Fix:** pack the archive again from its source, with the build,
`dotnet runic assets pack` or `AssetArchive.WriteAsync`.

### RAS2001

The `Runic.Assets` build targets report this error. The project sets both
`RunicAssetsDist`, a directory the build packs, and `RunicAssetsEmbeddedArchive`,
an archive packed before.

**Fix:** set one of them.

### RAS2002

The directory that `RunicAssetsDist` names does not exist when the build packs
it.

**Fix:** build the web UI before compiling the project, or correct the path.

### RAS2003

The asset packer that `RunicAssetsPackerPath` names does not exist. By default
it is `tools/net10.0/Runic.Assets.Packer.dll` in the `Runic.Assets` package.

**Fix:** restore the project again, or correct `RunicAssetsPackerPath`.

### RAS2004

The archive that `RunicAssetsEmbeddedArchive` names does not exist.

**Fix:** pack the archive first, or correct the path.

### RAS2005

The asset packer could not pack `RunicAssetsDist`. Its own `RAS1xxx` error is
above in the build output.

**Fix:** follow the packer error.

### RAS2006

The archive that `RunicAssetsEmbeddedArchive` names failed the packer's check
before it was embedded: it is not a valid Runic Assets archive, or an asset
does not match its manifest. The packer's `RAS1007` error with the reason is
above in the build output. The build checks the archive because
`AssetArchive.ReadEmbedded` does not check digests at run time. The check runs
again when the archive or the packer changes. Since 0.15.

**Fix:** pack the archive again from its source, or correct the path.

## Runic Desktop build

The `Runic.Desktop` build targets report these errors and the warning
`RUNICDESK007`; `RUNICDESK001` comes from
`Runic.Desktop.Kestrel`, `RUNICDESK005` and `RUNICDESK006` from
`Runic.Desktop.NativeServer`.
The experimental IDs `RUNICDESK100` to `RUNICDESK102`, `RUNICDESK104` (the side panel, until 0.22.0) and
`RUNICDESK105` (the close drain, until 0.22.0) are retired; `RUNICDESK103` (hosted applications) is in use
([Graduated and retired IDs](experimental.md#retired)).

### RUNICDESK001

`RunicDesktopMinimalHost` has a value other than `true` or `false`. The
property selects how `Runic.Desktop.Kestrel` builds its ASP.NET Core host.

**Fix:** set it to `true` or `false`, or remove it for the default, `false`.

### RUNICDESK002

A Native AOT publish for `win-x64` or `win-arm64` links the WebView2 loader
statically, but the restored `Microsoft.Web.WebView2` package has no
`WebView2LoaderStatic.lib` for the runtime identifier.

**Fix:** restore the project for the runtime identifier again, so the
`Microsoft.Web.WebView2` native SDK assets are present.

### RUNICDESK003

`RunicDesktopServer` has a value other than `Native`, `Kestrel` or `None`.

**Fix:** set it to `Native` for the native server, `Kestrel` for ASP.NET Core
Kestrel, `None` for an executable that opens no Runic Desktop window, or remove
it: with one server package it selects that server, and with both the native
server.

### RUNICDESK004

The executable references Runic Desktop but no presentation server, or
`RunicDesktopServer` names a server whose package it does not reference. Since
0.15 `Runic.Desktop` carries no server of its own; without one the first window
would fail with `presentation-server-unavailable`.

**Fix:** add a `PackageReference` to `Runic.Desktop.NativeServer` (the native
server, without ASP.NET Core, which new applications use) or to
`Runic.Desktop.Kestrel` (ASP.NET Core Kestrel, the server of 0.14 and earlier),
matching the version of `Runic.Desktop`. An
executable that opens no Runic Desktop window sets `RunicDesktopServer` to
`None`. See [Migrating to 1.0](migration-1.0.md#desktop-presentation-server).

### RUNICDESK005

The native Desktop server has no library for the build: a NativeAOT publish
that links the server's static library (`RunicDesktopNativeServerLinkage`
`Static`, the default) found none (`Runic.Desktop.NativeServer.Static.<rid>`,
which NuGet restores through `Runic.Desktop.NativeServer`'s `runtime.json` for a
runtime-specific restore), or `RunicDesktopNativeServerLibraryDirectory` names a
directory without `librax_server` or `rax_server`.

**Fix:** publish with the runtime identifier (`dotnet publish -r <rid>`
restores for it), use a runtime identifier the package supports (its README
lists them), or point `RunicDesktopNativeServerLibraryDirectory` at a directory
with the library, as `buildRunicApplication` does with its source build.

### RUNICDESK006

`RunicDesktopNativeServerLinkage` has a value other than `Static`, `Shared` or
`Application`. The property selects how a NativeAOT publish gets the native
Desktop server: `Static` links the package's static library into the
executable, `Shared` puts the shared library beside it, and `Application`
leaves the server to the application's own Rust static library, which links the
`runic-artifex-server` crate. It has no effect on builds that run under the JIT. Since
0.17.1.

**Fix:** set one of the three values, or remove the property for the default,
`Static`. See
[NativeAOT and an application's own Rust library](../packages/dotnet/Runic.Desktop.NativeServer/README.md#nativeaot-and-an-applications-own-rust-library).

### RUNICDESK007

A warning: `RunicDesktopServer` is `None`, but the project references
`Runic.Desktop.Kestrel` or `Runic.Desktop.NativeServer`. `None` declares an
executable that opens no Runic Desktop window, so it uses no presentation
server: a trimmed or NativeAOT build keeps neither, and a window would fail with
`presentation-server-unavailable`. Since 0.22; before, such an executable
served its windows from Kestrel when it referenced it.

**Fix:** remove `RunicDesktopServer` (or set it to `Native` or `Kestrel`) when
the executable opens windows, or remove the server package when it does not. If
the reference is intended, for example because a library brings it in, suppress
the warning with `<NoWarn>$(NoWarn);RUNICDESK007</NoWarn>`.

## Application icon

The `Runic.Platform` build targets derive the Windows `.ico` from the package
icon, `RunicPackageIcon` (by default `icons/icon.png`), when the project sets no
`ApplicationIcon`, and the `Runic.Platform.MacOS` targets the development
bundle's `icon.icns` ([the application icon](packaging.md#the-application-icon)).
They report these warnings for an icon they cannot use, and the build goes on
without the derived icon: the executable keeps the generic icon, and the
development bundle has none. Since 0.23. A project that treats warnings as
errors fails; fix the icon, or set `RunicApplicationIconGenerationEnabled` to
`false` to derive nothing.

### RUNICICON001

The package icon is not a PNG the build can read: another format under a `.png`
name, a damaged file (a wrong checksum, data that ends early), or a PNG larger
than 8192 px. The message says what is wrong.

**Fix:** save the icon as a PNG again, or point `RunicPackageIcon` at another
file.

### RUNICICON002

The package icon is not square. Each derived icon is a square, so the build does
not stretch or crop it.

**Fix:** make the icon square, with transparent margins if the artwork is not.

### RUNICICON003

The package icon is smaller than 256 px, the largest size in the `.ico`. The
build only scales down.

**Fix:** export the icon at 512 px (or 1024 px, which the Mac App Store needs);
at least 256 px is required.

## Navigation ReactiveUI build

The `Runic.Navigation.ReactiveUI` and `Runic.Navigation.ReactiveUI.Reactive`
build targets report this warning. `RUNICNAV001` was the experimental
Navigation ID until 0.7.0-preview.7; it is retired and not reused.

### RUNICNAV002

The project's resolved references, direct or through another package or
project, contain both navigation adapters: `Runic.Navigation.ReactiveUI`, which
uses ReactiveUI.Primitives, and `Runic.Navigation.ReactiveUI.Reactive`, which
uses ReactiveUI's System.Reactive flavor. An application uses one flavor. The
two adapters add extension methods with the same names in different
namespaces, so a file that imports both namespaces fails with CS0121, and
types of the two ReactiveUI distributions do not mix. Since 0.11; earlier
releases did not report the mix. When a `Runic.Application.Views` ReactiveUI
adapter is referenced too, [`RUNICBRIDGE023`](#runicbridge023) reports the mix
as an error instead.

**Fix:** keep one navigation adapter: `Runic.Navigation.ReactiveUI.Reactive`
with ReactiveUI's System.Reactive flavor, otherwise `Runic.Navigation.ReactiveUI`.
A reference that arrives through another project or package is changed there.

## Runic.Create

`dnx Runic.Create` reports these codes as the fault of a failed run. Human
output prints the link to the entry above the fault line; JSON output carries
the code in `fault.code`. Usage and validation errors (`RCREATE001`,
`RCREATE002`) exit with code 2, other failures with code 1, and a cancelled run
with code 130. See the
[Runic.Create guide](../tools/Runic.Create/README.md#exit-codes).

### RCREATE001

An option has a value that is not one of its choices, such as
`--frontend solid`. The message lists the choices.

**Fix:** pick one of the listed values.

### RCREATE002

The project name does not start with a letter or contains characters other
than letters, digits, `.`, `_` and `-`.

**Fix:** choose another name.

### RCREATE003

The destination directory exists and is not empty. Exit code 1.

**Fix:** choose another name or `--output`, or pass `--force` (since 0.11) to
create the project there anyway, as `dotnet new --force` does; files the
template writes replace existing files of the same name.

### RCREATE004

`dotnet new install` of the matching `Runic.Application.Templates` version
failed. Its output is printed before the fault.

**Fix:** check the NuGet source and network access, or pass
`--template-source` with a source that has the version.

### RCREATE005

`dotnet new runic-app` failed. Its output is printed before the fault. With
`--no-install`, the creator uses the installed template, and `dotnet new` fails
when none is installed.

**Fix:** correct the problem the template reports, then run the creator again.
With `--no-install`, install the template with the printed
`dotnet new install` command, or run the creator without `--no-install`.

## Privileged actions

The `Runic.Platform` build targets validate `RunicPrivilegedAction` and
`RunicPrivilegedCommand` items (`RUNICPA001` to `RUNICPA009`, `RUNICPA011` and
`RUNICPA015`) before any platform target reads
them; the `Runic.Platform.Linux.Polkit` targets then report `RUNICPA010`,
`RUNICPA012`, `RUNICPA013` and `RUNICPA014` before they write the Polkit policy.
The `Runic.Platform.Linux.Polkit`, `Runic.Platform.MacOS` and
`Runic.Platform.Windows.Elevation` targets report `RUNICPA108` for a renamed
build name.
The `PrivilegedActions` class is written only when none of them remains. See
[Declare privileged actions](../packages/dotnet/Runic.Platform/README.md#declare-privileged-actions)
and the [Polkit package guide](../packages/dotnet/Runic.Platform.Linux.Polkit/README.md).

### RUNICPA001

`RunicPrivilegedActionsClassName` is not a C# identifier, or
`RunicPrivilegedActionsNamespace` is not a C# namespace.

**Fix:** set a valid name, or remove the property to use `PrivilegedActions`
in the project's root namespace.

### RUNICPA002

Two items have the same action ID, or two actions generate the same property
name.

**Fix:** give each action its own ID; set `Name` on one of two actions whose
last ID segments are the same.

### RUNICPA003

Two actions cannot be told apart by their first argument: they have the same
`Verb`, even for different programs, or one of them has none. pkexec authorizes
an action by the program's real path and the verb, but the privileged program
sees only the verb, and one executable can be installed under several paths
(a wrapper script and its target). With a shared verb, pkexec could authorize
one action while the program acts on another.

**Fix:** give every action its own `Verb`. Use an action without a `Verb` only
when it is the only action.

### RUNICPA004

The action ID is not a lowercase reverse-DNS name with at least two segments,
such as `org.example.app.activate`.

**Fix:** use lowercase ASCII letters, digits and hyphens, separated by dots.

### RUNICPA005

`Program` is missing or is not an absolute, normalized path. pkexec matches
the policy against the program's real path.

**Fix:** set `Program` to the path the program is installed at, without
symbolic links, `.` or `..`, for example `$(MyInstallPrefix)/libexec/app/helper`.

### RUNICPA006

`Description` or `Message` is missing. The authentication dialog shows them.

**Fix:** describe the action and say why authentication is required.

### RUNICPA007

`Verb` is longer than 4096 characters or contains a NUL character.

**Fix:** use a short word such as `activate`.

### RUNICPA008

`ArgumentPattern`, or a pattern in a `RunicPrivilegedCommand`'s `Arguments`, is
not a valid regular expression on its own, or it needs
backtracking. It is compiled with `RegexOptions.NonBacktracking`, as at run
time, so backreferences and lookarounds are rejected here.

**Fix:** correct the pattern. It is matched against the whole argument. List
the allowed characters: `\d` and `\w` match every script's digits and letters,
a negated class such as `[^/]` matches a line break, an inline `(?i)` makes
`[a-z]` match the Kelvin sign (U+212A), and `(?x)` turns `#` into a comment.
The pattern must be valid both alone and anchored as `^(?:pattern)\z`.

### RUNICPA009

`MaximumArguments` is not a number from 0 to 64.

**Fix:** set the number of arguments the action accepts after its verb.

### RUNICPA010

`PolkitAllowGui` is neither `true` nor `false`.

**Fix:** set `true` only when the program opens windows as root; pkexec then
keeps `DISPLAY` and `XAUTHORITY`.

### RUNICPA011

The property name generated for the action is not a C# identifier, is the
class name, or is reserved: `All`, the property names platform packages
generate in other parts of the class (`MacOSDaemon`, `WindowsElevation`), and
any name in `RunicPrivilegedActionsReservedNames`.

**Fix:** set `Name` on the item.

### RUNICPA012

`PolkitAllowAny`, `PolkitAllowInactive` or `PolkitAllowActive` is not a Polkit authorization:
`no`, `yes`, `auth_self`, `auth_self_keep`, `auth_admin` or `auth_admin_keep`.

**Fix:** use one of them, or remove the metadata to require an administrator's
password each time (`auth_admin`).

### RUNICPA013

`Program`, `Description`, `Message`, `Verb` or `PolkitIconName` contains a line
break, another control character, a character XML cannot carry (such as
U+FFFE) or `]]>`. polkitd keeps only the last piece of text its XML parser
reports, and the parser splits text at line breaks.

**Fix:** write the text on one line, with characters XML allows.

### RUNICPA014

A warning: `PolkitAllowAny`, `PolkitAllowInactive` or `PolkitAllowActive` is `yes`, `auth_self`
or `auth_self_keep`. `yes` lets every matching subject run the program as root
without authenticating. `auth_self` accepts the user's own password, which any
process running as that user can effectively supply, so it does not stand
between the user's processes and root.

**Fix:** use `auth_admin` or `auth_admin_keep`. To let specific users or groups
run the action without a password, add a site rule in `/etc/polkit-1/rules.d`
on the machines that need it.

### RUNICPA015

A `RunicPrivilegedCommand` item is invalid: its `Action` names no
`RunicPrivilegedAction` item, its name is empty, longer than 4096 characters or
contains a NUL character, its action has another command with the same name,
`Arguments` has an empty pattern (two `;` in a row) or more than 63 patterns,
or `MinimumArguments` is not a number from 0 to the number of patterns. It is
also reported when an action sets `MaximumArguments` lower than one of its
commands needs, counting the command name.

**Fix:** set `Action` to an action's ID, give each command of an action its own
name, write one pattern per argument separated by `;` (`\x3B` for a semicolon
inside a pattern), and remove `MaximumArguments` from an action with commands,
which then accepts as many arguments as its longest command.

### RUNICPA101

A `Runic.Platform.MacOS` daemon setting is missing or invalid: once
`RunicMacPrivilegedDaemonLabel` is set, the label, `RunicMacBundleIdentifier`
(by default `ApplicationId`)
and `RunicMacPrivilegedDaemonIdentifier` must be reverse-DNS names,
`RunicMacTeamIdentifier` the ten-character Team ID (the daemon's launch
constraint names it), and `RunicMacPrivilegedDaemonProgram` the daemon
executable's path in the bundle, starting with `Contents/`. These macOS checks
(`RUNICPA101` to `RUNICPA107`) run after the shared ones; nothing is written
while any of them remains. See the
[macOS package guide](../packages/dotnet/Runic.Platform.MacOS/README.md#privileged-actions).

**Fix:** set the properties, or remove `RunicMacPrivilegedDaemonLabel` from a
project that does not build the macOS daemon.

### RUNICPA102

An action ID starts with a namespace macOS uses for its own authorization
rights or rules (`system.`, `config.`, `com.apple.`, `authenticate`, `admin`,
`allow` or `default`). The daemon defines a right named after each action ID,
and must not redefine one of the system's.

**Fix:** use the application's own reverse-DNS prefix.

### RUNICPA103

An action's `Message`, the text of the macOS authorization dialog, is longer
than 1024 characters or contains a NUL character.

**Fix:** shorten the message.

### RUNICPA104

`MacProgram` is not a path inside the bundle (such as `Contents/MacOS/helper`)
or a normalized absolute path: it is empty or has empty, `.` or `..` segments.

**Fix:** give the program's path inside the bundle, or its absolute path.

### RUNICPA105

`MacProgramIdentifier` is not a reverse-DNS code-signing identifier, or an
action runs a program from the bundle (a relative `MacProgram`) without one. A
program whose path someone other than root can change runs only when its
signature names this identifier.

**Fix:** set `MacProgramIdentifier` to the identifier the program is signed
with (`codesign --identifier`).

### RUNICPA106

The macOS privileged daemon (`RunicMacPrivilegedDaemon`) is published, or built
for a runtime identifier, without `PublishAot`. Code signing covers only the
executable; a JIT runtime also loads assemblies, `*.deps.json` and
`*.runtimeconfig.json` files that nothing checks, and the daemon runs as root.

**Fix:** set `PublishAot` to `true` in the daemon's project.

### RUNICPA107

An application with a macOS privileged daemon is published, or built for a
macOS runtime identifier, without `PublishAot` (outside
`RunicMacPrivilegedDevelopmentSigning`). The daemon trusts the application's signature, but a JIT runtime
reads startup hooks (`DOTNET_STARTUP_HOOKS`) and debugger and profiler settings
from whoever starts it, so code the application does not contain could run
under its signature.

**Fix:** publish the application with `PublishAot` set to `true`.

### RUNICPA108

The project still sets a privileged-action build name that has been renamed:
the property `RunicPolkitPolicy` (now `RunicPolkitPolicyCopyEnabled`),
`RunicMacLaunchDaemonPlist` (now `RunicMacLaunchDaemonPlistCopyEnabled`) or
`RunicElevatedHelper` (now `RunicWindowsElevatedHelper`), or the metadata
`AllowAny`, `AllowInactive`, `AllowActive`, `AllowGui` or `IconName` on a
`RunicPrivilegedAction` item (now `PolkitAllowAny`, `PolkitAllowInactive`,
`PolkitAllowActive`, `PolkitAllowGui` and `PolkitIconName`, like
`WindowsProgram` and `MacProgram`). The old names are no longer applied; each
one the project still sets is reported once, and the message names its
replacement. Design-time builds do not report it.

**Fix:** rename the property or metadata in the project file, its imports or the
command line, for example `<RunicElevatedHelper>` to
`<RunicWindowsElevatedHelper>` or `AllowActive="auth_admin_keep"` to
`PolkitAllowActive="auth_admin_keep"`.

## D-Bus source generator

`Runic.Platform.Linux.DBus` generates proxies and services from introspection XML
listed as `AdditionalFiles` with `RunicDBusMode` metadata. The
[package guide](../packages/dotnet/Runic.Platform.Linux.DBus/README.md#generate-proxies-and-services)
describes the metadata. Each diagnostic names the XML file and points at the
element or line it concerns. Nothing is generated for a file with an error; a
warning leaves the generated code unchanged.

### RUNICDBUS001

`RunicDBusMode` is neither `Proxy`, `Service` nor `Proxy,Service`.

**Fix:** use one of those values. Separate both modes with a comma, not a semicolon.

### RUNICDBUS002

The XML file has no `RunicDBusNamespace` metadata, nor its former name
`Namespace`.

**Fix:** add `RunicDBusNamespace="My.App.DBus"` to its `AdditionalFiles` item.

### RUNICDBUS003

`RunicDBusVisibility` is neither `internal` nor `public`.

**Fix:** use `internal`, the default, or `public`.

### RUNICDBUS004

The compiler could not read the XML file.

**Fix:** check that the file exists and is readable.

### RUNICDBUS005

The file is not well-formed XML; the message gives the parser's reason.

**Fix:** correct the XML, or replace it with the service's introspection data.

### RUNICDBUS006

The root element is not `<node>`.

**Fix:** wrap the interfaces in `<node>`, as `busctl introspect --xml-interface` writes them.

### RUNICDBUS007

An `<interface>` has no name, or a name that is not a D-Bus interface name,
such as one without a dot.

**Fix:** give the interface its full D-Bus name, such as `org.example.Player`.

### RUNICDBUS008

The `<node>` declares no `<interface>`.

**Fix:** add the interfaces to generate, or remove the file from `AdditionalFiles`.

### RUNICDBUS009

`RunicDBusClassName` is not a C# identifier, or names the proxy of a file with
more than one interface, which gets one proxy class per interface.

**Fix:** use an identifier, or split the interfaces into separate files to name each proxy.

### RUNICDBUS010

Two files would generate a type with the same name in one namespace, such as
two interfaces that both end in `.Player`.

**Fix:** set `RunicDBusClassName` on one of them, or give it another `Namespace`.

### RUNICDBUS011

An interface member cannot be generated: a method, signal or property name that
is not a D-Bus member name, an argument or property type that is not one
complete D-Bus type, or a property `access` other than `read`, `write` or
`readwrite`. The message names the member.

**Fix:** correct the XML.

### RUNICDBUS012

A generated member would take the name of another member of its class, so one
would hide, overload or duplicate the other. The message names the class, the
member and the D-Bus method, property or signal it was generated for. The
generator reports this instead of generating:

- a member with the name of a `DBusProxy` or `DBusObject` member, such as
  `CallAsync` for a method `Call`, `WatchSignalAsync` for a signal `Signal`,
  `Path` for a service property `Path` or `EmitSignal` for a service signal
  `Signal`;
- a member with the name of an `object` member or of the class itself;
- a member that another generated member already uses, such as
  `GetPropertiesAsync` for a property `Properties`, `WatchPropertiesAsync` for a
  signal `Properties`, `InterfaceName` in a proxy, or the same name from two
  interfaces of one service file, including two interfaces that end in the same
  name.

Before 0.9, the service-file duplicates were reported as RUNICDBUS011, and the
other collisions compiled with the inherited member silently hidden. Since 0.11 a
service property named `MaxConcurrentCalls` collides with the `DBusObject` member
of that name.

**Fix:** put interfaces whose members collide in separate files, or set
`RunicDBusClassName`. A D-Bus member whose name collides with an inherited member
cannot be generated; call it with `DBusProxy.CallAsync`, `GetPropertyAsync` or
`WatchSignalAsync`, or answer it in a `DBusObject` you write yourself, and leave it
out of the XML the generator reads.

### RUNICDBUS013

Warning. The XML has a standard D-Bus annotation, set to `true`, that the
generated code does not carry out:

- `org.freedesktop.DBus.Deprecated` on an interface, method, signal or property:
  the generated members are not marked `[Obsolete]`.
- `org.freedesktop.DBus.Method.NoReply` on a method, in `Proxy` mode: the
  generated `<Method>Async` waits for a reply that the service may never send.
  A service ignores the annotation, since a caller that asks for no reply
  discards one.

The file is generated as before; each annotation is reported once per file.
Annotations that are `false`, `org.freedesktop.DBus.Property.EmitsChangedSignal`,
which the generator applies, and annotations of other tools, such as
`org.gtk.GDBus.*`, are not reported.

**Fix:** for a deprecated member, prefer its replacement, and remove the
annotation from the project's copy of the XML, or the member if the application
does not use it. For a `NoReply` method, pass a cancellation token or set the
proxy's `Timeout`, or call it with `DBusProxy.CallAsync` and leave it out of the
XML. To accept the warning for the whole project, add `RUNICDBUS013` to
`NoWarn`.

## Privileged actions on Windows

The `Runic.Platform.Windows.Elevation` build targets report these errors for
the `WindowsProgram` metadata of `RunicPrivilegedAction` items and for a helper
project (`RunicWindowsElevatedHelper`). See the
[package guide](../packages/dotnet/Runic.Platform.Windows.Elevation/README.md).

### RUNICEL001

`WindowsProgram` is not a path relative to the application's directory or a
local absolute path: it contains `.` or `..`, is a network (`\\server\share`),
device (`\\?\`) or drive-relative (`C:helper.exe`) path, ends in a dot or space,
or contains a character Windows does not allow in a file name.

**Fix:** use a path such as `helpers\example-helper.exe`, or an absolute path
under `C:\Program Files`.

### RUNICEL002

An elevated helper is published, or built for a runtime identifier, without
`PublishAot`. An elevated process takes its environment from the user's
profile, which the user can write, and a JIT runtime loads startup hooks
(`DOTNET_STARTUP_HOOKS`), another runtime (`DOTNET_ROOT`) or a profiler
(`CORECLR_PROFILER`) from it.

**Fix:** set `PublishAot` to `true` in the helper's project.

### RUNICEL003

The helper's own `ApplicationManifest` does not request `requireAdministrator`.

**Fix:** remove `ApplicationManifest` to use the package's manifest, or set
`requestedExecutionLevel level="requireAdministrator"` in yours.

### RUNICEL004

The helper is not a console executable (`OutputType` `Exe`). Tools a GUI
helper starts would not inherit the output it sends to the application.

**Fix:** set `OutputType` to `Exe`; the provider starts the helper with its
window hidden.

### RUNICEL005

An elevated helper is published or linked with `EventSourceSupport` or
`IlcStandaloneGCSupport` turned on, or with `InvariantGlobalization` off, for
example through a global property or a later import that overrides the
package's values. The NativeAOT runtime reads its settings from the user's
environment before `Main`: EventPipe would connect to `DOTNET_DiagnosticPorts`
or write `DOTNET_EventPipeOutputPath`, the standalone GC would load
`DOTNET_GCPath`, and ICU could be loaded through
`DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU`.

**Fix:** remove the setting; the package turns them off and invariant
globalization on for helpers.

## Runic.Platform analyzers

The `Runic.Platform` package carries Roslyn analyzers for its API, with code
fixes in the IDE (since 0.14). They run in every project that uses
`Runic.Platform`, also one that gets it only through another package, such as
`Runic.Desktop` or a Views host adapter. They report while you type and in a
build.

### RUNICPLAT001

The title and command id of `new DesktopMenuItem(title, commandId)` look
swapped: the title is shaped like a command id (lower-case segments joined by
dots, underscores or hyphens, such as `file.new`) and the command id is not a
valid one (1 to 64 ASCII letters, digits, dots, underscores or hyphens) or reads
like a title (`New Window`). Both parameters are strings, so the order is not
checked by the compiler; the 0.11 rename from `DesktopMenuCommand(id, label)`
reversed it. An invalid command id also throws when the menu is set.

**Fix:** pass the title first, and name the arguments so the order reads in the
source: `new DesktopMenuItem(Title: "New Window", CommandId: "file.new")`. The
code fix does both. Suppress the warning for a title that really is shaped like
an id.

## Vite plugin

`@runic-artifex/vite-plugin-runic` reports these errors when Vite resolves its
configuration, and while `dotnet runic dev` serves, a frontend module that does
not compile. Numbers below `007` are not assigned. See the
[plugin guide](../packages/web/vite-plugin-runic/README.md).

### RUNICP007

`runic({ devtools: true })` is served without the Vite DevTools dock: the
plugins array has no `DevTools()` from `@vitejs/devtools`. (The Vite DevTools
dock is the bar that Vite adds to the page while it serves it, with a Runic
panel; it is not the browser's developer tools, which F12 or Inspect opens.)

**Fix:** install `@vitejs/devtools` and add `DevTools()` to the plugins, as the
`runic-app` template's Vite starters do, or leave `devtools` at its default,
`"auto"`, which shows the dock's Runic panel only when `DevTools()` is
registered.

### RUNICP008

A frontend module does not compile in the Vite development server that
`dotnet runic dev` runs: a syntax error in a `.ts`, `.tsx`, `.vue` or `.svelte`
file, an import that does not resolve, or another Vite plugin's error. Since
0.23 the plugin prints each such error as one line in MSBuild's form, with the
module's absolute path and the 1-based position, and the name of the Vite plugin
that failed at the start of the message, so that IDE error lists show it next to
the build's errors (see [IDE contract](../tools/dotnet-runic/README.md#ide-contract)).
`[runic] Frontend errors cleared` follows once the modules compile again. The
plugin prints nothing when Vite serves without `dotnet runic dev`. There is no
type check: a type error that Vite still compiles is not reported.

**Fix:** correct the module at the line and column given; the page's error
overlay and Vite's output show the same error with its code frame.
