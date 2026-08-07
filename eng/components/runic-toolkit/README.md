# Runic Toolkit

Runic Toolkit is a NativeAOT-first application toolbelt for composing the same
application model across desktop windows, browser frontends, Generic Host, and
framework-specific UI adapters. It owns application lifecycle, frontend-neutral
desktop contracts, application-bridge contracts, frontend build integration,
and the developer CLI. It does not own a UI language, workflow engine,
command-line framework, localization system, or asset model.

This repository is the clean-break successor to WebUIToolkit. Package,
assembly, namespace, MSBuild, protocol, npm, diagnostic, and tool identities use
`RunicToolkit.*`, `runic.toolkit.*`, `@runic-artifex/*`, `RTK*`, and
`dotnet-runic-toolkit`; no compatibility aliases are provided for the retired
identity.

## Package families

| Family | Purpose |
| --- | --- |
| `RunicToolkit.Hosting.*` | Deterministic lifecycle, Generic Host, WebUi, CsWebUi, build, and generator contracts |
| `RunicToolkit.MVVM*` | Prerelease ViewModel projection experiment retained while the domain-oriented Application Bridge is proven |
| `RunicToolkit.Frontend.Sdk` | Framework-neutral frontend contracts, builds, manifests, and development metadata |
| `RunicToolkit.Desktop` | Frontend-neutral desktop capabilities and close contracts |
| `RunicToolkit.Collections` | Observable range collection primitives |
| `RunicToolkit.DotNet.RunicToolkit` | `dotnet runic-toolkit` development and diagnostic tool |

The web clients live under `web/packages` and use the GitHub-compatible npm
scope `@runic-artifex`.

## Independent products and integrations

Independent RunicArtifex products own their official Toolkit adapters:

- [Runic Flow](https://github.com/Runic-Artifex/runic-flow) owns
  `RunicFlow.RunicToolkit`;
- [Runic Assets](https://github.com/Runic-Artifex/runic-assets) owns
  `RunicAssets.RunicToolkit`;
- [Runic Command Line](https://github.com/Runic-Artifex/runic-command-line)
  will own `RunicCommandLine.RunicToolkit`.

Runic Text Resources is independently consumable and needs no Toolkit
dependency. Examples and cross-repository package canaries live in
[runic-toolkit-examples](https://github.com/Runic-Artifex/runic-toolkit-examples),
not in this library repository.

## Application Bridge direction

The generic MVVM protocol is not the long-term public application boundary.
[Issue #5](https://github.com/Runic-Artifex/runic-toolkit/issues/5) and
[ADR 0015](docs/adr/0015-effect-schema-application-bridge.md) define its
replacement: named domain commands and events, Effect Schema as the TypeScript
wire authority, deterministic committed contract artifacts, and reflection-free
C# dispatch. Existing prerelease MVVM packages remain available only while a
production Setup vertical proves the replacement.

## Development

On NixOS, enter the checked-in environment and run the standalone verification:

```bash
nix develop
./eng/verify.sh
```

The pipeline verifies clean identities, restores and builds the filtered
solution, runs the managed contract suites, verifies the npm workspaces, packs
the NuGet surface, and consumes it from an isolated local feed.

## License

Runic Toolkit is licensed under the [MIT License](LICENSE). Third-party
components retain their own licenses and notices.
