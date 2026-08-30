# Runic.Application.Templates

Create a Runic Desktop app with a working Application Bridge and React, Vue, Svelte, or Angular frontend.

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-svelte --name MyApp
cd MyApp
dotnet run
```

Requires the .NET 10 SDK, Node.js 24.18, npm 11.16, and either a supported browser or the platform WebView prerequisite reported by `dotnet runic doctor`. Replace `<VERSION>` with the current preview shown on [NuGet](https://www.nuget.org/packages/Runic.Application.Templates), and replace `svelte` with `react`, `vue`, or `angular` to select the frontend. The explicit preview version is intentional: a template must create a version-matched NuGet/npm package set.

For a local candidate, install the canonical template package from the local
NuGet feed and configure only the generated workspace's `@runic-artifex` npm
scope to the matching local npm registry before `npm install`:

```bash
dotnet new install Runic.Application.Templates::<CANDIDATE> --nuget-source /path/to/nuget-feed
dotnet new runic-app-svelte --name MyCandidateApp
printf '@runic-artifex:registry=http://127.0.0.1:<PORT>\n' > MyCandidateApp/.npmrc
npm --prefix MyCandidateApp/Frontend ci --ignore-scripts
```

`<CANDIDATE>` and the npm registry must be from the same local candidate set;
the generated project references `Runic.Application`, `Runic.Application.Bridge`,
and the `@runic-artifex` frontend packages by their exact candidate versions.

```bash
dotnet run -- --smoke-test
```

Each template includes a counter contract, generated C# dispatcher, frontend controller, and production asset build. Svelte uses the Runic Svelte/SvelteKit path; React, Vue, and Angular consume the controller directly. See the [template source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/templates/RunicToolkit.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
