# dotnet-runic

`dotnet-runic` checks and coordinates a Runic Views Window project. Generated
projects pin the tool locally, so the first run of a new project is:

```bash
dotnet tool restore
dotnet runic dev
```

All commands find the single `.csproj` in the current directory; pass
`--project path/to/App.csproj` (`-p`) otherwise. `--configuration` (`-c`)
selects the build configuration. `dotnet runic <command> --help` describes
every option.

`dev` requires `RunicApplicationFrontendWindowProject=true` and a CS-WebUI
(`Runic.Application.Views.CsWebUi`) or Runic Desktop (`Runic.Application.Views.Desktop`)
host. It restores the .NET and JavaScript dependencies, builds the project, and
runs the native Window alongside the frontend's Vite or Angular development
server. While the development server runs, the build skips the production
frontend build and leaves the development document in `www/`
(`RunicApplicationFrontendBuildEnabled=false`, `RunicApplicationFrontendCopyEnabled=false`); the Views
MSBuild targets still generate the typed TypeScript clients. `dotnet watch`
restarts the Window after C# edits. `--no-restore`, `--no-frontend-watch`,
`--no-dotnet-watch`, and `--dry-run` select parts of that loop. `dev` installs
the frontend packages itself and passes
`RunicApplicationFrontendInstallEnabled=false`, so the build does not install
them a second time (and `RunicApplicationFrontendDelivery=Files`, so a project
that embeds its frontend serves it live while developing); with `--no-restore` nothing
installs them, and a frontend that declares dependencies but has no
`node_modules` stops with `RAPPDEV1008`. When `dotnet watch` restarts or `dev`
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
Ctrl+C or SIGTERM stops `dev` the way closing the last window stops the
application: the Window gets SIGTERM on Linux and macOS, or Ctrl+C on Windows,
and up to 5 seconds to close its windows and finish its work before `dev` kills
it. A restart after a C# or frontend compiler edit gets the same grace;
`dev` sets `DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS=5000` for `dotnet watch`
unless you set it yourself.

`dev` picks a free loopback port for the development server, and on a shared
machine another process could take that port first. Since 0.11, `dev` passes
the development server process a random value for the session and accepts the
server as ready only when its `/__runic/state` answers with that value, so
`dev` never writes a page that another process served into the web root.
`@runic-artifex/vite-plugin-runic` answers it, and every frontend that
`dotnet new runic-app` generates is set up for it:

| Frontend | What answers `/__runic/state` | When `dev` requires the answer |
| --- | --- | --- |
| React, Vue, Svelte | `runic()` in `vite.config.ts` | The resolved Vite configuration has `runic()` or `runicDevSession()` |
| SvelteKit | `runicDevSession()` in `vite.config.ts`, next to `sveltekit()` (SvelteKit warns about `runic()`, whose `transformIndexHtml` hook it does not support) | The same |
| Angular | `runicDevSessionProxy()` in `proxy.conf.mjs`, the serve target's `proxyConfig` in `angular.json` (the Angular CLI loads no Vite plugins) | `package.json` declares `@runic-artifex/vite-plugin-runic` |

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
| `--launch-profile <name>` | Runs the Window with that profile from `Properties/launchSettings.json`, so its `environmentVariables` and `commandLineArgs` apply, under `dotnet watch` too. Without it the Window runs with `--no-launch-profile`, as before. A profile's variables replace `dev`'s defaults and `-e` values of the same name. |
| `--no-build` | Skips `dev`'s own .NET restore and build, as `dotnet run --no-build` does, and starts from the existing build output; build the project with the same `--configuration` first. The frontend packages are still installed unless you pass `--no-restore`. `dotnet watch` still builds after C# edits. |
| `-e`, `--environment NAME=VALUE` | Sets an environment variable for the Window process, as `dotnet run -e` does; repeat it for several. It wins over the defaults in the table below, so `-e DOTNET_ENVIRONMENT=Staging` runs the Window in `Staging`. The variables `dev` uses to describe the development server (`RUNIC_APPLICATION_*` below) are refused with `RAPPDEV1012`, and a value without `=` with `RAPPDEV1011`. `--dry-run` prints the names, never the values. |
| `-v`, `--verbosity <level>` | Passes the MSBuild verbosity (`q[uiet]`, `m[inimal]`, `n[ormal]`, `d[etailed]` or `diag[nostic]`) to the restore, the build and `dotnet watch`. |

`dev` passes these variables to the Window process:

| Variable | Value |
| --- | --- |
| `DOTNET_ENVIRONMENT` | `Development`, unless you set it or `ASPNETCORE_ENVIRONMENT`, in the environment or with `-e` |
| `DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS` | `5000`, unless you set it |
| `DOTNET_WATCH_RESTART_ON_RUDE_EDIT` | `1` |
| `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` | Without a development server, the absolute frontend output directory; cleared while one runs. An internal contract with the Runic Desktop host, which reads it only in a `Development` environment. |
| `RUNIC_APPLICATION_VITE_*`, `RUNIC_APPLICATION_FRONTEND_*`, `RUNIC_APPLICATION_DEV_PROJECT` | The development server's address, entry and compiler integration while one runs; cleared otherwise. Internal. |

