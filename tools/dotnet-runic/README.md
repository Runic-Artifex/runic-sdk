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

`dev` requires `RunicViewsWindowProject=true` and a CS-WebUI
(`Runic.Application.CsWebUi`) or Runic Desktop (`Runic.Application.Desktop`)
host. It restores the .NET and JavaScript dependencies, builds the project, and
runs the native Window alongside the frontend's Vite or Angular development
server. While the development server runs, the build skips the production
frontend build and leaves the development document in `www/`
(`RunicBridgeBuildFrontend=false`, `RunicBridgeCopyFrontend=false`); the Views
MSBuild targets still generate the typed TypeScript clients. `dotnet watch`
restarts the Window after C# edits. `--no-restore`, `--no-frontend-watch`,
`--no-dotnet-watch`, and `--dry-run` select parts of that loop. `--no-restore`
also passes `RunicBridgeInstallFrontend=false`, so the build does not install
missing frontend packages either. Application arguments after `--` are passed
to the Window process. When a restore, install or build step fails, the error
names the program and its working directory and then points to `doctor`.

`doctor` checks the Views Window opt-in, the .NET SDK, the declared JavaScript
runtime and package manager, the matching lock file, the configured
development-server inputs, and, once the project is restored, that every Runic
package belongs to one release train. An unrestored project gets a warning that
tells you to run `dotnet runic dev` or `dotnet restore`. The browser check
follows the referenced host: a Runic Desktop project needs no browser, and a
missing browser is only a warning for a CS-WebUI project because CS-WebUI falls
back to the platform WebView. Only browser smoke checks require Chromium.

### Doctor JSON output

`dotnet runic doctor --output json` (or `RUNIC_COMMANDLINE_OUTPUT=json`) writes
one `runic.commandline/1` envelope. A `runic.commandline/1` envelope carries a
payload only when it succeeds, so by default JSON output reports every completed
inspection as a successful envelope (exit code 0) with the checks in the payload,
even when checks fail; read `payload.healthy` to decide. Errors that stop the
inspection, such as a missing project, produce a failed envelope with a `fault`
and no payload.

`--fail-on <never|fail|warn>` selects when doctor fails, with the same meaning in
both output modes. The default is `fail` for human output and `never` for JSON
output. When a check reaches the threshold, doctor exits with code 1. In JSON
mode that is a failed envelope with no payload: the fault has code
`RAPPCLI1009` and `details` mapping each non-passing check id to `fail` or
`warn`, and the diagnostics mark the checks at the threshold as errors.

Setting `RUNIC_COMMANDLINE_OUTPUT=json` therefore changes doctor's exit code:
failing checks exit 0 unless `--fail-on` is given. Pass `--fail-on fail` in CI
when the exit code must reflect the checks.

In CI, either let the exit code decide or check the payload:

```bash
dotnet runic doctor --output json --fail-on fail > doctor.json
dotnet runic doctor --output json | jq -e '.payload.healthy'
```

The payload type is `runic.application.tool.doctor/1`:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["project", "host", "healthy", "summary", "checks"],
  "properties": {
    "project": { "type": "string", "description": "Absolute path of the inspected project file." },
    "host": { "enum": ["cswebui", "desktop", "unknown"], "description": "The referenced Runic Views host." },
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
`vite-config`, `vite-entry` and `browser`.

## Project properties

The tool reads these optional MSBuild properties. A generated project needs
none of them; the defaults follow the frontend directory.

| Property | Default |
| --- | --- |
| `RunicBridgeFrontendDir` | `Frontend` |
| `RunicApplicationFrontendPackageDirectory` | the frontend directory |
| `RunicApplicationFrontendOutputDirectory` | `<frontend>/dist` |
| `RunicApplicationFrontendWebRoot` | `www`, relative to the build output |
| `RunicApplicationFrontendDevServerKind` | `angular` with `angular.json`, `vite` with a `vite.config.*`, otherwise none |
| `RunicApplicationFrontendViteDevServerEntry` | the first of `/src/main.ts`, `/src/main.tsx`, `/src/main.js`, `/src/main.jsx` |
| `RunicApplicationFrontendViteConfiguration` | the frontend's `vite.config.*` |
| `RunicApplicationFrontendDevServerDocument` | `index.html`; separate several documents with `;` |
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
