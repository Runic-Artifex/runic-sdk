# Runic SDK

Build native desktop applications with C# logic and React, Vue, Svelte or Angular.
Adopt Application Views, Desktop and Assets independently. Command Line and
Translations are independently released SDKs.

## Start building an application

Create an app with the guided creator, then run it:

```sh
dnx Runic.Create@<VERSION>
cd MyApp
dotnet tool restore
dotnet runic dev
```

The creator asks for the frontend (React, Vue, Svelte or Angular), package
manager (npm, pnpm or Bun), Window host (CS-WebUI or Runic Desktop) and
ViewModel library (CommunityToolkit.Mvvm or ReactiveUI), then prints a command
that recreates the project without questions. The
[project creator](https://docs.runic-artifex.eu/create/) builds the same command
in the browser, and `dotnet new runic-app --help` lists the template options.
The [getting-started guide](https://docs.runic-artifex.eu/getting-started/)
lists the prerequisites and the current version. For an existing project, copy
the install command for the capability you need from the
[package catalog](https://docs.runic-artifex.eu/packages/).

Explore the [first Window](examples/first-window/README.md),
[CommunityToolkit Notes](examples/notes-view-first/README.md), and
[Reactive Notes](examples/notes-reactive-views/README.md) for the current
Window and View model. The instructions below are for contributing to the SDK itself.

## Start developing

Install the .NET SDK in `global.json`, Node in `.node-version`, and the Bun version
in `package.json` (`bun eng/toolchain.mjs` prints every toolchain pin).
On Linux with Nix, `nix develop` provides the SDK, Node, Bun, C++ compiler, and
webview dependencies from the shared flake. Run these commands from this directory:

See [NixOS development and native shutdown](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/nixos-development.md)
for the environment requirements and regression checks.

```sh
nix develop             # Linux: use the complete pinned environment
bun run bootstrap       # One frozen npm workspace install and .NET restore
bun run build           # SDK libraries and web packages
bun run test application # Focused checks in the current checkout
```

Open `RunicSdk.Core.slnx` for the SDK and test work. Builds use Debug by default;
set `CONFIGURATION=Release` for release builds.

## Layout

| Directory | Ownership |
| --- | --- |
| `packages/dotnet`, `packages/web` | Published libraries, generators and framework integrations |
| `tools` | Application tooling, bridge inspector and asset packer |
| `examples` | First Window and Notes application examples |
| `tests` | Managed/native suites, package/template consumers and required fixtures |
| `specs` | Shared protocols, schemas and conformance corpora |
| `docs` | Link to the shared portal in runic-site |
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

Start with the [first Window](examples/first-window/README.md), then compare the
[CommunityToolkit Notes](examples/notes-view-first/README.md) and
[Reactive Notes](examples/notes-reactive-views/README.md) examples. They use
explicit .NET Window and View types, generated TypeScript clients, and ordinary
frontend components. See the [Views guide](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/application/README.md)
and [host selection](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/host-selection.md).
The [host-choice and footprint assessment](https://github.com/Runic-Artifex/runic-site/blob/main/docs/guides/desktop/host-choice-and-footprint.md)
retains the historical Linux measurement and links to current size guidance.

## Verify packages and releases

```sh
bun run verify:candidate # Pack, then run the package and template checks below once
bun run pack             # Materialize the current workspace NuGet and npm inventory
bun run verify-packages  # Install archives into isolated consumers outside this checkout
bun run verify:templates # Packed React/Vue/Svelte/Angular apps with npm, pnpm, and Bun
bun run ci               # Optional: debug the GitHub workflow locally
bun run affected main    # Changed components plus their dependent components
```

Package consumers use a fresh NuGet cache and map Runic identities to the local
candidate feed. npm consumers install tarballs outside the workspace and reject
source links or unpublished dependency specifiers. Template acceptance additionally
requires Bash and the npm and pnpm versions from `bun eng/toolchain.mjs`. Artifacts are written to
`artifacts/packages`; these commands never publish packages. Packing builds the
whole set in a staging directory and replaces `artifacts/packages` only when every
package succeeded, so a failed or interrupted pack keeps the previous set. It does
not modify tracked files. Consumer trees are deleted afterwards; pass `--keep`
(`bun run verify-packages --keep`) to retain them for diagnosis.

`eng/workspace.json` lists maintained artifacts and component dependencies.
Its `version` is the release-train version. `eng/Versions.props` and every npm
manifest carry checked copies; `bun run version:bump <version>` rewrites them and
the CLI compatibility metadata together. `eng/toolchain.mjs` owns the .NET, Node,
Bun, npm and pnpm pins and checks the template defaults and the Nix shell against
them.
CI uses separate jobs for managed suites, web packages, browser/HMR
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
