# `dotnet webuitoolkit`

`WebUIToolkit.DotNet.WebUIToolkit` is the development-loop coordinator for
native CsWebUi applications. It deliberately does not start an ASP.NET Core
server.

Install a packed build as a local or global .NET tool, then run it from a
directory containing one application project:

```console
dotnet webuitoolkit dev
dotnet webuitoolkit dev samples/Todo.React -- --advanced
```

The `dev` command:

1. evaluates the selected project and its `WebUIToolkit.Frontend.Sdk`
   properties;
2. generates and verifies configured C# and TypeScript contracts;
3. performs one normal .NET/frontend build, restoring dependencies unless
   `--no-restore` is supplied;
4. starts a loopback-only Vite development server for opted-in projects, or
   the project-provided build watcher for legacy projects;
5. starts the native application under `dotnet watch`;
6. injects Vite's client and source entry into the native document only for
   development-server sessions, so CSS and JavaScript update without a .NET
   restart, document reload, or ViewModel loss;
7. forwards versioned cwhtml compiler diagnostics, including stable IDs and
   exact source spans, into Vite's standard browser overlay and clears it after
   a successful compilation; legacy build-watch mode still
   detects `webuitoolkit.assets.json`, mirrors that graph into the runtime web
   root, and restarts the native host; and
8. forwards Ctrl+C to both process trees and waits for their termination.

Host rebuilds set `WebUIToolkitFrontendEnabled=false`, because the independently
running frontend watcher owns that part of the loop. This prevents duplicate
Vite builds. The initial regular build remains authoritative for shared assets
and the runtime output layout.

## Project contract

The command consumes these evaluated MSBuild properties:

- `WebUIToolkitFrontendEnabled`
- `WebUIToolkitFrontendWorkspaceRoot`
- `WebUIToolkitFrontendWorkspace`
- `WebUIToolkitFrontendPackageDirectory`
- `WebUIToolkitFrontendOutputDirectory`
- `WebUIToolkitFrontendWebRoot`
- `WebUIToolkitFrontendDevWatchTarget`
- `WebUIToolkitFrontendViteDevServerEnabled`
- `WebUIToolkitFrontendViteDevServerEntry`
- `WebUIToolkitFrontendViteConfiguration`
- `WebUIToolkitCwhtmlDiagnosticsPath`
- `WebUIToolkitFrontendContractSource`
- `WebUIToolkitFrontendContractCSharpOutput`
- `WebUIToolkitFrontendContractTypeScriptOutput`
- `WebUIToolkitFrontendContractTool`

`WebUIToolkitFrontendViteDevServerEnabled=true` selects the native-window HMR
path. The tool assigns a free loopback port, passes `--host 127.0.0.1`,
`--strictPort`, and the selected port to the workspace's `dev` script, waits
for `/@vite/client`, then supplies the origin and configured entry module to
the managed application. Vite serves assets only; HTMX requests continue to
use the private CsWebUi binding.

The coordinator wraps the application's normal Vite configuration with a
development-only diagnostics plugin. It watches the atomic
`webuitoolkit.cwhtml.diagnostics/1.0` snapshot, sends errors through Vite's
standard overlay protocol, and removes the overlay when the snapshot recovers.
The wrapper is temporary and does not modify the application's Vite config.

`WebUIToolkitFrontendDevWatchTarget` is preferred in legacy mode. This allows one
MSBuild target to coordinate Vite and another asset compiler such as cwhtml.
For compatibility, a configured npm workspace is used directly when no target
is declared.

The frontend watcher must publish `webuitoolkit.assets.json` last. Its content
hash is the reload boundary: intermediate file writes do not restart the
CsWebUi window. Asset mirroring rejects symbolic links and removes only files
that belonged to the preceding frontend graph, preserving separately supplied
vendor files.

Use `--dry-run` to inspect the evaluated project, frontend, asset, runtime, and
contract paths without generating files or starting child processes.
Use `--no-dotnet-watch` to run the already-built managed host once while still
supervising Vite; this is primarily useful for deterministic browser gates.

## Diagnostics

| Code | Meaning |
| --- | --- |
| `WUTDEV1001` | Invalid command or option |
| `WUTDEV1002` | Project discovery failed |
| `WUTDEV1003` | MSBuild project evaluation failed |
| `WUTDEV1004` | A required executable is unavailable |
| `WUTDEV1005` | Frontend SDK configuration is incomplete |
| `WUTDEV1006` | Contract generation or the initial build failed |
| `WUTDEV1007` | A supervised watcher exited unexpectedly |
| `WUTDEV1008` | Local filesystem/process I/O failed |
| `WUTDEV1099` | An invariant inside the coordinator failed |

Child processes receive arguments as discrete tokens without shell parsing.
Captured setup output is bounded, while long-running frontend and host output
is streamed with `[frontend]` and `[host]` prefixes.
