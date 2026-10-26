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
| `--package-manager` | `npm`, `pnpm`, `bun` | `npm` |
| `--host` | `desktop` (Runic Desktop), `cswebui` (CS-WebUI) | `desktop` |
| `--linux-gtk` | `3` (GTK 3 and WebKitGTK 4.1), `4` (GTK 4 and WebKitGTK 6): the GTK version of Runic Desktop on Linux | `3` |
| `--view-models` | `reactiveui` (ReactiveUI 26 Primitives), `toolkit` (CommunityToolkit.Mvvm) | `reactiveui` |
| `--tests` | Adds a TUnit test project, `MyApp.Tests` | No test project |
| `--nix` | Adds a Nix flake that builds the application with the Runic SDK's `buildRunicApplication` | No flake |
| `--packaging` | Adds the `packaging/` manifests that `dotnet runic package` fills (MSIX, macOS bundle, desktop entry, AppStream metadata, Flatpak manifest), as editable overrides of the command's defaults | The command's built-in manifests |
| `--restore` | Restores the created projects' NuGet packages, for an IDE that opens the project next | No restore; `dotnet runic dev` and `dotnet build` restore |
| `--application-id` | The reverse-DNS application ID, such as `org.example.Notes` | `com.example.` and the project name's ASCII letters and digits |

`--frontend sveltekit` starts a SvelteKit 3 frontend that `runicAdapter()` from
`@runic-artifex/sveltekit` prerenders into `Frontend/dist`, with
`@runic-artifex/svelte` for the Views. It works with either host and either
ViewModel library, and `dotnet runic dev` serves it from SvelteKit's
development server.

`dotnet runic dev` restores NuGet packages, installs the frontend packages,
builds the application, starts the frontend development server, and opens the
app. `dotnet runic doctor` checks the prerequisites at any point, including
before the first restore.

With `--tests`, `dotnet test --project MyApp.Tests` runs TUnit tests that drive
the Window's ViewModels and generated Bridges in memory with
`Runic.Application.Testing`, without a browser or native window. The test project
builds the app without its frontend, and a `global.json` selects
Microsoft.Testing.Platform for `dotnet test`.

You need the .NET 10 SDK and either Node.js 24 or 26 with npm or pnpm, or
Bun 1.4. Runic is tested with .NET 10; its build tools (the Bridge generator,
the assets packer, `dotnet-runic` and `runic-create`) also start on a newer
.NET runtime when the 10.0 runtime is not installed. Each generated project contains only the selected package manager's
lock file and a local `dotnet-runic` tool manifest. The project sets
`RestorePackagesWithLockFile`, so its first restore writes `packages.lock.json`,
which pins every NuGet package to its version and content hash; commit it with
the frontend's lock file.

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
  dialogs or clipboard. `--linux-gtk` does not change a `cswebui` project:
  CS-WebUI has no GTK choice.

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
Node 26.
Setting `RUNIC_APPLICATION_SERVE_ONLY=1` starts an app's local server, on either host,
without opening a browser and prints `RUNIC_APPLICATION_URL=<url>`. Setting
`RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1` makes a Runic Desktop app print
`RUNIC_APPLICATION_OPENED=<presentation>` once its Window has opened and
connected, then close it; with `RUNIC_TEMPLATE_DESKTOP_SMOKE=1` the acceptance
check runs a default `desktop` project and the `--linux-gtk 4` variant this way
under Xvfb.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/), [project creator](https://docs.runic-artifex.eu/create/), [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
