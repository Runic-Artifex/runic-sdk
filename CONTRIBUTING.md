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

Shared features have one behavior implementation and idiomatic integrations.
For navigation, keep guards, history, results, cancellation and ownership in
`Runic.Navigation`. ReactiveUI can expose observable/command/scheduler APIs;
Toolkit can compose the async core with native commands and source generators.
WPF/web presentation is a separate integration choice from MVVM framework.
Verify behavioral conformance through real consumers rather than requiring
matching API signatures. Add adapters when concrete consumer friction warrants
them; native framework APIs that already compose well need no symmetry wrapper.

ReactiveUI is the primary MVVM path and CommunityToolkit is supported, but
general-purpose projects (Views and Navigation cores, Platform, Desktop, Assets
and tools) depend on neither. Core code understands `ICommand`,
`INotifyPropertyChanged`, `INotifyDataErrorInfo` and the Runic attributes; a
library's types are known only to its convention module
(`Runic.Application.Views/Conventions`, `Runic.Application.Views.Codegen/Conventions`),
its generator plug-in and its adapter package. `eng/mvvm-layering.test.mjs` fails
when a project outside its allow-list references ReactiveUI, System.Reactive or
CommunityToolkit.Mvvm, directly, through a project reference, in its packed
build files or in its locked package graph.

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
ran. Managed groups build and run their test executables, and a single .NET test
project runs with `dotnet run`. Package scripts own their web tests.

