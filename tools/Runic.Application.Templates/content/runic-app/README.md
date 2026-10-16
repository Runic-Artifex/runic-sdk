# RunicWindowApp

A .NET 10 desktop application with a __FRONTEND_NAME__ frontend, created from the
Runic application template. C# owns the application state and commands;
__FRONTEND_NAME__ renders it.

## Run it

```sh
dotnet tool restore
dotnet runic dev
```

`dotnet tool restore` installs the project-local `dotnet runic` tool.
`dotnet runic dev` restores NuGet packages, installs the frontend packages
with __PACKAGE_MANAGER_NAME__, builds the application, starts the
__DEVELOPMENT_SERVER_NAME__ development server, and opens the app. Frontend
edits reload in place; C# edits rebuild and restart the app. Stop it with Ctrl+C.

If something is missing, `dotnet runic doctor` checks the .NET SDK, Node.js
or Bun, __PACKAGE_MANAGER_NAME__, the lock file, and the Runic package
versions. It works before the first restore.

`dotnet build` and `dotnet run` also work without the tool. The first build
installs the frontend packages with __PACKAGE_MANAGER_NAME__, and later builds
install them again when `package.json` or the lock file changes; set
`RunicApplicationFrontendInstallEnabled` to `false` to install them yourself.
These commands build the production frontend instead of starting a development server.

## What opens

`RunAsync` in `Program.cs` opens `WorkspaceWindow` with
`host.OpenWindowAsync<WorkspaceWindow>()` and waits for it with
`WaitForCloseAsync()`. The Window declaration in `Views.cs` and this body are
the same on every host; only the `Run` call and its options choose the host.

<!--#if (gtk4) -->
`Program.cs` runs the application with `RunicDesktopHost.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 4 with WebKitGTK 6 on Linux.
`WithGtk4()` selects the GTK 4 backend and its window provider together, and
on Linux `RunicDesktopHost.Run` runs GTK on the process main thread. Linux needs
GTK 4.12 or newer and WebKitGTK 6.0 (for example `libgtk-4-1` and
`libwebkitgtk-6.0-4`); `dotnet runic doctor` lists anything missing.
Without an embedded WebView it falls back to an installed browser. See the
[Runic.Desktop.Gtk4 guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Desktop.Gtk4/README.md)
for the GTK 4 backend and platform services.
<!--#elif (desktopHost) -->
`Program.cs` runs the application with `RunicDesktopHost.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 3 with WebKitGTK 4.1 on Linux.
Without an embedded WebView it falls back to an installed browser. To choose
another presentation, call `DesktopEventLoop.Run` and create a
`RunicDesktopHost` with other `WindowOptions`; see the
[Runic.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Desktop/README.md)
for native prerequisites and platform services. Create the project with
`--host desktop-gtk4` to use GTK 4 and WebKitGTK 6 on Linux.
<!--#else -->
`RunicCsWebUiHost.Run` in `Program.cs` uses CS-WebUI. It opens the app
in an installed browser in app mode (Chrome, Edge or another Chromium-based
browser works best; Firefox works without app mode), falls back to the default
browser, and then to the platform WebView: the Edge WebView2 Runtime on
Windows, GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS.
Applications that need native windows, dialogs, or platform services can use
the template's default Runic Desktop host (create the project without
`--host`); see the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/).
<!--#endif -->

The page names no host: `Frontend/index.html` loads `webui.js` and
`runic-views.js`, which the host package copies into `www/` from its own
Views client. Switching hosts changes the host package and `Program.cs`, not
the frontend. A project that
references both hosts sets `RunicApplicationHost` to `desktop` or `cswebui`.

## File dialogs and the clipboard

`services.AddRunicPlatformServices()` in `WorkspaceServices.cs` registers
`IFileDialogs`, `ITextClipboard`, `IDesktopFileLauncher` and
`IWindowPlatformServices` (from `Runic.Platform`) for each window. A ViewModel
takes them in its constructor:

```csharp
public sealed class NotesViewModel(IFileDialogs files, ITextClipboard clipboard)
```

<!--#if (desktopHost) -->
They bind to the native window when it opens and use the provider for the
running platform and backend: Win32 on Windows, AppKit on macOS, and
xdg-desktop-portal on Linux. Before the window opens, and in a window without
a native owner (an installed-browser fallback), they return
`Unavailable(OwnerUnavailable)`. `dotnet runic doctor` names the provider and
checks for the portal on Linux. The
[Runic.Application.Views.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.Desktop/README.md)
has an open, read and atomic-save example.
<!--#else -->
CS-WebUI windows have no native owner, so these services return
`Unavailable(OwnerUnavailable)`. The same ViewModels get native dialogs and the
clipboard on the default Runic Desktop host (create the project without
`--host`); see the
[Runic.Application.Views.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.Desktop/README.md).
<!--#endif -->

## Project layout

