# RunicToolkit.Templates

Create a native CS-WebUI desktop app with a working Application Bridge and React, Vue, Svelte, or Angular frontend.

```bash
dotnet new install RunicToolkit.Templates::<VERSION>
dotnet new runic-toolkit-svelte --name MyApp
cd MyApp
dotnet run
```

Requires the .NET 10 SDK, Node.js 24.18 or later, npm, and CS-WebUI platform support. Replace `<VERSION>` with the current preview shown on [NuGet](https://www.nuget.org/packages/RunicToolkit.Templates), and replace `svelte` with `react`, `vue`, or `angular` to select the frontend. The explicit preview version is intentional: a template must create a version-matched NuGet/npm package set.

```bash
dotnet run -- --smoke-test
```

Each template includes a counter contract, generated C# dispatcher, frontend controller, and production asset build. Svelte uses the Runic Svelte/SvelteKit path; React, Vue, and Angular consume the controller directly. See the [template source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/templates/RunicToolkit.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