GitHub CI includes package/template consumers and native checks on Linux x64,
Windows x64 and macOS arm64. Pushes to `main` run every job. Pull requests run
the jobs whose inputs changed, using component ownership in `eng/workspace.json`;
see [affected-only pull requests](eng/ci/README.md#affected-only-pull-requests).
Do not routinely duplicate the entire workflow locally. `bun run affected <base-ref>` helps select relevant components
and consumers; include consumers when changing a shared contract.

For workflow debugging, `bun run ci --job templates` or `bun run ci` can run the
GitHub workflow locally with Docker/Podman. See [local CI](eng/ci/README.md).
Keep generated contracts and locks current. Stop after relevant checks pass unless
new changes or failures justify more verification. Manual native/UI checks and
soaks are scoped to the behavior being changed, not every PR or release.

### TUnit suites

The .NET test suites in `tests/dotnet`, the ReactiveUI fixtures in `tests/fixtures`
and the examples' tests use [TUnit](https://tunit.dev) on Microsoft.Testing.Platform
(MTP). Each suite is an executable; run it with `dotnet run --project <suite>`. The
repository's `global.json` does not opt in to the MTP mode of `dotnet test`.

- **Project.** Keep `OutputType` `Exe`. Reference `TUnit.Core`, `TUnit.Engine`,
  `Microsoft.Testing.Extensions.TrxReport` and `Microsoft.Testing.Extensions.CodeCoverage`,
  pinned in `Directory.Packages.props`. The `TUnit` metapackage, which the `runic-app`
  template and the examples use, also adds TUnit's assertion library and MTP telemetry;
  the suites keep their own assertion helpers, and `Runic.Desktop.Tests` keeps
  `xunit.assert`. Set `TUnitImplicitUsings` to `false`, write `using TUnit.Core;`, and
  delete `Program.cs`: TUnit generates the entry point. A suite that also starts its own
  executable as a child process or helper, or must run inside a native event loop
  (`Runic.Platform.Runtime.Tests` on macOS), sets `GenerateTestingPlatformEntryPoint` to
  `false`, and its `Program.cs` runs that helper or loop before it builds the test application
  the way the generated entry point does. Commit the suite's `packages.lock.json`
  (`bun run lock:nuget`).
- **Cases.** Each case is a non-static `[Test]` method in a non-static class and fails when
  it throws. A case that had a name in a former runner keeps it as its `[DisplayName]`;
  otherwise it keeps its method name. A case that ran once per value has one
  `[Arguments]` row per value and a `[DisplayName]` with its parameters, such as
  `ResultRaceAsync(seed: $seed, disposeDuringRace: $disposeDuringRace)`. Where
  `System.ComponentModel` is imported, alias `DisplayNameAttribute` to
  `TUnit.Core.DisplayNameAttribute`.
- **Opt-in checks.** Checks that need a native desktop, a display or a manual step are
  `[Explicit]` cases with a `[Category]`. They are listed but run only when a filter
  selects them, for example `--treenode-filter "/*/*/*/*[Category=Native]"`. Options of
  such a check are `--test-parameter <name>=<value>` settings, which the cases read from
  `TestContext.Parameters`. The suites' READMEs list their categories.
- **Running and filtering.** `bun run test <group>` and `bun run test <suite>.csproj` pass
  arguments after the scope to every suite they run. `--list-tests` lists the cases and
  `--treenode-filter "/<assembly>/<namespace>/<class>/<method>"` selects them, with `*` as a
  wildcard, for example
  `dotnet run --project tests/dotnet/Runic.Navigation.Tests -- --treenode-filter "/*/*/NavigationTests/ResultRaceAsync"`.
  `--filter` is not supported. The exit code is 0 when all selected cases pass, 2 when one
  fails and 8 when none ran.
- **Reports.** `bun run test` and the managed CI job pass `--report-trx --coverage` and
  write `<suite>.trx` and `<suite>.cobertura.xml` to `artifacts/test-results/<suite>`
  (`artifacts/test-results/<group>/<suite>` in CI), which CI uploads as
  `managed-test-results-<group>-<run>` without the TRX attachment copies.
  The native job writes `Runic.Desktop.Tests`'s TRX report and MTP diagnostics to
  `artifacts/native-tests`.
- **Parallelism.** TUnit runs cases in parallel unless told otherwise. Allow it only when no
  case shares static state, `Trace.Listeners`, the current directory, environment
  variables, ports or files with another. The Navigation cases share
  `RunicModelContextRegistry.Shared`, `Trace.Listeners` and static race switches, and
  several are timing-sensitive, so its `AssemblyInfo.cs` runs the suite `[NotInParallel]`.
  Most other suites came from sequential runners and run `[NotInParallel]` too; each
  `AssemblyInfo.cs` names the shared state. `Runic.Create.Tests`, `Runic.Assets.Desktop.Tests`
  and the `notes-view-first` and `wpf-hybrid-editor` example tests share no such state and
  run in parallel.
- **WPF.** The Windows WPF suites run their cases on one STA thread with a WPF `Application`, through
  the `WpfDispatcherExecutor` in each suite, and keep the former order with
  `[NotInParallel("WpfSuite", Order = n)]`.

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
   `eng/workspace.json`, `eng/support.json` and the toolchain pins, and the
   support table in `README.md` from `eng/support.json`. CI checks both with
   `--check`.
2. `bun eng/generate-shipping-projects.mjs` rewrites
   `eng/build/shipping-projects.props` from `eng/workspace.json`. CI checks it
   with `--check`.
3. `bun eng/run.mjs build` (the same as `bun run build`) builds the Views runtime,
   `RunicSdk.Core.slnx` and the web packages. The build rewrites the committed
   generated clients in `examples/{first-window,first-window-desktop,multi-window,notes-view-first,notes-reactive-views}/Frontend/src/generated`.
   CI's build job runs the same build and then fails on any changed tracked file.

Commit the resulting changes. `regen` does not change lockfiles or anything outside
the solution:

- Run `bun install` for `bun.lock`.
- Run `bun run lock:nuget` for the NuGet `packages.lock.json` files after changing
  a project or package reference, `Directory.Packages.props`, the .NET SDK pin or
  the projects in `RunicSdk.Core.slnx`. CI restores with `--locked-mode`. When a
  rebase conflicts on lock files, take either side and rerun the command.
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
