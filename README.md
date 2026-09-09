# Runic SDK

Build native desktop applications with C# logic and React, Vue, Svelte or Angular.
Adopt Application, Desktop, Assets, Translations and Command Line independently;
the SDK's NuGet and npm packages release together.

## Start building an application

Follow the [getting-started guide](https://docs.runic-artifex.eu/getting-started/)
to install the published templates and run an app. For an existing project, copy
the install command for the capability you need from the
[package catalog](https://docs.runic-artifex.eu/packages/).

Explore the [customer editor](examples/customer-migration/README.md) and
[document application](examples/document-migration/README.md) for complete examples.
The instructions below are for contributing to the SDK itself.

## Start developing

Install the .NET SDK in `global.json`, Node in `.node-version`, and Bun 1.4.2. The full translation compiler test suite also requires `clang++`
with C++20 support.
On Linux with Nix, `nix develop` provides the SDK, Node, Bun, C++ compiler, and
webview dependencies from the shared flake. Run these commands from this directory:

See [NixOS development and native shutdown](docs/guides/desktop/nixos-development.md)
for the environment requirements and regression checks.

```sh
nix develop             # Linux: use the complete pinned environment
bun run bootstrap       # One frozen npm workspace install and .NET restore
bun run build           # SDK, editor, current example, and documentation
bun run test command-line # Focused checks in the current checkout
bun run example:counter # Small member-based bridge example
bun run example:customers # Customer editor migrated away from CommunityToolkit MVVM
bun run example:documents # Open, edit and save text documents
bun run dev:docs        # Documentation development server
bun run dev:editor      # Build and launch the translations editor
```

Open `RunicSdk.slnx` for the complete solution or `RunicSdk.Core.slnx` for SDK
and test work without the editor frontend. Builds use Debug by default; set
`CONFIGURATION=Release` for release builds. Native editor execution requires the
platform webview runtime described in [Desktop guidance](docs/guides/desktop/window-close-lifecycle.md).

## Layout

| Directory | Ownership |
| --- | --- |
| `packages/dotnet`, `packages/web` | Published libraries, generators and framework integrations |
| `tools` | CLI, bridge inspector, asset packer, translation compiler and templates |
| `apps` | First-party applications |
| `examples` | Maintained counter, customer and document examples |
| `tests` | Managed/native suites, package/template consumers and required fixtures |
| `specs` | Shared protocols, schemas and conformance corpora |
| `docs` | Documentation site and product/architecture guides |
| `eng` | Shared build policy, focused checks, package inventory and release tooling |

Source dependencies use explicit `ProjectReference` and `workspace:*` links.
They never fall back to a published Runic package when a sibling project is missing.
The root Bun lockfile and NuGet configuration own active workspace restores.
Root `Directory.Packages.props` centralizes shared .NET dependencies; a documented
translation compiler pin remains local. Product-specific analyzer/build policies
are explicitly imported from `eng/build`. Template lockfiles and isolated fixtures are independent
consumer evidence, not additional development workspaces.

See [CONTRIBUTING.md](CONTRIBUTING.md) for ownership, development and verification guidance.

## Migrating existing applications

The [customer migration reference](examples/customer-migration/README.md)
includes an original WPF/CommunityToolkit implementation, shared business rules,
and an idiomatic Runic replacement. Its [migration RFC](docs/guides/application/architecture/mvvm-migration-rfc.md)
records the accepted direction and the remaining DX and OS-integration work.
The [host-choice and footprint assessment](docs/guides/desktop/host-choice-and-footprint.md)
defines the proposed CS-WebUI/Desktop parity work and size-tuning experiments.

## Verify packages and releases

```sh
bun run pack             # Materialize the current workspace NuGet and npm inventory
bun run verify-packages  # Install archives into isolated consumers outside this checkout
bun run verify:templates # Packed React/Vue/Svelte/Angular apps with npm, pnpm, and Bun
bun run ci               # Optional: debug the GitHub workflow locally
bun run affected main    # Changed components plus their dependent components
```

Package consumers use a fresh NuGet cache and map Runic identities to the local
candidate feed. npm consumers install tarballs outside the workspace and reject
source links or unpublished dependency specifiers. Template acceptance additionally
requires Bash, npm 12.0.2, and pnpm 12.3.4. Artifacts are written to
`artifacts/packages`; these commands never publish packages.

`eng/workspace.json` lists maintained artifacts and component dependencies.
`eng/Versions.props` defines the .NET release version. npm packages retain explicit
versions, checked against the inventory. Coordinate version changes in a single PR.
CI uses separate jobs for managed suites, web packages, docs, the editor, browser/HMR
checks, packages and template consumers. Managed desktop, native window/close,
NativeAOT and footprint checks target Linux x64, Windows x64 and macOS Apple Silicon. Broader native UI certification remains
a separate platform test concern.

Published versions and migration notes are linked from the
[release page](https://docs.runic-artifex.eu/releases/). Maintainers follow the
[current release guide](eng/release/README.md): run the preview
workflow on main to test, package, publish and create the GitHub release.

Use `bun run test <scope>` or `bun run verify <scope>` for focused local checks;
`--list` shows the available scopes. Full CI runs on GitHub. The [local CI
tooling](eng/ci/README.md) remains available for workflow debugging with Docker/Podman.
