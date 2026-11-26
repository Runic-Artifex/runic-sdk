## A solution with a client library: `dotnet new runic-app --library`

`--library <Name>` creates the application together with a library project for ViewModels and Views that more than
one application can present, ready to run from a fresh clone:

```sh
dotnet new runic-app -n MyApp --library MyApp.Core
cd MyApp
dotnet tool restore
dotnet runic dev --project MyApp
```

- **A solution:** `MyApp.slnx` lists the application first, so Visual Studio starts it, then the library and, with
  `--tests`, the test project. The application moves to `MyApp/`; the tool manifest, `.gitignore`, `global.json` and
  `.vscode/` stay at the solution's root, and `.vscode/` launches and debugs the application from there.
- **A client library:** `MyApp.Core` sets `RunicApplicationFrontendClientLibrary` and declares `AboutViewModel` and
  its View. Its build generates the View's TypeScript client into its frontend package, `MyApp.Core/Frontend`, whose
  `build` script compiles it into `dist/`. The application references the library, maps it with
  `RunicApplicationFrontendClientModule`, presents `AboutViewModel` as `WorkspaceViewModel.About`, and its page shows
  the library's about panel with the client it imports from the package.
- **One JavaScript workspace:** the root `package.json` lists both frontends and the package manager, and the template
  ships a workspace lock for every frontend and package manager, eighteen in all, so no install is ever unlocked.
  `dotnet build` and `dotnet runic dev` install the workspace once at its root and build the library's package before
  the application's frontend, with no manual step.
- Every frontend works with it: React, Vue, Svelte, SvelteKit, and Angular with the Angular CLI or Vite. It does not
  combine with `--translations` or `--nix` yet; the template leaves them out.

The guide is [A solution with a library](../../../tools/Runic.Application.Templates/README.md#a-solution-with-a-library).
