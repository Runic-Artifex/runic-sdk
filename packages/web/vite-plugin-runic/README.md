# `@runic-artifex/vite-plugin-runic`

Vite 8 integration for Runic Desktop bootstrap, development diagnostics, and
optional Vite DevTools. The .NET Views build owns generated TypeScript clients;
this plugin does not generate application contracts.

```ts
import { defineConfig } from "vite";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({ plugins: [runic({ desktop: true })] });
```

`desktop: true` inserts the Desktop bootstrap script and sets a relocatable
production base. `desktop` stays opt-in: Views pages and CS-WebUI do not need
it. For a CS-WebUI frontend, use `runic({ devtools: false })` or
omit the plugin entirely. A Runic Views frontend needs neither: its page loads
`runic-views.js`, the Views client that every host provides, so `runic()` with
`base: "./"` builds one page for Runic Desktop and CS-WebUI. The Views build
passes the host it builds for as `RUNIC_APPLICATION_HOST` for a configuration
that still differs per host. The optional
`@vitejs/devtools` peer enables the Runic panel of the Vite DevTools dock (in
this README, "the dock"; not the browser's own developer tools, which open with
F12 or Inspect): register `DevTools()` in your Vite configuration, as the
`runic-app` template's Vite starters do. The plugin runs on Node 24.18 or later
24.x, or Node 26.

```ts
import { defineConfig } from "vite";
import { DevTools } from "@vitejs/devtools";
import { runic } from "@runic-artifex/vite-plugin-runic";

export default defineConfig({ plugins: [runic(), DevTools()] });
```

