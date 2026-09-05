# Runic SDK

One development checkout for Runic's application framework, desktop host, assets,
translations, command line, frontend integrations, translations editor, examples,
and documentation. NuGet and npm packages retain their existing identities and
can still be consumed independently.

## Start developing

Install the .NET SDK in `global.json`, Node in `.node-version`, and Bun 1.4.0. The full translation compiler test suite also requires `clang++`
with C++20 support.
On Linux with Nix, `nix develop` provides the SDK, Node, Bun, C++ compiler, and
webview dependencies from the shared flake. Run these commands from this directory:

```sh
bun run bootstrap       # One frozen npm workspace install and .NET restore
bun run build           # SDK, editor, current example, and documentation
bun run test            # Managed contracts, frontend tests, generated artifacts, docs
bun run example:counter # Small member-based bridge example
bun run dev:docs        # Documentation development server
bun run dev:editor      # Build and launch the translations editor
```

Open `RunicSdk.slnx` for the complete solution or `RunicSdk.Core.slnx` for SDK
and test work without the editor frontend. Builds use Debug by default; set
`CONFIGURATION=Release` for release builds. Native editor execution requires the
platform webview runtime described in `packages/runic-desktop/README.md`.

## Layout

| Directory | Ownership |
| --- | --- |
| `packages/runic-toolkit` | Application runtime, bridge compiler, hosting, testing, CLI, templates, Angular |
| `packages/runic-desktop` | Desktop host and browser transport |
| `packages/runic-assets` | Assets runtime, packer, ASP.NET Core and desktop adapters |
| `packages/runic-translations` | Translation runtime, compiler, build tools and Vite integration |
| `packages/runic-command-line` | Command line runtime, generators, process runner and testing |
| `packages/runic-vite` | Vite application integration |
| `packages/runic-svelte` | Svelte and SvelteKit adapters |
| `apps/translations-editor` | First-party editor consuming workspace packages |
| `examples/current` | Maintained examples; older imported samples are historical fixtures |
| `docs` | Framework documentation site |
| `eng` | Workspace commands, package inventory, release authority and migration provenance |

Source dependencies use explicit `ProjectReference` and `workspace:*` links.
They never fall back to a published Runic package when a sibling project is missing.
The root Bun lockfile and NuGet configuration own active workspace restores.
Root `Directory.Packages.props` centralizes shared .NET dependencies; a documented
translation compiler pin remains local. Product-specific analyzer/build policies
remain with their product. Template lockfiles and historical fixtures are independent
consumer evidence, not additional development workspaces.

## Verify packages and releases

```sh
bun run pack             # Materialize all 19 NuGet packages and 8 npm archives
bun run verify-packages  # Install archives into isolated consumers outside this checkout
bun run verify:templates # Packed React/Vue/Svelte/Angular apps with npm, pnpm, and Bun
bun run verify           # Build, tests, pack, and standalone package consumers
bun run affected main    # Changed components plus their dependent components
```

Package consumers use a fresh NuGet cache and map Runic identities to the local
candidate feed. npm consumers install tarballs outside the workspace and reject
source links or unpublished dependency specifiers. Template acceptance additionally
requires Bash, npm 11.16.0, and pnpm 11.25.0. Artifacts are written to
`artifacts/packages`; these commands never publish packages.

`eng/workspace.json` lists maintained artifacts and component dependencies.
`eng/Versions.props` defines the .NET release version. npm packages retain explicit
versions, checked against the inventory. Coordinate version changes in a single PR.
CI runs the integrated verification and template consumers on Linux, plus managed
desktop contracts and NativeAOT bridge checks on Linux, Windows, and both macOS
architectures. Native UI certification remains a separate platform test concern.

Root CI creates downloadable
candidates only. The root `.github/workflows/ci.yml` owns this checkout's CI.

