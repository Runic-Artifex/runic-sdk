# Diagnostics

Every diagnostic ID that a Runic SDK package reports has an entry here, except
the experimental API IDs (`RUNICNAV001`, `RUNICWPF001`), which
[Experimental APIs](experimental.md) describes. Build errors, `dotnet runic`
failures, the asset packer and Runic.Create link to their entry at the release
tag of the package that reported them, so the text matches the version you use.
Change the tag in the address to read another release.

Each entry starts with what the ID means. **Fix** says what to change.

- [Bridge code generator (`RUNICBRIDGE`)](#bridge-code-generator)
- [`dotnet runic` (`RAPPCLI`, `RAPPDEV`, `RAPPSUP`, `RTKSIZE`, `RCLI`)](#dotnet-runic)
- [Asset packer (`RAS`)](#asset-packer)
- [Runic.Create (`RCREATE`)](#runiccreate)

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
`[RunicIgnore]`, or opt into unconditional `[JsonIgnore]` as described under
[Shared DTO exclusions](../packages/dotnet/Runic.Application.Views/README.md#shared-dto-exclusions).
A `NavigationResult<T>` holds .NET content: return its `Outcome`, a
`NavigationOutcome`, instead.

### RUNICBRIDGE004

Two generated names collide: ViewModel names, presentation kinds, state wire
names, the reserved `revision` and `validation` fields, interactions, routes,
client members, or generated files (including a hand-written `types.ts`).

**Fix:** rename one member, or set a wire name with `[RunicAlias]`.

### RUNICBRIDGE005

The model assembly or one of its dependencies could not be loaded.

**Fix:** check the bootstrap output and that every Runic package has the same
version. `dotnet runic doctor` reports packages from different releases.

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
`TContent`.

**Fix:** add a getter, make content read-only to the web view, register a View
for the item or content type, or give the region a specific content interface
or base class.

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
slot but has no View, so pushing or presenting it there throws
`NotSupportedException`. Broad slot types are not checked. The warning repeats
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

## dotnet runic

`dotnet runic` reports these codes as the fault of a failed command, except
`RCLI8101` and `RCLI8102`, which mark individual doctor checks. Human
output prints the link to the entry above the fault line; JSON output carries
the code in `fault.code`. Usage errors exit with code 2, other failures with
code 1. See the [`dotnet runic` guide](../tools/dotnet-runic/README.md).

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

The Window process or the frontend development server exited unexpectedly or
did not become ready in time.

**Fix:** read the process output above the fault. For a development server,
check that the configured dev script starts with the frontend's own command,
such as `npm run dev`.

### RAPPDEV1008

`--no-restore` was given but the frontend packages are not installed.

**Fix:** install them with the frontend's package manager, or omit
`--no-restore`.

### RAPPDEV1009

A Window entry page does not exist: a Vite build input or the configured
development document names a file that is missing.

**Fix:** create the page or remove it from `build.rolldownOptions.input`.

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
