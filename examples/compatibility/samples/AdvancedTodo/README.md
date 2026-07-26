# Advanced ToDo

**Difficulty:** Advanced  
**Experience:** Native desktop window powered by local HTML, CSS, and JavaScript  
**Host:** `CsWebUi` 2.5.0-beta.4.3 through `WebUIToolkit.Hosting.CsWebUi`

This sample grows a task list into a small, understandable desktop application.
It is independently inspired by the idea of an advanced ToDo sample—not by
another sample's code or text. Its purpose is to show where concerns belong
once an app needs persistence, multiple projections of its data, cancellable
work, workflow navigation, and useful diagnostics.

## What it demonstrates

- A responsive desktop UI rendered by an installed browser or native WebView,
  with assets served locally by cs-webui.
- The WebUIToolkit lifecycle:
  `GenericHostWebUIToolkitApplicationBuilder` → `WebUiModeRunner` →
  `CsWebUiBrowserHostFactory`.
- Explicit, reflection-free `WebUIToolkit.MVVM` property and command bindings
  backed by `CommunityToolkit.Mvvm` generated members.
- Thin `WebUiWindow.BindAsync` callbacks that translate browser arguments into
  revisioned MVVM session mutations.
- Validation before mutation, with errors returned as application state.
- Search plus All / Active / Completed projections.
- JSON persistence with serialized mutations and atomic file replacement.
- An async starter-task import that can be cancelled through an
  `MvvmCancelRequest` before it persists.
- A typed `WebUIToolkit.MVVM.Flow` Details → Review workflow, including
  validation, retained Back navigation, cancellation, and a typed result.
- A bounded in-app activity feed that explains outcomes without displaying
  capabilities, request IDs, file paths, or arbitrary exception text.

## Run

From the repository root:

```bash
direnv allow
dotnet run --project samples/AdvancedTodo
```

The Nix development shell supplies the cs-webui native library and browser
dependencies. The native window closes the WebUIToolkit root session and host
when the user closes it.

Tasks persist in the platform's local application-data directory, under:

```text
WebUIToolkit/AdvancedTodo/todos.json
```

Set `ADVANCED_TODO_DATA` to choose an explicit file:

```bash
ADVANCED_TODO_DATA=/tmp/advanced-todo.json \
  dotnet run --project samples/AdvancedTodo
```

Delete that JSON file to reset the demo.

To exercise the non-GUI application paths:

```bash
dotnet run --project samples/AdvancedTodo -- --self-test
```

The self-test uses its own temporary directory and covers JSON persistence,
pre-mutation cancellation, workflow validation, navigation, and typed
completion. It does not start cs-webui or open a window.

## Architecture

```text
Native cs-webui window
        │ local index.html / CSS / JavaScript
        │ WebUiWindow.BindAsync callbacks
        ▼
UI/NativeTodoController
        │ revisioned property + command requests
        ▼
WebUIToolkit.MVVM session
        │ explicit CommunityToolkit adapter
        ▼
UI/TodoViewModel
      │             │
      ▼             ▼
TodoService    TodoCreationFlow
      │             │
      ▼             ▼
JsonTodoRepository  WebUIToolkit.MVVM.Flow
      │
      ▼
  todos.json
```

The native host has a separate responsibility from the application:

- `CsWebUiBrowserHostFactory` adapts cs-webui window ownership, dispatcher
  access, navigation, close notification, and teardown to neutral Hosting
  contracts.
- `WebUiModeRunner` enforces the runtime → window → root session → navigation →
  show sequence and reverses ownership on shutdown.
- `NativeTodoController` binds this app's named UI operations. It does not own
  native process/window lifetime.

The remaining layers stay host-independent:

- `Domain` contains immutable task data and its priority vocabulary.
- `Application` owns use cases, filtering vocabulary, persistence contracts,
  and the typed creation workflow.
- `UI/TodoViewModel` owns observable state and generated commands.
- `Infrastructure/JsonTodoRepository` owns the local persistence mechanism.
- `wwwroot` owns presentation and calls only the controller's named boundary.

`TodoService` serializes read-modify-write operations so later multi-window
extensions do not lose updates. Search and filtering remain presentation-only.

## Code tour

Read these files in order:

1. `Domain/TodoItem.cs` — the immutable domain model.
2. `Application/TodoService.cs` — use cases and the safe cancellation boundary.
3. `Infrastructure/JsonTodoRepository.cs` — async, atomic JSON persistence.
4. `UI/TodoViewModel.cs` — observable state and commands; no browser types.
5. `Application/TodoCreationFlow.cs` — the typed two-step Flow definition,
   validation, and presentation leases.
6. `UI/NativeTodoController.cs` — explicit CommunityToolkit bindings, retained
   MVVM session, revisioned mutations, and cs-webui callbacks.
7. `Program.cs` — host composition and lifecycle policy.
8. `wwwroot/advanced-todo.js` — DOM projection; user text is assigned with
   `textContent`, not interpolated into HTML.

## Production boundary

This is a real local desktop host, not an ASP.NET application and not a fake
console UI. cs-webui owns its private local server and browser/WebView window.
The sample keeps `SetPublic(false)` through the adapter default and serves only
its local `wwwroot`.

For a larger app, split `NativeTodoController` by feature, move snapshot DTOs
into a dedicated presentation-contract assembly, and add structured telemetry.
If windows need to share live changes, add an application event stream and
refresh each retained MVVM session from repository notifications.

## Things to try

1. Submit a one-character title and inspect inline validation.
2. Search for text that exists only in Notes.
3. Start the two-second import and cancel it; no partial starter set is saved.
4. Walk through the guided planner, go Back, and finish.
5. Reload the app and confirm that tasks survived in JSON.
6. Set `ADVANCED_TODO_DATA` to compare two independent data stores.
