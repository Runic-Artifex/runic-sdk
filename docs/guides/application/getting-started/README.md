# Getting started

Install the published templates from NuGet and create a starter app:

```sh
dotnet new install Runic.Application.Templates::<VERSION>
dotnet new runic-app-react -n MyApp
cd MyApp
dotnet tool restore
dotnet runic dev
```

Choose `react`, `vue`, `svelte`, or `angular` as the template short name. The
templates use npm by default; add `--packageManager pnpm` or
`--packageManager bun` to `dotnet new` to use another package manager. You need
the .NET 10 SDK and Node.js 24 with npm or pnpm, or Bun 1.4.

`dotnet runic dev` restores the .NET and frontend packages, builds the app,
starts the frontend development server, and opens the app through CS-WebUI in
an installed browser's app mode, falling back to the platform WebView.
`dotnet runic doctor` checks the prerequisites at any point. The generated
README describes the project layout, the ignored `Frontend/src/generated`
clients, publishing, and the project settings. The
[docs site](https://docs.runic-artifex.eu/getting-started/) has the same steps
for each package manager.

The starter uses a .NET Window and View contract with a generated TypeScript
client. The frontend owns its rendered components; .NET owns the typed model and
operation lifetime. For native windows and platform services, see
[host selection](../../desktop/host-selection.md).

For the smallest source example, see [First Window](../../../../examples/first-window/README.md).
For existing projects, add `Runic.Application.CsWebUi` or
`Runic.Application.Desktop` as described in the
[`Runic.Application` package guide](../../../../packages/dotnet/Runic.Application.Views/README.md).
