# Advanced ToDo

**Difficulty:** Advanced
**Experience:** Native desktop UI generated from compiled `.cwhtml`
**Transport:** One `CsWebUiHtmxTransport` binding with opaque per-session routes

This sample grows a task list into a small desktop application while keeping
state, validation, commands, workflow navigation, persistence, and HTML
rendering in C#. npm pins HTMX, Bootstrap 5.3, and Font Awesome; Vite produces
the local development and minified production asset graph served by CsWebUi.

## What it demonstrates

- Compiled `AdvancedTodoDocument.cwhtml` and `AdvancedTodoApp.cwhtml` views.
- A private runtime web root containing the generated initial document.
- One fixed native binding (`webuitoolkitHtmx`) instead of feature-specific
  `WebUiWindow.BindAsync` callbacks.
- Random closed HTMX routes for quick add, search/filter, toggle, delete, clear,
  import, and each workflow transition.
- Server-side quick-add validation with rejected values retained in the form.
- JSON persistence with serialized mutations and atomic file replacement.
- A cancellable two-second starter import. The start command owns background
  work, the cancel command awaits deterministic cancellation, and a declarative
  HTMX status action refreshes successful completion.
- A typed `WebUIToolkit.MVVM.Flow` Details → Review workflow with validation,
  retained Back navigation, Finish, and Cancel.
- A bounded in-app diagnostic feed containing safe application outcomes.
- Deterministic disposal of the native transport, opened view, endpoint runtime,
  generated web root, workflow, import task, and repository.

There is no application-authored DOM renderer or `webui.call` integration.

## Run

From the repository root:

```bash
direnv allow
dotnet run --project samples/AdvancedTodo
```

Use the coordinated developer loop while editing `.cwhtml`, C#, JavaScript,
or CSS:

```bash
dotnet webuitoolkit dev samples/AdvancedTodo/AdvancedTodo.csproj
```

Tasks persist below the platform local application-data directory:

```text
WebUIToolkit/AdvancedTodo/todos.json
```

Choose an explicit file with:

```bash
ADVANCED_TODO_DATA=/tmp/advanced-todo.json \
  dotnet run --project samples/AdvancedTodo
```

## Self-test

```bash
dotnet run --project samples/AdvancedTodo -- --self-test
```

The self-test creates isolated data and exercises the real compiled endpoint:
invalid and valid quick add, search/filter, toggle, Flow validation,
Details/Review/Back/Finish, cancellable import, persistence, opaque route
emission, local asset validation, the sole native binding contract, and removal
of the private generated web root.

## Architecture

```text
Compiled .cwhtml document and fragments
                 │ opaque hx-post routes
                 ▼
       CsWebUiHtmxTransport
          (one native binding)
                 │
                 ▼
       HtmxEndpointRuntime
                 │ closed fields + commands
                 ▼
 CommunityToolkit MVVM adapter
                 │
                 ▼
          TodoViewModel
        ┌────────┴─────────┐
        ▼                  ▼
   TodoService      TodoCreationFlow
        │                  │
        ▼                  ▼
 JsonTodoRepository  MVVM.Flow workflow
```

Start with `Program.cs`, then read `UI/AdvancedTodoApplicationRoot.cs`,
`UI/AdvancedTodoRenderModel.cs`, and the two files under `Views/`. The domain,
service, repository, and Flow files remain independent of CsWebUi and HTMX.
