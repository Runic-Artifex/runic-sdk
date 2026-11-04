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
- The new Window's ViewModel is resolved from DI like the first one's. To give it input
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
  application's services. The parameter overload, and the members below (since
  0.16), have default implementations that throw `NotSupportedException`, so a
  fake overrides only what the application uses. To run the real service, open
  the first Window with `RunicWindowTestHost.CreateWindowAsync<TWindow>(services)`
  from Runic.Application.Testing, which opens every Window headless.

#### Finding, activating and opening Windows for a result

Since 0.16, the service also manages the open Windows:

- `Windows` lists every Window that a host opened with these services, through
  the service or `host.OpenWindowAsync`, in the order they opened, until its
  close starts: the application closed it, a close request decided to close it,
  `CompleteAsync` completed it, or the user closed it. `WindowsChanged` reports
  each open and close, one change at a time and in order, on a thread-pool
  thread, each with its own `Windows` list. It is not raised in any Window's
  model context, so a ViewModel posts what it does to its own.
  `ObserveWindows()` returns the list now and after each change as a plain
  `IObservable`, without System.Reactive; `ObserveWindows(context)` delivers each
  list in a turn of `context`, such as the subscribing ViewModel's
  `IRunicModelContext`. A handler that throws is logged as event 1181.
- `FindOrOpenAsync<TWindow, TParameter>(key, parameter, options, activation)`
  brings the open `TWindow` with that key to the front, or opens a new one with
  the key. The key belongs to the Window type and is compared with `Equals`; two
  calls with the same key at once open one Window, and a Window whose close has
  started is never found, so the next call opens a new one. When the call that
  opens is cancelled, a call that was waiting for it, and was not cancelled
  itself, looks again and opens the Window; a failed open fails every waiting
  call. `FindWindow<TWindow>(key)` only looks it up.
- `ActivateAsync(window, activation)` shows a hidden Window, restores a
  minimized one and asks for the focus with `IRunicWindowPresentation.PresentAsync`.
  When a window-modal Window blocks it, that Window comes forward after it.
- `OpenForResultAsync<TWindow, TParameter, TResult>(parameter, options)` opens a
  Window and returns its `RunicWindowResult<TResult>` once it closed. The
  Window's ViewModel takes `IRunicWindowResult<TResult>` and calls
  `CompleteAsync(value)`, which sets the result and closes the Window without
  asking its close guard; the command that called it finishes normally, since
  the close gives accepted operations up to 5 seconds. A ViewModel that completes
  in its constructor, before its Window opened, closes the Window once it opened.
  A Window that closes
  without a result, by the user or the application, returns a result whose
  `HasValue` is `false` (the default value). Cancelling the opener's token closes
  the Window without asking and throws `OperationCanceledException`.

```csharp
public sealed partial class ShellViewModel(IRunicWindowService windows, IRunicWindowContext window) : ObservableObject
{
    [ObservableProperty] private string _account = "Household";

    // Open the document in a Window of its own, or bring the open one to the front.
    [RelayCommand]
    private async Task OpenDocumentAsync(string path) =>
        await windows.FindOrOpenAsync<DocumentWindow, DocumentRequest>(path, new DocumentRequest(path));

    // A dialog over this Window that returns the account chosen, if any.
    [RelayCommand]
    private async Task ChooseAccountAsync(CancellationToken cancellationToken)
    {
        var chosen = await windows.OpenForResultAsync<AccountWindow, AccountRequest, string>(new AccountRequest(Account),
            new RunicWindowOptions { Owner = window.Window, Modality = RunicWindowModality.Window }, cancellationToken);
        if (chosen.TryGetValue(out var account)) Account = account;
    }
}

public sealed partial class AccountViewModel(IRunicWindowResult<string> result) : ObservableObject
{
    [RelayCommand]
    private async Task ChooseAsync(string account) => await result.CompleteAsync(account);
}
```

With ReactiveUI the commands are `ReactiveCommand.CreateFromTask` with the same
bodies, as in the
[multi-window example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/multi-window).

#### Owned and window-modal Windows

`RunicWindowOptions.Owner` names the open Window a new Window belongs to; a
ViewModel names its own with `IRunicWindowContext.Window`. `Modality =
RunicWindowModality.Window` makes the owner take no input until the new Window
closes, and needs an owner; the application's other Windows stay usable.

| Host | Owned | Window-modal |
| --- | --- | --- |
| Runic Desktop, Windows (WebView2) | The owner's `HWND` is the window's owner: it stays above it and has no taskbar button. | The owner is disabled (`EnableWindow`) while the window is shown, and enabled before it is destroyed, so the owner is activated again. |
| Runic Desktop, macOS (WKWebView) | A child window of the owner's `NSWindow`: it stays above it and moves with it. | A sheet of the owner's `NSWindow`; it ignores the window's position. A sheet has no title bar, so no close button, and Esc does not close it: give its page a Cancel button (`IRunicWindowContext.RequestCloseAsync`). AppKit shows one sheet of an owner at a time: a second window modal to the same owner is queued and stays invisible until the first ends; `FocusAsync` focuses the visible one. |
| Runic Desktop, Linux (GTK 3 and GTK 4) | `transient_for` the owner's GTK window. | The owner's GTK window is insensitive while the window is shown. `gtk_window_set_modal` is not used: it would block every window of the application. |
| WPF | `WpfPlatformServices.ForDesktopWindowOwner(window)` gives a Runic Desktop window a WPF owner. | As on Windows. |
| CS-WebUI, serve-only mode, a browser fallback | Not available: the Window opens on its own. | Not available. |

On Runic Desktop an owned Window hides and shows with its owner
(`SetVisibleAsync`). A window-modal Window blocks its owner while it is shown: one
opened `Hidden` blocks it from its first show, and hiding it lets the owner take
input again, as everywhere. Several Windows can be modal to one owner, and a
modal Window can open its own modal Window; an owner takes input again when the
last one that blocks it closed or was hidden. A blocked owner takes no input, its
title bar included, and ignores the user's and the window manager's close on
every platform (on Linux too), so its own close confirmation never shows behind
its modal Window.

When a guarded owner closes, by `RequestCloseAsync` or the user, and when the
application quits, the guards of the Windows it owns are asked first, innermost
first, the last opened first: one that refuses keeps the owner open with them,
and a window-modal one that agrees closes before the owner's own guard is asked.
An owner without a guard, and the application's own close (`CloseAsync`,
`DisposeAsync`), close its owned Windows without asking, so a pending
`OpenForResultAsync` returns no result. The owner's `IRunicWindowPresentation.Features` include
`OwnedWindows` where the host can own Windows. A host that cannot opens the
Window on its own and logs `WindowOwnerIgnored` (event 1180), as it does for an
owner that has closed or belongs to another host; `ValidateWindow` on CS-WebUI
reports `window-owner-unsupported`.



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

### Who is connected, and who called

A Window's page can be open more than once: a CS-WebUI window with WebUI's
`MultiClient` configuration is shared by every browser that reaches it, and
any page gets a new connection when it reloads. `IRunicConnections` (since
0.16), which `AddRunicWindowService()` registers per scope, lists the
Window's page connections and tells a command which one invoked it:

- `Current` lists the connections that completed their `hello`, in the order
  they connected, until the transport reports them closed. Each is a
  `RunicConnection(ConnectionKey, ClientKey)` with opaque keys: compare and
  store them, but do not parse them or show them to users.
- `Connected` and `Disconnected` are plain .NET events, raised in a turn of
  the Window's model context, as a command body runs, so a handler may change
  state the page sees. `Current` changes in the same turn. The turn runs in no
  command's invocation, so a handler has no `Caller`. A connection the Window
  already had when the service was bound is reported with `Connected` too; one
  that closes before its `Connected` turn runs is never reported. Once a
  closing Window's model context takes no more turns, `Current` still follows
  the connections but no event is raised, and closing the Window raises no
  `Disconnected`. A handler that throws is logged as event 1230
  `ConnectionHandlerFailed`, and the other handlers still run.
- `Caller` is the connection whose command, operation or in-page menu click
  of this Window runs on the current asynchronous flow, and `null` elsewhere:
  in the window scope (`RunicInteractionInvocation.EnterWindow`), or when the
  command belongs to another Window. It stays the same through the command's
  `await`s, also after its connection closed, until the command ends.
- **A page's invocation ends with its command.** An invocation with a
  connection identity, a page's command, operation or in-page menu click,
  ends when the work that entered it does: a bridged command or operation
  when its task completes, and a command the Bridge runs synchronously when
  `Execute` returns. An in-page menu click ends with the command's task when
  the command is a CommunityToolkit `IAsyncRelayCommand` (Runic reads its
  `ExecutionTask`), and otherwise when `Execute` returns: an async
  ReactiveCommand, or another `ICommand` that starts work it does not return,
  asks the clicking page before its first `await`. Work a command started
  that runs on after that, such as a timer, `Task.Run` or a background loop,
  has no invocation: its `Caller` is `null`, and its questions and view
  requests follow the rules outside a scope instead of reaching the page.
  Such work enters `RunicInteractionInvocation.EnterWindow(viewModel)` to
  reach the window's page.
- **The window scope does not end.** A native menu-bar command, a host
  shortcut, the window's close decision and `EnterWindow` name no page, so
  their scope flows with the work they start: an async native-menu command
  that asks after an `await` still reaches the window's one page.
- **A navigation keeps the identity that started it.** A navigation runs its
  leave guards and other hooks in an invocation with the identity of the
  code that started it, until the navigation ends: a guard of a Back that a
  synchronous command starts without awaiting it (`_ = Main.BackAsync()`)
  still asks the page that clicked.
- A connection key is never reused within a Window: the window gives every
  connection its own key (`0:1#2` on CS-WebUI), and a command of a closed
  page never reaches the page that took its slot.
- `connections.WhenChanged()` is an `IObservable<IReadOnlyList<RunicConnection>>`
  that emits `Current` when subscribed and after each change. It needs no
  System.Reactive and works with both ReactiveUI flavors.

`OpenWindowAsync` binds the service to the Window it opens when the
Window's Bridge attaches, on Runic Desktop and on CS-WebUI. A Runic Desktop
window has one page connection, and a new one after each reload; a native
menu bar or host shortcut has no page, so its command has no `Caller`. A WPF
`RunicViewHost` creates no Window scope, so the service it would resolve from
the application's provider lists nothing and raises nothing; there,
`RunicInteractionInvocation.Current` still names the page's connection during
a command.

"Who else is viewing", with CommunityToolkit.Mvvm. Each page's claims live in
a `RunicConnectionState`, so they go when the page disconnects:

