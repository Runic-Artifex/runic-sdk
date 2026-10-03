# CommunityToolkit Notes

A notes application with nested content, dialogs, and two windows, built with
CommunityToolkit.Mvvm ViewModels and explicit Runic Windows and Views.

The [ViewModels](ViewModels.cs) use CommunityToolkit.Mvvm. A partial
[Window](NotesWindow.cs) owns the scoped root ViewModel. Partial
[Views](Views.cs) select which content each ViewModel property presents, hold a
typed `DataContext`, and receive attached and web-mounted lifecycle callbacks.
The build runs after the MVVM source generators and emits C# attachments plus
one TypeScript client module per ViewModel.

```text
NotesWindow<ShellViewModel>
  Shell.Main -> HomeView or DocumentView
    Document.CurrentPane -> EditorView or PreviewView
  Shell.Sidebar -> SidebarView (independent)
  Shell.Dialog -> ConfirmNavigationView (transient)
```

The browser owns the component tree; the plain TypeScript (`Frontend`),
`Svelte`, and `Angular` frontends in this folder use the same generated
clients and the shared `@runic-artifex/views` runtime; the Svelte and Angular
pages connect them with `useView` and `injectView()` from the workspace
packages. A .NET View is a logical presentation object whose lifetime follows
the browser outlet that mounts it. Microsoft DI owns one scope per window, and
[`NotesApplication`](NotesApplication.cs) calls `AddRunicViews()` to register
the Views and the default View locator. The `--splat` variant locates Views
through ReactiveUI's Splat instead, with the same scoped ViewModels.

## Run it from this repository

```sh
bun run bootstrap
bun run example:notes
```

To use the Svelte or Angular frontend, build it and point the application at
its output:

```sh
(cd examples/notes-view-first/Svelte && bun install --frozen-lockfile && bun run build)
dotnet run --project examples/notes-view-first/NotesViewFirst.csproj -- --web-root "$PWD/examples/notes-view-first/Svelte/dist"
```

This example builds the Runic packages from source. To copy it out, replace the
`ProjectReference` and `Import` lines in `NotesViewFirst.csproj` with a
`Runic.Application.CsWebUi` package reference, as described in the
[First Window README](../first-window/README.md#copy-it-into-your-own-project).

## Checks

[ViewLifetimeCheck.cs](ViewLifetimeCheck.cs) checks repeated pane changes, web
mounts, and native route reuse. The [browser check](browser-smoke.mjs) covers
editing, preview, a modal, navigation, asynchronous detach, and reload;
[window-smoke.mjs](window-smoke.mjs) checks that two windows get distinct
scopes. CS-WebUI keeps a native route registration until its window closes, so
retained page references reuse routes while new page identities add routes.

```sh
dotnet build examples/notes-view-first/NotesViewFirst.csproj -c Release
dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime
dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime --splat
node examples/notes-view-first/browser-smoke.mjs
node examples/notes-view-first/window-smoke.mjs
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Svelte/dist" node examples/notes-view-first/browser-smoke.mjs
```

On Linux with Nix, prefix the commands with `direnv exec .`.
