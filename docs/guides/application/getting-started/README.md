# Getting started

Create a starter app with the guided creator and run it:

```sh
dnx Runic.Create@<VERSION>
cd MyApp
dotnet tool restore
dotnet runic dev
```

`dnx` ships with the .NET 10 SDK and runs the creator without installing it.
The creator asks for the project name, frontend, package manager, Window host,
and ViewModel library. It installs `Runic.Application.Templates`, runs
`dotnet new runic-app`, and prints both commands so you can recreate the
project without questions:

```sh
dotnet new install Runic.Application.Templates@<VERSION>
dotnet new runic-app --name MyApp --frontend svelte --package-manager bun --host desktop --view-models reactiveui
```

| Option              | Choices                                         | Default   |
| ------------------- | ----------------------------------------------- | --------- |
| `--frontend`        | `react`, `vue`, `svelte`, `angular`             | `react`   |
| `--package-manager` | `npm`, `pnpm`, `bun`                            | `npm`     |
| `--host`            | `cswebui`, `desktop`                            | `cswebui` |
| `--view-models`     | `toolkit` (CommunityToolkit.Mvvm), `reactiveui` | `toolkit` |

You need the .NET 10 SDK and Node.js 24 with npm or pnpm, or Bun 1.4.

`dotnet runic dev` restores the .NET and frontend packages, builds the app,
starts the frontend development server, and opens the app. The CS-WebUI host
uses an installed browser's app mode, falling back to the platform WebView; the
Runic Desktop host opens a native window with the embedded WebView.
`dotnet runic doctor` checks the prerequisites at any point. The generated
README describes the project layout, the ignored `Frontend/src/generated`
clients, publishing, and the project settings. The
[project creator](https://docs.runic-artifex.eu/create/) builds the command for
your choices and previews the generated files.

The starter uses a .NET Window and View contract with a generated TypeScript
client. The frontend owns its rendered components; .NET owns the typed model and
operation lifetime. For native windows and platform services, see
[host selection](../../desktop/host-selection.md).

For the smallest source example, see [First Window](../../../../examples/first-window/README.md).
For existing projects, add `Runic.Application.CsWebUi` or
`Runic.Application.Desktop` as described in the
[`Runic.Application` package guide](../../../../packages/dotnet/Runic.Application.Views/README.md).