```csharp
public sealed partial class BoardViewModel : ObservableObject, IDisposable
{
    private readonly IRunicConnections _connections;
    // The cards each page claimed; removed with the page.
    private readonly RunicConnectionState<HashSet<string>> _claims;

    public BoardViewModel(IRunicConnections connections)
    {
        _connections = connections;
        _claims = new(connections, _ => []);
        connections.Connected += (_, _) => Viewers = connections.Current.Count;
        connections.Disconnected += (_, _) => Viewers = connections.Current.Count;
    }

    // "3 people are viewing this board"
    [ObservableProperty]
    public partial int Viewers { get; private set; }

    [RelayCommand]
    private void Claim(string cardId)
    {
        // The page that clicked, as the transport authenticated it.
        if (_claims.TryGetCaller(out var claimed)) claimed.Add(cardId);
    }

    public void Dispose() => _claims.Dispose();
}
```

Do not broadcast connection keys, or claims tied to them, in state that every
page sees together with other users' data unless the application's own
sign-in decided who may see whom: a viewer count says nothing about anyone, a
list of keys and what each page did does.

With ReactiveUI:

```csharp
_viewers = connections.WhenChanged().Select(list => list.Count).ToProperty(this, vm => vm.Viewers);
```

**Per-connection state.** `RunicConnectionState<T>` keeps one value per
connection, such as a draft, a selection or a cursor that each page keeps for
itself. It creates a value on first use, only for a connection that `Current`
lists, with the Window's own `RunicConnection` (a `RunicConnection` whose
client key is not the connection's gets nothing). It removes the value,
disposing an `IDisposable` one, when the connection's `Disconnected` is
raised, and disposes the rest when it is disposed; a `Dispose` that throws is
logged as event 1231 `ConnectionStateDisposeFailed` and the others still run.
Its factory runs under its lock and must not call `TryGet` of the same state.
Dispose it with the ViewModel:

```csharp
// In a ViewModel whose primary constructor takes IRunicConnections connections:
private readonly RunicConnectionState<Draft> _drafts = new(connections, _ => new Draft());

[RelayCommand]
private void Type(string text)
{
    if (_drafts.TryGetCaller(out var draft)) draft.Text = text;
}
```

