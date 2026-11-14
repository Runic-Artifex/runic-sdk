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
`@vitejs/devtools` peer enables the Runic dock; register `DevTools()` in your
Vite configuration when selecting `devtools: true`. The plugin runs on Node 24.18
or later 24.x, or Node 26.

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
The dock does not show these calls yet.

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
    "location": { "file": "/src/Demo/CounterViewModel.cs", "line": 18, "column": 33 }
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
  a CommunityToolkit `[ObservableProperty]`. Without a PDB or source file an
  entry has no `location`; the build does not fail. A map is as current as the
  last build: `dotnet watch`, and so `dotnet runic dev`, rebuilds it after a C#
  edit, and the plugin reads it again when the file changes.
- **Where the plugin finds it.** `dotnet runic dev` passes the map's path in
  `RUNIC_APPLICATION_DEV_SOURCE_MAP`. Otherwise, or when that file does not
  exist, the plugin takes the newest `runic-bridge/sources.json` under `obj` of
  the project `dotnet runic dev` runs, else of the Vite root or its parent (the
  project of a `Frontend/` directory). `runic({ sourceMap:
  "../obj/Debug/net10.0/runic-bridge/sources.json" })` names the file, or a
  directory to search, relative to the Vite root, and `sourceMap: false` turns
  both endpoints off. Without a map `/__runic/sources` answers 404.
- **Opening.** `POST /__runic/open` with `{ "id": "<entry id>" }`, or with the
  exact `{ "file", "line", "column" }` of an entry, opens that location through
  Vite's own editor launcher ([launch-editor](https://github.com/vitejs/launch-editor):
  `LAUNCH_EDITOR`, else a running editor such as VS Code or a JetBrains IDE like
  Rider, else `VISUAL`/`EDITOR`) and answers 200. It is meant for the page,
  DevTools panels and the Runic dock:

  ```ts
  await fetch("/__runic/open", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id: "Demo.CounterViewModel/command/increment" }),
  });
  ```

- **What it refuses.** Vite's `/__open-in-editor` opens any path; the Runic
  action opens only a location the map lists, of a file that exists, so a
  page cannot open anything else. It answers a POST of JSON (415 otherwise)
  from a page of the development server's own origin (`Origin` and
  `Sec-Fetch-Site`; a cross-origin page cannot send JSON without a CORS
  preflight, which it never grants) and from this machine (a loopback client
  address), else 403; an id or location the map does not list, an entry
  without a location, and a file that no longer exists answer 404. The map
  itself answers only same-origin requests from this machine too.
- **Only development servers.** Both endpoints exist only on the development
  server (`vite dev`, and `ng serve` through `runicDevSessionProxy()`), not in
  `vite build` or `vite preview`.

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
  the `.rmf2`/`.mf2` file:line that defines it. See
  [Messages of `m.*()` calls](#messages-of-m-calls).

C# locations open in your editor through [`/__runic/open`](#source-map-and-open-in-editor);
frontend files through Vite's `/__open-in-editor`.

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
`runic({ inspect: false })` leaves it out of the client and turns off
`/__runic/handlers`. Under the Angular CLI, `runicDevSessionProxy()` answers
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
  for its source (`GET /__runic-translations/message`), and its link opens it
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
[`@runic-artifex/views/component`](../views/README.md#components). `vite build`
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

SvelteKit renders its pages itself and warns about every plugin with a
`transformIndexHtml` hook. Use `runic({ html: false })`: the plugin then has no
such hook, injects nothing into HTML and leaves host scripts alone, and keeps
everything else: the timeline, the dock, `/__runic/state` and the dev session.
The page loads the client from its client hooks:

```ts
// vite.config.ts
plugins: [sveltekit({ adapter: runicAdapter() }), runic({ html: false })],
```

```ts
// src/hooks.client.ts
import "@runic-artifex/vite-plugin-runic/client";
```

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
  timeline without failures, and under `dotnet runic dev` the session header
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
`templateUrl`), each for pages of this machine only.

`runicDevSessionProxy({ maxTimelineEntries, printFailures, sourceMap, inspect })`
takes the same timeline limit as `runic()`, `printFailures: false` to keep the
terminal quiet, `sourceMap` as `runic({ sourceMap })` takes it (`false` turns
off `/__runic/sources` and `/__runic/open`), and `inspect: false` to turn off
`/__runic/handlers`. The `stale-input` warning works the same as with `runic()`; turn it off
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

