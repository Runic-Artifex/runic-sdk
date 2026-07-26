# Simple Todo

**Difficulty:** Beginner  
**Style:** Cross-platform desktop UI with CsWebUi  
**Concept:** An original implementation of the familiar todo-list learning
exercise, built on WebUIToolkit's intended application stack.

This is a real desktop application, not a test fixture. It demonstrates how to:

- host local HTML, CSS, and JavaScript in a native CsWebUi window;
- keep state and commands in a UI-independent CommunityToolkit.Mvvm ViewModel;
- add, toggle, and remove items from `ObservableRangeCollection<T>`;
- map explicit properties and commands into an ordered WebUIToolkit MVVM session;
- return an authoritative, source-generated JSON snapshot after every action; and
- let `WebUiModeRunner` own validation, startup, window, root-session, and shutdown.

## Run it

From the repository root:

```bash
dotnet run --project samples/SimpleTodo
```

CsWebUi opens the installed browser or supported WebView as a desktop window.
Close the window to stop the application.

No npm install, frontend build, database, external CDN, or separate web server
is involved. On NixOS, enter the repository's direnv shell first so the pinned
native WebUI library and browser dependencies are available.

The non-visual state and command path has a smoke-test mode:

```bash
dotnet run --project samples/SimpleTodo -- --smoke-test
```

## Architecture

```text
Local HTML/CSS/JS
  └─ four explicit CsWebUi bindings
       └─ TodoBackend
            └─ ordered WebUIToolkit MVVM session
                 └─ CommunityToolkit property/command adapter
                      └─ TodoViewModel

WebUiModeRunner
  ├─ validates the local asset manifest
  ├─ creates the CsWebUi window
  ├─ activates the root MVVM session
  └─ reverses that ownership when the window closes
```

CsWebUi and WebUIToolkit have distinct jobs here. CsWebUi owns the desktop
window, local asset server, and JavaScript-to-.NET callback bridge.
WebUIToolkit owns the application lifecycle and the revisioned MVVM dispatch
boundary. The browser receives only the four capabilities registered by this
application.

## Guided tour

1. Start with `TodoViewModel.cs`. It is ordinary MVVM code with generated
   observable properties, validation annotations, and relay commands with
   `CanExecute` rules. There is no browser code in it.
2. Read `TodoBackend.cs`. Stable member IDs map properties and commands to
   direct delegates. Each browser action becomes a property mutation followed
   by a command mutation against one ordered MVVM session.
3. Open `www/app.js`. It knows only four backend capabilities:
   `todoSnapshot`, `todoAdd`, `todoToggle`, and `todoRemove`. It renders the
   complete state returned by the backend and uses `textContent`, not raw HTML,
   for task titles.
4. Finish in `Program.cs`. `CsWebUiBrowserHostFactory` adapts the native
   CsWebUi window to WebUIToolkit's browser contracts, while
   `WebUiModeRunner` composes it with the manifest validator and root session.

## Deliberate simplifications

The tasks live in memory and one window owns one ViewModel session. Persistence,
editing, filtering, multiple lists, navigation, and background work belong in
the Advanced Todo sample.
