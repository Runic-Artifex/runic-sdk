# Diagnostics

Every diagnostic ID that a Runic SDK package reports has an entry here, except
the experimental API ID (`RUNICWPF001`), which
[Experimental APIs](experimental.md) describes. Build errors, `dotnet runic`
failures, the asset packer and Runic.Create link to their entry at the release
tag of the package that reported them, so the text matches the version you use.
Change the tag in the address to read another release.

Each entry starts with what the ID means. **Fix** says what to change.

- [Bridge code generator (`RUNICBRIDGE`)](#bridge-code-generator)
- [`dotnet runic` (`RAPPCLI`, `RAPPDEV`, `RAPPSUP`, `RTKSIZE`, `RCLI`)](#dotnet-runic)
- [Asset packer (`RAS`)](#asset-packer)
- [Runic.Create (`RCREATE`)](#runiccreate)
- [Privileged actions (`RUNICPA`)](#privileged-actions)
- [D-Bus source generator (`RUNICDBUS`)](#d-bus-source-generator)

## Bridge code generator

`Runic.Application.Views` generates the Bridge and the TypeScript clients after the
model assembly compiles. It reports every ViewModel's first problem in one
build. An error names the type and member, points at its source line when the
assembly has a portable PDB (the default), and ends with the link to its entry.
The [generator section](../packages/dotnet/Runic.Application.Views/README.md#generator-diagnostics)
of the Runic.Application.Views guide has the same table.

### RUNICBRIDGE001

The generator invocation or build configuration is invalid, or the generator
failed internally. An example is a malformed `RunicApplicationFrontendCompositionType`.

**Fix:** correct the build property. Report an internal error with the
ViewModel that triggers it.

### RUNICBRIDGE002

A CommunityToolkit `ObservableValidator` ViewModel is in a Native AOT publish.
Its validation is not supported under Native AOT.

**Fix:** publish framework-dependent until its validation is verified under AOT.

### RUNICBRIDGE003

A state, command or interaction value type is not a supported bridge value. The
message names the member path, for example `EditorViewModel.Current.value`.

**Fix:** use a supported scalar, collection, public DTO, `[RunicUnion]` or
`[RunicBridgeCodec]` type. Exclude computed DTO properties with
`[RunicIgnore]`, or opt into unconditional `[JsonIgnore]` and `[JsonExtensionData]` as described under
[Shared DTO exclusions](../packages/dotnet/Runic.Application.Views/README.md#shared-dto-exclusions).
A `NavigationResult<T>` holds .NET content: return its `Outcome`, a
`NavigationOutcome`, instead.

### RUNICBRIDGE004

Two generated names collide: ViewModel names, presentation kinds, state wire
names, the reserved `revision` and `validation` fields, interactions, routes,
client members, or generated files (including a hand-written `types.ts`). A
type named like a generated bridge (`CounterBridge` beside `CounterViewModel`)
collides with it, and so would a generated TypeScript module that declares one
name twice.

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
`TContent`, or a `[RunicAppendOnlyText]` member is not a read-only `string`
state property.

**Fix:** add a getter, make content read-only to the web view, register a View
for the item or content type, give the region a specific content interface
or base class, or make an append-only text a read-only `string`.

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
it, so pushing or presenting it there throws `NotSupportedException`. Broad slot
types are not checked. The warning repeats
on builds that reuse the cached generation.

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

A warning from the `Runic.Application.Views` build targets. The project, one of
its imports (such as `Directory.Build.props`) or the command line sets a build
property or item by its name from 0.7.0-preview.6 or earlier. Since
0.7.0-preview.7 every property of these targets starts with
`RunicApplicationFrontend`. The old name still applies in this preview, and wins
when both are set; the next preview removes it. The message names the new
property. The
[renamed-names table](../packages/dotnet/Runic.Application.Views/README.md#moved-and-renamed-since-070-preview6)
lists every rename.

**Fix:** rename the property or item, for example
`<RunicBridgeFrontendDir>` to `<RunicApplicationFrontendDirectory>`. A condition
on `RunicBridgeBootstrap` reports nothing, but should read
`RunicApplicationFrontendBootstrap` before the next preview.

### RUNICBRIDGE018

The `Runic.Application.Views` build targets report this error. A project
declares Runic Views and generates its composition
(`RunicApplicationFrontendCompositionType`), but a referenced assembly already
declares a type of the same name. Both would define `AddRunicViews()`, so the
call would be ambiguous (`CS0121`). It typically follows an upgrade from
0.7.0-preview.6: the WPF adapter now generates in the application, whose default
composition name is `<project name>.RunicBridgeComposition`, and the model
project had used that name for its own composition. A project without Views of
its own is not affected; its bootstrap build uses the referenced type.

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
the generated output.

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
Bridge code that the earlier build generated in
`obj/<Configuration>/<TargetFramework>/runic-bridge/` instead of running the
generator. That code, or the composition that
`RunicApplicationFrontendCompositionType` names, is not there.

**Fix:** build the project first with the same `Configuration`,
`TargetFramework` and `PublishAot`, then publish with `--no-build`, or publish
without `--no-build`.

## dotnet runic

`dotnet runic` reports these codes as the fault of a failed command, except
`RCLI8101` and `RCLI8102`, which mark individual doctor checks. Human
output prints the link to the entry above the fault line; JSON output carries
the code in `fault.code`. Usage errors exit with code 2, other failures with
code 1. An unexpected internal failure reports `RCLI5000`, exits with code 3
and prints the exception to standard error; include it when you report the
problem. See the [`dotnet runic` guide](../tools/dotnet-runic/README.md).

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
`--aot` or `--self-contained` is used without `--rid`.

**Fix:** pass a runtime identifier such as `linux-x64`, `win-x64` or
`osx-arm64` with `--rid`.

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

### RAPPSUP001

`support` received a `--mode` other than `preview`, `collect` or `remove`.

**Fix:** pass one of those modes.

### RAPPSUP002

`support --mode collect` has no `--destination`.

**Fix:** pass the envelope file with `--destination`.

### RAPPSUP003

The `--destination` file of `collect` already exists.

**Fix:** choose a new file, or remove the old envelope with `--mode remove`.

### RAPPSUP004

The support envelope would exceed its size limit.

**Fix:** collect from a smaller Editor diagnostic ZIP.

### RAPPSUP005

`support --mode remove` was given a collector source such as
`--editor-diagnostics`.

**Fix:** pass only `--destination`.

### RAPPSUP006

`support --mode remove` has no `--destination`.

**Fix:** pass the envelope file with `--destination`.

### RAPPSUP007

The `--destination` file of `remove` does not exist.

**Fix:** pass the path of an envelope that `collect` wrote.

### RAPPSUP008

`preview` or `collect` has no collector.

**Fix:** select the Editor collector with `--editor-diagnostics <zip>`.

### RAPPSUP009

The Editor collector source is not a `.zip` file.

**Fix:** pass the diagnostic ZIP that the Editor exported.

### RAPPSUP010

The Editor diagnostic ZIP is missing or exceeds its size limit.

**Fix:** export the diagnostics from the Editor again and pass that file.

### RAPPSUP011

The Editor diagnostic ZIP has an invalid number of entries.

**Fix:** pass the ZIP exactly as the Editor exported it.

### RAPPSUP012

The Editor diagnostic ZIP does not contain a bounded `diagnostics.json`.

**Fix:** pass the ZIP exactly as the Editor exported it.

### RAPPSUP013

The Editor diagnostic source is not a valid ZIP file.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP014

The Editor diagnostic summary is not valid JSON.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP015

The diagnostic summary uses a schema that this version of `dotnet runic` does
not support.

**Fix:** use a `dotnet runic` from the same release as the Editor.

### RAPPSUP016

The diagnostic groups in the summary are invalid or exceed their limit.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP017

The `--destination` file of `remove` is not a support envelope created by
`support --mode collect`.

**Fix:** pass the envelope that `collect` wrote; `remove` deletes nothing else.

### RAPPSUP018

The diagnostic summary omits a required field.

**Fix:** export the diagnostics from the Editor again, with the Editor and
`dotnet runic` from the same release.

### RAPPSUP019

A text field of the diagnostic summary is not a string.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP020

A count in the diagnostic summary is not a non-negative integer.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP021

A flag in the diagnostic summary is not `true` or `false`.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP022

A section of the diagnostic summary is not a JSON object.

**Fix:** export the diagnostics from the Editor again.

### RAPPSUP023

The diagnostic summary contains fields that the envelope does not support.

**Fix:** use a `dotnet runic` from the same release as the Editor.

### RAPPSUP024

The diagnostic summary contains a path or other sensitive text, which a
support envelope must not include.

**Fix:** export the diagnostics from the Editor again; it removes paths from
the summary.

### RAPPSUP025

`support` could not read or write a required local file.

**Fix:** check that the ZIP and the destination folder exist and that you can
read and write them.

### RTKSIZE1001

`size` received invalid options: no `--runtime`, `--verify-argument` without
`--verify`, or a `--report` path that already exists.

**Fix:** pass `--runtime`, for example `linux-x64`, add `--verify`, or choose a
new report path; `size` does not overwrite earlier reports. See
[Measure size](../tools/dotnet-runic/README.md#measure-size).

### RTKSIZE1002

`size` could not evaluate the project's publish settings with `dotnet msbuild`.
The message includes the MSBuild output.

**Fix:** check that the project restores and builds for the selected runtime.

### RCLI8101

A doctor check failed. `doctor --output json` reports one diagnostic with this
code for each failed check, named by its check id.

**Fix:** follow the remediation of the check. See
[Doctor JSON output](../tools/dotnet-runic/README.md#doctor-json-output).

### RCLI8102

A doctor check passed with a warning. `doctor --output json` reports one
diagnostic with this code for each such check.

**Fix:** follow the remediation of the check, or accept the warning.

## Asset packer

`Runic.Assets.Packer`, which the Runic.Assets build runs, reports these codes.
Human output writes the message and the link to standard error. See the
[packer guide](../tools/Runic.Assets.Packer/README.md#exit-codes-and-errors).

### RAS1001

The source directory does not exist. Exit code 3.

**Fix:** build the frontend first, or point the archive at its output folder,
such as Vite's `dist`.

### RAS1002

The entry point does not exist in the source directory or was excluded. Exit
code 4.

**Fix:** pass the entry document with `--entry-point`, or remove it from the
exclusions.

### RAS1003

The archive could not be packed, for example because of an invalid asset path,
a symbolic link or an I/O error. Exit code 5.

**Fix:** correct the path or file that the message names.

### RAS1004

The pinned directory mode, the default without `--trusted-generated-output`,
requires Linux. Exit code 5.

**Fix:** pass `--trusted-generated-output` for output of the current build, as
the MSBuild integration does. See
[`--trusted-generated-output`](../tools/Runic.Assets.Packer/README.md#--trusted-generated-output).

## Runic.Create

`dnx Runic.Create` reports these codes as the fault of a failed run. Human
output prints the link to the entry above the fault line; JSON output carries
the code in `fault.code`. See the [Runic.Create guide](../tools/Runic.Create/README.md).

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

### RCREATE006

`--output` (`-o`) and its former name `--directory` were both passed with
different values.

**Fix:** pass the destination once, with `--output`.

## Privileged actions

The `Runic.Platform` build targets validate `RunicPrivilegedAction` and
`RunicPrivilegedCommand` items (`RUNICPA001` to `RUNICPA009`, `RUNICPA011` and
`RUNICPA015`) before any platform target reads
them; the `Runic.Platform.Linux.Polkit` targets then report `RUNICPA010`,
`RUNICPA012`, `RUNICPA013` and `RUNICPA014` before they write the Polkit policy.
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

`AllowGui` is neither `true` nor `false`.

**Fix:** set `true` only when the program opens windows as root; pkexec then
keeps `DISPLAY` and `XAUTHORITY`.

### RUNICPA011

The property name generated for the action is not a C# identifier, is the
class name, or is reserved: `All`, the property names platform packages
generate in other parts of the class (`MacOSDaemon`, `WindowsElevation`), and
any name in `RunicPrivilegedActionsReservedNames`.

**Fix:** set `Name` on the item.

### RUNICPA012

`AllowAny`, `AllowInactive` or `AllowActive` is not a Polkit authorization:
`no`, `yes`, `auth_self`, `auth_self_keep`, `auth_admin` or `auth_admin_keep`.

**Fix:** use one of them, or remove the metadata to require an administrator's
password each time (`auth_admin`).

### RUNICPA013

`Program`, `Description`, `Message`, `Verb` or `IconName` contains a line
break, another control character, a character XML cannot carry (such as
U+FFFE) or `]]>`. polkitd keeps only the last piece of text its XML parser
reports, and the parser splits text at line breaks.

**Fix:** write the text on one line, with characters XML allows.

### RUNICPA014

A warning: `AllowAny`, `AllowInactive` or `AllowActive` is `yes`, `auth_self`
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

## D-Bus source generator

`Runic.Platform.Linux.DBus` generates proxies and services from introspection XML
listed as `AdditionalFiles` with `RunicDBusMode` metadata. The
[package guide](../packages/dotnet/Runic.Platform.Linux.DBus/README.md#generate-proxies-and-services)
describes the metadata. Each error names the XML file; nothing is generated for it.

### RUNICDBUS001

`RunicDBusMode` is neither `Proxy`, `Service` nor `Proxy,Service`.

**Fix:** use one of those values. Separate both modes with a comma, not a semicolon.

### RUNICDBUS002

The XML file has no `Namespace` metadata.

**Fix:** add `Namespace="My.App.DBus"` to its `AdditionalFiles` item.

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
complete D-Bus type, a property `access` other than `read`, `write` or
`readwrite`, or two interfaces of one service file that would generate the same
member. The message names the member.

**Fix:** correct the XML, or put interfaces whose members collide in separate files.

## Privileged actions on Windows

The `Runic.Platform.Windows.Elevation` build targets report these errors for
the `WindowsProgram` metadata of `RunicPrivilegedAction` items and for a helper
project (`RunicElevatedHelper`). See the
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
