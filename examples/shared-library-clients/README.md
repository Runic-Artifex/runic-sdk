# Shared library clients

A .NET library holds ViewModels that several applications present, and a
frontend package holds the components for them. The library's build generates
their TypeScript clients once, into that package, and an application imports
them from the package instead of generating its own copies. One application is
enough to show it; a second would map the library the same way.

| File | What it does |
| --- | --- |
| [`SharedUi/SharedUi.csproj`](SharedUi/SharedUi.csproj) | A client library: `RunicApplicationFrontendClientLibrary=true`, with `RunicApplicationFrontendDirectory` set to the `shared-ui` package. |
| [`SharedUi/AboutViewModel.cs`](SharedUi/AboutViewModel.cs), [`SupportViewModel.cs`](SharedUi/SupportViewModel.cs) | Two ViewModels: an about panel with a writable update channel and a command, and a support menu with a command. |
| [`SharedUi/Views.cs`](SharedUi/Views.cs) | Their Views. The library declares them, so it owns their TypeScript. |
| [`shared-ui/package.json`](shared-ui/package.json) | The frontend package `@acme/shared-ui`: `exports` for its components, `./generated` (the clients) and `./generated/mocks`, and `@runic-artifex/views` as a peer dependency. |
| [`shared-ui/src/about-panel.ts`](shared-ui/src/about-panel.ts) | `mountAboutPanel`, a component typed by the generated `AboutClient`. |
| [`shared-ui/src/generated/`](shared-ui/src/generated/runic-clients.ts) | What SharedUi's build writes: a client, contract and mock per ViewModel, `types.ts`, `runic-clients.ts` and `runic-mocks.ts`. |
| [`App/SharedLibraryApp.csproj`](App/SharedLibraryApp.csproj) | The application. It references SharedUi and maps it with `RunicApplicationFrontendClientModule`, and lists the package's sources as `RunicApplicationFrontendInput`. |
| [`App/ShellViewModel.cs`](App/ShellViewModel.cs), [`ShellWindow.cs`](App/ShellWindow.cs) | The application's own ViewModel, which presents the two library ViewModels as content, and its Window. It declares no View of the library's ViewModels. |
| [`App/Frontend/src/main.ts`](App/Frontend/src/main.ts) | The Vite page: it connects the shell, mounts the package's about panel, and renders the support menu with `SupportClient` from `@acme/shared-ui/generated`. |

## What each build writes

`dotnet build App/SharedLibraryApp.csproj` builds SharedUi first. SharedUi's
build writes `shared-ui/src/generated/`. It builds and copies no frontend, and
the application does not copy the package to its `www/`.

The application's build generates the C# Bridges of all three ViewModels,
because its `ShellViewModel` presents the library's, and registers SharedUi's
Views in `AddRunicViews()`. Its generated folder,
[`App/Frontend/src/generated/`](App/Frontend/src/generated/shell.ts), holds
only `shell.ts` with its contract and mock, and `runic-client-modules.ts`.
`shell.ts` imports `pageAbout` and `pageSupport` from
`@acme/shared-ui/generated`. When the package was generated from another build
of SharedUi than the application compiles against, `runic-client-modules.ts`
fails the frontend's type check with `TS2344`, naming both contracts.

The Views README has the full guide:
[Share ViewModels and their clients through a library](../../packages/dotnet/Runic.Application.Views/README.md#share-viewmodels-and-their-clients-through-a-library).

## How the page finds the package

Inside this repository nothing installs the examples' frontends: the
repository's root install provides Vite, TypeScript and `@runic-artifex/views`.
So the application finds `@acme/shared-ui` in its folder, through `paths` in
[`App/Frontend/tsconfig.json`](App/Frontend/tsconfig.json) and an alias in
[`App/Frontend/vite.config.ts`](App/Frontend/vite.config.ts).

A real application installs the package instead. In a Bun or npm workspace,
list both folders in the root `package.json`, depend on the package, and
delete the `paths` and the alias:

```json
{ "private": true, "workspaces": ["shared-ui", "App/Frontend"] }
```

```json
{
  "dependencies": {
    "@acme/shared-ui": "workspace:*",
    "@runic-artifex/views": "<VERSION>"
  }
}
```

Keep `resolve.dedupe: ["@runic-artifex/views"]` in the Vite config: a linked
package can resolve its own copy of the runtime, and the page must load one.
Replace the `ProjectReference` and `Import` lines of both projects with package
references, as the
[First Window README](../first-window/README.md#copy-it-into-your-own-project)
shows.

## Run it from this repository

```sh
bun run bootstrap
dotnet run --project examples/shared-library-clients/App -c Release
```

On Linux with Nix, prefix commands with `direnv exec .` to use the SDK's
locked environment.

## Checks

The SDK's CI builds both projects and runs the browser check. The check
confirms that the application generated no copy of the library's clients and
copied no frontend of the library. Then, in Chromium, it renders the page,
runs the about panel's command and channel write and the support menu's
command.

```sh
dotnet build examples/shared-library-clients/App/SharedLibraryApp.csproj -c Release
node examples/shared-library-clients/browser-smoke.mjs
```
