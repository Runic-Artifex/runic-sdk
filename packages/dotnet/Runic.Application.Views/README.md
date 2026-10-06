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
first-window example is exercised through Native AOT and Chromium in CI.
`RUNICBRIDGE002` rejects a selected CommunityToolkit `ObservableValidator`
ViewModel in Native AOT until its validation behavior is verified. The
generator reports every ViewModel's first problem in one build:
`RUNICBRIDGE003` is an unsupported value type, `RUNICBRIDGE004` a generated name
collision, `RUNICBRIDGE005` an assembly that could not be loaded, and
`RUNICBRIDGE001` any other unsupported shape.

The [First Window](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window),
[CommunityToolkit Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first), and
[Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-reactive-views)
examples exercise the packaged graph and generated client.

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
| `RunicBridgeRegisterGlobally` | `false` (host adapters) | `true` registers Bridges in a process-wide registry instead of the DI composition. |
| `RunicBridgeModelAssembly` | the project itself | Inspect a separately built ViewModel assembly instead of a bootstrap build. |
| `RunicBridgeReactiveUiFlavor` | none | `primitives` or `reactive` for ReactiveUI projects. |

`dotnet build` and `dotnet publish` copy the built frontend as loose files to
`www/` next to the executable; they do not embed it. `dotnet runic dev` sets
`RunicBridgeBuildFrontend=false` and `RunicBridgeCopyFrontend=false` while its
development server serves the frontend. The
[`dotnet runic` README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)
lists the development-server properties it reads.

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
