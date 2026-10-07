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

The `Effect` folder is the plain frontend with its Editor written with
[`@runic-artifex/views-effect`](../../packages/web/views-effect/README.md):
field writes and Save run as Effects, and Save is an operation with an explicit
request ID, retries and a timeout that cancels it in .NET. Its build swaps
[`Effect/src/editor.ts`](Effect/src/editor.ts) in for `Frontend/src/editor.ts`,
so the plain bundle does not change:

```sh
(cd examples/notes-view-first/Effect && bun run --bun build)
dotnet run --project examples/notes-view-first/NotesViewFirst.csproj -- --web-root "$PWD/examples/notes-view-first/Effect/dist"
```

The `React` and `Vue` folders swap in an Editor written with
[`@runic-artifex/react`](../../packages/web/react/README.md) or
[`@runic-artifex/vue`](../../packages/web/vue/README.md) the same way. They use
the bindings' source and the React or Vue install of those packages, so they
need no install of their own:

```sh
(cd examples/notes-view-first/React && bun run --bun build)
(cd examples/notes-view-first/Vue && bun run --bun build)
```

## Declared failures

`EditorViewModel.Save` declares `[RunicFailure(typeof(SaveFailure))]`, a
`[RunicUnion]` of `TitleRequired` and `TitleTaken`, and throws
`RunicFailureException` instead of an exception the Bridge would report as
`rejected`. The generated `save()` resolves a `BridgeOutcome`, so every variant
checks `outcome.ok` and shows the failure with
[`describeSaveFailure`](Frontend/src/save-failure.ts), which handles each case
with `matchCase`. The Angular, React and Vue editors read it from their command
helper's `failure`. The Effect editor reads it from `ViewOperationFailed` until
`@runic-artifex/views-effect` has its own tag for declared failures.

This example builds the Runic packages from source. To copy it out, replace the
`ProjectReference` and `Import` lines in `NotesViewFirst.csproj` with a
`Runic.Application.CsWebUi` package reference, as described in the
[First Window README](../first-window/README.md#copy-it-into-your-own-project).

## Tests

[Tests](Tests/NotesWindowTests.cs) is an xUnit project that drives the real
ViewModels and generated Bridges with `RunicWindowTestHost` from
[Runic.Application.Testing](../../packages/dotnet/Runic.Application.Testing/README.md).
Storage waits on the injected `TimeProvider`, so a test advances a fake clock
to finish a save. Home lists saved notes in a `[RunicCollection]`; a test checks
that saving a note again arrives as keyed collection changes.

[Frontend/test](Frontend/test/notes.test.ts) tests the frontend against the
generated typed mocks (`src/generated/*.mock.ts`) with `bun test`, without .NET or
a browser.

```sh
dotnet test examples/notes-view-first/Tests/NotesViewFirst.Tests.csproj
(cd examples/notes-view-first/Frontend && bun install --frozen-lockfile && bun run test)
```

## Checks

[ViewLifetimeCheck.cs](ViewLifetimeCheck.cs) checks repeated pane changes, web
mounts, and native route reuse. The [browser check](browser-smoke.mjs) covers
editing, Save's declared failure, preview, a modal, navigation, asynchronous
detach, and reload;
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
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Effect/dist" node examples/notes-view-first/browser-smoke.mjs
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/React/dist" node examples/notes-view-first/browser-smoke.mjs
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Vue/dist" node examples/notes-view-first/browser-smoke.mjs
```

On Linux with Nix, prefix the commands with `direnv exec .`.
