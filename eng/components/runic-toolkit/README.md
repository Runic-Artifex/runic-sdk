# Runic Toolkit

Runic Toolkit is a NativeAOT-first application toolbelt for composing the same
application model across desktop windows, browser frontends, Generic Host, and
framework-specific UI adapters. It owns application lifecycle, frontend-neutral
desktop contracts, MVVM transport contracts, frontend build integration, and the
developer CLI. It does not own a markup language, workflow engine, command-line
framework, localization system, or asset model.

This repository is the clean-break successor to WebUIToolkit. Package,
assembly, namespace, MSBuild, protocol, npm, diagnostic, and tool identities use
`RunicToolkit.*`, `runic.toolkit.*`, `@runic-artifex/*`, `RTK*`, and
`dotnet-runic-toolkit`; no compatibility aliases are provided for the retired
identity.

## Package families

| Family | Purpose |
| --- | --- |
| `RunicToolkit.Hosting.*` | Deterministic lifecycle, Generic Host, WebUi, CsWebUi, build, and generator contracts |
| `RunicToolkit.MVVM*` | NativeAOT-safe wire/session contracts plus CommunityToolkit and ReactiveUI adapters |
| `RunicToolkit.Frontend.Sdk` | Framework-neutral frontend contracts, builds, manifests, and development metadata |
| `RunicToolkit.Desktop` | Frontend-neutral desktop capabilities and close contracts |
| `RunicToolkit.Collections` | Observable range collection primitives |
| `RunicToolkit.DotNet.RunicToolkit` | `dotnet runic-toolkit` development and diagnostic tool |

The web clients live under `web/packages` and use the GitHub-compatible npm
scope `@runic-artifex`.

## Independent products and integrations

Independent RunicArtifex products own their official Toolkit adapters:

- [Runic Markup](https://github.com/Runic-Artifex/runic-markup) owns
  `RunicMarkup.RunicToolkit.*`;
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
