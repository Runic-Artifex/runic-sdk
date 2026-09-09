# Contributing to Runic SDK

Run all workspace commands from the SDK root. Install the versions in `global.json`,
`.node-version` and `package.json`; Linux developers can use `nix develop`.

```sh
bun run bootstrap
bun run build
bun run test command-line
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
| User and architecture guidance | `docs`, `docs/guides` |
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

## Verify a change

Run the relevant tests and build/type checks for your change. The focused runner
uses the current checkout and incremental builds, without containers:

```sh
bun run test --list
bun run test command-line
bun run test web/application-bridge
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

Repository scripts, build tools and verification use Bun 1.4.2. Use `bun run --bun`
when invoking package scripts so Node shebangs also run under Bun. Node is retained
for npm/pnpm package and template compatibility checks, not the default workspace
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
