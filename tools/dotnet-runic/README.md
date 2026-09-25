# dotnet-runic

`dotnet-runic` checks and coordinates a Runic Views Window project. Generated
projects pin the tool locally:

```bash
dotnet tool restore
dotnet runic doctor --project path/to/App.csproj
dotnet runic dev --project path/to/App.csproj
```

`dev` requires `RunicViewsWindowProject=true`. The Views MSBuild targets own
View discovery, typed TypeScript generation, frontend builds, and asset copying.
The CLI restores the selected .NET and JavaScript dependencies, invokes that
MSBuild owner, then runs the native Window alongside the configured Vite,
Angular, or frontend watcher. Frontend and application arguments after `--` are
passed as ordinary process arguments.

`doctor` checks the Views Window opt-in, the selected package train, the .NET
SDK, the declared JavaScript runtime and package manager, the matching lock
file, and configured frontend development-server inputs. It treats an absent
browser as a warning because browser availability is only needed for browser
smoke checks.

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
