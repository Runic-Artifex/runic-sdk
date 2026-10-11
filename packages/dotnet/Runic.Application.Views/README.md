# Runic.Application.Views

Typed .NET Windows and Views with generated TypeScript clients. C# ViewModels
own application state, commands, and operation lifetimes; a React, Vue,
Svelte, Angular, or plain TypeScript frontend renders them through a generated
client. Every package's ID is its namespace: this package's types are in
`Runic.Application.Views`, and the host adapters' in
`Runic.Application.Views.Desktop`, `Runic.Application.Views.CsWebUi` and
`Runic.Application.Views.Wpf`.

## Install

Reference a host adapter; it brings this package and its build targets:

```sh
dotnet add package Runic.Application.Views.CsWebUi --prerelease  # CS-WebUI browser or WebView window
dotnet add package Runic.Application.Views.Desktop --prerelease  # or: Runic Desktop native host
dotnet add package CommunityToolkit.Mvvm                         # or ReactiveUI with Runic.Application.Views.ReactiveUI
dotnet add package Microsoft.Extensions.DependencyInjection      # ServiceCollection; the adapters need only the abstractions
```

To start a new application, run the
[guided creator](https://docs.runic-artifex.eu/getting-started/)
(`dnx Runic.Create@<VERSION>`) or the `dotnet new runic-app` template, then
`dotnet tool restore` and `dotnet runic dev`. The template defaults to a native
Runic Desktop window and ReactiveUI ViewModels; the minimal Window below keeps
to the shortest program, CS-WebUI (which opens a browser window) with
CommunityToolkit.Mvvm, as in the First Window example.

## Glossary

These words name one concept each across the Runic packages and frontends.

- **Window**: a top-level application window and its root ViewModel, declared
  as a partial `RunicWindow<TViewModel>`. The same declaration opens on every
  host with `host.OpenWindowAsync<TWindow>()`. A Window is also a View.
  Runic Desktop's `DesktopWindow` is the native window underneath one.
- **View**: a logical .NET presentation object for one ViewModel, a partial
  `RunicView<TViewModel>` with a typed `DataContext`. It owns no DOM: the
  frontend renders a component for it, which `ViewOutlet` selects in React,
  Vue, Svelte and Angular.
- **Bridge**: the code generated per ViewModel, C# and a typed TypeScript
  client, that carries snapshots, property writes, commands and operations
  between the ViewModel and its View over the wire protocol. `Bridge*` types
  and the `RUNICBRIDGE` diagnostics belong to this generated layer.
- **Host**: the adapter that opens Windows on a platform: `RunicDesktopHost`
  (`Runic.Application.Views.Desktop`) and `RunicCsWebUiHost`
  (`Runic.Application.Views.CsWebUi`), both an `IRunicWindowHost`. In WPF,
  `RunicViewHost` hosts one View inside the visual tree. An open Window's
  `Host` property returns its per-Window adapter, an `IBridgeWindow`.
- **Presentation**: what shows a Window's or View's web content on screen: an
  embedded WebView or an installed browser window, such as
  `DesktopBridgeWindow.Presentation` (a `DesktopWindow`) or the WebView of a
  `RunicViewHost`. A presentation closes and reopens without changing the
  ViewModel.
- **Surface**: Runic Desktop's `DesktopSurface`, which serves a Window's web
  content (`DesktopSurfaceOptions.Content`), capabilities and browser sessions,
  and opens presentations of it. `DesktopBridgeWindow.Surface` is the Window's
  surface.
- **Owner**: the native window that platform services, such as file dialogs,
  the clipboard and file launchers, attach to: `DesktopNativeOwner` for a Runic
  Desktop window, or an `INativePickerOwner` for another host. Native dialogs
  require one (`OwnerPolicy.RequireOwner`).

## A minimal Window

In a project named `MyApp`:

```csharp
// Counter.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace MyApp;

public sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty] private int count;

    [RelayCommand]
    private void Increment() => Count++;
}

// Selecting the Window makes the build generate Frontend/src/generated/counter.ts.
public sealed partial class CounterWindow : RunicWindow<CounterViewModel>;
```

```csharp
// Program.cs
using Microsoft.Extensions.DependencyInjection;
using MyApp;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
services.AddScoped<CounterViewModel>();
services.AddRunicViews();
using var provider = services.BuildServiceProvider();

// RunicDesktopHost.Run(provider, ...) from Runic.Application.Views.Desktop runs the same application natively.
return RunicCsWebUiHost.Run(provider, async host =>
{
    await using var window = await host.OpenWindowAsync<CounterWindow>();
    await window.WaitForCloseAsync();
    return 0;
});
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
or `RunicApplicationFrontendCompositionType`) provides two registrations:

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

The window binds its root ViewModel to the window's model context, and a Bridge
attached directly to a `WindowContentSession` binds its ViewModel for the
Bridge's lifetime, so no ViewModel needs a manual
`RunicModelContextRegistry.Bind`. A root, content or Bridge ViewModel already
bound to a different context is rejected with an `InvalidOperationException`
that names its type.

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

### Opening a Window

Every host opens Windows the same way. The application's entry point calls the
host's `Run` method, `RunicDesktopHost.Run` (`Runic.Application.Views.Desktop`) or
`RunicCsWebUiHost.Run` (`Runic.Application.Views.CsWebUi`), which runs the
asynchronous application on the event loop the host needs and passes it an
`IRunicWindowHost`:

```csharp
static async Task<int> RunAsync(IRunicWindowHost host)
{
    await using var window = await host.OpenWindowAsync<MainWindow>(new RunicWindowOptions { Width = 1000, Height = 700 });
    await window.WaitForCloseAsync();
    return 0;
}
```

Switching host changes only the `Run` call, its package and its host options;
the Window declaration and this body stay the same, with ReactiveUI or
CommunityToolkit.Mvvm ViewModels.

- `OpenWindowAsync<TWindow>(options, cancellationToken)` creates the Window,
  checks that the generated Bridge is registered, resolves the root ViewModel in
  a new DI scope, assigns it to `DataContext`, and opens the native window.
  `RunicWindowOptions` sets the content root (by default `www` next to the
  application), the start page (`index.html`) and the initial size on every host.
  The Window owns the scope until it is disposed. A Window instance opens once.
- `ValidateWindow<TWindow>(options)` runs the same checks at startup without
  opening anything and returns a `RunicWindowValidationResult`: an error
  `bridge-not-registered` when `AddRunicViews()` is missing, a warning
  `viewmodel-not-registered` when the container does not report the ViewModel,
  and the host's own checks, such as Runic Desktop's window options.
  `ThrowIfInvalid()` throws a `RunicWindowConfigurationException`, the same
  exception `OpenWindowAsync` throws on every host.
- `WaitForCloseAsync(cancellationToken)` completes when the window closes or
  starts closing; the token stops only the wait. `CloseAsync(timeout,
  cancellationToken)` stops new operations, waits up to `timeout` for accepted
  ones, and returns a `BridgeWindowCloseResult`; its token also stops only the
  caller's wait. Disposal is asynchronous only.
- `Host` returns the host adapter, an `IBridgeWindow`:
  `DesktopBridgeWindow<TViewModel>` (with `Surface`, `Presentation` and
  `NativeOwner`) or `CsWebUiBridgeWindow<TViewModel>` (with `NativeWindow`).

### Moved and renamed since 0.7.0-preview.6

The Window types and entry points of the two hosts were merged into this API,
and package, build property and outlet names now match what they are. Earlier
API shapes were removed rather than deprecated. Old build property names still
work in 0.7.0-preview.7 with warning
[RUNICBRIDGE017](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge017),
and Angular's `RunicViewOutlet` remains a deprecated alias; both are removed in
the next preview. The old package IDs get no further releases, so replace them
in `PackageReference` and `Directory.Packages.props`; namespaces are unchanged.

| Before | Now |
| --- | --- |
| `RunicWindow<T>(T dataContext)` constructor | `RunicWindow<T>()`; `OpenWindowAsync` assigns `DataContext` |
| `ReactiveRunicWindow<T>` (`Runic.Application.ReactiveUI`, `.Reactive`) | `RunicWindow<T>`; Views keep `ReactiveRunicView<T>` |
| `CsWebUiWindow<T>` | `RunicWindow<T>` |
| `provider.OpenWindow<TWindow, TViewModel>(factory)` (CS-WebUI) | `await host.OpenWindowAsync<TWindow>(options)` on `RunicCsWebUiHost` |
| `provider.OpenDesktopWindowAsync<TWindow, TViewModel>(desktop, surfaceOptions, factory, windowOptions)` | `await host.OpenWindowAsync<TWindow>(options)` on `RunicDesktopHost` (`SurfaceOptions`, `WindowOptions`) |
| `provider.ValidateWindow<TViewModel>()`, which threw | `host.ValidateWindow<TWindow>(options)`, which returns `RunicWindowValidationResult` |
| `provider.ValidateDesktopWindow<TViewModel>(desktop, windowOptions)` | `host.ValidateWindow<TWindow>(options)` |
| `CsWebUiConfigurationException` (`Code`, `DiagnosticMessage`, `Remediation`) | `RunicWindowConfigurationException` (`Diagnostics`) |
| `DesktopConfigurationException` from `OpenDesktopWindowAsync` | `RunicWindowConfigurationException`, with the Desktop exception as `InnerException` |
| `DesktopEventLoop.Run(options, async desktop => ...)` around `OpenDesktopWindowAsync` | `RunicDesktopHost.Run(provider, options, async host => ...)` |
| `WebUiApplication.Wait()` and `WebUiApplication.Clean()` in `Program.cs` | `RunicCsWebUiHost.Run(provider, async host => ...)` |
| `window.Presentation.WaitForClose()`, `WebUiApplication.Wait()` | `await window.WaitForCloseAsync(cancellationToken)` |
| `IBridgeWindow.CloseAsync(TimeSpan)` | `IBridgeWindow.CloseAsync(TimeSpan, CancellationToken)` and `WaitForCloseAsync(CancellationToken)` |
| `SetRootFolder`, `SetSize`, `SetPort`, `Show`, `ShowWebView`, `StartServer` on `CsWebUiBridgeWindow<T>` and `CsWebUiWindow<T>` | `RunicWindowOptions`, and `UseWebView`, `ConfigureWindow` and `ShowWindow` on `RunicCsWebUiHost`; `NativeWindow` remains |
| Log events 1050 `CsWebUiWindowRegistrationMissing` and 2001 `DesktopWindowRegistrationMissing` | Event 1050 `WindowRegistrationMissing` on every host |
| Package `Runic.Application` | `Runic.Application.Views`, its namespace |
| Package `Runic.Application.CsWebUi` | `Runic.Application.Views.CsWebUi` |
| Package `Runic.Application.Desktop` (assembly `Runic.Application.Desktop.dll`) | `Runic.Application.Views.Desktop` (`Runic.Application.Views.Desktop.dll`) |
| Package `Runic.Application.Wpf` (assembly `Runic.Application.Wpf.dll`) | `Runic.Application.Views.Wpf` (`Runic.Application.Views.Wpf.dll`) |
| Package `Runic.Application.ReactiveUI` | `Runic.Application.Views.ReactiveUI` |
| Package `Runic.Application.ReactiveUI.Reactive` | `Runic.Application.Views.ReactiveUI.Reactive` |
| Log category `Runic.Application.Desktop` (Desktop transport) | `Runic.Application.Views.Desktop` |
| `RunicBridgeFrontendDir` | `RunicApplicationFrontendDirectory` |
| `RunicBridgeTypescriptDir` | `RunicApplicationFrontendGeneratedDirectory` |
| `RunicBridgeFrontendPackageManager` | `RunicApplicationFrontendPackageManager` |
| `RunicBridgeFrontendBuildCommand` | `RunicApplicationFrontendBuildCommand` |
| `RunicBridgeFrontendInstallCommand` | `RunicApplicationFrontendInstallCommand` |
| `RunicBridgeInstallFrontend` | `RunicApplicationFrontendInstallEnabled` |
| `RunicBridgeBuildFrontend` | `RunicApplicationFrontendBuildEnabled` |
| `RunicBridgeCopyFrontend` | `RunicApplicationFrontendCopyEnabled` |
| `RunicBridgeBuildEnabled` | `RunicApplicationFrontendGenerationEnabled` |
| `RunicBridgeCompositionType` | `RunicApplicationFrontendCompositionType` |
| `RunicBridgeModelAssembly` | `RunicApplicationFrontendModelAssembly` |
| `RunicBridgeReactiveUiFlavor` | `RunicApplicationFrontendReactiveUiFlavor` |
| `RunicBridgeDocumentation` | `RunicApplicationFrontendDocumentationEnabled` |
| `RunicBridgeBootstrap` (set in the bootstrap pass) | `RunicApplicationFrontendBootstrap`; the old name is still set in this preview |
| `RunicViewsWindowProject` | `RunicApplicationFrontendWindowProject` |
| `RunicBridgeFrontendInput` items | `RunicApplicationFrontendInput` items |
| `RunicViewOutlet` (`@runic-artifex/angular`) | `ViewOutlet`, as in React, Vue and Svelte; the element stays `<runic-view-outlet>` |

Runic Desktop's window lifecycle is asynchronous too: see the
[Runic.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md)
for `DesktopWindow.WaitForCloseAsync` and signed window positions.

The [First Window](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/first-window),
[CommunityToolkit Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first), and
[Reactive Notes](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-reactive-views)
examples exercise the packaged graph and generated client.

## Threading: state after await

A Window's ViewModels belong to its model context. The bridge reads state,
applies property writes and starts commands in that context's turns, one at a
time. The rule for both MVVM flavours is:

> **Set ViewModel state directly in a command, also after `await`. Leave the
> model context only for work that doesn't touch ViewModel state, and commit
> its result with `IRunicModelContext.InvokeAsync`.**

```csharp
[RelayCommand] // or ReactiveCommand.CreateFromTask(SaveAsync, outputScheduler: scheduler)
private async Task SaveAsync(CancellationToken token)
{
    Status = "Saving";
    await _store.SaveAsync(Title, token);
    Status = "Saved";               // runs in a model-context turn
}
```

- A command body that the bridge starts, such as a CommunityToolkit
  `[RelayCommand]` or `AsyncRelayCommand`, or a ReactiveUI
  `ReactiveCommand.CreateFromTask`, runs in a turn. Every `await` in it, nested
  ones included, resumes in a later turn of the same context, as code on a UI
  thread resumes on that thread. Writes after `await` are applied in the order
  in which the continuations become ready, and no other turn runs between two
  awaits of one body.
- Code after `ConfigureAwait(false)`, in `Task.Run`, in a timer or in another
  background callback runs outside the context. Do CPU-bound or blocking work
  there, then commit: `await modelContext.InvokeAsync(() => Items = loaded)`.
  Don't use `ConfigureAwait(false)` in ViewModel code that writes state after
  it. Code you start outside a turn, such as a test that calls
  `ExecuteAsync` directly, also commits with `InvokeAsync`.
- Don't block a turn on asynchronous work (`.Result`, `.Wait()`,
  `GetAwaiter().GetResult()`): its continuation needs the same context, so it
  deadlocks, as it would on a UI thread.
- When the context closes, for example because the window closed, pending
  continuations run on the thread pool outside any turn, so the command still
  finishes; nothing presents its later writes.
- With WPF, `DispatcherModelContext` is the UI dispatcher, so the same rule
  holds there. Navigation hooks are not commands; see
  [Runic.Navigation threading](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#threading).

## Read-only and settable state

The web frontend is the View, so the usual MVVM rule applies: the ViewModel's
C# accessibility is the contract, and the View decides what it binds two-way.
A property with a public getter is state every View can read. A property with a
public setter is also what any View, whether WPF, Avalonia or the web frontend,
may set, so its generated client gets a `set<Property>` member. A `private set`
keeps the property read-only to every View. Runic adds no attribute of its own.

Give status that the ViewModel computes, such as `IsDirty`, `Error`, a status
message or a collection, a private setter, and keep form fields publicly
settable. With CommunityToolkit.Mvvm 8.4, declare partial properties:

```csharp
public sealed partial class EditorViewModel : ObservableObject
{
    // Form field: the client has setTitle.
    [ObservableProperty]
    public partial string Title { get; set; }

    // Status: read-only to every View, so there is no setIsDirty or setError.
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }
}
```

A field such as `[ObservableProperty] private bool isDirty;` generates a
property with a public setter, so the web frontend could set it too. With
ReactiveUI, give the property a private setter, either written out with
`RaiseAndSetIfChanged` or generated with `[Reactive]`:

```csharp
public sealed partial class EditorViewModel : ReactiveObject
{
    private bool _isDirty;

