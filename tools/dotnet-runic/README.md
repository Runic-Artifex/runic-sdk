# dotnet-runic

`dotnet-runic` checks and coordinates a Runic Views Window project. Generated
projects pin the tool locally, so the first run of a new project is:

```bash
dotnet tool restore
dotnet runic dev
```

All commands find the single `.csproj` in the current directory; pass
`--project path/to/App.csproj` (`-p`) otherwise. `dev`, `doctor`, `size` and
`package` also start from a solution root: in a directory without one `.csproj`
but with one solution (`.sln` or `.slnx`), or with `--project` naming a solution
or its directory, they evaluate the solution's projects and take its one Window
project (`RunicApplicationFrontendWindowProject=true`), and `dev`, `size` and
`package` print `Solution: …/Acme.slnx, whose one Window project is
Acme.App/Acme.App.csproj`. With several Window projects they list them and stop
(`RAPPDEV1002`); choose one with `--project`. At a solution root, `dotnet runic`
needs a tool manifest there: the template writes `.config/dotnet-tools.json`
into the project folder, and `dotnet` looks for one in the current folder and
its parents only. Move it to the solution root, or run
`dotnet new tool-manifest` and `dotnet tool install dotnet-runic`
there, so `dotnet runic` works from the root and from each project folder.
`--configuration` (`-c`)
selects the build configuration. `dotnet runic <command> --help` describes
every option.

