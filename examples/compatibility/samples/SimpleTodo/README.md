# Simple Todo

**Difficulty:** Beginner  
**Style:** Compiled HTML and HTMX in a native CsWebUi window

Simple Todo is a small desktop application that demonstrates the intended
compiled WebUIToolkit stack without ASP.NET or a loopback API. Its browser
dependencies and CSS/JavaScript production graph use the same npm/Vite
pipeline as the framework samples.

It demonstrates:

- a CommunityToolkit.Mvvm ViewModel with relay commands and closed form validation;
- `ObservableRangeCollection<T>` projected through the CommunityToolkit
  collection binding;
- `.cwhtml` authoring for both the initial document and HTMX response
  fragment;
- compiler-generated action, field, command, fragment, focus, and event
  registration, with `nameof(...)` declarations shared through typed
  `HtmxFields`/`HtmxCommands` handles and closed per-view routes assigned by
  `HtmxEndpointRuntime`;
- one generated CommunityToolkit adapter factory owning property, collection,
  command, and source-generated JSON registration;
- generated typed action handles and an immutable route projection instead of
  an application-authored route table;
- one bounded CsWebUi JSON binding shared by all HTMX requests;
- npm-pinned HTMX 2.0.10, its CSP companion, Bootstrap 5.3.8, and Font Awesome,
  bundled by Vite and served entirely from local assets; and
- deterministic reverse-order teardown of transport, view, runtime, session,
  and the generated web root.

## Run it

From the repository root:

```bash
dotnet run --project samples/SimpleTodo
```

For the coordinated asset, cwhtml, C#, and CsWebUi development loop:

```bash
dotnet webuitoolkit dev samples/SimpleTodo/SimpleTodo.csproj
```

If the packaged tool is not installed, use the repository-local command shown
in [Getting started](../../docs/getting-started/README.md).

On NixOS, enter the repository's direnv shell first so the pinned native WebUI
library and Chromium dependencies are available.

The headless path compiles the views, generates the initial document, drives
invalid/add/toggle/remove requests through the real HTMX endpoint, checks the
collection-backed model, and verifies that generated browser assets contain no
legacy per-operation callback contract:

```bash
dotnet run --project samples/SimpleTodo -- --smoke-test
```

The native browser path starts the real CsWebUi server and a persistent
headless Chromium process. It loads the shipped browser bridge and HTMX,
submits the visible composer form, executes the C# command, and verifies that
the compiled fragment replaced the browser DOM with the new task and a newer
revision:

```bash
WEBUI_BROWSER_PATH=/path/to/pinned/chromium \
  dotnet run --project samples/SimpleTodo -- --browser-smoke-test
```

The repository's Nix/direnv development shell sets `WEBUI_BROWSER_PATH` to its
pinned Chromium automatically. The browser test owns a unique temporary
Chromium profile and deletes it after shutting down CsWebUi, the native
transport, and the browser process.

## Architecture

```text
TodoApp.cwhtml + TodoDocument.cwhtml
  └─ generated IHtmlRenderable views
       └─ generated ConfigureHtmx registration
            └─ CsWebUiHtmxApplication (opaque action routes + lifetime)
                 └─ one CsWebUi native transport binding
                      └─ CommunityToolkit adapter
                           ├─ validated properties and relay commands
                           └─ observable collection projection
```

The browser submits ordinary HTMX forms. It does not choose CLR members,
receive a full-state JSON model, or handle application rendering. The compiled
fragment reads an immutable `TodoRenderModel`; the runtime HTML writer encodes
all task titles and validation text.

## Initial document delivery

Action routes are random and exist only after the HTMX view opens, so a checked
in static page cannot safely contain them. `TodoApplicationRoot.PrepareAsync`
opens the view, renders the compiled document with those routes, and combines
it with the pinned static assets in a private per-run directory. The normal
manifest validator verifies that finished root before CsWebUi starts.

This provides a local static bootstrap document without introducing an HTTP
framework, a catch-all file handler, or another browser callback. The temporary
root is deleted during application teardown.

## Guided tour

1. Start with `TodoViewModel.cs` and `TodoItem.cs` for ordinary MVVM state.
2. Read `Views/TodoApp.cwhtml`; `data-hx-*` is rendered HTMX configuration,
   while build-time-only `data-wut-*` declarations generate closed registration
   metadata and are stripped from the HTML.
3. Read `TodoApplicationRoot.cs` for domain validation, the one-call generated
   adapter activation, high-level native application builder, generated
   document, and smoke test.
4. Finish in `Program.cs`, where `WebUiModeRunner` receives the prepared
   manifest root and owns window/session startup and shutdown.

The tasks intentionally remain in memory. Persistence, editing, filtering,
navigation, and background workflows belong in the Advanced Todo sample.
