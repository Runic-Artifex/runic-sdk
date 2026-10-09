# Runic.Application

Typed .NET Windows and Views with generated TypeScript clients. C# ViewModels
own application state, commands, and operation lifetimes; a React, Vue,
Svelte, Angular, or plain TypeScript frontend renders them through a generated
client. The package's types are in the `Runic.Application.Views` namespace.

## Install

Reference a host adapter; it brings this package and its build targets:

```sh
dotnet add package Runic.Application.CsWebUi --prerelease   # CS-WebUI browser or WebView window
dotnet add package Runic.Application.Desktop --prerelease   # or: Runic Desktop native host
dotnet add package CommunityToolkit.Mvvm                     # or ReactiveUI with Runic.Application.ReactiveUI
dotnet add package Microsoft.Extensions.DependencyInjection  # ServiceCollection; the adapters need only the abstractions
```

To start a new application, run the
[guided creator](https://docs.runic-artifex.eu/getting-started/)
(`dnx Runic.Create@<VERSION>`) or the `dotnet new runic-app` template, then
`dotnet tool restore` and `dotnet runic dev`.

## A minimal Window

In a project named `MyApp`:

```csharp
// Counter.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views.CsWebUi;

namespace MyApp;

public sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty] private int count;

    [RelayCommand]
    private void Increment() => Count++;
}

// Selecting the Window makes the build generate Frontend/src/generated/counter.ts.
public sealed partial class CounterWindow(CsWebUiBridgeWindow<CounterViewModel> host)
    : CsWebUiWindow<CounterViewModel>(host);
```

```csharp
// Program.cs
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using MyApp;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();
using var provider = services.BuildServiceProvider();

await using (var window = provider.OpenWindow<CounterWindow, CounterViewModel>(host => new CounterWindow(host)))
{
    window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();
```

```ts
import { connectCounter } from "./generated/counter.js";

const counter = await connectCounter();
counter.subscribe(state => { document.querySelector("#count")!.textContent = String(state.count); });
document.querySelector("#increment")!.addEventListener("click", () => void counter.increment());
```

Generated modules import the shared browser runtime, so the frontend installs
`@runic-artifex/views` (`npm install @runic-artifex/views@preview`) and bundles
its entry. `connectCounter()` returns a `CounterClient`; `subscribe` delivers
the current state first, calls reject with the runtime's `BridgeError`, and
`dispose()` releases the connection. `@runic-artifex/react`,
`@runic-artifex/vue`, `@runic-artifex/svelte` (`useView`) and
`@runic-artifex/angular` (`injectView()`) bind clients to component
lifetimes.

`Frontend/index.html` loads `webui.js` and the host's client script
(`runic-cswebui.js` or `runic-desktop-views.js`) before the module. The
[First Window example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window)
is this application with a complete frontend.

## Windows, Views, and composition

A partial `RunicWindow<TViewModel>` declares a window and its root ViewModel; a
partial `RunicView<TViewModel>` selects the content presented for a ViewModel
property. `[RunicViewContract("name")]` adds a second View for the same
ViewModel. The build compiles the application, inspects these classes after the
MVVM source generators run, and generates C# attachments and one ordinary
TypeScript module per ViewModel.

A View is a logical .NET presentation object with typed `DataContext`. Its
browser counterpart can be a plain TypeScript function or a framework
component. The browser acknowledges mount and unmount, so a content session
creates a fresh View for each presentation and releases it when the outlet
changes. The ViewModel belongs to its application or window DI scope and may
survive View changes. Window-local routes and operation admission are
host-neutral; native window creation belongs to the host adapter.

The generated composition class (`<ProjectName>.RunicBridgeComposition`,
or `RunicBridgeCompositionType`) provides two registrations:

- `AddRunicViews()` registers the generated Bridges, every non-Window View as
  transient, and `ServiceProviderViewLocator` as the default
  `IRunicViewLocator`. Register an earlier `IRunicViewLocator` or View to
  replace a default.
- `AddRunicBridges()` registers only the Bridges, for applications that create
  Views themselves or use another locator, such as `ReactiveRunicViewLocator`.

ViewModels stay explicit registrations because their lifetime is an
application decision; a Window's root ViewModel must be scoped. The
registrations are generated code, so they need no reflection and are
NativeAOT-safe.

Presented content is bound to the window's model context, and keeps its
checked-field registries, only while it is attached. Replacing, clearing, or
pruning it releases both; the window keeps a weak identity so presenting the same
object again returns the same reference. When the browser still shows a suspended
reference, as after `Main = b; Main = a;` in one command, its mount resumes on the
re-attached bridge with a fresh View. Snapshot revisions increase across the whole
window, so a re-attached route never publishes an older revision. The
[wire protocol](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/README.md)
describes these rules.

Generated clients expose a snapshot, subscriptions, typed property setters,
commands, and disposal. Calls reject with typed errors. Accepted operations
remain owned by .NET across a browser reload; a reconnect gets a new snapshot.
A transport failure after acceptance can leave completion unknown, so callers
should inspect state before retrying a non-idempotent command. The generated
field-write API returns receipts and version conflicts; frontend form helpers
must await pending edits before issuing Save.

The runtime emits state and replies with generated and explicit
`Utf8JsonWriter` code. The build tool inspects the compiled application;
release runtime serialization does not reflect over ViewModel members. The
first-window example is exercised through Native AOT and Chromium in CI. The
generator's errors are listed under [Generator diagnostics](#generator-diagnostics).

The [First Window](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window),
[CommunityToolkit Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first), and
[Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-reactive-views)
examples exercise the packaged graph and generated client.

## Generated TypeScript

Each ViewModel gets a module named after it (`EditorViewModel` becomes
`editor.ts`) with its state interface, client interface and `connect`
function. Every enum, DTO and union in the contract becomes one exported type
named after its C# type, declared once in `types.ts` beside the modules. Next
to each client, `editor.mock.ts` exports a typed test double, `mockEditor`; see
[Testing](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).
Applications import it only from tests, so it is not bundled. Each
module imports the named types it uses and re-exports them, so
`import type { DocumentPane } from "./generated/document.js"` works too:

```csharp
/// <summary>A pane of the document page.</summary>
public enum DocumentPane { Editor, Preview }

/// <summary>The pane the document currently shows.</summary>
public DocumentPane ActivePane => ...;
```

```ts
/** A pane of the document page. */
export type DocumentPane = "Editor" | "Preview";
```

- An enum is a union of its wire names (the C# names, or `[RunicAlias]`), so
  a `switch` over it can be exhaustive.
- A DTO is an interface with its wire member names. A `[RunicUnion]` is a
  union of its cases, discriminated by `$case`.
- Two C# types with the same name, or a name that collides with a generated
  declaration such as `EditorState`, are qualified with their namespace
  segments, for example `NotesItem` and `TasksItem`.
  Names are stable while no new collision appears: adding a same-named type,
  even in another ViewModel, qualifies the existing one too. Import the name
  your frontend uses and expect a compile error, not a silent change, when
  that happens. Generic DTOs append their arguments, for example
  `PageOfNoteRow` or `BoxOfArrayOfInt32`, and an enum without cases is
  `never`. A C# type named like a TypeScript reserved word is qualified the
  same way.
- A generic DTO member declared `T` is nullable where the type argument is:
  `value: string` in `Box<string>` and `value: string | null` in
  `Box<string?>`, and the generated .NET reader rejects `null` for the
  former. A member declared `T?` is always nullable. Members inherited from a
  generic base class follow the derived type's arguments, and a type without
  annotations, such as `[RunicCommandInput(typeof(Box<string>))]` or a union
  case, is non-nullable throughout. Uses whose members differ get separate
  declarations, for example `BoxOfString` and `BoxOfNullableOfString`; a type
  used only as `Box<string?>` keeps the plain name `BoxOfString`.
- A content property is the union of the `PageReference` types of the
  ViewModels it can present, such as
  `DocumentPageReference | HomePageReference`, with `| null` when it is
  nullable. A `NavigationRegion<TContent>` slot always includes `| null`,
  because a region can be empty; see [Navigation](#navigation-experimental).
- `<summary>` comments on ViewModels, state properties, commands, interactions,
  DTO types and members, and enum cases become TSDoc. The bootstrap pass writes
  the XML documentation file for this; a project that already writes one keeps
  its own. Set `RunicBridgeDocumentation=false` to skip it. A separate model
  assembly (`RunicBridgeModelAssembly`) needs its XML documentation file beside
  it.
- A command argument keeps the parameter name of its CommunityToolkit
  `[RelayCommand]` method. Other commands name it after the argument's C# type,
  or `input` for scalars.

## Generator diagnostics

The generator reports every ViewModel's first problem in one build. An error
names the type and member, and points at its source line when the inspected
assembly has a portable PDB (the default). A member generated by a source
generator, such as a CommunityToolkit `[ObservableProperty]`, points at its
`[RelayCommand]` method or its declaring class.

| ID | Problem | Fix |
| --- | --- | --- |
| `RUNICBRIDGE001` | Invalid generator invocation or build configuration, such as a malformed `RunicBridgeCompositionType`; also an internal generator error. | Correct the build property. Report an internal error with the ViewModel that triggers it. |
| `RUNICBRIDGE002` | A CommunityToolkit `ObservableValidator` ViewModel in a Native AOT publish. | Publish framework-dependent until its validation is verified under AOT. |
| `RUNICBRIDGE003` | A state, command, or interaction value type is not a supported bridge value. The message names the member path, for example `EditorViewModel.Current.value`. | Use a supported scalar, collection, public DTO, `[RunicUnion]` or `[RunicBridgeCodec]` type. Exclude computed DTO properties with `[RunicIgnore]`, or opt into unconditional `[JsonIgnore]` as described under [Shared DTO exclusions](#shared-dto-exclusions). |
| `RUNICBRIDGE004` | Two generated names collide: ViewModel names, presentation kinds, state wire names, the reserved `revision` and `validation` fields, interactions, routes, client members, or generated files (including a hand-written `types.ts`). | Rename one member, or set a wire name with `[RunicAlias]`. |
| `RUNICBRIDGE005` | The model assembly or one of its dependencies could not be loaded. | Check the bootstrap output and package versions. |
| `RUNICBRIDGE006` | The assembly has no Window or View class (an error only when generation is required, see [Build properties](#build-properties)), one is not public, top-level, concrete and closed, or a View contract is invalid, duplicated or missing. | Make the class public and top-level; give each `[RunicViewContract]` a unique letters-and-digits name. |
| `RUNICBRIDGE007` | A ViewModel does not implement `INotifyPropertyChanged`, is not a public top-level class, or has no state, command or interaction. | Change the ViewModel declaration. |
| `RUNICBRIDGE008` | A state property has no public getter or an empty wire name, ViewModel content or a `NavigationRegion<TContent>` slot has a public setter, a ViewModel collection's item type has no registered View, or a region slot is a `NavigationRegion<object>` or has no ViewModel with a registered View that is a `TContent`. | Add a getter, make content read-only to the web view, register a View for the item or content type, or give the region a specific content interface or base class. |
| `RUNICBRIDGE009` | A command's name does not end with `Command`, its shape is unsupported, or a non-ReactiveUI command has `[RunicCommandResult]`. | Rename the command or use a supported CommunityToolkit, ReactiveUI or `[RunicCommandInput]` command. |
| `RUNICBRIDGE010` | A `[RunicCollection]` member is not a read-only, non-nullable collection of DTO rows, or its key is not a non-nullable `string`, `Guid` or `Int32` row property. | Change the collection or its key. |
| `RUNICBRIDGE011` | A ReactiveUI interaction has no public getter or has a public setter. | Expose the interaction as a get-only property. |
| `RUNICBRIDGE012` | A `[RunicFailure]` is on a member that is not a Bridge command or its `[RelayCommand]` method, is on both the property and the method, or names `object`, an exception or `Nullable<T>`. | Declare one failure type per command: any Bridge value type, typically a DTO, an enum or a `[RunicUnion]`. A failure type the Bridge cannot encode is `RUNICBRIDGE003` at `{Model}.{Command}.failure`. |

## Build properties

The build targets ship in this package and apply to every project that
references a host adapter. A project needs none of these properties unless it
departs from the conventional `Frontend` folder.

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicBridgeFrontendDir` | `$(MSBuildProjectDirectory)/Frontend` | Frontend package directory. |
| `RunicBridgeTypescriptDir` | `<frontend>/src/generated` | Generated TypeScript clients. |
| `RunicBridgeFrontendPackageManager` | `packageManager` in `package.json`, then `pnpm-lock.yaml`, `bun.lock`, else `npm` | Selects the default build and install commands. |
| `RunicBridgeFrontendBuildCommand` | `npm run build`, `pnpm run build`, or `bun run --bun build` | Production frontend build. |
| `RunicBridgeFrontendInstallCommand` | `npm ci`, `pnpm install --frozen-lockfile`, or `bun install --frozen-lockfile`, each with `--ignore-scripts` | Runs when `node_modules` is missing. |
| `RunicBridgeInstallFrontend` | `true` | Set `false` when a workspace install owns the frontend packages. |
| `RunicBridgeBuildFrontend` | `true` | Set `false` when another tool owns the frontend build. TypeScript is still generated. |
| `RunicBridgeCopyFrontend` | `true` | Copies `<frontend>/dist` to `www/` in the build output. Publish always copies it. |
| `RunicBridgeCompositionType` | `<project name>.RunicBridgeComposition` (host adapters) | Generated composition class. |
| `RunicBridgeModelAssembly` | the project itself | Inspect a separately built ViewModel assembly instead of a bootstrap build. |
| `RunicBridgeReactiveUiFlavor` | none | `primitives` or `reactive` for ReactiveUI projects. |

`dotnet build` and `dotnet publish` copy the built frontend as loose files to
`www/` next to the executable; they do not embed it. `dotnet runic dev` sets
`RunicBridgeBuildFrontend=false` and `RunicBridgeCopyFrontend=false` while its
development server serves the frontend. The
[`dotnet runic` README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)
lists the development-server properties it reads.

Bridge generation turns on by default for a project that references
`Runic.Application` or builds in this repository. That default is optional:
when the assembly declares no Runic Window or View, for example an application
that only uses the navigator, the build generates nothing (and removes stale
generated C# and TypeScript), skips the frontend install, build and copy steps,
and succeeds with a normal-importance message. A publish that reuses an earlier
build (`--no-build`) still copies an existing `<frontend>/dist` to `www/`.
Generation is required, and `RUNICBRIDGE006` stays an error for an assembly
without a Window, when the project sets `RunicBridgeBuildEnabled` to `true`, or
when it shows that it expects output: a `package.json` in
`RunicBridgeFrontendDir`, `RunicBridgeFrontendInput` items,
`RunicBridgeCompositionType` or `RunicBridgeModelAssembly`. Projects that
reference a host adapter (`Runic.Application.CsWebUi` or
`Runic.Application.Desktop`) always require generation, because the adapter
sets `RunicBridgeCompositionType`. A navigator-only application should set
`RunicBridgeBuildEnabled` to `false`: that also skips the nested bootstrap
build, which optional mode still pays for.

A single-project application is compiled twice: a bootstrap pass with an empty
generated composition, which the generator inspects, then the real build with
the generated code. `RunicBridgeBootstrap` is `true` only in the bootstrap pass.

## Typed commands and checked writes

CommunityToolkit `IRelayCommand<T>` and `IAsyncRelayCommand<T>` use the same
supported input shapes and generated codecs as ReactiveUI commands: scalars,
nullable values, DTOs, collections, unions, and explicit custom codecs. Generated
`canX(input)` queries use the actual parameter. Async Toolkit commands expose
`startX`, completion, recovery, and cancellation, with no invented result value.
Toolkit cancellation calls the command's `Cancel()` and therefore targets its
current execution; it does not isolate concurrent invocations of the same
command instance.

`[RelayCommand(IncludeCancelCommand = true)]` is supported too. Toolkit's
generated `SaveCancelCommand` exposes parameterless `saveCancel()` and
`canSaveCancel` state in the web client. It executes the actual generated cancel
command, so availability follows Toolkit's `CanBeCanceled` and cancellation
targets the same native `SaveCommand`. The existing `startSave` operation still
owns admission, completion, recovery, and View lifetime cancellation. Recognition
requires Toolkit's generator metadata, the paired async command, and its
cancellation-token method with `IncludeCancelCommand = true`; other plain
`ICommand` properties still require `[RunicCommandInput]`.

Checked-write receipts decode their values just like state: for example, an
`Int64` receipt's `snapshot.value` or conflict's `incoming.value` is a `bigint`.
An identical request ID and payload replays its receipt. Reusing an ID with a
different baseline version, baseline value, or new value produces a conflict.
Each field retains at most 64 receipts within a default 256 KiB encoded-value
and receipt-metadata budget. Evicted or oversized receipts leave bounded
request-ID tombstones; reconcile authoritative state before issuing a new ID.
The write may have succeeded even when its receipt could not be retained.

## Batch synchronous model updates

```csharp
using (BridgeSnapshotBatch.Begin(document))
{
    document.Title = imported.Title;
    document.Content = imported.Content;
    document.Language = imported.Language;
}
```

Nested scopes on the same model capture and publish one final snapshot per
attached bridge when the outer scope ends. Notifications still advance the
revision, and direct command/checked-write replies capture current state
immediately. A batch controls snapshot work; it does not make mutations atomic
or own the model scheduler. Enter the model's execution context as usual and
keep the batch around synchronous changes, after awaiting I/O. The translations
editor uses this during bulk document reconciliation and command result updates.

## Structured validation

Models and nested DTOs may implement `INotifyDataErrorInfo`. Generated snapshots
then expose `validation.hasErrors`, `validation.truncated`, and `validation.errors`, alongside existing
property error string arrays. Each error contains a `path` of wire property
names, list indexes, or dictionary keys and a `message`. Root entity errors use
an empty path. Aliases are preserved, so a postal-code error might have the path
`["profile", "postal-code"]`.

Return a `BridgeValidationMessage` from `GetErrors` to supply a stable `Code`,
`Severity`, or relative CLR `MemberPaths`. Ordinary strings and
`ValidationResult` remain supported. Generated metadata resolves paths without
runtime reflection. Traversal and messages are bounded; `truncated` reports when
validation details are incomplete. Nested `ErrorsChanged` notifications publish new validation
state, and removed objects lose their subscriptions. This projection works
across model frameworks; it does not run validation rules itself.

Use `[RunicIgnore]` on validation-only DTO properties such as a computed
`HasErrors`; their values are represented by the validation projection.

## Shared DTO exclusions

Public readable DTO properties are part of the Bridge contract, including
computed properties. A DTO reader needs a public constructor matching its
included properties, or public setters with a public parameterless constructor.
Use `[RunicIgnore]` to leave a property on .NET without exposing it to the Bridge.
By default, `[JsonIgnore]` affects System.Text.Json serialization only; it does
not exclude a Bridge property.

For shared DTOs that already use System.Text.Json attributes, opt in from the
assembly **declaring the ViewModel**:

```csharp
using Runic.Application.Views;

[assembly: RunicBridgeJsonIgnore]
```

For example, a separate Core assembly can keep its DTO free of Runic dependencies:

```csharp
using System.Text.Json.Serialization;

public sealed record HistoryQuery(string Message, string Author)
{
    [JsonIgnore]
    public bool IsFiltered => Message.Length != 0 || Author.Length != 0;
}
```

The opt-in excludes `[JsonIgnore]` and `[JsonIgnore(Condition = JsonIgnoreCondition.Always)]`
throughout that ViewModel's DTO graphs, including nested DTOs, collections,
command values, interactions and declared failures. Generated codecs, TypeScript,
validation traversal and the Hot Reload fingerprint use the same exclusions.
The Core assembly and the assembly declaring only a Window need no marker.
Each ViewModel uses its own declaring assembly's policy.

`Never`, `WhenWritingNull` and `WhenWritingDefault` remain included; Bridge
fields have a fixed contract and are emitted even when null or default.
`[RunicIgnore]` always excludes a property, including one also marked
`[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`. Root ViewModel properties
still use `[RunicIgnore]`; this opt-in applies to DTO properties only.
Adding the marker can change an existing DTO wire contract and requires rebuilding
the .NET application and generated frontend together.

## Failure detail in development

A command, setter, checked write or operation that throws replies with a
bounded message such as `"Save failed."` and is logged (see
[Logging and telemetry](#logging-and-telemetry)). In
development the error also carries `detail: {type, message, stack}`, which the
TypeScript runtime exposes as `BridgeError.detail` and the Vite plugin shows in
DevTools. Development means `DOTNET_ENVIRONMENT` (or else
`ASPNETCORE_ENVIRONMENT`) is `Development`, which `dotnet runic dev` sets.
Set `BridgeDiagnostics.IncludeFailureDetail` to `true` or `false` to choose
explicitly. Detail can contain file paths and application data, so do not
enable it for a distributed build.

## Declared failures

A command declares its expected failure type with `[RunicFailure(typeof(SaveFailure))]`
on the command property or on its CommunityToolkit `[RelayCommand]` method, and
signals it by throwing `RunicFailureException(new TitleRequired())`. The Bridge
replies `domain-failed` with the encoded failure, in every environment and
never with `detail`; an operation ends with the `domain-failed` status. Use
`[RunicUnion]` for several cases. A value that is not the declared type, one
that cannot be encoded, or one over 4 KiB is reported as an ordinary failure
(event 1008). A synchronous command reports only failures thrown synchronously
from `Execute`, so an `async void` plain `ICommand` cannot declare a failure
that it throws after its first `await`. Operations exist only for asynchronous
CommunityToolkit and ReactiveUI commands.

```csharp
[RunicUnion(typeof(TitleRequired), typeof(TitleTaken))]
public abstract record SaveFailure;
[RunicUnionCase("titleRequired")] public sealed record TitleRequired : SaveFailure;
[RunicUnionCase("titleTaken")] public sealed record TitleTaken(string ExistingTitle) : SaveFailure;

[RelayCommand, RunicFailure(typeof(SaveFailure))]
private async Task SaveAsync(CancellationToken token)
{
    if (string.IsNullOrWhiteSpace(Title)) throw new RunicFailureException(new TitleRequired());
    // ...
}
```

The generated client's `save()` then returns
`Promise<BridgeOutcome<EditorState, SaveFailure>>` instead of
`Promise<EditorState>`, its operation is `BridgeOperation<void, SaveFailure>`, and
the generated mock encodes the failure. A ReactiveUI command also reports the
failure on `ThrownExceptions`, so give every bridged `ReactiveCommand` a
subscriber.

## Navigation (experimental)

Navigation comes from the
[Runic.Navigation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md)
package, which this package depends on. Its types, and the model context
types, are in the `Runic.Navigation` namespace. Its README covers regions,
transitions, ownership, results and disposal. Register it per window scope
with `services.AddRunicNavigation()`, and give the window's session the
navigator's model context; `AddRunicNavigation()` does that for windows opened
from the scope.

### Content slots

Expose a region as a get-only `NavigationRegion<TContent>` property, and the
generator treats it as a content slot. The snapshot presents `Current`, or
clears the slot when the region is empty. The wire value and TypeScript type
are those of a nullable content property (for example `DocumentPageReference | HomePageReference | null`), so
moving a non-null content property to a region adds `| null`, and the frontend
must render the empty state. `TContent` must be an interface or base class of
ViewModels with registered Views. A `NavigationRegion<object>` slot, or one
with a public setter, is `RUNICBRIDGE008`.

The generated Bridge observes the region's `Current` and binds the window
session to the navigator, so retiring owned content forgets its routes. A
navigator serves one window, and the session must use the navigator's model
context. Nothing is forwarded by hand, and `ViewOutlet`s and test drivers
(`View<T>(vm => vm.Main)`) work unchanged. Going back presents the same
`PageReference` id with the retained model, and the outlet mounts a fresh View.
Browser reload, remount and disconnect never touch entries.

**Stale-route window.** A commit retires and forgets departing owned content
right away, while the frontend sees the new `Current` only with the next state
capture. Until then it may still hold the old reference. An invocation on that
reference is rejected like any forgotten route, and the outlet may briefly
show the old View or nothing. The next capture converges.

## Logging and telemetry

`OpenWindow` (CS-WebUI) and `OpenDesktopWindowAsync` (Desktop) pass the scope's
`ILoggerFactory` to the Window's `WindowContentSession` when one is registered,
for example with `services.AddLogging(...)`. A session constructed directly
takes it as `loggerFactory`. Without a factory, each entry is written to
`System.Diagnostics.Trace` (`TraceError`, or `TraceWarning` for warnings) as
its formatted message followed by the exception; a failed View mount goes to
standard error instead.

Every entry carries the exception, in every environment: logs belong to the
operator, whereas `BridgeDiagnostics` governs only what a Bridge reply shows
to web content. Exception messages can contain application data, so treat log
output accordingly. The message properties are listed per event.

| Event ID | Name | Level | Logged when | Properties |
| --- | --- | --- | --- | --- |
| 1000 | `BridgeCommandFailed` | Error | A command throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1001 | `BridgeSetterFailed` | Error | A property setter throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1002 | `BridgeFieldWriteFailed` | Error | A checked field write throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1003 | `BridgeOperationAdmissionFailed` | Error | Admitting an operation throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1004 | `BridgeOperationFailed` | Error | An admitted operation throws. | `Member`, `ErrorType` |
| 1005 | `BridgeOperationCancellationCallbackFailed` | Warning | A cancellation callback throws while a Window closes. | `ErrorType` |
| 1006 | `BridgeCommandDomainFailed` | Debug | A command throws its declared failure. | `Model`, `Member`, `Route`, `FailureType` |
| 1007 | `BridgeOperationDomainFailed` | Debug | An admitted operation throws its declared failure. | `Member`, `FailureType` |
| 1008 | `BridgeDomainFailureNotEncoded` | Error | A `RunicFailureException` from a command or operation is undeclared, cannot be encoded or is over 4 KiB, so it is reported as failed. When the encoder threw, the entry carries both exceptions. | `Model`, `Member`, `Route`, `FailureType`, `Reason` |
| 1010 | `BridgeSnapshotCaptureFailed` | Error | A state snapshot writer throws. | `Model`, `Route`, `ErrorType` |
| 1011 | `BridgeSnapshotDeliveryFailed` | Error | A host rejects a state or delta frame. | `Model`, `Route`, `ErrorType` |
| 1012 | `BridgeCollectionKeysRejected` | Error | A `[RunicCollection]` has a null row or a null, empty or duplicate key, so the route withholds its state. | `Model`, `Field`, `Key`, `Route` |
| 1020 | `ViewMountFailed` | Error | A .NET View fails to mount. | `Route`, `ErrorType` |
| 1021 | `ViewRemountFailed` | Error | A View fails to mount again after a reconnect. | `Route`, `ErrorType` |
| 1033 | `ModelContextReleaseFailed` | Error | A window session's own model context fails to shut down in the background. | `ErrorType` |
| 1040 | `RoutedRegionRouteIncompatible` | Error | A ReactiveUI `ReactiveRoutedRegion<T>` receives a ViewModel that is not a `T`, so it presents no content. | `Region`, `Model` |
| 1041 | `RoutedRegionRouterFailed` | Error | The router observed by a `ReactiveRoutedRegion<T>` fails; the region keeps its last content. | `Region`, `ErrorType` |
| 1042 | `ReactiveCommandFailed` | Error | A ReactiveUI command or object observed with `ObserveBridgeExceptions(logger)` reports an exception other than a declared `RunicFailureException` or a cancellation on `ThrownExceptions`. | `Source` (the command expression or `sourceName`), `ErrorType` |
| 1050 | `CsWebUiWindowRegistrationMissing` | Error | A CS-WebUI Window's generated Bridge is not registered. | `Code`, `DiagnosticMessage`, `Remediation` |
| 2000 | `DesktopSnapshotDeliveryFailed` | Error | Runic Desktop cannot run a state delivery script. | `Route`, `ErrorType` |
| 2001 | `DesktopWindowRegistrationMissing` | Error | A Desktop Window's generated Bridge is not registered. | `Code`, `DiagnosticMessage`, `Remediation` |
| 2002 | `DesktopWindowCloseAfterOpen` | Warning | `RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` closed a Desktop window right after it opened. | `Variable` |
| 3000 | `WindowCloseCancellationCallbackFailed` | Error | A Runic Desktop close-cancellation callback throws. | `ErrorType` |
| 3001 | `WindowCloseConfirmationFailed` | Error | A Runic Desktop close confirmation throws; the Window stays open. | `ErrorType` |
| 3002 | `DesktopConfigurationInvalid` | Error | `DesktopHost.Validate` finds a check that fails the window request. | `Code`, `Option`, `DiagnosticMessage`, `Remediation` |
| 3003 | `DesktopConfigurationLimited` | Warning | `DesktopHost.Validate` finds an option the presentation ignores or narrows, or a browser fallback opens without a permission grant. | `Code`, `Option`, `DiagnosticMessage`, `Remediation` |
| 3004 | `BrowserLaunchStalled` | Warning | A browser was still running but had requested nothing after `ConnectionTimeout`, so Runic Desktop relaunched it (`DesktopHostOptions.BrowserLaunchAttempts`). | `Attempt` (the attempt that stalled), `Attempts`, `TimeoutSeconds` |
| 3005 | `BridgeHandshakeExpired` | Warning | A Bridge WebSocket sent no token check, so Runic Desktop closed it and the page reconnects: after 10 seconds, or after 2 seconds when a newer WebSocket needs the only connection. | `ConnectionId`, `SilentSeconds`, `Reason` |

`Model` is the ViewModel type name, `Member` the command or property, and
`Route` the Bridge route (a content presentation's route is per instance, such
as `content12`). A declared failure is an expected outcome, so it is logged at
Debug, still with its exception; `FailureType` is the failure value's type.

Events 1000-1021 and 1050 use the category `Runic.Application.Views`
(`RunicViewsTelemetry.LogCategory`). Event 1033 here is a window session's
own model context failing to shut down, and uses the session's logger.
Navigation (1060-1074) and the model context (1030-1033) log under
`Runic.Navigation` and `Runic.Navigation.RunicModelContext`; see the
[Runic.Navigation logging](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#logging)
section. Events 2000-2002 use `Runic.Application.Desktop`. Events 3000-3005
use `Runic.Desktop` and need `DesktopHostOptions.LoggerFactory`.

Events 1040-1042 come from `Runic.Application.ReactiveUI` (and its `.Reactive`
flavor) and also use `Runic.Application.Views`. They need the
`ReactiveRoutedRegion<T>(router, loggerFactory)` constructor, which a
ViewModel can call with an `ILoggerFactory` injected from DI, as
`examples/notes-reactive-views` does. Without a factory, the region writes the
same messages to `Trace` as described above. `Region` is the region's ViewModel
type name `T`. Event 1042 uses the logger passed to `ObserveBridgeExceptions`;
`Source` is the observed command's expression, such as `SaveCommand`, or `sourceName`.

Bridge calls are traced by the `ActivitySource` and measured by the `Meter`
named `Runic.Application.Views` (`RunicViewsTelemetry.ActivitySourceName` and
`MeterName`). With OpenTelemetry, call `AddSource(...)` and `AddMeter(...)`
with these names. Tag values are generated contract names, outcomes and
exception types, never arguments or state.

| Span | Covers |
| --- | --- |
| `runic.bridge.command` | A synchronous or awaited command. |
| `runic.bridge.set` | A property setter. |
| `runic.bridge.write` | A checked field write. |
| `runic.bridge.operation.start` | Admitting an operation. |
| `runic.bridge.operation` | Running an admitted operation. |

Spans are named `<kind> <Model>.<Member>` and have the tags
`runic.bridge.kind`, `runic.bridge.model`, `runic.bridge.member`,
`runic.bridge.route` and `runic.bridge.outcome`. The outcome is `ok`,
`rejected`, `cancelled`, `failed`, `domain_failed` or `disconnected`. An
admission can also be `unavailable`, `capacity`, `duplicate` or `expired`, and
an operation whose result could not be delivered is `delivery_failed`. A failed
span has the status `Error` and `error.type`. A `domain_failed` call is an
expected outcome: its span status stays Unset, it has no `error.type`, and
`runic.bridge.failures` does not count it.

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `runic.bridge.calls` | Counter | `{call}` | kind, model, member, outcome, `error.type` |
| `runic.bridge.call.duration` | Histogram | `s` | kind, model, member, outcome, `error.type` |
| `runic.bridge.failures` | Counter | `{failure}` | kind (a call kind, `snapshot.capture`, `snapshot.delivery`, `collection.keys` or `mount`), model, member, `error.type` |
| `runic.bridge.snapshot.frames` | Counter | `{frame}` | model, `runic.bridge.frame` (`state`, `delta` or `failure`) |
| `runic.bridge.snapshot.size` | Histogram | `By` | model, frame |
| `runic.bridge.snapshot.delivery.duration` | Histogram | `s` | model, frame |
| `runic.bridge.snapshot.recoveries` | Counter | `{snapshot}` | model |
| `runic.bridge.snapshot.queue.depth` | ObservableUpDownCounter | `{frame}` | none |

Route names are span tags but not metric tags, because content routes identify
one instance. The duration histograms advise buckets from 0.5 ms to 10 s, and
the size histogram from 256 B to 4 MiB. The queue depth reports the frames
waiting for slow hosts across all Bridges when it is observed.

## Incremental generation

The generator caches successful multi-view generation in its C# `obj` output
directory. It hashes the model/dependency assemblies, generator assemblies,
output-affecting options, and generated output contents. Missing or edited
outputs invalidate the cache; unchanged generated files keep their timestamps.
Set `RUNIC_BRIDGE_CODEGEN_FORCE=1` or `RUNIC_BRIDGE_CODEGEN_CACHE=0` to bypass it.

The configured frontend build command (`RunicBridgeFrontendBuildCommand`) runs
only when one of its inputs is newer than
`obj/<Configuration>/<TargetFramework>/runic-bridge-frontend.stamp`: files under
`RunicBridgeFrontendDir` (excluding `node_modules`, `dist`, `build`, `bin`, `obj`
and dot-directories), the generated TypeScript, and the project file. Generated
TypeScript is rewritten only when its content changes, so C#-only edits skip the
frontend build. Add `RunicBridgeFrontendInput` items, from a target that runs
before `RunicViewsGenerateBridge`, for inputs outside the frontend directory such
as workspace packages or generated assets; the translations editor is an example.
Delete the stamp or rebuild to force a frontend build. Set
`RunicBridgeBuildFrontend=false` when another tool, such as a running dev server,
owns the frontend build; `dotnet runic dev` does this while its development
server runs.

Generated C# and TypeScript files start with `// <auto-generated />`. Only such
files are removed from the output directories when a ViewModel disappears, so
`RunicBridgeTypescriptDir` may point into a directory with hand-written modules.

## Accepted application work

Cancellation requests that a command stop; its invocation can finish before
the underlying task completes. In particular, ReactiveUI bridge execution
can settle a cancelled invocation and release its subscription while an
uncooperative task continues recovery. `WindowContentSession.BeginCloseAsync`
and its result's `Completion` drain bridge invocations. Their completion does
not establish that independently accepted application work, durable changes,
or recovery has finished.

Own such work with an `AcceptedWorkScope` in the scoped model or service that
owns its resources. Pass a factory so ownership is registered before the task
starts, and include recovery, publication, and cleanup in that task:

```csharp
private readonly AcceptedWorkScope _acceptedWork = new();

public Task SaveAsync(CancellationToken cancellationToken) =>
    _acceptedWork.RunAsync(() => SaveAndRecoverAsync(cancellationToken));

public async ValueTask DisposeAsync()
{
    await _acceptedWork.DisposeAsync();
    // Release the model's resources after its accepted tasks finish.
}
```

`RunAsync` invokes its factory synchronously on the calling thread and returns
the original task, preserving its result, failure, or cancellation. Concurrent
drain includes accepted factories that have not yet returned a task. A null
task or synchronous factory exception releases that admission and throws to
the caller. Calls after drain starts throw `InvalidOperationException` without
invoking the factory.

`DrainAsync(cancellationToken)` closes admission and waits for actual task
completion, including faulted and cancelled tasks. Its cancellation token only
stops that observer's wait; it neither cancels nor abandons owned work. Drain
observes task failures without throwing them again, so callers still handle
outcomes through their original tasks. `DisposeAsync` is idempotent and waits
without cancellation. Keep resources alive through this drain even if a
bridge invocation or an earlier drain observer has already been cancelled.

The scope does not serialize operations, dispatch model changes, request
cancellation, or implement recovery. The application still owns those policies
and must return a task that covers all work requiring its resources.