    // Form field: the client has setTitle.
    [Reactive]
    public partial string Title { get; set; } = "";

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    // The same with the ReactiveUI source generator.
    [Reactive]
    public partial string? Error { get; private set; }
}
```

The comment on each generated `set<Property>` member names the C# setter it
calls. To ask the ViewModel to change its status, call a command. The
[React](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/react#read-only-state-and-two-way-binding),
[Vue](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/vue#read-only-state-and-v-model),
[Svelte](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/svelte#read-only-state-and-bindvalue)
and [Angular](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/angular#read-only-state-and-two-way-binding)
guides show reading state one way and binding a settable property two way.
The `dotnet new runic-app` template's Counter page has one of each.

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

- Numbers follow JavaScript's precision: `int`, `double` and smaller numeric
  types are `number`, but `long`, `ulong` and `BigInteger` are `bigint`,
  because a `number` is exact only up to 2^53, and `decimal` is a `string`.
  The wire carries 64-bit and big integers as strings; the generated client
  converts them, and the member's TSDoc says so. Use `BigInt(value)` and
  `Number(value)` to convert at the edges, or model a small count as `int`.
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
  The frameworks' `ViewRegistry<State["main"]>` accepts such a nullable type.
- `<summary>` comments on ViewModels, state properties, commands, interactions,
  DTO types and members, and enum cases become TSDoc. A summary on a partial
  property such as `[ObservableProperty] public partial bool IsDirty { get; private set; }`
  or `[Reactive] public partial string Name { get; set; }` is read from its
  declaring part in source, found through the assembly's PDB (source paths
  that a CI or deterministic build maps, such as `/_/`, resolve through the
  project's source roots), because the XML
  documentation file keeps the generated part's `<inheritdoc/>`. The bootstrap
  pass writes the XML documentation file for this; a project that already writes one keeps
  its own. Set `RunicApplicationFrontendDocumentationEnabled=false` to skip it. A separate model
  assembly (`RunicApplicationFrontendModelAssembly`) needs its XML documentation file beside
  it. A command generated from a method, such as a CommunityToolkit
  `[RelayCommand]`, uses the method's comment. The C#-only comments an MVVM
  library's source generator writes are not copied; a generated cancel command
  is documented as cancelling its command.
- A command argument keeps the parameter name of its CommunityToolkit
  `[RelayCommand]` method. Other commands name it after the argument's C# type,
  or `input` for scalars.

The generated C# bridge calls public runtime types such as `ViewModelBridge<T>`,
`PropertyDescriptor<T>`, `CommandDescriptor<T>`, `BridgeWire`, `BridgeJson` and
`ReactiveCommandExecution`. They are marked `[EditorBrowsable(Never)]`, so they
stay out of completion lists; they are not application API.

## Generator diagnostics

The generator reports every ViewModel's first problem in one build. An error
names the type and member, and points at its source line when the inspected
assembly has a portable PDB (the default). A member generated by a source
generator, such as a CommunityToolkit `[ObservableProperty]`, points at its
`[RelayCommand]` method or its declaring class. Each error and warning ends with
a link to its entry in the [diagnostics catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#bridge-code-generator)
for the release you use.

`RUNICBRIDGE013` is a warning and repeats on builds that reuse the cached
generation; every other code fails the build.

The build targets run the generator and the frontend install and build
commands without wrapping their failures in MSB3073. Errors and warnings that a
command prints in the canonical `file(line,column): error CODE: text` form, such
as the generator's and `tsc`'s, are logged once with their code and location. A
failed command that reports no such error gets one summary error instead:
`RUNICBRIDGE001` for the generator, `RUNICBRIDGE016` for the frontend
commands.

| ID | Problem | Fix |
| --- | --- | --- |
| `RUNICBRIDGE001` | Invalid generator invocation or build configuration, such as a malformed `RunicApplicationFrontendCompositionType`; also an internal generator error. | Correct the build property. Report an internal error with the ViewModel that triggers it. |
| `RUNICBRIDGE002` | A CommunityToolkit `ObservableValidator` ViewModel in a Native AOT publish. | Publish framework-dependent until its validation is verified under AOT. |
| `RUNICBRIDGE003` | A state, command, or interaction value type is not a supported bridge value. The message names the member path, for example `EditorViewModel.Current.value`. | Use a supported scalar, collection, public DTO, `[RunicUnion]` or `[RunicBridgeCodec]` type. Exclude computed DTO properties with `[RunicIgnore]`, or opt into unconditional `[JsonIgnore]` as described under [Shared DTO exclusions](#shared-dto-exclusions). A `NavigationResult<T>` holds .NET content: return its `Outcome`, a `NavigationOutcome`, as `CreateBackCommand()` does. |
| `RUNICBRIDGE004` | Two generated names collide: ViewModel names, presentation kinds, state wire names, the reserved `revision` and `validation` fields, interactions, routes, client members, or generated files (including a hand-written `types.ts`). Also a command whose client member is reserved in a TypeScript object: `new` (a construct signature), `then` (a thenable client), `constructor`, `__proto__`, `toJSON`, `toString` or `valueOf`. | Rename one member, or set a state wire name with `[RunicAlias]`. Rename a reserved command; the message suggests a name, such as `CreateCommand` for `NewCommand`. |
| `RUNICBRIDGE005` | The model assembly or one of its dependencies could not be loaded. | Check the bootstrap output and package versions. |
| `RUNICBRIDGE006` | The assembly has no Window or View class (an error only when generation is required, see [Build properties](#build-properties)), one is not public, top-level, concrete and closed, or a View contract is invalid, duplicated or missing. | Make the class public and top-level; give each `[RunicViewContract]` a unique letters-and-digits name. |
| `RUNICBRIDGE007` | A ViewModel does not implement `INotifyPropertyChanged`, is not a public top-level class, or has no state, command or interaction. | Change the ViewModel declaration. |
| `RUNICBRIDGE008` | A state property has no public getter or an empty wire name, ViewModel content or a `NavigationRegion<TContent>` slot has a public setter, a ViewModel collection's item type has no registered View, or a region slot is a `NavigationRegion<object>` or has no ViewModel with a registered View that is a `TContent`. | Add a getter, make content read-only to the web view, register a View for the item or content type, or give the region a specific content interface or base class. |
| `RUNICBRIDGE009` | A command's name does not end with `Command`, its shape is unsupported, or a non-ReactiveUI command has `[RunicCommandResult]`. | Rename the command or use a supported CommunityToolkit, ReactiveUI or `[RunicCommandInput]` command. |
| `RUNICBRIDGE010` | A `[RunicCollection]` member is not a read-only, non-nullable collection of DTO rows, or its key is not a non-nullable `string`, `Guid` or `Int32` row property. | Change the collection or its key. |
| `RUNICBRIDGE011` | A ReactiveUI interaction has no public getter or has a public setter. | Expose the interaction as a get-only property. |
| `RUNICBRIDGE012` | A `[RunicFailure]` is on a member that is not a Bridge command or its `[RelayCommand]` method, is on both the property and the method, or names `object`, an exception or `Nullable<T>`. | Declare one failure type per command: any Bridge value type, typically a DTO, an enum or a `[RunicUnion]`. A failure type the Bridge cannot encode is `RUNICBRIDGE003` at `{Model}.{Command}.failure`. |
| `RUNICBRIDGE013` (warning) | A public, concrete ViewModel in the application assembly can be put in a ViewModel content property, ViewModel collection or `NavigationRegion<TContent>` slot, but has no View, so pushing or presenting it throws `NotSupportedException` naming the slot and the type. Broad slot types are not checked: framework types (System, ReactiveUI, CommunityToolkit), a type the slot's owner or a Window's ViewModel also derives from or implements, and a type that more than eight ViewModels without a View could fill. | Add a View, such as `public sealed partial class SettingsView : RunicView<SettingsViewModel>;` (with the slot's `[RunicViewContract]`, if any). If the type is never presented there, suppress the warning with `<NoWarn>$(NoWarn);RUNICBRIDGE013</NoWarn>`. |
| `RUNICBRIDGE014` | A project declares Runic Views, but `RunicApplicationFrontendDirectory` does not exist or, with the default build command, has no `package.json`. Reported by the build targets. | Create the frontend there, point `RunicApplicationFrontendDirectory` at your frontend package, or set `RunicApplicationFrontendBuildEnabled=false` when another tool builds it. |
| `RUNICBRIDGE015` (warning) | A project declares a Runic Window or View, but generation is off because it references no host adapter and not `Runic.Application.Views` directly, so no bridge, `AddRunicViews()` or client is generated. Reported by the build targets. | Set `RunicApplicationFrontendGenerationEnabled=true` to generate in that project, or `false` when another project generates its Views. |
| `RUNICBRIDGE016` | The frontend install or build command failed without reporting an error in the canonical `file(line,column): error CODE: text` form (a Vite or package-manager error, for example), or the install created no `node_modules` for declared dependencies. The command's own output is printed above the error. Reported by the build targets. | Fix the problem the command reports, or run `dotnet runic doctor` to check the package manager and lock file. |
| `RUNICBRIDGE017` (warning) | The project, an import or the command line sets a build property or item by its 0.7.0-preview.6 name (`RunicBridge*`, `RunicViewsWindowProject`). The old name still applies in this preview. Reported by the build targets. | Rename it to the `RunicApplicationFrontend*` name in the message; see the [rename table](#moved-and-renamed-since-070-preview6). |

## Build properties

The build targets ship in this package and apply to every project that
references a host adapter. A project needs none of these properties unless it
departs from the conventional `Frontend` folder. Every property starts with
`RunicApplicationFrontend`, as do the development-server properties of
`dotnet runic dev`. The `RunicBridge*` and `RunicViewsWindowProject` names of
0.7.0-preview.6 and earlier still apply in this preview with warning
`RUNICBRIDGE017`; the [rename table](#moved-and-renamed-since-070-preview6)
maps them. Underscore-prefixed properties, `RunicApplicationHostAdapter` (which
the host adapters set) and the `RunicViews*` target names are not settings.

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicApplicationFrontendDirectory` | `$(MSBuildProjectDirectory)/Frontend` | Frontend package directory. |
| `RunicApplicationFrontendGeneratedDirectory` | `<frontend>/src/generated` | Generated TypeScript clients. |
| `RunicApplicationFrontendPackageManager` | `packageManager` in `package.json`, then `pnpm-lock.yaml`, `bun.lock`, else `npm` | Selects the default build and install commands. |
| `RunicApplicationFrontendBuildCommand` | `npm run build`, `pnpm run build`, or `bun run --bun build` | Production frontend build. |
| `RunicApplicationFrontendInstallCommand` | `npm ci`, `pnpm install --frozen-lockfile`, or `bun install --frozen-lockfile`, each with `--ignore-scripts` | Runs before the frontend build when `package.json` or a lock file changed since the last install (stamp: `obj/runic-bridge-frontend-install.stamp`), or when declared dependencies have no `node_modules`. A `package.json` without dependencies needs no `node_modules`. |
| `RunicApplicationFrontendInstallEnabled` | `true` | Set `false` when a workspace install owns the frontend packages. |
| `RunicApplicationFrontendBuildEnabled` | `true` | Set `false` when another tool owns the frontend build. TypeScript is still generated. |
| `RunicApplicationFrontendCopyEnabled` | `true` | Copies `<frontend>/dist` to `www/` in the build output. Publish always copies it. |
| `RunicApplicationFrontendCompositionType` | `<project name>.RunicBridgeComposition` (host adapters) | Generated composition class. |
| `RunicApplicationFrontendModelAssembly` | the project itself | Inspect a separately built ViewModel assembly instead of a bootstrap build. |
| `RunicApplicationFrontendReactiveUiFlavor` | none | `primitives` or `reactive` for ReactiveUI projects. |
| `RunicApplicationFrontendGenerationEnabled` | see below | `true` requires Bridge generation, `false` turns it off. |
| `RunicApplicationFrontendDocumentationEnabled` | `true` | `false` skips the XML documentation file that the bootstrap pass writes for comments in the generated TypeScript. |
| `RunicApplicationFrontendWindowProject` | `false` | `true` marks a Window project that `dotnet runic dev` and `doctor` run. |

