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
  and the `RUNICBRIDGE` diagnostics belong to this generated layer, along with
  the host-adapter contracts `IBridgeWindow`, `IBridgeTransport` and
  `BridgeSnapshotBatch`. Types an application names are `Runic*`, such as
  `RunicWindowCloseResult`, `RunicValidationMessage` and `RunicCodecAttribute`.
  The types that generated code calls are in the
  `Runic.Application.Views.Generated` namespace (see
  [Generated-code support API](#generated-code-support-api)).
- **Host**: the adapter that opens Windows on a platform: `RunicDesktopHost`
  (`Runic.Application.Views.Desktop`) and `RunicCsWebUiHost`
  (`Runic.Application.Views.CsWebUi`), both an `IRunicWindowHost`. In WPF,
  `RunicViewHost` hosts one View inside the visual tree. An open Window's
  `Host` property returns its per-Window adapter, an `IBridgeWindow`.
- **Presentation**: what shows a Window's or View's web content on screen: an
  embedded WebView or an installed browser window, such as
  `DesktopBridgeWindow.Window` (a `DesktopWindow`) or the WebView of a
  `RunicViewHost`. A presentation closes and reopens without changing the
  ViewModel.
- **Surface**: Runic Desktop's `DesktopSurface`, which serves a Window's web
  content (`DesktopSurfaceOptions.Content`), capabilities and browser sessions,
  and opens presentations of it. `DesktopBridgeWindow.Surface` is the Window's
  surface.
- **Owner**: the native window that platform services, such as file dialogs,
  the clipboard and file launchers, attach to: `DesktopNativeOwner` for a Runic
  Desktop window, or an `INativeWindowOwner` for another host. Native dialogs
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
    // A private setter keeps Count read-only to the page; Increment changes it.
    [ObservableProperty]
    public partial int Count { get; private set; }

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

`Frontend/index.html` loads `webui.js` and `runic-views.js` before the module.
The host serves `webui.js`, and the host package copies its Views client into
`www/` as `runic-views.js`, so the page names no host and one frontend build runs
on Runic Desktop and CS-WebUI (see [The host of a page](#the-host-of-a-page)). The
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
component. The browser opens and closes a presentation of each View it shows,
so a content session creates a fresh View for each presentation and releases it
when the outlet changes. The ViewModel belongs to its application or window DI scope and may
survive View changes. Window-local routes and operation admission are
host-neutral; native window creation belongs to the host adapter.

The generated composition class, `<RootNamespace>.RunicViewsComposition` unless
`RunicApplicationFrontendCompositionType` names another, provides two
registrations:

- `AddRunicViews()` registers the generated Bridges, every non-Window View as
  transient, and `ServiceProviderViewLocator` as the default
  `IRunicViewLocator`. Register an earlier `IRunicViewLocator` or View to
  replace a default.
- `AddRunicBridges()` registers only the Bridges, for applications that create
  Views themselves or use another locator, such as `ReactiveRunicViewLocator`.

ViewModels stay explicit registrations because their lifetime is an
application decision; a Window's root ViewModel must be scoped. The
registrations are generated code, so they need no reflection and are
NativeAOT-safe. The class carries `[GeneratedCode]` and
`[ExcludeFromCodeCoverage]`.

The namespace is the project's `RootNamespace` made a valid C# namespace: a
character other than a letter, digit, underscore or dot becomes `_`, and a
segment that starts with a digit or is a C# keyword gets a `_` prefix, so a
project named `2048Game` gets `_2048Game.RunicViewsComposition`. When a
project imports two compositions, for example its own and a model library's,
`services.AddRunicViews()` is ambiguous (CS0121); call each by its class
instead:

```csharp
Contoso.Notes.RunicViewsComposition.AddRunicViews(services);
Contoso.Notes.Model.RunicViewsComposition.AddRunicBridges(services);
```

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
re-attached bridge with a fresh View. A window has one revision counter for all
its routes, so a re-attached route never publishes an older revision. The
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
  ones, and returns a `RunicWindowCloseResult`; its token also stops only the
  caller's wait. Disposal is asynchronous only.
- `RequestCloseAsync(cancellationToken)` closes the window as its close button
  does and returns whether it closed; with `GuardClose` it asks `CanCloseAsync`
  first (see [Guarding a user close](#guarding-a-user-close)).
- `Host` returns the host adapter, an `IBridgeWindow`:
  `DesktopBridgeWindow<TViewModel>` (with `Surface`, `Window` and
  `NativeOwner`) or `CsWebUiBridgeWindow<TViewModel>` (with `Window`), each
  `Window` being the host's own window object.

### View lifetime hooks

A View can implement two optional interfaces. They are about the View, the
presentation object, not the ViewModel, which outlives its Views.

| Hook | Called when |
| --- | --- |
| `IRunicViewLifetime.OnAttached()` | the content session has created the View for a presentation and bound its `DataContext`, before the web component mounts |
| `IRunicWebMountLifetime.OnWebMounted()` | the browser reports that the View's web component mounted |
| `IRunicWebMountLifetime.OnWebUnmounted()` | the web component unmounted, or its presentation ended (a closed window, a released connection) |
| `IRunicViewLifetime.OnDetached()` | the View stops being presented, after `OnWebUnmounted` |

The order is always attach, web mount, then web unmount, detach; then the
session disposes the View if it is `IDisposable` and releases it. Each web
presentation gets its own View instance, so two windows showing one ViewModel
call the hooks of two Views. A View whose presentation fails to mount is
unmounted and detached for the hooks that already ran. The session calls the
hooks one at a time per View, in the window's model turn, so a hook may read
its ViewModel; it should not block or wait for another turn.

The hooks correspond to ReactiveUI's view activation (`WhenActivated`), Avalonia's
`AttachedToVisualTree`/`DetachedFromVisualTree` and MAUI's `Loaded`/`Unloaded`.
`ReactiveRunicView<T>` (`Runic.Application.Views.ReactiveUI`) implements both
interfaces: it activates an `IActivatableViewModel` on web mount and deactivates
it on web unmount and detach, and offers `protected virtual` hooks of the same
names to override without calling `base`.

Runic has no ViewModel lifetime interface. Use `IActivatableViewModel` with
`ReactiveRunicView<T>`, `INavigationAware` for navigated content, or set
CommunityToolkit's `ObservableRecipient.IsActive` from a View's
`OnWebMounted` and `OnWebUnmounted`.

### Opening a Window from a ViewModel

The host is passed only to the `Run` callback. To open Windows from a command
or a service, register `IRunicWindowService` and take it in a constructor:

```csharp
services.AddRunicWindowService();
```

With ReactiveUI, as in the
[multi-window example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/multi-window):

```csharp
public sealed class ProjectViewModel : ReactiveObject
{
    public ProjectViewModel(IRunicWindowService windows)
    {
        // Opens another Window on the same project, in its own scope. The service releases it when it closes.
        NewWindowCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            await windows.OpenWindowAsync<ProjectWindow>(cancellationToken: token);
        });
    }

    public ReactiveCommand<RxVoid, RxVoid> NewWindowCommand { get; }
}
```

With CommunityToolkit.Mvvm:

```csharp
public sealed partial class ProjectViewModel(IRunicWindowService windows) : ObservableObject
{
    [RelayCommand]
    private async Task NewWindowAsync() => await windows.OpenWindowAsync<ProjectWindow>();
}
```

- `OpenWindowAsync<TWindow>(options, cancellationToken)` has the same
  parameters and checks as the host's, and opens the Window in its own DI scope
  on the host that runs the application, on Runic Desktop and CS-WebUI alike.
  On CS-WebUI, `WaitForCloseAsync` completes only when every browser window has
  closed.
- The service is a singleton, so ViewModels in every Window and singleton
  services can take it. A host binds it before it resolves the ViewModel of the
  first Window it opens, so it already works in that ViewModel's constructor.
  Before any Window has opened it throws `InvalidOperationException`.
- The service releases each Window it opened, with its scope and ViewModels,
  once the Window closes, so a command doesn't need to keep it. Keep the
  returned Window to wait for it or to close it; disposing it earlier is safe.
  A release failure is logged as event 1051.
- Runic has no owned or modal Windows, so there is no owner parameter. The new
  Window's ViewModel is resolved from DI like the first one's. To give it input
  of its own, pass a parameter (since 0.14): `OpenWindowAsync<TWindow, TParameter>(parameter, options)`
  hands it to the new Window's scope, where the ViewModel, or a scoped service
  it uses, takes `IRunicWindowParameter<TParameter>`. Each Window gets its own,
  also when several open at once. State that every Window shares stays in a
  singleton service, as the
  [multi-window example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/multi-window)
  does with its shared project.

  ```csharp
  await windows.OpenWindowAsync<PartWindow, PartRequest>(new PartRequest(world, WindowedPart.Atlas));

  public sealed class PartViewModel(IRunicWindowParameter<PartRequest> request) : ReactiveObject
  {
      public WorldViewModel World { get; } = request.Value.World;
  }
  ```

  The host has the same overload, `host.OpenWindowAsync<TWindow, TParameter>(parameter)`.
  A test that builds such a ViewModel by hand passes
  `new RunicWindowParameter<PartRequest>(request)`.
- A test registers a fake that records the requested Window types after the
  application's services. The parameter overload has a default implementation
  that throws `NotSupportedException`, so a fake overrides it only when the
  application uses it.

#### Windows from another service provider

A host and its window service open Windows from one service provider. A part of
the application with services of its own, such as a dashboard that a setup
assistant starts in the same process once it has created what the dashboard
needs, builds its own provider and creates a host for it on the running
Desktop host:

```csharp
await using var dashboardServices = new ServiceCollection()
    .AddDashboard()            // its ViewModels, AddRunicViews() and AddRunicWindowService()
    .BuildServiceProvider();
var dashboard = new RunicDesktopHost(dashboardServices, setupHost.Desktop);
await using var window = await dashboard.OpenWindowAsync<DashboardWindow>();
await window.WaitForCloseAsync();
```

`setupHost` is the `RunicDesktopHost` the application runs on, and `Desktop`
its event loop's `DesktopHost`; a `RunicDesktopHost` created in
`DesktopEventLoop.Run(options, desktop => ...)` takes that `desktop`. On
CS-WebUI, use `new RunicCsWebUiHost(dashboardServices)`. The
`IRunicWindowService` of the second provider follows the second host once it
has opened a Window, so the dashboard's ViewModels open further Windows from
their own services. Both hosts share the event loop and its lifetime.

### A Window with its own page

Every Window opens `index.html` unless `RunicWindowOptions.StartPage` names
another page. With Vite, each page is a build input, and listing inputs replaces
the default `index.html`, so keep it in the list. The `runic-app` template's
`vite.config.ts` already lists it; add the second page next to it:

```ts
// Frontend/vite.config.ts (Vite 8)
export default defineConfig({
  base: "./",
  plugins: [svelte(), runic()],
  build: {
    rolldownOptions: { input: ["index.html", "settings.html"] },
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022"
  }
});
```

Create `Frontend/settings.html` beside `index.html`, for example as a copy that
loads its own entry script, and open it with:

```csharp
await windows.OpenWindowAsync<SettingsWindow>(new RunicWindowOptions { StartPage = "settings.html" });
```

The build writes `www/settings.html` beside `www/index.html`, and
`dotnet runic dev` serves both. Vite resolves inputs against its root, the
frontend directory. The object form, `input: { main: "index.html", settings:
"settings.html" }`, names the output chunks; `rollupOptions` still works in
Vite 8 but is deprecated. A page in a folder, such as `pages/settings.html`,
works the same way with `StartPage = "pages/settings.html"`:
`@runic-artifex/vite-plugin-runic` points its host scripts (`webui.js` and the
Runic client script) at the web root.

A page that the build does not emit fails before a window waits for it: on
Runic Desktop, opening the Window throws `RunicWindowConfigurationException`
with the code `content-entry-not-found`, which names the page and the pages the
content root has; `dotnet runic dev` stops with `RAPPDEV1009` when a build
input's file is missing. CS-WebUI does not check the page before it opens
the window.

### Guarding a user close

A Window that sets `GuardClose` asks `CanCloseAsync` before a close the user
starts (the title-bar close button, Alt+F4 or the window manager) and stays open
when it returns `false`, like `Window.Closing` with `e.Cancel = true` in WPF and
Avalonia, but asynchronous:

```csharp
public sealed partial class EditorWindow : RunicWindow<EditorViewModel>
{
    public EditorWindow() => GuardClose = true;
}
```

The declaration is the same for ReactiveUI and CommunityToolkit.Mvvm
ViewModels; only the guards and the commands below differ.
The default `CanCloseAsync` clears each `NavigationRegion<T>` the Window's
ViewModel exposes, in declaration order, so the departure guards of their
entries decide, as WPF's `NavigationHost.GuardWindowClose` does (see
[Navigation](#navigation)). The clears pass
`Admission = NavigationAdmission.Supersede`, so a region created with
`RejectWhileBusy` is cleared during a request instead of refusing the close as
`Busy`. Override it to add a check of your own,
and call the base method to keep the navigation guards. It starts in a turn of
the window's model context, one decision runs at a time, and it may wait as
long as a prompt needs; the event loop is never blocked. An exception keeps the
window open and is logged as event 1052.

A guard can ask the user in the page: a ReactiveUI interaction it handles, such
as `ReactiveLeaveConfirmation.WithInteraction`, reaches the web View's mounted
handler when the Window has exactly one presentation with that handler
mounted. It never reaches another Window. If the View unmounts or the Window
closes during the question, the guard's answer is "keep", so the Window stays
open (see
[Leave confirmation in a web View](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view)).

A guard can also ask with a native message box, which needs no page: it works
while the page is loading or busy, follows the platform's button order and
keys, and screen readers announce it as an alert. The ViewModel takes
`IDesktopMessageBoxes` (or `IWindowPlatformServices.MessageBoxes`) from the
window's scope, and the Window's override asks it:

```csharp
// EditorWindow
protected override async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken) =>
    await base.CanCloseAsync(cancellationToken) && (DataContext is not { } editor || await editor.ConfirmCloseAsync(cancellationToken));

// EditorViewModel(IDesktopMessageBoxes messageBoxes)
public async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken)
{
    if (!IsDirty) return true;
    var result = await messageBoxes.ShowAsync(new DesktopMessageBox($"Save changes to “{Title}”?")
    {
        Kind = DesktopMessageBoxKind.Warning,
        Buttons = [Save, DontSave, Cancel], // Default, Destructive and Cancel roles
    }, cancellationToken);
    return result switch
    {
        PlatformResult<DesktopMessageBoxButton?>.Success { Value.Id: "save" } => await SaveAsync(cancellationToken),
        PlatformResult<DesktopMessageBoxButton?>.Success { Value.Id: "discard" } => true,
        PlatformResult<DesktopMessageBoxButton?>.Success => false,
        // No native box on this host, such as CS-WebUI: ask in the page instead.
        _ => await AskInPageAsync(cancellationToken),
    };
}
```

The box is modal to the window and closes with it. See
[Message boxes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#message-boxes)
for the rules and each platform's behaviour.

To close a Window from code the way the close button does, call
`RequestCloseAsync(cancellationToken)`. It returns `true` when the window
closed and `false` when `CanCloseAsync` kept it open, like `Window.Close()`
in WPF and Avalonia, which raises the cancellable `Closing`, but awaitable.
Without `GuardClose` it closes. Keep the Window that `OpenWindowAsync`
returned, including from `IRunicWindowService`, to call it from a command; a
ViewModel closes its own Window through `IRunicWindowContext` (see
[Closing a ViewModel's own Window](#closing-a-viewmodels-own-window)).
With ReactiveUI, the command passes its token to the request. The Bridge starts
the command in a turn of its own Window's model context, so the body sets state
directly after `await`:

```csharp
CloseEditorCommand = ReactiveCommand.CreateFromTask(async token =>
{
    if (!await _editor.RequestCloseAsync(token)) StatusText = "The editor has unsaved changes.";
});
```

With CommunityToolkit.Mvvm:

```csharp
[RelayCommand]
private async Task CloseEditorAsync()
{
    if (!await _editor.RequestCloseAsync()) StatusText = "The editor has unsaved changes.";
}
```

A request while another request or a user close is being decided waits for
the same decision; its token stops only the caller's wait. When the window may
close, the visible window closes at once and disposal releases its scope, as
after a user close.

- `CloseAsync`, disposal and a host shutdown (Ctrl+C, SIGTERM) are the
  application's own close: they do not ask, and they cancel a pending
  decision's token. A pending `RequestCloseAsync` then returns `true`.
- On Runic Desktop the embedded window asks, and `RequestCloseAsync` is its
  native close request. A Window that also has
  `WindowOptions.ConfirmCloseAsync` asks its own guard first, then that
  confirmation, for both.
- A presentation that cannot keep its window open opens the Window unguarded and
  logs event 1053 `WindowCloseGuardUnavailable` instead of failing: CS-WebUI, a
  browser window (an explicitly selected browser or the `EmbeddedThenBrowser`
  fallback), or a custom embedded-window host without the `CloseConfirmation`
  capability. A browser tab or window closes without asking the application.
  Save drafts as the user edits, or handle the close afterwards, where that
  matters. `RequestCloseAsync` still asks there, because the application is
  the one closing.

### Closing a ViewModel's own Window

A Quit or Done command closes the Window its ViewModel belongs to through
`IRunicWindowContext`, which `AddRunicWindowService()` registers per scope.
`OpenWindowAsync` binds it to the Window it opens before it resolves the
Window's ViewModel, so the ViewModel takes it in its constructor and needs no
holder that the code calling `OpenWindowAsync` fills in:

```csharp
services.AddRunicWindowService();
```

With ReactiveUI:

```csharp
public sealed partial class EditorViewModel : ReactiveObject
{
    public EditorViewModel(IRunicWindowContext window)
    {
        // Closes this Window as its close button does: GuardClose and the navigation guards decide.
        QuitCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            if (!await window.RequestCloseAsync(token)) StatusText = "Save or discard the draft first.";
        });
    }

    [Reactive] public partial string StatusText { get; private set; } = "";

    public ReactiveCommand<RxVoid, RxVoid> QuitCommand { get; }
}
```

With CommunityToolkit.Mvvm:

```csharp
public sealed partial class EditorViewModel(IRunicWindowContext window) : ObservableObject
{
    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [RelayCommand]
    private async Task QuitAsync()
    {
        if (!await window.RequestCloseAsync()) StatusText = "Save or discard the draft first.";
    }
}
```

- `RequestCloseAsync(cancellationToken)` is the Window's own
  `RequestCloseAsync`: it asks `CanCloseAsync` when the Window sets
  `GuardClose`, returns `false` with the Window open when that keeps it, and
  `true` once it closed. It works on Runic Desktop and on CS-WebUI, where the
  request is guarded although the browser's own close is not.
- The context belongs to the Window's DI scope. A scoped service that the
  ViewModel takes shares it; a navigation entry created with
  `RunicNavigatorOptions.CreateEntryScopes` has a scope of its own, so pass the context to it from
  the Window's ViewModel. Resolved outside a Window's scope, or called before
  the Window has opened, for example from the ViewModel's constructor, it
  throws `InvalidOperationException`.
- The interface has one method, so a test registers a fake that answers
  `true` or `false` after the application's services, or opens the Window and
  lets its guards decide.

### Menu bar commands

A Window's root ViewModel runs the menu bar's commands while its Window is the
key window by implementing `IRunicMenuCommandSource`. The host's menu bar
(Runic Desktop's `DesktopHostOptions.MenuBar`) names each command by id:

```csharp
public sealed partial class EditorViewModel : ObservableObject, IRunicMenuCommandSource
{
    public ICommand? GetMenuCommand(string commandId) => commandId switch
    {
        "file.save" => SaveCommand,
        "edit.find" => Find.OpenCommand, // a child ViewModel's command
        _ => null,
    };
}
```

The host asks once for each id when the Window opens, and again after
`DesktopHost.SetMenuBar`, in the ViewModel's model turn. An item is enabled
while its declared `IsEnabled` and its command's `CanExecute(parameter)` are
`true`, following `CanExecuteChanged`, and runs `Execute(parameter)` in the
model turn, where `parameter` is the item's `CommandParameter` (`null` for
none). One command can therefore serve several items, such as one per zoom
level in a radio group:

```csharp
new DesktopMenu("View",
[
    new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = zoom == 0.8, CommandParameter = "0.8" },
    new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = zoom == 1.5, CommandParameter = "1.5" },
])
```

A failure is logged as event 2004, `DesktopMenuCommandFailed` (2102,
`CsWebUiMenuCommandFailed`, on CS-WebUI). An id the
ViewModel returns `null` for goes to the application's commands,
`RunicDesktopHost.UseApplicationMenu`, such as New or Settings…, which also run
while no Window is open. Runic Desktop shows the menu bar natively on macOS, in
the Windows window and in GTK 3 and GTK 4 windows. A
Window whose `DesktopWindowOptions.MenuBarPresentation` is `Page` renders the
bar in its page instead and runs its items through
`DesktopWindow.TryExecuteMenuItemAsync`, which uses the same commands; see
[the Desktop guide](../Runic.Desktop/README.md#where-the-bar-is-shown). A Runic
Window's page does this with `createMenuBarController` from
`@runic-artifex/views` and its own menubar component; see
[In-page menu bar](../../web/views/README.md#in-page-menu-bar). A CS-WebUI
window has no native bar, so `RunicCsWebUiHostOptions.MenuBar` always goes to
the page, through the same routes and the root ViewModel's commands; see
[the CS-WebUI guide](../Runic.Application.Views.CsWebUi/README.md#menu-bar).

A ViewModel shows a context menu with
`IWindowPlatformServices.ShowContextMenuAsync`, from a command the page runs in
its `contextmenu` handler, and runs the chosen item itself; see
[Runic.Platform](../Runic.Platform/README.md#menus).

### Keyboard shortcuts in the page

In-window shortcuts belong to the page: handle `keydown` and run the generated
command, as a button would. `matchesShortcut` and `formatShortcut` from
`@runic-artifex/views` read the same shortcut text as `DesktopShortcut`, so one
`Primary+S` is ⌘S on macOS and Ctrl+S on Windows and Linux:

```ts
import { formatShortcut, matchesShortcut } from "@runic-artifex/views";

addEventListener("keydown", (event) => {
  if (matchesShortcut(event, "Primary+S") && editor.state.canSave) {
    event.preventDefault();
    void editor.save();
  }
});
saveButton.title = `Save (${formatShortcut("Primary+S")})`;
saveButton.setAttribute("aria-keyshortcuts", "Control+S Meta+S");
```

- **Primary, not Ctrl.** `Primary` is `metaKey` on Apple platforms and
  `ctrlKey` elsewhere; matching requires exactly the shortcut's modifiers, and
  keys an input method is composing never match.
- **Text editing comes first.** Leave Undo, Cut, Copy, Paste, Select All and
  arrow-key movement to text fields; check `event.target` before taking a key
  that editing uses.
- **Keys the window keeps.** The operating system and web view reserve some
  keys: ⌘Q, ⌘W, ⌘H and ⌘M on macOS, Alt+F4 on Windows, F12 and the zoom keys in
  embedded windows. A key the page handles with `preventDefault()` does not
  reach a menu item with the same shortcut, so declare a command's shortcut on
  the menu item or in the page, not both.
- **Make shortcuts discoverable.** Show them beside the control or in the
  page's menus with `formatShortcut`, and set `aria-keyshortcuts` so screen
  readers announce them.

Global shortcuts, which work while the application is in the background, are
the application's `IDesktopGlobalShortcuts`, not the page's. Let the user choose
the key on a settings page: `shortcutFromEvent` records a `keydown` as the same
text, which the page sends through an ordinary command; the ViewModel keeps it
in the application's settings and binds it.

```ts
import { shortcutFromEvent } from "@runic-artifex/views";

keyField.addEventListener("keydown", (event) => {
  const shortcut = shortcutFromEvent(event);
  if (shortcut === null) return; // A modifier alone, a dead key or composition.
  event.preventDefault();
  void settings.chooseQuickNoteKey(shortcut);
});
```

```csharp
[RelayCommand]
public async Task ChooseQuickNoteKeyAsync(string shortcut, CancellationToken cancellationToken)
{
    if (!DesktopShortcut.TryParse(shortcut, out var key)) return;
    var result = await _shortcuts.BindAsync(
        [new DesktopGlobalShortcut("quick-note", "Open a quick note") { PreferredShortcut = key }], cancellationToken);
    if (result is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success([{ Status: DesktopGlobalShortcutStatus.Bound }]))
        _settings.QuickNoteKey = key.ToString();
    else
        QuickNoteKeyProblem = "That key is taken or not allowed; choose another.";
}
```

Windows and macOS bind the key the application passes and report a key another
application holds as `Conflict` and a key the system keeps, such as F12 or ⌘Q,
as `Refused`. On Linux the desktop owns the keys: the key is a suggestion, the
user assigns or changes it in the desktop's settings (`ConfigureAsync` opens
them), and `BindingsChanged` reports the change, so show the desktop's
`TriggerDescription` instead of a recorder there. See
[Global shortcuts](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#global-shortcuts).

### Receiving dropped files

A Window's root ViewModel receives the files the user drops onto its window,
with their real paths, by implementing `IRunicFileDropTarget`:

```csharp
public sealed partial class EditorViewModel : ObservableObject, IRunicFileDropTarget
{
    public async ValueTask OnFilesDroppedAsync(RunicFileDrop drop, CancellationToken cancellationToken)
    {
        foreach (var file in drop.Files.Where(static file => !file.IsDirectory))
            await OpenAsync(file.Path, cancellationToken);
    }
}
```

The host calls it in the ViewModel's model turn, as a command runs, once per
drop and in drop order; the token is cancelled when the Window closes. A failure
is logged as event 1054, `WindowFileDropFailed`, and the Window stays open.
`RunicFileDrop.Files` lists each dropped file or directory as a
`RunicDroppedFile` with its absolute `Path`, `Name` and `IsDirectory`.

The paths come from the native window, never from the page. Runic Desktop's
embedded window (WebView2, WKWebView, GTK 3 and GTK 4) and the WPF child web
View deliver drops; CS-WebUI and an installed browser have no native window
that could, so there the page only gets HTML5 `File` objects without paths. The
page keeps its own `dragenter`, `dragover` and `drop` events, so it can
highlight a drop target, and dropping a file never navigates the Window away.
To tell drop areas apart, mark an element with `data-runic-drop-target`:

```html
<li data-runic-drop-target="folder:42">Invoices</li>
```

The nearest marked element under the drop becomes `RunicFileDrop.Target`. The
page reports it, so treat it as an untrusted routing label: never as a path,
and never for an access decision. In a Flatpak sandbox the paths are
document-portal paths under `/run/user/<uid>/doc`, which the application can
open; a macOS App Sandbox grants access to dropped files while the application
runs.

`RunicFileDrop.Effect` is `Copy`, `Move` or `Link`, as the drag source, the
user's modifier keys and the page agreed; mark where a drop copies, moves, links
or is refused with `data-runic-drop-effect="copy|move|link|none"`, and treat the
effect like the label. Files without a local path, such as a macOS Mail
attachment, arrive in `RunicFileDrop.PromisedFiles` (`Files` may then be empty):
read each once with `OpenReadAsync` and dispose it; the host disposes the ones
the handler did not open when it returns. Drops into a frame of the page's own
origin arrive as drops onto the page; frames of other origins deliver none.
Dragged text and links stay with the page's own `drop` event.

### Dragging files out

A ViewModel drags files, text or links out of its Window with
`IWindowPlatformServices.StartDragAsync`, while the user holds the mouse button.
The page cancels its own drag in `dragstart` and runs an ordinary command with a
key; the ViewModel builds the data from its own state:

```js
row.addEventListener("dragstart", (event) => {
  event.preventDefault();
  void files.dragItem(row.dataset.key);
});
```

```csharp
[RelayCommand]
private async Task DragItemAsync(string key)
{
    var result = await _platform.StartDragAsync(new DesktopDragData
    {
        Paths = [_items[key].Path], AllowedEffects = DesktopDragEffects.Copy | DesktopDragEffects.Move,
    });
    if (result is PlatformResult<DesktopDragEffects>.Success(DesktopDragEffects.Move)) await RemoveAsync(key);
}
```

The result is the drop's effect, or `None` when the drag was cancelled or no
button was held. Runic Desktop's WebView2 and WKWebView windows and WPF windows
(`WpfPlatformServices.ForWindow`) start drags; see
[the Desktop guide](../Runic.Desktop/README.md#dragging-files-out).

### Copying and pasting from C#

A ViewModel that copies or pastes more than text takes `IDesktopClipboard`, the
window's clipboard with HTML, RTF, PNG images and file lists
([Runic.Platform's clipboard](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform/README.md#clipboard)):

```csharp
public sealed partial class ReportViewModel(IDesktopClipboard clipboard) : ObservableObject
{
    [RelayCommand]
    private async Task CopyChartAsync() => await clipboard.WriteAsync(new() { Image = await RenderChartPngAsync(), Text = Summary });
}
```

Use it for what the page cannot do: a command from a menu or a shortcut, work
that finishes after the click, such as copying an export, and file lists. The
page's own `navigator.clipboard` stays the simple choice for text and images it
already has, under a user gesture and the web view's permission rules; it cannot
copy files. Runic Desktop windows (Windows, macOS, GTK 3 and GTK 4) and WPF windows
provide the formats; CS-WebUI reports them unavailable.

### Moved and renamed since 0.7.0-preview.6

The Window types and entry points of the two hosts were merged into this API,
and package, build property and outlet names now match what they are. Earlier
API shapes were removed rather than deprecated. Since 0.9 the old build property
and item names no longer apply: each fails the build with
[RUNICBRIDGE017](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge017),
which names the new one. Angular's `RunicViewOutlet` remains a deprecated alias,
removed in the next major release. The old package IDs get no further releases, so replace them
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
| `SetRootFolder`, `SetSize`, `SetPort`, `Show`, `ShowWebView`, `StartServer` on `CsWebUiBridgeWindow<T>` and `CsWebUiWindow<T>` | `RunicWindowOptions`, and `UseWebView`, `ConfigureWindow` and `ShowWindow` of `RunicCsWebUiHostOptions`; the bridge window's `Window` remains |
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
| `RunicBridgeReactiveUiFlavor` | removed; the ReactiveUI flavor is inferred (see `RunicApplicationFrontendReactiveUiFlavor` below) |
| `RunicBridgeDocumentation` | `RunicApplicationFrontendDocumentationEnabled` |
| `RunicBridgeBootstrap` (set in the bootstrap pass) | `RunicApplicationFrontendBootstrap`; the bootstrap pass no longer sets the old name |
| `RunicViewsWindowProject` | `RunicApplicationFrontendWindowProject` |
| `RunicBridgeFrontendInput` items | `RunicApplicationFrontendInput` items |
| `RunicApplicationId` (`runic-app` template, 0.8 and earlier) | `ApplicationId`, which `Runic.Platform.MacOS` and `Runic.Platform.Linux.Polkit` read too |
| `RunicApplicationName` (`runic-app` template, 0.8 and earlier) | `ApplicationTitle` |
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
[RelayCommand] // or ReactiveCommand.CreateFromTask(SaveAsync)
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
- With the ReactiveUI adapters' main-thread scheduler, ReactiveUI work that such
  background code schedules, such as a command's result and `IsExecuting`, an
  `ObserveOn(RxSchedulers.MainThreadScheduler)` or a `ToProperty`, still runs on
  the context of the turn whose async flow started it (since 0.10). A plain
  `command.Execute()` that hand-written code starts in a turn therefore delivers
  its notifications on that context, as a bridged execution does. The scheduler
  does not move a plain property write; commit those with `InvokeAsync`.
- Don't block a turn on asynchronous work (`.Result`, `.Wait()`,
  `GetAwaiter().GetResult()`): its continuation needs the same context, so it
  deadlocks, as it would on a UI thread.
- When the context closes, for example because the window closed, pending
  continuations run on the thread pool outside any turn, so the command still
  finishes; nothing presents its later writes.
- With WPF, `DispatcherModelContext` is the UI dispatcher, so the same rule
  holds there. Navigation hooks are not commands; see
  [Runic.Navigation threading](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#threading).

## State before a reply

A command, setter or checked write often changes another View than the one
it was called on, such as a page command that adds a document to its shell's
list. A reply carries no state: it names the `revision` of the first state of
its route that includes the call's effects, and every View's state travels as
pushed states and frames. Runic sends every push that the call produced, on
every route of the window, before it sends the reply (read-your-writes), and
the client resolves the call once its route has reached that revision. When
the page's call resolves, the other Views' clients have already applied those
states, so code after `await` can read them without waiting for a
subscription:

```ts
await workspace.load(request);
const document = shell.snapshot.documents.at(-1); // includes what load added
```

- "Produced" means requested before the reply was ready: a change made in the
  command, or in a model turn that ran before its reply, is included. A
  change that work outside the command makes later, such as a timer or a
  `Task.Run` that commits afterwards, arrives as an ordinary frame.
- The guarantee covers the `command`, `set` and `write` requests of awaited
  and synchronous commands, setters and checked writes on Runic Desktop,
  CS-WebUI, WPF and `Runic.Application.Testing`'s in-memory transport. An
  operation's admission (`start`) also names a revision; its status follows the
  [operation rules](#operation-results-and-parameterised-queries).
- A reply waits at most 5 seconds for those pushes. A page that takes no
  pushes for longer, for example because it is frozen in the debugger, gets
  the reply anyway, and the states follow. A client whose route has not reached
  the reply's revision 5 seconds later reads a `snapshot` of the route.
- A synchronous route that a test calls inside the model turn, for example
  through `IRunicModelContext.InvokeAsync`, replies at once: the states
  cannot be captured until that turn ends.
- A custom `IBridgeTransport` keeps the guarantee when `BroadcastAsync` (and
  `IBridgeConnectionTransport.SendAsync`) returns once the push is on the
  connection ahead of later sends, as Runic's hosts do (see
  [Custom window hosts](../Runic.Application.Testing/README.md#custom-window-hosts)).

The rules are in the protocol's
[reply order](../../../specs/application/README.md#reply-order) and
[client resolution](../../../specs/application/README.md#client-resolution).

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

The examples in this README use partial properties. `[ObservableProperty]`
on a field, such as `[ObservableProperty] private bool isDirty;`, is still
supported, but the property it generates always has a public setter, so the
web frontend could set it too. A field suits a form field, such as the First
Window example's `step`. For status, use a partial property with a private
setter, or write the property out with `SetProperty` and a private setter. With
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

- Every C# type has one TypeScript form; [Wire types](#wire-types) lists them.
  Numbers follow JavaScript's precision, so `long` is a `bigint`.
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
  because a region can be empty; see [Navigation](#navigation).
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

<a id="generated-code-support-api"></a>
The generated C# bridge calls public runtime types such as `ViewModelBridge<T>`,
`PropertyDescriptor<T>`, `CommandDescriptor<T>`, `BridgeWire`, `BridgeJson` and
`ReactiveCommandExecution`. They are in the `Runic.Application.Views.Generated`
namespace (`Runic.Application.Views.ReactiveUI.Generated` and
`Runic.Application.Views.ReactiveUI.Reactive.Generated` for the ReactiveUI
helpers) and marked `[EditorBrowsable(Never)]`, so they stay out of completion
lists and application namespaces. They are not application API: the release
policy keeps them binary compatible within a major release for code that an
earlier minor release generated, but their source shape may change for
hand-written code. Generated files name them with `global::`, so regenerating
is the only migration; hand-written code that names them, such as a test Bridge,
adds `using Runic.Application.Views.Generated;`.

Each ViewModel's generated bridge is an internal class `<Name>Bridge`, such as
`CounterBridge` for `CounterViewModel`, in the `Runic.Application.Views.Generated.Bridges`
namespace of the application's assembly, not beside the ViewModel, so no
application type collides with it. Code in that assembly, or in a test project it
grants `InternalsVisibleTo`, that constructs a bridge adds
`using Runic.Application.Views.Generated.Bridges;` (see
[Runic.Application.Testing](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md)).

### Wire types

State, DTO members, command arguments and results use these TypeScript types in
the generated client. The client checks every value it receives against them,
so a malformed reply never reaches application state.

| C# | TypeScript | On the wire |
| --- | --- | --- |
| `bool` | `boolean` | |
| `int`, `uint`, `short`, `ushort`, `byte`, `sbyte` | `number` | A number; the client rejects one outside the C# type's range. |
| `double`, `float` | `number` | A finite number. |
| `long`, `ulong`, `BigInteger` | `bigint` | A decimal string, such as `"9007199254740993"`. |
| `decimal` | `string` | Invariant decimal text, such as `"12.50"`, so no digit is lost. |
| `string` | `string` | |
| `Guid` | `string` | `"d3b07384-d9a0-4c9b-8f1e-2a6c0e5f7b11"`, written in lower case; either case is accepted. |
| `DateTimeOffset` | `string` | ISO 8601 with its offset, `Z` for a zero offset: `"2026-10-03T12:30:00+02:00"`, `"2026-10-03T10:30:00.5Z"`. The browser's `new Date().toISOString()` is accepted. |
| `DateTime` | `string` | ISO 8601: `Z` for UTC, an offset for local time, none for an unspecified kind. A value ending in `Z` reads back as UTC. |
| `DateOnly` | `string` | `"2026-10-03"` |
| `TimeOnly` | `string` | `"08:15:00"`, `"08:15:00.5"`: a fraction of up to 7 digits, written without trailing zeros. |
| `TimeSpan` | `string` | An ISO 8601 duration of days, hours, minutes and seconds, such as `"PT12.3456789S"`, `"-P1DT2H3M4.5S"` or `"PT0S"`; `Temporal.Duration.from` reads it. Years, months and weeks are rejected. |
| An enum | A union of its wire names, such as `"Editor" \| "Preview"` | The C# name, or its `[RunicAlias]`. A `[Flags]` enum needs a `[RunicCodec]`. |
| `T?`, for a value or a reference type | `T \| null` | `null`. In a value that reaches the page the member is always present; see [Optional members](#optional-members) for values the page sends. |
| `T` with nullable reference types enabled | `T` | Never `null`. Without annotations (`#nullable disable`), a reference type is `T` too. |
| `T[]`, `List<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `ICollection<T>`, `IEnumerable<T>`, `ImmutableArray<T>`, `ImmutableList<T>`, `ObservableCollection<T>`, `ReadOnlyObservableCollection<T>`, `ReadOnlyCollection<T>` | `readonly T[]` | An array. `string?[]` is `readonly (string \| null)[]`. Multi-dimensional arrays and other collections fail the build. |
| `Dictionary<string, T>`, `IDictionary<string, T>`, `IReadOnlyDictionary<string, T>` | `Readonly<Record<string, T>>` | An object. Other key types fail the build. |
| A record, class or struct (DTO) | An `interface` with `readonly` members | A member's name starts with a lower-case letter (`MeasuredAt` is `measuredAt`), unless `[RunicAlias]` or `[JsonPropertyName]` names it. |
| A `[RunicUnion]` | A union discriminated by `$case` | |
| A type with `[RunicCodec]` | The codec's TypeScript type | Whatever the codec writes. |
| `JsonElement`, `JsonElement?`, `JsonNode?` | `unknown` | Any JSON value, as it is. A `JsonElement` holds JSON `null` as a value; a default `JsonElement`, which holds none, is written as `null`. `JsonNode?` and `JsonElement?` are C# `null` for JSON `null`. |
| `JsonNode` | `NonNullable<unknown>` | Any JSON value except `null`, which a `JsonNode` cannot hold. `JsonObject`, `JsonArray`, `JsonValue` and `JsonDocument` fail the build: declare a `JsonNode`, or a DTO for a known shape. |

Other framework types, such as `object`, `char`, `Uri` and tuples, fail the
build with a `RUNICBRIDGE` diagnostic that names the member.

A `number` is exact only up to 2^53 (`Number.MAX_SAFE_INTEGER`). A `long` can
be larger, and an id or a version above that would silently change if it were
a `number`, so `long`, `ulong` and `BigInteger` are `bigint`. The generated
TSDoc of such a member says so. A `bigint` does not mix with `number` in
arithmetic or `Math`, and `JSON.stringify` rejects it, so a formatter written
for numbers needs `Number(value)` first; `BigInt(value)` converts back.
For a value the UI formats, such as a byte count or a duration in
milliseconds, prefer `double` in C#: it is a `number` in TypeScript and exact
for every whole number up to 2^53, which is 8 PiB as a byte count. Keep `long`
for ids and counters that must stay exact. Use `int` for a small count.

The client checks a JSON value in both directions: it must be `null`, a
boolean, a finite number, a string, or an array or plain object of them, at
most 64 levels deep. `undefined` (except as an object member, which
`JSON.stringify` leaves out), `NaN`, a `bigint`, a `Date` or a `Map` is
rejected before the call rather than changed by `JSON.stringify`. The value is
not copied. Its numbers are JavaScript numbers in the page, exact only up to
2^53, while .NET keeps the digits it reads, such as `2.50`.

A frontend whose own types use optional members (`version?: string`) maps the
`T | null` members at its edge, for example with `value ?? undefined`.

### Optional members

A DTO that only the page sends, as a command input or an interaction result,
declares as optional (`label?: string | null`) the members .NET fills in when
the page leaves them out:

- a nullable member, which is `null` when it is missing;
- a constructor parameter with a default, which takes that default;
- a member with a setter or `init` that is nullable, which keeps the value a new
  instance has, such as its initializer.

```csharp
public sealed record BuildRequest(string Action, string? Host = null, int Retention = 3);
```

```ts
await builds.start({ action: "switch" }); // host null, retention 3
```

A non-nullable member without a constructor default stays required: .NET
cannot tell whether it has an initializer. A type that also reaches the page,
as state, a command result, a declared failure or an interaction input,
declares every member as present, because .NET always writes it; .NET still
fills in what a page leaves out.

A `JsonElement` member tells an absent value from `null`. A default
`JsonElement`, which holds no value, is left out of the object, and the member
is optional (`previous?: unknown`) everywhere: `undefined` in the page is
absent, `null` is JSON `null`. A missing member reads as a default
`JsonElement`. Elsewhere, such as a state property or a command argument of
its own, a default `JsonElement` is `null`.

A DTO that only reaches the page needs no constructor for its members: a
class with only read-only or computed properties is fine as state or a result. A DTO the
page sends needs a public constructor whose parameters map to its properties,
or a public parameterless constructor with public setters (`RUNICBRIDGE003`).

## Generator diagnostics

The generator reports every ViewModel's first problem in one build. An error
names the type and member, and points at its source line when the inspected
assembly has a portable PDB (the default). A member generated by a source
generator, such as a CommunityToolkit `[ObservableProperty]`, points at its
`[RelayCommand]` method or its declaring class. Each error and warning ends with
a link to its entry in the [diagnostics catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#bridge-code-generator)
for the release you use.

`RUNICBRIDGE013`, `015`, `019`, `020`, `028`, `029`, `030`, `031`, `032` and `033` are warnings; the generator's
(`013`, `020`, `028`, `031` and `032`) repeat on builds that reuse the cached generation. Every other
code fails the build. The
[diagnostics catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#bridge-code-generator)
lists every code with its severity, what reports it, its cause and its fix.

The build targets run the generator and the frontend install and build
commands without wrapping their failures in MSB3073. Errors and warnings that a
command prints in the canonical `file(line,column): error CODE: text` form, such
as the generator's, `tsc`'s and `vue-tsc`'s, are logged once with their code and location.
When `svelte-check` fails, its errors and warnings are logged at their file, line and
column too, without a code, from its default output or `--output machine`; warnings
of a check that passes stay in the command output. A
failed command that reports no such error gets one summary error instead:
`RUNICBRIDGE024` (an internal generator error) for the generator,
`RUNICBRIDGE016` for the frontend commands.

## Ship the frontend inside the executable

By default the build copies the frontend to `www/` next to the application.
With `RunicApplicationFrontendDelivery` set to `Embedded`, it packs the same
files into the application assembly as a Runic Assets archive instead, so the
build and publish output have no `www/`, and a single-file or NativeAOT
executable carries its frontend:

```xml
<PropertyGroup>
  <RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery>
</PropertyGroup>
```

The application needs no code for it. The build composes the tree in
`obj/<configuration>/<framework>/runic-www/`, packs it as `RunicAssetsDist`
(the Runic.Assets defaults apply: `.git` and `.env*` are left out, and source
maps warn with `RAS1005`), and names the resource in the
`Runic.Application.FrontendArchive` runtime option. `RunicDesktopHost` and the
WPF `RunicViewHost` then serve the archive in place of the web root, with each
Window's `StartPage` at its root. A Window's `ContentRoot`, the host's
`SurfaceOptions` and `dotnet runic dev`, which builds with `Files` and serves
the live frontend, still take precedence. `RunicDesktopHostOptions.Assets`
serves another source, such as an archive read at run time.

`Embedded` needs the Runic Desktop host (`Runic.Application.Views.Desktop`,
which brings Runic.Assets); the CS-WebUI host serves a folder and builds as
`Files` with `RUNICBRIDGE030`.

## Build properties

The build targets ship in this package and apply to every project that
references a host adapter. A project needs none of these properties unless it
departs from the conventional `Frontend` folder. Every property except
`RunicApplicationHost` starts with `RunicApplicationFrontend`, as do the
development-server properties of `dotnet runic dev`. The `RunicBridge*` and `RunicViewsWindowProject` names of
0.7.0-preview.6 and earlier no longer apply and fail the build with
`RUNICBRIDGE017`; the [rename table](#moved-and-renamed-since-070-preview6)
maps them. Underscore-prefixed properties, such as `_RunicApplicationHostAdapter`,
which the host adapters set, are not settings.

`RunicViewsGenerateBridge`, `RunicViewsBuildFrontend` and `RunicViewsCopyFrontend`
are extension points: a project may run its own targets before or after them
(`BeforeTargets`/`AfterTargets`), and they keep their names and order. Other
`RunicViews*` target names are implementation details and may change in any
release. `RunicViewsGetFrontendFiles`, which an application calls on the
projects it references, is a protocol between Runic packages of one release, not
an extension point.

| Property | Default | Purpose |
| --- | --- | --- |
| `RunicApplicationFrontendDirectory` | `$(MSBuildProjectDirectory)/Frontend` | Frontend package directory. |
| `RunicApplicationFrontendGeneratedDirectory` | `<frontend>/src/generated` | Generated TypeScript clients. |
| `RunicApplicationFrontendPackageManager` | `packageManager` in `package.json`, then `pnpm-lock.yaml`, `bun.lock`, else `npm` | `npm`, `pnpm` or `bun`: selects the default build and install commands. Another value, such as `yarn`, uses the npm commands and warns with [`RUNICBRIDGE029`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge029) unless the project sets both commands. |
| `RunicApplicationFrontendBuildCommand` | `npm run build`, `pnpm run build`, or `bun run --bun build` | Production frontend build. |
| `RunicApplicationFrontendInstallCommand` | `npm ci`, `pnpm install --frozen-lockfile`, or `bun install --frozen-lockfile`, each with `--ignore-scripts` | Runs before the frontend build when `package.json` or a lock file changed since the last install (stamp: `obj/runic-bridge-frontend-install.stamp`), or when declared dependencies have no `node_modules`. A `package.json` without dependencies needs no `node_modules`. |
| `RunicApplicationFrontendAllowUnlockedInstall` | `false` | The default install is locked: when `package.json` declares dependencies and the package manager's lock file (`package-lock.json` or `npm-shrinkwrap.json`, `pnpm-lock.yaml`, `bun.lock` or `bun.lockb`) is missing, the build fails with [`RUNICBRIDGE026`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge026) before installing. Set `true` to run `npm install`, `pnpm install` or `bun install` (with `--ignore-scripts`) without one instead. |
| `RunicApplicationFrontendInstallEnabled` | `true` | Set `false` when a workspace install owns the frontend packages. |
| `RunicApplicationFrontendNodeModulesDirectory` | none | A `node_modules` installed by something else, such as the [Nix builder](https://github.com/Runic-Artifex/runic-sdk/blob/main/nix/README.md) from the lock file. The install does not run, and the frontend's `node_modules` becomes a symbolic link (a junction on Windows) to it; an existing `node_modules` that is not a link is `RUNICBRIDGE021`. |
| `RunicApplicationFrontendBuildEnabled` | `true` | Set `false` when another tool owns the frontend build. TypeScript is still generated. |
| `RunicApplicationFrontendCopyEnabled` | `true` | Mirrors the frontend's output directory to the web root in the build output: a file that an earlier copy wrote and the output directory no longer has is deleted. Publish always mirrors it. |
| `RunicApplicationFrontendOutputDirectory` | `dist` | The frontend build's output directory, relative to the frontend directory, which build and publish copy and `dotnet runic dev` serves from. |
| `RunicApplicationFrontendWebRoot` | `www` | The directory below the build and publish output that receives the frontend and the hosts serve. Another name reaches the hosts as the `Runic.Application.FrontendWebRoot` runtime option; `RunicWindowOptions.ContentRoot` and WPF's `RunicViewHost.ContentRoot` still win. |
| `RunicApplicationFrontendDelivery` | `Files` | `Files` copies the frontend to the web root. `Embedded` packs the same tree (referenced projects' frontends, this project's output and the host's `runic-views.js`) into the assembly with Runic.Assets, and the Runic Desktop and WPF hosts serve it; see [Ship the frontend inside the executable](#ship-the-frontend-inside-the-executable). Closed: another value, the CS-WebUI host, or a project that sets `RunicAssetsDist` itself warns with [`RUNICBRIDGE030`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge030) and builds as `Files`. |
| `RunicApplicationHost` | the only host adapter referenced | `desktop` or `cswebui`: whose Views client the build copies to `runic-views.js` in the web root. Set it when a project references both hosts. The frontend build receives it as `RUNIC_APPLICATION_HOST`. |
| `RunicApplicationFrontendCompositionType` | `<RootNamespace>.RunicViewsComposition` (host adapters), with the namespace made a valid C# name | Generated composition class. |
| `RunicApplicationFrontendModelAssembly` | the project itself | Inspect a separately built ViewModel assembly instead of a bootstrap build. |
| `RunicApplicationFrontendGenerationEnabled` | see below | `true` requires Bridge generation, `false` turns it off. |
| `RunicApplicationFrontendDocumentationEnabled` | `true` | `false` skips the XML documentation file that the bootstrap pass writes for comments in the generated TypeScript. |
| `RunicApplicationFrontendWindowProject` | `false` | `true` marks a Window project that `dotnet runic dev` and `doctor` run. |
| `RunicApplicationFrontendBootstrap` | set by the targets (read-only) | `true` only in the bootstrap pass (see below). A project reads it, for example to leave a reference out of that pass; it does not set it. |

The ReactiveUI flavor of the generated code is not a setting. The build infers it
from the Runic ReactiveUI adapter the project compiles against, by package or
project reference, directly or transitively: System.Reactive with
`Runic.Application.Views.ReactiveUI.Reactive`, otherwise ReactiveUI.Primitives. A
project that still sets `RunicApplicationFrontendReactiveUiFlavor`, removed in
0.10, fails with `RUNICBRIDGE017`. A ReactiveUI command or interaction without either adapter
is `RUNICBRIDGE025`, and adapters of both flavors, including
`Runic.Navigation.ReactiveUI*`, are `RUNICBRIDGE023`.

These are not settings and not part of the compatibility promise: the generator's
command line (`--generate`, `--composition-stub`, `--reactiveui-flavor`,
`--path-map`, `--framework` and the others), the names of the generated files
below `obj/<configuration>/<framework>/runic-bridge/`, and the stamp, state and
cache files of the build (`runic-bridge-frontend*.stamp`, `.state`, `.outputs`,
`RunicBridge.NoViews.marker`). Only the properties, items and targets this
section names are a contract.

`dotnet build` and `dotnet publish` copy the built frontend as loose files to
`www/` (the web root) next to the executable; they do not embed it. `Runic.Assets` is the
separate pipeline that embeds a frontend in the assembly, for ASP.NET Core and
non-Views Desktop applications, with its own `RunicAssetsDist` property; see
[Runic.Assets or the Views `www/` folder](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Assets/README.md#runicassets-or-the-views-www-folder)
for serving an embedded archive from a Views application. On macOS, an application
in an `.app` bundle without `www/` next to its executable reads the frontend
from the bundle's `Contents/Resources/www`, where Apple expects resources
(`RunicWindowOptions.ContentRoot` overrides both). `dotnet runic dev` sets
`RunicApplicationFrontendBuildEnabled=false` and
`RunicApplicationFrontendCopyEnabled=false` while its development server serves
the frontend. The
[`dotnet runic` README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)
lists the development-server properties it reads.

Two items extend the frontend build. `RunicApplicationFrontendInput` adds an
input outside the frontend directory, such as a monorepo's shared components
that the frontend imports through a Vite alias; a change rebuilds the frontend:

```xml
<ItemGroup>
  <RunicApplicationFrontendInput Include="../ui/lib/**/*;../src/locales/**/*" />
</ItemGroup>
```

`RunicApplicationFrontendBuildEnvironment` names an environment variable that
changes the built frontend, such as a `VITE_*` variable set in the shell, in
addition to `NODE_ENV`, which is always tracked. `.env` files in the frontend
directory are inputs already (see [Incremental generation](#incremental-generation)):

```xml
<ItemGroup>
  <RunicApplicationFrontendBuildEnvironment Include="APP_FLAVOR" />
</ItemGroup>
```

Bridge generation turns on by default for a project that references
`Runic.Application.Views` directly, references a host adapter
(`Runic.Application.Views.Desktop`, `Runic.Application.Views.CsWebUi` or
`Runic.Application.Views.Wpf`) directly or transitively, or builds in this repository.
Each adapter's build props set `_RunicApplicationHostAdapter` to `true`, which is
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

### The host of a page

A page loads `webui.js`, which the host serves, and `runic-views.js`, the host's
Views client, which the host package copies into `www/`:
`Runic.Application.Views.Desktop` (and so `Runic.Application.Views.Wpf`) copies
`runic-desktop-views.js`, `Runic.Application.Views.CsWebUi` copies
`runic-cswebui.js`. Both are also copied under those names, so a page that loads
one of them directly keeps working. Neither `index.html` nor the Vite
configuration names a host, so the same `dist/` runs on every host, and
switching hosts changes the host package and `Program.cs`, not the frontend.
`runic({ desktop: true })` from `@runic-artifex/vite-plugin-runic` is not needed
for Views: it adds the Runic Desktop bootstrap script, which CS-WebUI does not
serve.

A project that references both hosts, for example to ship on CS-WebUI and
compare on Runic Desktop, chooses the client with `RunicApplicationHost`:

```sh
dotnet run -p:RunicApplicationHost=desktop
```

Without it, such a project copies no `www/runic-views.js`, and gets warning
`RUNICBRIDGE019` when a copied page loads `runic-views.js`. A page that loads
`runic-cswebui.js` or `runic-desktop-views.js` itself builds without the
warning. The frontend build receives the host as the `RUNIC_APPLICATION_HOST`
environment variable and is part of its build state, so a Vite configuration
that still needs a host-specific build can read it and is rebuilt when it
changes. A frontend that a referenced project builds, such as the model project
of a WPF application, sees that project's host, which is empty when it
references no host package; such a frontend cannot depend on the application's
host.

On every host the Bridge works under a Content-Security-Policy that allows
only same-origin scripts and connections and no `'unsafe-eval'`, such as
`default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'`.
The host's scripts are served from the page's origin, and Bridge requests,
replies and pushes are data: JSON text that the client parses with
`JSON.parse`. Runic Desktop sends the policy as a header
(`RunicDesktopHostOptions.ContentSecurityPolicy`); a CS-WebUI page carries it
in a `<meta>` element. APIs that run your own script in the page, such as
`DesktopSurface.ExecuteJavaScriptAsync`, still need `'unsafe-eval'`. See the
[security guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/security.md#framing-and-content-security-policy).

## Adding a Window to an existing web app

A Window's frontend can reuse the components, styles and translations of a web
app in the same repository, without copying them. The frontend stays a package
of its own; Vite, TypeScript, Tailwind and the .NET build each need to be told
about the files outside it. This layout puts a Svelte and Tailwind app in `ui/`
and the Runic project next to it:

```text
my-app/
├── ui/                  the existing web app
│   ├── lib/             components, formatters and t() ($lib)
│   ├── locales/         message tables
│   └── app.css          the Tailwind theme
└── runic/
    ├── MyApp.Runic.csproj
    └── Frontend/        the Window's frontend package
        ├── package.json
        ├── tsconfig.json
        ├── vite.config.ts
        └── src/app.css
```

`resolve.alias` points the app's import paths at its files. An alias can also
replace a module that the page does not want, such as a global store that
owns the app's HTTP session, with a small stand-in; list it before the
general alias. `resolve.dedupe` resolves the shared files' bare imports from
this package. Without it, a file in `ui/lib` resolves `svelte` from `ui/`,
which gives the page a second Svelte runtime, or fails when `ui/` has no
`node_modules`. List every package that both the shared files and the page
import, and add those packages to this frontend's `package.json`:

```ts
// runic/Frontend/vite.config.ts
import { resolve } from "node:path";
import tailwindcss from "@tailwindcss/vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { runic } from "@runic-artifex/vite-plugin-runic";
import { defineConfig } from "vite";

const ui = resolve(import.meta.dirname, "../../ui");

export default defineConfig({
  base: "./",
  plugins: [tailwindcss(), svelte(), runic()],
  resolve: {
    alias: [
      // The app's global store is not used here; its readers get a stand-in.
      { find: /^\$lib\/app\.svelte$/, replacement: resolve(import.meta.dirname, "src/app-shim.svelte.ts") },
      { find: /^\$lib/, replacement: resolve(ui, "lib") },
    ],
    dedupe: ["svelte", "clsx", "tailwind-merge", "tailwind-variants", "@lucide/svelte"],
  },
  build: {
    rolldownOptions: { input: ["index.html"] },
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
  },
});
```

TypeScript needs the same paths, in the same order:

```json
{
  "compilerOptions": {
    "baseUrl": ".",
    "paths": {
      "$lib/app.svelte": ["./src/app-shim.svelte.ts"],
      "$lib": ["../../ui/lib"],
      "$lib/*": ["../../ui/lib/*"]
    }
  }
}
```

Tailwind generates only the classes it finds in the files it scans, and it
does not scan outside this package, so the classes that only the shared
components use would be missing. Import the app's theme and name the shared
files with `@source`:

```css
/* runic/Frontend/src/app.css */
@import "../../../ui/app.css";
@source "../../../ui/lib";
```

The .NET build rebuilds the frontend when a file under its frontend directory
changes. Add the shared files as `RunicApplicationFrontendInput` items, so a
change there rebuilds the page too, and name any environment variable that the
shared code or the Vite configuration reads as a
`RunicApplicationFrontendBuildEnvironment` item; see
[Build properties](#build-properties):

```xml
<ItemGroup>
  <RunicApplicationFrontendInput Include="../ui/lib/**/*;../ui/app.css;../ui/locales/**/*" />
  <RunicApplicationFrontendBuildEnvironment Include="VITE_DEFAULT_LOCALE" />
</ItemGroup>
```

- **Translations.** The page can import the app's `t()` and its message
  tables through the alias, and take the locale from `navigator.language`,
  which usually follows the system language (`LANG` on Linux). Include the
  message tables in `RunicApplicationFrontendInput`, so an edited translation
  rebuilds the page.
- **Hosts.** The page loads `runic-views.js`, so it needs no host-specific
  script or Vite configuration; see [The host of a page](#the-host-of-a-page).
- **Type checking.** `svelte-check` or `tsc` in the Runic frontend also checks
  the shared files it imports, without the app's own dependencies and type
  declarations, and can report errors in them that the app's own check does
  not. Check the shared files in the app that owns them.
- **Lock files.** The frontend has its own `package.json` and lock file unless
  it joins the app's package manager workspace. In a workspace, set
  `RunicApplicationFrontendInstallEnabled` to `false`, because the workspace
  install owns `node_modules`.
- **Lists.** Tailwind removes list markers from every list, and Runic Desktop's
  WebKitGTK then no longer reports such a list as a list to a screen reader;
  see [Runic.Desktop accessibility](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Desktop#accessibility).
- **Screen readers on Linux.** On Runic Desktop's WebKitGTK, live regions and
  changed accessible names are silent under Orca, and a list item whose only
  content is a button can crash the page; see the same section for what to use
  instead.

## Typed commands and checked writes

CommunityToolkit `IRelayCommand<T>` and `IAsyncRelayCommand<T>` use the same
supported input shapes and generated codecs as ReactiveUI commands: scalars,
nullable values, DTOs, collections, unions, and explicit custom codecs. Generated
`canX(input)` queries use the actual parameter. Async Toolkit commands expose
`startX`, completion, recovery, and cancellation. A `[RelayCommand]` method that
returns `Task<T>` delivers its `T` as the operation's result; one that returns
`Task` has none.
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

A command without an argument publishes its availability as `canX` state. When
`[RelayCommand(CanExecute = nameof(CanCreate))]` names a `bool` property whose
wire name is that state, such as `CanCreate` for `CreateCommand`, the property
is that availability and is written once, from the command's `CanExecute`.
A `PropertyChanged` for the property publishes it, so it needs no
`[RunicIgnore]`, and the page needs no `NotifyCanExecuteChangedFor`. A property with another name, or one for a
command with an argument, whose availability is the `canX(input)` query, stays
state of its own.

Checked-write receipts decode their values just like state: for example, an
`Int64` receipt's `snapshot.value` or conflict's `incoming.value` is a `bigint`.
An identical request ID and payload replays its receipt. Reusing an ID with a
different baseline version, baseline value, or new value produces a conflict.
Each field retains at most 64 receipts within a default 256 KiB encoded-value
and receipt-metadata budget. Evicted or oversized receipts leave bounded
request-ID tombstones; reconcile authoritative state before issuing a new ID.
The write may have succeeded even when its receipt could not be retained.

## Commands whose parameter has no wire form

A command whose parameter is a .NET object that cannot cross the Bridge, such as
`ReactiveCommand<Item, RxVoid>` bound directly by WPF, is presented as is with
`[RunicCommandKey]` (since 0.11). It names a ViewModel method that resolves a key
the page sends to the parameter; `null` means "no such object":

```csharp
public ItemsViewModel()
{
    DeleteItemCommand = ReactiveCommand.CreateFromTask<Item>(DeleteAsync, canDelete);
}

[RunicCommandKey(nameof(FindItem))]
public ReactiveCommand<Item, RxVoid> DeleteItemCommand { get; }

public Item? FindItem(ulong id) => _items.Lookup(id).ValueOrDefault();
```

The generated client takes the resolver's parameter, with its name and wire
type: `deleteItem(id: bigint)`, `canDeleteItem(id: bigint)` and, for an
asynchronous command, `startDeleteItem(id: bigint, options?)`. .NET decodes the
key, resolves it, evaluates the command's `CanExecute` with the resolved object
and runs the command with it, inside the generated call: the model turn,
failure replies, cancellation and logging are those of any generated command.
No wrapper command is needed.

- **Unresolved key.** When the resolver returns `null`, the call is `rejected`
  with reason `unresolved-key` (`BridgeError.reason` in the page,
  `RunicCallReply.ErrorReason` in tests) and the command does not run;
  `canDeleteItem(id)` is `false`. Nothing is logged.
- **Resolver shapes.** One method of the ViewModel or a base class, not
  overloaded, instance or static, `public` or `internal`:
  `P? Find(TKey key)`, `ValueTask<P?> FindAsync(TKey key)` or
  `ValueTask<P?> FindAsync(TKey key, CancellationToken cancellationToken)`
  (`Task<P?>` works too). `TKey` is any command input type; `P` is the command's
  parameter type (for a plain `ICommand`, whatever the resolver returns).
- **Turns.** A synchronous resolver runs in the call's model turn. An
  asynchronous one starts in that turn; `CanExecute` and the command run in a
  new turn after it completes. The resolution is window work like an awaited
  command: closing the window waits for it and then cancels its token, which is
  also cancelled when the page cancels the call.
- **No side effects.** A resolver can run more than once for one request: for
  `canX(key)` and then `x(key)`, or when a page repeats an operation start. A
  repeated start with the same `requestId` and key observes the admitted
  operation whatever the key resolves to now; the operation's identity is the
  key, not the object.
- **Exceptions.** A resolver that throws `ArgumentException`, `FormatException`
  or `JsonException` rejects the call with `invalid-input`; any other exception
  fails it, as a command's would.
- **Libraries.** ReactiveUI commands of both flavors, CommunityToolkit
  `IRelayCommand<T>` and `IAsyncRelayCommand<T>` (put the attribute on the
  property or on the `[RelayCommand]` method) and a plain `ICommand`, which takes
  `[RunicCommandKey]` instead of `[RunicCommandInput]`. A command without a
  parameter cannot have a key.

The generator reports a misplaced attribute or an unusable resolver as
[`RUNICBRIDGE027`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge027).

## Operation results and parameterised queries

What a call gives the page back depends on the command:

| Generated member | Resolves to |
| --- | --- |
| `estimate(choice)` | The ViewModel's state after the command finished (`Promise<CleanupState>`), or a `BridgeOutcome` of that state and a [declared failure](#declared-failures). |
| `canEstimate(choice)` | Whether the command can run with that argument (`Promise<boolean>`). Generated for a command with a parameter, instead of a `canEstimate` state property. |
| `startEstimate(choice)` | A `BridgeOperation<TResult>` handle for status, Cancel and `outcome()`. |

A command's result reaches the page through its operation. A public
`ReactiveCommand<TInput, TResult>` generates `BridgeOperation<TResult>`, and
`outcome()` resolves to `{ ok: true, value }`; see
[Generated ReactiveUI contracts](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/dotnet/Runic.Application.Views.ReactiveUI#generated-reactiveui-contracts).
A CommunityToolkit `[RelayCommand]` method that returns `Task<T>` generates
`BridgeOperation<T>` the same way, with `T`'s nullability:

```csharp
[RelayCommand(IncludeCancelCommand = true)]
private async Task<CleanupEstimate> MeasureAsync(CleanupChoice choice, CancellationToken token) =>
    new(choice.Retention, await estimator.FreedBytesAsync(choice.Retention, token), DateTimeOffset.Now);
```

```ts
const outcome = await cleanup.startMeasure({ retention: 3 }).outcome();
if (outcome.ok) render(outcome.value.bytes);
```

A method that returns `Task`, a synchronous command and a plain `ICommand` have
no result: their operation is a `BridgeOperation<void>`. The awaited `measure()`
call resolves to the state after the command either way.

A command started as an operation can report how far it got with
`RunicOperationProgress.Report(fraction)`, a number from 0 to 1. The call is
ambient: it reaches the operation whose command runs on the current
asynchronous flow, so the command keeps its signature, and it does nothing
outside an operation (an awaited `measure()` call, a unit test). Pages that
watch the operation receive the latest value as `progress` on its running
status, at most every 100 ms; values outside 0 to 1 are clamped.

```csharp
[RelayCommand(IncludeCancelCommand = true)]
private async Task ImportAsync(ImportSource source, CancellationToken token)
{
    for (var index = 0; index < source.Files.Count; index++)
    {
        await importer.ImportAsync(source.Files[index], token);
        RunicOperationProgress.Report((index + 1) / (double)source.Files.Count);
    }
}
```

A result that other parts of the page show, or that must survive a reload, is
better published as state. Publish it in a read-only property, together with the input it
answers. The page can then tell whether the result still matches its current
choice. A second property tells where the work is, so the page can show
progress, cancellation and failure. This is a simplified version of a cleanup
estimate that measures in .NET what keeping the newest restore points would
free:

```csharp
/// <summary>What to measure.</summary>
public sealed record CleanupChoice(int Retention);

/// <summary>What a cleanup keeping the newest <c>Retention</c> restore points frees.</summary>
public sealed record CleanupEstimate(int Retention, double Bytes, DateTimeOffset MeasuredAt);

public enum EstimatePhase { Idle, Measuring, Measured, Cancelled, Failed }

public sealed partial class CleanupViewModel(ICleanupEstimator estimator) : ObservableObject
{
    /// <summary>The last measurement, with the choice it answers.</summary>
    [ObservableProperty]
    public partial CleanupEstimate? Estimate { get; private set; }

    [ObservableProperty]
    public partial EstimatePhase Phase { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Measures what a cleanup keeping <paramref name="choice"/> frees.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task EstimateAsync(CleanupChoice choice, CancellationToken token)
    {
        Phase = EstimatePhase.Measuring;
        Error = null;
        try
        {
            var bytes = await estimator.FreedBytesAsync(choice.Retention, token);
            // The result is state, published with the input it answers.
            Estimate = new CleanupEstimate(choice.Retention, bytes, DateTimeOffset.Now);
            Phase = EstimatePhase.Measured;
        }
        catch (OperationCanceledException)
        {
            Phase = EstimatePhase.Cancelled;
            throw;
        }
        catch (Exception e)
        {
            Phase = EstimatePhase.Failed;
            Error = e.Message;
            throw;
        }
    }
}
```

The page starts each measurement with `startEstimate`, through
`createLatestOperationController` from `@runic-artifex/views`, so a newer choice
cancels the measurement it supersedes. It shows an estimate only when the
estimate answers the current choice:

```ts
import { createLatestOperationController } from "@runic-artifex/views";
import { connectCleanup } from "./generated/cleanup.js";

const cleanup = await connectCleanup();
const measuring = createLatestOperationController();
let retention = 3;

function choose(keep: number) {
  retention = keep;
  return measuring.run({ start: () => cleanup.startEstimate({ retention: keep }) });
}

cleanup.subscribe(state => {
  // An estimate answers the choice it was measured for, so ignore an older one.
  const estimate = state.estimate?.retention === retention ? state.estimate : undefined;
  if (state.phase === "Measuring") render("Measuring…");
  else if (state.phase === "Failed") render(`Measuring failed: ${state.error}`);
  else if (estimate) render(`Frees about ${formatBytes(estimate.bytes)}.`);
});
```

In a component, the adapter's `useLatestOperation()` (React, Vue, Svelte) or
`injectLatestOperation()` (Angular) wraps the same controller and disposes it with
the component.

A question that takes parameters works the same way. There is no query route
that returns a computed value: run a command with the parameters, and read the
answer from the state it publishes. `await cleanup.estimate({ retention: 5 })`
resolves with the state after the command, so `(await cleanup.estimate(...)).estimate`
is the answer for a caller that awaits it. Only availability is a query:
`canEstimate(choice)` asks the command's `CanExecute` with that argument, and
nothing pushes a change of its answer. An answer that the page can compute
from state it already has, such as how many restore points a retention
removes, needs no round trip. For progress, Cancel and stale-selection
handling, see the
[operations guide](https://docs.runic-artifex.eu/guides/application/guides/operations-and-cancellation/)
and [`@runic-artifex/views`](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#operation-feedback-and-latest-selection).

`Bytes` is a `double` because a `long` would be a `bigint` in TypeScript; see
[Wire types](#wire-types).

## Large lists and frequent changes

A route publishes each change of its state as a frame. What a frame costs
depends on the member:

- A `[RunicCollection(nameof(Row.Id))]` list publishes only the rows that were
  added, removed, moved or replaced, and a row's own property change replaces
  that row. Use it for any list that can grow beyond a few dozen rows or
  changes often.
- Every other member is compared: a publication unit encodes it and sends it
  whole when the encoding differs from what the page has. The generator reports
  an observable collection of DTO rows that is not a `[RunicCollection]` as
  `RUNICBRIDGE032`.

A notification requests a publication unit, and a route starts its units at
least 8 ms apart: the first change after a pause is published at once, and the
changes of the next 8 ms follow together in one frame once the interval has
passed, so the last change is always published. A model that raises thousands
of notifications a second, such as progress or a live counter, therefore sends
at most about 125 frames a second per route. A setter, command or checked write
is not delayed: it publishes its change, with any change that waits for the
interval, before it replies. Wrap a bulk change in a `BridgeSnapshotBatch`
(below) to publish it as one frame when the batch ends.

## Batch synchronous model updates

```csharp
using (BridgeSnapshotBatch.Begin(document))
{
    document.Title = imported.Title;
    document.Content = imported.Content;
    document.Language = imported.Language;
}
```

Nested scopes on the same model publish one frame per attached bridge when
the outer scope ends: the batch is one publication unit. A batch controls
when changes are published; it does not make mutations atomic
or own the model scheduler. Enter the model's execution context as usual and
keep the batch around synchronous changes, after awaiting I/O. The translations
editor uses this during bulk document reconciliation and command result updates.

## Long text members

Every `string` state property without `[RunicCodec]` is a text member. When the page's client and the host both
implement the `text-splice/1` feature, a setter of a long text member sends only the characters that changed, and the
Bridge publishes a change of a long text member as one splice (the characters removed at a position and the text
inserted there), or, for a value that dropped its start and grew at its end, as two. The generated Bridge rebuilds the
page's value from the splice and calls the property's setter with the whole string, so setters, validation and
normalization see what they always saw. A keystroke in a 100,000-character property costs about 130 bytes up, 160 bytes
down and 200 KB of allocation (the new string) instead of 100 KB each way and 300 KB
(`tests/benchmarks`, `TextSpliceBenchmarks`). Short values (a splice that keeps fewer than 64 characters), changes from
or to `null`, and windows with a page that does not list the feature use whole values. Nothing needs to be declared.

## Live logs and other growing text

A read-only `string` that grows at its end, such as the output of a running
build, would otherwise cross whole in every state, up to its full length per
line of output. Mark it `[RunicAppendOnlyText]`, and a change of the property
alone crosses as the text it appends:

```csharp
public sealed partial class OperationsViewModel : ObservableObject
{
    private const int LogCharacters = 128_000;

    /// <summary>The end of the running operation's log.</summary>
    [ObservableProperty, RunicAppendOnlyText]
    public partial string Log { get; private set; } = "";

    private void OnOutput(string text)
    {
        var log = Log + text;
        // Keeps the last 128 KB; the page receives the dropped length and the new text.
        Log = log.Length > LogCharacters ? log[^LogCharacters..] : log;
    }
}
```

The page sees an ordinary `log: string`. The Bridge compares the new value with
the value the page has and publishes a frame with the number of UTF-16 code
units dropped from its start and the text appended, keeping the longest text
the two share. Appends in one `BridgeSnapshotBatch` are one change. A cleared
or replaced log is a frame that drops everything. A change from or to `null`
(for `string?`) is a `set` of the whole value, and other members that changed
travel as `set` changes in the same frame. A page that falls behind recovers
with a full state, as with `[RunicCollection]`. The property must be a
read-only `string` or `string?` state property (`RUNICBRIDGE008`). The frame
format is in [state changes](../../../specs/application/state-changes.md#text).

## Structured validation

Models and nested DTOs may implement `INotifyDataErrorInfo`. Generated snapshots
then expose `validation.hasErrors`, `validation.truncated`, and `validation.errors`, alongside existing
property error string arrays. Each error contains a `path` of wire property
names, list indexes, or dictionary keys and a `message`. Root entity errors use
an empty path. Aliases are preserved, so a postal-code error might have the path
`["profile", "postal-code"]`.

Return a `RunicValidationMessage` from `GetErrors` to supply a stable `Code`,
`Severity`, or relative CLR `MemberPaths`. A member path names CLR properties
separated by dots and may index lists and dictionaries: `Items[0].Name`,
`Lookup[primary].Name` or `Groups[0][1]`; a path that names no Bridge member
or element reports at the object. Ordinary strings and
`ValidationResult` remain supported. An empty message is reported as
`Invalid value.`, in `validation` and in the `…Errors` arrays. Generated metadata resolves paths without
runtime reflection. Traversal and messages are bounded; `truncated` reports when
validation details are incomplete. Nested `ErrorsChanged` notifications publish new validation
state, and removed objects lose their subscriptions. This projection works
across model frameworks; it does not run validation rules itself.

Each object is read with `GetErrors(null)` for its own errors and
`GetErrors(member)` for each Bridge member. Many implementations, including
CommunityToolkit's `ObservableValidator`, also return every property's errors
for `null`. A `ValidationResult` from `GetErrors(null)` whose `MemberNames` all
name Bridge members of the same object is therefore left to `GetErrors(member)`
and reported once, at the member's path. Other member names still resolve
relative to the object, and a result without member names reports at the
object's path. Strings and `RunicValidationMessage`s from `GetErrors(null)`
are reported as before.

A ViewModel that implements ReactiveUI.Validation's `IValidatableViewModel`
(`RunicArtifex.ReactiveUI.Validation` or its `.Reactive` flavor), such as a
`ReactiveValidationObject`, is read through its `ValidationContext` instead of
`GetErrors(null)`: a validation with properties reports at each property's
path, and every other validation, such as a rule created with
`ValidationRule(observable, message)`, at the object's path. Each message
appears once. The generated Bridge emits this reader, so no further package is
needed. A Bridge member's messages still come from `GetErrors(member)` and keep
the ViewModel's formatter, as in the per-property `…Errors` arrays; the other
messages are the validation text on one line, separated by spaces. `ReactiveValidationObject.GetErrors(null)`
itself is unchanged.

A ViewModel or DTO that implements `IValidatableViewModel` without
`INotifyDataErrorInfo`, such as a `ReactiveObject` with its own
`ValidationContext`, is supported the same way: the ViewModel has the
`validation` member, property rules report at their properties' paths and the
other rules at the object's path, and checked writes report the property's
rules. Its messages are the validation text on one line, and it has no
`…Errors` arrays, which come from `INotifyDataErrorInfo`. The
`ValidationContext` property is not part of the contract. A type that
implements another interface named `IValidatableViewModel` is reported as
warning `RUNICBRIDGE031`.

The Bridge also observes each `ValidationContext`'s `ValidationStatusChange`,
of the ViewModel and of every object in its state graph. A
`ReactiveValidationObject` raises `ErrorsChanged` only for the properties its
rules named before, so a rule that names no property would otherwise change
the page only when `HasErrors` changes. `hasErrors` of an object read through
its `ValidationContext` follows the messages, so a disposed
`ReactiveValidationObject`, whose `HasErrors` keeps its last value, reports no
errors. A nested DTO that derives from `ReactiveValidationObject` keeps
`HasErrors` and `ValidationContext` out of its contract.

A checked write's receipt (`write{Property}`) carries the property's messages
after the write as `applied.validation`, one per line, when the write left the
property invalid.

Use `[RunicIgnore]` on validation-only DTO properties such as a computed
`HasErrors`; their values are represented by the validation projection.

## Shared DTO exclusions

Public readable DTO properties are part of the Bridge contract, including
computed properties. A DTO the page sends needs a public constructor matching its
included properties, or public setters with a public parameterless constructor;
one that only reaches the page needs neither ([Optional members](#optional-members)).
Use `[RunicIgnore]` to leave a property on .NET without exposing it to the Bridge.
System.Text.Json's unconditional ignores apply too: a DTO property with
`[JsonIgnore]`, `[JsonIgnore(Condition = JsonIgnoreCondition.Always)]` or
`[JsonExtensionData]` is not part of the contract, as it is not part of the JSON,
throughout the ViewModel's DTO graphs, including nested DTOs, collections,
command values, interactions and declared failures. Generated codecs, TypeScript,
validation traversal and the Hot Reload fingerprint use the same exclusions.

For example, a separate Core assembly can keep its DTO free of Runic dependencies:

```csharp
using System.Text.Json.Serialization;

public sealed record HistoryQuery(string Message, string Author)
{
    [JsonIgnore]
    public bool IsFiltered => Message.Length != 0 || Author.Length != 0;
}
```

To keep such properties in the contract, opt out from the assembly **declaring
the ViewModel**:

```csharp
using Runic.Application.Views;

[assembly: RunicIncludeJsonIgnored]
```

The Core assembly and the assembly declaring only a Window need no marker.
Each ViewModel uses its own declaring assembly's policy.

An extension-data member keeps the JSON members a DTO's type does not declare,
such as those a newer version of the application saved, for System.Text.Json to
write back. They stay on .NET: a DTO that the Bridge reads from the browser has
no extension data.

```csharp
public sealed record SavedRequest(string Kind)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
```

`Never`, `WhenWritingNull` and `WhenWritingDefault` remain included; Bridge
fields have a fixed contract and are emitted even when null or default.
`[RunicIgnore]` always excludes a property, including one also marked
`[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`. Root ViewModel properties
still use `[RunicIgnore]`; System.Text.Json's ignores apply to DTO properties only.
Adding or removing the opt-out changes the DTO wire contract and requires
rebuilding the .NET application and generated frontend together.

## Failure detail in development

A command, setter, checked write or operation that throws replies with a
bounded message such as `"Save failed."` and is logged (see
[Logging and telemetry](#logging-and-telemetry)). In
development the error also carries `detail: {type, message, stack}`, which the
TypeScript runtime exposes as `BridgeError.detail` and the Vite plugin shows in
DevTools. Development means `DOTNET_ENVIRONMENT` (or else
`ASPNETCORE_ENVIRONMENT`) is `Development`, which `dotnet runic dev` sets.

Each window decides for itself, when it opens: set
`RunicWindowOptions.IncludeFailureDetail` (or
`WindowContentSessionOptions.IncludeFailureDetail` for a session you create, and
`RunicWindowTestHostOptions.IncludeFailureDetail` in tests) to `true` or `false`
to choose explicitly; `null`, the default, follows the environment. Two windows
in one process can differ. A Bridge attached without a window session follows
the environment. Detail can contain file paths and application data, so do not
enable it for a distributed build.

## Bridge input limit

A window decodes each Bridge input up to one limit in UTF-8 bytes: a setter
value, a command argument, a checked write, an operation start, and an
interaction request or reply. A larger one is answered as invalid input
(`rejected` with reason `invalid-input`) and does not reach the ViewModel. The
default is 1 MiB. Raise it for a window that takes larger values, such as a text
editor bound to a `string`, or lower it, with `RunicWindowOptions.MaximumInputBytes`
(per Window), `RunicDesktopHostOptions.MaximumInputBytes` or
`RunicCsWebUiHostOptions.MaximumInputBytes` (the host's default for Windows that
set none), `WindowContentSessionOptions.MaximumInputBytes` for a session you
create, or `RunicWindowTestHostOptions.MaximumInputBytes` in tests. A value must
be between 1 KiB and 64 MiB; another one throws `ArgumentOutOfRangeException`.
The WPF host uses the default. Independently of the limit, exact number strings
(`long`, `ulong`, `BigInteger`, `decimal`) are at most 4 096 characters, and a
window runs at most 64 awaited command calls at once.

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
failure on `ThrownExceptions`; the generated Bridge observes it there and does
not log it, so the ViewModel needs no subscription.

`RunicFailureException<TFailure>` derives from `RunicFailureException` and
keeps the failure's type, so code that catches it, such as a test or a
`ThrownExceptions` subscription, reads `Failure` without a cast. The Bridge
treats both the same way:

```csharp
throw new RunicFailureException<SaveFailure>(new TitleTaken(existing.Title));

catch (RunicFailureException<SaveFailure> error) when (error.Failure is TitleTaken taken) { /* ... */ }
```

## Navigation

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

**Tabs with a stack each (since 0.12).** Expose each tab's region as its own
`NavigationRegion<TContent>` slot, and the
[`NavigationSelection`](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#selecting-among-sibling-regions)
that chooses among them as a get-only property. That property is a state slot:
its wire value is `SelectedIndex`, the index in `Regions`, or `null` when the
selection has no region, and its TypeScript type is `number | null`. The
generated Bridge observes the selection's `SelectedIndex` as it observes a
region's `Current`, so a committed switch publishes the new index and a vetoed
one publishes nothing. A selection with a public setter is `RUNICBRIDGE008`.
The web view selects through a command that calls `SelectAsync` and returns the
request's `NavigationOutcome`, so the frontend learns whether a departure guard
kept the tab:

```csharp
public sealed partial class CustomersPageViewModel : ObservableObject
{
    public CustomersPageViewModel(RunicNavigator navigator)
    {
        List = navigator.CreateRegion<IPage>(this, NavigationTarget.Create<CustomerListViewModel>());
        Reports = navigator.CreateRegion<IPage>(this, NavigationTarget.Create<ReportsViewModel>());
        Tabs = navigator.CreateSelection(this, [List, Reports]);
    }

    public NavigationRegion<IPage> List { get; }
    public NavigationRegion<IPage> Reports { get; }
    public NavigationSelection Tabs { get; } // TypeScript: readonly tabs: number | null

    [RelayCommand] // selectTab(index) in TypeScript
    private async Task<NavigationOutcome> SelectTabAsync(int index) =>
        (await Tabs.SelectAsync(Tabs.Regions[index])).Outcome;
}
```

With ReactiveUI, expose `Tabs.CreateSelectCommand()` from Runic.Navigation.ReactiveUI
as the command. The frontend shows the slot at the selected index. It can also
keep every tab's outlet mounted and hide the others, which keeps each tab's DOM,
such as scroll positions and unsent input:

```tsx
function CustomersPage({ view, state }: { view: CustomersPageClient; state: CustomersPageState }) {
  const tabs = [state.list, state.reports];
  return (
    <>
      <nav>
        <button aria-pressed={state.tabs === 0} onClick={() => void view.selectTab(0)}>Customers</button>
        <button aria-pressed={state.tabs === 1} onClick={() => void view.selectTab(1)}>Reports</button>
      </nav>
      <ViewOutlet content={state.tabs === null ? null : tabs[state.tabs]} registry={pages} />
      {/* or keep every tab mounted:
      {tabs.map((tab, index) => (
        <section key={index} hidden={state.tabs !== index}><ViewOutlet content={tab} registry={pages} /></section>
      ))} */}
    </>
  );
}
```

Don't move the highlighted tab before the command settles: `state.tabs`
changes only when the switch commits, so the tab bar shows the kept tab after
a veto. The tab set is static; Runic Views presents no collection of region
slots.

### Navigation command results

A command that navigates returns the request's `NavigationOutcome`, as
`CreateBackCommand()` and the adapter's entry commands do, because the Bridge
cannot encode a `NavigationResult<TContent>` (`RUNICBRIDGE003`). The generated
client receives it as a typed result:

```ts
interface NavigationOutcome {
  readonly kind: "Committed" | "Rejected" | "Superseded" | "Failed";
  readonly rejection: NavigationRejection | null; // set for "Rejected"
  readonly arrival: "None" | "Completed" | "Failed" | "Cancelled"; // "None" unless "Committed"
}
```

`arrival` reports how the
[arrival hooks](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#after-the-commit-arrival)
(`INavigationArrived`) of the entry the commit made current ended. A
`"Failed"` hook leaves the commit standing, so a page that went back or
navigated can show that the destination's data did not load. Like a `"Failed"`
kind, it carries no exception message, which can contain application data:
the hook's exception is logged as event 1076, and code that needs it awaits the
region's request in its own command and reads `Committed.ArrivalError`.

### Operations and navigation entries

Operations belong to the Window, not to the page that started them: their
`status`, `watch`, `cancel` and `stream` requests are answered by the Window,
so a remounted or another presentation still observes them. By default an operation keeps running
when the navigation entry whose page started it retires (Back, Replace, Reset
or Clear removed the entry), and the navigator disposes the page, its entry
scope and its model-context lease at once. When the operation uses the page or
services of that entry scope, declare what retirement does with
`[RunicOperationRetirement]` (since 0.11), on the command property or on the
ViewModel class for all of its operations:

| `RunicOperationRetirement` | At retirement | The page, its entry scope and its lease are disposed |
| --- | --- | --- |
| `Keep` (default) | The operation keeps running. | At once, as before. |
| `Cancel` | Its cancellation token is cancelled, as by the client's `cancel()`; it usually ends `cancelled`. | After the operation's work ended. |
| `Drain` | The operation keeps running to its end. | After the operation's work ended. |

```csharp
public sealed class UploadViewModel : ReactiveObject
{
    // Uses the entry's scoped IUploadClient: retiring the page cancels it, and the client is disposed afterwards.
    [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
    public ReactiveCommand<RxVoid, RxVoid> UploadCommand { get; }
}

[RunicOperationRetirement(RunicOperationRetirement.Drain)] // every operation of the page
public sealed partial class ExportViewModel : ObservableObject
{
    [RelayCommand]
    private Task ExportAsync(CancellationToken cancellationToken) => ...;

    // A property declaration wins over the class's; [property:] forwards it to the generated command.
    [RelayCommand]
    [property: RunicOperationRetirement(RunicOperationRetirement.Keep)]
    private Task RefreshAsync(CancellationToken cancellationToken) => ...;
}
```

- Retirement never waits for the work: Back commits and completes at once,
  and `RunicNavigator.IsBusy` and `WhenIdleAsync` do not wait for it. Only the
  disposal of that page moves to the end of its `Cancel` and `Drain` operations.
  `RunicNavigator.DisposeAsync` waits for them up to its `CloseTimeout`, then
  logs Navigation event 1077 and returns; the page is still disposed when they
  end.
- The disposal waits for the work, not for the operation's reply. A ReactiveUI
  command's operation reports `cancelled` as soon as its cancellation is
  requested, while a `CreateFromTask` body can still be running; the page and
  its entry scope are disposed after that body ended. A cancelled
  `CreateFromObservable` execution ends with its unsubscription. A
  CommunityToolkit operation ends with its method's task, so its reply already
  waits for the body.
- The policy covers the operations that the page started, and those of the
  ViewModels presented under it (its content properties and collections,
  transitively) when the operation started. A ViewModel presented elsewhere
  later is not followed.
- A retained entry (a push covered it) keeps every operation, whatever the
  policy. Unmounting a presentation, a reload or a disconnect is not
  retirement either.
- It applies to content the navigator owns (`Own`, `Create`, `Share`).
  Borrowed content is never retired by the navigator, and a page that a
  non-navigator `Present`, `Clear` or content property replaces is
  application-owned: own its work with an [`AcceptedWorkScope`](#accepted-application-work).
- Only operations, the asynchronous commands that the client starts with
  `start…`, carry a policy. An awaited call (`upload()`) is window work that
  ends with its caller or the Window. On a synchronous command, a state
  property or a class without operations, the attribute has no effect and the
  generator warns (`RUNICBRIDGE028`).
- A cancelled ReactiveUI command reports the cancellation on
  `ThrownExceptions` when its work ends, which for `Cancel` can be after the
  page's Bridge detached. Since 0.11 the Bridge keeps observing the command
  until that work ended, so the cancellation never reaches ReactiveUI's default
  exception handler, which ends the process, and the ViewModel needs no
  `ThrownExceptions` subscription for it.
- Each operation that retirement cancels or waits for is logged at Debug
  (event 1017). The policy is not part of the contract: the generated
  TypeScript, the contract hash and the mocks do not change.
- A Window whose root ViewModel is a page that a navigator holds, such as a
  WPF `WpfBridgeView` or `RunicViewHost` that a `NavigationHost` presents,
  follows the same policy for that page. Unloading such a child view still
  asks its remaining operations to cancel, as before, so a `Drain` there runs
  only until the view unloads.

A late completion that navigates should not navigate from a page that is no
longer shown. `NavigationEntryContext.BackAsync` and `CompleteAsync` already
reject a retained or retired entry with `NotCurrent`; for a request to another
region, pass the page's entry as `NavigationRequestOptions.Requester`, which
rejects the request unless that entry and the entries owning its region are
current when it is admitted and when it commits:

```csharp
await shell.Main.PushAsync(NavigationTarget.Create<ReportViewModel>(),
    new NavigationRequestOptions { Requester = _entry.Id });
```

### Operations that outlive a window

The registry of operations is per Window: a second Window, or a second launch,
cannot observe an operation the first Window started, and closing the Window
waits for its operations up to the close timeout, then cancels them. Work that
must outlive a Window, or that several Windows observe, belongs to an
application-owned service registered outside the Window's scope, as
nix-manager's `IOperationService`:

- The service runs at most one operation of a kind and keeps its state and an
  ordered event stream (progress, log lines, the result).
- A Window's ViewModel subscribes to that stream when it opens and publishes
  what it shows as state; a new Window, or a reattached one, reads the current
  state first and then follows the stream.
- `WaitAsync` lets a command or a Window await the end of a run; stopping that
  wait does not stop the run.
- Cancellation is the application's decision: a Window asks the service to
  cancel, and the service decides who may. Runic does not keep a cross-window
  operation registry.

## Logging and telemetry

Registering logging is all an application needs: with `services.AddLogging(...)`,
for example `AddLogging(logging => logging.AddSerilog(...))`, or the Generic
Host, Runic logs every runtime message through that `ILoggerFactory`, with the
categories and event IDs below. `RunicDesktopHost.Run` also passes it to the Desktop host when
`DesktopHostOptions.LoggerFactory` is not set, until `Run` returns, and
`RunicViewHost` takes it from its `Services` when it opens. A host message
after that, such as a browser profile that a minute later still cannot be
deleted, or one from a factory that was disposed meanwhile, goes to `Trace`. Without a registered factory, Runic falls back to
`System.Diagnostics.Trace`.

`OpenWindowAsync` passes the scope's
`ILoggerFactory` to the Window's `WindowContentSession` when one is registered,
for example with `services.AddLogging(...)`. A session constructed directly
takes it as `loggerFactory`. Without a factory, each entry is written to
`System.Diagnostics.Trace` (`TraceError`, or `TraceWarning` for warnings) as
its formatted message followed by the exception; a failed View mount goes to
standard error instead.

Every entry carries the exception, in every environment: logs belong to the
operator, whereas `IncludeFailureDetail` governs only what a Bridge reply shows
to web content. Exception messages can contain application data, so treat log
output accordingly. The message properties are listed per event.

Runic does not log an exception that ends the process, such as one thrown by
the application body that `RunicDesktopHost.Run` rethrows. The
[crash reporting guide](https://docs.runic-artifex.eu/guides/desktop/crash-reporting/)
shows how to log it from `AppDomain.UnhandledException`, capture a dump, keep
NativeAOT symbols and what to attach to a bug report.

| Event ID | Name | Level | Logged when | Properties |
| --- | --- | --- | --- | --- |
| 1000 | `BridgeCommandFailed` | Error | A command throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1001 | `BridgeSetterFailed` | Error | A property setter throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1002 | `BridgeFieldWriteFailed` | Error | A checked field write throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1003 | `BridgeOperationAdmissionFailed` | Error | Admitting an operation throws. | `Model`, `Member`, `Route`, `ErrorType` |
| 1004 | `BridgeOperationFailed` | Error | An admitted operation throws. | `Member`, `ErrorType` |
| 1005 | `BridgeOperationCancellationCallbackFailed` | Warning | A cancellation callback throws while a Window closes, or while a retiring navigation entry cancels its operations. | `ErrorType` |
| 1006 | `BridgeCommandDomainFailed` | Debug | A command throws its declared failure. | `Model`, `Member`, `Route`, `FailureType` |
| 1007 | `BridgeOperationDomainFailed` | Debug | An admitted operation throws its declared failure. | `Member`, `FailureType` |
| 1008 | `BridgeDomainFailureNotEncoded` | Error | A `RunicFailureException` from a command or operation is undeclared, cannot be encoded or is over 4 KiB, so it is reported as failed. When the encoder threw, the entry carries both exceptions. | `Model`, `Member`, `Route`, `FailureType`, `Reason` |
| 1010 | `BridgeSnapshotCaptureFailed` | Error | A state snapshot writer throws. | `Model`, `Route`, `ErrorType` |
| 1011 | `BridgeSnapshotDeliveryFailed` | Error | A transport rejects a `state`, `frame` or `failure` push. | `Model`, `Route`, `ErrorType` |
| 1012 | `BridgeCollectionKeysRejected` | Error | A `[RunicCollection]` has a null row or a null, empty or duplicate key, so the route withholds its state. | `Model`, `Field`, `Key`, `Route` |
| 1013 | `AcceptedWorkFailed` | Error | A task owned by an `AcceptedWorkScope` faults. | `ErrorType` |
| 1014 | `AcceptedWorkDomainFailed` | Debug | A task owned by an `AcceptedWorkScope` throws a declared failure. | `FailureType` |
| 1015 | `PathWatchProblem` | Warning | A `RunicPathWatcher` started without a `failed` callback meets a problem it works around: lost events, a watch limit or a watch that could not start. Always written to `Trace`. | `Problem` |
| 1016 | `PathWatchPolling` | Debug | A `RunicPathWatcher` started without a `failed` callback polls for a directory that does not exist yet, such as a state directory created on the first start. Not written to `Trace`. | `Problem` |
| 1017 | `BridgeOperationRetired` | Debug | The navigation entry whose page started an operation declared `RunicOperationRetirement.Cancel` or `Drain` retires: a `Cancel` operation is asked to cancel, and the page is disposed after the operation ends. | `Member`, `Policy`, `Route` |
| 1018 | `BridgeProtocolMismatch` | Warning | A page's `hello` names another Views protocol than 4, so the page is refused; use the Runic packages of one release and rebuild the frontend. Once per connection. | `ClientProtocol`, `HostProtocol`, `Connection` |
| 1019 | `BridgeRequestBeforeHello` | Debug | A request is answered `no-hello`: a `receive` before the connection's `hello`, or a request that waited for a `hello` that failed, closed or did not come within 5 seconds. Once per connection. | `Kind`, `Connection` |
| 1020 | `ViewMountFailed` | Error | A .NET View fails to mount. | `Route`, `ErrorType` |
| 1021 | `ViewRemountFailed` | Error | A View fails to mount again after a reconnect. | `Route`, `ErrorType` |
| 1022 | `BridgeRequestFailed` | Error | Handling a request outside a command, setter or operation, sending a targeted push, or reporting a closed connection or an operation's status throws. A failed request is answered `failed`. | `ErrorType` |
| 1023 | `BridgeMailboxOverflowed` | Warning | The mailbox of a connection whose transport cannot push to one connection (CS-WebUI) exceeds 1,024 pushes or 4 MiB, so its oldest pushes are dropped. Once per connection. | `Connection`, `Dropped` |
| 1024 | `BridgeReplyWaitExceeded` | Warning | A setter, checked write, command or operation start waited 5 s for the pushes its window had to deliver before the reply, and replied without them: the page did not take pushes, or code blocked a route's delivery. Once per window. | `Route`, `LimitSeconds`, `Pending` |
| 1025 | `BridgeCollectionChangedOutsideModelTurn` | Warning | A collection that a Bridge observes changed outside its window's model turn, such as a DynamicData `Bind` without `ObserveOn` the window's scheduler: the change waited for a turn and can race the window. Once per Bridge and field. | `Model`, `Field`, `Route` |
| 1033 | `ModelContextReleaseFailed` | Error | A window session's own model context fails to shut down in the background. | `ErrorType` |
| 1040, 1041 | Retired | | Reported by `ReactiveRoutedRegion<T>`, removed in 0.9; not reused. | |
| 1042 | `ReactiveCommandFailed` | Error | A bridged ReactiveUI command reports on `ThrownExceptions` an exception that no Bridge call received, while a generated Bridge presents it, or a command or object observed with `ObserveBridgeExceptions(logger)` reports one; declared `RunicFailureException`s and cancellations are not logged. An exception raised in a Bridge call's flow, such as by a plain `Execute` in a wrapper command, is held until the outermost Bridge call in that flow ends, and not logged if a call received it or an exception wrapping it. Still logged when hand-written code receives and handles it from a plain `Execute`; never when it ran the command with `RunAsync`. A streamed operation holds nothing. Also logged after the Bridge detached, with its logger, for an execution that a Bridge call or `RunAsync` stopped awaiting when it was cancelled, such as an operation that a retiring navigation entry cancels. | `Source` (`Model.Command` for the Bridge; the command expression or `sourceName` for `ObserveBridgeExceptions`), `ErrorType` |
| 1043 | `ReactiveSchedulerOutsideModelTurn` | Warning | Work is scheduled on the model-context main-thread scheduler that `AddRunicReactiveModelContext()` installs, outside a model turn and outside any async flow that a still-open model context's turn started, so it runs on the scheduler it replaced. Logged once per ViewModel type found on the call stack, and once for work with no ViewModel there, such as a timer. Runic Desktop, CS-WebUI and `RunicWindowTestHost.Create` pass the application's logger factory when a window opens; before that, or without one, it goes to `Trace`. | `Source`, `Fallback` |
| 1050 | `WindowRegistrationMissing` | Error | `ValidateWindow` or `OpenWindowAsync` finds a Window's generated Bridge unregistered, on any host. | `Code`, `DiagnosticMessage`, `Remediation` |
| 1051 | `WindowReleaseFailed` | Error | `IRunicWindowService` fails to release a Window it opened, after that Window closed. | `Window`, `ErrorType` |
| 1052 | `WindowCloseGuardFailed` | Error | A guarded Window's `CanCloseAsync` throws; the window stays open. A departure guard that throws is logged by the navigator as 1060 instead. | `Window`, `ErrorType` |
| 1053 | `WindowCloseGuardUnavailable` | Warning | A Window with `GuardClose` opens on a presentation that cannot keep its window open, such as CS-WebUI or a browser, so a user close does not ask `CanCloseAsync`. | `Window`, `Presentation` |
| 1054 | `WindowFileDropFailed` | Error | `IRunicFileDropTarget.OnFilesDroppedAsync` of a Window's root ViewModel threw for a drop of files; later drops still arrive. | `ViewModel`, `FileCount`, `ErrorType` |
| 1090 | `ReactiveSchedulerKept` | Information | A Runic Desktop, CS-WebUI or test-host window opens while ReactiveUI's main-thread scheduler is one set before `AddRunicReactiveModelContext()`, so Runic's was not installed. Once per scheduler type; a UI dispatcher's scheduler, such as WPF's, is not logged. | `Scheduler` |
| 1091 | `ReactiveSchedulerReplaced` | Warning | Such a window opens after another scheduler replaced the installed one, for example a later `RxAppBuilder` `BuildApp()` with `WithPlatformServices()`. Call `WithRunicModelContext()` on that builder. Once per scheduler type; a UI dispatcher's scheduler is not logged. | `Scheduler` |
| 1100 | `HotReloadRestartRequired` | Error | A Hot Reload edit changed a generated Bridge contract; restart the application. The `RUNIC_IDE_CONTRACT_RESTART_REQUIRED` line also goes to standard error. | `Mismatch` |
| 1101 | `HotReloadRestartNotificationFailed` | Warning | A `RunicViewsHotReload.RestartRequired` handler throws. | `ErrorType` |
| 1102 | `HotReloadRefreshFailed` | Warning | Refreshing a Bridge after a Hot Reload update throws. | `Model`, `ErrorType` |
| 1110 | `ViewHostFallbackShown` | Warning | A WPF `RunicViewHost` cannot open its web presentation and shows its `Fallback`. | `ErrorType` |
| 1111 | `ViewHostFallbackFailed` | Error | Showing that fallback throws. | `ErrorType` |
| 1112 | `ViewHostPresentationChangeFailed` | Error | A `RunicViewHost` presentation change throws outside its open. | `ErrorType` |
| 1113 | `ViewHostCleanupFailed` | Error | Releasing a `RunicViewHost` presentation throws. | `ErrorType` |
| 1114 | `ChildViewCleanupFailed` | Error | A `WpfBridgeView` fails to release after its presentation closed. | `ErrorType` |
| 2000 | `DesktopSnapshotDeliveryFailed` | Error | Runic Desktop cannot deliver a push to a page. | `ErrorType` |
| 2002 | `DesktopWindowCloseAfterOpen` | Warning | `RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` closed a Desktop window right after it opened. | `Variable` |
| 2003 | `DesktopPortalDiagnostic` | Warning or Debug | On Linux, xdg-desktop-portal reports a diagnostic for the portal services `AddRunicPlatformServices()` registers (notifications, status icon, login startup and inhibition): Warning for a rejected identity, an unavailable portal or a denied request, Debug for an accepted request or registration. | `Code`, `DiagnosticMessage`, `Remediation` |
| 2004 | `DesktopMenuCommandFailed` | Error | A menu bar command of a Window's root ViewModel (`IRunicMenuCommandSource`) or of `RunicDesktopHost.UseApplicationMenu` threw, or binding the menu bar's ids failed. | `CommandId`, `ErrorType` |
| 2005 | `DesktopDevelopmentAssetsServed` | Information | `dotnet runic dev` without a development server: a Development host serves the frontend output directory live (`RUNIC_APPLICATION_DEVELOPMENT_ASSETS`) and reloads its windows when it changes. Once per directory and start page. | `Directory` |
| 2006 | `DesktopDevelopmentAssetsUnavailable` | Warning | That directory, or the `www/` folder behind it, cannot be served, for example because it lacks the start page or contains a symbolic link; the window serves the other folder. | `Directory`, `ErrorType`, `Instead` |
| 2007 | `DesktopDevelopmentReloadFailed` | Debug | A window did not reload after the directory changed, for example because it closed meanwhile. | |
| 2100 | `CsWebUiWindowShowFailed` | Error | CS-WebUI cannot show a window: it finds no browser, the selected browser does not exist or start, its page does not connect in time, or the WebView does not start. `OpenWindowAsync` fails with a `RunicWindowConfigurationException`. | `Window`, `Code`, `Browser`, `BrowserPath`, `DiagnosticMessage` |
| 2101 | `CsWebUiNativeLibraryUnchecked` | Warning | CS-WebUI loads a WebUI library that the application selected through `CSWEBUI_NATIVE_LIBRARY` or `WebUiNativeLibrary.SetLibraryPath`, whose version Runic cannot check; it needs WebUI commit `efac0e24` or later and is tested with `2b35db13f` (CsWebUi 2.5.0-beta.4.8). Once per process, before the first window opens. | `Path` |
| 2102 | `CsWebUiMenuCommandFailed` | Error | A menu bar command of a CS-WebUI Window's root ViewModel (`IRunicMenuCommandSource`, `RunicCsWebUiHostOptions.MenuBar`) threw, or binding the menu bar's ids failed. | `CommandId`, `ErrorType` |
| 3000 | `WindowCloseCancellationCallbackFailed` | Error | A Runic Desktop close-cancellation callback throws. | `ErrorType` |
| 3001 | `WindowCloseConfirmationFailed` | Error | A Runic Desktop close confirmation throws; the Window stays open. | `ErrorType` |
| 3002 | `DesktopConfigurationInvalid` | Error | `DesktopHost.Validate` finds a check that fails the window request. | `Code`, `Option`, `DiagnosticMessage`, `Remediation` |
| 3003 | `DesktopConfigurationLimited` | Warning | `DesktopHost.Validate` finds an option the presentation ignores or narrows, or a browser fallback opens without a permission grant. | `Code`, `Option`, `DiagnosticMessage`, `Remediation` |
| 3004 | `BrowserLaunchStalled` | Warning | A browser was still running but had requested nothing after `ConnectionTimeout`, so Runic Desktop relaunched it (`DesktopHostOptions.BrowserLaunchAttempts`). | `Attempt` (the attempt that stalled), `Attempts`, `TimeoutSeconds` |
| 3005 | `BridgeHandshakeExpired` | Warning | A Bridge WebSocket sent no token check, so Runic Desktop closed it and the page reconnects: after 10 seconds, or after 2 seconds when a newer WebSocket needs the only connection. | `ConnectionId`, `SilentSeconds`, `Reason` |
| 3006 | `DesktopShutdownRequested` | Information | Ctrl+C, SIGTERM or SIGHUP asks `DesktopEventLoop.Run` to close the Desktop windows. | `Reason` |
| 3007 | `DesktopShutdownFailed` | Error | Closing the Desktop windows for that shutdown throws. | `ErrorType` |
| 3008 | `BrowserProfileNotDeleted` | Warning | A generated browser profile still cannot be deleted a minute after its browser closed; it stays in the temporary folder. | |
| 3009 | `DesktopDiagnosticSinkFailed` | Warning | `DesktopHostOptions.DiagnosticSink` throws for a diagnostic. | `Code`, `ErrorType` |
| 3010 | `EmbeddedWindowDiagnostic` | Warning or Error | An embedded window reports an event of its own: on Linux, `web-process-terminated` when the WebKitGTK web process crashed and the page was reloaded (Warning), or was not reloaded after three reloads within a minute (Error); `spell-checking-dictionary-missing` when no spelling dictionary is installed for a language of `DesktopHostOptions.SpellCheckingLanguages` (Warning). | `Code`, `DiagnosticMessage`, `Remediation` |
| 3011 | `WindowStateHandlerFailed` | Error | A `DesktopWindow.StateChanged` handler threw; later handlers and events still run. | `ErrorType` |
| 3012 | `FileDropHandlerFailed` | Error | A `DesktopWindow.FilesDropped` handler threw; later handlers and drops still run. | `ErrorType` |
| 3013 | `WindowClosedHandlerFailed` | Error | A `DesktopWindow.Closed` handler threw; later handlers still run. | `ErrorType` |
| 3014 | `MenuCommandFailed` | Error | A menu command target's `CanExecute` or `Execute` threw; the item counts as not runnable. | `Member`, `CommandId`, `ErrorType` |
| 3015 | `MenuBarPlacementChanged` | Information | On Linux, a window's menu bar moved into the desktop's global menu or back into the window, or a Flatpak cannot reach the global menu registrar (`global-menu-permission`, the placement unchanged). | `OldPlacement`, `NewPlacement`, `Reason` |
| 3016 | `GlobalMenuFailed` | Warning | On Linux, the window's menu could not be exported or the global menu registrar refused the window (`global-menu-failed`); the bar stays in the window. Once per window. | `DiagnosticMessage` |
| 3017 | `MenuBarPlacementHandlerFailed` | Error | A `DesktopWindow.MenuBarPlacementChanged` handler threw; later handlers and events still run. | `ErrorType` |
| 3018 | `DesktopQuitRequested` | Information | On macOS, Quit, the Dock's Quit or logging out quits the application after `DesktopHostOptions.ConfirmQuitAsync` and the close guards agreed: `ShutdownRequested` is cancelled and the windows close. | `Reason` |
| 3019 | `DesktopQuitConfirmationFailed` | Error | `DesktopHostOptions.ConfirmQuitAsync` threw; the application keeps running. | `ErrorType` |
| 3020 | `DesktopReopenHandlerFailed` | Error | A `DesktopHost.ReopenRequested` handler threw; later handlers still run. | `ErrorType` |
| 3021 | `EmbeddedConnectionDeadlineExtended` | Warning | An embedded WebView had requested nothing after `ConnectionTimeout` but was still loading the page, so Runic Desktop kept waiting, up to four times the timeout. Once per presentation, also when the page then connects. | `TimeoutSeconds`, `Progress` (what the WebView was doing), `LimitSeconds` |

`Model` is the ViewModel type name, `Member` the command or property, and
`Route` the Bridge route (a content presentation's route is per instance, such
as `content12`). A declared failure is an expected outcome, so it is logged at
Debug, still with its exception; `FailureType` is the failure value's type.

Events 1000-1012, 1018-1025, 1050-1053 and 1100-1102 use the category `Runic.Application.Views`
(`RunicViewsTelemetry.LogCategory`); the Hot Reload events 1100-1102 use the
logger of the window session that owns the Bridge. Events 1110-1114 use
`Runic.Application.Views.Wpf` and the factory of the host's `Services`. Event 1033 here is a window session's
own model context failing to shut down, and uses the session's logger.
Events 1013 and 1014 use the logger passed to `new AcceptedWorkScope(logger)`,
or `Trace` without one.
Navigation (1060-1075) and the model context (1030-1033) log under
`Runic.Navigation` and `Runic.Navigation.RunicModelContext`; see the
[Runic.Navigation logging](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#logging)
section. Events 2000, 2002 and 2004 use `Runic.Application.Views.Desktop`, and events 2100-2102
use `Runic.Application.Views.CsWebUi` and the factory of the Window's scope. Events 3000-3012
use `Runic.Desktop` and `DesktopHostOptions.LoggerFactory`, which `RunicDesktopHost.Run`
and `RunicViewHost` fill from the application's services.

Events 1042, 1043, 1090 and 1091 come from `Runic.Application.Views.ReactiveUI` (and its `.Reactive`
flavor) and also use `Runic.Application.Views`. The Bridge logs event 1042 through the Window's logger factory,
with the ViewModel type and command property, such as `EditorViewModel.SaveCommand`, as
`Source`; an exception that a Bridge call received is logged as 1000 or 1004
instead. `ObserveBridgeExceptions(logger)` replaces that logging for its command
and uses the logger passed to it; `Source` is the observed command's expression,
such as `SaveCommand`, or `sourceName`.

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
`rejected`, `cancelled`, `failed`, `domain_failed` or `released` (`disconnected` before 0.10). An
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
| `runic.bridge.snapshot.frames` | Counter | `{frame}` | model, `runic.bridge.frame` (`state`, `frame` or `failure`) |
| `runic.bridge.snapshot.size` | Histogram | `By` | model, frame |
| `runic.bridge.snapshot.delivery.duration` | Histogram | `s` | model, frame |
| `runic.bridge.snapshot.recoveries` | Counter | `{snapshot}` | model |
| `runic.bridge.snapshot.queue.depth` | ObservableUpDownCounter | `{frame}` | none |

Route names are span tags but not metric tags, because content routes identify
one instance. The duration histograms advise buckets from 0.5 ms to 10 s, and
the size histogram from 256 B to 4 MiB. The queue depth reports the frames
waiting for slow hosts across all Bridges when it is observed.

## Incremental generation

The generated C# goes to `runic-bridge/` in the project's `IntermediateOutputPath`
(`obj/<Configuration>/<TargetFramework>/` by default, with the runtime identifier
when one is set), and the bootstrap pass builds below `BaseIntermediateOutputPath`.
The frontend files below are in `BaseIntermediateOutputPath` too, so
`UseArtifactsOutput` and a custom `BaseIntermediateOutputPath` or
`IntermediateOutputPath` move all of them.

The generator caches successful multi-view generation in its C# `obj` output
directory. It hashes the model/dependency assemblies, generator assemblies,
output-affecting options, and generated output contents. Missing or edited
outputs invalidate the cache; unchanged generated files keep their timestamps.
Set `RUNIC_BRIDGE_CODEGEN_FORCE=1` or `RUNIC_BRIDGE_CODEGEN_CACHE=0` to bypass it.

The configured frontend build command (`RunicApplicationFrontendBuildCommand`) runs
when one of its inputs is newer than
`<BaseIntermediateOutputPath><Configuration>/<TargetFramework>/runic-bridge-frontend.stamp`
(`obj/Debug/net10.0/runic-bridge-frontend.stamp` by default), when a file
that the last build left in `<frontend>/dist` is missing (a deleted `dist`
included), or when its build state changed. The inputs are the files under
`RunicApplicationFrontendDirectory` (excluding `node_modules`, `dist`, `build`, `bin`, `obj`
and dot-directories), the generated TypeScript, the project file and
`RunicApplicationFrontendInput` items. The build state is a hash of the build
command, `RunicApplicationHost`, `NODE_ENV` and the environment variables named by
`RunicApplicationFrontendBuildEnvironment` items; only the hash is written, to
`runic-bridge-frontend.state` in `BaseIntermediateOutputPath` (`obj/` by default). `dist` serves every configuration and target
framework, so a build whose state differs from the one that last built `dist`
builds it again, for example Debug after a Release build with other variables. Generated TypeScript is rewritten only when its
content changes, so C#-only edits skip the frontend build.
`RunicApplicationFrontendInput` items are the extension point for inputs outside
the frontend directory, such as workspace packages, shared components imported
through a Vite alias, or generated assets; declare them in the project, or add
them from a target that runs before `RunicViewsGenerateBridge` (the translations
editor is an example). Delete the stamp or rebuild to force a frontend build.

The copy to `www/` mirrors `dist`. Each copy records the files it wrote, with
their last write times, in `IntermediateOutputPath`, and the next one deletes those that no
frontend has any more, such as assets with an earlier hash, or a
`runic-views.js` that no host copies any more. A recorded file whose time has
changed since, because someone else wrote it, stays. Nothing else is deleted:
the host-specific client scripts, a `dotnet runic dev` document and files the
application puts in `www/` itself stay. Files that a build before 0.7.0-preview.9 left behind are not recorded;
delete `www/` once to drop them. Set
`RunicApplicationFrontendBuildEnabled=false` when another tool, such as a running dev server,
owns the frontend build; `dotnet runic dev` does this while its development
server runs.

Generated C# and TypeScript files start with `// <auto-generated />`. Only such
files are removed from the output directories when a ViewModel disappears, so
`RunicApplicationFrontendGeneratedDirectory` may point into a directory with hand-written modules.

## Watching files

A ViewModel whose state comes from the file system can refresh when the file
system changes, rather than when a page connects. `RunicPathWatcher` watches
files, symbolic links, directories and Git checkouts, debounces their events,
and calls back in a turn of the model context, so the callback sets state
directly:

```csharp
public sealed partial class OverviewViewModel : ObservableObject, IDisposable
{
    private readonly RunicPathWatcher _watcher;
    private readonly RunicWatchTarget _system = RunicWatchTarget.Link("/run/current-system");

    public OverviewViewModel(IRunicModelContext context, RunicPathWatcherOptions? watching = null)
    {
        _watcher = RunicPathWatcher.Start(context,
            [
                _system,
                RunicWatchTarget.Directory("/nix/var/nix/profiles", "system-*-link"),
                RunicWatchTarget.GitHead(_configurationCheckout),
            ],
            change => _ = RefreshAsync(change.Contains(_system)),
            failure => Problem = failure.ToString(),
            watching);
        _ = RefreshAsync(systemChanged: true);   // start first, then read the initial state
    }

    public void Dispose() => _watcher.Dispose();
}
```

| Target | Reports | For example |
| --- | --- | --- |
| `File(path)` | The file is created, deleted, replaced, renamed or written. | A settings file, `flake.lock` |
| `Link(path)` | The path a symbolic link leads to, through every link in its chain, differs: the link was created, removed or re-pointed. Changes inside the final file or directory are not reported. | `/run/current-system`, `~/.local/state/nix/profiles/home-manager` |
| `Directory(path, pattern)` | The set of entry names matching the pattern differs: one is added, removed or renamed. Writes to entries, and a rename onto a name that already existed (the same set of names), are not reported. | `/nix/var/nix/profiles/system-*-link` |
| `Files(path, pattern)` | An entry matching the pattern changes as for `File`: it is added, removed, renamed, written, or replaced by renaming another file over it, as atomic writers update a file. Where it polls, the watcher reads each matching entry's size and write time. | A directory of records that other processes rewrite, `*.json` |
| `GitHead(repository)` | The commit `HEAD` resolves to, or the branch it names, differs: a commit, reset, switch, detach or pull. Works in linked worktrees, whose `.git` is a file, follows symbolic refs (five levels, as Git does) and supports the reftable format. `git pack-refs`, fetches and other branches are not reported with the files format. | A configuration checkout |

After an event the watcher waits for `Debounce` (250 ms) without another
event, at most ten intervals, then reads the affected targets and reports
those whose state changed. A burst of writes, `ln -sfn`'s temporary link or
Git's `.lock` files therefore produce one callback or none. For the same
reason a target that changes and changes back within that window, such as a
link re-pointed from A to B and back to A, is not reported, except a `File`
or `Files` target, for which an event that names a matching entry alone counts. `GitHead` reads `HEAD`, the loose ref
and `packed-refs` itself; it starts no `git` process. With the reftable format
any update of the ref stack counts, of the repository's and, in a linked
worktree, of the worktree's own, so other branches and fetches are reported
too. To react to work-tree edits, add `Directory` or `File` targets for them.

A change's `Targets` are the targets that changed; its `Entries` name the paths,
each with its `Target` and a `RunicPathChangeKind`: `Created`, `Deleted` or
`Changed` (the set is open; treat an unknown kind as `Changed`). A `File` target
reports the file. A `Directory` or `Files` target reports the matching entries
that were added or removed, a rename being both, in ordinal order of their
names, and a `Files` target also those whose size, write time, kind or link
target changed. Where the watcher cannot name what changed, as for `Link` and
`GitHead`, an unreadable directory, or an event that named an entry whose
metadata stayed the same, the entry is the target's own full path with
`Changed`. Entries compare two readings, so an entry created and deleted again
between them is not listed:

```csharp
change =>
{
    foreach (var entry in change.Entries)
        if (entry.Kind == RunicPathChangeKind.Created) Records.Add(Load(entry.Path));
}
```

Problems never throw on a watcher thread. They reach the `failed` callback,
in a model turn, or `System.Diagnostics.Trace` (event 1015) without one, and the watcher
keeps working by polling every `PollingInterval` (2 s) where it needs to. A
missing directory is expected, as on an application's first start, so without
a `failed` callback it is event 1016 at `Debug` level instead of a warning:

| `RunicPathWatchErrorKind` | When | Then |
| --- | --- | --- |
| `Missing` | A directory a target needs does not exist. | Polls until it does, then reports the change. |
| `Overflow` | The platform dropped events. | Reads the affected targets again. |
| `WatchLimit` | `fs.inotify.max_user_watches` or `max_user_instances` is exhausted. | Polls; retries the watch on each poll. |
| `Failed` | A watch could not start or stopped. | Polls; retries the watch on each poll. |
| `NotGitRepository` | A `GitHead` directory exists but holds no repository. | Watches it for `.git` or `HEAD` to appear and reports the change; reads it only at the 30-second backstop. |

`RunicPathWatchErrorKind` is open: a minor release may add a kind, so give a
`switch` over it a default arm and treat an unknown kind as `Failed`, after
which the watcher polls.

Dispose the watcher with its ViewModel. It also stops when the model context
closes, and no callback starts after `Dispose` returns in a model turn. Set
`UsePolling` to read the targets every `PollingInterval` instead of watching.
To test a ViewModel that watches, pass it the options of a
[`RunicPathWatchTester`](../Runic.Application.Testing/README.md#file-watchers).

### Refresh on change or on connect

- **Refresh on change** when the ViewModel's state mirrors something outside
  the application that can change while a page is open: a profile, a running
  system, a Git checkout, a configuration file. Start a watcher with the
  ViewModel, read the state once after starting it, and refresh in its
  callback. A page that connects later receives the current snapshot; it does
  not need to call a refresh command.
- **Refresh on connect**, or on a command, when nothing can notify you: a
  remote service, a command's output, or state that is expensive to read and
  only interesting when someone looks. A watcher would only add work. Keep a
  manual refresh command for state that a watcher covers only partly.
- Combine them when the change is the trigger but reading is expensive: let the
  callback mark the state stale or start a superseding read, rather than
  reading everything for each event.

Before, a page had to ask: nix-manager polled every 30 seconds while a page
was open and observed again when a page connected after more than 5 seconds.

### Platforms

`RunicPathWatcher` uses .NET's `FileSystemWatcher` on the directory that holds
each watched entry, so a link replaced by a rename is seen in its parent, and
shares one watcher per directory across the process. It also watches that
directory's parent: inotify and FSEvents keep a watch on a deleted directory
and report nothing for a new directory of the same name, so the watcher
compares the directory's identity (device, inode and birth time, since ext4
reuses a freed inode at once, or volume and file index on Windows) when the parent reports it, on each read and when another watcher
joins, and starts a new watch when it was replaced. `PathWatcherTests` run the
first five rows on Linux, Windows and macOS in CI, and check that an event,
not a poll, reported each change; the network and limit rows follow the
platforms' documentation.

| Case | Linux (inotify) | Windows (`ReadDirectoryChangesW`) | macOS (FSEvents) |
| --- | --- | --- | --- |
| Link re-pointed by rename | Rename event in the parent directory | Same | Same; events can arrive later and merged, the watcher reads the targets again |
| File written in place | Write event | Write event | Write event, merged with others |
| Directory entry added or removed | Create, delete or rename event | Same | Same |
| Watched directory deleted and recreated | The parent's watch reports the delete; the watcher reports `Missing`, and the parent's event reports the return | Same | Same; FSEvents reports nothing for the watched directory itself, only its parent does |
| Directory replaced before any read (`rm -rf` and `git clone`) | The old watch reports nothing more; the parent's event leads to a new watch | Same | Same |
| Network, FUSE and virtiofs file systems | Polls: inotify reports only local changes (NFS, CIFS, sshfs, a virtual machine's shared folders) | Polls network drives and UNC paths | Polls network volumes |
| Limits | Each `FileSystemWatcher` holds an inotify instance (`fs.inotify.max_user_instances`, 128 by default per user) and each directory a watch (`fs.inotify.max_user_watches`), its parent one more; `GitHead` adds one per directory under `refs/heads`. Exhausted: `WatchLimit` and polling | 64 KiB event buffer; overflow: `Overflow` | Overflow: `Overflow` |
| Creating symbolic links | Allowed | Needs Developer Mode or elevation; reading them does not | Allowed |

Even with working events, the watcher reads its targets every 30 seconds (or
every `PollingInterval` when that is longer) to recover from events a
platform lost.

## Accepted application work

Cancellation requests that a command stop; its invocation can finish before
the underlying task completes. In particular, ReactiveUI bridge execution
can settle a cancelled invocation and release its subscription while an
uncooperative task continues recovery. `WindowContentSession.DrainAsync`
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
  when `package.json` or a lock file changed since the last install, and when
  declared dependencies have no `node_modules`. If that install did not run or
  failed, install them with the frontend's package manager, or run
  `dotnet runic doctor` to check the package manager and lock file. Set
  `RunicApplicationFrontendInstallEnabled=false` when a workspace install owns
  them.
- **The build says only one ReactiveUI flavor can be used.**
  `Runic.Application.Views.ReactiveUI` and `Runic.Application.Views.ReactiveUI.Reactive`
  cannot both be referenced, and the `.Reactive` adapter needs
  `Runic.Navigation.ReactiveUI.Reactive`. Keep the pair for one flavor; see
  [Runic.Application.Views.ReactiveUI.Reactive](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI.Reactive/README.md).
