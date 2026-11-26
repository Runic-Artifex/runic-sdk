# RunicWindowApp

A .NET 10 desktop application with a __FRONTEND_NAME__ frontend, and a library
beside it, RunicCoreLibrary, created from the Runic application template with
`--library RunicCoreLibrary`. The library holds ViewModels and Views that more
than one application can present. Its build generates their TypeScript clients
into the library's own frontend package, which the application's page imports
instead of generating copies.

## Run it

```sh
dotnet tool restore
dotnet runic dev --project RunicWindowApp
```

On a fresh clone nothing else is needed. `dotnet runic dev` and
`dotnet build` restore the NuGet packages and install the JavaScript workspace
once, at this folder, with __PACKAGE_MANAGER_NAME__ from its lock file. Then the
library's build generates its clients into `RunicCoreLibrary/Frontend` and builds
that package, before the application's frontend, which imports it, builds or
is served. `dotnet runic dev` serves the application's frontend from the
__DEVELOPMENT_SERVER_NAME__ development server and opens the Window.
`dotnet build` builds the whole solution, `RunicWindowApp.slnx`. Visual Studio
and Rider open it and start the application, its first project.

The application's README, `RunicWindowApp/README.md`, describes the application:
its Window, debugging in your IDE, tests, publishing and packaging. Run its
commands in `RunicWindowApp/`, or pass `--project RunicWindowApp` to
`dotnet runic`.

## Solution layout

| Path | Purpose |
| --- | --- |
| `RunicWindowApp.slnx` | The solution: the application first, so Visual Studio starts it, then the library. |
| `RunicWindowApp/` | The application project and its frontend, `RunicWindowApp/Frontend`. |
| `RunicCoreLibrary/RunicCoreLibrary.csproj` | The library. `RunicApplicationFrontendClientLibrary` makes it a client library: its build writes the clients of its Views into `Frontend/src/generated` and builds its frontend package; it builds no page of its own. |
| `RunicCoreLibrary/AboutViewModel.cs`, `RunicCoreLibrary/Views.cs` | `AboutViewModel` and its View, `AboutView`. The application shows it as `WorkspaceViewModel.About`. |
| `RunicCoreLibrary/Frontend/package.json` | The library's frontend package, `runic-library-starter`. Its `build` script compiles `src/` into `dist/`, which its exports name: `runic-library-starter` for the code every page shares, `runic-library-starter/generated` for the clients, and `runic-library-starter/generated/mocks` for their mocks. |
| `package.json` | The JavaScript workspace: the two frontend packages and the package manager, __PACKAGE_MANAGER_NAME__. |
<!--#if (packageManager == "npm") -->
| `package-lock.json` | The workspace's lock file, for both frontends. Commit it. |
<!--#elif (packageManager == "pnpm") -->
| `pnpm-lock.yaml`, `pnpm-workspace.yaml` | The workspace's lock file, for both frontends, and pnpm's list of its packages. Commit both. |
<!--#else -->
| `bun.lock` | The workspace's lock file, for both frontends. Commit it. |
<!--#endif -->
| `.config/dotnet-tools.json` | The `dotnet runic` tool, for every project of the solution. |
| `.vsconfig` | The Visual Studio components the application's launch profiles need. |
<!--#if (editor == "vscode") -->
| `.vscode/` | VS Code tasks for `dotnet runic dev` and debug configurations for the application, from this folder. |
<!--#endif -->
<!--#if (tests) -->
| `RunicWindowApp.Tests/WorkspaceWindowTests.cs` | TUnit tests that drive the application's Window through `Runic.Application.Testing`. |
| `global.json` | Runs `dotnet test` on Microsoft.Testing.Platform, which TUnit uses. |
<!--#endif -->

The generated clients and the compiled package are not committed:
`.gitignore` excludes `Frontend/src/generated` and `Frontend/dist` in both
projects, and the next build writes them again.

## How the application uses the library

- `RunicWindowApp/RunicWindowApp.csproj` references the library project and maps
  it with `RunicApplicationFrontendClientModule`: the application's generated
  clients import the library's from `runic-library-starter/generated`. The
  application's build still generates the C# Bridges of the library's
  ViewModels, and `AddRunicViews()` registers the library's Views.
- `WorkspaceViewModel.About` presents an `AboutViewModel`, which
  `AddWorkspace()` registers.
- The page's about panel, in `RunicWindowApp/Frontend/src`, connects to it with
  the library's `AboutPageReference` and calls `aboutHeading` from the package.
- `RunicWindowApp/Frontend/package.json` depends on `runic-library-starter`,
  which the workspace links to `RunicCoreLibrary/Frontend`.

A change to the library's C# or to its package rebuilds both on the next
`dotnet build`. When the package was built from another build of the library
than the application compiles against, the application's type check fails with
`TS2344`, naming both contracts: build the solution again.

To share the library with a second application, add the application to the
solution and to the workspace's packages, reference the library from it, and
map it with `RunicApplicationFrontendClientModule` in the same way.
