# Contributing to Runic SDK

Run all workspace commands from the SDK root. Install the versions in `global.json`,
`.node-version` and `package.json`; Linux developers can use `nix develop`.

```sh
bun run bootstrap
bun run build
bun run test
```

Use `RunicSdk.Core.slnx` for libraries, tools and managed tests, or `RunicSdk.slnx`
when working on the editor. `CONFIGURATION=Release` selects release verification.
The compiler tests require a C++20-capable `clang++`.

## Where changes belong

| Change | Location |
| --- | --- |
| Managed library, generator or adapter | `packages/dotnet/<package>` |
| npm runtime or framework integration | `packages/web/<package>` |
| CLI, compiler, packer or project template | `tools/<tool>` |
| First-party application | `apps/<app>` |
| Maintained migration or getting-started example | `examples/<scenario>` |
| Managed/native tests | `tests/dotnet`, `tests/native` |
| Cross-package consumer checks | `tests/web`, `tests/templates` |
| Required historical/invalid consumer inputs | `tests/fixtures` |
| Shared contracts, schemas and conformance data | `specs/<component>` |
| User and architecture guidance | `docs`, `docs/guides` |
| Build, CI and release tooling | `eng` |

Update `eng/workspace.json` when adding an artifact or changing ownership.
Use `ProjectReference` and `workspace:*` for internal development dependencies.
Published package names and dependency ranges are public contracts. A package's
shipped targets and native assets stay with it. Shared .NET build policy lives in
`eng/build`; product exceptions must be explicit rather than inherited from a
second repository root. Web packages declare their own build/test dependencies.

The root Bun lockfile and NuGet configuration own development restores. Independent
lockfiles in template and historical consumer fixtures prove installation behavior;
they are excluded from the active workspace. `eng/archive` is historical reference
and must never become a dependency of a maintained artifact or check.

## Verify a change

Run the relevant package tests during development. Before completing a structural
or packaging change, run:

```sh
bun run verify            # Full build, tests, packing, isolated NuGet/npm consumers
bun run verify:templates  # Four frontend templates with npm, pnpm and Bun
```

Template acceptance also requires npm 11.16.0 and pnpm 11.25.0. Root CI exercises
native window close handling and NativeAOT on Linux, Windows and both macOS
architectures. `bun run affected <base-ref>` reports changed components and their
consumers; it does not replace verification. Keep generated contracts and lockfiles
current, and document platform checks that could not run locally.


Repository scripts, build tools and verification use Bun 1.4.0. Use `bun run --bun`
when invoking package scripts so Node shebangs also run under Bun. Node is retained
for npm/pnpm package and template compatibility checks, not the default workspace
runtime. `node:` imports refer to compatible APIs and do not require launching Node.
Native CI targets Linux x64, Windows x64 and macOS Apple Silicon; Intel macOS is
not a CI certification target.

The optional pinned Vite DevTools dock currently rejects Bun in its upstream
transport. Core Runic diagnostics and HMR run on Bun; the dock is exercised by
the installed npm/Node consumer. Explicitly requiring the dock on Bun reports
`RUNICP007`. See the [Vite plugin guide](packages/web/vite-plugin-runic/README.md#bun-runtime).

Vue template type checking remains an explicit npm/Node compatibility check
because the pinned `vue-tsc` relies on Node behavior; the template's build is also
verified with only Bun and .NET on PATH.
