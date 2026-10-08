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
installs missing frontend packages with __PACKAGE_MANAGER_NAME__; set
`RunicBridgeInstallFrontend` to `false` to install them yourself. These
commands build the production frontend instead of starting a development server.

## What opens

<!--#if (gtk4) -->
`Program.cs` runs the application with `DesktopEventLoop.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 4 with WebKitGTK 6 on Linux.
`WithGtk4()` selects the GTK 4 backend and its window provider together, and
on Linux `DesktopEventLoop.Run` runs GTK on the process main thread. Linux needs
GTK 4.12 or newer and WebKitGTK 6.0 (for example `libgtk-4-1` and
`libwebkitgtk-6.0-4`); `dotnet runic doctor` lists anything missing. The
project also references `Runic.Platform.Linux.Gtk4` and
`Runic.Platform.Linux.Portal` for portal file dialogs and the clipboard. Without
an embedded WebView it falls back to an installed browser. See the
[Runic.Desktop.Gtk4 guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop.Gtk4/README.md)
for the GTK 4 backend and platform services.
<!--#elif (desktopHost) -->
`Program.cs` runs the application with `DesktopEventLoop.Run`, which starts a
Runic Desktop host on the event loop each platform needs, and opens the Window
in a native window with the platform's embedded WebView: the Edge WebView2
Runtime on Windows, WKWebView on macOS, or GTK 3 with WebKitGTK 4.1 on Linux.
Without an embedded WebView it falls back to an installed browser. Change
`DesktopWindowOptions` to choose another presentation, and see the
[Runic.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md)
for native prerequisites and platform services. Create the project with
`--host desktop-gtk4` to use GTK 4 and WebKitGTK 6 on Linux.
<!--#else -->
`window.Show("index.html")` in `Program.cs` uses CS-WebUI. It opens the app
in an installed browser in app mode (Chrome, Edge or another Chromium-based
browser works best; Firefox works without app mode), falls back to the default
browser, and then to the platform WebView: the Edge WebView2 Runtime on
Windows, GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS.
Applications that need native windows, dialogs, or platform services can use
the Runic Desktop host (`--host desktop` when creating a project); see the
[host selection guide](https://docs.runic-artifex.eu/guides/desktop/host-selection/).
<!--#endif -->

## Project layout

| Path | Purpose |
| --- | --- |
<!--#if (viewModels == "reactiveui") -->
| `WorkspaceViewModel.cs` | ReactiveUI ViewModels: the workspace, its Welcome page, and a Counter. Commands run on the Window's model context. |
<!--#else -->
| `WorkspaceViewModel.cs` | CommunityToolkit.Mvvm ViewModels: the workspace, its Welcome page, and a Counter. |
<!--#endif -->
| `Views.cs` | The `WorkspaceWindow` and the Views. The build generates a typed client for each. |
| `Program.cs` | Registers the ViewModels, calls `AddRunicViews()`, and opens the Window. |
<!--#if (frontend == "angular") -->
| `Frontend/src/app` | Components connect generated clients with `injectView()` and render the page that `WorkspaceViewModel.Main` selects with `RunicViewOutlet`, both from `@runic-artifex/angular`. |
<!--#elif (frontend == "svelte") -->
| `Frontend/src/App.svelte`, `Frontend/src/pages` | Connect generated clients with `useView` and render the page that `WorkspaceViewModel.Main` selects with `ViewOutlet`, both from `@runic-artifex/svelte/views`. |
<!--#elif (frontend == "vue") -->
| `Frontend/src/App.vue`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/vue` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#else -->
| `Frontend/src/App.tsx`, `Frontend/src/pages` | Connect generated clients with `useView` from `@runic-artifex/react` and render the page that `WorkspaceViewModel.Main` selects. |
<!--#endif -->
<!--#if (frontend != "angular" && desktopHost) -->
| `Frontend/vite.config.ts` | Adds `runic({ desktop: true })` from `@runic-artifex/vite-plugin-runic`, which loads the Runic Desktop bootstrap and builds with relative asset URLs. To add its Runic DevTools dock, install `@vitejs/devtools` and register `DevTools()`. |
<!--#elif (frontend != "angular") -->
| `Frontend/vite.config.ts` | Adds `runic()` from `@runic-artifex/vite-plugin-runic` for Runic development diagnostics. To add its Runic DevTools dock, install `@vitejs/devtools` and register `DevTools()`. |
<!--#endif -->
| `Frontend/src/generated` | Typed clients generated from the ViewModels. They import the shared `@runic-artifex/views` runtime. |

The generated clients are not committed: `.gitignore` excludes
`Frontend/src/generated`, and `dotnet build` or `dotnet runic dev` writes
them again. Until the first build, your editor and
`__PACKAGE_MANAGER_NAME__ run typecheck` report the missing modules.

## Publish

```sh
dotnet publish -c Release -r linux-x64
```

Use `win-x64`, `osx-arm64`, or another runtime identifier for other
platforms. The publish folder contains the executable and a `www` folder with
the built frontend; distribute the whole folder. Users need no Node.js or
package manager, only the platform WebView or a browser described above.

## Project settings

The Runic packages supply defaults, so the project file only opts in with
`RunicViewsWindowProject`. Set these properties to change them:

| Property | Default |
| --- | --- |
| `RunicBridgeFrontendDir` | `Frontend` |
| `RunicBridgeTypescriptDir` | `Frontend/src/generated` |
| `RunicBridgeFrontendPackageManager` | `packageManager` in `Frontend/package.json`, then the lock file |
| `RunicBridgeFrontendBuildCommand` | `<package manager> run build` |
| `RunicBridgeInstallFrontend` | `true`: install missing frontend packages during build |
| `RunicBridgeBuildFrontend` | `true`; `dotnet runic dev` sets `false` while its development server runs |

See the [Runic.Application package guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md)
for every build property, and the
[examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples)
for nested Views, routing, and multiple windows.
