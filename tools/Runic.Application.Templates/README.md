# Runic.Application.Templates

Start a Runic desktop application with C# application logic and a React, Vue,
Svelte, or Angular frontend. By default the app opens in a native Runic Desktop
window and its ViewModels use ReactiveUI.

```bash
dotnet new install Runic.Application.Templates@<VERSION>
dotnet new runic-app -n MyApp
cd MyApp
dotnet tool restore
dotnet runic dev
```

`dnx Runic.Create@<VERSION>` asks for each option, then runs the same two
`dotnet new` commands. The template's options are:

| Option | Choices | Default |
| --- | --- | --- |
| `--frontend` | `react`, `vue`, `svelte`, `angular` | `react` |
| `--package-manager` | `npm`, `pnpm`, `bun` | `npm` |
| `--host` | `desktop` (Runic Desktop), `desktop-gtk4` (Runic Desktop with GTK 4 on Linux), `cswebui` (CS-WebUI) | `desktop` |
| `--view-models` | `reactiveui` (ReactiveUI 26 Primitives), `toolkit` (CommunityToolkit.Mvvm) | `reactiveui` |
| `--tests` | Adds a TUnit test project, `MyApp.Tests` | No test project |

`dotnet runic dev` restores NuGet packages, installs the frontend packages,
builds the application, starts the frontend development server, and opens the
app. `dotnet runic doctor` checks the prerequisites at any point, including
before the first restore.

With `--tests`, `dotnet test --project MyApp.Tests` runs TUnit tests that drive
the Window's ViewModels and generated Bridges in memory with
`Runic.Application.Testing`, without a browser or native window. The test project
builds the app without its frontend, and a `global.json` selects
Microsoft.Testing.Platform for `dotnet test`.

You need the .NET 10 SDK and either Node.js 24 with npm or pnpm, or Bun 1.4.
Each generated project contains only the selected package manager's lock file
and a local `dotnet-runic` tool manifest.

What each host opens:

- `desktop` opens a native window with the platform's embedded WebView, native
  file dialogs and the clipboard, through `RunicDesktopHost.Run`, which picks
  the event loop each platform needs.
- `desktop-gtk4` is `desktop` on GTK 4 and WebKitGTK 6 on Linux (`WithGtk4()`,
  with the GTK 4 portal and clipboard packages); on Windows and macOS it is the
  same as `desktop`.
- `cswebui` opens a browser window: an installed browser in app mode (Chrome,
  Edge or another Chromium-based browser works best), with the platform WebView
  as a fallback. It is supported on a best-effort basis and has no native
  dialogs or clipboard.

`--view-models toolkit` uses CommunityToolkit.Mvvm instead of ReactiveUI and is
equally supported. `dotnet publish` writes the executable and a `www` folder
with the built frontend; users need no Node.js. The generated README explains
the project layout and settings.

## Test a local candidate

Install the template from a local NuGet feed. Every project also needs the
matching candidate npm archives (`@runic-artifex/views` and the framework
binding) through a local `@runic-artifex` registry before its first restore:

```bash
dotnet new install Runic.Application.Templates@<CANDIDATE> --nuget-source /path/to/nuget-feed
dotnet new runic-app -n MyCandidateApp --frontend svelte
cd MyCandidateApp/Frontend
npm config set --location=project @runic-artifex:registry http://127.0.0.1:<PORT>
```

`<CANDIDATE>` and the npm registry must come from the same package set. The
SDK's template acceptance check (`bun run verify:templates`) runs the commands
above for every frontend and package manager with the default Runic Desktop and
ReactiveUI, plus pairwise CS-WebUI and CommunityToolkit.Mvvm variants, and
checks that `Runic.Create` produces the same project. The pairwise variants and
the npm projects add `--tests` and run the generated tests.
Setting `RUNIC_APPLICATION_SERVE_ONLY=1` starts an app's local server, on either host,
without opening a browser and prints `RUNIC_APPLICATION_URL=<url>`. Setting
`RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` makes a Runic Desktop app print
`RUNIC_APPLICATION_OPENED=<presentation>` once its Window has opened and
connected, then close it; with `RUNIC_TEMPLATE_DESKTOP_SMOKE=1` the acceptance
check runs a default `desktop` project and the `desktop-gtk4` variant this way
under Xvfb.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/), [project creator](https://docs.runic-artifex.eu/create/), [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
