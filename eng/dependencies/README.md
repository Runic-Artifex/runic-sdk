# Dependency review

Run `bun run dependencies:audit > /tmp/runic-dependencies.json` in the SDK development
shell. The command reads maintained manifests, central NuGet versions, Bun lockfiles
and workflow/composite-action references, then queries npm, NuGet and GitHub release
metadata. It is read-only, bounds requests, and exits unsuccessfully if a registry
query fails. GitHub queries use the existing `gh` authentication. Review `latest`,
`rc`, `beta` and `next` separately; the command never selects or installs a candidate.

`eng/toolchain.mjs` reads the actual maintained SDK, Node, Bun, npm and pnpm pins;
dependency audits and template lock generation use that reader, not a release receipt.

Audit output is not committed; rerun the command for current versions. Historical
imports, benchmark receipts and archived source trees are excluded from updates.
Native SDK headers, Nix inputs, runtime releases and the local container image also
need the checks below.

## October 2026 decisions

| Area | Decision and compatibility reason |
| --- | --- |
| .NET SDK | Move SDK 10.0.400 to 10.0.401 (runtime 10.0.12, the September security release). nixpkgs packages exactly 10.0.401, and CI's setup-dotnet installs it from `global.json`. The flake now fails evaluation when the nixpkgs SDK differs from `global.json`, which disables roll-forward. |
| Node | Move to Node 24.21.0, the latest Node 24 LTS release. nixpkgs `nodejs_24` now packages the same version, which resolves September's 24.19/24.20 mismatch. The flake asserts it matches `.node-version`. Stay on Node 24: Node 26 (26.10.0) is still Current, not LTS. Vite 8.3 and SvelteKit 3 allow both; Angular 22.2 allows `>=26`. Moving would also mean widening every package and starter `engines` range (`>=24.18.0 <25`). Recheck after Node 26 enters LTS. |
| Bun/npm/pnpm | Bun 1.4.2 is still the latest stable release, so it stays. Move npm 12.0.2 to 12.2.0 and pnpm 12.3.4 to 12.9.1 in the flake (verified archive hashes), CI setup and starter defaults. pnpm 12 records its own launcher in the first document of each starter's `pnpm-lock.yaml`. Regenerate that document with the pinned pnpm, because `--frozen-lockfile` rejects a different version. |
| Actions | Move `microsoft/setup-msbuild` v2.0.0 to v3.0.0 (commit SHA). v3 only moves the action to the Node 24 runtime, and the job uses no inputs. setup-bun v2.2.0 and NuGet/login v1.2.0 are already current. First-party actions keep major tags: checkout v7, cache v6, upload-artifact v7, download-artifact v8, setup-dotnet v6 and setup-node v7 are the current majors. |
| Runners | Keep `ubuntu-24.04`. `ubuntu-26.04` is available, but `ubuntu-latest` still maps to 24.04. Moving would also change local act's 24.04 runner image, its apt package set and the native WebKit/GTK baseline. Move all three together in a separate change. `windows-latest` is Windows Server 2025 with VS 2026; `macos-26` is current. |
| Nix/local CI | Refresh nixpkgs from 2026-09-07 to 2026-09-30. The `catthehacker/ubuntu:act-24.04` index digest is unchanged, so the local runner image stays as is. Rename the deprecated `xorg.xorgserver`/`xorg.xf86videodummy` attributes in the KDE Xorg container. |
| Patches | Keep both. act 0.2.89 is still the latest release and nektos/act#6115 is still open. nixpkgs still packages AT-SPI 2.60.6. Upstream 2.60.7, 2.62.0.1 and `main` still send the `Embedded` message without releasing it. |
| NuGet | Move Extensions and System.DirectoryServices.Protocols to 10.0.12, Test SDK to 18.10.1, coverlet to 10.1.0, SourceLink to 10.0.401 (matching the 10.0.401 SDK), CsWin32 to 0.3.346 and WebView2 to 1.0.4258.31 (target product version 154.0.4258.31; its `WebView2.h` is unchanged, so the hand-written vtables and IIDs still match). MetadataLoadContext moves to 10.0.12: it was not held for MSBuild or analyzer loading, and nothing has referenced it since the inspector was removed. Move ReactiveUI/ReactiveUI.Reactive to 26.0.1 and Binding to 9.1.0. The public API is unchanged; the majors drop .NET 11 runtime-async, and Primitives 9 binds a lone `SubscribeSafe` lambda to `onNext`. Runic has no such calls. The VS extension takes VSSDK.BuildTools 18.9.820, a build-only change: the in-process API stays at 17.14 and the host range stays `[17.14,19.0)`. Only the Windows CI job checks the packaged VSIX. Keep MSBuild Framework/StringTools at 18.9.6. SDK 10.0.400 ships MSBuild 18.9.6, and 10.0.401 ships 18.9.11, which is not on NuGet; 18.9.6 is the newest published reference that both hosts satisfy. 18.10.1 is the VS 18.10 / 10.0.5xx MSBuild, and no released SDK carries it yet. The translations build task runs in the host MSBuild, so move with the first 10.0.5xx SDK. Keep xUnit 2.9.3 and the runner 4.0.0, which are current. |

