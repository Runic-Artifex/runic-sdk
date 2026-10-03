# Runic.Application.Templates

Start a Runic desktop application with C# application logic and a React, Vue,
Svelte, or Angular frontend.

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-react -n MyApp
cd MyApp
dotnet tool restore
dotnet runic dev
```

Replace `react` with `vue`, `svelte`, or `angular` to select the frontend.
`dotnet runic dev` restores NuGet packages, installs the frontend packages,
builds the application, starts the frontend development server, and opens the
app. `dotnet runic doctor` checks the prerequisites at any point, including
before the first restore.

You need the .NET 10 SDK and either Node.js 24 with npm or pnpm, or Bun 1.4.
The templates default to npm; add `--packageManager pnpm` or
`--packageManager bun` to choose another package manager. Each generated
project contains only that package manager's lock file and a local
`dotnet-runic` tool manifest.

The app opens through CS-WebUI in an installed browser in app mode (Chrome,
Edge or another Chromium-based browser works best), with the platform WebView
as a fallback. `dotnet publish` writes the executable and a `www` folder with
the built frontend; users need no Node.js. The generated README explains the
project layout and settings.

## Test a local candidate

Install the template from a local NuGet feed. Every project also needs the
matching candidate npm archives (`@runic-artifex/views` and the framework
binding) through a local `@runic-artifex` registry before its first restore:

```bash
dotnet new install Runic.Application.Templates::<CANDIDATE> --nuget-source /path/to/nuget-feed
dotnet new runic-app-svelte -n MyCandidateApp
cd MyCandidateApp/Frontend
npm config set --location=project @runic-artifex:registry http://127.0.0.1:<PORT>
```

`<CANDIDATE>` and the npm registry must come from the same package set. The
SDK's template acceptance check (`bun run verify:templates`) runs the commands
above for every framework and package manager. Setting
`RUNIC_APPLICATION_SERVE_ONLY=1` starts a generated app's local server without
opening a browser and prints `RUNIC_APPLICATION_URL=<url>`.

See the [getting-started guide](https://docs.runic-artifex.eu/getting-started/), [template source](https://github.com/Runic-Artifex/runic-sdk/tree/main/tools/Runic.Application.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples), and [issues](https://github.com/Runic-Artifex/runic-sdk/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
