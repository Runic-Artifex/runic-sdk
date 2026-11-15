# Debugging a Runic application

A Runic application has two halves to debug: the .NET Window with its ViewModels, and the page that renders them in
a web engine. This guide shows how to debug both under `dotnet runic dev`, from your IDE's .NET debugger to the
browser's developer tools, and how the Runic tools of 0.20 connect a click in the page to the C# that runs for it.
Each section links the README that describes its tool in full.

| You want to… | Use |
| --- | --- |
| Stop on a breakpoint in a ViewModel | [Your IDE's .NET debugger](#the-net-debugger-in-your-ide) |
| Inspect the page's DOM, console, network, and scripts | [The browser's developer tools](#the-browsers-developer-tools-per-engine) |
| See each Bridge call with the C# that ran and open it in the editor | [The Runic dock](#the-runic-dock) |
| Click an element and see its commands, state, and texts | [Runic Inspect](#runic-inspect) |
| Time C# handlers, or follow a click into the ViewModel's HTTP and database calls | [Performance panel and traces](#performance-panel-async-stacks-and-traces) |
| See Bridge errors before you build | [Analyzers](#bridge-errors-while-you-type) |

**"DevTools" means two things.** This guide, like the SDK's READMEs, says **the browser's developer tools** for the
engine's own tools (F12, Inspect, Safari's Web Inspector) and **the dock** for the Vite DevTools dock
(`DevTools()` from `@vitejs/devtools`) with its Runic panel. They are separate: the dock is part of the page under
`vite dev`, and the browser's developer tools are the engine's.

Everything here is for development and costs nothing in production; see [In production](#in-production).

## The .NET debugger in your IDE

`dotnet runic dev` builds the application, starts the frontend's development server, and runs the Window under
`dotnet watch`, which restarts it after a C# edit. A debugger attached to that Window ends at each restart. To debug
C#, let the IDE start the Window instead:

1. Run `dotnet runic dev --no-window`. It serves only the frontend and prints `[dev] Ready` when the page can load.
2. Start the Window with the IDE's debugger (F5) from a launch profile with `DOTNET_ENVIRONMENT=Development`.

The IDE's build keeps the development page that `dev` wrote, so frontend edits still reach the Window while you
debug. `dev` does not restart the Window behind the debugger: apply C# edits with the IDE's Hot Reload, and restart
the Window after a change Hot Reload cannot apply, such as a new ViewModel member. To keep `dev` starting the Window,
attach to the process that `[dev] Window started: process <pid>` names instead (`--no-dotnet-watch` keeps it from
restarting).

[Debug the Window in your IDE](../tools/dotnet-runic/README.md#debug-the-window-in-your-ide) has the details and the
snippets for a project that did not come from the template.

### Visual Studio and Rider

Projects from `dotnet new runic-app` have `Properties/launchSettings.json`. Visual Studio and Rider list its
profiles next to the Start button:

- **`<Project>`** starts the Window with `DOTNET_ENVIRONMENT=Development`: the browser's developer tools on the page
  and detailed Bridge errors. `dotnet run` uses it too.
- **`<Project> (WebView2 script debugging)`** (Runic Desktop) also opens WebView2's DevTools Protocol endpoint on port
  9222 on Windows, and in Visual Studio attaches its JavaScript debugger to the page beside the .NET debugger
  (`jsWebView2Debugging`).

Run `dotnet runic dev --no-window` in a terminal (Visual Studio's Developer PowerShell, or Rider's terminal) and press
F5. In Rider you can add `dotnet runic dev --no-window` to the run configuration's "Before launch" list as an external
tool. Without `dev`, F5 builds the production frontend and the Window shows that. See the template README's
[Debug it in your IDE](../tools/Runic.Application.Templates/content/runic-app/README.md#debug-it-in-your-ide).

### VS Code

`dotnet new runic-app --editor vscode` adds `.vscode/` with tasks and configurations for the C# extension
(`ms-dotnettools.csharp`; C# Dev Kit is not needed):

| Configuration | What it does |
| --- | --- |
| `Window` | Runs the `runic: dev --no-window` task until `[dev] Ready`, builds, and starts the Window under the debugger. |
| `Attach to the Window (dotnet runic dev)` | Runs the `runic: dev` task and attaches to the Window process. |
| `Window + WebView2 (Windows)` | Runic Desktop on Windows: the Window with WebView2's endpoint on port 9222, and VS Code's JavaScript debugger attached to the page, so breakpoints in `Frontend/src` stop too. |

The tasks put `dev`'s failures (`RAPPDEV` codes) and build errors in the Problems panel. VS Code builds from other
vendors, such as VSCodium, cannot run Microsoft's .NET debugger; use netcoredbg there.

**The Runic extension for VS Code** (`runic-artifex.runic`, from
[runic-editors](https://github.com/Runic-Artifex/runic-editors/blob/main/vscode/README.md)) adds a Dev Session view
that follows `dotnet runic dev`, F5 of the Window without a `launch.json`, doctor results, and a ViewModels tree that
opens the C# members. It is merged but **not published for 0.20**: there is no release or marketplace entry yet.
To try it, download the `vscode-vsix` artifact of a successful CI run of runic-editors' `main` and install it:

```sh
gh run download --repo Runic-Artifex/runic-editors --name vscode-vsix
code --install-extension runic.vsix
```

It replaces the Runic MF2 Translations extension; uninstall that one first.

### The IDE contract

IDE tasks and extensions read `dotnet runic dev` through stable lines and a session file, described in
[IDE contract](../tools/dotnet-runic/README.md#ide-contract):

- **Lines:** `[dev] Window started: process <pid>` (the process to attach to), `[dev] Ready`, and
  `[dev] Window exited: process <pid>`. A failure is one line in MSBuild's form,
  `<project> : error RAPPDEV1007: <message>`, which `$msCompile` matches.
- **Session file:** `obj/runic/dev-session.json` (`runic.application.tool.dev-session/1`), rewritten on each change.
  `state` is `starting`, `ready` or `stopped`; a `dev` that was killed leaves its last state, so a reader trusts
  `starting` and `ready` only while a process with `processId` runs and started at `processStartedAt` (to within a
  second). It also lists the development server's URL, the pages, the Window's process and owner (`dev`, or `ide`
  with `--no-window`), `debugEndpoints` (WebView2's DevTools Protocol endpoint when the environment opens one),
  `unavailableDebugEndpoints` (one that the engine will not open, with a `reason`), and `sourceMap`, the Bridge
  source map that the page's tools read. See [Session file](../tools/dotnet-runic/README.md#session-file).

## The browser's developer tools per engine

A Runic Desktop window has the browser's developer tools only when `DOTNET_ENVIRONMENT` (or else
`ASPNETCORE_ENVIRONMENT`) is `Development`, which `dotnet runic dev` and the template's launch profiles set. In other
environments they are off, so a user of a release cannot script the page and its Bridge.
`DesktopWebContentPolicy.DevTools` overrides it per window
([links, new windows, downloads and developer tools](../packages/dotnet/Runic.Desktop/README.md#links-new-windows-downloads-and-developer-tools)).

| Engine | Where | Open | Debugger and automation |
| --- | --- | --- | --- |
| WebView2 | Windows | F12, or Inspect from the page's context menu | A DevTools Protocol endpoint on request (below) |
| WKWebView | macOS 13.3 and later | Safari ▸ Develop ▸ your Mac ▸ the window's page (turn on "Show features for web developers" in Safari's Advanced settings) | Safari's Web Inspector only; no DevTools Protocol endpoint |
| WebKitGTK | Linux | Inspect Element from the page's context menu (WebKit's inspector) | No DevTools Protocol endpoint |
| Installed browser | CS-WebUI's default, and Runic Desktop's fallback without a WebView | The browser's own F12 | The browser's own rules: Runic's policy does not apply to it |

- **WebView2 endpoint.** WebView2 opens a Chrome DevTools Protocol endpoint when the Window starts with
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`: the template's WebView2 script debugging
  profile sets it, or pass it with `dotnet runic dev -e WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`.
  `dev` then lists `http://127.0.0.1:9222/` under `debugEndpoints` in the session file. VS Code's JavaScript debugger
  (`msedge` with `useWebView`), Visual Studio's, `edge://inspect`, and agents attach to it.
- **Not when elevated.** WebView2 Runtime 150 and later ignores that variable in an elevated process. When `dev`
  runs as administrator, its Window does too, so `dev` lists the endpoint under `unavailableDebugEndpoints` with
  `"reason": "elevated"` and prints a `[dev] Debugging:` hint. F12 still works; for the endpoint, run `dev` and your
  IDE without elevation.
- **WKWebView** makes the page inspectable (`isInspectable`) in Development; macOS before 13.3 decides that itself.
- **WebKitGTK** turns on WebKit's developer extras in Development. Runic does not set WebKit's remote inspector
  variables, so IDE JavaScript debuggers attach only on Windows.

**For agents.** Where a DevTools Protocol endpoint is open (WebView2 above, or a Chromium browser started with
remote debugging), `chrome-devtools-mcp --browser-url=http://127.0.0.1:9222` or Playwright's `connectOverCDP`
attaches to the Window's page. In the page, `globalThis[Symbol.for("runic.vite.inspect")].describe(element)` returns
Runic Inspect's overview as data (see [Runic Inspect](#runic-inspect)). On macOS and Linux an agent has no endpoint
to attach to.

## The Runic dock

Under `dotnet runic dev`, a page built with Vite shows the Vite DevTools dock, a small bar at the edge of the window,
when `Frontend/vite.config.ts` registers `DevTools()`. `dotnet new runic-app` registers it for React, Vue, Svelte,
SvelteKit, and Angular with `--angular-build vite`; the Angular CLI runs no Vite plugins and has no dock. To turn it
off, delete the `DevTools(),` line.

Its **Runic** panel shows:

- **Bridge calls**, newest first, with the C# that ran and how long the page waited:

  ```text
  command EditorViewModel.save → SaveAsync() 23 ms
  C# 21 ms · revision 4 · EditorViewModel.cs:42
  ```

  `C# <n> ms` is .NET's own timing of the handler. **Open** opens the handler (the `[RelayCommand]` method, a setter)
  in your editor: the one `LAUNCH_EDITOR` names, else a running VS Code, Rider, or other editor Vite recognizes.
- **Failures**: a failed call shows .NET's exception with a button per stack frame in your ViewModels; the **Last
  failure** card links the same frames. .NET sends the detail only in Development
  ([failure detail in development](../packages/dotnet/Runic.Application.Views/README.md#failure-detail-in-development)).
- **Connection**, each View's **state** as a tree, and **superseded commands**.

The first time a page opens the dock, it asks for the **one-time code** that Vite DevTools prints in the
`dotnet runic dev` output. Keep that check on: it keeps other pages of this machine out of the dock. See
[Bridge call timeline in the dock](../packages/web/vite-plugin-runic/README.md#bridge-call-timeline-in-the-dock).

The handlers and locations come from the **development source map** that each build writes,
`obj/<Configuration>/<TargetFramework>/runic-bridge/sources.json`, from the assembly's portable PDB; `dev` passes its
path to the development server. The generated clients link into it too: Go to Definition on `client.increment()` in
a tsserver-based editor opens the C# handler. See
[Source map and open in editor](../packages/web/vite-plugin-runic/README.md#source-map-and-open-in-editor).

## Runic Inspect

Press **Alt+Shift+X** in the page to turn on Runic Inspect, then click an element (the click does not reach the
page's handler). Esc turns it off. The overview shows:

- the element's **component** and the components around it, with a link to its source (React, Vue, Svelte, Angular);
- the **commands** it calls, with their C# handlers, file:line and availability (`canSave`), found in the
  component's source **before the first click** ("from the source, not called yet") and learned from the calls it
  makes;
- the **ViewModel members** it sets and the state it shows, with current values;
- its recent **Bridge calls**;
- **texts** from the ViewModel (text references) and the **messages** of `m.*()` calls it shows, with catalog, key,
  locale, and `.rmf2` file:line. **Edit** changes a message's text in every locale in place, without a reload
  (`@runic-artifex/vite-plugin-runic-translations` 0.12 or later).

Runic Inspect is plain DOM, so it works in every engine: WebView2, WKWebView, WebKitGTK, and an installed browser.
Under `dotnet runic dev`, where the window's document is the .NET host's page, it asks the Vite server for the
source map, the handler analysis, message sources, and opening over the development client's HMR connection, with
the same access rules as the HTTP endpoints. Under the Angular CLI, `runicDevSessionProxy()` answers the source map,
handler analysis, and open requests over HTTP.

**What opens in the editor.** Only a location in a file the source map lists (your C#), a frontend file that Vite
serves to the page, or the `.rmf2`/`.mf2` source that Runic Translations names for a message. Nothing else, unlike
Vite's `/__open-in-editor`.

**From another machine.** Runic Inspect's data is loopback-only by default. `runic({ allow })` or `RUNIC_DEV_ALLOW`
(addresses, CIDR networks such as a Tailscale network, host names) lets other machines read the source map and
handler analysis and edit translations. Opening in the editor always stays loopback-only, because it launches the
editor on the development machine: such a page shows locations without links.

See [Runic Inspect](../packages/web/vite-plugin-runic/README.md#runic-inspect),
[Edit a translation on the page](../packages/web/vite-plugin-runic/README.md#edit-a-translation-on-the-page), and
[Runic Inspect from another machine](../packages/web/vite-plugin-runic/README.md#runic-inspect-from-another-machine).
`runic({ inspect: false })` turns it off.

## Performance panel, async stacks and traces

The [tracing guide](tracing.md) covers these in full:

- **Performance panel.** In a Chromium engine (WebView2, Chrome, Edge), record with "Show custom tracks" on. The
  **Runic** track group has a **Bridge calls** track and, under `dotnet runic dev`, a **C# handlers** track with the
  handler's duration measured in .NET. Other engines list the same calls in the dock without the tracks.
- **Async stacks.** A breakpoint after a reply, such as in a state subscriber, shows a
  `Runic command EditorViewModel.save` async stack segment back to the click that made the call (Chromium).
- **One trace from the page to .NET.** With `OTEL_EXPORTER_OTLP_ENDPOINT` set in the shell that runs
  `dotnet runic dev`, the page's span for a call, the `runic.bridge.*` span, and the ViewModel's own spans form one
  trace in a local OTLP viewer such as the Aspire dashboard. `dev` starts no container or viewer: start the one you
  want yourself.

## Bridge errors while you type

`Runic.Application.Views` ships Roslyn analyzers for the Bridge rules that the source alone shows, so Visual Studio,
Rider, and VS Code's C# extension report them as you type, with the generator's ID, message, and help link, instead
of only after a build. Code fixes rename a command (or its `[RelayCommand]` method), make a setter private, or make a
class public. The generator still checks everything at build time. See
[Errors and warnings while you type](diagnostics.md#errors-and-warnings-while-you-type).

With `TreatWarningsAsErrors`, `RUNICBRIDGE` warnings fail the build like other warnings. Keep one a warning with
`WarningsNotAsErrors`, hide it with `NoWarn`, or fix the declaration.

## In production

None of this ships:

- `vite build` replaces the development client with an empty module: no dock, no Runic Inspect, no translation
  editor, no OTLP export. Its output is byte-identical with and without them.
- `@runic-artifex/views` has a development build, which `vite dev`, `ng serve`, and Vitest resolve, and a production
  build without call reporting, `console.createTask`, or trace context. `bun build` bundles the development build
  unless you pass `--production` or set `NODE_ENV=production`; the Views MSBuild targets set `NODE_ENV=production` for
  a Release build and `dotnet publish`. See [Development build](../packages/web/views/README.md#development-build).
- The source map stays in `obj`: it is not in generated TypeScript, build or publish output, or a package.
- Outside Development, a Runic Desktop window has no browser developer tools, replies carry no handler timing, and
  failures carry no exception detail. Do not ship with `DOTNET_ENVIRONMENT=Development`; see the
  [security guide](security.md#release-builds).

## Troubleshooting

- **No DevTools Protocol endpoint on Windows.** `dev` or your IDE runs elevated, and `dev` printed a
  `[dev] Debugging:` hint; the session file lists the endpoint under `unavailableDebugEndpoints`. Run both without
  elevation. Without that hint, check that `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` reaches the Window: set it in the
  launch profile, with `dotnet runic dev -e`, or in the environment `dev` runs in.
- **F12 does nothing.** The Window does not run in Development: start it with `dotnet runic dev` or a launch profile
  that sets `DOTNET_ENVIRONMENT=Development`. On macOS, use Safari's Develop menu instead.
- **The breakpoint never binds, or the debugger detaches.** `dotnet watch` restarted the Window after a C# edit. Use
  `dotnet runic dev --no-window` and F5.
- **F5 shows the production page.** `dotnet runic dev --no-window` was not running when the IDE built. Start it
  first and wait for `[dev] Ready`.
- **The dock asks for a code.** Enter the one-time code that Vite DevTools printed in the `dotnet runic dev` output.
- **No dock.** The Vite configuration does not register `DevTools()`, or the frontend runs on the Angular CLI.
- **No C# handler in the dock, or Runic Inspect shows no C# location.** The project has not been built since the
  ViewModel changed, the build has no portable PDB, or `runic({ sourceMap: false })` is set. The overview's notes say
  which. Locations without links mean the page was opened from another machine.
- **A command shows only after its first click.** The handler calls the client in another file (a store, a service,
  a custom hook); the overview names the import.
- **No messages in Runic Inspect.** The Translations Vite plugin is older than 0.12, or the component does not render
  again on a text change (`useTextLocale`, `textLocaleSignal`).
- **No C# handlers track, or no page spans in the trace.** See the [tracing guide's troubleshooting](tracing.md#troubleshooting).
- **A Bridge error appears only after building.** The rule depends on more than the source; the analyzers leave it to
  the generator.