## September 2026 decisions

| Area | Decision and compatibility reason |
| --- | --- |
| .NET | Move SDK 10.0.302 to 10.0.400 and Extensions packages to 10.0.11; remain on the stable .NET 10 train. |
| Roslyn/MSBuild | Move Roslyn to 5.9.0 and MSBuild Framework to 18.9.6 together with the SDK. Exclude StringTools runtime assets from the inspector because MSBuildLocator must load the installed SDK's copy. |
| NuGet tooling | Update SourceLink, Test SDK, test adapter, ReactiveUI and its generator to current stable versions. Keep xUnit 2.9.3, coverlet 10.0.1, MVVM Toolkit 8.4.2, Build Locator 1.11.2 and CsWebUi beta.4.4, which are current in their package identities. |
| WebView2 | Update native SDK to 1.0.4191.47 and copy its target product version from `WebView2EnvironmentOptions.h`; validate COM vtable layout and NativeAOT on Windows. |
| Bun | Pin 1.4.2 in Nix, manifests, templates and CI; use upstream release archives with verified hashes for both Linux architectures. |
| Node/package managers | Test real Node 24.20.0 LTS, npm 12.0.2 and pnpm 12.3.4 in compatibility jobs. Nix currently packages Node 24.19.0; both satisfy the supported Node 24 range. The development shell also pins npm 12.0.2 and the native pnpm 12.3.4 launcher. |
| Actions | Upgrade cache to v6 and setup-node to v7. Checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8 and setup-bun v2 already track current release majors. |
| Local CI | Refresh nixpkgs to its 2026-09-07 revision and the Ubuntu 24.04 runner image to the current registry digest. Keep act 0.2.89 plus the documented artifact protocol patch: upstream has no newer stable release containing that fix. |
| TypeScript | Keep native compiler packages on 7.0.2. Upgrade Angular/Svelte/lint consumers only to 6.0.3: Angular requires `<6.1` and Svelte's checker/typescript-eslint do not yet support 7. |
| Effect | Adopt the explicitly requested 4.0.0-rc.112 as a coordinated API migration, covering the runtime, schema compiler, generated facades, consumers and templates. Do not mix Effect 3/4 services or widen peers to claim untested compatibility. |
| Web/framework packages | Align current stable versions across maintained packages and template manifests, regenerate each supported package manager's locks, and test real packed consumers. Use DevTools/kit 0.5.2 with devframe 0.9.16. Vite 8.2.2 only accepts DevTools `^0.4 || ^0.5`; 0.7.3 fails clean npm peer resolution. The Bun browser check verifies the real dock renders and receives live diagnostics through the upstream SSE transport. |

Vue 3.5.42 / vue-tsc 3.3.11 still fails to resolve `.vue` imports when its checker
runs under Bun 1.4.2. Keep the documented Node compatibility typecheck; Bun-only
Vue production builds remain required.

