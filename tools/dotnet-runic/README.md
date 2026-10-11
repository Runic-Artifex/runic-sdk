# dotnet-runic

`dotnet-runic` checks and coordinates a Runic Views Window project. Generated
projects pin the tool locally, so the first run of a new project is:

```bash
dotnet tool restore
dotnet runic dev
```

Both commands find the single `.csproj` in the current directory; pass
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
them a second time; with `--no-restore` nothing
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
`new DesktopContent.Directory(www, "settings.html")`) loads in `dev` exactly
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

`doctor` checks the Views Window opt-in, the .NET SDK, the declared JavaScript
runtime and package manager, the matching lock file, the configured
development-server inputs, and, once the project is restored, that every Runic
package belongs to one release train. An unrestored project gets a warning that
tells you to run `dotnet runic dev` or `dotnet restore`. The browser check
follows the referenced host: a Runic Desktop project needs no browser, and a
missing browser is only a warning for a CS-WebUI project because CS-WebUI falls
back to the platform WebView. Only browser smoke checks require Chromium.
A project that references `Runic.Desktop.Gtk4` also gets a `gtk4-profile` check.
It warns once and lists every missing piece: `Runic.Platform.Linux.Gtk4`, which
creates the portal parent window and the GTK 4 clipboard;
`Runic.Platform.Linux.Portal`, which can also come transitively, for example
through `Runic.Platform.Linux`, once restored; and, on a Linux machine,
`libgtk-4.so.1`, `libwebkitgtk-6.0.so.4` (or `.so.0`) and a GTK older than
4.12. The remediation names the distribution's packages for Debian/Ubuntu,
Fedora, Arch, openSUSE and NixOS, read from `/etc/os-release`, and the library
file names on other distributions. The check also warns when the project
restores `Runic.Platform.Linux`: its GTK 3 portal parent loads `libgtk-3`, and a
GTK 4 process must not load GTK 3. With `--rid`, `target-presentation` reports
the native libraries instead. A project on `Runic.Application.Views.Desktop` gets the
platform providers from that package, and `services.AddRunicPlatformServices()`
selects the GTK 4 one. For such a project the check lists only the native
libraries, and warns about `Runic.Platform.Linux` only when the project
references it directly.

A project on `Runic.Application.Views.Desktop` or `Runic.Application.Views.CsWebUi` also
gets a `platform-services` check. It names the provider that
`AddRunicPlatformServices()` selects on this machine. On Linux, it warns when
there is no D-Bus session bus or no installed `xdg-desktop-portal` (the
`org.freedesktop.portal.Desktop` D-Bus service in `XDG_DATA_HOME` or
`XDG_DATA_DIRS`), because the file dialogs and the file launcher use the portal.
For CS-WebUI it reports that the services return `OwnerUnavailable`. The check
describes this machine, so `--rid` skips it.

### Deployment checks for a target

`dotnet runic doctor --rid <rid>` (also `--runtime` or `-r`) adds checks for
publishing to a runtime identifier. Doctor evaluates the project with the
global properties of `dotnet publish -r <rid>` (`RuntimeIdentifier=<rid>` and
`_IsPublishing=true`), and in Release unless you pass `-c`, so RID-, publish- and
configuration-conditioned `PublishAot`, `PublishSelfContained` and
`SelfContained` settings apply. Doctor does not see properties passed to
`dotnet publish` on its command line; `--aot`, `--no-aot` and
`--self-contained` check a publish with `-p:PublishAot=true`,
`-p:PublishAot=false` or `--self-contained true`. A malformed RID, or `--aot`,
`--no-aot` or `--self-contained` without `--rid`, is a usage error
(`RAPPCLI1011`, exit code 2).

