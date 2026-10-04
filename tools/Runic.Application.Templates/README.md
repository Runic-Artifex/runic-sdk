# Runic.Application.Templates

Start a Runic desktop application with C# application logic and a React, Vue,
Svelte, or Angular frontend.

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
| `--host` | `cswebui` (CS-WebUI), `desktop` (Runic Desktop) | `cswebui` |
| `--view-models` | `toolkit` (CommunityToolkit.Mvvm), `reactiveui` | `toolkit` |

`dotnet runic dev` restores NuGet packages, installs the frontend packages,
builds the application, starts the frontend development server, and opens the
app. `dotnet runic doctor` checks the prerequisites at any point, including
before the first restore.

You need the .NET 10 SDK and either Node.js 24 with npm or pnpm, or Bun 1.4.
Each generated project contains only the selected package manager's lock file
and a local `dotnet-runic` tool manifest.

With the CS-WebUI host, the app opens in an installed browser in app mode
(Chrome, Edge or another Chromium-based browser works best), with the platform
WebView as a fallback. With the Runic Desktop host, it opens in a native window
with the platform's embedded WebView. `dotnet publish` writes the executable
and a `www` folder with the built frontend; users need no Node.js. The
generated README explains the project layout and settings.

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
above for every frontend and package manager, plus pairwise Runic Desktop and
ReactiveUI variants, and checks that `Runic.Create` produces the same project.
Setting `RUNIC_APPLICATION_SERVE_ONLY=1` starts a CS-WebUI app's local server
without opening a browser and prints `RUNIC_APPLICATION_URL=<url>`.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/), [project creator](https://docs.runic-artifex.eu/create/), [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