| Path | Purpose |
| --- | --- |
<!--#if (viewModels == "reactiveui") -->
| `WorkspaceViewModel.cs` | ReactiveUI 26 ViewModels (the Primitives flavour): the workspace, its Welcome page, and a Counter. Their commands need no scheduler: they run on the Window's model context. |
<!--#else -->
| `WorkspaceViewModel.cs` | CommunityToolkit.Mvvm ViewModels: the workspace, its Welcome page, and a Counter. |
<!--#endif -->
| `Views.cs` | The `WorkspaceWindow` and the Views. The build generates a typed client for each. |
| `WorkspaceServices.cs` | `AddWorkspace()` registers the ViewModels, the generated `AddRunicViews()` and the platform services. |
| `Program.cs` | Opens the Window with the services from `AddWorkspace()`. |
<!--#if (frontend == "angular") -->
| `Frontend/src/app` | Components connect generated clients with `injectView()` and render the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/angular`. |
<!--#elif (frontend == "svelte") -->
| `Frontend/src/App.svelte`, `Frontend/src/pages` | Connect generated clients with `useView` and render the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/svelte/views`. |
<!--#elif (frontend == "vue") -->
| `Frontend/src/App.vue`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/vue` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#else -->
| `Frontend/src/App.tsx`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/react` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#endif -->
<!--#if (frontend != "angular") -->
| `Frontend/vite.config.ts` | Adds `runic()` from `@runic-artifex/vite-plugin-runic` for Runic development diagnostics. To add its Runic DevTools dock, install `@vitejs/devtools` and register `DevTools()`. |
<!--#endif -->
| `Frontend/src/generated` | Typed clients generated from the ViewModels. They import the shared `@runic-artifex/views` runtime. |
<!--#if (tests) -->
| `RunicWindowApp.Tests/WorkspaceWindowTests.cs` | TUnit tests that drive the Window through `Runic.Application.Testing`. |
| `global.json` | Runs `dotnet test` on Microsoft.Testing.Platform, which TUnit uses. |
<!--#endif -->

The generated clients are not committed: `.gitignore` excludes
`Frontend/src/generated`, and `dotnet build` or `dotnet runic dev` writes
them again. Until the first build, your editor and
`__PACKAGE_MANAGER_NAME__ run typecheck` report the missing modules.
<!--#if (frontend != "angular") -->
The frontend's `build` script runs `vite build` before the type check, so
modules that a Vite plugin generates, such as the Runic Translations
`virtual:runic-translations/...` declarations, exist when the check runs on a
clean checkout.
<!--#endif -->

<!--#if (tests) -->
## Test it

```sh
dotnet test --project RunicWindowApp.Tests
```

`RunicWindowApp.Tests` drives the Window's real ViewModels and generated Bridges
in memory, as the frontend does, without a browser or native window.
`RunicWindowTestHost.Create` builds the Window from the services that
`AddWorkspace()` registers, so register new ViewModels there and both the app
and the tests use them. Tests read state by ViewModel member, set properties and
run commands:

```csharp
(await window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).EnsureOk();
var counter = window.Root.View<CounterViewModel>(vm => vm.Main);
(await counter.ExecuteAsync(vm => vm.IncrementCommand)).EnsureOk();
await Assert.That(counter.Snapshot().Read(vm => vm.Count)).IsEqualTo(1);
```

TUnit runs the tests in parallel, and each test builds its own Window. The test
project builds the app with `RunicApplicationFrontendBuildEnabled=false`, so tests do not
install or build the frontend. `dotnet run --project RunicWindowApp.Tests` runs
the tests too. See the
[Runic.Application.Testing guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Testing/README.md)
for the drivers, a fake clock, collections and navigation, and test the frontend
with the generated typed mocks of `@runic-artifex/views/mock`.

<!--#endif -->
## State and threading

