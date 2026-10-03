# dotnet-runic

`dotnet-runic` checks and coordinates a Runic Views Window project. Generated
projects pin the tool locally, so the first run of a new project is:

```bash
dotnet tool restore
dotnet runic dev
```

Both commands find the single `.csproj` in the current directory; pass
`--project path/to/App.csproj` otherwise.

`dev` requires `RunicViewsWindowProject=true` and a CS-WebUI
(`Runic.Application.CsWebUi`) or Runic Desktop (`Runic.Application.Desktop`)
host. It restores the .NET and JavaScript dependencies, builds the project, and
runs the native Window alongside the frontend's Vite or Angular development
server. While the development server runs, the build skips the production
frontend build and leaves the development document in `www/`
(`RunicBridgeBuildFrontend=false`, `RunicBridgeCopyFrontend=false`); the Views
MSBuild targets still generate the typed TypeScript clients. `dotnet watch`
restarts the Window after C# edits. `--no-restore`, `--no-frontend-watch`,
`--no-dotnet-watch`, and `--dry-run` select parts of that loop. Application
arguments after `--` are passed to the Window process.

`doctor` checks the Views Window opt-in, the .NET SDK, the declared JavaScript
runtime and package manager, the matching lock file, the configured
development-server inputs, and, once the project is restored, that every Runic
package belongs to one release train. An unrestored project gets a warning that
tells you to run `dotnet runic dev` or `dotnet restore`. A missing browser is a
warning too: CS-WebUI falls back to the platform WebView, and only browser smoke
checks require Chromium.

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
