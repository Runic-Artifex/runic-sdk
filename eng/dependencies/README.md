# Dependency review

Run `bun run dependencies:audit > /tmp/runic-dependencies.json` in the SDK development
shell. The command reads maintained manifests, central NuGet versions, Bun lockfiles
and workflow/composite-action references, then queries npm, NuGet and GitHub release
metadata. It is read-only, bounds requests, and exits unsuccessfully if a registry
query fails. GitHub queries use the existing `gh` authentication. Review `latest`,
`rc`, `beta` and `next` separately; the command never selects or installs a candidate.

The dated JSON file records the initial 2026-09-08 audit, including declared/resolved
versions and upstream requirements. Historical imports, benchmark receipts and
archived source trees are excluded from updates. Native SDK headers, Nix inputs,
runtime releases and the local container image also need the checks below.

## September 2026 decisions

| Area | Decision and compatibility reason |
| --- | --- |
| .NET | Move SDK 10.0.302 to 10.0.400 and Extensions packages to 10.0.11; remain on the stable .NET 10 train. |
| Roslyn/MSBuild | Move Roslyn to 5.9.0 and MSBuild Framework to 18.9.6 together with the SDK. Exclude StringTools runtime assets from the inspector because MSBuildLocator must load the installed SDK's copy. |
| NuGet tooling | Update SourceLink, Test SDK, test adapter, ReactiveUI and its generator to current stable versions. Keep xUnit 2.9.3, coverlet 10.0.1, MVVM Toolkit 8.4.2, Build Locator 1.11.2 and CsWebUi beta.4.4, which are current in their package identities. |
| WebView2 | Update native SDK to 1.0.4191.47 and copy its target product version from `WebView2EnvironmentOptions.h`; validate COM vtable layout and NativeAOT on Windows. |
| Bun | Pin 1.4.2 in Nix, manifests, templates and CI; use upstream release archives with verified hashes for both Linux architectures. |
| Node/package managers | Test real Node 24.20.0 LTS, npm 12.0.2 and pnpm 12.3.4 in compatibility jobs. Nix currently packages Node 24.19.0; both satisfy the supported Node 24 range. |
| Actions | Upgrade cache to v6 and setup-node to v7. Checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8 and setup-bun v2 already track current release majors. |
| Local CI | Refresh nixpkgs to its 2026-09-07 revision and the Ubuntu 24.04 runner image to the current registry digest. Keep act 0.2.89 plus the documented artifact protocol patch: upstream has no newer stable release containing that fix. |
| TypeScript | Keep native compiler packages on 7.0.2. Upgrade Angular/Svelte/lint consumers only to 6.0.3: Angular requires `<6.1` and Svelte's checker/typescript-eslint do not yet support 7. |
| Effect | Adopt the explicitly requested 4.0.0-rc.112 as a coordinated API migration, covering the runtime, schema compiler, generated facades, consumers and templates. Do not mix Effect 3/4 services or widen peers to claim untested compatibility. |
| Web/framework packages | Align current stable versions across maintained packages and template manifests, regenerate each supported package manager's locks, and test real packed consumers. Assess DevTools 0.7.3 with devframe/crossws together. |

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
