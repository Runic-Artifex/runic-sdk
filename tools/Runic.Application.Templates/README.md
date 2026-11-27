# Runic.Application.Templates

Start a Runic desktop application with C# application logic and a React, Vue,
Svelte, SvelteKit, or Angular frontend. By default the app opens in a native Runic Desktop
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
| `--frontend` | `react`, `vue`, `svelte`, `sveltekit`, `angular` | `react` |
| `--angular-build` | `cli` (the Angular CLI), `vite` (Vite with the Analog Angular plugin): what builds, serves and tests an Angular frontend | `cli` |
| `--package-manager` | `npm`, `pnpm`, `bun` | `npm` |
| `--host` | `desktop` (Runic Desktop), `cswebui` (CS-WebUI) | `desktop` |
| `--linux-gtk` | `3` (GTK 3 and WebKitGTK 4.1), `4` (GTK 4 and WebKitGTK 6): the GTK version of Runic Desktop on Linux | `3` |
| `--desktop-server` | `native` (`Runic.Desktop.NativeServer`, no ASP.NET Core), `kestrel` (`Runic.Desktop.Kestrel`, ASP.NET Core Kestrel): the server Runic Desktop serves the page from | `native` |
| `--view-models` | `reactiveui` (ReactiveUI 26 Primitives), `toolkit` (CommunityToolkit.Mvvm) | `reactiveui` |
| `--editor` | `none`, `vscode`: adds `.vscode/` with tasks for `dotnet runic dev`, debug configurations for the C# extension and the recommended extensions | `none` |
| `--library` | A library project's name, such as `MyApp.Core`: creates a solution of the application and that library, which holds shared ViewModels and Views and a frontend package with their TypeScript clients, in one JavaScript workspace. See [A solution with a library](#a-solution-with-a-library). Not with `--translations` or `--nix` yet: they are left out with a warning | One project, no solution |
| `--tests` | Adds a TUnit test project, `MyApp.Tests` | No test project |
| `--translations` | Translates the application with [Runic Translations](https://github.com/Runic-Artifex/runic-translations-sdk) 0.9: one catalog in English and German (`Frontend/translations`) for the page and C#, a language switch, the application's language for every window, `RUNIC_LOCALE` for test runs, and page and .NET tests of the translated text | English text in the code |
| `--nix` | Adds a Nix flake that builds the application with the Runic SDK's `buildRunicApplication` | No flake |
| `--packaging` | Adds the `packaging/` manifests that `dotnet runic package` fills (MSIX, macOS bundle, desktop entry, AppStream metadata, Flatpak manifest), as editable overrides of the command's defaults | The command's built-in manifests |
| `--restore` | Restores the created projects' NuGet packages, for an IDE that opens the project next | No restore; `dotnet runic dev` and `dotnet build` restore |
| `--application-id` | The reverse-DNS application ID, such as `org.example.Notes` | `com.example.` and the project name's ASCII letters and digits |

Every project has `Properties/launchSettings.json`, whose first profile,
which `dotnet run` uses too, starts the Window with
`DOTNET_ENVIRONMENT=Development`; with CS-WebUI, a second profile
opens the DevTools Protocol of the Window's Chrome or Edge on port 9225 for a
JavaScript debugger's attach (since 0.22). Its `.vsconfig`
lets Visual Studio offer the components that debugging needs: the .NET desktop
development workload and JavaScript diagnostics, for script debugging (with
Runic Desktop, through Runic DevKit's Debug the Window). The generated README's "Debug it in your
IDE" section shows how Visual Studio, Rider and VS Code debug the Window while
`dotnet runic dev --no-window` serves the frontend. In Visual Studio's New
Project dialog the templates have an icon and list the same options, except
`--restore`: Visual Studio restores the projects it opens.

`--frontend sveltekit` starts a SvelteKit 3 frontend that `runicAdapter()` from
`@runic-artifex/sveltekit` prerenders into `Frontend/dist`, with
`@runic-artifex/svelte` for the Views. It works with either host and either
ViewModel library, and `dotnet runic dev` serves it from SvelteKit's
development server.

`--frontend angular` builds with the Angular CLI. `--angular-build vite` builds,
serves and tests the same Angular frontend with Vite instead, through the Analog
Angular plugin (`@analogjs/vite-plugin-angular`) and the Runic Vite plugin,
which adds what the other frontends have: with `--translations`, the
Translations Vite plugin, so an edited message reaches the page under
`dotnet runic dev` without a build; the Runic DevTools dock of
`@vitejs/devtools`; and the component build target of trusted component mode.
`vite build` checks no types, so the frontend's `build` and `typecheck` scripts
run `ngc --noEmit`, and its test runs on Vitest with the same plugin. The Analog
plugin is a third-party package that uses Angular's internal compiler API, so
update Angular and the plugin together.

`dotnet runic dev` restores NuGet packages, installs the frontend packages,
builds the application, starts the frontend development server, and opens the
app. `dotnet runic doctor` checks the prerequisites at any point, including
before the first restore.

With `--tests`, `dotnet test --project MyApp.Tests` runs TUnit tests that drive
the Window's ViewModels and generated Bridges in memory with
`Runic.Application.Testing`, without a browser or native window. The test project
builds the app without its frontend, and a `global.json` selects
Microsoft.Testing.Platform for `dotnet test`.

You need .NET SDK 10.0.404 or later, or the .NET 11 SDK, and either Node.js 24
or 26 with npm or pnpm, or Bun 1.4. The application targets `net10.0`, so it
runs on the .NET 10 runtime: an SDK 10 includes it, but with only the .NET 11
SDK installed, install the .NET 10 runtime too, or the application exits with
code 150 when it starts. `dotnet runic doctor` checks both. The generated
project has no `global.json` that selects an SDK, so it builds with the newest
one installed. Runic is tested with .NET 10; its build tools (the Bridge generator,
the assets packer, `dotnet-runic` and `runic-create`) also start on a newer
.NET runtime when the 10.0 runtime is not installed. Each generated project contains only the selected package manager's
lock file and a local `dotnet-runic` tool manifest. The project sets
`RestorePackagesWithLockFile`, so its first restore writes `packages.lock.json`,
which pins every NuGet package to its version and content hash; commit it with
the frontend's lock file.

## A solution with a library

`--library <Name>` creates a solution instead of a single project: the
application, and a library project beside it for ViewModels and Views that
more than one application can present.

```bash
dotnet new runic-app -n MyApp --library MyApp.Core
cd MyApp
dotnet tool restore
dotnet runic dev
```

In the solution's folder, `dotnet runic dev` and `dotnet runic doctor` take
its one Window project, the application.

| Path | What it holds |
| --- | --- |
| `MyApp.slnx` | The solution, with the application first, so Visual Studio starts it |
| `MyApp/` | The application, as `dotnet new runic-app` creates it without `--library`; its page shows the library's about panel |
| `MyApp.Core/` | The library: `AboutViewModel` and its View. `RunicApplicationFrontendClientLibrary` makes its build generate the View's TypeScript client into its frontend package, `MyApp.Core/Frontend` |
| `MyApp.Core/Frontend/` | The library's frontend package, `myapp-core`: its `build` script compiles it into `dist/`, whose exports hold the clients (`myapp-core/generated`) and their mocks (`myapp-core/generated/mocks`) |
| `package.json` and its lock | A JavaScript workspace of the two frontends, with the lock file of the selected package manager (and `pnpm-workspace.yaml` for pnpm) |
| `.config/`, `.vscode/`, `.gitignore`, `global.json` | Shared by the solution's projects; `.vscode/` (with `--editor vscode`) runs and debugs the application from the solution's folder |

The application references the library and maps it with
`RunicApplicationFrontendClientModule`, so its page imports the library's
clients from the package instead of generating copies. A fresh clone needs no
manual step: `dotnet build` of the solution, and `dotnet runic dev`, install the
workspace once at its root from the lock file, and build the library and its
package before the application's frontend. Every frontend and package manager
has a workspace lock in the template. The
[Views guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#share-viewmodels-and-their-clients-through-a-library)
describes client libraries.

`--library` does not support `--translations` or `--nix` yet. With either,
`dotnet new` creates the solution without it and prints a warning that names
each option it left out:

```text
Processing post-creation actions...
Description: Warning: --translations was left out. --library does not support Translations yet, so the solution has no translation catalog.
Manual instructions: Create the application without --library to translate it (…).
```

`dnx Runic.Create` refuses the combination with
[`RCREATE006`](../../docs/diagnostics.md#rcreate006). Visual Studio and Rider
offer both options beside a library project; their descriptions say that they
do not apply to it. Create the application without `--library` to use them.

## Add a View to an existing project

The `runic-view` item template adds a View, or a Window, and its C# ViewModel to
a project that already references a Runic host (`Runic.Application.Views.Desktop`
or `Runic.Application.Views.CsWebUi`) or `Runic.Application.Views`, such as a
`runic-app` project or an application you are moving to Runic:

```bash
dotnet new runic-view -n Settings                          # SettingsView.cs, SettingsViewModel.cs
dotnet new runic-view -n Settings --view-models toolkit    # CommunityToolkit.Mvvm
dotnet new runic-view -n Preferences --window              # PreferencesWindow.cs, PreferencesViewModel.cs
```

| Option | Choices | Default |
| --- | --- | --- |
| `--view-models` | `reactiveui` (ReactiveUI 26 Primitives, with `Runic.Application.Views.ReactiveUI`), `toolkit` (CommunityToolkit.Mvvm) | `reactiveui` |
| `--window` | Declares a Window, which a host opens with `OpenWindowAsync`, instead of a View | A View |
| `--namespace` | The namespace of the new types | The project's `RootNamespace` once it has been restored, otherwise `App` |

Pass the name without the `View` or `Window` suffix. Register the new ViewModel
with your services, for example `services.AddScoped<SettingsViewModel>()`;
`AddRunicViews()` registers the View. The next build writes its typed client to
the frontend's `src/generated` folder.

What each host opens:

- `desktop` opens a native window with the platform's embedded WebView, native
  file dialogs and the clipboard, through `RunicDesktopHost.Run`, which picks
  the event loop each platform needs.
- `desktop --linux-gtk 4` is `desktop` on GTK 4 and WebKitGTK 6 on Linux
  (`WithGtk4()`, with the GTK 4 portal and clipboard packages); on Windows and
  macOS it is the same as `desktop`.
- `cswebui` opens a browser window: an installed browser in app mode (Chrome,
  Edge or another Chromium-based browser works best), with the platform WebView
  as a fallback. It is supported on a best-effort basis and has no native
  dialogs or clipboard. `--linux-gtk` and `--desktop-server` do not change a
  `cswebui` project: CS-WebUI has no GTK or server choice.

A `desktop` project serves its page from the native server
(`Runic.Desktop.NativeServer`): the application needs no ASP.NET Core, so a
framework-dependent build needs only the .NET runtime and a NativeAOT build is
about a quarter smaller. `--desktop-server kestrel` uses ASP.NET Core
Kestrel (`Runic.Desktop.Kestrel`) instead, for an application that uses ASP.NET
Core itself.

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

`<CANDIDATE>` and the npm registry must come from the same package set: the
template has no version options, and its pack stamps the Runic NuGet and npm
versions that projects reference. Pack a candidate template for other versions
with `-p:RunicNuGetTemplateVersion=<VERSION>`,
`-p:RunicCsWebUiNuGetTemplateVersion=<VERSION>` and
`-p:RunicNpmTemplateVersion=<VERSION>`. The
SDK's template acceptance check (`bun run verify:templates`) runs the commands
above for every frontend and package manager with the default Runic Desktop and
ReactiveUI, plus pairwise CS-WebUI and CommunityToolkit.Mvvm variants, and
checks that `Runic.Create` produces the same project. The pairwise variants and
the npm projects add `--tests` and run the generated tests; every project also
gets a View and a Window from `runic-view`. CI builds the SvelteKit projects on
Node 26. A `--library` solution for each frontend, together covering every
package manager, is checked as a fresh clone: `dotnet build` of the solution,
the frontends' type checks and tests and, for CS-WebUI, `dotnet runic dev`
from a clean checkout, whose development server must resolve the library's
built package. `RUNIC_TEMPLATE_LIBRARY_VARIANTS` selects them, and an empty
`RUNIC_TEMPLATE_MANAGERS` or `RUNIC_TEMPLATE_VARIANTS` skips the other
projects.
Setting `RUNIC_APPLICATION_SERVE_ONLY=1` starts an app's local server, on either host,
without opening a browser and prints `RUNIC_APPLICATION_URL=<url>`. Setting
`RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` makes a Runic Desktop app print
`RUNIC_APPLICATION_OPENED=<presentation>` once its Window has opened and
connected, then close it; with `RUNIC_TEMPLATE_DESKTOP_SMOKE=1` the acceptance
check runs a default `desktop` project and the `--linux-gtk 4` variant this way
under Xvfb.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/), [project creator](https://docs.runic-artifex.eu/create/), [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