Vite 8.3.0-beta.1 accepts DevTools `^0.7.1`, so it is the next candidate for
DevTools 0.7.3. It is deferred because the latest React, Vue and SvelteKit plugin
peer ranges exclude that Vite prerelease. A clean npm 12 install with Vite
8.3.0-beta.1, DevTools 0.7.3 and the React plugin 6.1.1 fails with `ERESOLVE`.
Recheck these upstream peers before updating the coordinated stack; do not require
starter users to bypass dependency validation. DevTools 0.5.2 remains an upgrade
from the previously committed 0.4.12.

Regenerate starter lockfiles after building web packages with
`bun eng/dependencies/update-template-locks.mjs`. It packs local candidates into a
temporary loopback registry, runs the authority-pinned Bun/npm/pnpm resolvers, then
removes temporary URLs and deletes its temporary workspace. No packages are published.
The template acceptance step rebinds hashes and npm 12 registry URLs to its own exact
candidates. pnpm 12 needs its installation script to materialize its native launcher.

The web wave passed the complete build, documentation checks, editor checks, Svelte
and SvelteKit tests, and installed npm consumers. The DevTools tests also check real
Node startup and Chromium rendering with Bun. Retain `@types/cookie` 0.6.0: 1.0 is a
deprecated stub for modern cookie versions, while SvelteKit still uses cookie 0.6.

## Effect 4 migration

The bridge, Desktop transport, compiler, framework consumers and starters pin
`4.0.0-rc.112` together. Regenerate C#-authority facades with `contract:generate`;
they now expose `Schema.Codec` and Effect 4 checks. Effect-authority applications
use `Schema.Union([schemas])`, `Schema.Literals([values])`, `.annotate(...)`, and
`.check(Schema.isBetween({ minimum, maximum }))` in place of the Effect 3 forms.
Strict TypeScript projects using the runtime declarations need `ESNext.Disposable`
in their `compilerOptions.lib` alongside their existing platform libraries.

The runtime uses context-bound runners and `Layer.effect` with scoped finalizers.
The controller still returns an exit from `interrupt`; it waits for both
interruption and completion. Frame consumption starts immediately, and event/error
subscriptions are acquired before the merged readers start, preserving synchronous
host replies under Effect 4's scheduling. The compiler reads v4 AST checks and
keeps the existing named integer wire definition. Boilerplate descriptions supplied
implicitly by Effect 3 disappear from generated documentation; explicit descriptions
and wire identities remain supported.

## Separately maintained repositories

This SDK wave inventories the adjacent repositories without changing their release
boundaries. `cs-webui` has its own central NuGet pins, .NET SDK and Nix lock, plus an
upstream React example using react-scripts. `local-planning` maintains its SvelteKit
portal, Mermaid, Zod and YAML tooling separately. `runic-brand` owns font/image
tooling; `runic-site` owns the marketing SvelteKit app; `runic-flow` and `runic-markup`
own separate .NET/Nix toolchains, with a VS Code client in `runic-markup`. Their
updates require their own repository checks and commits. Historical SDK imports and
frozen legacy-example dependencies remain excluded; the current SDK acceptance
consumers are updated with the runtime they exercise.

Upgrade waves are reviewed by the maintainer through their commits. Validate each
wave with affected suites, then use the real SDK CI workflow for package consumers,
both-host development, NativeAOT, footprint and supported operating systems. Revert
a wave's commits as a unit, including lockfiles and generated files, if rollback is
needed. Package publication remains a separate release operation.

Sources: [Effect migration](https://github.com/Effect-TS/effect/blob/main/MIGRATION.md),
[Bun 1.4.2](https://bun.sh/blog/bun-v1.4.2),
[.NET release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json),
[cache v6.1.0](https://github.com/actions/cache/releases/tag/v6.1.0),
[setup-node v7](https://github.com/actions/setup-node/releases/tag/v7.0.0),
[act releases](https://github.com/nektos/act/releases),
[Node releases](https://nodejs.org/dist/index.json),
[setup-msbuild v3](https://github.com/microsoft/setup-msbuild/releases/tag/v3),
[runner images](https://github.com/actions/runner-images#available-images).