**Trust.** Only the Bridge runtime sets the caller: it enters
`RunicInteractionInvocation` with the identity that the Window's transport
authenticated for the request, never values in the request's JSON, and
application code cannot enter one with an identity.
`RunicInteractionInvocation.Current?.ConnectionKey` and `ClientKey` are the
same values, for code that has no `IRunicConnections`. The keys tell pages
apart; they do not say who the user is. On CS-WebUI every client that reaches
the window's server can connect, so an application that shares a window with
`MultiClient` authenticates its users itself and maps a connection to a user
after its own sign-in; see the
[CS-WebUI security notes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.CsWebUi/README.md#security).

In tests, `RunicWindowTestHost.Connections` lists the test connections and
`host.Root.ForConnection("bob-tab", clientKey: "bob")` drives the Window as a
second page; see
[Runic.Application.Testing](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md#several-pages-of-one-window).
### Presenting, hiding and observing a ViewModel's own Window

`IRunicWindowPresentation` is the Window a ViewModel belongs to as it appears on
the desktop. `AddRunicWindowService()` registers it per scope, next to
`IRunicWindowContext`, so the ViewModel takes it in its constructor and never
casts `IRunicWindow.Host` to a host's type:

| Member | What it does |
| --- | --- |
| `PresentAsync(activation)` | Brings the Window to the front: shows a hidden one, restores a minimized one and asks for the keyboard focus, with the launch's activation token where the desktop needs one. |
| `MinimizeAsync()` | Minimizes it to the taskbar or Dock. |
| `SetVisibleAsync(visible)` | Hides it without closing it, for example to a status icon, or shows it again. |
| `SetFullScreenAsync(fullScreen)` | Enters or leaves full screen. |
| `SetDocumentEditedAsync(edited)` | Marks unsaved changes where the platform shows them: the dot in the macOS close button. |
| `SetColorSchemeAsync(scheme)` | Switches it between light and dark; the page's `prefers-color-scheme` follows. |
| `IsActive`, `IsMinimized`, `IsVisible`, `Activity` | Whether it has the keyboard focus, is minimized and is shown, with `ActivityChanged` and `PropertyChanged`. |
| `Features` | What the host can do now. |

With CommunityToolkit.Mvvm, a ViewModel that polls less while the user works
elsewhere and hides to the status area:

```csharp
public sealed partial class InboxViewModel : ObservableObject
{
    private readonly IRunicWindowPresentation _window;

    public InboxViewModel(IRunicWindowPresentation window)
    {
        _window = window;
        window.ActivityChanged += (_, e) => PollInterval = e.Activity.IsActive ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1);
    }

    [ObservableProperty]
    public partial TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(5);

    [RelayCommand]
    private async Task HideToTrayAsync() => await _window.SetVisibleAsync(false);
}
```

With ReactiveUI, observe a property with `WhenAnyValue`, or the activity as an
`IObservable<RunicWindowActivity>` from `ObserveActivity()`, which needs no
System.Reactive and composes with either flavor:

```csharp
public InboxViewModel(IRunicWindowPresentation window)
{
    _pollInterval = window.WhenAnyValue(w => w.IsActive)
        .Select(active => active ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1))
        .ToProperty(this, x => x.PollInterval);
    HideToTrayCommand = ReactiveCommand.CreateFromTask(async token => { await window.SetVisibleAsync(false, token); });
}
```

- The changes are raised in a turn of the Window's model context, as a command
  runs, so a handler sets ViewModel state directly.
- Requests return `PlatformResult<PlatformUnit>`, as the platform services do,
  and never throw for what a host cannot do: `BackendUnavailable` for a feature
  the host lacks, `OwnerUnavailable` before the Window opens or in a scope that
  no Window opened, `OwnerClosed` once it closed. A desktop may refuse to take
  the focus from the application the user works in; Windows then flashes the
  taskbar button and `PresentAsync` reports `PermissionDenied`.
- Where the host reports no activity (no `RunicWindowPresentationFeatures.Activity`),
  the Window counts as active and visible (`RunicWindowActivity.Assumed`), so
  work that pauses while it is inactive keeps running. A closed Window reports
  `RunicWindowActivity.Closed`. Unknown counts as active, so check
  `Features.HasFlag(RunicWindowPresentationFeatures.Activity)` before you rely
  on `IsActive == true`: code that notifies only while the Window is inactive
  never notifies on CS-WebUI or in an installed browser, and should notify
  there whatever `IsActive` says.
- On Linux a minimized window is only a hint: Wayland compositors do not tell
  an application that its window was minimized. A GTK 4 window (GTK 4.12 and
  later) reports itself not visible once the compositor stops showing it; a
  GTK 3 window on Wayland stays visible and only loses the focus.
- The color scheme is not every Window's own. Without
  `RunicWindowPresentationFeatures.PerWindowColorScheme` it is shared, and
  `SetColorSchemeAsync` changes every Window that shares it: on Linux every GTK
  window of the application, on Windows the windows that share a WebView2
  profile (`DesktopWindowOptions.ProfilePath`). `ColorScheme` reports what each
  Window shows, also after another Window or the application changed it, and
  raises `PropertyChanged`. On Runic Desktop the application's scheme
  (`DesktopHost.SetColorSchemeAsync`) is applied to a shared scheme last, so it
  wins over the Windows' own, whatever order they are in.
- Code outside the scope, such as a status icon's handler, reads the same
  object from `IRunicWindow.Presentation`:
  `await window.Presentation.PresentAsync(launch.PlatformContext)`.
- Tests register `RunicTestWindowPresentation` with
  `AddRunicTestWindowPresentation()` from Runic.Application.Testing. It
  records each request and lets the test change the activity, and reaches the
  ViewModel through the same per-Window presentation as a host's.
- This is not a ViewModel activation hook (the I-142 proposal, which was
  dropped): it reports the native window, not whether a View currently shows
  the ViewModel. Use the View lifetime hooks for that.

| Host | Present | Minimize | Hide and show | Full screen | Document edited | Color scheme | Activity |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Runic Desktop, WebView2 | Yes | Yes | Yes | Yes | No | Yes; shared by the windows of a configured `ProfilePath` | Yes |
| Runic Desktop, WKWebView | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| Runic Desktop, WebKitGTK (GTK 3, GTK 4) | Yes | Yes | Yes | Yes | No | Yes, shared by every window of the application | Yes; minimized only on X11 |
| Runic Desktop, an installed browser after a fallback | No | No | No | No | No | No | No |
| WPF, `WpfPlatformServices.ForWindowPresentation(window)` | Yes | Yes | Yes | Yes | No | Title bar and the pages of `RunicViewHost`s | Yes |
| CS-WebUI | Yes | WebView only | No | No | No | No | No |

A custom Runic Desktop window host provides the activity and the color scheme
through `IDesktopActivityWindowHost` and `IDesktopColorSchemeWindowHost`; a
custom Window host gives the Window its own presentation with
`RunicWindowOpening<TViewModel>.UsePresentation` once the native window exists.

### What the page sees of a close

Every close of a Window (`CloseAsync`, `RequestCloseAsync` once it decided,
`DisposeAsync`, a host's shutdown) tells its pages, when their client and the
host implement the `presented/1` feature (0.15): the page renders its last
state and reports the connection as `closed` instead of reconnecting or lost,
so a CS-WebUI tab left open after the application exited says that the
application closed it rather than showing an error.

`CloseAsync(timeout)` waits for each such page to acknowledge, at the same time
as for accepted operations and at most `timeout` or 5 seconds, whichever is
shorter; a page that disconnects counts as done. The result reports it:

```csharp
var closed = await window.CloseAsync(TimeSpan.FromSeconds(10));
if (!closed.PagesAcknowledged) logger.LogInformation("A page did not confirm the close.");
```

`PagesAcknowledged` is `true` when every page acknowledged, also when no page
uses the feature, such as a page of an older release. `DisposeAsync` and
`CloseAsync(TimeSpan.Zero)` tell the pages but do not wait. Only the first
close of a Window tells and waits; later calls return its result. An
unacknowledged close is logged at Debug as event 1026.

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
none). An item that a page clicks in its in-page menu bar runs as a command
of that page: its questions, interactions and view requests reach only that
page. One that the native bar or a host shortcut runs has no page and runs in
the window's interaction scope, so it reaches the one page presentation of the
Window that handles it, as during a close decision (see
[Asking the page](#asking-the-page-view-requests-and-questions)).
One command can therefore serve several items, such as one per zoom
level in a radio group:

```csharp
new DesktopMenu("View",
[
    new DesktopMenuItem("Small", "view.zoom") { RadioGroup = "zoom", IsChecked = zoom == 0.8, CommandParameter = "0.8" },
    new DesktopMenuItem("Large", "view.zoom") { RadioGroup = "zoom", IsChecked = zoom == 1.5, CommandParameter = "1.5" },
])
```

Check marks follow the ViewModel when it also implements
`IRunicMenuCheckedSource`. The host asks it for each item declared with
`IsChecked`, in the model turn, when the Window opens, after `SetMenuBar` and
in the turn after the root ViewModel raises `PropertyChanged` (one read for any
number of changes in a turn). `null` leaves the item to the application's commands
and then to its declaration.
Each Window answers for its own bar, so the native bar follows the key window,
and the page's bar and a Linux global menu show the same marks:

```csharp
public sealed partial class EditorViewModel : ObservableObject, IRunicMenuCommandSource, IRunicMenuCheckedSource
{
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private double _zoom = 1.5;

    public bool? IsMenuItemChecked(string commandId, string? commandParameter) => commandId switch
    {
        "view.wrap" => WordWrap,
        "view.zoom" => commandParameter == Zoom.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };
}
```

ReactiveUI ViewModels raise `PropertyChanged` from `RaiseAndSetIfChanged` the
same way. Only the root ViewModel's `PropertyChanged` is followed: when a mark
depends on a child ViewModel or on state outside the ViewModels, call
`RunicMenu.Refresh(rootViewModel)` (or `RunicMenu.RefreshAll()`) after it changed.
A custom `IDesktopMenuCommandTarget` overrides `IsChecked(item)` and returns `null`
for items it has no answer for.

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
| `RunicBlob` | `RunicBlob` (`{url, type, size, name}`) | A URL the host serves, never the bytes; see [Blobs and uploads](#blobs-and-uploads). To the page only. |
| `RunicUpload` | `BlobRef` (`{id, name, type, size}`) | A file the page uploaded with `client.upload(file)`; see [Blobs and uploads](#blobs-and-uploads). |

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

`RUNICBRIDGE013`, `015`, `019`, `020`, `028`, `029`, `030`, `031`, `032`, `033`, `034` and `053` are warnings; the generator's
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
`Runic.Application.FrontendArchive` runtime option. `RunicDesktopHost`, the
WPF `RunicViewHost` and, since 0.15, `RunicCsWebUiHost` then serve the archive
in place of the web root, with each Window's `StartPage` at its root; nothing
is extracted to disk. A Window's `ContentRoot`, the Desktop host's
`SurfaceOptions` and `dotnet runic dev`, which builds with `Files` and serves
the live frontend, still take precedence. `RunicDesktopHostOptions.Assets` and
`RunicCsWebUiHostOptions.Assets` serve another source, such as an archive read
at run time, or one folder of an archive with `Subdirectory`:

```csharp
var archive = AssetArchive.ReadEmbedded(typeof(Program).Assembly, "Frontends.runic-assets");
var options = new RunicDesktopHostOptions { Assets = archive.Subdirectory("settings") };
```

`Embedded` needs one host that serves the archive: Runic Desktop
(`Runic.Application.Views.Desktop`) or CS-WebUI
(`Runic.Application.Views.CsWebUi`), both of which bring Runic.Assets. A project
that references both sets `RunicApplicationHost`; otherwise the build uses
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
| `RunicApplicationFrontendDelivery` | `Files` | `Files` copies the frontend to the web root. `Embedded` packs the same tree (referenced projects' frontends, this project's output and the host's `runic-views.js`) into the assembly with Runic.Assets, and the Runic Desktop, WPF and CS-WebUI hosts serve it; see [Ship the frontend inside the executable](#ship-the-frontend-inside-the-executable). Closed: another value, no single Desktop or CS-WebUI host, or a project that sets `RunicAssetsDist` itself warns with [`RUNICBRIDGE030`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicbridge030) and builds as `Files`. |
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
  which usually follows the system language (`LANG` on Linux), or from the
  window's locale that the application sets, which also translates the text the
  ViewModels produce; see [Translated text](#translated-text). Include the
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

## Editable rows and nested fields

Since 0.16 a page edits the members of an object and the cells of a keyed
collection's rows one field at a time, with drafts, conflicts and validation,
and without a command per cell (`field-paths/1`,
[field paths](../../../specs/application/README.md#field-paths)):

```csharp
public sealed record Address(string Street, string PostalCode, string City);
public sealed record InvoiceLine(int Id, string Product, int Quantity, decimal Price);

public sealed partial class InvoiceViewModel : ObservableObject
{
    [ObservableProperty]
    public partial Address Address { get; set; } = new("", "", "");

    [RunicCollection(nameof(InvoiceLine.Id))]
    public ObservableCollection<InvoiceLine> Lines { get; } = [];

    [RunicRowEditor]
    internal void UpdateLine(int id, InvoiceLine line)
    {
        if (line.Quantity > 10_000) throw new ArgumentException("At most 10,000 per line.");
        Lines[Lines.IndexOf(Lines.Single(existing => existing.Id == id))] = line;
        OnPropertyChanged(nameof(Total));
    }
}
```

```ts
const street = useField(invoice, InvoiceFields.address.street, { commit: "pause" });
const quantity = useField(invoice, InvoiceFields.lines.at(line.id).quantity, { commit: "pause" });
```

- **Object members.** A settable member whose value is a DTO, such as
  `Address`, also has a field for each of its members, down to three objects
  deep: `InvoiceFields.address.street`, `InvoiceFields.address.geo.lat`. A
  write copies the member's value with that one member changed and assigns the
  copy with the setter, so the setter's validation, normalization and change
  notification run as for a whole value. An object on the path that is `null`
  rejects the write.
- **Copies.** The generated copy keeps what the page does not see, such as a
  `[RunicIgnore]` database id: a record or struct is copied with `with`, a
  sealed class with its copy constructor or `Clone()`, and a sealed class whose
  settable members are all on the wire through its codec. A class that is not
  sealed, or one with hidden settable members and no copy constructor or
  `Clone()`, cannot be copied exactly and has no fields below it; as rows it is
  `RUNICBRIDGE052`, as an object inside a row warning `RUNICBRIDGE053`.
  `[RunicNoField]` leaves a member of a row or object without a field.
- **Rows.** A `[RunicCollection]` with a `[RunicRowEditor]` method has a field
  for each row member except the key: `InvoiceFields.lines.at(key).quantity`.
  The method takes the row's key and the row with the one cell changed, and
  optionally a `RunicRowEdit` with the changed cell (`Path`), the row before the
  edit (`Previous`) and the page's `ConnectionKey`; it replaces the row
  (`Lines[index] = line`), may normalize it and updates what depends on it. Throw to refuse the change: the page keeps its draft and shows
  the message. Without a collection name the attribute finds the collection by
  its row type; `[RunicRowEditor(nameof(Lines))]` names it. A collection without
  a row editor stays read-only for the page. `RUNICBRIDGE050` and `RUNICBRIDGE051`
  report editors that match no collection, several, or have another signature.
- **Conflicts.** Each field path is a checked field with its own version: the
  revision at which a publication last changed it. A write that is based on an
  older state of the cell than the cell's last change is a `conflict`, also when
  the value changed back, and keeps the page's draft until the user retries or
  resets it. A change of another cell of the same row, or another member of the
  same object, is not a conflict. A write of a row that was removed is rejected,
  and a row removed and added again under its key is a new row. With
  `[RunicRowEditor(Conflicts = RunicRowConflicts.Row)]` any change of the row
  after the page's state is a conflict instead, so a price typed for a line
  whose product another user changed meanwhile is not applied.
- **Receipts.** An editor that removes the row is `rejected`; one that changes
  the row and then throws is `committed-with-error`.
- **Cost.** The window keeps a digest of each field path's value per row. A
  publication compares only the rows its changes carry; a reset of the
  collection or a full state compares every row. `[RunicRowEditor]` does not
  apply to query collections yet (`RUNICBRIDGE050`).
- **Validation.** A field's `errors` are the `validation` messages at its path
  and below: a cell's path names its row by index, such as
  `["lines", 2, "quantity"]`, which the generated descriptor finds from the row's
  key. Rows report their messages as any DTO does: an `INotifyDataErrorInfo`
  row (an immutable record whose `GetErrors` reads its own values), or a
  `RunicValidationMessage` of the ViewModel with the member path
  `Lines[2].Quantity`. A nested `ReactiveValidationObject` reports its rules at
  its members' paths, such as `["address", "street"]`.
- **Tests.** `RunicViewDriver.WritePathAsync(vm => vm.Lines, ["7", "quantity"], 3)`
  writes a cell as the page does; pass `expectedVersion` to write from an older
  state and observe a `RunicFieldWriteReceiptKind.Conflict`.

The [invoice grid example](../../../examples/invoice-grid/README.md) edits an
address and the cells of an invoice's lines this way.

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

## Commands scoped to their page

A wizard's Next, clicked twice before the second page arrived, must not skip a
page. Mark such a command `[RunicPresentationScoped]` (since 0.15), on the
command property or on its CommunityToolkit `[RelayCommand]` method, and renew
the presentation when the command changes the page:

```csharp
public sealed partial class SetupViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int Step { get; private set; } = 1;

    [RelayCommand]
    [RunicPresentationScoped]
    private void Next()
    {
        Step++;
        RunicPresentation.Renew(this);
    }
}
```

The page sends a scoped command with the epoch of the state it showed when the
user clicked, and the Bridge runs it only while that epoch is current. The
second Next was clicked on the first page, whose epoch `Renew` ended: it does
not run, and its call resolves with the current state, without an error, as if
it had run.

- **Epochs.** An epoch begins with a route's first state, so content that is
  presented again, a new page in a navigation region, has a new one without
  `Renew`. `RunicPresentation.Renew(viewModel)` begins one for every route that
  presents the ViewModel, in every window, from any thread: call it where the
  page changes without a new presentation, such as Next and Back, or after a
  cancelled dialog returned to its page. The route publishes the renewal as a
  full state with its next change or with the reply of the command that called
  it, and scoped calls are checked against the new epoch at once. A change of a
  content slot does not renew the slot's owner.
- **Order.** A superseded call is answered before its input is decoded, its key
  resolved or its availability checked, so a stale Back on the first page does
  not report `unavailable`.
- **Contract.** The generated client lists the commands in `scopedCommands`;
  adding or removing the attribute changes the View's fingerprint, and the
  attribute on any other member is warning `RUNICBRIDGE034`. Setters,
  checked writes and operation starts are not scoped. A page or host of an
  older release does not use the `presentation-epoch/1` feature and runs scoped
  commands as before.

## Waiting until the page shows a state

`RunicPresentation.WaitForPresentedAsync(viewModel, timeout, cancellationToken)`
(since 0.15) completes with `true` once a page rendered the state of
`viewModel` that includes everything changed before the call, for example
before a result page takes a screenshot or a window closes after showing a
summary:

```csharp
Page = result;
if (!await RunicPresentation.WaitForPresentedAsync(result, TimeSpan.FromSeconds(2)))
    logger.LogDebug("No page showed the result in time.");
```

It publishes what is pending first, as a call's reply does. A ViewModel that no
route presents yet, as `result` above before its parent published `Page`,
counts with the first state of the first route that presents it. The first page
that reports completes the wait; a hidden or minimized page reports after its
next timer. It returns `false` at the timeout, which runs on the window's
`TimeProvider`, also when no page uses the `presented/1` feature. Only waits ask
the page to report, so an idle or typing page sends nothing extra.

## Asking the page: view requests and questions

A ViewModel reaches its web View in two ways, in both MVVM flavors (since
0.16):

- a **view request**, `ViewRequest<T>`, is one-way: focus a field, scroll a row
  into view, scroll to the top, show a toast. The ViewModel raises it and
  carries on; nothing waits for the page.
- a **view question**, `ViewQuestion<TInput, TOutput>`, waits for the page's
  answer, such as a confirmation. It is the flavor-neutral counterpart of a
  ReactiveUI `Interaction<TInput, TOutput>`, so a CommunityToolkit ViewModel can
  ask the page too.

Expose both as get-only properties:

```csharp
public sealed record FocusTarget(string Field);
public sealed record DiscardRequest(string Title);

public sealed partial class EditorViewModel : ObservableObject
{
    public ViewRequest<FocusTarget> Focus { get; } = new(ViewRequestDelivery.NextMounted);
    public ViewRequest<string> Toast { get; } = new(ViewRequestDelivery.Broadcast);
    public ViewQuestion<DiscardRequest, bool> ConfirmDiscard { get; } = new((_, _) => ValueTask.FromResult(false));

    [RelayCommand]
    private void AddItem()
    {
        var item = new Item();
        Items.Add(item);
        Focus.Raise(new FocusTarget($"item-{item.Id}"));
    }

    [RelayCommand]
    private async Task DiscardAsync(CancellationToken token)
    {
        if (await ConfirmDiscard.AskAsync(new DiscardRequest(Title), token)) Reset();
    }
}
```

The page handles them with the generated client, while the component that
owns the field is mounted:

```ts
const stopFocus = editor.requests.focus.handle(({ field }) => document.getElementById(field)?.focus());
const stopConfirm = editor.interactions.confirmDiscard.handle(({ title }, { signal }) => confirmDialog(`Discard ${title}?`, signal));
```

`Raise` publishes what the ViewModel changed before it, and the page runs the
handler once it has rendered that state, so the new item's element exists
when `focus` runs. A request is not state: a snapshot or a reconnect never
repeats it, it needs no reset, and it never reaches another window.
`Drop` and `NextMounted` requests that a command raises reach only the page
that invoked it; a `Broadcast` reaches every connection of the window, as a
state change does. Each request declares where it goes with `ViewRequestDelivery`:

| Delivery | Inside a command a page invoked | Elsewhere (window scope, timer, startup) | With no such presentation, or several |
| --- | --- | --- | --- |
| `Drop` (default) | that page's presentation that handles it | the window's one presentation that handles it | dropped, logged at Debug as event 1120 |
| `NextMounted` | the same | the same | kept, the latest one wins, for the next presentation of the window that handles it (event 1121) |
| `Broadcast` | every presentation of the window that handles it, on every connection | the same | dropped |

`Drop` is the default because most requests only make sense for what the
user is looking at now: a focus or a scroll that arrives after the user moved
on is worse than none. Use `NextMounted` for a request about a page that is
about to appear, such as focusing the first field of an item that a command
opens. A request raised outside a command for a ViewModel that several windows
present is dropped. A `NextMounted` request a command keeps waits for that
page and is gone when its connection closes, also when the command raised it
on a ViewModel that nothing presents yet, such as an item it opens. One raised
outside any scope before any window presents the ViewModel waits for the
first window that does; one raised in a window's scope waits for that window. "The one
presentation" counts every route of the window that presents the ViewModel,
so a ViewModel shown in two panes has two. `Raise` never waits: outside the
model's turn it publishes and sends in the next turn. An input that does not fit the window's input limit makes
`Raise` throw `ArgumentException`.

`AskAsync` asks the page in the same scopes as an interaction (see
[Leave confirmation in a web View](../Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view)
for the table). When no page can answer it runs the fallback, or fails with
`RunicInteractionUnhandledException` when there is none. The page's
cancellation, its presentation closing, a timeout (by default two minutes, at
most ten, set with the constructor's `timeout`) and the caller's token fail it
as interactions do.

**Work that no page started.** A timer, a notification's `Activated` handler
or other background work of a window enters the window scope explicitly, so
its questions and interactions reach the window's one presentation that
handles them, as during a close decision:

```csharp
using (RunicInteractionInvocation.EnterWindow(viewModel))
    if (await ConfirmReload.AskAsync(path)) await ReloadAsync();
```

`EnterWindow` finds the one window that presents the ViewModel and enters no
scope when none or several do, or when a command that a page of that window
invoked is current: that command keeps reaching only its own page.
`TryEnterWindow(viewModel, out var scope)` returns whether it entered the
scope. It never carries a page's identity: only a command that a page's
transport delivered can reach that page's presentation alone. A menu item a
page clicks in its in-page menu bar runs as that page's command; one that the
native bar or a host shortcut runs is in the window scope
([Menu bar commands](#menu-bar-commands)).

**Fallbacks that no longer answer (since 0.16).** A .NET handler or fallback
that used to answer a question asked by a command on another route of the
window, or by a menu-bar command, is now skipped while a page has a handler
mounted for it, also a 0.15 page on a 0.16 host, since interaction handlers
are not a new feature. Remove such a fallback, or keep it for when no page is
mounted.

Pages and hosts of earlier releases keep working: the producer sends requests
only to pages that list the protocol feature `view-requests/1`, and a page of
this release registers no request handlers with an earlier host. Tests answer and
collect both through `RunicViewDriver.MountPresentation()`; see
[Runic.Application.Testing](../Runic.Application.Testing/README.md#interactions).

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

- A `[RunicCollection(nameof(Row.Id))]` list (the key a `string`, `Guid`, `int` or
  `long` row property) publishes only the rows that were
  added, removed, moved or replaced, and a row's own property change replaces
  that row. Use it for any list that can grow beyond a few dozen rows or
  changes often.
- Every other member is compared: a publication unit encodes it and sends it
  whole when the encoding differs from what the page has. The generator reports
  an observable collection of DTO rows that is not a `[RunicCollection]` as
  `RUNICBRIDGE032`.
- A list too large to hold or send, such as a database table, is a
  [query collection](#query-collections): each page loads only the rows it shows.

A notification requests a publication unit, and a route starts its units at
least 8 ms apart: the first change after a pause is published at once, and the
changes of the next 8 ms follow together in one frame once the interval has
passed, so the last change is always published. A model that raises thousands
of notifications a second, such as progress or a live counter, therefore sends
at most about 125 frames a second per route. A setter, command or checked write
is not delayed: it publishes its change, with any change that waits for the
interval, before it replies. Wrap a bulk change in a `BridgeSnapshotBatch`
(below) to publish it as one frame when the batch ends.

## Query collections

A list too large to send, such as 500,000 rows of a database table, stays where it is: expose it as a
`RunicQueryCollection<TRow, TKey>` over a row source, and each page loads only the rows it shows. The page sorts,
filters and scrolls it; the ViewModel needs no viewport commands.

```csharp
public sealed record Order(int Id, string Customer, decimal Amount, DateTime Placed);

public sealed class OrdersViewModel : ObservableObject
{
    public OrdersViewModel(Func<ShopContext> database, IRunicModelContext context)
    {
        var fields = new RunicQueryFields<Order>()
            .AddText("customer", order => order.Customer)
            .Add("amount", order => order.Amount)
            .Add("placed", order => order.Placed)
            .AddCustom("mine", (orders, _) => orders.Where(order => order.Customer == Environment.UserName));
        Orders = new(new RunicQueryableRowSource<Order, int>(() => database().Orders, fields, order => order.Id),
            order => order.Id, new RunicQueryCollectionOptions { ModelContext = context });
    }

    /// <summary>Every order, as the page sorts and filters it.</summary>
    public RunicQueryCollection<Order, int> Orders { get; }
}
```

- **State.** The property's member in the page's state is `{ query, total, version }`: the sort and filter, the number
  of matching rows (`null` until counted, and again after a query change until a page reads rows of the new query; a
  refresh keeps the old total until its new count), and a version that increases when loaded rows may be out of date. The rows
  are not state. Each page reads the rows of its range with the generated client's `queries.orders.setRange(start,
  count)`, and .NET answers that page alone (`query-collections/1`, [protocol](../../../specs/application/README.md#query-collections)).
  Two CS-WebUI clients or two windows that scroll the same list therefore don't move each other's rows, and a page that
  scrolls far sends only its own range.
- **Query.** `RunicQuery` is a list of sort keys and filters: `equals`, `contains`, `range` and `custom`, the escape
  hatch for a filter the typed model lacks (a full-text search, "rows I own"). The query is ViewModel state that every
  presentation shares, like any other member: the page changes it with `queries.orders.setQuery(...)`, .NET with
  `SetQuery`, and the ViewModel reads `Orders.Query` (to export the filtered rows, or to show the filter in a title).
  A sort one page sets shows on every page of the ViewModel. Give each presentation its own ViewModel for independent
  queries.
- **Row source.** `IRunicRowSource<TRow, TKey>` has `CountAsync`, `LoadAsync(query, range)`, which may also return the
  total, as from `COUNT(*) OVER ()`, `IndexOfAsync(query, key)` and `Validate(query)`. `RunicQueryableRowSource` runs
  a query over any `IQueryable<T>`: an in-memory list (`rows.AsQueryable()`) or a database table through Entity
  Framework Core or another LINQ provider. `RunicQueryFields<TRow>` lists the fields a page may use and maps them to
  `Where` and `OrderBy` expressions that the provider translates; a query that names another field, an unknown filter
  operator, or a value the field cannot read is rejected as invalid input, and never becomes the collection's query.
  A source of your own implements `Validate`: the page's field names, custom filter names and values are untrusted
  input, so accept only what the source supports by name. A `custom` filter's delegate also receives untrusted JSON;
  `Validate` runs it once over no rows, so an argument it rejects is invalid input when the page sets the query. A source with asynchronous LINQ (`ToListAsync`) or an HTTP API implements the interface itself with
  `fields.Apply(...)`. The collection orders by the row key last, so a range is stable.
- **Loading.** The collection reads the source in pages of `PageSize` rows (100), never in a model turn. Loads, counts
  and key searches share `MaximumConcurrentLoads` (4) slots, so no burst of requests runs more source calls at once.
  Requests that need the same page share its load, a load that no request waits for any more is cancelled (a page that
  scrolled past it), a failed page is not kept, and at most `MaximumCachedPages` (64) pages stay cached, least recently
  used first. A query change cancels the count and key searches of the old query, and the new query is counted when a
  page next reads rows, so a burst of query changes counts the last one. A range at or past a counted total reads
  nothing. Memory and traffic therefore stay bounded whatever the list's length:
  `QueryCollectionTests.ScrollingHalfAMillionRowsKeepsMemoryAndTrafficBoundedAsync` scrolls 500,000 rows with two pages
  at once, with at most 16 cached pages, about 1.9 KB per 40-row reply and no row in any frame. `Statistics` reports
  what was loaded.
- **Fairness.** The Bridge admits per connection and collection 2 `query-rows`, 1 `query-set` and 1 `query-index` in
  flight, and answers more `busy` at once, so one page cannot take every slot of the source from another; the page's
  client retries them after a short delay.
- **Cancellation of synchronous LINQ.** `RunicQueryableRowSource` checks cancellation between the rows it reads, so a
  cancelled load or search frees its slot at its next row. A synchronous provider cannot interrupt a call before its first
  row, such as a `Count()` or a slow query plan: that call completes in the background, in its slot. A source with
  asynchronous LINQ (`CountAsync`, `ToListAsync` with the token) is interrupted at once.
- **Changes.** Call `Invalidate(key)` after a row changed in place: the cached page that holds it is discarded and the
  version raised, so pages load their range again, mostly from the cache. Call `Refresh()` after rows were added or
  removed, which moves the rows after them: every page and the count are taken again. `SetQuery` does the same for a
  new query. The generated Bridge binds the ViewModel's model context to the collection, so these changes are published in
  its turns. A collection used without a Bridge raises them in its `ModelContext`, or the `SynchronizationContext` it was
  created on (a WPF View's UI thread); without either, it logs event 1193 once and raises them where they happen.
- **Scroll to a key.** A page's `scrollToKey(key)` asks .NET for the row's position in the current query
  (`IndexOfAsync`, a `query-index` request) and scrolls there, for example to bring a search result into view.
- **WPF.** A WPF View binds `Orders.CreateView()` as an `ItemsControl`'s `ItemsSource` with a virtualizing panel: the
  list has `Total` items, loads the pages its panel reads, shows `null` for a row until its page arrives, and resets when
  the version changes. Each View creates its own, as each page has its own range.
- **Tests.** `RunicViewDriver.QueryRowsAsync`, `SetQueryAsync` and `IndexOfAsync` send what a page sends, also on a second
  connection, and `RunicTestRowSource` serves rows with latency and records every load, count and search.

The generator reports a query collection with a setter, `[RunicCollection]` or `[RunicAppendOnlyText]`, a key other than
`string`, `Guid` or `Int32`, or rows that are not DTOs as `RUNICBRIDGE060`.

### DynamicData

The core has no DynamicData dependency. A `SourceCache` is a row source through `RunicQueryableRowSource` over
`cache.Items.AsQueryable()`, and its changes invalidate the collection:

```csharp
_changes = cache.Connect().Subscribe(changes =>
{
    // Adds and removes move the rows after them; updates stay where they are.
    if (changes.Adds > 0 || changes.Removes > 0 || changes.Moves > 0) Rows.Refresh();
    else Rows.Invalidate(changes.Select(change => change.Key));
});
```

The [DynamicData example](../../../examples/dynamicdata) shows both: `SortAndVirtualize` over a cache, and a 100,000-row
query collection with sort, filter and variable row heights.

### Trees

A tree keeps row changes when it is flattened: publish the visible rows, each with its depth, parent key and whether it
is expanded, in a keyed collection or a query collection, and expand or collapse by inserting or removing the children's
rows. The page then renders a flat list with indentation, and an expand sends one `add` frame of the children instead of
a new tree.

```csharp
public sealed record TreeRow(string Key, string? ParentKey, int Depth, string Label, bool HasChildren, bool Expanded);

public sealed partial class FolderTreeViewModel(IFolders folders) : ObservableObject
{
    private readonly ObservableCollection<TreeRow> _rows = [];

    [RunicCollection(nameof(TreeRow.Key))]
    public ReadOnlyObservableCollection<TreeRow> Rows => field ??= new(_rows);

    // The page sends the row's key; Row resolves it.
    [RelayCommand, RunicCommandKey(nameof(Row))]
    private void Toggle(TreeRow row)
    {
        var index = _rows.IndexOf(row);
        if (row.Expanded) Collapse(index);
        else Expand(index, [.. folders.Children(row.Key).Select(child =>
            new TreeRow(child.Key, row.Key, row.Depth + 1, child.Name, child.HasChildren, Expanded: false))]);
    }

    private TreeRow? Row(string key) => _rows.FirstOrDefault(row => row.Key == key);

    private void Expand(int index, IReadOnlyList<TreeRow> children)
    {
        _rows[index] = _rows[index] with { Expanded = true };
        for (var offset = 0; offset < children.Count; offset++) _rows.Insert(index + 1 + offset, children[offset]);
    }

    // The row's descendants are the rows after it that are deeper.
    private void Collapse(int index)
    {
        _rows[index] = _rows[index] with { Expanded = false };
        while (index + 1 < _rows.Count && _rows[index + 1].Depth > _rows[index].Depth) _rows.RemoveAt(index + 1);
    }
}
```

For a tree too large to publish, use a query collection whose row source returns the visible rows of the tree in
depth-first order: keep the expanded keys in the ViewModel, let the source filter rows whose ancestors are all expanded
(a `custom` filter or a `path` column), and call `Refresh()` after an expand or collapse.

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

### Field drafts before departures

A web page may hold what the user typed for a moment before it writes it, such
as a field with `commit: "pause"`. With `field-flush/1` (0.16 hosts and pages),
the window asks its pages to write those drafts before it asks a departure guard:
before the guards of a navigation of the window's model context
(`RunicDraftFlush`, which the navigator awaits), and before the close guard of a
Window with `GuardClose`, from `RequestCloseAsync` (also inside a model turn, such
as a native menu's command) or the close button. It asks only its own pages,
waits up to 1 second (event 1140 when a page does not answer) and then decides
on the ViewModel's state. A disposed window is asked nothing, and a page that a
push cannot reach does not delay the decision. In tests, set
`InMemoryBridgeTransport.FieldFlush` to write what a page would; the mock bridge
has `flushFields()`.

### Validation in progress

An asynchronous rule, such as one that asks a server whether a user name is
taken, can report that it is still checking. Implement `IRunicPendingValidation`
on the ViewModel, or on a DTO of its state, usually by delegating to a
`RunicPendingValidation`. The Bridge then reports the pending member paths in
`validation.pending` (the `validation-pending/1` feature), and the page's field
shows `validating`. Paths are relative CLR property paths, as in
`RunicValidationMessage.MemberPaths`, and `""` is the object itself. A pending
path is not an error: it does not set `hasErrors`.

```csharp
public sealed partial class SignUpViewModel : ObservableValidator, IRunicPendingValidation
{
    public RunicPendingValidation Pending { get; } = new();
    IReadOnlyCollection<string> IRunicPendingValidation.PendingMemberPaths => Pending.PendingMemberPaths;
    event EventHandler? IRunicPendingValidation.PendingChanged { add => Pending.PendingChanged += value; remove => Pending.PendingChanged -= value; }

    [ObservableProperty, CustomValidation(typeof(SignUpViewModel), nameof(ValidateUserName))]
    public partial string UserName { get; set; } = "";

    // CommunityToolkit: the check runs while the name is pending. Each check cancels the previous one of the
    // same path, so the result of an older name never overwrites that of a newer one.
    partial void OnUserNameChanged(string value) => _ = Pending.Track(nameof(UserName), async cancellationToken =>
    {
        var taken = await _users.ExistsAsync(value, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _taken = taken;
        ValidateProperty(UserName, nameof(UserName));
    });
}
```

With ReactiveUI.Validation, `TrackPending` follows a rule's state stream and
marks the member pending while the state is the one the rule starts each check
with:

```csharp
var checking = new ValidationState(true, "Checking…");
var states = ValidationAsync.ForLatest(this.WhenAnyValue(vm => vm.UserName), CheckUserNameAsync, checking, scheduler)
    .TrackPending(Pending, nameof(UserName), state => ReferenceEquals(state, checking));
this.ValidationRule(vm => vm.UserName, states);
```

`Track(path, cancellationToken => …)` cancels the previous check that it
started for the path, and a check that ignores its token still does not
complete after a newer one: its task is cancelled. `Track(path, () => …)`
does not cancel; overlapping checks of a path are counted, as with
`Begin(path)`, which returns a lease for any other check. `validation.pending`
lists the paths sorted; when the limits cut it, `pendingTruncated` is set. WPF binds `Pending[UserName]` or `Pending.HasPending`. In
tests, `RunicViewState.IsValidating(vm => vm.UserName)` and
`PendingValidation` read the projection.

## Translated text

Text that a ViewModel produces, such as a status line or a validation
message, can follow the window's language without a call per string. The
ViewModel produces a text reference of
[Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.8.0/docs/guides/translations/transport.md)
(a message key, typed arguments and a fallback text) instead of a sentence, and
the page formats it with its Translations runtime (the `translation-text/1`
feature). When the language changes, the page formats the references it has
again.

**Values.** A member, DTO member, command input or result of type
`Runic.Translations.TranslationReference` crosses the Bridge as the reference's
transport JSON, and is a `TextReference` in TypeScript. The Views packages do
not depend on Runic Translations: the generator recognizes the type by name, and
the generated code, compiled into your application, calls its serializer. Add
the `Runic.Translations` package and the generated catalog to the .NET project
as the [Translations guide](https://github.com/Runic-Artifex/runic-translations-sdk/blob/v0.8.0/docs/guides/translations/runic-desktop.md#strings-outside-the-views)
shows. `RunicTextReference` holds a reference's JSON where Views code needs one:

```csharp
public partial class EditorViewModel : ObservableObject
{
    [ObservableProperty]
    public partial TranslationReference Status { get; set; } = NotesText.Ready();

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _store.SaveAsync(Title);
        Status = NotesText.Saved(Title);
    }
}

// NotesText.Saved: a reference with a fallback for a page that cannot translate it.
public static TranslationReference Saved(string title) => new(NotesTextCatalog.CatalogId,
    NotesTextCatalog.ContractFingerprint, "saved",
    new Dictionary<string, TranslationReferenceArgument> { ["title"] = new(TextArgumentType.String, title) },
    $"Saved {title}.");
```

**Validation messages.** Give a validation message a reference, and the page
shows the error's `text` in the window's language, with the message as the
fallback. `{name}Errors` keeps the messages.

- `RunicValidationMessage`: set `Text = RunicTextReference.Create(reference,
  TranslationReferenceJsonContext.Default.TranslationReference)`.
- DataAnnotations and CommunityToolkit `ObservableValidator`: return a
  `RunicValidationResult(message, text, memberNames)` from a
  `[CustomValidation]` method or a `ValidationAttribute`. The built-in
  attributes have no reference; their messages stay as they are.
- ReactiveUI.Validation: pass a rule a state whose `IValidationText` also
  implements `IRunicTextReferenceSource`, or add a component that implements
  it:

  ```csharp
  sealed class TranslatedText(TranslationReference reference) : IValidationText, IRunicTextReferenceSource
  {
      private readonly string _message = reference.FallbackText ?? reference.Key;
      public RunicTextReference? Text { get; } =
          RunicTextReference.Create(reference, TranslationReferenceJsonContext.Default.TranslationReference);
      public int Count => 1;
      public string this[int index] => _message;
      public IEnumerator<string> GetEnumerator() { yield return _message; }
      IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
      public string ToSingleLine(string? separator) => _message;
  }

  this.ValidationRule(vm => vm.Title, this.WhenAnyValue(vm => vm.Title).Select(title => title.Length <= 80
      ? ValidationState.Valid : new ValidationState(false, new TranslatedText(NotesText.TitleTooLong(80)))));
  ```

- Any other entry of `GetErrors` can implement `IRunicTextReferenceSource`.

**The window's language.** The application owns the window's locale: take
`IRunicWindowLocale` in the ViewModel's constructor and set `Locale`, for
example from the application's settings, before the window opens, and again
from a language switch command. A language switch on the page must call that
command: a page that sets its Translations runtime's locale itself is set back
to the window's locale by the next push, at the latest when it reconnects. The window's pages follow it: each sets the
locale of its Translations runtime, so the page's own messages switch too, and
formats the references again. `null`, the default, leaves each page in its own
language. A Window that the application creates itself sets
`WindowContentSession.Locale`. The service raises `PropertyChanged`, so C# text,
such as a tray menu, can follow with the generated C# catalog's
`ITranslationManager`.

```csharp
public partial class ShellViewModel(IRunicWindowLocale locale, AppSettings settings) : ObservableObject
{
    public void Initialize() => locale.Locale = settings.Language;   // "de", "pt-BR" or null

    [RelayCommand]
    private void SwitchLanguage(string language)
    {
        locale.Locale = language;
        settings.Language = language;
    }
}
```

**The page.** Connect the page's Translations runtime once, in `main.ts`, with
`configureTranslatedText` from `@runic-artifex/views`. With the generated ESM of
Runic Translations and its Vite plugin:

```ts
import { configureTranslatedText } from "@runic-artifex/views";
import { m } from "virtual:runic-translations/app/messages";
import { configureLocaleResolver, createLocaleSource } from "virtual:runic-translations/app/runtime";
import { resolveTextReference } from "virtual:runic-translations/app/transport";

const locale = createLocaleSource({ initialLocale: navigator.language });
configureLocaleResolver(locale.getLocale);
configureTranslatedText({
  ...locale,
  resolve: (reference, language) => resolveTextReference(reference, m, { locale: language }).text,
});
```

Then show references with the framework's helper. Field errors
(`useField(...).errors`, `injectField`, `createFieldController`) already show
validation messages in the window's language. A `number`, `decimal`,
`bigint`, `date`, `time` or `datetime` field without its own `locale` option
shows and reads its text in the window's locale (`1,5`, `03.11.2026, 21:30` in
`de`; `11/03/2026, 9:30 PM` in `en-US`), and formats it again when the locale
changes, but not while the user edits a draft. Dates and times also accept the
ISO form, and the ViewModel still receives the invariant and ISO values. Use a
text input for such a field (`inputmode="decimal"` for numbers):
`<input type="number">`, `type="date"`, `type="time"` and `datetime-local` take
only the invariant and ISO forms, so a field bound to one of them takes
`locale: null`.

**Server rendering.** `configureTranslatedText` configures the page's runtime,
which is shared by everything on the page. Under SvelteKit or Angular server
rendering, call it in the browser only (in `onMount`, a `+layout.ts` with
`export const ssr = false`, or behind `isPlatformBrowser`), or render on the
server with `resolveTextReference(reference, m, { locale })` and the request's
locale instead of the shared configuration. The server has no window locale.

| Framework | Status line | Validation error |
| --- | --- | --- |
| React | `useText(view.state?.status)` | `useText(error.text, error.message)` |
| Vue | `useText(() => view.state.value?.status)` | `useText(() => error.text, () => error.message)` |
| Svelte | `useText(() => view.state?.status).current` | `useText(() => error.text, () => error.message).current` |
| Angular | `{{ state.status \| runicText }}` or `injectText(() => …)` | `{{ error.text \| runicText: error.message }}` |
| Effect | `texts(state.status)` | `texts(error.text, error.message)` |
| None | `resolveText(state.status)` with `subscribeText` | `resolveText(error.text, error.message)` |

A reference that the page cannot translate, such as one of another catalog,
whose arguments no longer match, or of a later Translations transport version
than the page's runtime reads, shows its fallback text. Without a configured
runtime, every reference shows its fallback text. The text is plain: render it
as text, never as HTML. `useWindowLocale()` (`injectWindowLocale()`,
`windowLocale()`) reads the window's locale, for example for `lang`.

**Tests.** `RunicViewState.Read(vm => vm.Status)` reads a `TranslationReference`
or `RunicTextReference` member, and `RunicWindowTestHost.Create(services)` binds
`IRunicWindowLocale` of the services' scope to the window, so
`host.Content.Locale` shows what the ViewModel set. In page tests, the mock
bridge's `setWindowLocale("de")` pushes a locale as the application does.

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
Binary data does not need a larger limit: a `RunicBlob` crosses as a URL and a
`RunicUpload` streams over HTTP, outside the Bridge (see
[Blobs and uploads](#blobs-and-uploads)), so a `byte[]` member, which crosses
as a JSON array of numbers, is only for small values.
The WPF host uses the default. Independently of the limit, exact number strings
(`long`, `ulong`, `BigInteger`, `decimal`) are at most 4 096 characters, and a
window runs at most 64 awaited command calls at once.

## Blobs and uploads

Images, thumbnails and downloads that a ViewModel produces, and files the user
picks in the page, cross as `RunicBlob` and `RunicUpload` (Views protocol
feature `blobs/1`, `specs/application/README.md`, "Blobs and uploads"). Their
bytes never cross the Bridge as JSON, so neither the input limit nor JSON
encoding applies, and a large file streams instead of being buffered.

**A blob for the page.** Expose a `RunicBlob` as a state property, in a DTO or
collection row, or as a command or operation result:

```csharp
public sealed partial class ProfileViewModel : ObservableObject
{
    [ObservableProperty]
    public partial RunicBlob? Avatar { get; private set; }

    public void ShowAvatar(string path) => Avatar = RunicBlob.FromFile(path, "image/png", "avatar.png");

    [RelayCommand]
    private Task<RunicBlob> ExportAsync() => Task.FromResult(RunicBlob.FromStream(
        token => ValueTask.FromResult<Stream>(File.OpenRead(_reportPath)), "text/csv", fileName: "report.csv",
        disposition: RunicBlobDisposition.Attachment));
}
```

The page receives `{url, type, size, name}` and uses `url` directly:

```tsx
{state.avatar && <img src={state.avatar.url} alt="" />}
<a href={report.url} download={report.name ?? undefined}>Download</a>
```

- `FromBytes` keeps bytes in memory, `FromFile` opens the file for each request
  and `FromStream` calls its function for each request; the host streams the
  content and answers byte ranges of a seekable stream, which `<video>` and
  `<audio>` need. Create a new `RunicBlob` for new content: a URL's content
  never changes, and the browser caches it as immutable.
- The URL is a capability of the Window: unguessable, and served only to the
  Window's page (on Runic Desktop and WPF only to the browser client that holds
  the Window's cookie).
- How long a URL works:
  - A blob in a state property, or in a DTO or list inside it: until the
    property holds another value. Set it to `null` or a new blob, and the old
    URL answers 404.
  - A blob in a row of a `[RunicCollection]`: until that row leaves the
    collection or is replaced by a row with another blob. Any number of rows
    stays served, so a list of 1,500 thumbnails keeps all 1,500.
  - A blob in a command or operation result, or in an interaction input: until
    its View is released (interactions: until the Window closes). A Window
    keeps at most 1,024 of these; beyond that the least recently sent ones are
    released, and event 1208 `BlobEvicted` is logged once per request with
    their number. Results the page still needs belong in state instead.
  - Any blob: until you call `blob.Release()`, which ends its URLs in every
    Window at once. Release a download or a thumbnail a long-lived View no
    longer shows. If the blob crosses the Bridge again, it gets a new URL.
  - Every blob: until the Window closes.
- The response carries the blob's `ContentType`, `X-Content-Type-Options:
  nosniff` and a sandboxing `Content-Security-Policy`, so an HTML or SVG blob
  opened in the Window runs no script in the application's origin.
  `Disposition` sets `Content-Disposition` (`inline` by default, `attachment`
  to download on navigation) with `FileName`.
- A property that holds a `RunicBlob` is read-only for the page, whatever its
  setter. A `RunicBlob` command input fails the build with RUNICBRIDGE065: the
  page sends files as uploads.

**An upload from the page.** A command takes a `RunicUpload`:

```csharp
[RelayCommand]
private async Task AttachAsync(RunicUpload file, CancellationToken cancellationToken)
{
    // Everything about an upload comes from the page: check what you read before you use it.
    await file.SaveAsAsync(Path.Combine(_attachments, Guid.NewGuid().ToString("N")), cancellationToken: cancellationToken);
    Attachments.Add(new Attachment(file.FileName, file.Length));
}
```

The page uploads the file over HTTP, then calls the command with the reference:

```ts
const file = input.files![0]!;
const ref = await client.upload(file, { signal, onProgress: ({ loaded, total }) => setProgress(loaded / total) });
await client.attach(ref);
```

- `client.upload` exists on generated clients whose contract takes a
  `RunicUpload`; `upload` from `@runic-artifex/views` does the same for any
  page. The content streams to the host with the connection's token; the
  Bridge carries only the reference.
- An upload decodes only for the Window and the page connection that uploaded
  it, and only once: the first `OpenReadAsync` (a seekable stream; disposing it
  deletes the content), `SaveAsAsync` (a rename on the same volume, otherwise a
  copy) or `Discard` claims it, and a later request that names it is rejected
  as invalid input. An unclaimed upload is deleted when its connection closes,
  when the Window closes, or after 10 minutes.
- `FileName`, `ContentType` and the content are untrusted. `FileName` is
  reduced to a file name without directories or control characters; never use
  it as a path.
- An upload whose body sends nothing for 30 seconds is dropped (HTTP 408), on
  every host.
- Limits: `RunicWindowOptions.MaximumUploadBytes` (per upload, default 256 MiB)
  and `MaximumWindowUploadBytes` (unclaimed uploads of a Window at once, default
  1 GiB), with host defaults `RunicDesktopHostOptions.MaximumUploadBytes` and
  `RunicCsWebUiHostOptions.MaximumUploadBytes`, and
  `WindowContentSessionOptions` for a session you create. The WPF host uses the
  defaults.
- Uploads are stored in a new folder of each Window, `runic-uploads-{random}`
  with mode 0700, below the temporary directory when no other user can change
  it (it is the user's own, or sticky such as `/tmp`), otherwise below
  `$XDG_CACHE_HOME/runic/uploads` (`~/.cache/runic/uploads`). The host reads an
  upload through the file it created, never through its path again, so a link
  put in its place is not followed. The folder is deleted when the Window
  closes; folders left by a crash are deleted by the next run once nothing
  changed them for a day.

| Host | Where blobs and uploads are served | Bound to |
| --- | --- | --- |
| Runic Desktop (Kestrel or the native server), WPF `RunicViewHost` | the Window's surface, `/_runic/blobs/` and `/_runic/upload`, same origin as the page | the Window and its browser client (cookie, and the launch code of an installed browser); an upload also to its connection |
| CS-WebUI | a loopback listener of each Window, `http://localhost:{port}/` (also bound on `127.0.0.1`; only `127.0.0.1` where `localhost` cannot be bound), with CORS for the page's origin, serving loopback peers only | the Window; an upload also to its connection. CS-WebUI's file dialogs are unavailable, so an upload is how a file gets in. A page `Content-Security-Policy` must allow `http://localhost:*` for `img-src`, `media-src` and `connect-src` |
| `Runic.Application.Testing` | in memory, `http://runic.test/` | `RunicWindowTestHost.UploadAsync` and `ReadBlobAsync` |

In tests, `RunicBlob.FromBytes` makes blobs, `RunicUpload.FromBytes` makes an
upload for a ViewModel without a Window, `RunicWindowTestHost.UploadAsync`
simulates a page's upload that a command then takes, and
`RunicWindowTestHost.ReadBlobAsync(url)` returns what the host serves for a URL
from the state: status, headers and content. On the page side,
`@runic-artifex/views/mock` grants uploads (`MockBridge.uploads`,
`uploadContent`, `failNextUpload`) and `mockBlob(content)` makes a `RunicBlob`
whose URL loads in a test.

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
a veto. This tab set is static: each tab is a slot property. For tabs that
open and close at run time, use a region collection.

### Region collections

Since 0.16, mark a get-only `NavigationSelection` property with
`[RunicRegionCollection(typeof(TContent))]` to present every region of the
selection, such as document tabs that each keep their own back stack. The
ViewModel adds regions with `Add`; the page selects and closes them by key
(feature `region-collections/1`):

```csharp
public sealed partial class EditorWindowViewModel(RunicNavigator navigator) : ObservableObject
{
    [RunicRegionCollection(typeof(IDocumentPage))]
    public NavigationSelection Documents { get; } = navigator.CreateSelection(this, []);

    [RelayCommand]
    private async Task<NavigationOutcome> OpenAsync(string path)
    {
        var tab = navigator.CreateRegion<IDocumentPage>(this, NavigationTarget.Create<DocumentViewModel, string>(path));
        Documents.Add(tab);
        return (await Documents.SelectAsync(tab)).Outcome;
    }
}
```

The generated state member and client methods:

```ts
readonly documents: RegionCollection<DocumentPageReference | DocumentInfoPageReference>;
// { regions: readonly { key: string; content: R | null }[]; selected: number | null }
selectDocuments(key: string): Promise<RegionOutcome>;
closeDocuments(key: string): Promise<RegionOutcome>;
```

- Each region is `{ key, content }`. `key` stays the same while the region
  exists and is never reused in the window, so frameworks keep each tab's
  component by it. `content` is the region's `Current`, or `null` while the
  region is empty; a push inside a tab changes `content` and keeps `key`.
- `selected` is the index of `Selected`, or `null` without regions. It changes
  only when a switch commits.
- `select{Name}(key)` calls `SelectAsync`, so the current page of the tab left
  is asked as `Retain`. `close{Name}(key)` calls `RemoveAsync`, so every page of
  the tab is asked as `Retire`; a committed close selects the next tab. Both
  resolve with the request's `NavigationOutcome` once the state includes it. A
  veto resolves too, with `kind: "Rejected"` and `rejection: "Guard"`.
- A key of a tab that closed meanwhile rejects with reason `unresolved-key`.
  Keys are unique in the collection and never reused for another of its
  regions; two collections can share a key.
- **Concurrent requests.** A close of a tab whose close is still pending,
  from the same page or another one, joins it: the guards are asked once, and
  every close resolves with the same outcome. A select while a close is
  pending is a later request of the selection, so it supersedes the close
  (`NavigationAdmission.Supersede`): the close resolves `Superseded`, its
  guards' cancellation token is cancelled, so a guard that shows a prompt
  should close it, and the tab stays. A guard that ignores the token holds the
  select until it returns.
- `select{Name}` and `close{Name}` wait for the connection's `hello` after a
  reconnect, and carry no presentation epoch: the key names the tab, whatever
  state the page showed.
- `PageCanClose = false` generates no `close{Name}`; a page's close is then
  `unavailable`. Close such tabs from the ViewModel.
- Content that no region presents any more is pruned, like the items of a
  ViewModel collection.
- **Window close.** A guarded window close (`GuardClose`) removes the
  collection's tabs one by one with `RemoveAsync`. The first tab that refuses
  keeps the window open; the tabs before it are closed, not left empty. To ask
  every tab in one decision, own the tabs and the selection by a page entry in
  a region slot: the close's clear of that slot retires every tab in one
  transition, and closes none when one refuses.
- **Restart (since 0.16).** Keys don't survive a restart. A saved
  [navigation path](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#routes-saved-paths-and-deep-links)
  records the tabs by position, each with its own back stack, and the selected
  tab. `RestoreAsync` creates the tabs it needs with
  `NavigationSelectionOptions.RestoreRegion`, restores their pages and adds them.
- Without the attribute, a `NavigationSelection` stays a selection slot whose
  value is `SelectedIndex`. Only Views with a region collection get a new
  contract fingerprint.
- `RUNICBRIDGE070` reports the attribute on another property type, a setter,
  `typeof(object)` or a content type without a View. `RUNICBRIDGE071` reports a
  `select{Name}` or `close{Name}` method that collides with another client
  member.

Each framework adapter renders the regions with `RegionOutlets`, one
`ViewOutlet` per region keyed by `key`. By default it keeps every region
mounted and hides all but the selected one (`hidden` and an inline
`display: none`), which keeps each tab's DOM. In both modes the Bridge
presents every tab's content, so every tab's route stays live and keeps
receiving state; `mode: "selected"` only unmounts the hidden components.
`panelProps(entry)` adds attributes to each panel, such as `role="tabpanel"`
and an `id` for the tab's `aria-controls`; the adapters' READMEs show a tab
bar with roles and focus handling. A tab bar uses `regionEntries(state.documents)`:

```tsx
<nav>
  {regionEntries(state.documents).map(tab => (
    <span key={tab.key}>
      <button aria-pressed={tab.selected} onClick={() => void view.selectDocuments(tab.key)}>Tab {tab.index + 1}</button>
      <button onClick={() => void view.closeDocuments(tab.key)}>×</button>
    </span>
  ))}
</nav>
<RegionOutlets collection={state.documents} registry={pages} />
```

In tests, `state.Regions(vm => vm.Documents)` reads the collection, and
`driver.SelectRegionAsync(vm => vm.Documents, key)` and `CloseRegionAsync`
send the page's requests; the reply's `RegionOutcome` is the outcome. The
[document tabs example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/document-tabs)
uses all of it.

### Back and the URL

With Views protocol 4's `navigation-path/1` (since 0.16), a page goes back in
its window as a Back button does, and can keep its URL in step with the
window's navigation, on Runic Desktop and CS-WebUI alike.

**Back.** Call `installBackNavigation()` once in the page. The mouse's Back
button, Alt+Left, Cmd+[ and the keyboard's Browser Back key then run
`RunicNavigator.BackAsync()` for the window's navigator: in the innermost
presented region that can go back, such as the selected tab before its page,
through the departure guards. Only regions a Bridge of the window presents
count, as a content slot or a tab of a presented region collection. Alt+Left and Cmd+[ stay with text fields, and an
event the page already handled is left alone. `navigateBack()` does the same
from code. The .NET side needs nothing beyond its navigator.

**URL.** Choose the window's path region, usually its main region, and give
its pages routes ([routes](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Navigation/README.md#routes-saved-paths-and-deep-links)):

```csharp
public ShellViewModel(RunicNavigator navigator)
{
    Main = navigator.CreateRegion<IPage>(this, NavigationTarget.Create<CustomerListViewModel>());
    _path = RunicPresentation.SyncNavigationPath(Main); // uses the registered INavigationRouteMap
}
```

```ts
import { installBackNavigation, installNavigationPath } from "@runic-artifex/views";
installNavigationPath(); // #/customers/42 follows Main; Back, Forward and the address bar open their paths
```

- The route of `Main`'s current page is the window's route path. When it
  changes, the page pushes a history entry, goes back to the previous one, or
  replaces the current one, so the URL reads `#/customers/42/invoices/7`.
- The browser's Back and Forward and an edited address bar open the URL's path
  with `NavigationRouteOpen.BackToOrPush`: back to that page when `Main`'s
  history has it, a push otherwise, through the departure guards. When the
  request doesn't commit, because a guard refused or no route matches, the URL
  goes back to the window's path.
- A URL with a path when the page loads, such as a bookmarked CS-WebUI address,
  opens that path as a deep link.
- On CS-WebUI the browser's own Back then works through the history; add
  `installBackNavigation()` for the mouse and keys in a Desktop window, whose
  web view may not map them. Both can be installed: the keys then go back in
  the page's history, so a Back runs once.
- The path region can be chosen after the page connected, or the window can
  learn its navigator later: the page is told the path then, and a link in its
  URL opens at that moment.
- Several pages of one window, such as two CS-WebUI browsers, share the path
  region: every page's history follows it, and the last change wins.
- One path region per window: a second `SyncNavigationPath` for the navigator
  throws until the first is disposed.
- Tests drive it with `RunicWindowTestHost.NavigateBackAsync()` and
  `OpenNavigationPathAsync(path)` (a browser's `popstate`), and read
  `NavigationPathPushes`.
- A page or host of an earlier release lists no common feature: the page's
  calls fail with a `failed` BridgeError, and nothing else changes.

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

### Page errors

An error in the page, such as a `TypeError` in a click handler or a promise
nothing awaited, stays in the web view's console unless the page reports it.
Call `installErrorReporting()` of `@runic-artifex/views` once when the page
starts, before the first View connects:

```ts
import { installErrorReporting } from "@runic-artifex/views";

installErrorReporting({
  // Optional: remove application data from messages before they leave the page.
  redact: report => ({ ...report, message: report.message.replace(/\d{12,19}/g, "[number]") }),
});
```

The page then reports uncaught errors (`window.onerror`), unhandled promise
rejections and the errors the Views runtime catches, such as a subscriber that
threw, to the .NET host. The host logs each one as event 1150 `PageError` in
the category `Runic.Application.Views.Page`
(`RunicViewsTelemetry.PageLogCategory`), with the stack last. That is where a
crash reporter or log forwarder that reads `ILogger` picks it up, beside the
host's own errors; filter or route the category on its own, for example
`builder.AddFilter(RunicViewsTelemetry.PageLogCategory, LogLevel.Error)`.

Reports are bounded and redacted before they leave the page: the message is
cut at 1024 characters and the stack at 30 lines and 4096 characters, every
URL loses its query and fragment, and `redact` can change or drop a report. At
most 10 reports go out per minute (`maxReportsPerMinute`); repeats of one error
within the minute are counted, and the count arrives as `Dropped`. The host
treats a report as untrusted input: it cuts texts again (message and URL at
2048 characters, stack at 8192, never between the halves of a surrogate pair),
removes control characters and the bidirectional embeddings, overrides and
isolates (U+202A-U+202E, U+2066-U+2069), and starts every stack line with
`    | `, so in a plain-text sink a page's text stays inside its entry and
cannot pass for a line the host wrote. The text itself still comes from the
page: read it as the page's claim. The host logs at most 10 reports per
connection and 30 per window a minute, so a page that reloads or reconnects
gets no fresh budget, and logs event 1151 `PageErrorsSuppressed` or 1152
`WindowPageErrorsSuppressed` once when it starts counting.

Reporting is a Views protocol 4 feature, `client-errors/1`
([page errors](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/README.md#page-errors)):
a page and host of 0.16 or later use it, and with an earlier host the page
drops its reports. The returned function removes the reporter. Errors still
reach the console and `onBridgeDiagnostic` as before. Page errors carry no
trace id; correlate them with the host's spans by time and route.

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
| 1190 | `BridgeQueryRowsFailed` | Error | A query collection's row source threw while loading a page's range, or its rows could not be encoded. The page's `query-rows` is answered `failed`, and its range shows the error. | `Model`, `Member`, `Route`, `Start`, `End`, `ErrorType` |
| 1191 | `BridgeQueryIndexFailed` | Error | A query collection's row source threw in `IndexOfAsync`. The page's `query-index` is answered `failed`. | `Model`, `Member`, `Route`, `ErrorType` |
| 1193 | `QueryCollectionWithoutModelContext` | Warning | A query collection raised a property change outside any model or synchronization context, on the thread its load completed on: it was used without a Bridge and without `RunicQueryCollectionOptions.ModelContext`. Once per collection. | `Row` |
| 1192 | `BridgeQuerySetFailed` | Error | Changing a query collection's query threw something other than the `ArgumentException` of an invalid query. | `Model`, `Member`, `Route`, `ErrorType` |
| 1026 | `WindowCloseUnacknowledged` | Debug | A Window's close waited for its pages that use `presented/1` until its timeout or 5 s, and some did not acknowledge it (`RunicWindowCloseResult.PagesAcknowledged` is `false`). | `Pages` |
| 1140 | `FieldFlushUnanswered` | Debug | Before a departure guard or a close guard, the window asked its pages that use `field-flush/1` to write their field drafts, and some did not answer within 1 s; the departure is decided on the state as it is. | `Pages` |
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
| 1200 | `BlobServeFailed` | Warning | A `RunicBlob` could not be opened or streamed to the page; the request fails. | `ErrorType` |
| 1201 | `UploadRejected` | Information | The window refused an upload: no valid token, too large, no room, idle for 30 seconds, or the connection closed. | `Reason`, `Detail` |
| 1202 | `UploadReceiveFailed` | Warning | An upload could not be stored: the client went away, the disk is full, or no private upload folder could be created. Its slot, bytes and partial file are freed. | `ErrorType` |
| 1203 | `UploadExpired` | Debug | An upload that no command claimed within 10 minutes was deleted. | `Length` |
| 1204 | `BlobRequestDenied` | Warning | A blob or upload request came from another origin or site than the window's page (CS-WebUI; Runic Desktop reports it as a Desktop diagnostic). | `Request`, `Reason` |
| 1205 | `BlobsUnavailable` | Warning | A `RunicBlob` crossed the Bridge, but the window's host serves no blobs, so its URL is empty. Once per window. | |
| 1206 | `BlobCleanupFailed` | Warning | A stored upload could not be deleted. | `ErrorType` |
| 1207 | `BlobEndpointFailed` | Warning | CS-WebUI could not start a window's blob endpoint; the window opens without blobs and uploads. | `ErrorType` |
| 1208 | `BlobEvicted` | Warning | A window holds more blobs of command results and interaction inputs than its limit (1,024), so the least recently sent ones' URLs were released; logged once per request or publication. State and collection rows never count. Release results a View no longer shows with `RunicBlob.Release()`. | `Count`, `Maximum` |
| 1110 | `ViewHostFallbackShown` | Warning | A WPF `RunicViewHost` cannot open its web presentation and shows its `Fallback`. | `ErrorType` |
| 1111 | `ViewHostFallbackFailed` | Error | Showing that fallback throws. | `ErrorType` |
| 1112 | `ViewHostPresentationChangeFailed` | Error | A `RunicViewHost` presentation change throws outside its open. | `ErrorType` |
| 1113 | `ViewHostCleanupFailed` | Error | Releasing a `RunicViewHost` presentation throws. | `ErrorType` |
| 1120 | `ViewRequestDropped` | Debug | A `ViewRequest<T>` found no single presentation to deliver to, or several windows present its ViewModel, and was dropped ([Asking the page](#asking-the-page-view-requests-and-questions)). | `Name`, `Reason` |
| 1121 | `ViewRequestKept` | Debug | A `NextMounted` view request found no single presentation and is kept for the next one that handles it. | `Name` |
| 1114 | `ChildViewCleanupFailed` | Error | A `WpfBridgeView` fails to release after its presentation closed. | `ErrorType` |
| 1130 | `WindowPresentationHandlerFailed` | Error | An `IRunicWindowPresentation.ActivityChanged` or `PropertyChanged` handler threw; later handlers and changes still run. | `Handler`, `ErrorType` |
| 1150 | `PageError` | Error | A page that called `installErrorReporting()` reports an uncaught error, unhandled rejection or runtime-caught error (`client-errors/1`). Category `Runic.Application.Views.Page`; see [page errors](#page-errors). | `Source`, `ErrorName`, `ErrorMessage`, `PageUrl`, `Route`, `Dropped`, `Client`, `ErrorStack` |
| 1151 | `PageErrorsSuppressed` | Warning | A page's connection reports more than 10 errors in a minute; the rest of the minute is counted into `Dropped` of the next 1150. Category `Runic.Application.Views.Page`. | `Limit` |
| 1152 | `WindowPageErrorsSuppressed` | Warning | A window's pages report more than 30 errors in a minute across their connections, such as a page in a reload loop; the rest of the minute is counted into `Dropped` of the next 1150. Category `Runic.Application.Views.Page`. | `Limit` |
| 1170 | `WindowLocaleChanged` | Debug | The application set the window's locale (`IRunicWindowLocale`, `WindowContentSession.Locale`), which pages with `translation-text/1` follow. | `Locale` |
| 1171 | `ValidationTextTooLarge` | Warning | A validation message's text reference is larger than 16 KiB, so its error carries only the message. | `Key`, `Bytes`, `Limit` |
| 1180 | `WindowOwnerIgnored` | Warning | A Window opens without its `RunicWindowOptions.Owner`: the host cannot own Windows (CS-WebUI, serve-only mode, a browser), or the owner has closed or belongs to another host. | `Window`, `Owner`, `Reason` |
| 1181 | `WindowsChangedHandlerFailed` | Error | An `IRunicWindowService.WindowsChanged` handler threw; later handlers still run. | `ErrorType` |
| 1210 | `BridgeRegionRequestFailed` | Error | `SelectAsync` or `RemoveAsync` of a region collection threw for a page's `select{Name}` or `close{Name}`; the call fails. | `Model`, `Member`, `Route`, `ErrorType` |
| 1211 | `BridgeRegionRequestUnresolved` | Debug | A page's `select{Name}` or `close{Name}` named a key that the region collection no longer has, such as a tab that closed meanwhile; the call rejects with `unresolved-key`. | `Model`, `Member`, `Route`, `Key` |
| 1230 | `ConnectionHandlerFailed` | Error | A handler of `IRunicConnections.Connected` or `Disconnected` throws; the other handlers still run ([Who is connected, and who called](#who-is-connected-and-who-called)). | `Event`, `ErrorType` |
| 1231 | `ConnectionStateDisposeFailed` | Error | A value of a `RunicConnectionState<T>` throws from `Dispose`, on its connection's disconnect or the state's disposal; the other values are still disposed. | `ValueType`, `ErrorType` |
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
| 3040 | `WindowActivityHandlerFailed` | Error | A `DesktopWindow.ActivityChanged` handler threw; later handlers and events still run. | `ErrorType` |

`Model` is the ViewModel type name, `Member` the command or property, and
`Route` the Bridge route (a content presentation's route is per instance, such
as `content12`). A declared failure is an expected outcome, so it is logged at
Debug, still with its exception; `FailureType` is the failure value's type.

Events 1000-1012, 1018-1026, 1050-1053, 1100-1102, 1130, 1140, 1180-1181, 1190-1193 and 1210-1211 use the category `Runic.Application.Views`
(`RunicViewsTelemetry.LogCategory`); event 1130 uses the logger of the Window's session; the Hot Reload events 1100-1102 use the
logger of the window session that owns the Bridge. Events 1110-1114 use
`Runic.Application.Views.Wpf` and the factory of the host's `Services`; so does event 1130 of
`WpfPlatformServices.ForWindowPresentation`, with the factory passed to it. Event 1033 here is a window session's
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
`rejected`, `cancelled`, `failed`, `domain_failed` or `released` (`disconnected` before 0.10); a
`[RunicPresentationScoped]` command that a renewal superseded is `superseded`. An
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