`devtools` takes `true`, `false` or `"auto"`. `"auto"` is the default and the
same as omitting the option: the development client is injected while serving,
and the Runic dock appears when `DevTools()` is registered. `true` also requires
`DevTools()`: serving without it fails with
[`RUNICP007`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#runicp007).
`false` turns off both the dock and the development client.
`maxTimelineEntries` sets how many timeline entries the dock keeps: 200 by
default, rounded down and limited to 1 to 500.

While serving, the plugin adds `virtual:runic/client`, which loads the
development client (`@runic-artifex/vite-plugin-runic/client`), to `index.html`.
The client has no exports. It shows the page's connection to its .NET host, as
`@runic-artifex/views` reports it (`connecting`, `connected`, `reconnecting`,
`lost`, and `closed` once the application closed the window), on the dock's **Connection** card from page load, before a View
connects (with `@runic-artifex/views` 0.11 or later; earlier releases start it
when the first View connects), and forwards
`@runic-artifex/views` failures to the timeline: the failed route, the
error kind and, for errors, a failure with the exception type, message and
stack. .NET includes that detail only in development (see the
`IncludeFailureDetail` window option of Runic.Application.Views). The **Last failure** card of the
dock shows the most recent one. A failure keeps file paths, so that a stack is
useful, but is bounded and has credential values redacted. Failures stay in
the dock: `/__runic/state` and the copied diagnostic state leave them out, and
the other timeline fields stay path-free. An application without a Vite-served `index.html` imports
the client itself, with `import "@runic-artifex/vite-plugin-runic/client"` (or
`virtual:runic/client`): `runic()` resolves that import to the same module it
injects while serving, and to an empty module in the build and on the server,
so nothing reaches production output. `devtools: false` turns the client off:
no injection, and an import of it is empty.
The plugin adds `@runic-artifex/vite-plugin-runic/client` to
`optimizeDeps.include`, because the dependency scanner cannot see the injected
client: with a cold `node_modules/.vite` cache, the first page would otherwise
discover it, and Vite would reload the page and close the host's connection.

The development client also warns about a text field that loses typed
characters: when script sets the focused `<input>` or `<textarea>` to a value
it had before the user's last edit while a Bridge request is in flight, which
happens when a field is bound with a command per key. It reports a
`stale-input` Views diagnostic, shown on the timeline and passed to
`onBridgeDiagnostic` listeners, and logs a console warning, once per field.
The warning names the field by its `id` or `name`, never its value. Bind text
fields with the text-field binding of your framework package
(`injectTextField` in `@runic-artifex/angular`, `useTextField` in
`@runic-artifex/react`, `vue` or `svelte`) or a setter. Turn the warning off with
`runic({ staleInputWarning: false })`, or from the page by setting
`globalThis[Symbol.for("runic.vite.staleInputWarning")] = false`.

### State inspector

With `@runic-artifex/views` 0.15 or later the development client also reports
each open View's state, as the page shows it, and the dock shows it on the
**Views** card: one card per View with its route, revision and presentation
epoch, the most recently changed first, and the state as a tree. The client
sends a View's latest state at most every 250 ms, and the server keeps the 32
Views that changed last, per page (several windows can share one development
server), and forgets a View when it closes and a page's Views when the page
goes away. The state is redacted and bounded like timeline details: a member
whose name suggests a credential or a path (`password`, `accessToken`,
`filePath`, …) and a string that looks like a credential or a path read
`[redacted]`, strings are shortened, long lists end with `… N more`, and deep
or large states are shown shallower. It is part of `/__runic/state` (`views`)
and of the copied diagnostic state, which hold no failures.

A command that the Bridge answered `superseded` (a
[command scoped to its page](../views/README.md#scoped-commands) called from an earlier page)
resolves like one that ran, so the page shows nothing. The client reports it
as a timeline entry (`kind: "command"`, label `<member> superseded`,
`detail.code: "superseded"` with the route, member, the epoch it was issued for
and the reply's revision), and the dock lists the latest on the **Superseded
commands** card. With the Angular CLI, read both from `/__runic/state`.

The inspector runs only where the development client does: never in a build.
`@runic-artifex/views` reports to it only once the client asked for it
(`runic.views.inspector` in the
[shared globals](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/README.md#shared-globals)).

### Bridge calls in the Performance panel

With `@runic-artifex/views` 0.20 or later the development client also listens
to the Bridge-call channel (`runic.views.calls`). It draws each settled call
on a **Bridge calls** track in a **Runic** track group of a Chromium engine's
Performance recording (turn on "Show custom tracks"), and its listening turns
on the async stack segments that lead from a reply back to the click that made
the call. See
[Bridge calls in the browser's developer tools](../views/README.md#bridge-calls-in-the-browsers-developer-tools).
The dock lists the same calls with their C# handlers (next section).
Only the
[development build](../views/README.md#development-build) of
`@runic-artifex/views` reports calls, which `vite dev` resolves; `vite build`
bundles the production build, which has no reporting code. A .NET host in Development names each call's C#
handler and its duration, which the client lists on the call's entry and draws
on a **C# handlers** track below it.

### OTLP export of the page's spans

While serving, the plugin exports a span per Bridge call to an OTLP/HTTP
endpoint, so the page's call, the .NET `runic.bridge.*` span and the
ViewModel's own spans form one trace. It is on when `OTEL_EXPORTER_OTLP_ENDPOINT`
or `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` is set in the development server's
environment (and off with `OTEL_SDK_DISABLED=true` or `OTEL_TRACES_EXPORTER=none`):
the development client then gives each call a W3C trace context, which its
requests carry to .NET, and sends the settled call to the development server,
which posts it as OTLP/JSON. The page loads no OpenTelemetry code and posts
nothing cross-origin; a production build contains none of it.

```ts
runic({ otlp: false });                                         // never
runic({ otlp: { url: "http://localhost:4318/v1/traces", serviceName: "notes-page" } });
```

`url` is the full traces URL; `headers` defaults to `OTEL_EXPORTER_OTLP_HEADERS`,
and `serviceName` to `OTEL_SERVICE_NAME`, or else the root's package name, with
`-page` appended. [Tracing Bridge calls](../../../docs/tracing.md) sets it up
with the Aspire dashboard.

### Bridge call timeline in the dock

The client also sends each settled call to the development server, and the
dock's **Bridge calls** card shows the latest 20, the newest first:

```text
command EditorViewModel.save → SaveAsync() 23 ms
C# 21 ms · revision 4 · EditorViewModel.cs:42 · 2026-11-15T10:12:03.120Z
[Open SaveAsync()]
```

- **The call.** Its kind (`command`, `set`, `write`, `can`, `region`,
  `operation`, `query`), the C# ViewModel and the member, and how long the page
  waited from the call to its settled reply. A call that did not end `ok` adds
  its outcome and reason, such as `· cancelled (superseded)` or
  `· error (domain-failed)`.
- **The C# handler.** The plugin looks the call up in the
  [source map](#source-map-and-open-in-editor): `SaveAsync()` for a command's
  `[RelayCommand]` method, `Title.set` for a setter. The button opens it in the
  editor (see [the dock's open action](#the-docks-open-action)). Without a map (no build yet, or
  `sourceMap: false`) the row has no handler.
- **The handler's own duration.** When the reply carries .NET's timing of the
  handler (a .NET host in Development that measures it), the second line
  starts with `C# <n> ms`, and the call's `handlerDuration` holds it; without it
  the row shows the page's duration only.
- **Failures.** A failed call shows .NET's exception (the development detail of
  the error reply) and a button for each stack frame in a file of the source
  map, which opens that line, such as `EditorViewModel.SaveAsync() ·
  EditorViewModel.cs:12`. Other frames stay text on the **Last failure** card,
  which links the same frames.
- **Bounds and privacy.** The server keeps as many calls as timeline entries
  (`maxTimelineEntries`, 200 by default). What the page sends is checked like a
  timeline entry: names and routes of a fixed shape, finite durations, and the
  failure under the failure rules (bounded, credential values redacted). The
  handler comes from the server's own map, and an entry names it by the map's
  member id and the file's name, never a path. `/__runic/state` has the calls
  (`calls`, typed `RunicBridgeCall`) without failures, like the timeline.

Under `dotnet runic dev` the window's document is the .NET host's page, which
loads Vite's modules from Vite's origin, so the dock's own loader (a script
that loads `/__devtools/embedded.js` relative to the document) finds nothing.
The development client is one of Vite's modules, so it loads the dock from
Vite's origin whenever the document's scripts did not; with `html: false`
(SvelteKit) it always does, and `runic()` removes the dock's HTML hook, which
SvelteKit would warn about. The first time a page opens the dock, the dock asks
for the one-time code that Vite DevTools prints in the development server's
output (shown by `dotnet runic dev`); keep that check on, as it is what keeps
other pages of this machine out of the dock.

## Source map and open in editor

The Bridge generator writes a development source map beside its generated C#
in `obj` (`obj/<Configuration>/<TargetFramework>/runic-bridge/sources.json`).
It lists every ViewModel with a generated Bridge and every member its client
and the Bridge frames name, with the C# that runs for it and where it is.
`vite dev` serves it at `/__runic/sources`, and `/__runic/open` opens one of
its locations in your editor. The map never enters the generated TypeScript,
a `vite build` or a package: production output is the same with and without
it.

```jsonc
// GET /__runic/sources
{
  "format": "runic-bridge-sources/1",
  "viewModels": [{
    "id": "Demo.CounterViewModel", "name": "Counter", "route": "counter",
    "contract": "Demo.CounterViewModel:EE06…", "pageKinds": ["counter"],
    "symbol": "Demo.CounterViewModel",
    "location": { "file": "/src/Demo/CounterViewModel.cs", "line": 6, "column": 29 }
  }],
  "members": [{
    "id": "Demo.CounterViewModel/command/increment", "viewModel": "Demo.CounterViewModel",
    "member": "increment", "kind": "command",
    "symbol": "Demo.CounterViewModel.Increment()", "property": "Demo.CounterViewModel.IncrementCommand",
    "location": { "file": "/src/Demo/CounterViewModel.cs", "line": 18, "column": 18 }
  }]
}
```

- **Lookup.** A ViewModel's `id` is the part of a generated client's contract
  before the colon, and `route` is the route `connect<Name>()` connects. A
  member's `id` is `{viewModel}/{kind}/{member}`, where `member` is the name the
  client and the Bridge frames use (`count`, `increment`), and `kind` is one of
  `state` (read through the getter), `setter` (`set…` and `write…`, through the
  setter), `command` (located at its handler: the `[RelayCommand]` method, a
  method group the command is created from, such as
  `ReactiveCommand.CreateFromTask(SaveAsync)`, or else the line that creates
  it), `commandState` (`canSave` and other state a command adds), `interaction`
  and `request`. The TypeScript types are exported as `RunicSourceMap`,
  `RunicSourceMember` and `RunicSourceLocation`.
- **Locations.** A location is `{ file, line, column }`: the absolute path of a
  local source file and a 1-based line and column, as in a compiler
  diagnostic. They come from the application assembly's portable PDB (the
  default), then from the source of members a source generator writes, such as
  a CommunityToolkit `[ObservableProperty]`. A method is located at its name and
  an accessor at its `get` or `set`, not at the first statement the PDB
  records, so a Debug and a Release build give the same map. Without a PDB or source file an
  entry has no `location`; the build does not fail. A map is as current as the
  last build: `dotnet watch`, and so `dotnet runic dev`, rebuilds it after a C#
  edit, and the plugin reads it again when the file changes.
- **Where the plugin finds it.** `dotnet runic dev` passes the map's path in
  `RUNIC_APPLICATION_DEV_SOURCE_MAP`. Otherwise, or when that file does not
  exist, the plugin takes the newest `runic-bridge/sources.json` under `obj` of
  the project `dotnet runic dev` runs, else of the Vite root or its parent (the
  project of a `Frontend/` directory). `runic({ sourceMap:
  "../obj/Debug/net10.0/runic-bridge/sources.json" })` names the file, or a
  directory to search, relative to the Vite root. `sourceMap: false` reads no
  map: `/__runic/sources` is gone, and `/__runic/open` opens only frontend
  files. Without a map `/__runic/sources` answers 404.
- **Opening.** `POST /__runic/open` with `{ "id": "<entry id>" }`, or with
  `{ "file", "line", "column" }` in a file of an entry (`column` defaults to 1;
  a .NET stack frame's line) or in a frontend file Vite serves to the page
  (`server.fs.allow`, without `server.fs.deny`; an absolute, normalized path,
  such as a component that [Runic Inspect](#runic-inspect) links to), opens that
  location with [launch-editor](https://github.com/vitejs/launch-editor), the
  package behind Vite's own editor launcher (`LAUNCH_EDITOR`, else a running
  editor such as VS Code or a JetBrains IDE like Rider, else
  `VISUAL`/`EDITOR`), and answers 200. It is meant for the page and
  other development tools:

  ```ts
  await fetch("/__runic/open", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id: "Demo.CounterViewModel/command/increment" }),
  });
  ```

- **What it refuses.** Vite's `/__open-in-editor` opens any path; the Runic
  action opens only a file the map lists or a frontend file Vite serves (with
  `server.fs.strict: false`, any file, as Vite then serves any), at a positive
  line and column, and only while the file exists, so a page cannot open
  anything else. It answers a POST of JSON (415 otherwise)
  from a page of the development server's own origin (`Origin` and
  `Sec-Fetch-Site`; a cross-origin page cannot send JSON without a CORS
  preflight, which it never grants) and from this machine (a loopback client
  address), else 403; an id or file the map does not list, an entry
  without a location, and a file that no longer exists answer 404. The map
  itself answers only same-origin requests from this machine too, unless
  other machines are allowed ([Runic Inspect from another machine](#runic-inspect-from-another-machine));
  the open action stays loopback-only, as it launches the editor on this
  machine.
- **Only development servers.** Both endpoints exist only on the development
  server (`vite dev`, and `ng serve` through `runicDevSessionProxy()`), not in
  `vite build` or `vite preview`.

### The dock's open action

The dock's open buttons call the `runic:open-in-editor` Vite DevTools action
with `{ id }` or a stack frame's `{ file, line }`. It decides what may open
exactly as `/__runic/open` does (`resolveOpenLocation`: a location in a file
the current map lists, while the file exists) and refuses everything else,
launching nothing. Then it opens the location with
[launch-editor](https://github.com/vitejs/launch-editor) 2.14.2 itself, the
package behind Vite's `/__open-in-editor`, so the editor is chosen the same
way (`LAUNCH_EDITOR`, a running editor, `VISUAL`/`EDITOR`). The action makes
no HTTP request: only clients that Vite DevTools trusts (its one-time code)
can call an action, which takes the place of the endpoint's origin checks.
With `sourceMap: false` it opens nothing. It does not open frontend files:
the dock links only what the map lists.

## Runic Inspect

**Runic Inspect** is a select mode of the development client: press
**Alt+Shift+X** in the page (or call `toggle()`, below), hover an element to
outline it, and click it to see how it is connected. The selecting click
reaches no handler of the page: while the mode is on, a transparent layer of
the overlay takes every pointer event, and the window's capture phase stops
them. **Esc** or Alt+Shift+X turns it off. The overview shows:

- **Component**: the element's framework component and the components around
  it, from each framework's own development metadata, with an editor link:

  | Framework | What it shows |
  | --- | --- |
  | React 19 | The component and its owners from the element's fiber, and the file:line of the element's JSX: the fiber's `_debugStack` frame resolved through the served module's source map. |
  | Svelte 5 | The component and the file:line:column of the element (`__svelte_meta`). |
  | Vue 3 | The component (`__vueParentComponent`) and its file (`__file`, from the SFC compiler), without a line. |
  | Angular 17.1+ | The component class (`ng.getComponent`/`ng.getOwningComponent`) and, with the compiler's debug info, its file:line (`ɵcmp.debugInfo`). |

- **Commands**: the commands the element calls, each with its C# handler (such
  as the `[RelayCommand]` method) and file:line from the
  [source map](#source-map-and-open-in-editor), how often the element called
  it, and its availability state (`canSave`) when the ViewModel has one.
- **ViewModel members**: the members the element sets (`set…`/`write…`, with
  the C# setter), and state members whose value is the element's text or a
  form control's value, each with its current value and C# member. A value
  match is marked as such: it is a match, not proof.
- **ViewModels** the element is connected to, with the C# class.
- **Recent Bridge calls** the element made: kind, member, duration, outcome and
  the reply's revision.
- **Texts from the ViewModel**: text references (`TextReference` members) the
  element shows, with their catalog, key, locale and arguments.
- **Messages**: the messages of generated `m.*()` calls the element shows, in
  its text or in an attribute (`title`, `aria-label`, `placeholder`, `alt`, an
  input's value), each with its catalog, key, locale, its text as authored and
  the `.rmf2`/`.mf2` file:line that defines it, and **Edit**, which edits its
  text in every locale in place. See
  [Messages of `m.*()` calls](#messages-of-m-calls) and
  [Edit a translation on the page](#edit-a-translation-on-the-page).

C# locations and frontend files open in your editor through
[`/__runic/open`](#source-map-and-open-in-editor): a C# location by its source
map id, a frontend file by its path, which the endpoint opens only when Vite
serves that file to the page. Runic Inspect does not use Vite's
`/__open-in-editor`, which opens any path for any page.

**Under `dotnet runic dev`** the window's document is the .NET host's page,
which loads Vite's modules from Vite's origin, so a relative URL such as
`/__runic/sources` names the host. Runic Inspect therefore asks the Vite server
over the development client's HMR connection instead of HTTP: a
`runic:request` event (`{ id, kind }` with `kind` `sources`, `handlers` with a
`file`, `message` with a `target` `{ catalog, key }`, or `open` with a
`target`), answered by `runic:response` with the same id. The server answers
as `/__runic/sources`, `/__runic/handlers`, the Translations message API and
`/__runic/open` do, with the same decision of what opens and the same access
rules, judged per request from the connection's address (Vite reports it):
`sources`, `handlers` and `message` for this machine and the hosts and
networks `allow` and `RUNIC_DEV_ALLOW` admit, `open` for this machine only
(403 otherwise). Vite admits that connection only with the token in its own client
module, which only the page's origins Vite's CORS allows can read (by default
`localhost`, `127.0.0.1` and `[::1]`, the .NET host's page among them), so it
needs no CORS rule of its own. Without an HMR connection (the Angular CLI, or
a page that loads the client without Vite's HMR) Inspect uses the HTTP
endpoints, which also serve tools such as `curl`.

**Before the first click.** The development server reads the element's
component file and finds which of its event handlers reach which client call:
directly (`client.save()`, `client.command("save")`), through Runic's helpers
(`useCommand`, `useOperation`, `injectCommand`, `injectOperation`), or through
other declarations of the same file (a function, a variable, a destructured
`run`, a class method, `this.x`). The overview lists the calls that are
commands of a ViewModel in the [source map](#source-map-and-open-in-editor),
with the C# handler, "from the source, not called yet", and a link to the
line that calls the client:

```tsx
const increment = useCommand(() => client?.increment()); // ← found here
return <button onClick={() => void increment.run()}>Increment</button>;
```

| Framework | Handlers read | How the element is found |
| --- | --- | --- |
| React (JSX/TSX) | `onClick={…}` and every other `on…` prop, of elements and components | The element's JSX line (`_debugStack`); for a `<button onClick={props.onClick}>`, the `<Button onClick={…}>` around it |
| Vue 3 (SFC) | `@click`/`v-on:click` in the template, with `<script setup>` and the Options API (`methods`, `setup()`) | The element's compiled listeners (`$setup.increment()`), else its component's only binding of the event |
| Svelte 5 | `onclick={…}` and `on:click={…}`, of elements and components | The element's file, line and column (`__svelte_meta`), then the component tags around it |
| Angular 17.1+ | `(click)="…"` in `template` and `templateUrl` files, with the class's fields and methods | The component file (`ɵcmp.debugInfo`) and `ng.getListeners`: the template's binding of the event and tag, by its order when there are several |

Svelte and Vue files are read with the application's own `svelte/compiler`
and `vue/compiler-sfc`, scripts with Vite's parser. Served modules are not
changed: the analysis answers `GET /__runic/handlers?file=…` for pages of this
machine, for frontend source files below the Vite root (not `node_modules`).

What it does not see, so the command is learned on first click:

- calls in other files: a store, a service, a custom hook or a function
  imported from another module. The overview names the import
  (`saveAll from ./store`);
- handlers that come from outside the component: a prop passed down several
  components, a context, an event bus;
- elements that a template repeats with different handlers where the
  framework does not say which one it is: an Angular `@for` with listeners that
  differ per item, a Vue listener whose compiled code is the method itself
  (`@click="save"`) when the component has several such bindings;
- render functions (`h("button", { onClick })`) and Vue JSX.

**On first click.** The development client also listens to the Bridge-call
channel (`runic.views.calls`), whose `start` report arrives in the stack of
the code that made the call. The DOM event being dispatched names the element
(its `target`, so React's delegation to the root does not matter), and the
nearest button, link or form field is recorded as calling that member. A
command found this way is marked with its call count; it covers what the
source analysis does not see. Use the element once with the mode off, then
inspect it. A call that does not run during the event, such as a write a
field controller sends after its previous one finished, is not attributed.

**For agents and tests**, the development client puts an API on the page at
`globalThis[Symbol.for("runic.vite.inspect")]`:

```ts
const inspect = (globalThis as any)[Symbol.for("runic.vite.inspect")];
inspect.toggle(true);        // select mode on, which marks m.*() text; returns whether it is on
const overview = await inspect.describe(document.querySelector("#save"));
// { element, component, viewModels, members, commands, calls, texts, messages, notes }
await inspect.select(document.querySelector("#save")); // shows the overview card
await inspect.open(overview.commands[0].location); // opens it in the editor
```

`describe(element)` resolves to JSON-safe data (the type is exported as
`RunicInspectDescription`); values are shortened, and `notes` says what the
overview cannot tell, such as a missing source map. `messages` is filled only
while select mode is on: call `toggle(true)` and let the page render once
before `describe`. With `chrome-devtools-mcp` or Playwright, evaluate it in
the page.

Runic Inspect is plain DOM in a shadow root, so it works in every engine
`dotnet runic dev` runs: an installed browser, WebView2, WKWebView and
WebKitGTK. It is part of the development client, which a build replaces with
an empty module: `vite build` output is byte-identical with and without it.
`runic({ inspect: false })` leaves it out of the client (with the translation
editor) and turns off `/__runic/handlers`. Under the Angular CLI, `runicDevSessionProxy()` answers
`/__runic/sources`, `/__runic/open` and `/__runic/handlers` too
([SvelteKit and Angular](#sveltekit-and-angular)), so the overview has the C#
handlers and the commands before the first click there as well.

### Messages of `m.*()` calls

A generated message function returns a plain string, so nothing in the page
says which message a text came from. Runic Inspect asks Runic Translations to
mark it, **only while select mode is on**:

- Turning select mode on calls `markers.set(true)` on the Translations
  text-change channel (`globalThis[Symbol.for("runic.translations.text")]`).
  The page renders its messages again, as on a language switch, and every
  `m.*()` call puts an invisible marker in front of its text that names the
  message. Turning select mode off renders the plain text again. Outside
  select mode the page's text, `textContent`, copied text and tests see no
  markers.
- The markers are invisible format characters that take no width, so the
  layout does not change. While select mode is on they are also in attribute
  values and anything else the text went into; the overview strips them from
  the element's text.
- For each message it finds, the overview asks the Translations dev server
  for its source (`GET /__runic-translations/message`, or over Vite's
  connection as above, where the server asks it), and its link opens it
  through `/__runic/open` with `{ "message": { catalog, key, locale } }`. The
  open action never takes a path for a message: it asks the same API and
  opens only the `.rmf2`/`.mf2` source it names.

It needs `@runic-artifex/vite-plugin-runic-translations` 0.12 or later under
`vite dev` (the markers, and in project mode with in-page editing on, the
source locations), and text helpers that render again on the text-change
channel: `useTextLocale` in React and Vue, Svelte's `useTextLocale` or
`useMessageLocale`, Angular's `textLocaleSignal`, as for a language switch. A
component that does not render again, or a value computed once (such as
`document.title`), keeps its text unmarked. Text formatted from a ViewModel's
text reference is listed under **Texts from the ViewModel** instead. With an
earlier plugin, the overview has no messages and its notes say why.

### Edit a translation on the page

Select a text in Runic Inspect and press **Edit** next to its message. The
panel below the overview reads the message from Runic Translations' in-page
editing API (`GET /__runic-translations/message`) and shows:

- a **locale** list, base locale first, starting with the locale the page
  shows; a locale without text of its own is marked `(missing)`, and saving it
  adds the message beside the base locale's;
- where the locale's text is defined (`translations/de.rmf2:4`), and for a
  library's text, the application override a save writes instead;
- its **structure**: the inputs (`$count :integer`), what it selects on
  (`$count (plural): one · *`), the locale's plural categories, with the ones
  that have no variant, and the markup it uses (`{#strong}`);
- the **MF2 source** of that locale, exactly as authored: declarations,
  `.match` and its variants, placeholders and markup. Plural, selector, markup
  and document messages are edited as their whole source.

**Save** (or Ctrl+Enter, ⌘+Enter) posts the text with the hash of the file it
was read from. Runic Translations compiles it with the rest of the catalog and
writes it with the Translations Editor's source-preserving writer: only that
entry changes, and the file keeps its comments, formatting and order. The dev
server then updates the page **in place**, as for a save in an editor: no
reload, the page keeps its state, and the card keeps its selection and
describes the element again with the new text. Each locale keeps its own
unsaved text while you switch between them. There is no separate preview: the
page itself shows the saved text.

- **The text does not compile** (422): the panel lists the RTR diagnostics
  with their lines, keeps your text, and nothing is written.
- **The file changed since it was read** (409), for example saved in an
  editor: the panel shows the text in the file now. **Reapply my text** keeps
  your text and saves it over the current file; **Use the file's text** drops
  your edit. When the page's text changes and you have not edited the locale,
  the panel reads the message again on its own.
- **Not allowed** (403): the panel says how to allow this machine (below).

The panel is plain DOM in the overlay's shadow root, so it works in every
engine `dotnet runic dev` runs. Tab moves through the locale list, the text,
Save and Revert; **Esc** in the panel closes it and returns to its Edit button.
Typing in it does not reach the page's own key handlers. Selecting another
element closes the panel unless it has unsaved text.

It needs `@runic-artifex/vite-plugin-runic-translations` 0.12 or later in
project mode (the `project` option), with `inPageEditing` on, which is the
default under `vite dev`. Without it, the overview lists messages without
**Edit**.

### Runic Inspect from another machine

The development server can serve a page to another machine: a laptop on the
same [Tailscale](https://tailscale.com/kb/1015/100.x-addresses) network, or
the browser of a laptop that shows a page of a dev server an agent runs on a
local server. Start Vite on a reachable address (`server.host`, `vite --host`)
and name the machines that may use Runic Inspect's data there:

```sh
RUNIC_DEV_ALLOW="100.64.0.0/10, fd7a:115c:a1e0::/48" vite dev --host
```

```ts
runic({ allow: ["100.64.0.0/10", "192.168.1.20", "laptop.tailnet.ts.net"] })
```

Entries are IPv4 or IPv6 addresses, CIDR networks, or host names (resolved
when a request arrives, and cached for 30 seconds); the environment variable
takes the same entries separated by commas or spaces and adds to the option.
An invalid entry stops the dev server with its name, and a non-empty list is
logged at start. `RUNIC_DEV_ALLOW` is one setting for both plugins: Runic
Translations' in-page editing API reads it too (its older
`RUNIC_TRANSLATIONS_EDIT_ALLOW` and `inPageEditing: { allow }` still work), so
the same variable lets that machine read the source map and handler analysis
and edit translations.

| Endpoint | Loopback | An allowed machine | Any other machine |
| --- | --- | --- | --- |
| `/__runic/sources`, `/__runic/handlers` | yes | yes | 403 |
| `/__runic-translations/message` (Runic Translations) | yes | yes | 403 |
| `/__runic/open` | yes | 403 | 403 |

`/__runic/open` launches an editor **on the development machine**, so it stays
loopback-only. A page opened from another machine (its address is not
`localhost`) shows file:line locations without links, and its notes say why.
When a machine is refused, the overview's notes name what was refused and the
setting that allows it.

**Security.**

- **Loopback by default.** Without a list, every endpoint answers only the
  machine the dev server runs on (`127.0.0.0/8`, `::1`), judged by the TCP
  connection's address; `X-Forwarded-For` and similar headers are ignored. A
  reverse proxy on the same machine makes every client look like loopback.
- **Same origin.** A request must come from a page of the dev server itself
  (`Sec-Fetch-Site`, `Origin`), so another site, or another dev server on a
  different port, cannot read the data or write a translation. Vite's host check
  (`server.allowedHosts`) runs first and refuses DNS-rebinding hosts; allow the
  host names your other machines use there (IP addresses are allowed by
  default).
- **Allow only machines you trust with your sources.** An allowed machine can
  read the source map and the analysis of frontend files, and change the
  catalog's translation sources.
- **Development only.** Nothing of it is in `vite build` or `vite preview`
  output: the build is byte-identical with and without Runic Inspect, the
  editor and the allow list.

## Component build target

Trusted component mode embeds a guest's Views as a custom element in a host
page's own document instead of a frame. `component` adds that build next to the
page:

```ts
// vite.config.ts
export default defineConfig({
  base: "./",
  plugins: [react(), runic({ desktop: true, component: { entry: "src/component.tsx" } })],
});
```

The entry's default export is the mount function of
[`@runic-artifex/views/component`](../views/README.md#components). With Angular,
the Analog plugin (`angular()`) compiles the component's code ahead of time as
it does the page's, and `runicComponentMount` of `@runic-artifex/angular/experimental`
is the mount function; it keeps Angular's component styles, which Angular adds
to the document at run time rather than to the build's CSS, in the shadow root
([Angular components](../angular/README.md#components-experimental)). `vite build`
then builds every environment, as `vite build --app` does, and after them the
`runic_component` environment into `_runic_component/` of the build output:

| File | Content |
| --- | --- |
| `component.js` | An ES module that defines the element when it loads and exports `tag`, the name it defined, and the element class as default. It bundles the framework runtime and its own copy of `@runic-artifex/views`: import maps are document-global, so a component cannot share the host page's. |
| `runic-component-meta.js` | The tag and the CSS, which the module imports. |
| `runic-component.json` | `{ version: 1, name, module, tag }`, which the guest reads to answer a component attach. |
| `assets/` | Chunks and assets above the inline limit. |

- **Tags.** The tag is `<name>-<hash>`: `component.name` (by default the root
  `package.json`'s `name`, made a valid prefix: `@acme/check-tool` becomes
  `acme-check-tool`) and the first 8 hex digits of a hash of the build. Two
  guests, and two builds of one guest, therefore never define the same tag. The
  same build loaded from a second origin, such as two running instances of one
  application, is defined as `<tag>-2`; read the module's `tag` export. Tags are
  prefixed in every engine: scoped custom element registries would let a
  component keep plain names, but WebKitGTK 2.54 does not implement them, and
  one naming rule for every engine is what the host page, tests and devtools see.
  A component that defines custom elements of its own should prefix them with
  the `tag` its mount function gets.
- **CSS.** The build's CSS, in one stylesheet, becomes constructable stylesheets
  that every element adopts in its shadow root; nothing is added to the
  document. Theme variables of the host page inherit into the shadow root.
  `:root` and `html` rules match nothing there; use `:host`. `@font-face` rules in
  a shadow root have no effect in Chromium: use the host page's fonts.
- **Addresses.** Chunks, assets and the CSS's `url()` references resolve against
  the module's own URL, so they load from the guest's origin, absolutely, in the
  host page's document. Runic Desktop serves the guest's `_runic_component/` with
  CORS for exactly the origin of the host page that embeds it, and refuses it to
  every other origin. This requires base `./`, which
  `component` sets when the configuration has none.
- **No document state.** The element touches no history, title, global listener,
  `<html lang dir>` or document stylesheet; its own `lang` and `dir` follow its
  instance's text locale.

The development server serves the page only: develop the component's Views on the
page, where they run in the page instance, and build the component with
`vite build`. The development client and the dock observe the page instance, not
a component's.

## SvelteKit and Angular

Angular built with Vite, through the Analog Angular plugin
(`plugins: [angular({ liveReload: true }), runic()]`), uses `runic()` as the other
frameworks do (`dotnet new runic-app --frontend angular --angular-build vite`);
see the [Angular package](../angular/README.md#building-with-the-angular-cli-or-vite).
What follows covers SvelteKit and the Angular CLI.

SvelteKit renders its pages itself and warns about every plugin with a
`transformIndexHtml` hook. Use `runic({ html: false })`: the plugin then has no
such hook, injects nothing into HTML and leaves host scripts alone, and keeps
everything else: the timeline, the dock, `/__runic/state` and the dev session.
The page loads the client from its client hooks:

```ts
// vite.config.ts
plugins: [sveltekit({ adapter: runicAdapter() }), runic({ html: false }), DevTools()],
```

```ts
// src/hooks.client.ts
import "@runic-artifex/vite-plugin-runic/client";
```

With `DevTools()` registered the client loads the dock as well, and `runic()`
removes the dock's own HTML hook, which SvelteKit would warn about and never
run.

`html: false` cannot be combined with `desktop`, whose bootstrap is a script
tag in the HTML: `runic({ html: false, desktop: true })` throws.

The Angular CLI's development server loads no Vite plugins, only a proxy
configuration. `runicDevSessionProxy()` returns Vite proxy entries that answer
locally and never proxy. Spread it into the file that the serve target's
`proxyConfig` in `angular.json` names:

```js
// proxy.conf.mjs
import { runicDevSessionProxy } from "@runic-artifex/vite-plugin-runic";

export default { ...runicDevSessionProxy() };
```

and load the client from `main.ts` in development only:

```ts
// src/main.ts, before bootstrapApplication
if (typeof ngDevMode === "undefined" || ngDevMode) await import("@runic-artifex/vite-plugin-runic/client");
```

A production build (`optimization` on, the default `production`
configuration) defines `ngDevMode` as `false`, so the import and the client's
chunk are left out. `isDevMode()` would keep the chunk in the output, because
the build cannot evaluate a function call.

The entries keep the development state in the `ng serve` process and answer:

- `/__runic/state` with that state, as `runic()` does: the connection, the
  timeline and the Bridge calls (without C# handlers: the Angular CLI's server
  reads no source map) without failures, and under `dotnet runic dev` the session header
  ([Dev session](#dev-session-and-host-scripts)). The answer is `200` with JSON; without `dotnet runic dev` the
  entry answers too, without the header.
- `/__runic/diagnostics`, which the client posts its reports to. Each Views
  failure is printed to the `ng serve` terminal with its exception type,
  message and stack:

  ```text
  [runic] views editorSave failed
    System.InvalidOperationException: Disk full.
      at Notes.Editor.Save() in /src/Notes/Editor.cs:line 12
  ```

For [Runic Inspect](#runic-inspect), the entries also answer
`/__runic/sources` (the development source map, found as `runic()` finds it,
relative to the `ng serve` working directory), `/__runic/open` (a C# location,
opened through the Angular development server's `/__open-in-editor`) and
`/__runic/handlers` (the handler analysis of a component and its
`templateUrl`), each for pages of this machine only, unless `allow` or
`RUNIC_DEV_ALLOW` admits other machines to `/__runic/sources` and
`/__runic/handlers` ([Runic Inspect from another machine](#runic-inspect-from-another-machine)).

`runicDevSessionProxy({ maxTimelineEntries, printFailures, sourceMap, inspect, allow })`
takes the same timeline limit as `runic()`, `printFailures: false` to keep the
terminal quiet, `sourceMap` as `runic({ sourceMap })` takes it (`false` turns
off `/__runic/sources` and `/__runic/open`), `inspect: false` to turn off
`/__runic/handlers`, and `allow` as `runic({ allow })` takes it. The `stale-input` warning works the same as with `runic()`; turn it off
from the page with the registered symbol above.

### The client's channel

Loaded by `runic()`, the client reports over Vite's HMR connection. Loaded
otherwise, it posts each report as `{ "event", "data" }` JSON to
`/__runic/diagnostics` on its own origin, one at a time and in order, and keeps
at most 100 waiting. `runic()` answers that endpoint too. The endpoint accepts
only a `POST` with `Content-Type: application/json` and an `Origin` of the
server itself (a cross-site page would need a CORS preflight, which it never
grants), rejects a `Sec-Fetch-Site` other than `same-origin` and a body over
64 KiB, and sanitizes every entry like any other timeline entry. If the first
report gets no `204` answer, the page has no Runic development server, and the
client stops and removes its listeners. Without Vite's HMR context the client
installs the `stale-input` warning only after that first answer.

## Dev session and host scripts

Under `dotnet runic dev`, the Vite process gets a random value for the session
in `RUNIC_APPLICATION_DEV_SESSION`, and `/__runic/state` answers with it in the
`X-Runic-Dev-Session` header. Since 0.11, `dev` treats the server as ready
only when that answer matches. Another process that takes the port before Vite
binds it cannot answer, so `dev` never loads its pages into the application.
Pages and other responses do not carry the header. Use the plugin from the same
release as `dotnet-runic`, because an older plugin does not answer and `dev`
times out with `RAPPDEV1007`.

`runicDevSessionProxy()` answers the session for the Angular CLI (above).
`dotnet runic dev` expects the answer from an Angular frontend whose
`package.json` declares this package. `runicDevSession()`, which answered only
the session, was removed in 0.15: SvelteKit uses `runic({ html: false })`
(above).

The `runic-app` template's SvelteKit and Angular frontends are set up this way.

The native host serves `webui.js` and its client script (`runic-desktop.js`,
`runic-views.js`, `runic-cswebui.js` or `runic-desktop-views.js`) from the root
of the web content, outside the Vite build. The plugin leaves those classic script tags
unbundled, so `vite build` prints no "can't be bundled without
type="module"" warning, and points them at the web root from every page: a
nested page such as `pages/settings.html` loads `../webui.js` in the build and
during `dotnet runic dev`.

## Plugin API

Another Vite plugin adds display-safe entries to the timeline through the
plugin's `api`, Vite's convention for inter-plugin communication:

```ts
const runicPlugin = config.plugins.find(plugin => plugin.name === "runic") as RunicVitePlugin | undefined;
runicPlugin?.api.reportDiagnostic({ source: "my-plugin", kind: "event", label: "Catalog reloaded" });
```

`source` is a short lowercase name; `views`, `assets` and `translations` are
reserved for the Runic packages of those names. `kind` is one of `command`,
`receipt`, `event`, `operation`, `connection` and `error` (a closed set); only an
`error` entry keeps its `failure`. The plugin bounds and redacts every entry.

The HMR resource helpers (`preserveRunicHmrResource`, `disposeRunicHmrResource`)
and `reportRunicState` of earlier previews are gone: keep HMR state in
`import.meta.hot.data`, and the connection state now comes from
`@runic-artifex/views` without application code.

