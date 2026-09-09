# Getting started

Install the published application templates from NuGet.org. Choose an exact
`<VERSION>` from the [published package catalog](https://docs.runic-artifex.eu/packages/)
and use the matching NuGet and npm package set.

```bash
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-svelte --name MyApp --packageManager pnpm
cd MyApp
dotnet tool restore
dotnet runic doctor
dotnet run
```

Install the .NET 10 SDK and a supported JavaScript package manager; the
[template guide](../../../../tools/Runic.Application.Templates/README.md) describes
runtime requirements and package-manager choices. `dotnet runic doctor` checks
platform prerequisites. Replace `svelte` with `react`, `vue`, or `angular` to
choose a frontend. Generated applications use public NuGet.org and npm packages;
GitHub package-feed credentials are not required.

The template supplies a version-matched tool manifest, frontend lockfile, bridge
contract, generated dispatcher, and production asset build. Run
`dotnet run -- --smoke-test` for a headless bridge check. Publishing embeds the
built frontend, so deployed applications do not need a JavaScript toolchain.

For an existing application, select only the packages your host and frontend
need from the catalog. `Runic.Application.Bridge` bundles its source generator;
do not add a separate `Runic.Application.Bridge.Generators` package reference.
See [Application Bridge](../guides/application-bridge.md) and
[framework integrations](../guides/frontend-frameworks.md).

Explore [runnable examples](../../../../examples/README.md) for complete applications.
To change the SDK itself, follow [source development](../contributing/development.md).