`dev` requires `RunicApplicationFrontendWindowProject=true` and a CS-WebUI
(`Runic.Application.Views.CsWebUi`) or Runic Desktop (`Runic.Application.Views.Desktop`)
host. It restores the .NET and JavaScript dependencies, builds the project, and
runs the native Window alongside the frontend's Vite or Angular development
server. While the development server runs, the build skips the production
frontend build of the project it serves and leaves the development document in
`www/`: `dev` passes `RunicApplicationFrontendDevSessionProject` with the
project's path, and only that project skips its frontend build and copy. The
Views MSBuild targets still generate the typed TypeScript clients, and the
projects it references, such as a client library, build their frontend packages,
so a page that imports a compiled library package works from a fresh clone
(since 0.24). While it runs, `dev` rebuilds those packages when they change, and
keeps the page's last good build when one fails
([Library packages under dev](#library-packages-under-dev)). `dotnet watch`
restarts the Window after C# edits. `--no-restore`, `--no-frontend-watch`,
`--no-dotnet-watch`, and `--dry-run` select parts of that loop; `--no-window`
runs only the frontend, for a Window that your IDE starts under its debugger
([Debug the Window in your IDE](#debug-the-window-in-your-ide)). The build
installs the frontend packages, at the JavaScript workspace root for a workspace
member, only when the lock file, a `package.json` or the package manager changed,
so a second `dev` start runs no install
([Fast iteration](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#fast-iteration)).
`dev` also passes `RunicApplicationFrontendDelivery=Files`, so a project that
embeds its frontend serves it live while developing. With `--no-restore` nothing
installs them (`RunicApplicationFrontendInstallMode=Never`), and with
`--no-build` no build runs to install them; then a frontend that declares
dependencies but has no `node_modules`, in its folder or at its workspace root,
stops with `RAPPDEV1008`. With Views targets older than the tool, `dev` warns
with `RAPPDEV1019` and installs the packages itself, as before 0.24. When `dotnet watch` restarts or `dev`
stops the Window, the application's expected exit code after SIGTERM or Ctrl+C
(143, 130) is not reported; any other exit code still is. Application
arguments after `--` are passed to the Window process. When a restore, install
or build step fails, the error
names the program and its working directory and then points to `doctor`.
With Vite, `dev` prepares a development document for every HTML page that
`vite build` emits, so a second window with its own page (for example
`settings.html` listed in `build.rolldownOptions.input` and opened with
`RunicWindowOptions { StartPage = "settings.html" }`, as
[A Window with its own page](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#a-window-with-its-own-page)
shows) loads in `dev` exactly
as in build and publish. Before the build, `dev` reads the build inputs from
the resolved Vite configuration with Node.js, or with Bun for a Bun frontend,
and prints them as `[dev] Pages: index.html, settings.html`. An input whose
file does not exist stops `dev` with `RAPPDEV1009: Window entry not found:
'settings.html' …` instead of a window that times out waiting for its page.
The Window runs with `DOTNET_ENVIRONMENT=Development` unless you set
`DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` yourself, so failed Bridge
calls carry the exception type, message and stack to the browser.
On macOS the Window runs from its ad-hoc signed development bundle in `obj/`,
which `dotnet run` starts, so notifications, login startup and open
registration work in `dev` too
([development bundle](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Platform.MacOS/README.md#development-bundle);
`RunicMacDevelopmentBundle=false` turns it off).
Ctrl+C or SIGTERM stops `dev` the way closing the last window stops the
application: the Window gets SIGTERM on Linux and macOS, or Ctrl+C on Windows,
and up to 5 seconds to close its windows and finish its work before `dev` kills
it. A restart after a C# or frontend compiler edit gets the same grace;
`dev` sets `DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS=5000` for `dotnet watch`
unless you set it yourself. On Linux and macOS, `dev` also stops when the
`dotnet` command that started it exits (0.18): the .NET 11 SDK's `dotnet runic`
dies on SIGTERM without passing it to the tool, so an IDE or script that sends
SIGTERM to `dotnet runic dev` would otherwise leave the development server,
`dotnet watch` and the application running.

`dev` picks a free loopback port for the development server, and on a shared
machine another process could take that port first. Since 0.11, `dev` passes
the development server process a random value for the session and accepts the
server as ready only when its `/__runic/state` answers with that value, so
`dev` never writes a page that another process served into the web root.
`@runic-artifex/vite-plugin-runic` answers it, and every frontend that
`dotnet new runic-app` generates is set up for it:

| Frontend | What answers `/__runic/state` | When `dev` requires the answer |
| --- | --- | --- |
| React, Vue, Svelte | `runic()` in `vite.config.ts` | The resolved Vite configuration has `runic()` |
| SvelteKit | `runic({ html: false })` in `vite.config.ts`, next to `sveltekit()` (without `html: false`, SvelteKit warns about `runic()`'s `transformIndexHtml` hook) | The same |
| Angular | `runicDevSessionProxy()` in `proxy.conf.mjs`, the serve target's `proxyConfig` in `angular.json` (the Angular CLI loads no Vite plugins) | `package.json` declares `@runic-artifex/vite-plugin-runic` |
| Angular with Vite (`--angular-build vite`) | `runic()` in `vite.config.ts`, next to `angular()` from `@analogjs/vite-plugin-angular`; the project has no `angular.json`, so `dev` starts Vite | The resolved Vite configuration has `runic()` |

A Vite project with the external frontend compiler, where `dev` does not
resolve the Vite configuration, is also checked when `package.json` declares
the plugin. The log says
`[dev] The Vite development server confirmed this dev session.` (or `Angular`).
A project set up without the plugin, for example one created before 0.11 or an
Angular project without `runicDevSessionProxy()` and without the plugin
dependency, is not checked: `dev` then relies on the server exiting when its
port is taken. In every case, `dev` writes the development documents only while
its own server process is still running. The plugin and the tool must come from
the same release; an older plugin does not answer, and readiness times out with
`RAPPDEV1007`.

Readiness also requires the entry module to come back as JavaScript. Vite answers
a path that is not below its root with its `index.html` fallback; `dev` then
stops with `RAPPDEV1007`, naming the entry and Vite's root, instead of giving the
window an HTML page as its entry module.

A Vite or Angular development server also gets `RUNIC_APPLICATION_DEV_SOURCE_MAP`
(0.20): the path of the development source map that the Bridge generator writes
to the project's intermediate directory (`obj/<Configuration>/<TargetFramework>/runic-bridge/sources.json`),
which `runic()` and, under the Angular CLI, `runicDevSessionProxy()` serve at
`/__runic/sources` and open in the editor through `/__runic/open` (see "Source
map and open in editor" in the `@runic-artifex/vite-plugin-runic` README).

Without a Vite or Angular development server, `dev` runs the frontend watcher
instead: the `RunicApplicationFrontendDevWatchTarget` MSBuild target, or the
frontend's `dev` script (for example `vite build --watch`), which writes the
frontend output directory (`RunicApplicationFrontendOutputDirectory`, by
default `Frontend/dist`). With `--no-frontend-watch` the build writes it once.
A Runic Desktop window (`Runic.Application.Views.Desktop`) then serves that
directory live, before the `www/` copy of the initial build, and reloads after
each change, so frontend edits reach the window without a rebuild:

```text
[dev] Assets: /path/to/App/Frontend/dist (served live; windows reload on change)
```

A window reloads the last HTML page it loaded, such as `settings.html`, or
else its start page; the page's query, hash and `history.pushState` route are
not kept. The Bridge reconnects as after any reload. A page that was still
loading or reconnecting when a change arrived reloads once its Bridge
connects. A page that has no Bridge, such as a half-written `index.html` or a
build error page, is reloaded by the next change too: the host navigates the
window's WebView, which runs no page script, so a strict
Content-Security-Policy does not block it. A window shown in an installed
browser instead of a WebView must be reloaded by hand then. On Linux and macOS the
window keeps serving the directory it opened, so a frontend build that deletes
and recreates the output directory itself, rather than its contents as
`vite build --watch` does, needs `dev` to be restarted. A CS-WebUI window keeps
the initial build, and `dev` prints
`[dev] Assets: the CS-WebUI host shows the initial build; restart to see frontend changes`.
An application that sets `RunicDesktopHostOptions.SurfaceOptions` serves its
own content in `dev` too.

Since 0.11, `dev` takes these options of `dotnet run` and `dotnet build`:

| Option | Effect |
| --- | --- |
| `--launch-profile <name>` | Runs the Window with that profile from `Properties/launchSettings.json`, so its `environmentVariables` and `commandLineArgs` apply, under `dotnet watch` too. Since 0.24 `dev` uses the first `Project` profile without it, as `dotnet run` does, and prints `[dev] Launch profile: <name> (the first Project profile; …)`. A profile's variables replace `dev`'s defaults; a `-e` value of a name the profile sets is passed to `dotnet run -e`, so it wins. |
| `--no-launch-profile` | Runs the Window without a launch profile, as `dev` did before 0.24. Cannot be combined with `--launch-profile` or `--no-window`. Since 0.24. |
| `--no-build` | Skips `dev`'s own .NET restore and build, as `dotnet run --no-build` does, and starts from the existing build output; build the project with the same `--configuration` first. The frontend packages are still installed unless you pass `--no-restore`. `dotnet watch` still builds after C# edits. |
| `-e`, `--environment NAME=VALUE` | Sets an environment variable for the Window process, as `dotnet run -e` does; repeat it for several. It wins over the defaults in the table below, so `-e DOTNET_ENVIRONMENT=Staging` runs the Window in `Staging`. The variables `dev` uses to describe the development server (`RUNIC_APPLICATION_*` below) are refused with `RAPPDEV1012`, and a value without `=` with `RAPPDEV1011`. `--dry-run` prints the names, never the values. |
| `-v`, `--verbosity <level>` | Passes the MSBuild verbosity (`q[uiet]`, `m[inimal]`, `n[ormal]`, `d[etailed]` or `diag[nostic]`) to the restore, the build and `dotnet watch`. |

Since 0.21, on Linux (see [Debug the page's JavaScript on Linux](#debug-the-pages-javascript-on-linux)):

| Option | Effect |
| --- | --- |
| `--no-debug-endpoint` | Opens neither WebKit's remote inspector for the Window nor Runic's CDP adapter on port 9222. Since 0.22, on every operating system, it also keeps a CS-WebUI Window's browser from opening its DevTools Protocol endpoint, and wins over `--debug-endpoint` ([CS-WebUI pages](#debug-a-cs-webui-pages-javascript)). |
| `--webkit-inspector` | Opens WebKit's own Web Inspector over HTTP for a browser instead of the CDP adapter, on WebKitGTK 2.54.3 or later; on an older WebKitGTK, `dev` opens the `inspector://` server and the adapter as usual and says why. |

Since 0.21, on every operating system (see [Debug the page in Chrome](#debug-the-page-in-chrome---browser)):

| Option | Effect |
| --- | --- |
| `--browser` | Serves the Window to a browser instead of opening it (serve-only mode), at `http://<label>.localhost:9224/`, an origin of its own for each run, for the one browser that its one-time code admits (`obj/runic/dev-browser.html` opens it), so the JavaScript debugger of your IDE's Chrome or Edge can open its pages. Cannot be combined with `--no-window` or `--webkit-inspector`. |

Since 0.22, for CS-WebUI (see [Debug a CS-WebUI page's JavaScript](#debug-a-cs-webui-pages-javascript)):

| Option | Effect |
| --- | --- |
| `--debug-endpoint` | Opens the DevTools Protocol endpoint of the Window's Chrome or Edge on a free loopback port, lists it in `debugEndpoints` and names it in `[dev] Debugging:`. Off by default, for an IDE to pass when it starts `dev`. `--no-debug-endpoint` wins. |

Since 0.21, on Windows (see [The Runic DevTools extension](#the-runic-devtools-extension)):

| Option | Effect |
| --- | --- |
| `--no-devtools-extension` | Does not add the Runic DevTools extension to the Window's `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS`, so its WebView2 does not load it. |

`dev` passes these variables to the Window process:

| Variable | Value |
| --- | --- |
| `DOTNET_ENVIRONMENT` | `Development`, unless you set it or `ASPNETCORE_ENVIRONMENT`, in the environment or with `-e` |
| `DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS` | `5000`, unless you set it |
| `DOTNET_WATCH_RESTART_ON_RUDE_EDIT` | `1` |
| `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` | Without a development server, the absolute frontend output directory; cleared while one runs. An internal contract with the Runic Desktop host, which reads it only in a `Development` environment. |
| `RUNIC_APPLICATION_VITE_*`, `RUNIC_APPLICATION_FRONTEND_*`, `RUNIC_APPLICATION_DEV_PROJECT` | The development server's address, entry and compiler integration while one runs; cleared otherwise. Internal. |
| `RUNIC_APPLICATION_SERVE_ONLY` | `1` with `--browser`, over a value from `-e` or the environment; otherwise as you set it. |
| `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS` | On Windows, in a `Development` environment, with a development server and without `--browser` or `--no-devtools-extension`: your value from `-e` or the environment, with the [Runic DevTools extension](#the-runic-devtools-extension)'s folder appended after a `;` unless it is listed already. Otherwise as you set it. |

`doctor` checks the Views Window opt-in, the .NET SDK, the declared JavaScript
runtime and package manager, the matching lock file, the configured
development-server inputs, and, once the project is restored, that the
project's release-train packages belong to one release train. An unrestored project gets a warning that
tells you to run `dotnet runic dev` or `dotnet restore`. The browser check
follows the referenced host: a Runic Desktop project needs no browser, and a
missing browser is only a warning for a CS-WebUI project because CS-WebUI falls
back to the platform WebView. Only browser smoke checks require Chromium.
The .NET SDK passes at a certified version: SDK 10.0.404, or SDK 11.0.100,
which builds `net10.0` applications too (0.18). It warns for any other SDK from
10.0.404 on, such as a newer patch or an uncertified newer major version, and
fails for an older SDK or one that cannot build the target framework. A newer
major SDK does not bring the runtime that a `net10.0` application runs on, so
`dotnet-runtime` checks with `dotnet --list-runtimes` that the .NET 10 runtime
(`Microsoft.NETCore.App` 10.0, and `Microsoft.AspNetCore.App` 10.0 for Runic
Desktop on Kestrel) is installed, unless the project is self-contained or rolls
forward to a newer major version (`RollForward` or `DOTNET_ROLL_FORWARD` set to
`Major` or `LatestMajor`). Without it, the application builds but exits with
code 150 when it starts; `dotnet runic dev` stops before it starts the
application instead ([`RAPPDEV1013`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#rappdev1013)).
The JavaScript runtime passes at the certified version and warns for a newer
release of the same major version. Node 26 is supported as well, with a
warning, since the starters and the Runic npm packages that run in Node declare
`engines.node` `^24.18.0 || ^26.0.0` (0.11).
npm passes at the certified npm 12.2.0, and warns for a later npm 12 and,
since 0.22, for npm 11.12.1 or a later npm 11: no Node release ships npm 12
yet, and Node 24 and 26 ship npm 11 (Node 26.0.0 npm 11.12.1, Node 24.18.0
npm 11.16.0). The starters declare the certified npm in `packageManager`; to
use it, run `npm install --global npm@12.2.0`.

Doctor checks every Views host adapter the project references, not only one.
It reads the project's direct package and project references, with the
project's `TargetFramework`; like `dev`, it does not support a multi-targeted
project (`RAPPDEV1005`). The `views-hosts` check names each host it found and
the checks that cover it. It warns when the project references none, and says
that a host is referenced indirectly when only the build flag that every
adapter's build props set once the project is restored shows an adapter. A project that references both
`Runic.Application.Views.CsWebUi` and `Runic.Application.Views.Desktop` gets the
CS-WebUI browser check and the Runic Desktop checks. `desktop-runtime` checks
that this machine can open Runic Desktop's embedded window: `libgtk-3.so.0` and
`libwebkit2gtk-4.1.so.0` on Linux, the same libraries Runic Desktop probes, the
WebView2 Runtime on Windows, and WKWebView on macOS. It warns when they are
missing, because `RunicDesktopHost.Run` then falls back to an installed browser.
A project on the GTK 4 profile gets `gtk4-profile` instead. A project that
references `Runic.Application.Views.Wpf` gets `wpf-runtime`, which warns on a
machine other than Windows or without the WebView2 Runtime.

With `--runtime`, the target checks follow the project's primary host: CS-WebUI when
the project references it, then Runic Desktop, then WPF. `target-presentation`
reports what that host needs. For the other hosts, doctor still runs
`desktop-runtime`, `gtk4-profile`'s library check and `wpf-runtime` on this
machine when the target has this machine's operating system, and `wpf-runtime`
reports a target other than Windows from any machine. When no check can cover a
host, for example Runic Desktop with `--runtime linux-x64` on Windows,
`views-hosts` warns and names the machine to run doctor on.

These host checks warn; they never fail. `--fail-on fail`, the default, keeps
the exit code and JSON envelope success of earlier releases. With `--fail-on warn`, a
machine without a referenced host's runtime, such as a headless CI image without
WebKitGTK, or a project without a Runic Views host, now fails. To keep the
earlier result there, use `--fail-on fail`, or install the runtime the warning
names.

A project that references `Runic.Desktop.Gtk4` also gets a `gtk4-profile` check.
It warns once and lists every missing piece: `Runic.Application.Views.Desktop`,
directly or restored transitively, whose `services.AddRunicPlatformServices()`
creates the GTK 4 window services (the platform packages keep them internal); and,
on a Linux machine, `libgtk-4.so.1`, `libwebkitgtk-6.0.so.4` (or `.so.0`) and a GTK
older than 4.12. The remediation names the distribution's packages for
Debian/Ubuntu, Fedora, Arch, openSUSE and NixOS, read from `/etc/os-release`, and
the library file names on other distributions. With `--runtime`,
`target-presentation` reports the native libraries instead.

A project on `Runic.Application.Views.Desktop` or `Runic.Application.Views.CsWebUi` also
gets a `platform-services` check. With both hosts it checks Runic Desktop's
providers. It names the provider that
`AddRunicPlatformServices()` selects on this machine. On Linux, it warns when
there is no D-Bus session bus or no installed `xdg-desktop-portal` (the
`org.freedesktop.portal.Desktop` D-Bus service in `XDG_DATA_HOME` or
`XDG_DATA_DIRS`), because the file dialogs and the file launcher use the portal.
For CS-WebUI it reports that the services return `OwnerUnavailable`. The check
describes this machine, so `--runtime` skips it.

Since 0.11, two more checks warn without failing:

- `views-package-versions` compares the installed `@runic-artifex/views`, found
  in `node_modules` as Node.js resolves it, with the restored
  `Runic.Application.Views`. A frontend whose packages were not reinstalled
  after a .NET package update gets a warning that names both versions. The
  check is omitted until the project is restored and the frontend packages are
  installed.
- `application-id` checks the project's `ApplicationId`: a reverse-DNS name of
  ASCII letters and digits in at least two dot-separated parts, each starting
  with a letter, at most 50 characters (`org.example.Notes`), which a D-Bus and
  Flatpak name, a macOS bundle identifier and an MSIX package name all accept.
  The template's packaging script applies the same rule. Another value warns;
  a project without `ApplicationId` passes.

`dotnet runic doctor --bundle <dir>` (since 0.24) also checks a web bundle that
a publish with `package --frontend-bundle` would use, as its
[`web-bundle`](#web-bundle) check. A build that takes its pages from a web
bundle runs no install, frontend build or client emission, so it needs neither
Node.js nor Bun. With `--bundle`, or when the project builds with
`RunicApplicationFrontendPrebuiltDirectory` (from the project or the
environment), doctor therefore reports `javascript-runtime`, `package-manager`,
`lock-file`, `frontend-workspace`, `package-manager-agreement`,
`frontend-dependencies`, `client-modules` and `views-dedupe` as not needed
(`SKIP` with the reason, status `skip` in JSON) and checks the bundle. Skipped
checks never reach a `--fail-on` threshold, so on a target-OS publish runner
without Node.js the exit code follows the other checks and `web-bundle`
(#1421). Without either, doctor runs those checks as before.

### Deployment checks for a target

`dotnet runic doctor --runtime <rid>` (also `--rid` or `-r`) adds checks for
publishing to a runtime identifier. Doctor evaluates the project with the
global properties of `dotnet publish -r <rid>` (`RuntimeIdentifier=<rid>` and
`_IsPublishing=true`), and in Release unless you pass `-c`, so RID-, publish- and
configuration-conditioned `PublishAot`, `PublishSelfContained` and
`SelfContained` settings apply. Doctor does not see properties passed to
`dotnet publish` on its command line; `--aot`, `--no-aot`,
`--self-contained` and `--no-self-contained` check a publish with
`-p:PublishAot=true`, `-p:PublishAot=false`, `--self-contained true` or
`--self-contained false`. `--aot` and `--no-aot` conflict, and so do
`--self-contained` and `--no-self-contained`. A malformed RID, or one of these
switches without `--runtime`, is a usage error (`RAPPCLI1011`, exit code 2).

| Check | What it reports |
| --- | --- |
| `target-rid` | Reads the support matrix embedded in the compatibility set, which is generated from `eng/support.json` (see [Supported platforms](https://github.com/Runic-Artifex/runic-sdk/blob/main/README.md#supported-platforms)). Passes for CI-verified RIDs of the project's host (`linux-x64`, `win-x64` and `osx-arm64`), where Runic CI builds and runs the host's native window layer for changes that affect it. Warns, with the reason, for RIDs with packaged native support but no CI coverage. Fails for RIDs that the host does not support, such as `win-arm64` and the musl RIDs with CS-WebUI, whose Linux library needs glibc 2.34; for RIDs the matrix does not list; and for version-specific RIDs such as `win10-x64`. Without a known host, doctor assumes the most permissive host and says so. After a failure doctor skips the other target checks. |
| `target-runtime-identifiers` | Warns when the project declares `RuntimeIdentifiers` without the target, which breaks `dotnet publish --no-restore`. |
| `target-publish` | Without Native AOT, says what target machines need: nothing for a self-contained publish, otherwise the .NET runtime, plus the ASP.NET Core runtime for Runic Desktop on `Runic.Desktop.Kestrel`. With `PublishAot=true`, fails when the target is another operating system, because Native AOT does not cross operating systems. On the same operating system it checks the native toolchain: clang or gcc and, unless `StripSymbols` is `false`, objcopy on Linux, the Xcode command-line tools on macOS, and the Visual Studio C++ tools for the target architecture on Windows (through `vswhere`). A Linux target with another architecture or C library warns that it needs a sysroot. |
| `target-presentation` | Names the native runtime that target machines need. Runic Desktop needs the WebView2 Runtime on Windows, GTK 3 and WebKitGTK 4.1 on Linux (GTK 4.12 or newer and WebKitGTK 6.0 when the project references `Runic.Desktop.Gtk4`), and WKWebView, which macOS includes. CS-WebUI needs an installed browser and falls back to the same platform WebView. When the target has the host's operating system, doctor also inspects this machine and warns when the runtime is missing. It reads the GTK 4 version through `pkg-config` when available. Doctor cannot inspect another operating system, so it reports those requirements as passing. |

Run doctor with `--runtime` on the machine or CI runner that publishes, and once for
each target:

```bash
dotnet runic doctor --runtime linux-x64 --fail-on fail
dotnet runic doctor --runtime win-x64 --aot --fail-on fail
```

### Doctor JSON output

`dotnet runic doctor --format json` (or `RUNIC_COMMANDLINE_OUTPUT=json`) writes
one `runic.commandline/1` envelope, as described in [Output and exit
codes](#output-and-exit-codes). Errors that stop the inspection, such as a
missing project, produce a failed envelope with a `fault` and no payload.

`--fail-on <never|fail|warn>` selects when doctor fails, with the same meaning in
both output modes, and defaults to `fail` in both. When a check reaches the
threshold, doctor exits with code 1. In JSON mode that is a failed envelope: the
fault has code `RAPPCLI1009`, `details` mapping each non-passing check id to
`fail` or `warn`, and `data` with the complete doctor payload
(`fault.data.type` is `runic.application.tool.doctor/1`, `fault.data.payload`
the payload below), and the diagnostics mark the checks at the threshold as
errors. `--fail-on never` reports every completed inspection as a successful
envelope instead, so you can decide on `payload.healthy`:

```bash
dotnet runic doctor --format json > doctor.json
jq '.fault.data.payload.checks[] | select(.status != "pass")' doctor.json
dotnet runic doctor --format json --fail-on never | jq -e '.payload.healthy'
```

The payload type is `runic.application.tool.doctor/1`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["project", "host", "target", "healthy", "summary", "checks"],
  "properties": {
    "project": { "type": "string", "description": "Absolute path of the inspected project file." },
    "host": { "enum": ["cswebui", "desktop", "wpf", "unknown"], "description": "The primary Runic Views host, which the --runtime checks follow: cswebui, then desktop, then wpf. wpf was added in 0.7.0-preview.9 for a project whose only host is WPF." },
    "target": { "type": ["string", "null"], "description": "The --runtime runtime identifier; null without --runtime." },
    "healthy": { "type": "boolean", "description": "False when any check has status fail." },
    "summary": {
      "type": "object",
      "additionalProperties": false,
      "required": ["passed", "warnings", "failed"],
      "properties": {
        "passed": { "type": "integer", "minimum": 0 },
        "warnings": { "type": "integer", "minimum": 0 },
        "failed": { "type": "integer", "minimum": 0 },
        "skipped": { "type": "integer", "minimum": 0, "description": "Checks with status skip. Added in 0.24." }
      }
    },
    "checks": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["id", "status", "message", "remediation", "helpUri"],
        "properties": {
          "id": { "type": "string", "description": "Stable check identifier, for example dotnet-sdk, lock-file or browser." },
          "status": { "enum": ["pass", "warn", "fail", "skip"], "description": "skip (added in 0.24): the check does not apply, such as the JavaScript checks with a prebuilt frontend; the message says why." },
          "message": { "type": "string" },
          "remediation": { "type": ["string", "null"], "description": "How to fix a warn or fail check; null when nothing is needed." },
          "helpUri": { "type": "string", "format": "uri", "description": "The check's section of this README at the release, under Doctor checks; its anchor is the check identifier. Added in 0.20." }
        }
      }
    },
    "hosts": { "type": "array", "items": { "enum": ["cswebui", "desktop", "wpf"] }, "description": "Every referenced Views host adapter, each checked by doctor; empty when there is none. Added in 0.7.0-preview.9." }
  }
}
```

The envelope's `diagnostics` repeat each failing check (code `RCLI8101`, kind
`doctor-check-failed`, message key `doctor.<id>.failed`) and each warning
(code `RCLI8102`, kind `doctor-check-warning`, message key
`doctor.<id>.warning`). A successful envelope cannot hold errors, so its
diagnostics are warnings. The envelope replaces the message and drops the
`arguments` (`[id]`) of a diagnostic whose text contains a path, but it never
redacts the message key, which always names the check. `payload.checks` is the
complete list.
Check identifiers are `dotnet-sdk`, `dotnet-runtime`, `windows-targeting`, `views-window`, `javascript-runtime`,
`package-manager`, `lock-file`, `frontend-workspace`, `package-manager-agreement`,
`frontend-dependencies`, `client-modules`, `views-dedupe`, `web-bundle`, `compatibility-set`, `views-package-versions`,
`launch-arguments`,
`application-id`, `frontend-dev-server`, `vite-config`, `vite-entry`,
`browser`, `views-hosts`, `desktop-runtime`, `wpf-runtime`, `gtk4-profile` and
`platform-services`; `views-package-versions` and `application-id` were added
in 0.11, `dotnet-runtime` in 0.18, and `windows-targeting`, `frontend-workspace`,
`package-manager-agreement`, `frontend-dependencies`, `client-modules`, `views-dedupe`, `web-bundle` and `launch-arguments` in 0.24. New identifiers may appear in later releases. With `--runtime`, the payload also lists
`target-rid`, `target-runtime-identifiers`, `target-publish` and
`target-presentation`.

### Doctor checks

Each check has a section here; `helpUri` in the JSON payload links it at the
release, with the check identifier as the anchor.

#### `windows-targeting`

Only for a project of a Windows target framework, such as a WPF project on
`net10.0-windows`, when doctor runs on Linux or macOS. The .NET SDK builds such
a project there only with `EnableWindowsTargeting=true`; without it the restore
fails with `NETSDK1100`. Fails without the property, with this line for the
application's `Directory.Build.props` (or its project file) as the fix, and
passes with it. Either way the application runs only on Windows. Added in 0.24.

```xml
<EnableWindowsTargeting Condition="'$(EnableWindowsTargeting)' == '' and !$([MSBuild]::IsOSPlatform('Windows'))">true</EnableWindowsTargeting>
```

#### `views-window`

Fails unless the project sets `RunicApplicationFrontendWindowProject=true`, the
Views Window opt-in that `dev` needs too.

#### `dotnet-sdk`

The .NET SDK that builds the project: passes at a certified version, warns for
another one from 10.0.404 on, and fails for an older SDK or one that cannot
build the project's target framework. See the paragraph on the .NET SDK above.

#### `dotnet-runtime`

Fails when `dotnet --list-runtimes` lacks the .NET runtime the application runs
on (and the ASP.NET Core runtime for Runic Desktop on Kestrel), unless the
project is self-contained or rolls forward to a newer major version. Added in
0.18.

#### `javascript-runtime`

Node.js, or Bun for a Bun frontend or workspace: fails when it is missing or
older than the certified version, and warns for a newer release, Node 26
included.

#### `package-manager`

The package manager that installs and builds the frontend (npm, pnpm or Bun),
chosen as the build chooses it: `RunicApplicationFrontendPackageManager`, then
the JavaScript workspace root's `packageManager` field or lock file, then the
frontend's own, then npm. Before 0.24 doctor read only the frontend's folder,
so a member of a Bun workspace was checked for npm. Fails when the manager is
unavailable or outside the supported range, and warns for another supported
version or one other than the version that `packageManager` declares. The supported range
is the certified version or a later release of its major version; for npm also
npm 11.12.1 or a later npm 11, the npm that Node 24 and 26 ship (0.22).

#### `lock-file`

Fails when the frontend has no lock file of its package manager, which
reproducible installs need. In a JavaScript workspace the lock file is the
root's, where the build's install runs.

#### `frontend-workspace`

The JavaScript workspace that installs the frontend: the nearest folder above
it whose `package.json` has `workspaces`, or that has `pnpm-workspace.yaml`, or
`RunicApplicationFrontendWorkspaceRoot`, as the build finds it (see
[JavaScript workspaces](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#javascript-workspaces)).
Passes for a member of the workspace, and for a frontend that is its own root.
Fails when the root's workspaces leave the frontend out, which the build
reports as `RUNICBRIDGE087`. Warns for a frontend with its own lock file below
a workspace, which installs on its own (unless
`RunicApplicationFrontendWorkspaceRoot=none` says so), and when doctor could
not read the workspace, for example before the first restore of a project that
takes the Views targets from its packages. Added in 0.24.

#### `package-manager-agreement`

That one package manager is named throughout: the workspace root's
`packageManager` and its lock files, `RunicApplicationFrontendPackageManager`,
and the `packageManager` of the frontend, the workspace's members and the
packages that the page depends on. Fails when `packageManager` names another
manager than the root's only lock file, and when a member declares another
manager than the workspace's (the build's `RUNICBRIDGE089`). Warns for lock
files of several managers at the root, for a property that differs from the
workspace's manager, and for a member with a lock file of its own, which the
workspace's install does not read. Added in 0.24.

#### `frontend-dependencies`

The frontend packages that the page depends on: client libraries' packages and
workspace packages that no .NET project owns, read without a build from
`RunicViewsGetFrontendDependencies` and the frontend graph it resolves, as
`dev` reads them. Passes when every compiled package's `exports` exist and
every source package's do. Warns for a compiled package (one with a `build`
script) that is not built yet, since `dotnet build` and `dev` build it. Fails
when a compiled package is missing its exports and its build is off
(`RunicApplicationFrontendBuildEnabled=false`) or has no command, and when a
source package exports a file that does not exist, since nothing writes it.
Warns when doctor could not read the packages. Added in 0.24.

#### `client-modules`

Each specifier that the page imports from a library or workspace package
resolves from the frontend, as Vite resolves it: the `Specifier` of every
`RunicApplicationFrontendClientModule`, and every package of
`frontend-dependencies` that the frontend's `package.json` declares. Only for a
page that imports such a package. Doctor follows `node_modules` from the
frontend up to the workspace root and the package's `exports` (with Vite's
`import`, `module`, `browser`, `development`, `production` and `default`
conditions), or its `main` without `exports`.

- Passes when each resolves to a file that exists, and lists them. A specifier
  that no installed package has passes through a tsconfig `paths` alias
  (`tsconfig.json` or `tsconfig.app.json`) when the Vite configuration has an
  alias for the package, as in the shared library clients example.
- Fails when the package is not in `node_modules` (the fix runs the install at
  the root, or adds `"@acme/shared-ui": "workspace:*"` to the frontend's
  dependencies), when its `exports` don't name the subpath (the fix adds it), when
  the file the package names does not exist and no build writes it, and for a
  relative `Specifier` (`RUNICBRIDGE082`).
- Warns before the first install, while the package's build has not written
  the file yet, for a `paths` alias that the Vite configuration lacks, and when
  `node_modules` holds another copy of the package than the workspace's, such
  as a published version.

Added in 0.24.

#### `views-dedupe`

Only when a package that the page depends on is linked into the frontend (a
workspace or folder package) and its `package.json` lists
`@runic-artifex/views`. Vite resolves the package's imports from its own
folder, which can hold another copy of the runtime than the frontend's.
Passes when the Vite configuration lists `@runic-artifex/views` in
`resolve.dedupe`, and warns otherwise, naming both copies when the install has
two. Doctor reads the configuration's text, so a `dedupe` list that the
configuration builds elsewhere is not seen. The fix:

```ts
export default defineConfig({ resolve: { dedupe: ["@runic-artifex/views"] } });
```

Added in 0.24.

#### `web-bundle`

Only with `doctor --bundle <dir>`, or for the bundle that
`RunicApplicationFrontendPrebuiltDirectory` names (since 0.24). Checks a web bundle offline, as
`package --frontend-bundle` and the publishing build use it: the manifest's
format (`rax.web-bundle/1`) and required members, that each entry's folders
exist inside the bundle, that each page hashes to the files the manifest
records (a truncated download), and that the bundle has an entry for the
project's assembly. Fails otherwise; the fix writes the bundle again with
`dotnet runic bundle`. The View fingerprints need this build's contract IR, so
the publish checks them (`RUNICBRIDGE094`).

#### `compatibility-set`

Once the project is restored, checks the packages of the release train that
the compatibility set records: runic-sdk's own NuGet and npm packages, which
must have the exact versions of the set, and the packages of the train's
Runic Command Line and Runic Translations releases (`Runic.CommandLine`,
`Runic.CommandLine.*`, `Runic.Translations`, `Runic.Translations.*`,
`dotnet-runic-translations` and Translations' npm packages). Those release on
their own between trains, so a version older than the train's or of another
line (minor version before 1.0) only warns. Every other package is not
checked, whatever its name, such as `RunicArtifex.DynamicData` and
`RunicArtifex.ReactiveUI.Validation` (0.22). Warns before the first restore.

#### `extensions`

Checks the tool extensions that `dotnet runic <name>` runs (see
[Tool extensions](#tool-extensions)), found from the project directory as
dispatch finds them. Each one reports its name, version and protocol with `--extension-info`,
or only its version with `--version` when it predates that option. Fails for
another extension protocol or a release line older than the compatibility
set's Translations release (minor version before 1.0). Warns for an older
release of that line, for a later line (Translations releases between trains),
or for a tool that reports no version. The remediation is the
`dotnet tool update` command. Passes when no extension is installed.
Added in 0.24.

#### `launch-arguments`

Compares the Window's arguments of the evaluated configuration (`--configuration`,
Debug by default) on each surface: the project's `StartArguments` and the
`commandLineArgs` of the `Project` profiles in `Properties/launchSettings.json`.
Warns when both are set, naming each such profile with what `dotnet run` passes
(the project's only) and what `dotnet runic dev`, Visual Studio and Runic DevKit
pass (both, the project's first), as `dev`'s `RAPPDEV1020` does. Also warns
when `launchSettings.json` cannot be read, and when a profile's `rax` object
has a key or value that Runic DevKit does not read: `dev` is `"no-window"` or
`"off"`, `page` is `true` or `false`, and `devArguments` is an array of
strings. Otherwise passes, with the arguments and the first profile. See
[Arguments for Debug builds](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#arguments-for-debug-builds).
Added in 0.24.

#### `views-package-versions`

Warns when the installed `@runic-artifex/views` and the restored
`Runic.Application.Views` come from different releases. Added in 0.11; omitted
until both are installed.

#### `application-id`

Warns for an `ApplicationId` that is not a reverse-DNS name of ASCII letters and
digits. Added in 0.11.

#### `frontend-dev-server`

Without a Vite development server, passes for the frontend's configured `dev`
script or watcher.

#### `vite-config`

With a Vite development server, fails when the configured Vite configuration
file is missing.

#### `vite-entry`

With a Vite development server, fails when the configured entry module does not
start with `/` or does not exist below Vite's root, which doctor takes from
`RunicApplicationFrontendViteRoot` (default: the frontend directory; see
[project properties](#project-properties)).

#### `browser`

For a CS-WebUI project, warns when no supported browser is installed (CS-WebUI
then falls back to the platform WebView). A Runic Desktop project needs none.

#### `views-hosts`

Names each Views host adapter the project references and the checks that cover
it. Warns when there is none, or when a host's runtime cannot be checked from
this machine.

#### `desktop-runtime`

Runic Desktop: warns when this machine cannot open its embedded window (GTK 3
and WebKitGTK 4.1 on Linux, the WebView2 Runtime on Windows), because the
application then falls back to an installed browser.

#### `wpf-runtime`

`Runic.Application.Views.Wpf`: warns on a machine other than Windows or without
the WebView2 Runtime.

#### `gtk4-profile`

A project on `Runic.Desktop.Gtk4`: warns once with every missing piece, the
Views Desktop adapter and, on Linux, GTK 4.12 or newer and WebKitGTK 6.0, with
the distribution's package names.

#### `platform-services`

Names the platform services provider that `AddRunicPlatformServices()` selects
here, and on Linux warns without a D-Bus session bus or `xdg-desktop-portal`.

#### `target-rid`

With `--runtime`: whether the project's host supports the runtime identifier,
from the support matrix. See [Deployment checks for a target](#deployment-checks-for-a-target).

#### `target-runtime-identifiers`

With `--runtime`: warns when the project's `RuntimeIdentifiers` leave out the
target.

#### `target-publish`

With `--runtime`: what target machines need without Native AOT, and the native
toolchain that a Native AOT publish needs.

#### `target-presentation`

With `--runtime`: the native runtime that target machines need for the host's
window.

## Library packages under dev

A page can import frontend packages of other projects: a client library's package
(`RunicApplicationFrontendClientLibrary`) or a JavaScript workspace package that
no .NET project owns. `dev`'s build builds them, as `dotnet build` does
([Library packages](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#library-packages)).
After the build, `dev` reads them with `dotnet msbuild <project>
-getTargetResult:RunicViewsGetFrontendDependencies` and the build's
`obj/runic/frontend-graph.json`. It runs that query only when the build listed
packages in `obj/runic/frontend-dependencies.txt`. Since 0.24:

- **At the start**, a package build that fails stops `dev` with `RAPPDEV1018` and the
  build's error, such as `shared-ui/src/index.ts(3,1): error TS1005`. So does a
  compiled package whose exported files are missing after the build. The message
  names the missing file and why it is missing: the package's build is off
  (`RunicApplicationFrontendBuildEnabled=false`), its build didn't write the file,
  or `--no-build` ran no build.
- **While `dev` runs**, it watches each compiled package whose build is on:
  - A package with a `watch` script runs it (`bun run --bun watch`), with its
    output prefixed by the package's folder name (`[shared-ui]`).
  - Otherwise, `dev` reruns the package's build (`bun run --bun build`). It runs
    300 ms after a file of the package stops changing. `node_modules`, `bin`, `obj`,
    hidden folders, the package's output folders and `*.tsbuildinfo` files don't
    count.

  Vite sees the new output and reloads the page. A source package (no `build`
  script) needs no watcher, because Vite follows the workspace link and watches its
  sources.
- **A failed rebuild keeps the page.** `dev` copies the package's output folders
  (its `exports` outside `src/`, such as `dist/`) aside before a rebuild. When the
  build fails or doesn't write an exported file, `dev` prints
  `<owner project> : error RAPPDEV1018: [shared-ui] …` and puts that copy back, so
  the page keeps the last good build. The next change rebuilds the package. A watch
  script writes its own output, so `dev` can only report it: a located error in
  its output (`src/x.ts(3,1): error TS1005: …`) is a `RAPPDEV1018` line. The
  package counts as built again once its outputs change with no new error. With
  `tsc -w`, set `noEmitOnError` to keep its last good output. A watch script that
  exits is reported the same way, and `dev` then reruns the package's build on
  changes instead.
- **Fail fast.** When the Vite development server answers the application entry
  with HTTP 500 twice, `dev` stops at once with `RAPPDEV1007` and Vite's error, for
  example `[vite:import-analysis] Failed to resolve import "@acme/shared-ui" from
  "src/main.ts"`. When the import names a dependency package, `dev` adds the
  package's state: "@acme/shared-ui is the frontend package of SharedUi: it has not
  been built (shared-ui/dist/index.js is missing)". A development server that exits
  reports the first error line of its output.
- **Repeated lines.** The development server's output and the package watchers'
  output show each line once per 10 s, ignoring a leading clock time and colours.
  Every 2 s, `dev` prints how often each held-back line came, for example
  `[vite] (repeated 1,106 more times) [vite] Internal server error: …`.
- **The package manager** of the development server is the workspace's, as in the
  build: `RunicApplicationFrontendPackageManager`, else the `packageManager` of
  the workspace root (the nearest folder above the frontend whose `package.json` has
  `workspaces`, or that has `pnpm-workspace.yaml`), then the root's lock file, then
  the frontend's own. A frontend with a lock file of its own, or
  `RunicApplicationFrontendWorkspaceRoot=none`, is its own root.

The [session file](#session-file) lists the packages and their state as
`dependencies`.

## Debug the Window in your IDE

Since 0.20, `dotnet runic dev --no-window` runs only the frontend (its Vite or
Angular development server, or its watcher) and leaves the Window to your IDE:
F5 builds and starts the Window under the IDE's .NET debugger, with breakpoints
from the first line, and frontend edits still reach the page live. `dev` does
not restart the Window after C# edits, so the debugger is never dropped; use
the IDE's Hot Reload, and restart the Window yourself after a change that
Hot Reload cannot apply, such as a new or renamed ViewModel member (the build
then writes the matching TypeScript client, which the development server
picks up).

While `dev` runs, it marks the frontend in `obj/runic/` (renewed every 5
seconds) and the session file says `"window": { "owner": "ide", "environment": … }`.
A build of the same output, such as the IDE's, then skips the production
frontend build and does not copy `dist/` over the development pages in `www/`;
it also puts those pages back after a clean or Rebuild. The build prints
`<Project>: dotnet runic dev serves the frontend; the build keeps its development pages …`.
Without `dev`, or 30 seconds after a `dev` that was killed, builds are
unchanged; publish never changes, and
`RunicApplicationFrontendDevSessionEnabled=false` turns it off. The Window
needs only `DOTNET_ENVIRONMENT=Development` (DevTools and detailed Bridge
errors). For a frontend without a development server, set
`RUNIC_APPLICATION_DEVELOPMENT_ASSETS` to the absolute path of the frontend's
output directory as well, so a Runic Desktop window serves it live; the session
file's `window.environment` lists both. `--no-window` cannot be combined with
the options that describe how `dev` starts the Window (`RAPPDEV1014`).

1. Start the frontend: `dotnet runic dev --no-window`, in a terminal or as an
   IDE task. It prints `[dev] Ready` when the frontend is served.
2. Start the Window with the IDE's debugger (F5) from a launch profile with
   `DOTNET_ENVIRONMENT=Development`.

The Window gets the project's arguments and those of its launch profile, which
each IDE combines in its own way. `dev` passes both too, the project's first:
when the project sets `StartArguments` and the profile `commandLineArgs`,
`dotnet run` alone would drop the profile's, so `dev` passes them after `--`
itself, before the arguments of `dev -- <args>`, and warns with `RAPPDEV1020`.
`[dev] Window arguments:` lists each part, also with `--dry-run`, and doctor's
[`launch-arguments`](#launch-arguments) check compares the surfaces. Set an argument for Debug builds only, such as
`/mock`, in a Debug-conditioned `StartArguments`, which every IDE and `dev`
read; [Arguments for Debug builds](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#arguments-for-debug-builds)
shows how and lists which surface reads which.

Since 0.20, projects from the `runic-app` template have that launch profile,
named after the project, and with `--editor vscode` VS Code tasks and
configurations like the ones below: see the template README's
[Debug it in your IDE](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/Runic.Application.Templates/content/runic-app/README.md#debug-it-in-your-ide).
For another project, add the snippets below.

A launch profile for Visual Studio and Rider (and `dotnet run`), in
`Properties/launchSettings.json`:

```json
{
  "profiles": {
    "Window (dotnet runic dev --no-window)": {
      "commandName": "Project",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development"
      }
    }
  }
}
```

**VS Code** (C# extension): a background task that waits for `Ready`, then a
build, as the debug configuration's `preLaunchTask`:

```jsonc
// .vscode/tasks.json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "runic: dev --no-window",
      "type": "shell",
      "command": "dotnet",
      "args": ["runic", "dev", "--no-window"],
      "isBackground": true,
      "problemMatcher": {
        // owner, applyTo, fileLocation and pattern as in the IDE contract's task: $msCompile after dev's label
        "background": { "activeOnStart": true, "beginsPattern": "^\\[dev\\] Evaluating", "endsPattern": "^\\[dev\\] Ready$" }
      }
    },
    { "label": "build", "type": "shell", "command": "dotnet", "args": ["build"], "problemMatcher": "$msCompile" },
    { "label": "runic: window", "dependsOn": ["runic: dev --no-window", "build"], "dependsOrder": "sequence" }
  ]
}
// .vscode/launch.json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Window",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "runic: window",
      "program": "${workspaceFolder}/bin/Debug/net10.0/App", // App.exe on Windows
      "cwd": "${workspaceFolder}",
      "env": { "DOTNET_ENVIRONMENT": "Development" }
    }
  ]
}
```

With C# Dev Kit, a `"type": "dotnet"` configuration can start the launch
profile above instead, after the same task:

```jsonc
{
  "name": "Window (C# Dev Kit)",
  "type": "dotnet",
  "request": "launch",
  "projectPath": "${workspaceFolder}/App.csproj",
  "launchConfigurationId": "TargetFramework=;Window (dotnet runic dev --no-window)",
  "preLaunchTask": "runic: dev --no-window"
}
```

**Rider**: start `dotnet runic dev --no-window` in Rider's terminal (or add it
to the run configuration's "Before launch" list as an external tool), then
debug the "Window (dotnet runic dev --no-window)" launch profile.

**Visual Studio**: start `dotnet runic dev --no-window` in the Developer
PowerShell or a terminal, choose the "Window (dotnet runic dev --no-window)"
profile next to the Start button, and press F5.
[Runic DevKit for Visual Studio](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit-vs)
(`runic-artifex.runic-devkit-vs`) does this for you: when F5 starts a Runic
project and no `dev --no-window` runs, it asks whether to start it, and starts
the Window once `dev` is ready. Its **Debug the Window** command also attaches
Visual Studio's JavaScript debugger to the page over a DevTools Protocol port,
so C# and `Frontend/src` breakpoints stop in one session. It needs Visual
Studio's JavaScript diagnostics component, which the template's `.vsconfig`
names. See
[Visual Studio](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#visual-studio)
in the debugging guide.

**VS Code** with
[Runic DevKit for VS Code](https://marketplace.visualstudio.com/items?itemName=runic-artifex.runic-devkit)
(`runic-artifex.runic-devkit`): its `runic` debug type starts
`dev --no-window`, waits for `Ready` and starts the Window under the debugger
without the tasks above.

To keep `dev` starting the Window and attach instead, use the
[IDE contract](#ide-contract): `[dev] Window started: process <pid>` names the
process to attach to; an attached debugger ends when `dotnet watch` restarts the
Window (`--no-dotnet-watch` avoids that).

Verified by hand in 0.20 on Linux with VS Code 1.144 and the C# extension
(2.160, `coreclr`): F5 ran the task, waited for `Ready`, built without
replacing the development page, stopped on a breakpoint in `Program.cs`, and
the Window showed the page from the Vite server and a frontend edit live.
Also verified by hand, with the 0.22.0 packages:

- **C# Dev Kit** 3.40 in VS Code 1.144 on Linux: the `"type": "dotnet"`
  configuration above ran the task, waited for `Ready`, started the launch
  profile under the debugger and stopped on a breakpoint in `Program.cs`; the
  Window showed the page from the Vite server and a frontend edit live.
- **Visual Studio 2026** Community on Windows 11, without Runic DevKit: with
  `dotnet runic dev --no-window` running, F5 on the project's launch profile
  built, stopped on a breakpoint in `Program.cs`, and the WebView2 Window showed
  the page from the Vite server and a frontend edit live.
- **Visual Studio with Runic DevKit** (0.22, Visual Studio 2026 18.12): Debug
  the Window and Attach to the Page, with C# and `Frontend/src` breakpoints in
  one session.

Runic does not test Rider. VS Code on Windows runs the configurations above
with the `.exe` program; Runic's CI checks the template's WebView2 script
debugging there, not VS Code's .NET debugger.

## Debug the page's JavaScript on Linux

Since 0.21, `dotnet runic dev` on Linux serves the Window's WebKitGTK pages to
JavaScript debuggers on port 9222, as WebView2 does on Windows (D066.39). VS
Code's JavaScript debugger attaches with an attach configuration on that port,
such as the template's `Attach to the page (JavaScript)` (`"type": "msedge"` with
`"useWebView": true`, or `"type": "chrome"`), and `webRoot` set to the frontend
directory: breakpoints in the frontend's sources through the development
server's source maps, stepping, conditions, logpoints, exceptions, the console,
the page's variables and hover all work, also after a reload or an HMR update.
Breakpoints and logpoints also hit code that runs while the page loads, in a
module that loads after thousands of others too: as Chromium does, the adapter
stops each script with a source map before it runs until the debugger has its
source map. When `dotnet watch` restarts the Window after a C# edit, the debugger stays
attached and continues on the new Window's page with its breakpoints. Runic's CI
runs these scenarios with VS Code's JavaScript debugger on GTK 3 and GTK 4 for
every change to the adapter. Verified by hand with VS Code 1.144 (dotnet-runic
0.22.0): the template's `Window + WebKitGTK (Linux)` stopped on a breakpoint in
`Program.cs` and then, through the adapter, on one in `App.tsx`'s click handler.

How it works: `dev` starts the Window with `WEBKIT_INSPECTOR_SERVER` on a free
loopback port (9223 with `--no-window`, which the template's
`Window with JavaScript debugging (Linux)` configuration sets), the server
WebKitGTK's own remote inspector clients use. Runic's CDP adapter in `dev`
translates between the Chrome DevTools Protocol and the WebKit Inspector
Protocol, and connects to WebKit only while a debugger is attached; in between,
GNOME Web can open the `inspector://` address that the `[dev] Debugging:` line
names. The session file lists both under `debugEndpoints`.

- **Security.** The endpoint can script the Window's page and its Bridge, so
  it admits only debuggers of the user who runs `dev`:
  - It listens on 127.0.0.1 and `[::1]`, on the same port, since 0.23: a
    debugger told `localhost` may connect to `[::1]` first, where another
    user could otherwise listen. When either address's port is taken, the
    port counts as taken. It refuses (403) a request whose `Host` is
    not an IP address or `localhost`, and any request with an `Origin`, so a
    web page in a browser cannot reach it. Its WebSocket paths carry a random
    secret that only `/json/version` and `/json/list` reveal.
  - Other users on the same machine can reach loopback ports too. So `dev`
    looks up the owner of each connection's other end in `/proc/net/tcp`,
    closes it when that is another user, and says so in a `[dev]` line. The
    adapter talks to WebKit's inspector port only when its listener belongs
    to you. If another user holds 9222, `dev` warns that an attach
    configuration with that port would reach their process.
  - WebKit's own `inspector://` port is WebKit's, so `dev` cannot filter it.
    Other local users can connect to it and script the Window, also while a
    debugger is attached: WebKit serves one client and lets a new one take
    its place (WebKitGTK 2.54). The adapter survives that (see "Another
    client of WebKit's inspector" below), but the Window can crash when a
    replaced client of WebKit's own, such as GNOME Web's `inspector://` page,
    sends again. On a machine that you share with other users, run `dev` with
    `--no-debug-endpoint`.
  - It runs only under `dotnet runic dev` and adds nothing to the application.
- **Not supported.** Setting a variable to an object or a `const` (WebKit has no
  `setVariableValue`; a primitive is assigned in the frame), restarting a frame,
  and the Elements, Network and other panels of a Chrome DevTools window on this
  endpoint. Rider's JavaScript debugger speaks the same protocol, but Runic does
  not test it. On macOS, WKWebView offers Safari's Web Inspector only.
- **WebKit's own inspector.** WebKit serves either its socket for the adapter or
  its Web Inspector over HTTP (`WEBKIT_INSPECTOR_HTTP_SERVER`), never both.
  `--webkit-inspector` opens the HTTP one instead of the adapter, for any browser,
  on WebKitGTK 2.54.3 or later; older WebKitGTK lets any web page reach it
  (WebKit bug 330602, also fixed in 2.56; the 2.55 development releases count
  as unfixed). A `WEBKIT_INSPECTOR_SERVER` or `WEBKIT_INSPECTOR_HTTP_SERVER`
  that you set with `-e` or in `dev`'s environment is used as it is. `dev`
  warns when such an HTTP inspector runs on a WebKitGTK without that fix, and
  when either address is not loopback. Under `--no-window` the version check
  uses the WebKitGTK that `dev` loads, which can differ from the Window's.
- **Another client of WebKit's inspector.** WebKit's `inspector://` server has
  one client slot, and a client that sets up takes it from the one that holds it
  without telling that one (WebKitGTK 2.54). When that happens while a debugger is
  attached, `dev` prints `[dev] Debugging: another client took over the Window's
  WebKit inspector …`, the debugger's session ends as in Chrome when another
  DevTools takes the page over (`Inspector.detached`, then
  `Target.detachedFromTarget`), and `dev` sends nothing more to WebKit until a
  debugger attaches again, which takes the inspector back. `dev` notices a new
  connection to the inspector port in Linux's TCP tables and then checks whether
  the Window still answers the adapter, so a Window stopped at a .NET breakpoint
  does not count. Each time the adapter attaches to a page it claims the slot in
  the same message, so a client that took the slot and left cannot make WebKit
  crash the Window through the adapter. A client that connects and waits several
  seconds before it sets up can go unnoticed; the session then stops answering
  instead of ending. WebKit's own clients are not protected: after the adapter
  took the slot from GNOME Web's `inspector://` page and released it, that page's
  next attach can crash the Window.
- **Port 9222 taken.** `dev` uses another free port and says which, and names
  what holds 9222 when it is your process, such as another `dev` session and
  its directory. Change the attach configuration's `port` to the one `dev`
  names; the session file lists it under `debugEndpoints`. When the project's
  `.vscode/launch.json` (or one in a parent folder of the repository) has a
  JavaScript attach configuration on 9222, such as the template's
  `Attach to the page (JavaScript)`, the line is a warning that names it: as it
  is, it would debug the other process's page. When another user holds 9222,
  or the inspector port 9223 under `--no-window`, the `[dev]` line is a warning
  too. Under `--no-window`, which the IDE's compound starts with an attach
  configuration on 9222, `dev` does not move: it stops with `RAPPDEV1017` and
  names what holds the port, often another `dev` session. `--no-debug-endpoint`
  opens nothing.
- **No answer after attaching.** When the Window's page does not answer the
  debugger's first command within 5 seconds, for example right after WebKit's
  Web Inspector opened in the Window, `dev` prints a `[dev] Debugging:` line,
  waits a second and sends the command again if sending it twice is harmless
  (an `enable` or a `get…`). Without an answer then, the command fails with an
  error that names it, and `dev` says to close WebKit's Web Inspector and any
  `inspector://` page and attach again. A Window stopped at a .NET breakpoint
  does not answer either.
- **Reporting a problem.** `RUNIC_DEV_CDP_TRACE=<file>` writes the adapter's
  traffic, which contains the page's data, to that file. `dev` creates the
  file readable by you only. JSON strings longer than 4096 characters, such as
  script sources and inline source maps, keep only their first 200 characters,
  so a script takes a few hundred bytes of the trace instead of its whole source.
  `RUNIC_DEV_CDP_TRACE_SOURCES=1` keeps them whole: on a page with about 4,000
  modules, a trace with the sources reached 723 MB in five minutes. The trace
  never shrinks or rotates; delete it when you are done.

## Debug the page in Chrome (`--browser`)

Since 0.21, `dotnet runic dev --browser` serves the Window to a browser instead
of opening it, so your IDE's JavaScript debugger can open its pages in Chrome or
Edge on every operating system (D066.38). On macOS, where WKWebView offers only
Safari's Web Inspector, it is the way to stop on breakpoints in the frontend's
sources from the IDE. The page runs on Chromium, not on the Window's engine
(WebKit on Linux and macOS, WebView2 on Windows), so check engine-specific
behaviour in the Window itself.

`dev` starts the Window with `RUNIC_APPLICATION_SERVE_ONLY=1` (the
[serve-only mode](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.Desktop/README.md)
of Runic Desktop and CS-WebUI) and opens no native window. Everything else is
as without `--browser`: the development server or watcher, the build, `dotnet
watch` restarting the Window after C# edits. Native
window features are unavailable: platform services such as file dialogs, menus
and the window's own commands report `OwnerUnavailable`, a Window with
`GuardClose` opens unguarded, and there is no side panel.

The Window's own address changes with every start and carries its server's
credential, so `dev` does not print it. Instead it serves the Window's pages at
a stable address of its own, `http://<label>.localhost:9224/` (another port
when 9224 is taken on 127.0.0.1 or `[::1]`; the lines say which), for one browser that a one-time
code admits. `<label>` is 26 random lowercase letters and digits, new for each
`dev` run and the same across its Window restarts. Chromium browsers (Chrome,
Edge) and Firefox resolve names under `.localhost` to the loopback interface
themselves; Chromium connects to `[::1]` first, Firefox to 127.0.0.1, so `dev`
listens on both.
`/` is the Window's first page, `/windows/2` and so on the Windows the
application opens after it. Until the Window serves, and while `dotnet watch`
restarts it, the address shows a page that reloads itself every second.

```text
[dev] Browser: the Window serves without a native window (--browser). Open http://<label>.localhost:9224/?code=… in Chrome or Edge, or the page obj/runic/dev-browser.html, …
[dev] Browser: Window serves at http://<label>.localhost:9224/?code=…
[dev] Ready
```

Since 0.23 (#1187), the address is a proxy to the Window's own loopback server,
and a tab keeps it across restarts:

- **An origin for each session.** Each `dev` run is an origin of its own, so
  pages, service workers, storage and cookies of an earlier session or another
  project, such as a script that an npm dependency left there, do not reach it.
  The address answers only its own name: a request to `127.0.0.1:9224`,
  `localhost:9224` or another label gets 403, which names no label.

- **The code.** The `[dev] Browser:` lines carry the current one-time code, and
  `dev` writes a page that opens the address with it,
  `obj/runic/dev-browser.html` (the session file's `browser.codeFile`), with
  the label. Only you
  can read that page: mode 0600 on Linux and macOS, an access list of your account
  alone on Windows. Opening the address with the code gives that browser a new
  cookie of `dev`'s (`HttpOnly`, `SameSite=Strict`), replaces the code and
  rewrites the page; a `[dev] Browser:` line prints the new code. `dev` deletes
  the page when it stops.
- **One browser at a time.** The new cookie takes over when that browser's next
  navigation, which the admission's page makes at once, presents it. Then the
  previous browser's admission ends: its next request gets 403, and its open
  WebSocket connections, such as a page's Bridge, close. A request that only
  carries the code, and no such navigation, ends nothing. To move to another
  browser or profile, open the page (or the newest code) there.
- **A client other than a browser.** The address is meant for a browser.
  Browsers resolve names under `.localhost` to the loopback interface
  themselves, and so does current curl (8.x). Programs that use the system's
  resolver, such as Node's `fetch`, .NET's `HttpClient` or PowerShell, find
  the name only where that resolver knows `.localhost` names: on Linux with
  systemd's resolver or `nss-myhostname`, not on Windows 11 or on a Linux with
  files and DNS alone (such as musl-based images). There, and on macOS, connect
  to `127.0.0.1:<port>` and send `Host: <label>.localhost:<port>` (curl:
  `--resolve <label>.localhost:9224:127.0.0.1`). Such a client is admitted as
  a browser is: the request with the code gets the cookie, and a `GET` of `/`
  with the cookie confirms it.
- **A Window the application closes.** When the application closes a Window
  while its process keeps running, `/` opens the first Window that still serves,
  and `/windows/<n>` of the closed one says that it closed.
- **Restarts.** After `dotnet watch` restarts the Window, a Runic Desktop page
  reloads itself onto the new Window. Its Bridge connects through the same
  address. When the stopping Window tells the page to close, `dev` passes on
  the Bridge's token refusal instead, so the page reloads; it waits at the
  address until the new Window serves, then opens it. A page's Bridge that
  reconnects to a Window that has gone gets the same answer.
  A CS-WebUI page's WebUI connection goes to the Window's own port, so reload a
  CS-WebUI tab after a restart; the address stays the same.
- **The Window's side.** `dev` sets `RUNIC_APPLICATION_SERVE_ORIGIN` to the
  address's origin, `http://<label>.localhost:<port>`, in the Window's
  environment, which other users cannot read: Runic Desktop admits the pages from it and
  connects their Bridge through it, and Runic's CS-WebUI host admits it at its
  upload endpoint. Requests keep their `Origin`; `dev` gives the Window its own
  `Host` and moves the Window's redirects onto the address.
- **More than one CS-WebUI Window.** CS-WebUI Windows each serve at their own
  server's root, so the address serves the first one; `/windows/<n>` sends the
  browser to a later one's own address, which does not survive a restart.

A VS Code launch configuration (the `runic-app` template's `Window in Chrome`)
runs `dev --browser` as a background task that ends at `[dev] Ready` and opens
the code page with VS Code's JavaScript debugger, with `webRoot` set to the
frontend directory for the development server's source maps. The page carries
the port, so the configuration needs no change when 9224 is taken:

```jsonc
// .vscode/launch.json; the task is the IDE contract's "runic: dev" task with "args": ["runic", "dev", "--browser"]
{
  "name": "Window in Chrome",
  "type": "chrome",                 // "msedge" for Edge
  "request": "launch",
  "preLaunchTask": "runic: dev --browser",
  "file": "${workspaceFolder}/obj/runic/dev-browser.html",
  "webRoot": "${workspaceFolder}/Frontend"
}
```

Before 0.23, the configuration opened `"url": "http://127.0.0.1:9224/"`; a
project generated earlier needs the `"file"` line instead. A configuration that
matches the page by URL, such as an attach configuration's `urlFilter`, uses
`http://*.localhost:9224/*`.

Breakpoints in the frontend's sources, stepping, conditions, logpoints,
exceptions, the console, the page's variables, reloads and HMR updates work as
in any Chrome page, and a Runic Desktop page's breakpoints stop again after a
restart. Other IDEs open the same page from their own JavaScript debug
configuration, such as Rider's "JavaScript Debug" with its `file://` URL, or the
address with the code from the `[dev] Browser:` line.

- **Security.** The address listens on 127.0.0.1 and `[::1]`, with the same
  rules on both, and answers only its own `Host`, `<label>.localhost:<port>`, which also keeps out DNS rebinding. A
  request with the current code (a `GET` of `/` or `/windows/<n>`) is admitted
  from any site, since the code page is a `file:` document; every other request
  needs the admitted browser's cookie, no `Origin` other than the address's own,
  and a `Sec-Fetch-Site` of `none` or `same-origin` when it has one, so a web
  page in a browser cannot reach the Window through it. The code is compared in
  constant time, and its answers are not cached and send no referrer. `dev`'s
  cookie is host-only (no `Domain`) and stays out of the requests it passes to
  the Window.
- **The label, the code and the cookie are bearer secrets.** Whoever has the
  code, or the admitted browser's cookie together with the label, uses the
  Window as you do. Browsers do not scope cookies by port, so your browser sends
  the cookie with every request to `<label>.localhost` on any port: someone who
  knew the label could have it send the cookie to a port of theirs. So `dev`
  keeps the label where it keeps the code: in its `[dev] Browser:` output and
  the code page, which only you can read. The session file has `*` in its place,
  and neither appears in process arguments or `dev`'s other lines. Do not paste
  the address where others can read it.
- **Other users on the machine.** Loopback ports admit every local account,
  but without the label and the code or cookie another user's process gets
  403. On Linux, `dev` also looks up the owner of each connection's other end
  in `/proc/net/tcp`, as for the CDP adapter, answers 403 when that is another
  user, and says so once per user in a `[dev] Warning:` line. macOS and Windows
  cannot tell a loopback connection's user, so there only the secrets keep
  another user out.
- **Standard input.** A serve-only Window stops serving at a line on its
  standard input, so `dev` gives it an empty pipe instead of the terminal.
- **The .NET debugger.** Attach it to the process that
  `[dev] Window started: process <pid>` names, as without `--browser`;
  `--no-window` and `--browser` do not combine, because the IDE could not tell
  `dev` the Window's address.
- **Not debugging endpoints.** `--browser` lists no `debugEndpoints` and opens
  neither WebView2's nor WebKit's: the debugger owns the browser it launched.

## Debug a CS-WebUI page's JavaScript

Since 0.22, a CS-WebUI Window's installed Chrome or Edge can open its Chrome
DevTools Protocol endpoint for a JavaScript debugger: Visual Studio's Attach to
Process, or js-debug's `attach`. Any local process can attach to it, so `dev`
asks for it only when an IDE does (D070.17). A terminal `dotnet runic dev`
opens none, and says so in a `[dev] Debugging:` line.

- **`--debug-endpoint`**, for an IDE to pass when it starts `dev`: `dev`
  sets `RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=0` for the Window. In Development,
  the CS-WebUI host (`Runic.Application.Views.CsWebUi`) then starts the browser
  of each Window that runs in a browser with `--remote-debugging-port` on a free
  loopback port, by the same rules as Runic Desktop. A Window with `UseWebView`
  opens none.
  - The Window prints `RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp <url>`
    when the browser listens, and `RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=<url>`
    when it exits.
  - `dev` reads those lines instead of printing them, and prints
    `[dev] Debugging: ... <url>`.
  - It lists the endpoint in the session file's `debugEndpoints` while that
    browser runs, until the Window process exits.
- **`--no-window`**: the IDE starts the Window with the session file's
  `window.environment`, which names port 9225. Only an IDE applies that
  environment, so this is the IDE asking too. `dev` cannot read that Window's
  lines, so `debugEndpoints` lists `http://127.0.0.1:9225/` for the session,
  as WebView2's port.
  - If something holds 9225 on 127.0.0.1 or `[::1]` when `dev` starts, `dev`
    prints a warning and lists the endpoint under `unavailableDebugEndpoints`
    (`"reason": "port-taken"`, or on Linux `"foreign-listener"` for another
    user). On Linux it does the same if another user starts listening on 9225
    later.
  - A Window that finds the port taken uses a free one and warns on its
    standard error.
- **Your own port**: `-e RUNIC_WEBUI_REMOTE_DEBUGGING_PORT=<port>`.
  - With a Window that `dev` starts, `dev` lists the port the Window confirms. For
    a fixed port, that is after `/json/version` answers on 127.0.0.1 and, on
    Linux, no other user listens there.
  - With `--no-window`, `dev` lists a fixed port.
  - The `-e` variable is passed on even with `--no-debug-endpoint`.
- **Not opened** in these cases:
  - with `--no-debug-endpoint`, which wins over `--debug-endpoint`, or with
    `--browser`;
  - for a Runic Desktop project;
  - when the Window uses Firefox or the platform WebView.

On a shared machine, use `--no-debug-endpoint`. See
[CS-WebUI pages in a JavaScript debugger](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#cs-webui-pages-in-a-javascript-debugger)
and the [security guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/security.md#development-server).

## The Runic DevTools extension

Since 0.21 the tool ships an unpacked Chrome DevTools extension (Manifest V3) in
its `devtools-extension` folder, beside its assembly:
`tools/net10.0/any/devtools-extension` in the package, so
`~/.nuget/packages/dotnet-runic/<version>/tools/net10.0/any/devtools-extension`
for a local tool. It adds a **Runic** panel (the page's Views with their state
and C# ViewModels, and the recent Bridge calls with timing, outcome and C#
handler) and a **Runic** pane in the Elements panel (the selected element's
bindings and commands, as Runic Inspect shows them). It reads Runic Inspect's
development API on the page (`snapshot()` and `describe($0)` on
`globalThis[Symbol.for("runic.vite.inspect")]`), so it works on pages that
vite-plugin-runic 0.21 or later serves under `vite dev` or `dev`, and asks for
no permissions besides DevTools.

`dev` prints `[dev] DevTools extension: <folder>` when the page has a
development server, and writes the folder to the [session file](#session-file)
as `devtoolsExtension`, with its fixed id `cddbfcinbkaindmfgcfdgapddkjeadgh` as
`devtoolsExtensionId` (the manifest's public `key` fixes it; there is no private
key). The build stamps the manifest with the package version: `X.Y.Z`, or
`X.Y.Z.N` for a prerelease `X.Y.Z-label.N` (a Chrome manifest version has only
numbers), with the full version as `version_name`. Load it in Chrome or Edge with **Load unpacked** on
`chrome://extensions` (Developer mode), or start Chromium or Chrome for Testing
with `--load-extension=<folder>`; branded Chrome ignores that switch since
Chrome 137. There is no Chrome Web Store listing.

- **The Window on Windows.** `dev` loads the extension into the Window's
  WebView2: in `Development` it appends the folder to the Window's
  `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS`, after the folders you set there, once,
  and prints `[dev] DevTools extension: the Window's WebView2 loads it …`. Open
  the Window's DevTools and choose the **Runic** panel.
  `--no-devtools-extension` leaves it out. With `--no-window`, the session
  file's `window.environment` has the variable for the IDE's launch. Outside
  `Development` `dev` sets nothing and the host loads no extensions.
- **The IDE's Chrome (`--browser`).** `dev` prints a second line with the
  folder: load it once with **Load unpacked** in the Chrome window that the
  launch opens (VS Code's JavaScript debugger keeps that browser profile between
  launches by default). The template's `Window in Chrome` configuration starts
  branded Chrome and passes no `--load-extension`, because Chrome ignores it
  and `launch.json` cannot read the session file. With Chromium or Chrome for
  Testing as `runtimeExecutable`, add
  `"runtimeArgs": ["--load-extension=<folder>"]` with the printed folder, and
  update it after a tool update.
[The Runic DevTools extension](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/debugging.md#the-runic-devtools-extension)
in the debugging guide has the steps and what each pane shows.

## IDE contract

Since 0.20, `dev` gives IDEs, tasks and extensions stable lines to wait on and a
session file to read. Unlike the rest of `dev`'s human text, these are a
contract: they keep their form within `runic.application.tool.dev-session/1`.

| Line (standard output) | When |
| --- | --- |
| `[dev] Window started: process <pid>` | Each time a Window process starts: the first start, a restart by `dotnet watch` after a C# edit, and a restart by `dev` itself. `<pid>` is the application's own process, the one to attach a .NET debugger to, not `dotnet watch` or `dotnet run`. |
| `[dev] Ready` | After each `Window started` line: the frontend (its development server, or its watcher or initial build) and the Window process can be used. With `--browser`, `Ready` waits for the Window's first page instead: it follows the first `Browser` line of each Window process, which can come before `Window started`, so a browser that an IDE opens at `Ready` finds the page. |
| `[dev] Browser: Window serves at <url>` | With `--browser` (since 0.21), each time the Window process serves a Window: the first at `dev`'s root address, later ones at `/windows/<n>` ([Debug the page in Chrome](#debug-the-page-in-chrome---browser)). Since 0.23 the URL carries the current one-time code (`?code=…`), which admits one browser; the session file's `browser.codeFile` opens the address with the newest code. One form for every Window; match it with `^\[dev\] Browser: Window serves at (?<url>http://\S+)$`. Other `[dev] Browser:` lines are human text. |
| `[dev] Window exited: process <pid>` | When that process exits, for example before `dotnet watch` restarts it. A debugger attached to it has ended. |
| `[dev] Evaluating` (line prefix) | Once, when `dev` starts evaluating the project, before it builds or starts anything: `[dev] Evaluating Views Window project...`, and `[dev] Evaluating Views Window project completed in <time>.` when that is done. Since 0.21 in the contract. |
| `[vite] [runic] Frontend errors cleared` | Since 0.23, after a frontend module that did not compile (a `RUNICP008` line, below) compiles again: the frontend errors printed since the last such line no longer apply. Printed by the Runic Vite plugin, so only for a Vite frontend. |
| `[dev] Reloading` (line prefix) | Each time `dev` itself restarts the Window process, for example after a frontend compiler change that hot reload cannot apply: `[dev] Reloading the Runic Views Window application.`, after the old process stopped and before the new one starts. `Window started` and `Ready` follow. Since 0.21 in the contract. |

**Stopping `dev`.** Stop `dev` as a terminal does, with Ctrl+C or SIGINT/SIGTERM.
`dev` then stops the frontend and the Window, marks the session file
`"stopped"`, replaces the development pages it wrote into the web root with a
page that loads nothing (since 0.23; a page that a build replaced meanwhile
stays), deletes `dev-frontend` and `dev-pages/`, touches `dev-stopped` and
exits with 130. The next build copies the frontend's pages back. A process
killed by force does none of this: its development pages stay until the next
build, a build within 30 seconds still keeps them, and the session file keeps
its state. The next `dev` checks the session file when it starts (see
[Session file](#session-file)) and, before its first build, removes the
`dev-frontend` and `dev-pages/` of a `dev` that no longer runs, so its own build
installs and builds the frontend (since 0.24).
On Windows, the frontend's development server or watcher runs in a Job object
with everything it starts (the package manager, Node.js below it and the
`runic-translations serve` that the Vite plugin starts), so the stop ends all of
them even when the package manager exits first, and Windows ends them when
`dev` itself is killed (since 0.23).
An IDE that can't send Ctrl+C, such as Visual Studio on Windows (a process
without a console has none), sets `RUNIC_DEV_STOP_ON_INPUT=1` in `dev`'s
environment and redirects its standard input (since 0.22). `dev` then reads its
input and stops in the same way at a line `stop` (case and surrounding space
ignored) or at the end of the input. To stop it, the IDE writes `stop` or closes
the pipe, waits for `dev` to exit, and ends the process tree only after a
timeout. A few seconds are usually enough; allow more while the Window closes.
`dev` prints `[dev] Standard input asked dev to stop; stopping the application.`
or `[dev] Standard input ended; …` on standard error. Other lines are ignored.
The processes that `dev` starts get an empty input of their own, so none of them
reads the request. Without the variable, `dev` doesn't read its input, and an
interactive terminal works as before. An older `dev` ignores the variable, so
keep the forced stop as the fallback.

**Known issue: SIGTERM with the .NET 11 SDK.** With SDK 11.0.100 on Linux, a
SIGTERM sent to the `dotnet` process that runs `dotnet runic dev` ends that
process without forwarding the signal to `dev`
([dotnet/sdk#57091](https://github.com/dotnet/sdk/issues/57091)): `dev` keeps
running without its parent, with its Window and development server, and a
process manager that stops it so leaves it behind. SDK 10 forwards the signal.
Set `DOTNET_CLI_ENABLEAOT=0` in the environment that starts `dev`, which makes
SDK 11 forward it too, or send the signal to `dev` itself (the session file's
`processId`) or to its process group. Ctrl+C in a terminal is not affected: the
terminal signals every process of its group. The stop on standard input
(`RUNIC_DEV_STOP_ON_INPUT=1`) is not affected either, as `dev` reads the input
it inherits from the `dotnet` process; Runic DevKit for Visual Studio and for VS
Code stop `dev` that way. Neither SDK forwards a SIGINT that reaches only the
`dotnet` process: send a forced stop's SIGINT to `dev` or its process group.

The two prefix lines are where a background task's `beginsPattern` starts a new
build cycle, as the template's `.vscode/tasks.json` below does; the text after
the prefix is not part of the contract.

`dev` writes the [session file](#session-file) before it prints each
`Window started`, `Ready`, `Browser` and `Window exited` line, so a reader that
reacts to a line finds the same state there: after `Window started`, the file's
`window.processId`; after `Ready`, `"state": "ready"`; after a `Browser` line,
the Window in `browser.windows`; after `Window exited`, no Window process and
`"state": "starting"`. In 0.20.0 previews, `dev` printed the line first.

`dev` finds the Window process below the `dotnet watch` or `dotnet run`
process it started: the shallowest process that runs the project's program
(MSBuild's `RunCommand`, so on macOS the development bundle's executable, or
`dotnet <AssemblyName>.dll` without an application host). It looks every
250 ms (every second on macOS, which runs `ps`), so the line follows the start
by up to that long.

A VS Code background task can wait for `Ready`, and while `dev` runs the same
task gets `dev`'s failures and the errors of each build in the Problems panel,
as the template's `.vscode/tasks.json` does. VS Code removes a background
task's problems when its process ends, so a failure that ends `dev` (a failed
first build, `RAPPDEV1018`) would leave Problems empty. Since 0.24 `dev` writes
that failure into the [session file](#session-file) (`failure`) before it
exits, and Runic DevKit shows it in Problems and its dependency tree, however
the task was started (F5's `preLaunchTask`, Terminal > Run Task or DevKit's
**Start dotnet runic dev**). Without DevKit it stays in the task's terminal:

```jsonc
// .vscode/tasks.json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "runic: dev",
      "type": "shell",
      "command": "dotnet",
      "args": ["runic", "dev"],
      "isBackground": true,
      "problemMatcher": {
        // $msCompile's pattern after dev's optional child-process label; the file holds
        // no ": " and the subcategory no colon, so a failure sentence is not a file
        "owner": "msCompile",
        "applyTo": "allDocuments",
        "fileLocation": "absolute",
        "pattern": {
          "regexp": "^(?:\\[(?:host|app|vite)\\] )?\\s*(?:\\s*\\d+>)?(\\S(?:(?!:\\s).)*?)(?:\\((\\d+|\\d+,\\d+|\\d+,\\d+,\\d+,\\d+)\\))?\\s*:\\s+(?:([^\\s:]+)\\s+)?((?:fatal +)?error|warning|info)\\s+(\\w+\\d+)?\\s*:\\s*(.*)$",
          "file": 1, "location": 2, "severity": 4, "code": 5, "message": 6
        },
        "background": {
          "activeOnStart": true,
          "beginsPattern": "^\\[dev\\] (Evaluating|Window exited|Reloading)",
          "endsPattern": "^\\[dev\\] Ready$"
        }
      }
    }
  ]
}
```

**Diagnostics.** When `dev` fails, it prints the fault as one line in MSBuild's
canonical form before the usual fault text, on standard error:

```text
/path/to/App/App.csproj : error RAPPDEV1007: The vite development server exited unexpectedly with code 1.
```

The origin is the project file, or `dotnet-runic` when `dev` failed before it
found one (`RAPPDEV1002`). The build output that `dev` passes through has the
same form with a line and column (`CounterViewModel.cs(18,5): error CS1002: …`,
`RUNICBRIDGE` codes from the Bridge generator), so one regex matches both. The
first build's lines come as the build prints them. The lines of a process that
`dev` starts, such as `dotnet watch` rebuilding after an edit, follow its label:
`[host] /path/to/App/CounterViewModel.cs(18,5): error CS1002: ; expected`
(`[app] ` with `--no-dotnet-watch`; the Vite development server's lines follow
`[vite] `). VS Code's built-in `$msCompile` takes `[host] /path/…` for the
file, so the task above uses its pattern after an optional label. `dev` also
prints a failure as a sentence (`…, so dev did not start: /path/to/index.ts(12,14): error TS2322: …`),
which `$msCompile` would file under the sentence. So in the task's pattern the
file holds no `": "` and the subcategory before `error` no colon.

Since 0.23, a frontend module that Vite cannot compile (a TSX syntax error, an
import that does not resolve) is also one such line per error, from the Runic
Vite plugin, besides Vite's own output and the page's overlay. The path is
absolute and the position 1-based, as in the build's lines:

```text
[vite] /path/to/App/Frontend/src/App.tsx(29,50): error RUNICP008: [vite:oxc] [PARSE_ERROR] Expected corresponding JSX closing tag for 'p'.
```

The message starts with the Vite plugin that failed. Once the modules that
failed compile again, `[vite] [runic] Frontend errors cleared` follows, and an
IDE removes the `RUNICP008` errors it took from the lines before. There is no
type check: a type error that Vite compiles is not one. This pattern matches
all of them, for other tools:

```text
^(?:\[(?:host|app|vite)\] )?\s*(?:\d+>)?(?<file>\S(?:(?!:\s).)*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?:[^\s:]+\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?$
```

### Session file

`dev` writes `dev-session.json` in the `runic` folder of the project's
`BaseIntermediateOutputPath`: `obj/runic/dev-session.json` next to the project
file by default. A project that sets `BaseIntermediateOutputPath` (for example in
`Directory.Build.props`) has it at `<BaseIntermediateOutputPath>/runic/dev-session.json`;
`dotnet msbuild <project> -getProperty:BaseIntermediateOutputPath` prints that
folder. `dev` writes the file when it starts, and rewrites it when the
development server is ready and each time the Window process starts or exits.
When `dev` ends, the file stays with `"state": "stopped"`; since 0.24, when a
failure stopped it, the same write records that failure in `failure`, so an IDE
can show it after `dev` has exited (a failure before `dev` evaluated the
project, such as a project that does not exist, has no session file to go
into). A `dev` that is
killed (an IDE that quits while its task runs, `kill -9`, a closed terminal)
can't write that, so the file keeps its last state. Before trusting `"starting"`
or `"ready"`, a reader checks that `dev` still runs. A process with the
top-level `processId` must run, and its start time must match
`processStartedAt`. Different APIs report a process's start time with different
precision, so compare to within a second. A dead `processId`, or one that a
later process reuses, means the session stopped. Since 0.20; earlier files have
no `processStartedAt`, and a reader then checks the process's command line or
image name. Beside it, `dev` keeps
`dev-frontend` and `dev-pages/` while it serves the frontend, for the build
([Debug the Window in your IDE](#debug-the-window-in-your-ide)). When it ends, it
touches `dev-stopped`, which Visual Studio's fast up-to-date check reads, so the
next F5 builds and puts the production frontend back in `www/`. Each write
replaces the whole file, so a reader never sees half of one. Two `dev` sessions of the
same project write the same file; the later one wins. `--dry-run` writes none.

```jsonc
{
  "schema": "runic.application.tool.dev-session/1",
  "state": "ready",                 // "starting", "ready" (after [dev] Ready) or "stopped"
  "processId": 41250,               // dotnet runic dev itself
  "startedAt": "2026-11-14T18:53:16.2642872+00:00",
  "processStartedAt": "2026-11-14T18:53:15.9010000+00:00", // dev's process start time (UTC), for the liveness check; since 0.20
  "project": "/path/to/App/App.csproj",
  "configuration": "Debug",
  "frontend": {
    "developmentServer": "vite",    // "vite", "angular", or null when a watcher or the build writes the output
    "url": "http://127.0.0.1:5173/",// the development server's origin, null without one
    "port": 5173,
    "stateUrl": "http://127.0.0.1:5173/__runic/state", // when vite-plugin-runic confirms the session, else null
    "directory": "/path/to/App/Frontend",
    "outputDirectory": "/path/to/App/Frontend/dist",
    "generatedDirectory": "/path/to/App/Frontend/src/generated" // the generated TypeScript clients, or null
  },
  "webRoot": "/path/to/App/bin/Debug/net10.0/www",
  // The Bridge generator's development source map that dev passes to the development server for vite-plugin-runic
  // (/__runic/sources), so IDE tools read the file the page uses; null when dev passes none (no development server).
  // The build writes it, so it may not exist before the first build. Since 0.20.
  "sourceMap": "/path/to/App/obj/Debug/net10.0/runic-bridge/sources.json",
  "pages": [
    // Each page the Window can load: its file in the web root and, with a development server, its URL there,
    // which a browser or a JavaScript debugger can open.
    { "document": "index.html", "file": "/path/to/App/bin/Debug/net10.0/www/index.html", "url": "http://127.0.0.1:5173/index.html" }
  ],
  "window": {
    "owner": "dev",                 // "dev", or "ide" with --no-window: the IDE starts the Window
    "program": "App",               // the program dev looks for
    "processId": 41377,             // the running Window process, or null
    "starts": 1,                    // how many Window processes have started in this session
    "environment": {                // what a Window that the IDE starts needs, as dev would set it
      "DOTNET_ENVIRONMENT": "Development"
      // and RUNIC_APPLICATION_DEVELOPMENT_ASSETS without a development server, for Runic Desktop; on Windows since 0.21,
      // RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS with the Runic DevTools extension unless --no-devtools-extension; since 0.22,
      // for CS-WebUI with --no-window, RUNIC_WEBUI_REMOTE_DEBUGGING_PORT="9225" unless --no-debug-endpoint
    }
  },
  // With --browser (since 0.21): dev's address for a browser, and the Windows the running Window process serves,
  // each at its address there (not the Windows' own addresses, which carry their credential); null without --browser.
  // Since 0.23 the address is http://<label>.localhost:9224/ with a session label, and it admits one browser by a
  // one-time code. This file has neither: the URLs have * for the label (a pattern for a launch configuration's
  // urlFilter), and codeFile is the page, readable only by the user who runs dev, that opens the address with the newest
  // code.
  "browser": {
    "url": "http://*.localhost:9224/",
    "codeFile": "/path/to/App/obj/runic/dev-browser.html",
    "windows": [
      { "url": "http://*.localhost:9224/", "type": "App.MainWindow" } // type: the Window's class, null until the host names it
    ]
  },
  "debugEndpoints": [
    // Debugging endpoints of the page's engine; none with --browser. On Windows, -e WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222
    // (or the variable in dev's own environment) lists WebView2's Chrome DevTools Protocol endpoint:
    { "engine": "webview2", "protocol": "cdp", "url": "http://127.0.0.1:9222/" }
    // On Linux since 0.21, dev's CDP adapter for the WebKitGTK Window and WebKit's inspector:// server
    // ("protocol": "webkit-inspector" for WebKit's HTTP inspector with --webkit-inspector):
    // { "engine": "webkitgtk", "protocol": "cdp", "url": "http://127.0.0.1:9222/" },
    // { "engine": "webkitgtk", "protocol": "webkit-remote-inspector", "url": "inspector://127.0.0.1:41653" }
    // Since 0.22, with --debug-endpoint, a CS-WebUI Window's Chrome or Edge while it runs (with --no-window, port 9225
    // for the session):
    // { "engine": "chromium", "protocol": "cdp", "url": "http://127.0.0.1:41625/" }
  ],
  "unavailableDebugEndpoints": [
    // Endpoints the environment asks for but the engine does not open, with the reason. Since 0.20, an elevated dev
    // (run as administrator, so its Window runs elevated too) lists WebView2's here with "reason": "elevated" instead of
    // in debugEndpoints, and prints a [dev] Debugging: hint. Since 0.22, a --no-window CS-WebUI port that something
    // else holds: "reason": "port-taken", or on Linux "foreign-listener" for another user's listener.
  ],
  // The unpacked Runic DevTools extension beside this tool, for Load unpacked or a browser's --load-extension;
  // null when the tool has none. Since 0.21.
  "devtoolsExtension": "/home/me/.nuget/packages/dotnet-runic/0.21.0/tools/net10.0/any/devtools-extension",
  "devtoolsExtensionId": "cddbfcinbkaindmfgcfdgapddkjeadgh", // its fixed id, null without the extension; since 0.21
  // Since 0.24: the frontend packages that the page depends on, dependencies first (Library packages under dev).
  // Empty until dev has read them after the build, and for a page that imports none.
  "dependencies": [
    {
      "package": "@acme/shared-ui",          // its package.json name, or null
      "directory": "/path/to/shared-ui",
      "kind": "compiled",                    // "compiled" (it has a build script) or "source"
      "owner": "/path/to/SharedUi/SharedUi.csproj", // the .NET project that owns it, or null for a workspace package
      "ownerName": "SharedUi",
      "specifier": "@acme/shared-ui/generated", // what a RunicApplicationFrontendClientModule maps to it, or null
      "buildCommand": "bun run --bun build", // null when dev does not build it
      "watchCommand": null,                  // its watch script's command, when dev runs it
      "exports": ["/path/to/shared-ui/dist/index.js"],
      "state": "built",   // "built", "building", "failed", "missing-output", "source" or "not-built" (its build is off)
      "watch": "rebuild", // "script" (its watch script), "rebuild" (dev reruns its build), "vite" (a source package) or "none"
      "message": null     // the last failure while state is "failed"
    }
  ],
  // Since 0.24: why dev stopped, when a failure stopped it (D087), written with "state": "stopped" in the same write;
  // null while dev runs and after a stop without a failure (Ctrl+C, the IDE's stop, a Window that exited normally).
  // The next dev's file starts with null again.
  "failure": {
    "code": "RAPPDEV1018",   // dev's code, as in its diagnostic line and the JSON fault
    "message": "A frontend package that the page depends on failed to build, so dev did not start: /path/to/shared-ui/src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'.",
    "project": "/path/to/SharedUi/SharedUi.csproj", // the .NET project that failed: the one that reported the first error, else the served project
    "package": "/path/to/shared-ui",                // the frontend package that failed, or null; dependencies[] lists it as "failed"
    // The errors and then the warnings of the step that failed (the build, the restore or the install), each once, and
    // dev's own line last, on the served project. At most 100 entries; each message is one line of at most 2,000
    // characters. line and column are 1-based, null when the diagnostic names no position.
    "diagnostics": [
      { "file": "/path/to/shared-ui/src/index.ts", "line": 12, "column": 14, "severity": "error", "code": "TS2322",
        "message": "Type 'string' is not assignable to type 'number'." },
      { "file": "/path/to/App/App.csproj", "line": null, "column": null, "severity": "error", "code": "RAPPDEV1018",
        "message": "A frontend package that the page depends on failed to build, so dev did not start: …" }
    ]
  }
}
```

Paths are absolute. Later versions of the schema only add properties, so a
reader ignores properties it doesn't know; a change that removes or changes one
gets a new schema name. 0.20 added `processStartedAt`, `sourceMap` and
`unavailableDebugEndpoints` within `runic.application.tool.dev-session/1`; 0.21
added the `webkitgtk` endpoints, `devtoolsExtension`, `devtoolsExtensionId`, `browser` and, with
`--no-window` on Linux, `WEBKIT_INSPECTOR_SERVER` in `window.environment`; 0.22 added the `chromium` endpoint and
`RUNIC_WEBUI_REMOTE_DEBUGGING_PORT` in `window.environment`; 0.24 added `dependencies` and `failure`. 0.23 added `browser.codeFile` and is the one exception
to the rule: within `/1` it changed `browser.url` and `browser.windows[].url` from addresses
(`http://127.0.0.1:9224/`) to `*` patterns of the per-session origin (`http://*.localhost:9224/`, D077.15). The address
is secret since 0.23, so the file cannot hold it, and the old value would no longer open the page: a reader opens
`codeFile`, and may use the patterns to match the page's URL.

WebView2 Runtime 150 and later ignores `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`
(and its other `WEBVIEW2_*` variables and current-user policy overrides) in an
elevated process ([Develop secure WebView2 apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)).
`dev` decides from its own token: a Window it starts inherits it, and with
`--no-window` the IDE that runs the `dev` task starts the Window with the same
token. Run `dev` and the IDE without elevation to debug the page's scripts.

## Output and exit codes

Every command writes human-readable text unless you pass `--format json` (or set
`RUNIC_COMMANDLINE_OUTPUT=json`); then standard output is one
`runic.commandline/1` envelope, and the human text of `dev`, `size` and `package`
goes to standard error. `--output json`, the spelling before 0.9, still works as a
hidden alias until 2.0. The same `--format human|json` selects the output of
`runic-create` and `runic-translations`.

A successful envelope carries the command's payload. A failed one carries a
`fault` with a code from the [diagnostics
catalog](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#dotnet-runic),
the code's catalog entry at this release in `fault.helpUri` (since 0.11; human
output prints it after the fault line), and, for `doctor` and `size`, the
payload in `fault.data` when the command got far enough to produce one. A
cancelled command (`RCLI4000`) has no `helpUri`. Human-readable output is not a contract; the
payloads are, under these identifiers:

| Command | Payload |
| --- | --- |
| `dev` | `runic.application.tool.dev/1` |
| `size` | `runic.application.tool.size/1`; the report file is `runic.size/2` |
| `doctor` | `runic.application.tool.doctor/1` |
| `assets pack` | `runic.assets.pack-result/1`, as the asset packer |
| `package <target>` | `runic.application.tool.package/1` (since 0.11) |

`runic.application.tool/1`, the payload of 0.8 and earlier, is gone: its
`Command`, `ExitCode` and `Output` properties carried the human text.

Every Runic tool (`dotnet runic`, `runic-create`, `runic-translations` and the
asset packer) uses the same exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 1 | The command failed on what it found, or something it needs is unavailable, for example a missing or invalid project, a failed build, a failing doctor check or a missing tool. |
| 2 | Usage or validation: the command line itself is wrong, for example an unknown option, an invalid value or two options that exclude each other. |
| 3 | An internal or host failure. Report it with the output. |
| 130 | Cancelled, for example with Ctrl+C. Stopping `dev` with Ctrl+C also exits 130, after the Window and the frontend server have stopped. |

A second Ctrl+C (or SIGQUIT, SIGTERM) while a command is still stopping forces
the exit with 130 (131, 143).

The `dev` payload describes the evaluated project. It is written when `dev`
ends, so it is mainly useful with `--dry-run`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["project", "frontendDirectory", "frontendOutputDirectory", "webRoot", "developmentServer", "dryRun"],
  "properties": {
    "project": { "type": "string", "description": "Absolute path of the project file." },
    "frontendDirectory": { "type": "string", "description": "Absolute path of the frontend package directory." },
    "frontendOutputDirectory": { "type": "string", "description": "Absolute path of the frontend's build output." },
    "webRoot": { "type": "string", "description": "Absolute path of the directory the Window serves." },
    "developmentServer": { "enum": ["vite", "angular", null], "description": "The development server dev starts; null when the frontend is built instead." },
    "dryRun": { "type": "boolean" }
  }
}
```

The `size` payload names the report and repeats its totals:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["report", "runtime", "publishExitCode", "totalBytes", "compressedBytes", "verification"],
  "properties": {
    "report": { "type": "string", "description": "Absolute path of the runic.size/2 report." },
    "runtime": { "type": "string" },
    "publishExitCode": { "type": "integer" },
    "totalBytes": { "type": "integer", "minimum": 0, "description": "Bytes of every published file." },
    "compressedBytes": { "type": "integer", "minimum": 0, "description": "Bytes of the published files as one ZIP archive." },
    "verification": {
      "type": "object",
      "additionalProperties": false,
      "required": ["status", "exitCode"],
      "properties": {
        "status": { "enum": ["not-run", "passed", "failed"] },
        "exitCode": { "type": ["integer", "null"], "description": "The --verify executable's exit code; null when it did not run." }
      }
    }
  }
}
```

## Project properties

The tool reads these optional MSBuild properties. A generated project needs
none of them; the defaults follow the frontend directory. The first three are
build properties of `Runic.Application.Views` too, so `dotnet build` and
`dotnet publish` copy the same output directory to the same web root that `dev`
serves (see its
[build properties](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#build-properties)).

| Property | Default |
| --- | --- |
| `RunicApplicationFrontendDirectory` | `Frontend`; the frontend's `package.json` directory |
| `RunicApplicationFrontendOutputDirectory` | `dist`, relative to the frontend directory |
| `RunicApplicationFrontendWebRoot` | `www`, relative to the build output |
| `RunicApplicationFrontendDevServerKind` | `angular` with `angular.json`, `vite` with a `vite.config.*`, otherwise none |
| `RunicApplicationFrontendViteDevServerEntry` | the first of `/src/main.ts`, `/src/main.tsx`, `/src/main.js`, `/src/main.jsx` below Vite's root; for SvelteKit, which has no main module, its root route: `/src/routes/+page.svelte`, `+layout.svelte`, `+layout.ts` or `+layout.js`. The path is the module's URL path, below Vite's root as Vite serves it |
| `RunicApplicationFrontendViteConfiguration` | the frontend's `vite.config.*` |
| `RunicApplicationFrontendViteRoot` | the frontend directory; set it, relative to the frontend directory, when `vite.config.*` sets `root`, for example `projects/app/src` in an Angular workspace |
| `RunicApplicationFrontendDevServerDocument` | every HTML build input with Vite, `index.html` with Angular and with SvelteKit, whose development server renders it from its routes; set it to override the list and separate several documents with `;` |
| `RunicApplicationFrontendDevWatchTarget` | none; an MSBuild target to run as the frontend watcher without a development server |

Vite resolves the entry module against its `root`, and so does `dev`: with
`root: 'projects/app/src'` and the module `projects/app/src/main.ts`, the entry is
`/main.ts`. `dev` reads the root from the resolved Vite configuration and warns
when it differs from `RunicApplicationFrontendViteRoot`; `doctor`, which does not
run the Vite configuration, checks the entry below `RunicApplicationFrontendViteRoot`.

The package manager comes from `packageManager` in the frontend `package.json`,
then from its lock file.

## Pack an asset archive

`assets pack` writes a directory, usually a frontend build output such as Vite's
`dist`, to a canonical Runic Assets archive. It is the command of the asset
packer that the `Runic.Assets` build runs, with the same arguments, defaults,
`RAS1xxx` codes and `runic.assets.pack-result/1` payload; it needs no project.

```bash
dotnet runic assets pack Client.Web/dist artifacts/app.runic-assets --trusted-generated-output
```

`.git` directories and files whose names start with `.env` are left out unless
you pass `--no-default-excludes`, and a packed source map (`*.map`) is reported
as warning `RAS1005` on standard error. See the
[packer README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/Runic.Assets.Packer/README.md)
for every option.

## Measure size

`size` publishes an application for a required runtime identifier, inventories
all published files, hashes each file, and writes an optional executable-check
result into a JSON report:

```bash
dotnet runic size --project path/to/App.csproj --runtime linux-x64 --report measurements/linux.json
```

As in `doctor`, the runtime identifier is `--runtime`, `--rid` or `-r`. `size`
publishes with Native AOT unless you pass `--no-aot`; `--aot` states the default.
It publishes a self-contained, size-optimized build unless you pass
`--no-self-contained` (since 0.11; `--self-contained` states the default): it
passes `Configuration` (`--configuration`, `Release` by default),
`RuntimeIdentifier`, `PublishAot`, `SelfContained` (`true`, or `false` with
`--no-self-contained`) and `OptimizationPreference=Size` to MSBuild, which
override the project's values; the report lists the effective settings. A
framework-dependent build needs `--no-aot` too, because Native AOT is always
self-contained.

`--property Name=Value` (since 0.11) passes another MSBuild property to the
evaluation and the publish, as `dotnet publish -p` does; repeat it for several.
It comes after the properties above, so `--property OptimizationPreference=Speed`
measures a speed-optimized build. A `;` or `,` in the value is escaped, so each
`--property` sets exactly one property. The properties that have their own
options (`Configuration`, `RuntimeIdentifier`, `PublishAot`, `SelfContained`)
are refused with `RAPPSIZE1006`, and a value that is not `Name=Value` with
`RAPPSIZE1005`. Each property given this way is also listed in the report's
`effectiveProperties`. `--property` has no `-p` short form, because `-p` is
`--project` in every `dotnet runic` command. `-v`, `--verbosity <level>` sets the
publish's MSBuild verbosity.

`--report` names a file that must not exist yet, so `size` never replaces earlier
evidence. Without it, each run writes a new
`runic-size-<runtime>-<UTC time>.json` (for example
`runic-size-linux-x64-20261020T091500Z.json`) in the current directory. Next to
the report, the run keeps `<report name>-<id>/` with `publish/`, `publish.log`,
`distribution.zip` and `verification.log`; the report refers to them, so delete
that directory together with the report.

`--verify` names any executable to run after a successful publish: a path or a
command on `PATH`, such as `node` or `bun`. It is not looked up in the publish
directory. It runs in the project directory and receives the `--verify-argument`
values, then the publish directory and the published application's path. Its
output goes to `verification.log` in the run directory, and a nonzero exit code
fails the command:

```bash
dotnet runic size --runtime linux-x64 --report measurements/linux.json \
  --verify node --verify-argument scripts/check-publish.mjs
# runs: node scripts/check-publish.mjs <publish directory> <publish directory>/App
```

The report's schema is `runic.size/2`. Paths are absolute; `files[].path` is
relative to the publish directory. Values in `effectiveProperties` are the
evaluated MSBuild properties, empty when unspecified, including each
`--property` name:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "required": ["schema", "createdAt", "project", "runtime", "sdk", "buildOperatingSystem", "buildArchitecture", "installedRuntimes", "sourceRevision", "sourceDirty", "effectiveProperties", "libraries", "publishDirectory", "publishExitCode", "publishLogPath", "archivePath", "totalBytes", "compressedBytes", "files", "verification", "notes"],
  "properties": {
    "schema": { "const": "runic.size/2" },
    "createdAt": { "type": "string", "format": "date-time", "description": "UTC, ending in Z." },
    "project": { "type": "string" },
    "runtime": { "type": "string" },
    "sdk": { "type": "string", "description": "dotnet --version." },
    "buildOperatingSystem": { "type": "string" },
    "buildArchitecture": { "type": "string" },
    "installedRuntimes": {
      "type": "array",
      "description": "dotnet --list-runtimes. A line in another shape is kept whole in name.",
      "items": {
        "type": "object",
        "required": ["name", "version", "directory"],
        "properties": { "name": { "type": "string" }, "version": { "type": "string" }, "directory": { "type": "string" } }
      }
    },
    "sourceRevision": { "type": ["string", "null"], "description": "The Git commit of the project directory; null outside Git." },
    "sourceDirty": { "type": ["boolean", "null"] },
    "effectiveProperties": { "type": "object", "additionalProperties": { "type": "string" } },
    "libraries": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Restored libraries by name/version, with their type, such as package or project." },
    "publishDirectory": { "type": "string" },
    "publishExitCode": { "type": "integer" },
    "publishLogPath": { "type": "string" },
    "archivePath": { "type": "string" },
    "totalBytes": { "type": "integer", "minimum": 0 },
    "compressedBytes": { "type": "integer", "minimum": 0 },
    "files": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["path", "category", "bytes", "sha256"],
        "properties": {
          "path": { "type": "string" },
          "category": { "type": "string", "description": "A heuristic, such as main-executable, native-dependency, debug-symbols or other-rid; new categories may appear." },
          "bytes": { "type": "integer", "minimum": 0 },
          "sha256": { "type": "string" }
        }
      }
    },
    "verification": {
      "type": "object",
      "required": ["status", "exitCode", "executable", "arguments"],
      "properties": {
        "status": { "enum": ["not-run", "passed", "failed"] },
        "exitCode": { "type": ["integer", "null"] },
        "executable": { "type": ["string", "null"] },
        "arguments": { "type": "array", "items": { "type": "string" } }
      }
    },
    "notes": { "type": "array", "items": { "type": "string" } }
  }
}
```

`runic.size/1`, the report of 0.8 and earlier, named `createdUtc`, `publishLog`
and `archive`, and kept `installedRuntimes` as one string.

A missing `--runtime` (`RAPPSIZE1001`), `--verify-argument` without `--verify`
(`RAPPSIZE1002`) or an invalid `--property` (`RAPPSIZE1005`, `RAPPSIZE1006`) is
a usage error, exit code 2. An existing `--report` (`RAPPSIZE1003`), a project
whose publish settings MSBuild cannot evaluate (`RAPPSIZE1004`), a failed
publish or a failed verification exits 1.

## Write a web bundle

`bundle` (since 0.24, split CI, D082) builds the frontends into a **web bundle**
(`rax.web-bundle/1`) that a publish on another machine takes with
`package <target> --frontend-bundle`, for a CI whose Linux jobs build and test
everything web and whose Windows or macOS job only publishes:

```bash
dotnet runic bundle --output-directory artifacts/web-bundle   # on Linux, with Node.js or Bun
dotnet runic package windows --frontend-bundle artifacts/web-bundle   # on Windows, without them
```

It runs `dotnet build -c Release` with
`RunicApplicationFrontendBundleDirectory`, so every project that declares
Views, the application's references included, writes its entry. Then it checks
the bundle and prints one line per project: its kind, its Views, and the
files, size and SHA-256 of its page. `--project` (`-p`) takes a project, a
solution (`.sln`, `.slnx`) or a directory with one; `--configuration` (`-c`)
is `Release` by default; `--property` (`-p:NAME=VALUE`) passes MSBuild
properties to the build. `--output-directory` (`-o`, `artifacts/web-bundle`
beside the project or solution by default) must be new, empty, or an earlier
bundle, whose entries `bundle` deletes so that none stays in the manifest. A
folder with other files is refused (`RAPPBUNDLE1001`). A failed build is
`RAPPBUNDLE1002`, and a bundle that does not check is `RAPPBUNDLE1003`, for
example when a project's frontend build is off (`RUNICBRIDGE092`). The payload
of `--format json` is `runic.application.tool.bundle/1`:

```json
{
  "directory": "/repo/artifacts/web-bundle",
  "format": "rax.web-bundle/1",
  "projects": [
    { "assembly": "Acme.App", "kind": "application", "views": 2, "files": 12, "bytes": 184320,
      "sha256": "7f3c…", "host": "desktop", "nodeEnv": "production" }
  ]
}
```

`sha256`, `host` and `nodeEnv` are null when the entry has none, as for a client
library, which has no page. The format of the bundle is in
[specs/application/web-bundle.md](https://github.com/Runic-Artifex/runic-sdk/blob/main/specs/application/web-bundle.md),
and the build properties in
[Building the frontend in another job](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#building-the-frontend-in-another-job).

## Package the application

`package <target>` (since 0.11) publishes the application with Native AOT and
packages it for one operating system. It runs on that operating system and uses
the platform's own packaging tools:

```bash
dotnet runic package windows    # MSIX, on Windows with the Windows SDK
dotnet runic package macos      # .app in a disk image, on macOS with Xcode's command line tools
dotnet runic package flatpak    # Flatpak bundle, on Linux with flatpak-builder
dotnet runic package appimage   # AppImage, on Linux with appimagetool
dotnet runic package deb        # Debian package, on Linux with dpkg-deb
dotnet runic package rpm        # rpm, on Linux with rpmbuild (also on Debian and Ubuntu)
dotnet runic package flathub --git-url https://github.com/example/notes.git --git-tag v1.2.0   # Flathub manifest, on any OS
```

The [packaging guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/packaging.md)
lists each format's tools and how to sign with your own credentials.

Every target takes `--project` (`-p`: a project, or a solution whose one Window
project it packages, as described at the top), `--configuration` (`-c`, `Release` by
default), `--runtime` (`--rid`, `-r`; the host's architecture by default) and
`--output-directory` (`-o`; `artifacts/package/<target>` beside the project by
default). The output directory is not `--output`, which stays the hidden alias
of `--format`. A target of another operating system, or a runtime identifier of
another operating system, several runtime identifiers, or a runtime identifier
that Runic does not support (`osx-x64`), is refused with `RAPPPKG1004` before
anything is built.

Since 0.24 every target except `flathub` also takes:

- `--property NAME=VALUE`, or `-p:NAME=VALUE` as for `dotnet publish` (also
  `-property:` and `/p:`), repeated for several properties. The identity's
  evaluation and the publish receive them, so a CI job can set `ApplicationId`,
  `Version` or `RunicApplicationHost` without editing the project. `-p` with a
  separate value stays `--project`, as for `dotnet run`. `Configuration`,
  `RuntimeIdentifier` and `PublishAot` have their own options, and
  `RunicApplicationFrontendPrebuiltDirectory` is `--frontend-bundle`
  (`RAPPPKG1008`, exit code 2).
- `--frontend-bundle <dir>`: publish with the pages of a web bundle that
  [`dotnet runic bundle`](#write-a-web-bundle) wrote on another machine,
  through `RunicApplicationFrontendPrebuiltDirectory`. The publish runs no
  frontend install or build, so the runner needs neither Node.js nor Bun.
  Before it publishes, `package` checks the bundle as `doctor --bundle` does
  (`RAPPPKG1008` for a missing or truncated one). The build then compares each
  View's fingerprint with its own (`RUNICBRIDGE094`) and the host
  (`RUNICBRIDGE095`).

```bash
dotnet runic package windows --frontend-bundle web-bundle -p:Version=1.4.0
```

The package's identity comes from the project file:

| Property | Use |
| --- | --- |
| `ApplicationId` | The package name, bundle identifier and Flatpak ID: a reverse-DNS name of ASCII letters and digits, each part starting with a letter, at most 50 characters (`RAPPPKG1001` otherwise) |
| `ApplicationTitle` | The display name; the assembly name by default |
| `Version` | The package version; MSIX and `Info.plist` use its numeric part (`1.2.3-preview.4` becomes `1.2.3.0` and `1.2.3`) |
| `Description`, `PackageProjectUrl` | The package description and homepage |
| `RunicApplicationPublisher` | The MSIX `Publisher`, which must equal the signing certificate's subject; `--publisher` overrides it |
| `RunicApplicationUrlSchemes`, `RunicApplicationFileTypes`, `RunicApplicationWebLinkHosts`, `RunicApplicationDBusActivatable` | The URL schemes, file types, web link hosts and D-Bus activation each package declares |
| `RunicPackageIcon` | The 512 px PNG icon; `icons/icon.png` by default. An `icon.svg` beside it is the scalable icon on Linux |
| `RunicPackageName` | The deb and rpm package name; the lower-cased assembly name by default, with characters a package name cannot have replaced by `-` |
| `PackageLicenseExpression` | The rpm `License`; `LicenseRef-proprietary` by default |
| `RunicMacSandbox` | `true` signs the macOS bundle for the App Sandbox, as `--sandbox` does; off by default |
| `RunicMacApplicationCategory` | The macOS `LSApplicationCategoryType`, a `public.app-category.*` identifier, which the Mac App Store requires; none by default (`RAPPPKG1001` for another value) |
| `RunicPackagingDirectory` | The manifest overrides; `packaging/` by default |

The tool embeds the manifests it fills: `windows/AppxManifest.xml`,
`macos/Info.plist`, `macos/entitlements.plist`, `linux/app.desktop`,
`linux/app.metainfo.xml`, `linux/app.service` and `linux/flatpak.yml`. A file
with the same path in the packaging directory replaces the embedded one, so the
manifests a `--packaging` project of the `runic-app` template copies are
editable. Both fill `{{Field}}` from the identity; a field the target does not
fill is `RAPPPKG1006`.

The packages are unsigned or ad-hoc signed: install them to test them, and sign
them with your own credentials to ship them. Runic signs nothing for you.

- **`windows`** writes the publish folder with `AppxManifest.xml` to `msix/` and
  packs `<ApplicationId>_<version>_<arch>.msix` with `makeappx`. The manifest
  declares the URL schemes, file types (several selected files reach one
  process), app URI handlers and the login startup task. `--certificate app.pfx`
  signs with a PFX file whose password is in `RUNIC_SIGNING_PASSWORD`, and
  `--thumbprint <sha1>` with a certificate from the store; `--timestamp-url`
  adds an RFC 3161 timestamp. `makeappx` and `signtool` are found on `PATH` or
  in the newest Windows SDK folder.
- **`macos`** builds `<ApplicationTitle>.app` (the executable and other Mach-O
  files in `Contents/MacOS`, the frontend in `Contents/Resources/www`, none when
  the application embeds it, other published files in `Contents/Resources` with
  a link in `Contents/MacOS`, and an `.icns` made with `sips` and `iconutil`),
  signs each nested library and executable, then the bundle with the hardened
  runtime and `macos/entitlements.plist`, verifies it with
  `codesign --verify --deep --strict` and writes `<ApplicationTitle>-<version>.dmg`.
  Without `--sign` the bundle is ad-hoc signed. `--sign "Developer ID
  Application: …"` signs the bundle and the disk image with your identity, and
  `--notary-profile <name>` then notarizes and staples the disk image with a
  `notarytool store-credentials` profile. It publishes for `osx-arm64`; Runic
  does not target Intel Macs, so `--runtime osx-x64` and universal bundles
  are refused with `RAPPPKG1004`. `--sandbox` (or `RunicMacSandbox`) signs it
  for the App Sandbox: `app-sandbox`, `network.server` and `network.client`
  for the loopback page server and bridge, `files.user-selected.read-write`
  and `files.bookmarks.app-scope`, filled into the `{{Entitlements}}` field of
  `macos/entitlements.plist`. `--provisioning-profile <file>` (with `--sign`)
  embeds the profile as `Contents/embedded.provisionprofile` and signs with its
  application and team identifiers. `--pkg` also writes
  `<ApplicationTitle>-<version>.pkg` with `productbuild`, which installs the
  bundle in `/Applications`, and `--installer-sign <identity>` signs it with an
  installer certificate; with `--notary-profile` a signed `.pkg` is notarized
  too. The command uploads nothing to App Store Connect.
- **`flatpak`** publishes into `flatpak-source/`, writes `<ApplicationId>.yml`
  from `linux/flatpak.yml` on the GNOME runtime (which provides GTK 4 and
  WebKitGTK 6, so the project uses `Runic.Desktop.Gtk4`), validates the desktop
  entry and metainfo with `desktop-file-validate` and `appstreamcli`, builds
  with `flatpak-builder --install-deps-from=flathub` and writes
  `<ApplicationId>.flatpak` with a repository in `repo/`. `--gpg-sign <key>`
  signs both with your key.
- **`appimage`** builds an AppDir (the publish folder in `usr/lib/<ApplicationId>`,
  `AppRun`, the desktop entry and icon at its root, the metainfo as
  `<ApplicationId>.appdata.xml`) and makes `<ApplicationTitle>-<version>-<arch>.AppImage`
  (spaces become `_`) with `appimagetool` from `PATH`. It passes appimagetool a
  local AppImage runtime, `runtime-<arch>` from the
  [type2-runtime releases](https://github.com/AppImage/type2-runtime/releases)
  beside appimagetool or the file `RUNIC_APPIMAGE_RUNTIME` names, so nothing is
  downloaded while packaging. The AppImage uses the system's GTK and WebKitGTK,
  so the target system needs them installed; it is not D-Bus activatable,
  because it has no fixed path.
- **`deb`** and **`rpm`** stage the same tree: the publish folder in
  `/usr/lib/<ApplicationId>/`, a `/usr/bin/<AssemblyName>` link, the desktop
  entry with the URL schemes and file types as `MimeType=`, the hicolor icons,
  the metainfo, the file types' MIME definitions and, with
  `RunicApplicationDBusActivatable`, the D-Bus service file. The package name is
  `RunicPackageName`, and a prerelease version sorts before its release
  (`1.2.3-preview.4` becomes `1.2.3~preview.4`). The deb depends on `libc6,
  libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0`, or on `libc6, libgtk-4-1,
  libwebkitgtk-6.0-4` when the project references `Runic.Desktop.Gtk4`, and is
  built with `dpkg-deb --root-owner-group`. The rpm requires `webkit2gtk4.1` or
  `webkitgtk6.0` with automatic dependencies off (the binary loads GTK with
  `dlopen`, so rpm would not find them), takes its `License` from
  `PackageLicenseExpression` and is built with `rpmbuild -bb`. Both validate the
  desktop entry with `desktop-file-validate` when it is installed.
- **`flathub`** writes a Flathub source-build manifest for your own submission:
  `<ApplicationId>.yml` on the GNOME runtime with the `dotnet10` and, for a
  frontend, `node24` SDK extensions, a `git` source at `--git-url` and
  `--git-commit` or `--git-tag`, the desktop entry and metainfo, and the offline
  sources `nuget-sources.json` and `npm-sources.json`. The command generates the
  sources from the committed lock files: the NativeAOT NuGet lock file
  (`NuGetLockFilePath` with `PublishAot=true`, whose `RuntimeIdentifiers` must list
  `linux-x64` and `linux-arm64`), the runtime packs the SDK downloads besides it,
  and the frontend's `package-lock.json` (npm only). It restores the NativeAOT
  graph for both runtime identifiers in locked mode to take each package's
  SHA-512 from the NuGet cache, checked against the lock file's content hash
  first, and pins the build's `RuntimeFrameworkVersion` to the lock file's
  NativeAOT compiler, so a newer SDK extension restores the same packages.
  It runs on any operating system; `--build` then builds the manifest on Linux as
  Flathub does (runtime and extensions, then every source, then the build without
  network access) and writes `<ApplicationId>.flatpak`. A missing commit or
  tag, lock file, hash or runtime identifier is `RAPPPKG1007`.

Each target lists what it wrote in the `runic.application.tool.package/1`
payload. `signing` is `none`, `adhoc`, `test` (a self-signed certificate, as in
CI) or `identity` (your certificate or Developer ID):

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["target", "identity", "packages", "warnings"],
  "properties": {
    "target": { "enum": ["windows", "macos", "flatpak", "appimage", "deb", "rpm", "flathub"] },
    "identity": {
      "type": "object",
      "additionalProperties": false,
      "required": ["id", "version"],
      "properties": { "id": { "type": "string" }, "version": { "type": "string" } }
    },
    "packages": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["path", "format", "signing"],
        "properties": {
          "path": { "type": "string", "description": "Absolute path of the package file or directory." },
          "format": { "type": "string", "description": "msix, app, dmg, pkg, flatpak, appimage, deb, rpm or flathub-manifest." },
          "signing": { "enum": ["none", "adhoc", "test", "identity"] }
        }
      }
    },
    "warnings": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["code", "message"],
        "properties": {
          "code": { "type": "string", "description": "A RAPPPKG2xxx code, such as RAPPPKG2001 (unsigned) or RAPPPKG2002 (not notarized); an open set." },
          "message": { "type": "string", "description": "Human-readable; match the code instead." }
        }
      }
    }
  }
}
```

Warnings, such as an unsigned package, do not fail the command: human output
prints `warning RAPPPKG2001: …` lines and the payload lists them with their
codes ([`RAPPPKG2001`–`2006`](https://github.com/Runic-Artifex/runic-sdk/blob/main/docs/diagnostics.md#rapppkg2001)).
A target or runtime of another operating system (`RAPPPKG1004`) is a usage
error, exit code 2. An invalid identity in the project (`RAPPPKG1001`), a
missing packaging tool (`RAPPPKG1002`), a failed publish (`RAPPPKG1003`), a
failing packaging tool (`RAPPPKG1005`), a failed validation (`RAPPPKG1006`) or
Flathub sources that cannot be generated (`RAPPPKG1007`) exits 1.

## Tool extensions

`dotnet runic translations …` runs the Runic Translations tool,
`runic-translations …`, with the arguments unchanged, and exits with its exit
code: `dotnet runic translations compile` is `runic-translations compile`, and
`dotnet runic translations --help` is the Translations tool's help. A leading
`--format` (or its alias `--output`) is passed on as `--format`. Added in 0.24.

`dotnet runic` finds the tool in this order:

1. `RAX_TRANSLATIONS_TOOL`, the path of a `runic-translations` program, for
   example a local build. It is not checked first: a wrong path fails with
   `RAPPCLI1014` instead of running another installation.
2. The nearest .NET tool manifest (`.config/dotnet-tools.json` or
   `dotnet-tools.json`, up to the one marked `isRoot`) that lists
   `dotnet-runic-translations` or the command `runic-translations`: it runs
   `dotnet tool run runic-translations -- …`.
3. `node_modules/.bin/runic-translations` in the current directory or above.
4. `runic-translations` on `PATH`: a global tool or a tool path.

Without one, the command fails with `RAPPCLI1013` and prints the command that
installs the Translations release of this compatibility set, with `--local`
when a tool manifest exists, for example:

```text
dotnet tool install dotnet-runic-translations --version 0.16.0 --local
```

It never installs anything itself. `doctor` checks the version of the installed
tool ([`extensions`](#extensions)). Only first-party extensions dispatch: any
other word after `dotnet runic` is still an application argument of `dev`.

## Support envelopes

The local support envelope of the Translations editor moved to the
Translations tool in 0.9:

```bash
runic-translations support preview --editor-diagnostics /path/to/editor-diagnostics.zip
```

`dotnet runic support --mode preview|collect|remove`, its former spelling, is a
hidden alias until 2.0. It forwards its arguments to `runic-translations
support <mode>`, found as `dotnet runic translations` finds it
([Tool extensions](#tool-extensions)), which then writes the output and
decides the exit code. Without that tool it fails with `RAPPCLI1012`.

Preview tool; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