| Check | What it reports |
| --- | --- |
| `target-rid` | Reads the support matrix embedded in the compatibility set, which is generated from `eng/support.json` (see [Supported platforms](https://github.com/Runic-Artifex/runic-sdk/blob/main/README.md#supported-platforms)). Passes for CI-verified RIDs of the project's host (`linux-x64`, `win-x64` and `osx-arm64`), where Runic CI builds and runs the host's native window layer for changes that affect it. Warns, with the reason, for RIDs with packaged native support but no CI coverage. Fails for RIDs that the host does not support, such as `win-arm64` and the musl RIDs with CS-WebUI, whose Linux library needs glibc 2.34; for RIDs the matrix does not list; and for version-specific RIDs such as `win10-x64`. Without a known host, doctor assumes the most permissive host and says so. After a failure doctor skips the other target checks. |
| `target-runtime-identifiers` | Warns when the project declares `RuntimeIdentifiers` without the target, which breaks `dotnet publish --no-restore`. |
| `target-publish` | Without Native AOT, says what target machines need: nothing for a self-contained publish, otherwise the .NET runtime, plus the ASP.NET Core runtime for Runic Desktop. With `PublishAot=true`, fails when the target is another operating system, because Native AOT does not cross operating systems. On the same operating system it checks the native toolchain: clang or gcc and, unless `StripSymbols` is `false`, objcopy on Linux, the Xcode command-line tools on macOS, and the Visual Studio C++ tools for the target architecture on Windows (through `vswhere`). A Linux target with another architecture or C library warns that it needs a sysroot. |
| `target-presentation` | Names the native runtime that target machines need. Runic Desktop needs the WebView2 Runtime on Windows, GTK 3 and WebKitGTK 4.1 on Linux (GTK 4.12 or newer and WebKitGTK 6.0 when the project references `Runic.Desktop.Gtk4`), and WKWebView, which macOS includes. CS-WebUI needs an installed browser and falls back to the same platform WebView. When the target has the host's operating system, doctor also inspects this machine and warns when the runtime is missing. It reads the GTK 4 version through `pkg-config` when available. Doctor cannot inspect another operating system, so it reports those requirements as passing. |

Run doctor with `--rid` on the machine or CI runner that publishes, and once for
each target:

```bash
dotnet runic doctor --rid linux-x64 --fail-on fail
dotnet runic doctor --rid win-x64 --aot --fail-on fail
```

### Doctor JSON output

`dotnet runic doctor --output json` (or `RUNIC_COMMANDLINE_OUTPUT=json`) writes
one `runic.commandline/1` envelope. A `runic.commandline/1` envelope carries a
payload only when it succeeds. Errors that stop the inspection, such as a missing
project, produce a failed envelope with a `fault` and no payload.

`--fail-on <never|fail|warn>` selects when doctor fails, with the same meaning in
both output modes, and defaults to `fail` in both. When a check reaches the
threshold, doctor exits with code 1. In JSON mode that is a failed envelope with
no payload: the fault has code `RAPPCLI1009` and `details` mapping each
non-passing check id to `fail` or `warn`, and the diagnostics mark the checks at
the threshold as errors.

A failing check therefore exits 1 in JSON mode too. To read every check from
the payload even when some fail, pass `--fail-on never` and decide on
`payload.healthy`:

```bash
dotnet runic doctor --output json > doctor.json
dotnet runic doctor --output json --fail-on never | jq -e '.payload.healthy'
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
    "host": { "enum": ["cswebui", "desktop", "unknown"], "description": "The referenced Runic Views host." },
    "target": { "type": ["string", "null"], "description": "The --rid runtime identifier; null without --rid." },
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
    }
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
`package-manager`, `lock-file`, `compatibility-set`, `frontend-dev-server`,
`vite-config`, `vite-entry` and `browser`. With `--rid`, the payload also lists
`target-rid`, `target-runtime-identifiers`, `target-publish` and
`target-presentation`.

## Project properties

The tool reads these optional MSBuild properties. A generated project needs
none of them; the defaults follow the frontend directory.

| Property | Default |
| --- | --- |
| `RunicApplicationFrontendDirectory` | `Frontend` |
| `RunicApplicationFrontendPackageDirectory` | the frontend directory |
| `RunicApplicationFrontendOutputDirectory` | `<frontend>/dist` |
| `RunicApplicationFrontendWebRoot` | `www`, relative to the build output |
| `RunicApplicationFrontendDevServerKind` | `angular` with `angular.json`, `vite` with a `vite.config.*`, otherwise none |
| `RunicApplicationFrontendViteDevServerEntry` | the first of `/src/main.ts`, `/src/main.tsx`, `/src/main.js`, `/src/main.jsx` |
| `RunicApplicationFrontendViteConfiguration` | the frontend's `vite.config.*` |
| `RunicApplicationFrontendDevServerDocument` | every HTML build input with Vite, `index.html` with Angular; set it to override the list and separate several documents with `;` |
| `RunicApplicationFrontendDevWatchTarget` | none; an MSBuild target to run as the frontend watcher without a development server |

The package manager comes from `packageManager` in the frontend `package.json`,
then from its lock file.

## Measure size

`size` publishes an application for a required runtime identifier, inventories
all published files, hashes each file, and writes an optional executable-check
result into a JSON report:

```bash
dotnet runic size --project path/to/App.csproj --runtime linux-x64 --report measurements/linux.json
```

As in `doctor`, the runtime identifier is `--runtime`, `--rid` or `-r`. `size`
publishes with Native AOT unless you pass `--no-aot`; `--aot` states the default.

`--verify` names any executable to run after a successful publish: a path or a
command on `PATH`, such as `node` or `bun`. It is not looked up in the publish
directory. It runs in the project directory and receives the `--verify-argument`
values, then the publish directory and the published application's path. Its
output goes to `verification.log` in the run directory next to the report, and a
nonzero exit code fails the command:

```bash
dotnet runic size --runtime linux-x64 --report measurements/linux.json \
  --verify node --verify-argument scripts/check-publish.mjs
# runs: node scripts/check-publish.mjs <publish directory> <publish directory>/App
```

## Local support envelope

`support` only reads an explicitly selected Editor diagnostic ZIP. It can
preview the selected collector and every omission, collect one unsigned local
JSON envelope, or verify and remove that envelope. It never launches a product,
scans a workspace, uploads data, opens a network transport, or configures
telemetry.

```bash
dotnet runic support --mode preview --editor-diagnostics /path/to/editor-diagnostics.zip
dotnet runic support --mode collect --editor-diagnostics /path/to/editor-diagnostics.zip --destination /path/to/support-envelope.json
dotnet runic support --mode remove --destination /path/to/support-envelope.json
```

The collector accepts only `runic.translations.editor-diagnostics/1` and
rejects paths, source/translation/review text, sessions, cookies, and tokens.
The resulting `runic.support-envelope/1` contains normalized
application/workspace counts plus a fixed omission record.

Preview tool; [MIT licensed](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).
