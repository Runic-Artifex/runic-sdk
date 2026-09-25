# Notes with explicit Window and Views

This SDK example uses the replacement View bridge from `Runic.Application.Views`.
It depends on no `Runic.Application.Bridge` package or compatibility adapter.
CS-WebUI is the native host while the View and ViewModel model is evaluated.

The [ViewModels](ViewModels.cs) use CommunityToolkit.Mvvm. A partial
[Window](NotesWindow.cs) owns the scoped root context. Partial
[Views](Views.cs) select content contracts, hold typed `DataContext`, and receive
attached and web-mounted lifecycle callbacks. The generator runs after the MVVM
source generators and emits C# attachments plus ordinary TypeScript modules.
No `[RunicViewModel]` annotation is needed in this app.

```text
NotesWindow<ShellViewModel>
  Shell.Main -> HomeView or DocumentView
    Document.CurrentPane -> EditorView or PreviewView
  Shell.Sidebar -> SidebarView (independent)
  Shell.Dialog -> ConfirmNavigationView (transient)
```

The browser owns the component tree. Plain TypeScript, Svelte, and Angular
consumers use the same generated contract. The .NET View is a logical
presentation object; its mount lifetime follows the browser outlet. Microsoft
DI owns one scope per window and constructs transient Views. The `--splat`
variant uses ReactiveUI view location while retaining the same scoped
ViewModels. [ViewLifetimeCheck.cs](ViewLifetimeCheck.cs) checks repeated pane
changes, web mounts, and native route reuse. The [browser check](browser-smoke.mjs)
exercises editing, preview, a modal, navigation, asynchronous detach, and
reload; [window-smoke.mjs](window-smoke.mjs) checks distinct scopes.

From this SDK worktree, use its locked environment:

```sh
direnv exec . dotnet build examples/notes-view-first/NotesViewFirst.csproj -c Release
direnv exec . dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime
direnv exec . dotnet run --no-build -c Release --project examples/notes-view-first/NotesViewFirst.csproj -- --check-view-lifetime --splat
direnv exec . node examples/notes-view-first/browser-smoke.mjs
direnv exec . node examples/notes-view-first/window-smoke.mjs
```

Build the `Svelte` or `Angular` frontend in this folder and set
`RUNIC_WEB_ROOT` to its output directory when running the browser check. The
build uses the generated modules in `Frontend/src/generated`. For example:

```sh
direnv exec . bun --cwd examples/notes-view-first/Svelte run build
RUNIC_WEB_ROOT="$PWD/examples/notes-view-first/Svelte/dist" direnv exec . node examples/notes-view-first/browser-smoke.mjs
```

`OpenWindow` constructs the application `NotesWindow` before attaching its
root Bridge. CS-WebUI still keeps native route registrations
until window close; retained page references reuse registrations, while new
page identities add routes. These limits are recorded in
[the cutover plan](../../VIEW-BRIDGE-CUTOVER.md).
