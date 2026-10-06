# Contributing to Runic SDK

Run all workspace commands from the SDK root. Install the versions in `global.json`,
`.node-version` and `package.json`; Linux developers can use `nix develop`.

```sh
bun run bootstrap
bun run build
bun run test application
```

Use `RunicSdk.Core.slnx` for libraries, tools and managed tests, or `RunicSdk.slnx`
when working on the editor. `CONFIGURATION=Release` selects release builds; CI
always uses Release.
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
| Required consumer inputs and invalid-data fixtures | `tests/fixtures` |
| Shared contracts, schemas and conformance data | `specs/<component>` |
| Package guidance and product specifications | Package READMEs, `specs` |
| Shared portal and portal guides | [runic-site/docs](https://github.com/Runic-Artifex/runic-site/tree/main/docs) |
| Build, CI and release tooling | `eng` |

Update `eng/workspace.json` when adding an artifact or changing ownership.
Use `ProjectReference` and `workspace:*` for internal development dependencies.
Published package names and dependency ranges are public contracts. A package's
shipped targets and native assets stay with it. Shared .NET build policy lives in
`eng/build`; product exceptions must be explicit rather than inherited from a
second repository root. Web packages declare their own build/test dependencies.

The root Bun lockfile and NuGet configuration own development restores. Independent
lockfiles in template and isolated consumer fixtures prove installation behavior;
they are excluded from the active workspace.

## Pull requests

`main` accepts changes only through pull requests. A ruleset requires the
always-run `verify` check from the CI workflow to pass before merging; no
approving review is required for commits attributed to a GitHub account. The
ruleset requires one approval from someone with write access when a pull request
contains commits whose author email is not linked to a GitHub account, so commit
with an email linked to your account. You cannot approve your own pull request.
Branch deletion and force pushes to `main` are
blocked. Organization administrators can bypass the ruleset, but only for
emergencies such as a broken release or a CI outage that blocks a fix; follow an
emergency push with a pull request that records why. Path-filtered workflows,
such as the DynamicData consumer, are not required checks because a skipped
workflow never reports.

## Verify a change

Run the relevant tests and build/type checks for your change. The focused runner
uses the current checkout and incremental builds, without containers:

```sh
bun run test --list
bun run test application
bun run test web/svelte
bun run test eng/release/contracts.test.mjs
bun run test tests/dotnet/Runic.Desktop.Tests/Runic.Desktop.Tests.csproj
```

`bun run verify <scope>` is an alias for the same focused checks. Calling either
without a scope lists the available checks and explicitly reports that no tests
ran. Managed groups build/run their executable suites; single .NET test projects
use `dotnet test` when appropriate. Package scripts own their web tests.

GitHub runs full CI, including package/template consumers and native checks on
Linux x64, Windows x64 and macOS arm64. Do not routinely duplicate the entire
workflow locally. `bun run affected <base-ref>` helps select relevant components
and consumers; include consumers when changing a shared contract.

For workflow debugging, `bun run ci --job templates` or `bun run ci` can run the
GitHub workflow locally with Docker/Podman. See [local CI](eng/ci/README.md).
Keep generated contracts and locks current. Stop after relevant checks pass unless
new changes or failures justify more verification. Manual native/UI checks and
soaks are scoped to the behavior being changed, not every PR or release.

### Public API changes

Shipping .NET libraries record their public API in `PublicAPI.Shipped.txt` and
`PublicAPI.Unshipped.txt`. Add new or changed members to `PublicAPI.Unshipped.txt`;
CI fails with RS0016 or RS0017 otherwise. See
[the build policy](eng/build/README.md#public-api).

### Regenerate checked files

When CI reports a stale generated file, run:

```sh
bun run regen
```

It runs these steps in order:

1. `bun tools/dotnet-runic/metadata/generate.mjs --write` rewrites
   `tools/dotnet-runic/metadata/runic.compatibility-set.json` from
   `eng/workspace.json` and the toolchain pins. CI checks it with `--check`.
2. `bun eng/generate-shipping-projects.mjs` rewrites
   `eng/build/shipping-projects.props` from `eng/workspace.json`. CI checks it
   with `--check`.
3. `bun eng/run.mjs build` (the same as `bun run build`) builds the Views runtime,
   `RunicSdk.Core.slnx` and the web packages. The build rewrites the committed
   generated clients in `examples/{first-window,first-window-desktop,notes-view-first,notes-reactive-views}/Frontend/src/generated`.
   CI's build job runs the same build and then fails on any changed tracked file.

Commit the resulting changes. `regen` does not change lockfiles or anything outside
the solution:

- Run `bun install` for `bun.lock`.
- Run `bun eng/dependencies/update-template-locks.mjs` for the starter locks
  (see [dependency review](eng/dependencies/README.md)).
- Run `bun install` in the example's frontend directory for an example lockfile.
- Rebuild the DynamicData example with the fork to rewrite its generated client
  (see [its README](examples/dynamicdata/README.md)).

### Versions and toolchain pins

`eng/workspace.json` owns the release-train version. Change it with:

```sh
bun run version:bump 0.7.0-preview.1
```

The command rewrites `RunicSdkVersion` in `eng/Versions.props`, every npm
manifest and the CLI compatibility metadata. It leaves
`RunicPackageValidationBaselineVersion` at the last published release and the
independently released `Runic.CommandLine` pin in `Directory.Packages.props`.
Template locks are stamped from the packed archives. The engineering tests fail
when a copy differs.

`global.json`, `.node-version`, the `packageManager` in `package.json` and the
npm/pnpm install in `.github/actions/setup-sdk/action.yml` own the toolchain pins;
`bun eng/toolchain.mjs` prints them. The engineering tests check the template
package-manager defaults and the Bun, npm and pnpm archives in `flake.nix` against
them; update those copies in the same change.

The DynamicData example and fixture fail to build when `DynamicDataForkRoot` is
not at `dynamicData.forkRevision` from `eng/workspace.json`. Pass
`-p:RunicCheckDynamicDataForkRevision=false` to try a candidate revision.

Tracked Markdown files must not contain broken relative links; the engineering
tests (`bun run test engineering`) check them. Link to files in other repositories
with absolute GitHub URLs.

Repository scripts, build tools and verification use the Bun version pinned in
`package.json`. Use `bun run --bun` when invoking package scripts so Node shebangs
also run under Bun. Node is retained for npm/pnpm package and template compatibility checks, not the default workspace
runtime. `node:` imports refer to compatible APIs and do not require launching Node.
Native CI targets Linux x64, Windows x64 and macOS Apple Silicon; Intel macOS is
not a CI certification target.

The optional Vite DevTools dock supports Bun and is also exercised by an
installed npm/Node consumer. See the
[Vite plugin guide](packages/web/vite-plugin-runic/README.md#bun-runtime).

Vue template type checking remains an explicit npm/Node compatibility check
because the pinned `vue-tsc` relies on Node behavior; the template's build is also
verified with only Bun and .NET on PATH.

## Linux embedded runtimes

The locked flake includes GTK3/WebKitGTK 4.1 and GTK4/WebKitGTK 6.0, TLS/GIO
modules, GStreamer codecs, session D-Bus tools, GTK/KDE portal backends and Xvfb.
Use `direnv exec . pkg-config --modversion gtk4 webkitgtk-6.0` to inspect the
selected native versions. The shell does not start portal daemons or select the
application's GTK backend; the desktop session owns portal service configuration.

Run isolated Xvfb checks with `GDK_BACKEND=x11` explicitly. An inherited
`WAYLAND_DISPLAY` can otherwise make GTK choose the user's Wayland session despite
Xvfb. Real Wayland and desktop chooser checks use the intended session separately.
