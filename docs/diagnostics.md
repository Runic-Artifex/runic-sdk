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
- [Navigation ReactiveUI build (`RUNICNAV`)](#navigation-reactiveui-build)
- [Runic.Create (`RCREATE`)](#runiccreate)
- [Privileged actions (`RUNICPA`)](#privileged-actions)
- [D-Bus source generator (`RUNICDBUS`)](#d-bus-source-generator)
- [Privileged actions on Windows (`RUNICEL`)](#privileged-actions-on-windows)
- [Vite plugin (`RUNICP`)](#vite-plugin)

## Bridge code generator

`Runic.Application.Views` generates the Bridge and the TypeScript clients after the
model assembly compiles. It reports every ViewModel's first problem in one
build. An error names the type and member, points at its source line when the
assembly has a portable PDB (the default), and ends with the link to its entry.
The `Runic.Application.Views` build targets report the codes that concern the
frontend and the build itself. Warnings don't fail the build unless the project
treats warnings as errors; the generator's warnings repeat on builds that reuse
the cached generation. Every other code fails the build. The
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
| [`RUNICBRIDGE003`](#runicbridge003) | Error | Generator | A value type that is not a supported bridge value |
| [`RUNICBRIDGE004`](#runicbridge004) | Error | Generator | Two generated names collide, or a command's client member is reserved in TypeScript |
| [`RUNICBRIDGE005`](#runicbridge005) | Error | Generator | An assembly, a Runic View or a bridged member's type could not be loaded |
| [`RUNICBRIDGE006`](#runicbridge006) | Error | Generator | No Window or View class, or an invalid class or View contract |
| [`RUNICBRIDGE007`](#runicbridge007) | Error | Generator | A ViewModel class has an unsupported shape |
| [`RUNICBRIDGE008`](#runicbridge008) | Error | Generator | A state property, ViewModel content member or append-only text has an unsupported shape |
| [`RUNICBRIDGE009`](#runicbridge009) | Error | Generator | A command has an unsupported name or shape |
| [`RUNICBRIDGE010`](#runicbridge010) | Error | Generator | A `[RunicCollection]` member is not a keyed collection of DTO rows |
| [`RUNICBRIDGE011`](#runicbridge011) | Error | Generator | A ReactiveUI interaction is not a get-only property |
| [`RUNICBRIDGE012`](#runicbridge012) | Error | Generator | A `[RunicFailure]` declaration is misplaced, repeated or names an unsupported type |
| [`RUNICBRIDGE013`](#runicbridge013) | Warning | Generator | A ViewModel that a content slot admits has no View |
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
| [`RUNICBRIDGE025`](#runicbridge025) | Error | Generator | A ReactiveUI command or interaction without the Runic ReactiveUI adapter of its flavor |
| [`RUNICBRIDGE026`](#runicbridge026) | Error | Build targets | The frontend declares dependencies but has no lock file for the default install |
| [`RUNICBRIDGE027`](#runicbridge027) | Error | Generator | A `[RunicCommandKey]` declaration is misplaced, or its resolver is missing or has an unsupported shape |

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
names, the reserved `revision` and `validation` fields, interactions, routes,
client members, or generated files (including a hand-written `types.ts`). A
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
unique letters-and-digits name.

### RUNICBRIDGE007

A ViewModel class has an unsupported shape. It does not implement
`INotifyPropertyChanged`, is not a public top-level class, or has no state,
command or interaction.

**Fix:** change the ViewModel declaration.

### RUNICBRIDGE008

A state property or ViewModel content member has an unsupported shape. A state
property has no public getter or an empty wire name, ViewModel content or a
`NavigationRegion<TContent>` slot has a public setter, a ViewModel collection's
item type has no registered View, or a region slot is a
`NavigationRegion<object>` or has no ViewModel with a registered View that is a
`TContent`, or a `[RunicAppendOnlyText]` member is not a read-only `string` or
`string?` state property.

**Fix:** add a getter, make content read-only to the web view, register a View
for the item or content type, give the region a specific content interface
or base class, or make an append-only text a read-only `string` or `string?`,
or remove the attribute.

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

A ReactiveUI interaction has no public getter or has a public setter.

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
without a View could fill. The warning repeats on builds that reuse the cached
generation.

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
the new property.

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
host that the application does not reference. Pages that load
`runic-desktop-views.js` or `runic-cswebui.js` directly choose their host at run
time and report nothing; libraries and test projects report nothing either.

**Fix:** set `RunicApplicationHost` to `desktop` or `cswebui` in the project or
on the command line, for example `dotnet run -p:RunicApplicationHost=desktop`,
to the host that the application runs on.

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

**Fix:** run the package manager's install once (`npm install`, `pnpm install`
or `bun install`) and commit the lock file it writes. To install without a lock
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

## dotnet runic

`dotnet runic` reports these codes as the fault of a failed command, except
`RCLI8101` and `RCLI8102`, which mark individual doctor checks. Human
output prints the link to the entry above the fault line; JSON output carries
the code in `fault.code`. Usage errors exit with code 2, other failures with
code 1, and a cancelled command reports `RCLI4000` and exits with code 130. An
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
no Translations tool to forward to: neither `runic-translations` on `PATH` nor
a local tool manifest that lists `dotnet-runic-translations`.

**Fix:** install the tool, for example with `dotnet tool install
dotnet-runic-translations`, and run `runic-translations support
preview|collect|remove`. See
[Support envelopes](../tools/dotnet-runic/README.md#support-envelopes).

### RAPPDEV1002

The project cannot be selected: the given project does not exist or is not a
`.csproj`, or the directory has no `.csproj` or several.

**Fix:** pass the project with `--project`.

### RAPPDEV1003

MSBuild could not evaluate the project.

**Fix:** run `dotnet build` on the project and fix the error it reports.

### RAPPDEV1004

A required program could not be started, for example `dotnet`, Node.js, Bun or
the package manager.

**Fix:** install the program and make sure it is on `PATH`.
`dotnet runic doctor` lists the runtime and package manager the project needs.

### RAPPDEV1005

The project is not a Runic Views Window project that `dev` can run, or its
frontend configuration is invalid. Examples: the project does not set
`RunicApplicationFrontendWindowProject=true`, has no `RunicApplicationFrontendDirectory` with a
`package.json`, names an unsupported package manager or development server, or
a Vite input or development document lies outside the web root.

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

`dotnet runic dev` picks a free loopback port for the development server, and
another process can take it before the server binds it. When the server exits
reporting that its port is in use, the command starts it again on a new port,
up to three times, and fails with this diagnostic only if every port was taken.

### RAPPDEV1008

`--no-restore` was given but the frontend packages are not installed.

**Fix:** install them with the frontend's package manager, or omit
`--no-restore`.

### RAPPDEV1009

A Window entry page does not exist: a Vite build input or the configured
development document names a file that is missing.

**Fix:** create the page or remove it from `build.rolldownOptions.input`. See
[A Window with its own page](../packages/dotnet/Runic.Application.Views/README.md#a-window-with-its-own-page).

### RAPPDEV1010

`dev` could not read the build inputs from the Vite configuration.

**Fix:** check that `vite build` works in the frontend directory.

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
code 2. Before 0.9 this was `RTKSIZE1001`.

**Fix:** choose a new report path, or omit `--report` for a new
`runic-size-<runtime>-<UTC time>.json`.

### RAPPSIZE1004

`size` could not evaluate the project's publish settings with `dotnet msbuild`.
The message includes the MSBuild output. This is a command failure, exit code 1.
Before 0.9 this was `RTKSIZE1002`.

**Fix:** check that the project restores and builds for the selected runtime.

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

`Runic.Assets.Packer`, which the Runic.Assets build runs, reports the
`RAS1xxx` codes. Human output writes the message and the link to standard
error. See the
[packer guide](../tools/Runic.Assets.Packer/README.md#exit-codes-and-errors).
The `Runic.Assets` build targets report the `RAS2xxx` codes.

### RAS1001

The source directory does not exist. Exit code 2.

**Fix:** build the frontend first, or point the archive at its output folder,
such as Vite's `dist`.

### RAS1002

The entry point does not exist in the source directory or was excluded. Exit
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

## Runic Desktop build

The `Runic.Desktop` build targets report these errors.

### RUNICDESK001

`RunicDesktopMinimalHost` has a value other than `true` or `false`.

**Fix:** set it to `true` or `false`, or remove it for the default, `false`.

### RUNICDESK002

A Native AOT publish for `win-x64` or `win-arm64` links the WebView2 loader
statically, but the restored `Microsoft.Web.WebView2` package has no
`WebView2LoaderStatic.lib` for the runtime identifier.

**Fix:** restore the project for the runtime identifier again, so the
`Microsoft.Web.WebView2` native SDK assets are present.

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
the code in `fault.code`. Usage and validation errors exit with code 2, other
failures with code 1, and a cancelled run with code 130. See the
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

The destination directory exists and is not empty.

**Fix:** choose another name or `--output`.

### RCREATE004

`dotnet new install` of the matching `Runic.Application.Templates` version
failed. Its output is printed before the fault.

**Fix:** check the NuGet source and network access, or pass
`--template-source` with a source that has the version.

### RCREATE005

`dotnet new runic-app` failed. Its output is printed before the fault.

**Fix:** correct the problem the template reports, then run the creator again.

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

## Vite plugin

`@runic-artifex/vite-plugin-runic` reports these errors when Vite resolves its
configuration. Numbers below `007` are not assigned. See the
[plugin guide](../packages/web/vite-plugin-runic/README.md).

### RUNICP007

`runic({ devtools: true })` is served without the Vite DevTools plugin: the
plugins array has no `DevTools()` from `@vitejs/devtools`.

**Fix:** install `@vitejs/devtools` and add `DevTools()` to the plugins, or
leave `devtools` at its default, `"auto"`, which shows the Runic dock only when
DevTools is registered.
