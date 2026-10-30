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

`runicDevSessionProxy({ maxTimelineEntries, printFailures })` takes the same
timeline limit as `runic()` and `printFailures: false` to keep the terminal
quiet. The `stale-input` warning works the same as with `runic()`; turn it off
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
`package.json` declares this package. `runicDevSession()` is a Vite plugin
that answers only the session, without the development client; SvelteKit
configurations from before 0.14 use it. It is deprecated in 0.14 and removed
in 0.15: use `runic({ html: false })` instead.

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

