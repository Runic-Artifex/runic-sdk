# Debugging a Runic application

A Runic application has two halves to debug: the .NET Window with its ViewModels, and the page that renders them in
a web engine. This guide shows how to debug both under `dotnet runic dev`, from your IDE's .NET debugger to the
browser's developer tools, and how the Runic tools of 0.20 connect a click in the page to the C# that runs for it.
Each section links the README that describes its tool in full.

| You want to… | Use |
| --- | --- |
| Stop on a breakpoint in a ViewModel | [Your IDE's .NET debugger](#the-net-debugger-in-your-ide) |
| Inspect the page's DOM, console, network, and scripts | [The browser's developer tools](#the-browsers-developer-tools-per-engine) |
| Stop on C# and `Frontend/src` breakpoints in one Visual Studio session | [Runic DevKit's Debug the Window](#visual-studio) |
| Stop on a breakpoint in the frontend's sources from your IDE, on any OS | [The page in Chrome](#the-page-in-chrome-dotnet-runic-dev---browser) |
| See each Bridge call with the C# that ran and open it in the editor | [The Runic dock](#the-runic-dock) |
| Click an element and see its commands, state, and texts | [Runic Inspect](#runic-inspect) |
| See the same in Chrome's or Edge's own developer tools: a Runic panel and an Elements pane | [The Runic DevTools extension](#the-runic-devtools-extension) |
| See View clients, states and field states readably in the console | [Custom object formatters](#custom-object-formatters) |
| See a component's View, field state and operation status in React DevTools or Vue DevTools | [Framework DevTools](#react-devtools-and-vue-devtools) |
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

### Visual Studio

Projects from `dotnet new runic-app` have `Properties/launchSettings.json`. Visual Studio lists its profiles next to
the Start button:

- **`<Project>`** starts the Window with `DOTNET_ENVIRONMENT=Development`: the browser's developer tools on the page
  and detailed Bridge errors. `dotnet run` uses it too.
- **`<Project> (CS-WebUI script debugging)`** (CS-WebUI, since 0.22) also opens the DevTools Protocol endpoint of the
  Window's Chrome or Edge on `http://127.0.0.1:9225/`. Any local process can attach to it while the Window runs
  ([CS-WebUI pages in a JavaScript debugger](#cs-webui-pages-in-a-javascript-debugger)).

**Runic DevKit for Visual Studio** (`runic-artifex.runic-devkit-vs`, Visual Studio 2026 on Windows x64; see its
[README](https://github.com/Runic-Artifex/runic-editors/blob/main/visualstudio/README.md)) adds the `dotnet runic dev`
loop to Visual Studio. Install it from the Visual Studio Marketplace (Extensions > Manage Extensions, search for
"Runic DevKit"; from runic-editors 0.22.0), or install `runic-devkit-vs.vsix` from the
[runic-editors releases](https://github.com/Runic-Artifex/runic-editors/releases). Its commands are in Tools > Runic
and in the Runic tool window (View > Other Windows > Runic):

- **Debug the Window** debugs the C# code and the page in one session, with breakpoints in `Frontend/src` and in your
  ViewModels. It starts `dotnet runic dev --no-window` if it does not run yet, waits for `[dev] Ready`, builds the
  project, and starts the Window under the .NET debugger (CoreCLR) with the session file's `window.environment` and a
  DevTools Protocol port: a free loopback port for WebView2 (Runic Desktop), 9225 for CS-WebUI. When the page
  answers on that port, it attaches Visual Studio's JavaScript debugger over the "JavaScript and TypeScript (Chrome
  DevTools/V8 Inspector)" connection. If a `dev` that DevKit started runs its own Window, it asks before it restarts
  that `dev` with `--no-window`; stop a `dev` that runs in a terminal with Ctrl+C first. If the page does not appear
  within three minutes of the Window running, the .NET debugger stays attached and the Runic SDK Output pane says why.
- **Attach to the Page (JavaScript)** attaches the JavaScript debugger alone to a page that already runs with an
  endpoint: one of the session file's `debugEndpoints`, or a Window that Debug the Window started. With several, it
  asks which one.
- **F5 asks first.** When F5 or Ctrl+F5 starts a Runic project and no `dotnet runic dev --no-window` runs for it, a
  message asks whether to start it. Yes starts it, waits until it is ready and then starts the Window; No starts the
  Window without it, so the Window shows the production frontend; Cancel starts nothing. F5 runs a launch profile, so
  it debugs only the .NET code: use Debug the Window for page breakpoints.
- When DevKit starts `dotnet runic dev` with its own Window (Start dotnet runic dev), it passes `--debug-endpoint` with
  `dotnet-runic` 0.22 and later, so a CS-WebUI Window opens its DevTools Protocol port. A `dev` that you start in a
  terminal opens none (D070.17).

Page breakpoints need:
- Visual Studio's **JavaScript diagnostics** component (`Microsoft.VisualStudio.Component.JavaScript.Diagnostics`).
  The template's `.vsconfig` names it next to the .NET desktop development workload, and Visual Studio offers to
  install a missing one when it opens the project. For another project, copy `.vsconfig` from a new one next to its
  solution.
- A Visual Studio that does not run as administrator: WebView2 ignores the port in an elevated process.
- `@runic-artifex/vite-plugin-runic` 0.22 or later, whose development source maps have absolute `sources`
  ([below](#the-development-servers-source-maps)). With an older plugin, breakpoints in `.tsx`, `.vue` and `.svelte`
  files stay unbound.

The template's `(WebView2 script debugging)` profile of 0.20 and 0.21 is gone: under F5, Visual Studio's JavaScript
adapter (`jsWebView2Debugging`) starts the Window without the profile's environment variables, so the Window runs
outside Development, Runic clears WebView2's debugging variable, and the adapter gives up and stops the Window. Delete
that profile from an existing project's `launchSettings.json`, and use Debug the Window instead.

Without the extension, run `dotnet runic dev --no-window` in the Developer PowerShell and press F5 for the .NET code.
For the page, start the Window with a DevTools Protocol port and use Debug > Attach to Process with the connection type
above ([CS-WebUI pages](#cs-webui-pages-in-a-javascript-debugger) shows the steps). Without `dev`, F5 builds the
production frontend and the Window shows that. See the template README's
[Debug it in your IDE](../tools/Runic.Application.Templates/content/runic-app/README.md#debug-it-in-your-ide).

### Rider

Rider lists the same launch profiles next to its Run button. Run `dotnet runic dev --no-window` in Rider's terminal,
or add it to the run configuration's "Before launch" list as an external tool, and debug the `<Project>` profile.
Without `dev`, the Window shows the production frontend.

### VS Code

`dotnet new runic-app --editor vscode` adds `.vscode/` with tasks and configurations for the C# extension
(`ms-dotnettools.csharp`; C# Dev Kit is not needed):

| Configuration | What it does |
| --- | --- |
| `Window` | Runs the `runic: dev --no-window` task until `[dev] Ready`, builds, and starts the Window under the debugger. |
| `Attach to the Window (dotnet runic dev)` | Runs the `runic: dev` task and attaches to the Window process. |
| `Window in Chrome` | Every OS: runs the `runic: dev --browser` task and opens the Window's page in Chrome under VS Code's JavaScript debugger ([The page in Chrome](#the-page-in-chrome-dotnet-runic-dev---browser)). |
| `Window + WebView2 (Windows)` | Runic Desktop on Windows: the Window with WebView2's endpoint on port 9222, and VS Code's JavaScript debugger attached to the page, so breakpoints in `Frontend/src` stop too. |
| `Window + WebKitGTK (Linux)` | Runic Desktop on Linux (preview): the same with WebKitGTK, through `dotnet runic dev`'s CDP adapter on port 9222. |
| `Attach to the page (JavaScript)` | VS Code's JavaScript debugger on port 9222 alone: the Window that `dotnet runic dev` started on Linux, or a WebView2 Window started with the port on Windows. |

The tasks put `dev`'s failures (`RAPPDEV` codes) and build errors in the Problems panel. VS Code builds from other
vendors, such as VSCodium, cannot run Microsoft's .NET debugger; use netcoredbg there.

**Runic DevKit for VS Code** (`runic-artifex.runic-devkit`, from
[runic-editors](https://github.com/Runic-Artifex/runic-editors/blob/main/vscode/README.md)) adds a Dev Session view
that follows `dotnet runic dev`, F5 of the Window without a `launch.json`, doctor results, and a ViewModels tree that
opens the C# members. Install it from the VS Code Marketplace (from runic-editors 0.22.0):

```sh
code --install-extension runic-artifex.runic-devkit
```

VSCodium, Cursor and other editors that use Open VSX install `runic-devkit.vsix` from the
[runic-editors releases](https://github.com/Runic-Artifex/runic-editors/releases) instead
(`code --install-extension runic-devkit.vsix`, or the editor's own command); it is not on Open VSX.

It replaces the Runic MF2 Translations extension and the extension of the 0.20 and 0.21 VSIX files, whose ID was
`runic-artifex.runic`; uninstall those first (`code --uninstall-extension runic-artifex.runic`).

### The IDE contract

IDE tasks and extensions read `dotnet runic dev` through stable lines and a session file, described in
[IDE contract](../tools/dotnet-runic/README.md#ide-contract):

- **Lines:** `[dev] Window started: process <pid>` (the process to attach to), `[dev] Ready`, and
  `[dev] Window exited: process <pid>`, and with `--browser` `[dev] Browser: Window serves at <url>`. A failure is one line in MSBuild's form,
  `<project> : error RAPPDEV1007: <message>`, which `$msCompile` matches.
- **Session file:** `obj/runic/dev-session.json` (`runic.application.tool.dev-session/1`), rewritten on each change.
  `state` is `starting`, `ready` or `stopped`; a `dev` that was killed leaves its last state, so a reader trusts
  `starting` and `ready` only while a process with `processId` runs and started at `processStartedAt` (to within a
  second). It also lists the development server's URL, the pages, the Window's process and owner (`dev`, or `ide`
  with `--no-window`), `debugEndpoints` (WebView2's DevTools Protocol endpoint when the environment opens one),
  `unavailableDebugEndpoints` (one that the engine will not open, with a `reason`), `browser` (the address that
  `--browser` serves the Window at), and `sourceMap`, the Bridge
  source map that the page's tools read. See [Session file](../tools/dotnet-runic/README.md#session-file).
- **Stopping:** write `stop` to `dev`'s standard input with `RUNIC_DEV_STOP_ON_INPUT=1` set, or send SIGINT or
  SIGTERM to `dev` itself (`processId`), not to the `dotnet` process that started it: the .NET 11 SDK does not forward
  SIGTERM to a local tool on Linux ([dotnet/sdk#57091](https://github.com/dotnet/sdk/issues/57091); set
  `DOTNET_CLI_ENABLEAOT=0` to make it), and no SDK forwards a SIGINT that reaches only that process. See
  [Stopping `dev`](../tools/dotnet-runic/README.md#ide-contract).

## The browser's developer tools per engine

A Runic Desktop window has the browser's developer tools in the window (F12, Inspect Element, Safari's Web
Inspector) only when `DOTNET_ENVIRONMENT` (or else `ASPNETCORE_ENVIRONMENT`) is `Development`, which
`dotnet runic dev` and the template's launch profiles set. `DesktopWebContentPolicy.DevTools` overrides it per window
([links, new windows, downloads and developer tools](../packages/dotnet/Runic.Desktop/README.md#links-new-windows-downloads-and-developer-tools)).

That setting covers only the tools in the window. A **remote debugging endpoint** that a variable in the Window
process's environment asks for is the engine's own: WebView2's `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` and
WebKitGTK's `WEBKIT_INSPECTOR_SERVER` and `WEBKIT_INSPECTOR_HTTP_SERVER`. `DevTools = false` does not close
WebKitGTK's (WebKitGTK 2.54.0, GTK 3 and GTK 4) and is not documented to close WebView2's. Outside Development the GTK 3
and GTK 4 hosts therefore remove both WebKit variables from the process's environment before their first web view, and
the WebView2 host removes `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` before it creates its WebView2 environment, also when
`DevTools` is `true`, so a release opens no such endpoint ([release builds](security.md#release-builds)). To debug a
release build's page that way, run it with `DOTNET_ENVIRONMENT=Development`.

| Engine | Where | Open | Debugger and automation |
| --- | --- | --- | --- |
| WebView2 | Windows | F12, Ctrl+Shift+I, Inspect from the page's context menu, Open DevTools in the window's system menu (Alt+Space), the template's View ▸ Developer Tools, or `DesktopWindow.OpenDevToolsAsync()` | A DevTools Protocol endpoint on request (below); DevTools extensions (below) |
| WKWebView | macOS 13.3 and later | Safari ▸ Develop ▸ your Mac ▸ the window's page (turn on "Show features for web developers" in Safari's Advanced settings) | Safari's Web Inspector only; no DevTools Protocol endpoint |
| WebKitGTK | Linux (GTK 3 and GTK 4) | Inspect Element from the page's context menu (WebKit's Web Inspector, docked or detached), the template's View ▸ Developer Tools, or `DesktopWindow.OpenDevToolsAsync()` | A DevTools Protocol endpoint for JavaScript debuggers under `dotnet runic dev` (preview, below), and WebKit's remote inspector |
| Installed browser | CS-WebUI's default, and Runic Desktop's fallback without a WebView | The browser's own F12 | The browser's own rules: Runic's policy does not apply to it |

- **WebView2 endpoint.** WebView2 opens a Chrome DevTools Protocol endpoint when the Window starts with
  `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`: the template's `Window + WebView2 (Windows)`
  VS Code configuration sets it, Runic DevKit's Debug the Window in Visual Studio adds it with a free port, or pass it
  with `dotnet runic dev -e WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`.
  `dev` then lists `http://127.0.0.1:9222/` under `debugEndpoints` in the session file. VS Code's JavaScript debugger
  (`msedge` with `useWebView`), Visual Studio's, `edge://inspect`, and agents attach to it.
- **Not when elevated.** WebView2 Runtime 150 and later ignores that variable in an elevated process. When `dev`
  runs as administrator, its Window does too, so `dev` lists the endpoint under `unavailableDebugEndpoints` with
  `"reason": "elevated"` and prints a `[dev] Debugging:` hint. F12 still works; for the endpoint, run `dev` and your
  IDE without elevation.
- **WebView2 DevTools window.** While a window's developer tools are on, its system menu (Alt+Space, or a right
  click on the title bar) has **Open DevTools**, and `await window.OpenDevToolsAsync()` opens the same Edge DevTools
  window from code; on WebKitGTK it shows WebKit's Web Inspector (since 0.23). It returns `false` where the developer
  tools are off, as in production, and on WKWebView and installed browsers. A menu bar item whose command id is
  `DesktopMenuItem.OpenDevToolsCommandId` (`runic.devtools.open`, since 0.23) does the same for the key window, and is
  disabled where it cannot; the `runic-app` template declares it as View ▸ Developer Tools in Development on Windows
  and Linux. See the [Runic.Desktop README](../packages/dotnet/Runic.Desktop/README.md#the-webview2-devtools-window-and-devtools-extensions).
- **WebView2 DevTools extensions** (since 0.21). In Development a WebView2 window loads unpacked browser extensions,
  such as React Developer Tools or Vue.js devtools, so their panels appear in its DevTools window and their hooks run
  in the page. List the extension folders (each one holds a `manifest.json`) in `DesktopHostOptions.DevToolsExtensions`,
  or for one session in `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS`, separated by `;`:

  ```csharp
  var options = new DesktopHostOptions
  {
      DevToolsExtensions = [@"C:\Tools\react-devtools-extension"],
  };
  ```

  ```powershell
  dotnet runic dev -e RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS="C:\Tools\react-devtools-extension;C:\Tools\vue-devtools"
  ```

  Get an unpacked extension by building it from its source (React's `packages/react-devtools-extensions`, Vue's
  `packages/chrome-extension`) or by extracting its release package. Outside Development the list is ignored and
  WebView2's extensions stay off, also those that a development run installed into the same profile. Extensions are
  installed into the window's WebView2 profile before its first page loads, so every window of that profile runs them;
  WebView2 keeps one until its files change. When a folder leaves the list, the next Development run with a non-empty
  list removes the extension that Runic installed from it; Runic records what it installed in
  `Runic.DevToolsExtensions.json` in the user data folder and never removes an extension it did not install. A folder that WebView2 rejects is reported as `devtools-extension-failed`, and the
  window opens without it. WKWebView, WebKitGTK and installed browsers ignore the list. Runic's CI checks that an
  extension's content script runs in the page; whether a given extension's DevTools panel works is up to WebView2's
  extension support, which does not cover every Chrome extension API.
- **WKWebView** makes the page inspectable (`isInspectable`) in Development; macOS before 13.3 decides that itself.
- **WebKitGTK.** `DevTools` controls WebKit's developer extras on both GTK hosts (and their side panels), so Inspect
  Element and the Web Inspector work in the window under `dotnet runic dev`. Since 0.21, `dotnet runic dev` also opens
  the page to JavaScript debuggers on port 9222, as WebView2 does on Windows (preview): it starts the Window with
  WebKit's remote inspector server (`WEBKIT_INSPECTOR_SERVER`) on a loopback port and translates between the Chrome
  DevTools Protocol and the WebKit Inspector Protocol. VS Code's JavaScript debugger attaches with the template's
  `Attach to the page (JavaScript)`: breakpoints in `Frontend/src` through the development server's source maps,
  stepping, conditions, logpoints, exceptions, the console, the page's variables and hover, also after a reload, an
  HMR update or a Window restart under `dotnet watch`, on GTK 3 and GTK 4, X11 and Wayland. `[dev] Debugging:` and
  the session file's `debugEndpoints` (`"engine": "webkitgtk", "protocol": "cdp"`) name the port. Rider's
  JavaScript debugger attaches to such ports too, but that is not verified yet. Setting a variable to an object or a
  `const`, restarting a frame, and the Elements and Network panels of a Chrome DevTools window are not supported. See
  the dotnet-runic README's
  [Debug the page's JavaScript on Linux](../tools/dotnet-runic/README.md#debug-the-pages-javascript-on-linux-preview).

  The endpoint listens on 127.0.0.1 only and refuses requests that carry an `Origin` or a `Host` other than an IP
  address or `localhost`, so a web page cannot reach it. WebKit's own server serves one client, and a new one takes its place: while no
  debugger is attached, GNOME Web (Epiphany), a WebKitGTK browser, opens the `inspector://` address that
  `[dev] Debugging:` names. Other local users can reach that port too.
  `--no-debug-endpoint` opens neither. WebKit serves either that socket or its Web Inspector over HTTP
  (`WEBKIT_INSPECTOR_HTTP_SERVER`) for any browser, never both; `dotnet runic dev --webkit-inspector` opens the HTTP
  one instead of the debuggers' endpoint, on WebKitGTK 2.54.3 or later. A `WEBKIT_INSPECTOR_SERVER` or
  `WEBKIT_INSPECTOR_HTTP_SERVER` you set yourself, with `-e` or in `dev`'s environment, is used as it is. Outside
  Development Runic clears both variables (above).
- **Do not set `WEBKIT_INSPECTOR_HTTP_SERVER` on WebKitGTK 2.54.2 or earlier**, releases without the fix for
  [WebKit bug 330602](https://bugs.webkit.org/show_bug.cgi?id=330602) (fixed on the 2.54 branch on 10 November 2026,
  after 2.54.2). Their HTTP inspector accepts connections with any website's `Origin` and `Host`, so while the port is
  open, any web page you visit can drive the Window's page and, through it, the Bridge. `--webkit-inspector` refuses
  them and opens the `inspector://` server instead, which speaks a protocol that no browser page can.

## The development server's source maps

Breakpoints in `Frontend/src` bind through the source maps that the frontend's development server sends with each
module. Since 0.22, `runic()` (`@runic-artifex/vite-plugin-runic`) makes their `sources` absolute file paths, such as
`/home/me/App/Frontend/src/App.tsx` or `C:/Users/me/App/Frontend/src/App.tsx`, where Vite 8 writes paths relative to
each module. A JavaScript debugger then finds your files without a web root. Visual Studio's needs that: it attaches
to the page over the DevTools Protocol with no web root, and with relative paths breakpoints in `.tsx`, `.vue` and
`.svelte` files stayed unbound. VS Code's JavaScript debugger (the template's configurations set `webRoot`), the
developer tools of Chrome, Edge and WebView2, and Runic Inspect read the absolute paths as they read Vite's.

Only `vite dev` changes: `vite build` and `vite preview` keep Vite's relative paths, so no path of the build machine
reaches a published map. A page that a machine other than yours loads from the development server sees your
project's directory in the maps, as it already does in Vite's `/@fs/` addresses. The Angular CLI's development server
(`--frontend angular` without `--angular-build vite`) writes maps of its own, which `runic()` does not reach; use a
debugger configuration with a web root there, or the Vite build of the Angular frontend.

## The page in Chrome (`dotnet runic dev --browser`)

Since 0.21, `dotnet runic dev --browser` serves the Window without a native window, and your IDE's JavaScript
debugger opens its page in Chrome or Edge at `http://127.0.0.1:9224/`, which `dev` redirects to the Window's own
address (it changes with every start and carries the Window's credential). This works the same on Windows, Linux and
macOS, so it is the IDE route to breakpoints in `Frontend/src` on macOS, where WKWebView has no DevTools Protocol
endpoint. The template's `Window in Chrome` configuration (`--editor vscode`) runs it: breakpoints through the
development server's source maps, stepping, conditions, logpoints, exceptions, the console, reloads and HMR.

Two differences from the Window: the page runs on **Chromium**, not on the Window's engine, so check WebKit- or
WebView2-specific behaviour in the Window; and it has **no native window features**: file dialogs, menus and the
window's commands report `OwnerUnavailable`, and there is no side panel. On Linux the address answers only the user who
runs `dev`; macOS and Windows cannot tell a loopback connection's user, so on a shared machine another local user's
process can follow it there. The Window admits one browser, and after
`dotnet watch` restarts it, open the address again. Attach the .NET debugger to the process that
`[dev] Window started` names, as without `--browser`. See the dotnet-runic README's
[Debug the page in Chrome](../tools/dotnet-runic/README.md#debug-the-page-in-chrome---browser).

**For agents.** Where a DevTools Protocol endpoint is open (WebView2 above, or a Chromium browser started with
remote debugging), `chrome-devtools-mcp --browser-url=http://127.0.0.1:9222` or Playwright's `connectOverCDP`
attaches to the Window's page. In the page, `globalThis[Symbol.for("runic.vite.inspect")].describe(element)` returns
Runic Inspect's overview as data (see [Runic Inspect](#runic-inspect)). On Linux, `dotnet runic dev`'s endpoint
translates what JavaScript debuggers use, not the page automation these tools drive, so they are not supported there;
on macOS there is no endpoint.

## CS-WebUI pages in a JavaScript debugger

Since 0.22, a CS-WebUI Window that runs in an installed Chrome, Edge or other Chromium browser can open that browser's
Chrome DevTools Protocol endpoint, so an IDE's JavaScript debugger attaches to the page you are using: breakpoints,
stepping, the console and the page's variables. Visual Studio debugs CS-WebUI pages only this way. Runic.Desktop adds
`--remote-debugging-port` to the browser's command line only when all of these hold:

- the Window process runs in **Development** (`DOTNET_ENVIRONMENT`, or else `ASPNETCORE_ENVIRONMENT`), the test that
  turns the developer tools on, and a `DesktopWindowOptions.WebContent` policy does not set `DevTools = false`;
- **`RUNIC_WEBUI_REMOTE_DEBUGGING_PORT`** asks for it: `0` for a free port that the browser picks at launch, or a fixed
  port. Outside Development the variable is ignored;
- the browser is Chromium-based and runs with its own profile directory: the generated one, or `ProfilePath`
  (Chromium 136 and later refuse remote debugging in the default profile). Firefox and the platform WebView fallback
  open no endpoint;
- the application's own browser arguments (`SetBrowserArguments`, `DesktopWindowOptions.BrowserArguments`) do not name
  `--remote-debugging-port`. If they do, theirs applies as written, in every environment, and Runic adds no second one.
  Unlike other custom arguments, the Development port does not replace Runic's Chromium defaults.

Any local process can attach to the endpoint, so it opens only when you or your IDE ask for it (D070.17): a terminal
`dotnet runic dev` opens none.

The endpoint exists only in untrimmed builds, such as the one `dotnet runic dev` runs. A trimmed or NativeAOT publish
turns the `Runic.Desktop.WebUiRemoteDebugging` feature switch off, so the trimmer removes the endpoint and its HTTP
client, and such a build opens no endpoint even in Development. Only the publish turns it off: a project that sets
`PublishAot` or `PublishTrimmed` keeps the endpoint in `dotnet build`, `dotnet run` and IDE debugging.

The browser listens on 127.0.0.1. Each window's browser gets its own port. When the endpoint is open, the Window process
prints `RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp http://127.0.0.1:<port>/` on its standard output, and
`RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=http://127.0.0.1:<port>/` when that browser exits. Before it reports an
endpoint, the Window confirms it:

- **A free port (`0`):** Chromium writes the port it bound into `DevToolsActivePort` in the profile.
- **A fixed port:** Chromium writes no such record, so the Window asks `/json/version` on 127.0.0.1. On Linux it also
  checks that no other user listens on that port. The Window holds a fixed port on 127.0.0.1 and `[::1]` until just
  before the browser starts. If something holds it already, the browser gets a free port instead, and the Window writes
  a warning on its standard error (event 3043). Its opened line names the port it really uses.
- **No endpoint:** if the endpoint cannot be confirmed, the Window writes a warning with the reason on its standard
  error (event 3042) and no opened line. Typical reasons are another user's listener on the port, or Chromium listening
  on `[::1]` because 127.0.0.1's port was taken. Do not attach a debugger to that port then.

When the endpoint opens:

| Start | Port | Where to find it |
| --- | --- | --- |
| `dotnet runic dev` in a terminal | None | `[dev] Debugging:` says how to ask for one |
| `dotnet runic dev --debug-endpoint` (for an IDE to pass when it starts `dev`) | A free one (`0`) | `[dev] Debugging:` and the session file's `debugEndpoints` (`"engine": "chromium"`), while the browser runs |
| `dotnet runic dev --no-window`, then the IDE starts the Window with the session file's `window.environment` | 9225 | `debugEndpoints`, for the session. If something holds 9225 when `dev` starts, or (on Linux) another user starts listening on it later, `dev` prints a warning and moves the entry to `unavailableDebugEndpoints` |
| The template's `<Project> (CS-WebUI script debugging)` profile | 9225 | The profile, and the Window's warning if it had to use another port |
| `dotnet runic dev -e RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=<port>` | Yours | `[dev] Debugging:` and `debugEndpoints` once the Window confirms it, as above |

A plain `dotnet runic dev --no-window` puts the variable only in the session file's environment, which only an IDE
applies to the Window it starts. With F5 on a profile that does not set it, no port opens.

`--no-debug-endpoint` wins over `--debug-endpoint` and sets no variable. `--browser` sets none either: its page runs in
the IDE's own browser ([above](#the-page-in-chrome-dotnet-runic-dev---browser)). A `-e` variable is passed on in any
case: it is your explicit choice, as for WebView2's. 9225 follows the ports `dev` uses already:
- 9222: the CDP adapter on Linux, and WebView2's in the template;
- 9223: WebKit's inspector under `--no-window`;
- 9224: `--browser`.

**Visual Studio.** With Runic DevKit, **Debug the Window** starts the Window with port 9225 and attaches to it
([Visual Studio](#visual-studio)), and **Attach to the Page (JavaScript)** attaches to a Window that already runs with
the port. Without the extension, start the Window with the `(CS-WebUI script debugging)` profile (F5 or Ctrl+F5) or
with `dotnet runic dev --debug-endpoint`, then use Debug > Attach to Process:
1. Choose the connection type "JavaScript and TypeScript (Chrome DevTools/V8 Inspector)". Older Visual Studio versions
   call it "Chrome devtools protocol websocket (no authentication)".
2. Type the endpoint as the connection target (`http://127.0.0.1:9225/`, or the port `dev` printed) and press Enter.
3. Attach to the page's entry with the "JavaScript and TypeScript" code type.

The .NET debugger of an F5 session stays attached beside it. Visual Studio needs the JavaScript diagnostics component,
which the template's `.vsconfig` names. Runic DevKit makes the same attach through Visual Studio's debugger API; the
dialog steps are not yet verified by hand.

**VS Code.** Attach js-debug to the port. Give the address as `127.0.0.1`: js-debug's default, `localhost`, can resolve
to `[::1]`, where another user can listen beside the browser.

```jsonc
{
  "name": "Attach to the CS-WebUI page",
  "type": "chrome",                  // or "msedge" for Edge
  "request": "attach",
  "address": "127.0.0.1",
  "port": 9225,                      // or the port that [dev] Debugging: names
  "webRoot": "${workspaceFolder}/Frontend"
}
```

For a port that stays the same across restarts, run `dotnet runic dev -e RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=9225`. An IDE
extension can read the port from `debugEndpoints` instead.

**Turn it off** in any of these ways:
- leave out `--debug-endpoint` and `--no-window`, or pass `--no-debug-endpoint`;
- leave the variable unset outside `dev`;
- set `DevTools = false` in the window's `WebContent` policy;
- run the Window outside Development.

Some things stay true while it is on:
- **The gate is the environment, not the build.** A Release build started with `DOTNET_ENVIRONMENT=Development` and the
  variable opens the port too, as it gets the developer tools.
- **The variable is inherited** by the processes the application starts.
- **The browser can outlive the Window.** If the Window process dies without closing its window (a crash, or a kill
  outside `dev`), its browser keeps the endpoint open until you close that browser window. `dev` stops listing it when
  the Window exits.
- **A shared profile.** Windows or applications that share one `ProfilePath` join the browser that holds the port.

What the endpoint exposes is in the [security guide](security.md#development-server).

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

Under `dev` the dock loads from the development server's origin, not the Window's. The Window answers the dock's
`/__devtools/embedded.js` with a module that imports the script from the server, so the page runs it once, and
redirects the dock's other `/__devtools/` and `/__devtools-assets/` requests to the server, so the dock's connection and
icons come from there
([security guide, "Development server"](security.md#development-server)).

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

## React DevTools and Vue DevTools

The development builds of the framework bindings show Runic's state in the framework's own DevTools (since 0.21):

- **React DevTools.** The hooks label their values in a component's **hooks** pane: `useView` and `useSuspenseView`
  show the View (`EditorViewModel · editor · rev 12`, or `connecting`, `failed: …`), `useField` and `useTextField` the
  field's state (`title = "Draft" · editing · 1 error`), `useCommand` its status (`idle`, `running`, `failed: …`),
  `useOperation` and `useLatestOperation` the operation's (`starting`, `running 40%`, `cancelling`, `succeeded`), and
  `useBridgeConnection` the connection. React computes a label only while React DevTools shows the component.
- **Vue DevTools.** A **Runic Views** inspector lists the page's Views by view model and route, with each View's
  revision, presentations, state and its last 20 calls (outcome, duration, C# handler, error). The timeline has a
  **Runic Bridge calls** layer with the start and end of each call. It needs `@vue/devtools-api` 8, an optional peer
  dependency of `@runic-artifex/vue`: install it in the application (`npm install -D @vue/devtools-api`). Without
  it, or without Vue DevTools on the page (the browser extension, or `vite-plugin-vue-devtools`), nothing loads.

They work wherever the framework's DevTools reach the page: an installed browser with the DevTools extension, and
`vite-plugin-vue-devtools`, which runs inside the page, in every engine. A WebView2 window in Development loads
unpacked extensions such as React Developer Tools and Vue.js devtools from `DesktopHostOptions.DevToolsExtensions` or
`RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS` (see [WebView2 DevTools extensions](#the-browsers-developer-tools-per-engine)); how
much of a panel works depends on WebView2's extension support. WKWebView and WebKitGTK load no browser extensions;
there, use the framework's standalone DevTools as its documentation describes. See
[React DevTools labels](../packages/web/react/README.md#react-devtools) and
[Vue DevTools](../packages/web/vue/README.md#vue-devtools).

## Custom object formatters

In development, `@runic-artifex/views` registers custom object formatters (since 0.21), so the console and the Sources
panel's scope and watch views show Runic objects readably instead of closures and proxies:

| Object | Header | Expanded |
| --- | --- | --- |
| A View client (`await connectEditor()`, `view.client`) | `EditorViewModel · editor · rev 12` | view model, route, contract, revision, presentations, released, the state, and the raw client |
| Its state (`client.snapshot`, a hook's or composable's state) | `EditorViewModel state · rev 12 {title: "Draft", …}` | each member |
| A field controller's state (`controller.current`) | `title = "Draft" · editing · 1 error` | value, text, editing, error, errors, conflict, validating |

The browser shows them only after you turn custom formatters on, once per browser profile:

- **Chromium** (an installed Chrome or Edge, and WebView2's DevTools): open DevTools' **Settings** (F1, or the gear
  icon), and under **Preferences** › **Console** check **Enable custom formatters**. Reopen DevTools.
- **Firefox**: open DevTools' **Settings** (F1) and, under **Advanced settings**, check **Enable custom formatters**
  (or set `devtools.custom-formatters.enabled` to `true` in `about:config`). Reopen DevTools.

WebKit's Web Inspector (WKWebView, WebKitGTK, Safari) has no custom formatters and shows the objects as before. The
production build of views contains no formatter and does not touch `window.devtoolsFormatters`. See
[Custom object formatters](../packages/web/views/README.md#custom-object-formatters).

## The Runic DevTools extension

Since 0.21, `dotnet-runic` ships an unpacked Chrome DevTools extension (Manifest V3) that shows Runic Inspect's data
inside the browser's own developer tools. It has no Chrome Web Store listing; you load it from the tool's folder.

- The **Runic** panel (a tab next to Elements and Console) lists the page's **Views**: each route with its C#
  ViewModel, revision and state, and its **recent Bridge calls** (the last 50): kind, outcome, time, revision, the C#
  member from the development source map, and the C# handler's name and duration when a .NET host in Development
  reports them. It refreshes every second while it is visible; **Pause** stops that. A C# location opens in the
  editor through the development server, as Runic Inspect's links do.
- The **Runic** pane of the Elements panel (next to Styles) describes the **selected element** (`$0`) as Runic Inspect
  does: its component, ViewModels, bound members with their values, the commands it calls with their C# handlers,
  its recent calls, texts and messages, and what it cannot tell.

**Where it is.** `dotnet runic dev` prints `[dev] DevTools extension: <folder>` when the page has a development
server, and the [session file](../tools/dotnet-runic/README.md#session-file) has the same folder as
`devtoolsExtension`. It is the `devtools-extension` folder beside the tool, for example
`~/.nuget/packages/dotnet-runic/0.21.0/tools/net10.0/any/devtools-extension` for the template's local tool. The folder
changes with the tool's version.

**Load it in Chrome or Edge:**

1. Open `chrome://extensions` (Edge: `edge://extensions`).
2. Turn on **Developer mode**.
3. Choose **Load unpacked** and select the folder that `dev` printed.
4. Open the page's developer tools (F12, or Inspect) and choose the **Runic** panel, or the **Runic** pane in
   Elements. Reopen developer tools that were open before you loaded it.

Its id is always `cddbfcinbkaindmfgcfdgapddkjeadgh` (the manifest's public `key` fixes it, whatever folder you
load), so its pages are `chrome-extension://cddbfcinbkaindmfgcfdgapddkjeadgh/…` and an allowlist can name it. The
session file has it as `devtoolsExtensionId`. No private key exists: the extension is not signed or published.
Its version is the tool's: `0.21.0` for 0.21.0, and for a prerelease `X.Y.Z-label.N` the manifest version is
`X.Y.Z.N` (Chrome allows only numbers) while `chrome://extensions` shows the full version (`version_name`).

After a tool update, load the new folder and remove the old entry. Chromium and Chrome for Testing (not Google
Chrome, whose branded builds ignore the switch since Chrome 137) and automation can also start with
`--load-extension=<folder> --disable-extensions-except=<folder>`.

**In the Window on Windows.** `dotnet runic dev` loads the extension into the Window's WebView2 by default: in
Development it appends the folder to the Window's `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS`, after any folders you listed
there with `-e` or in your environment, and only when the folder is not listed already. Open the Window's DevTools
(F12, or **Open DevTools** in its system menu) and choose the **Runic** panel. `dotnet runic dev --no-devtools-extension`
leaves it out; with `--no-window` the session file's `window.environment` carries the variable for your IDE's launch.
`dev` never sets it outside Development, and the host ignores it there. If WebView2 rejects the folder, the Window
reports `devtools-extension-failed` and opens without it. WKWebView and WebKitGTK have no extension support: use
Runic Inspect and the dock there.

**In the IDE's Chrome (`--browser`).** `dotnet runic dev --browser` prints the folder and how to load it. The
template's `Window in Chrome` configuration starts branded Chrome, which ignores `--load-extension`, and VS Code
cannot read the session file's `devtoolsExtension` into `launch.json`, so the template passes nothing. Load it once
with **Load unpacked** in the Chrome window that the launch opens; VS Code's JavaScript debugger keeps its own browser
profile between launches by default (`userDataDir`), so it stays loaded until the tool's folder changes. To start
Chromium or Chrome for Testing instead, set `runtimeExecutable` and add the folder that `dev` printed:

```jsonc
// .vscode/launch.json, the "Window in Chrome" configuration
"runtimeExecutable": "/path/to/chromium",
"runtimeArgs": ["--load-extension=/path/printed/by/dev/devtools-extension"]
```

The path names the tool's version, so update it after a tool update.

**What it reads.** The extension has no permissions besides DevTools. It reads the page through
`chrome.devtools.inspectedWindow.eval` of Runic Inspect's API (`snapshot()` and `describe($0)` on
`globalThis[Symbol.for("runic.vite.inspect")]`), so it shows data only where Runic Inspect runs: pages under
`vite dev` or `dotnet runic dev` with vite-plugin-runic 0.21 or later, and `runic({ inspect })` not `false`. On a
production build its panes say so. It sends nothing anywhere; "open in editor" goes to the development server, with
Runic Inspect's rules.

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
- Custom object formatters exist only in the development build of views.
- `@runic-artifex/react` and `@runic-artifex/vue` have the same two builds (since 0.21): only the development build
  labels the hooks for React DevTools or adds the Vue DevTools inspector, and only it imports `@vue/devtools-api`.
- The source map stays in `obj`: it is not in generated TypeScript, build or publish output, or a package.
- Outside Development, a Runic Desktop window has no developer tools in the window, no Open DevTools item and no
  browser extensions (`DevToolsExtensions` is ignored), replies carry no handler timing,
  and failures carry no exception detail. A remote debugging endpoint that the process's environment asks for is the
  exception: see [the browser's developer tools per engine](#the-browsers-developer-tools-per-engine). Do not ship
  with `DOTNET_ENVIRONMENT=Development`; see the
  [security guide](security.md#release-builds).

## Troubleshooting

- **No DevTools Protocol endpoint on Windows.** `dev` or your IDE runs elevated, and `dev` printed a
  `[dev] Debugging:` hint; the session file lists the endpoint under `unavailableDebugEndpoints`. Run both without
  elevation. Without that hint, check that `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` reaches the Window: set it in the
  launch profile, with `dotnet runic dev -e`, or in the environment `dev` runs in.
- **Windows Defender Firewall asks about `node.exe` or `bun.exe` on the first `dev`.** Vite 8 checks that the
  port is free by listening on it briefly on all interfaces (`0.0.0.0` and `::`) before it binds the `127.0.0.1`
  that `dev` passes. Nothing is served on that short listen. Cancel or deny the prompt: the development server stays
  on loopback and works as before, and Windows asks once per program.
- **F12 does nothing.** The Window does not run in Development: start it with `dotnet runic dev` or a launch profile
  that sets `DOTNET_ENVIRONMENT=Development`. On macOS, use Safari's Develop menu instead, and on Linux Inspect
  Element from the page's context menu.
- **The breakpoint never binds, or the debugger detaches.** `dotnet watch` restarted the Window after a C# edit. Use
  `dotnet runic dev --no-window` and F5. On Linux, VS Code's JavaScript debugger stays attached across the restart.
- **No JavaScript debugger endpoint on Linux.** `dev` printed no `[dev] Debugging: JavaScript debuggers attach …`
  line: it ran with `--no-debug-endpoint` or `--webkit-inspector`, `WEBKIT_INSPECTOR_HTTP_SERVER` is set, or the
  application runs on CS-WebUI only. When the attach fails although the line is there, close GNOME Web's inspector
  (WebKit serves one client at a time), and check the line's port: another program had 9222, and `dev` took another.
  `dev` names that program when it is yours, often another `dev` session, and warns when the project's
  `.vscode/launch.json` attaches to 9222, which would reach that program's page: set the configuration's `port` to the
  one `dev` names (the session file's `debugEndpoints` lists it), or stop the other program and restart `dev`.
  Under `--no-window`, `dev` stops with `RAPPDEV1017` instead, since the compound's attach configuration names 9222.
- **The JavaScript debugger attaches on Linux but nothing loads.** `dev` printed `[dev] Debugging: the Window's page
  did not answer …`: WebKit's inspector did not answer the debugger's first command within 5 seconds, which happened
  right after WebKit's Web Inspector opened in the Window. `dev` sends the command once more; when that is not answered
  either, close the Web Inspector (Inspect Element) and any `inspector://` page, then attach again. To report it, run
  `dev` with `RUNIC_DEV_CDP_TRACE=<file>`. Script sources and inline source maps keep only their first 200 characters
  there; `RUNIC_DEV_CDP_TRACE_SOURCES=1` keeps them whole, which reached 723 MB in five minutes on a page with about
  4,000 modules.
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
