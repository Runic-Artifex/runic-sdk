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

<!--#if (host == "desktop") -->
`Program.cs` starts a Runic Desktop host and opens the Window in a native
window with the platform's embedded WebView: the Edge WebView2 Runtime on
Windows, WKWebView on macOS, or GTK 3 with WebKitGTK 4.1 on Linux. Without an
embedded WebView it falls back to an installed browser. Change
`DesktopWindowOptions` to choose another presentation, and see the
[Runic.Desktop guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Desktop/README.md)
for native prerequisites, the GTK 4 backend, and platform services.
<!--#else -->
`window.Show("index.html")` in `Program.cs` uses CS-WebUI. It opens the app
in an installed browser in app mode (Chrome, Edge or another Chromium-based
browser works best; Firefox works without app mode), falls back to the default
browser, and then to the platform WebView: the Edge WebView2 Runtime on
Windows, GTK 3 with WebKitGTK 4.1 on Linux, or WKWebView on macOS.
Applications that need native windows, dialogs, or platform services can use
the Runic Desktop host (`--host desktop` when creating a project); see the
[host selection guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/guides/desktop/host-selection.md).
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