`dotnet build` and `dotnet publish` copy the built frontend as loose files to
`www/` next to the executable; they do not embed it. `dotnet runic dev` sets
`RunicApplicationFrontendBuildEnabled=false` and
`RunicApplicationFrontendCopyEnabled=false` while its development server serves
the frontend. The
[`dotnet runic` README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)
lists the development-server properties it reads.

Bridge generation turns on by default for a project that references
`Runic.Application.Views` directly, references a host adapter
(`Runic.Application.Views.Desktop`, `Runic.Application.Views.CsWebUi` or
`Runic.Application.Views.Wpf`) directly or transitively, or builds in this repository.
Each adapter's build props set `RunicApplicationHostAdapter` to `true`, which is
what the targets check. A project that reaches `Runic.Application.Views` only through
another project, such as a library that references a model project, does not
generate; if it declares a Window or View anyway, the build warns with
`RUNICBRIDGE015`. A test project (`IsTestProject` is `true`, which
`Microsoft.NET.Test.Sdk` and TUnit set) that references a Runic application
uses the application's generated Bridges: when it declares no Window or View
of its own and has no frontend `package.json`, `RunicApplicationFrontendInput`
items or `RunicApplicationFrontendModelAssembly`, generation stays off there without a
diagnostic or a bootstrap build. Any other project keeps the diagnostics
below. That default is optional:
when the assembly declares no Runic Window or View, for example an application
that only uses the navigator, the build generates nothing (and removes stale
generated C# and TypeScript), skips the frontend install, build and copy steps,
and succeeds with a normal-importance message. A publish that reuses an earlier
build (`--no-build`) still copies an existing `<frontend>/dist` to `www/`.
Generation is required, and `RUNICBRIDGE006` stays an error for an assembly
without a Window, when the project sets `RunicApplicationFrontendGenerationEnabled` to `true`, or
when it shows that it expects output: a `package.json` in
`RunicApplicationFrontendDirectory`, `RunicApplicationFrontendInput` items,
`RunicApplicationFrontendCompositionType` or `RunicApplicationFrontendModelAssembly`. Projects that
reference `Runic.Application.Views.CsWebUi` or `Runic.Application.Views.Desktop`, other than
those test projects, always require generation, because the adapter sets
`RunicApplicationFrontendCompositionType`. WPF projects (`UseWPF`) stay optional, because a
WPF application often keeps its Views in a WPF-free model project. A
navigator-only application should set
`RunicApplicationFrontendGenerationEnabled` to `false`: that also skips the nested bootstrap
build, which optional mode still pays for.

When the Views live in a referenced project, the application copies that
project's built `<frontend>/dist` to its own `www/` on build and publish, so the
application itself can set `RunicApplicationFrontendGenerationEnabled` to `false`. The
application's own frontend wins over a referenced one with the same file. WPF's
temporary markup-compile project (`*_wpftmp`) never runs the generator; it
compiles the code that the real project generated.

A single-project application is compiled twice: a bootstrap pass with an empty
generated composition, which the generator inspects, then the real build with
the generated code. `RunicApplicationFrontendBootstrap` is `true` only in the bootstrap pass.

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
must render the empty state. A `ViewRegistry<State["main"]>` accepts the nullable
slot type and checks its non-null kinds. `TContent` must be an interface or base class of
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

`OpenWindowAsync` passes the scope's
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
| 1013 | `AcceptedWorkFailed` | Error | A task owned by an `AcceptedWorkScope` faults. | `ErrorType` |
| 1014 | `AcceptedWorkDomainFailed` | Debug | A task owned by an `AcceptedWorkScope` throws a declared failure. | `FailureType` |
| 1020 | `ViewMountFailed` | Error | A .NET View fails to mount. | `Route`, `ErrorType` |
| 1021 | `ViewRemountFailed` | Error | A View fails to mount again after a reconnect. | `Route`, `ErrorType` |
| 1033 | `ModelContextReleaseFailed` | Error | A window session's own model context fails to shut down in the background. | `ErrorType` |
| 1040 | `RoutedRegionRouteIncompatible` | Error | A ReactiveUI `ReactiveRoutedRegion<T>` receives a ViewModel that is not a `T`, so it presents no content. | `Region`, `Model` |
| 1041 | `RoutedRegionRouterFailed` | Error | The router observed by a `ReactiveRoutedRegion<T>` fails; the region keeps its last content. | `Region`, `ErrorType` |
| 1042 | `ReactiveCommandFailed` | Error | A ReactiveUI command or object observed with `ObserveBridgeExceptions(logger)` reports an exception other than a declared `RunicFailureException` or a cancellation on `ThrownExceptions`. | `Source` (the command expression or `sourceName`), `ErrorType` |
| 1043 | `ReactiveSchedulerOutsideModelTurn` | Warning | Work is scheduled on the model-context main-thread scheduler that `AddRunicReactiveModelContext()` installs, outside a model turn, so it runs on the scheduler it replaced. Logged once per process; to `Trace` unless `InstallMainThreadScheduler(loggerFactory)` received a logger factory. | `Fallback` |
| 1050 | `WindowRegistrationMissing` | Error | `ValidateWindow` or `OpenWindowAsync` finds a Window's generated Bridge unregistered, on any host. | `Code`, `DiagnosticMessage`, `Remediation` |
| 2000 | `DesktopSnapshotDeliveryFailed` | Error | Runic Desktop cannot run a state delivery script. | `Route`, `ErrorType` |
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

Events 1000-1012, 1020, 1021 and 1050 use the category `Runic.Application.Views`
(`RunicViewsTelemetry.LogCategory`). Event 1033 here is a window session's
own model context failing to shut down, and uses the session's logger.
Events 1013 and 1014 use the logger passed to `new AcceptedWorkScope(logger)`,
or `Trace` without one.
Navigation (1060-1074) and the model context (1030-1033) log under
`Runic.Navigation` and `Runic.Navigation.RunicModelContext`; see the
[Runic.Navigation logging](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#logging)
section. Events 2000 and 2002 use `Runic.Application.Views.Desktop`. Events 3000-3005
use `Runic.Desktop` and need `DesktopHostOptions.LoggerFactory`.

Events 1040-1042 come from `Runic.Application.Views.ReactiveUI` (and its `.Reactive`
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

The configured frontend build command (`RunicApplicationFrontendBuildCommand`) runs
only when one of its inputs is newer than
`obj/<Configuration>/<TargetFramework>/runic-bridge-frontend.stamp`: files under
`RunicApplicationFrontendDirectory` (excluding `node_modules`, `dist`, `build`, `bin`, `obj`
and dot-directories), the generated TypeScript, and the project file. Generated
TypeScript is rewritten only when its content changes, so C#-only edits skip the
frontend build. Add `RunicApplicationFrontendInput` items, from a target that runs
before `RunicViewsGenerateBridge`, for inputs outside the frontend directory such
as workspace packages or generated assets; the translations editor is an example.
Delete the stamp or rebuild to force a frontend build. Set
`RunicApplicationFrontendBuildEnabled=false` when another tool, such as a running dev server,
owns the frontend build; `dotnet runic dev` does this while its development
server runs.

Generated C# and TypeScript files start with `// <auto-generated />`. Only such
files are removed from the output directories when a ViewModel disappears, so
`RunicApplicationFrontendGeneratedDirectory` may point into a directory with hand-written modules.

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
private readonly AcceptedWorkScope _acceptedWork;

public NotesModel(ILogger<NotesModel> logger) => _acceptedWork = new(logger);

public Task SaveAsync(CancellationToken cancellationToken) =>
    _acceptedWork.RunAsync(() => SaveAndRecoverAsync(cancellationToken));

public Task SaveAndCloseAsync(CancellationToken cancellationToken) =>
    _acceptedWork.RunAsync(async () =>
    {
        await SaveAndRecoverAsync(cancellationToken);
        // Closing disposes this model; that drain does not wait for this task.
        await _window.CloseAsync();
    });

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
stops that observer's wait; it neither cancels nor abandons owned work.
`DisposeAsync` is idempotent and waits without cancellation. Keep resources
alive through this drain even if a bridge invocation or an earlier drain
observer has already been cancelled.

Each owned task keeps its original outcome, and drain does not throw for it.
Because an invocation may stop observing a task before it fails, the scope logs
each fault once: event 1013 at Error, or 1014 at Debug for a declared
`RunicFailureException` (see [Logging and telemetry](#logging-and-telemetry)).
Cancellations are not logged. Pass the model's logger to the constructor;
without one, entries go to `System.Diagnostics.Trace`.

A drain or disposal started from accepted work of the same scope, such as the
"Save and close" task above, waits for all other accepted work but not for the
calling task or the accepted tasks awaiting it, which cannot finish until the
drain does. This applies to code that inherits the task's execution context,
including awaited continuations and `Task.Run`. Code that continues in that
task after the drain must not use the resources the owner released. If the
close is queued to another thread without the execution context, do not await
its disposal from the accepted task; let the task return instead.

The scope does not serialize operations, dispatch model changes, request
cancellation, or implement recovery. The application still owns those policies
and must return a task that covers all work requiring its resources.

## Troubleshooting

- **The frontend cannot resolve `./generated/...` imports.** The build writes
  the generated modules to `RunicApplicationFrontendGeneratedDirectory` (`Frontend/src/generated`
  by default). Build the .NET project once, or start `dotnet runic dev`, before
  you type-check or run the frontend on its own.
- **The build fails with `RUNICBRIDGE...`.** The error names the type and
  member and ends with a link to the ID's entry in the
  [diagnostics catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md),
  which says how to fix it. See [Generator diagnostics](#generator-diagnostics).
- **The frontend dependencies were not installed.** The build installs them
  when `node_modules` is missing. Install them with the frontend's package
  manager, or run `dotnet runic doctor` to check the package manager and lock
  file. Set `RunicApplicationFrontendInstallEnabled=false` when a workspace install owns
  them.
- **The build says only one ReactiveUI flavor can be used.**
  `Runic.Application.Views.ReactiveUI` and `Runic.Application.Views.ReactiveUI.Reactive`
  cannot both be referenced, and the `.Reactive` adapter needs
  `Runic.Navigation.ReactiveUI.Reactive`. Keep the pair for one flavor; see
  [Runic.Application.Views.ReactiveUI.Reactive](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/README.md).
