# Development and verification modes

WebUIToolkit has two deliberately different build modes.

## Development

`Development` is the default outside CI. It is intended for editing, debugging,
running samples, and ordinary pull-request work:

- package lock files may be refreshed by a normal restore;
- NuGet vulnerability auditing is deferred to verification;
- warnings remain visible but do not fail the build;
- trim and Native AOT analyzers do not run during every inner-loop build; and
- samples use source project references and run with ordinary `dotnet run`.

Run the complete managed inner loop with:

```powershell
./eng/dev.ps1
```

Or use the normal .NET commands directly:

```powershell
dotnet restore WebUIToolkit.slnx
dotnet build WebUIToolkit.slnx
dotnet test WebUIToolkit.slnx
```

## Verification

`Verification` is selected automatically in CI and explicitly by
`eng/verify.ps1`. It keeps the release-facing controls:

- locked NuGet restore and vulnerability auditing;
- warnings as errors;
- trim and Native AOT compatibility analysis for shipping projects;
- architecture, namespace, and contract checks; and
- the dedicated deterministic package, isolated-feed, offline, and Native AOT
  rehearsals under `eng/verify-wave-*.ps1`.

Run it with:

```powershell
./eng/verify.ps1
```

For an individual command, opt in with:

```powershell
dotnet build -p:WebUIToolkitBuildMode=Verification
```

Release verification remains strict without turning its package feeds, caches,
or publication checks into prerequisites for running a sample.

## Frontend and cwhtml development loop

`WebUIToolkit.Frontend.Sdk` now owns frontend workspace installation, generated
contract verification, Vite builds, the native bridge asset, manifests, and
build/publish copying for the React, Vue, Svelte, Angular, and compiled
cwhtml/HTMX Todo projects.
For example:

```powershell
dotnet build samples/Todo.Svelte/Todo.Svelte.csproj
dotnet msbuild samples/Todo.Svelte/Todo.Svelte.csproj -t:WebUIToolkitFrontendWatch
dotnet run --project samples/Todo.Svelte -- --advanced
```

Debug builds retain readable frontend output and source maps. Release builds
use Vite minification and content hashing and emit `vite.manifest.json` plus
`webuitoolkit.assets.json` with byte sizes and SHA-256 hashes. Vite is never the
application host and does not carry native UI requests; CsWebUi serves the
produced local files and owns the binary MVVM channel.

The `dotnet-webuitoolkit` tool discovers SDK metadata and provides one
coordinated command for contract generation, the initial Vite/.NET build,
asset watching, `dotnet watch`, CsWebUi startup, diagnostics, and clean
process-tree shutdown:

```powershell
dotnet webuitoolkit dev samples/SimpleTodo/SimpleTodo.csproj
```

SimpleTodo and AdvancedTodo now import shared cwhtml and frontend SDK targets;
their npm workspace owns HTMX, Bootstrap 5.3, Font Awesome, source maps,
minification, content hashing, and the asset manifest. SimpleTodo now generates
its action/field/command/fragment registration from build-time cwhtml metadata
and uses the high-level native application builder; AdvancedTodo remains the
next migration target. Browser overlays, true Vite HMR into the native window,
and state-preserving cwhtml replacement also remain milestones in the
[cwhtml development-experience plan](./cwhtml-development-experience.md).
