# Runic.Application.Templates

Create a Runic Desktop app with a working Application Bridge and React, Vue, Svelte, or Angular frontend.

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-svelte --name MyApp --packageManager pnpm
cd MyApp
dotnet tool restore
dotnet runic doctor
dotnet run
```

Requires the .NET 10 SDK, either Node.js 24.18 with npm 11.16 or pnpm 11.25, or Bun 1.4, plus the platform prerequisite reported by `dotnet runic doctor`. Replace `<VERSION>` with the current preview shown on [NuGet](https://www.nuget.org/packages/Runic.Application.Templates), and replace `svelte` with `react`, `vue`, or `angular` to select the frontend. Select `npm`, `pnpm`, or `bun` with `--packageManager`; npm is the default. The explicit preview version is intentional: a template must create a version-matched NuGet/npm package set.

Every generated project contains a local `dotnet-runic` tool manifest and exactly one package-manager lock file. Standard `dev`, `build`, and `typecheck` scripts hide the underlying Vite or Angular CLI syntax, while frozen installs keep the selected graph deterministic. Publishing embeds the static frontend output, so no JavaScript runtime or package manager is required on the target machine.

Vite+ is supported as an optional command facade over the selected manager: use `vp install --frozen-lockfile` and `vp run dev|build|typecheck`. Keep the generated npm, pnpm, or Bun `packageManager` declaration and lock file; for Angular, `vp run dev` invokes the project script and therefore `ng serve`, while the Vite+-built-in `vp dev` does not.

For a local candidate, install the canonical template package from the local
NuGet feed and configure only the generated frontend's `@runic-artifex` npm
scope to the matching local npm registry before the frozen install:

```bash
dotnet new install Runic.Application.Templates::<CANDIDATE> --nuget-source /path/to/nuget-feed
dotnet new runic-app-svelte --name MyCandidateApp --packageManager pnpm
cd MyCandidateApp/Frontend
pnpm config set --location=project @runic-artifex:registry http://127.0.0.1:<PORT>
pnpm install --frozen-lockfile --ignore-scripts
```

`<CANDIDATE>` and the npm registry must be from the same local candidate set;
the generated project references `Runic.Application`, `Runic.Application.Bridge`,
and the `@runic-artifex` frontend packages by their exact candidate versions.

From the generated project root, use `dotnet run -- --smoke-test` for a headless bridge check.

Each template includes a counter contract, generated C# dispatcher, frontend controller, and production asset build. Svelte uses the Runic Svelte/SvelteKit path; React, Vue, and Angular consume the controller directly. See the [template source](https://github.com/Runic-Artifex/runic-toolkit/tree/main/templates/RunicToolkit.Templates), [runnable examples](https://github.com/Runic-Artifex/runic-toolkit-examples), and [issues](https://github.com/Runic-Artifex/runic-toolkit/issues). Preview package; [MIT licensed](https://github.com/Runic-Artifex/runic-toolkit/blob/main/LICENSE).