`doctor` checks the Views Window opt-in, the .NET SDK, the declared JavaScript
runtime and package manager, the matching lock file, the configured
development-server inputs, and, once the project is restored, that every Runic
package belongs to one release train. An unrestored project gets a warning that
tells you to run `dotnet runic dev` or `dotnet restore`. The browser check
follows the referenced host: a Runic Desktop project needs no browser, and a
missing browser is only a warning for a CS-WebUI project because CS-WebUI falls
back to the platform WebView. Only browser smoke checks require Chromium.
The JavaScript runtime passes at the certified version and warns for a newer
release of the same major version. Node 26 is supported as well, with a
warning, since the starters and the Runic npm packages that run in Node declare
`engines.node` `^24.18.0 || ^26.0.0` (0.11).

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
| `target-publish` | Without Native AOT, says what target machines need: nothing for a self-contained publish, otherwise the .NET runtime, plus the ASP.NET Core runtime for Runic Desktop. With `PublishAot=true`, fails when the target is another operating system, because Native AOT does not cross operating systems. On the same operating system it checks the native toolchain: clang or gcc and, unless `StripSymbols` is `false`, objcopy on Linux, the Xcode command-line tools on macOS, and the Visual Studio C++ tools for the target architecture on Windows (through `vswhere`). A Linux target with another architecture or C library warns that it needs a sysroot. |
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
        "failed": { "type": "integer", "minimum": 0 }
      }
    },
    "checks": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["id", "status", "message", "remediation"],
        "properties": {
          "id": { "type": "string", "description": "Stable check identifier, for example dotnet-sdk, lock-file or browser." },
          "status": { "enum": ["pass", "warn", "fail"] },
          "message": { "type": "string" },
          "remediation": { "type": ["string", "null"], "description": "How to fix a warn or fail check; null when nothing is needed." }
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
Check identifiers are `dotnet-sdk`, `views-window`, `javascript-runtime`,
`package-manager`, `lock-file`, `compatibility-set`, `views-package-versions`,
`application-id`, `frontend-dev-server`, `vite-config`, `vite-entry`,
`browser`, `views-hosts`, `desktop-runtime`, `wpf-runtime`, `gtk4-profile` and
`platform-services`; `views-package-versions` and `application-id` were added
in 0.11. New identifiers may appear in later releases. With `--runtime`, the payload also lists
`target-rid`, `target-runtime-identifiers`, `target-publish` and
`target-presentation`.

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
| 1 | The command failed, or something it needs is unavailable, for example a failed build, a failing doctor check or a missing tool. |
| 2 | Usage or validation: an unknown option, a missing project or an invalid value. |
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
| `RunicApplicationFrontendViteDevServerEntry` | the first of `/src/main.ts`, `/src/main.tsx`, `/src/main.js`, `/src/main.jsx`; for SvelteKit, which has no main module, its root route: `/src/routes/+page.svelte`, `+layout.svelte`, `+layout.ts` or `+layout.js` |
| `RunicApplicationFrontendViteConfiguration` | the frontend's `vite.config.*` |
| `RunicApplicationFrontendDevServerDocument` | every HTML build input with Vite, `index.html` with Angular and with SvelteKit, whose development server renders it from its routes; set it to override the list and separate several documents with `;` |
| `RunicApplicationFrontendDevWatchTarget` | none; an MSBuild target to run as the frontend watcher without a development server |

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
(`RAPPSIZE1002`), an existing `--report` (`RAPPSIZE1003`) or an invalid
`--property` (`RAPPSIZE1005`, `RAPPSIZE1006`) is a usage error, exit code 2. A project whose publish settings MSBuild cannot evaluate
(`RAPPSIZE1004`), a failed publish or a failed verification exits 1.

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

Every target takes `--project` (`-p`), `--configuration` (`-c`, `Release` by
default), `--runtime` (`--rid`, `-r`; the host's architecture by default) and
`--output-directory` (`-o`; `artifacts/package/<target>` beside the project by
default). The output directory is not `--output`, which stays the hidden alias
of `--format`. A target of another operating system, or a runtime identifier of
another operating system, is refused with `RAPPPKG1004` before anything is built.

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
- **`macos`** builds `<ApplicationTitle>.app` (the executable in
  `Contents/MacOS`, the frontend in `Contents/Resources/www`, none when the
  application embeds it, and an `.icns` made with `sips` and `iconutil`), signs
  it with the hardened runtime and `macos/entitlements.plist`, verifies it with
  `codesign --verify --strict` and writes `<ApplicationTitle>-<version>.dmg`.
  Without `--sign` the bundle is ad-hoc signed. `--sign "Developer ID
  Application: …"` signs the bundle and the disk image with your identity, and
  `--notary-profile <name>` then notarizes and staples the disk image with a
  `notarytool store-credentials` profile. `--runtime osx-arm64,osx-x64` makes a
  universal binary with `lipo`.
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
          "format": { "type": "string", "description": "msix, app, dmg, flatpak, appimage, deb, rpm or flathub-manifest." },
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
An invalid identity (`RAPPPKG1001`) or a target or runtime of another
operating system (`RAPPPKG1004`) is a usage error, exit code 2. A missing
packaging tool (`RAPPPKG1002`), a failed publish (`RAPPPKG1003`), a failing
packaging tool (`RAPPPKG1005`) or a failed validation (`RAPPPKG1006`) exits 1.

## Support envelopes

The local support envelope of the Translations editor moved to the
Translations tool in 0.9:

```bash
runic-translations support preview --editor-diagnostics /path/to/editor-diagnostics.zip
```

`dotnet runic support --mode preview|collect|remove`, its former spelling, is a
hidden alias until 2.0. It forwards its arguments to `runic-translations
support <mode>`, run from `PATH` or from a local tool manifest that lists
`dotnet-runic-translations`, which then writes the output and decides the exit
code. Without that tool it fails with `RAPPCLI1012`.

Preview tool; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
