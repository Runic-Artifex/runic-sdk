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

The Vue checker limitation under Bun is still present; see the October notes below.
Keep the documented Node compatibility typecheck; Bun-only Vue production builds
remain required.

The September DevTools deferral (Vite 8.2 accepted only DevTools `^0.4 || ^0.5`)
is resolved by the October move to stable Vite 8.3 and DevTools 0.7.6.

Regenerate starter lockfiles after building web packages with
`bun eng/dependencies/update-template-locks.mjs`. It packs local candidates into a
temporary loopback registry, runs the authority-pinned Bun/npm/pnpm resolvers, then
removes temporary URLs and deletes its temporary workspace. No packages are published.
The template acceptance step rebinds hashes and npm 12 registry URLs to its own exact
candidates. pnpm 12 needs its installation script to materialize its native launcher.

The web wave passed the complete build, documentation checks, editor checks, Svelte
and SvelteKit tests, and installed npm consumers. The DevTools tests also check real
Node startup and Chromium rendering with Bun. In September `@types/cookie` stayed on
0.6.0 because SvelteKit 2 used cookie 0.6; SvelteKit 3 removed that reason (see
below).

## October 2026 decisions

| Area | Decision and compatibility reason |
| --- | --- |
| Web patch/minor | Align Angular 22.2.1, React/React DOM and their types 19.3.0, Svelte 5.57.1 with vite-plugin-svelte 7.3.1, Vue 3.5.43 with plugin-vue 6.0.9 and vue-tsc 3.3.12, Vitest 5.0.3, happy-dom 20.14.5, ESLint 10.12.0, typescript-eslint 8.71.0, Prettier 3.9.9, globals 17.13.0, bits-ui 2.19.5, shadcn-svelte 1.7.0, @lucide/svelte 1.52.0, tailwind-merge 3.7.0 and tslib 2.8.1 across workspace packages, docs, the editor, examples, starters and the packed-consumer checks. |
| Effect | Move the remaining `4.0.0-rc.112` pins (workspace root, the SvelteKit package's development dependency and the packed-consumer check) to the final 4.0.0. The Views runtime replaced the Effect-based Application Bridge, so the starters and runtime no longer pin Effect. |
| Vite and DevTools | Adopt Vite 8.3.2 with `@vitejs/devtools` and `devtools-kit` 0.7.6 (devframe 1.2.0). Vite 8.3 accepts only DevTools `^0.7.1`; the React 6.1.1, Vue 6.0.9 and Svelte 7.3.1 Vite plugins accept `^8.0.0`. A clean `npm install --strict-peer-deps` of that stack, SvelteKit 3 and the packed Runic Vite, Svelte and SvelteKit packages resolves. The kit API used by the dock is unchanged and the Bun Chromium dock test passes. `vite-plugin-runic` raises its optional DevTools peer to `^0.7.6`. |
| SvelteKit | Adopt `@sveltejs/kit` 3.0.0, `@sveltejs/package` 3.0.0 and `adapter-static` 4.0.0. The docs, editor and fixtures were migrated with `sv migrate sveltekit-3`, keeping only its `#lib` specifier rewrites. Configuration moves into `sveltekit()` in `vite.config.ts`, and tsconfigs extend `$app/tsconfig`. No ecosystem package blocks the move: vite-plugin-svelte 7.3.1, svelte-check 4.7.6, eslint-plugin-svelte 3.23.0 and prettier-plugin-svelte 4.1.1 accept it, bits-ui does not depend on Kit, and shadcn-svelte 1.7.0 resolves `#lib` subpath aliases. `@runic-artifex/sveltekit` now peers on Kit `>=3 <4`, adapter-static `>=4 <5`, Svelte `>=5.57.1` and Vite `>=8.0.12`, matching Kit 3's requirements. Kit 2 is no longer tested. The Svelte starter uses vite-plugin-svelte without Kit. |
| TypeScript | Keep 6.0.3 everywhere; no maintained manifest still pins a 7.x compiler. TypeScript 7.0.2 is deferred because Angular 22.2.1 (`compiler-cli`, `build`) peers `>=6.0 <6.1`, typescript-eslint 8.71.0 peers `<6.1.0`, svelte-check 4.7.6 peers `^5 \|\| ^6`, and SvelteKit 3 and `@sveltejs/package` 3 peer `^6.0.0`. |
| `@types/node` | 24.19.1, the latest 24.x. The toolchain stays on Node 24 LTS; Node 26 is not LTS yet. |
| `@types/cookie` | Removed. SvelteKit 3 uses cookie 2, which ships its own types, and the locale handle now derives its options from `Cookies.set`. |
| VS Code extension | Move `vscode-languageclient` to 10.1.2, which requires VS Code `^1.91` and a `LogOutputChannel` and no longer ships `terminateProcess.sh`, and `@vscode/vsce` to 4.0.0. Keep `@types/vscode` 1.100.0 to match `engines.vscode` `^1.100.0`; types newer than the engine floor would require raising it without an API need. |
| Vue checker under Bun | Rechecked with Vue 3.5.43 and vue-tsc 3.3.12. Under Bun 1.4.2 the checker still omits `.vue` files: `--listFilesOnly` lists none and a deliberate template prop error passes, while Node reports it. Keep the Node compatibility typecheck. |

## Effect 4 migration

The bridge, Desktop transport, compiler, framework consumers and starters pinned
`4.0.0-rc.112` together; the remaining pins now use 4.0.0 (see October decisions). Regenerate C#-authority facades with `contract:generate`;
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
[act releases](https://github.com/nektos/act/releases).
