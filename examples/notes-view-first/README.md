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

## Navigation

`AddNotes` calls `AddRunicNavigation()`, so each window scope has one
`RunicNavigator` from the `Runic.Navigation` package and namespace.
`WorkspaceNavigation` creates two regions on it: `Main`
starts with the window's Home, and `Dialog` starts empty. `ShellViewModel`
exposes both regions as content slots. The generated `main` and `dialog`
references are `| null`, so every frontend handles an empty region.

- **Notes** pushes a new owned `DocumentViewModel` on each visit with
  `Main.PushAsync<DocumentViewModel>()`, which builds it from the window's
  services. Leaving the document retires it and its `CurrentPane` child
  region. The editor and preview are window-scoped and borrowed, so the draft
  survives the visit.
- `CurrentPane` keeps the editor entry when the preview is pushed. **Editor**
  goes back to that same entry, with its draft.
- **Home** goes back. When the editor has unsaved edits, the document's
  departure guard, a `LeaveConfirmation.InDialog(…)`, pushes
  `ConfirmNavigationViewModel` into `Dialog` for a `bool` result. Confirming
  lets the Back commit, and the edits are discarded in the commit turn. A
  Back that is superseded, rejected or fails keeps the draft. Cancelling, or
  a dismissal, keeps the document entry. Confirm calls `CompleteAsync`, which
  returns from its entry and leaves `Dialog` empty. Cancel calls
  `DismissAsync`, which ends the question at once, also when its Back can't
  commit, so Cancel and Escape always end the question.
- The confirm is modal. While it asks, or while a page navigation is in
  flight, `WorkspaceNavigation.CanNavigate` is false. The sidebar's Home and
  Notes commands and the document's Editor and Preview commands are
  unavailable then, and the Bridge rejects them. The navigation methods check
  it too. A pane change during the guard would otherwise supersede the Back
  that asks and dismiss the dialog without an answer.
- The sidebar commands await the navigation, so a command started during the
  guard's wait completes after the dialog answers. Its button stays disabled
  until then. [focus.ts](Frontend/src/focus.ts) returns focus to any button
  that lost it by being disabled, once it is enabled again.

**Command ordering and guards.** A client that runs the window's commands one
after another, waiting for each to complete, deadlocks with a guard that
awaits the UI: the Home command waits for the dialog's answer, and the
answer waits in the queue behind the Home command. The Angular frontend's
ordered [`WindowOperations`](Angular/src/app/window-operations.ts) queue
therefore starts navigation commands with `runDispatched`, which orders them
by dispatch, not by completion. Use the same pattern for any command whose
completion depends on a later command.

The navigation types are experimental, and the projects suppress
`RUNICNAV001`.

## Declared failures

`EditorViewModel.Save` declares `[RunicFailure(typeof(SaveFailure))]`, a
`[RunicUnion]` of `TitleRequired` and `TitleTaken`, and throws
`RunicFailureException` instead of an exception the Bridge would report as
`rejected`. The generated `save()` resolves a `BridgeOutcome`, so every variant
checks `outcome.ok` and shows the failure with
[`describeSaveFailure`](Frontend/src/save-failure.ts), which handles each case
with `matchCase`. The Angular, React and Vue editors read it from their command
helper's `failure`, Svelte from `useCommand` too, and the Effect editor from the
`ViewDomainFailure` error of `@runic-artifex/views-effect`.

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
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Angular/dist/angular-composed/browser" node examples/notes-view-first/browser-smoke.mjs
```

On Linux with Nix, prefix the commands with `direnv exec .`.