Set ViewModel properties directly in commands, also after `await`: an async
command resumes on the Window's model context, so its writes are applied one
at a time and in order. Only code that leaves the context, after
`ConfigureAwait(false)` or in `Task.Run`, commits its result with
`IRunicModelContext.InvokeAsync`. See the
[threading rule](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views/README.md#threading-state-after-await).
<!--#if (viewModels == "reactiveui") -->

## ReactiveUI 26 Primitives

This project uses ReactiveUI 26 in its default flavour, which is built on
`ReactiveUI.Primitives`, not on System.Reactive. If you know ReactiveUI from
System.Reactive:

- `RxVoid` replaces `System.Reactive.Unit`, so a command without input or
  output is a `ReactiveCommand<RxVoid, RxVoid>`.
- `ISequencer` replaces `IScheduler`: `RxSchedulers.MainThreadScheduler`, formerly
  `RxApp.MainThreadScheduler`, is an `ISequencer`.
- To await a command, add `using ReactiveUI.Primitives.Signals;`; without it,
  `await command.Execute()` does not compile (CS1061).
  `command.Execute().ToTask()` from `ReactiveUI.Primitives` also works and
  returns the last value.

`AddRunicReactiveModelContext()` in `Program.cs` makes ReactiveUI's main-thread
scheduler deliver a command's results and `IsExecuting` on the model context
of the Window that runs it. Create commands without a scheduler, as
`WorkspaceViewModel.cs` does. Code that schedules from outside a model turn, for
example after `ConfigureAwait(false)`, runs on ReactiveUI's default scheduler;
Runic logs that once. For System.Reactive, use `Runic.Application.Views.ReactiveUI.Reactive`
instead; see the
[ReactiveUI adapter](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md).
<!--#endif -->

## Read-only and settable state

As in any MVVM View, the C# setters decide what the frontend may set. The
Counter's `Count` has a private setter, so the page only reads it; `Step` has a
public setter, so the generated client has `setStep` and the page binds it two
way. Give status such as `IsDirty`, `Error` or a collection a private setter
<!--#if (viewModels == "reactiveui") -->
(`private set => this.RaiseAndSetIfChanged(...)`, or `[Reactive]` with
`{ get; private set; }`), and keep form fields publicly settable.
<!--#else -->
(`[ObservableProperty] public partial T Name { get; private set; }`), and keep
form fields publicly settable.
<!--#endif -->

## Publish

```sh
dotnet publish -c Release -r linux-x64
```

Use `win-x64`, `osx-arm64`, or another runtime identifier for other
platforms. Windows builds are GUI applications (`WinExe`), so no console window
opens and diagnostics written to standard error are not visible there. The
publish folder holds the executable and a `www` folder with the built frontend;
ship those two together, without debug symbols. Users need no Node.js or package
manager, only the platform WebView or a browser described above. The
[Ship a Desktop application](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/shipping.md)
guide covers publish modes, signing and packaging per platform.
<!--#if (nix) -->

## Build it with Nix

`flake.nix` builds the application with the Runic SDK's
`buildRunicApplication`, in the Nix sandbox and without network access. NuGet
packages come from `packages.lock.json`, which `dotnet restore` writes because
the project sets `RestorePackagesWithLockFile`, and for the NativeAOT package
from `packages.aot.lock.json`, which `dotnet restore -p:PublishAot=true`
writes. Both include `linux-x64` (`RuntimeIdentifiers`), and the build
restores in locked mode: after changing a package reference, restore both
again, or the build fails with `NU1004`. The frontend's packages come from its
lock file. Commit the lock files, and add the files to Git before the first
build, because a flake sees only the files Git tracks:

```sh
dotnet restore
dotnet restore -p:PublishAot=true
<!--#if (tests) -->
dotnet restore RunicWindowApp.Tests
<!--#endif -->
git init && git add -A
nix build                # or nix build .#native-aot
result/bin/RunicWindowApp
```

<!--#if (tests) -->
The test project has its own lock file, `RunicWindowApp.Tests/packages.lock.json`.
Both packages build and run the tests, under the JIT, so a failing test fails
the build; `nix flake check` builds the default package. The build runs the
test application itself (`testRunner = "Microsoft.Testing.Platform"`), since
`dotnet test`'s VSTest options do not apply to TUnit.

<!--#endif -->
<!--#if (packageManager != "npm") -->
The frontend's packages are one fixed-output download. Its hash in
`flake.nix` is `lib.fakeHash` until the first `nix build`, which fails and
reports the real hash: replace `lib.fakeHash` with it, and update it whenever
the lock file changes.

<!--#endif -->
The package installs the program in `bin`, the desktop entry in
`share/applications`, and GTK and WebKitGTK as run-time libraries. `nix develop`
opens a shell with the .NET SDK, Node, the package manager and those libraries.
The SDK's
[Nix packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/nix/README.md)
covers icons, privileged actions, NativeAOT, and the NixOS and Home Manager
modules.
<!--#endif -->

## Project settings

The Runic packages supply defaults, so the project file only opts in with
`RunicApplicationFrontendWindowProject`. Set these properties to change them:

| Property | Default |
| --- | --- |
| `RunicApplicationFrontendDirectory` | `Frontend` |
| `RunicApplicationFrontendGeneratedDirectory` | `Frontend/src/generated` |
| `RunicApplicationFrontendPackageManager` | `packageManager` in `Frontend/package.json`, then the lock file |
| `RunicApplicationFrontendBuildCommand` | `<package manager> run build` |
| `RunicApplicationFrontendInstallEnabled` | `true`: install frontend packages during build when they are missing or `package.json` or the lock file changed |
| `RunicApplicationFrontendBuildEnabled` | `true`; `dotnet runic dev` sets `false` while its development server runs |

See the [Runic.Application.Views package guide](https://github.com/Runic-Artifex/runic-sdk/blob/v__RUNIC_NUGET_VERSION__/packages/dotnet/Runic.Application.Views/README.md)
for every build property, and the
[examples](https://github.com/Runic-Artifex/runic-sdk/tree/v__RUNIC_NUGET_VERSION__/examples)
for nested Views, routing, and multiple windows.
