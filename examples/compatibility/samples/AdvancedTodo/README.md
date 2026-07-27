# Advanced ToDo

**Difficulty:** Advanced
**Experience:** Native desktop UI generated from compiled `.cwhtml`
**Transport:** Generated registration over one native binding with opaque routes

This sample grows a task list into a small desktop application while keeping
state, validation, commands, workflow navigation, persistence, and HTML
rendering in C#. npm pins HTMX, Bootstrap 5.3, and Font Awesome; Vite produces
the local development and minified production asset graph served by CsWebUi.

## What it demonstrates

- Compiled `AdvancedTodoDocument.cwhtml` and `AdvancedTodoApp.cwhtml` views.
- Compiler-generated action, field, command, focus, event, and render-plan
  registration for all 13 application actions, with typed
  `HtmxFields`/`HtmxCommands` handles instead of consumer-authored protocol IDs.
- One generated CommunityToolkit adapter factory owning all six writable
  properties, thirteen sync/async commands, validation projection, and
  source-generated JSON metadata.
- Generated typed action handles and an immutable thirteen-route projection;
  no application-authored route table remains.
- Generated action URLs and authoritative revision markup; the presentation
  model contains no endpoint routes or transport revision.
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
- A high-level `CsWebUiHtmxApplication` owning transport, view, endpoint,
  session, and adapter lifetime.
- Endpoint and native-transport policies assembled through the cwhtml extension
  surface of the shared `WebUiAppBuilder`, outside the application root.
- Deterministic disposal of the generated web root, workflow, import task, and
  repository around that application lifetime.

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

If the packaged tool is not installed, use the repository-local command shown
in [Getting started](../../docs/getting-started/README.md).

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

The real-browser path uses isolated data and a private Chromium profile, then
submits Quick Add through the production native bridge:

```bash
dotnet run --project samples/AdvancedTodo -- --browser-smoke-test
```

## Architecture

```text
Compiled .cwhtml declarations
                 │ generated ConfigureHtmx
                 ▼
       CsWebUiHtmxApplication
        (routes + owned lifetime)
                 │ one native binding
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
The application-root file is now only a domain composition factory; the shared
aggregate owner supplies endpoint, generated assets, native-window/root-session
lifetime, and deterministic teardown.
