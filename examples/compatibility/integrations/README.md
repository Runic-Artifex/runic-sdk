# Runic package integration canaries

Use these small programs when you want to verify that a released Runic package
works in a clean consumer application. They contain no product-source project
references and are especially useful before adopting a preview or publishing a
NativeAOT application.

## Prerequisites

Use .NET SDK 10.0.302, pinned in [`../global.json`](../global.json). Check the
public package catalog for the current availability of each preview artifact.

## Run a canary

```bash
dotnet run --project integrations/Runic.Translations.Canary --configuration Release
dotnet run --project integrations/Runic.CommandLine.Canary --configuration Release
dotnet run --project integrations/Runic.Assets.Canary --configuration Release
```

| Canary | What it proves | Expected result |
| --- | --- | --- |
| `Runic.Translations.Canary` | Catalog compilation, source generation, typed lookup, and a deterministic external locale-pack hot-swap proof: compose from a staged pack file, atomically replace it, reject a tampered pack, and recover | Three `PASS n:` lines ending in `HOT-SWAP CANARY PASS` |
| `Runic.CommandLine.Canary` | Protocol, catalog, hosting, and process packages | A managed/NativeAOT success message |
| `Runic.Assets.Canary` | Archive round-trip plus ASP.NET Core and Runic Desktop adapters | An asset round-trip success message |

The Command Line and Assets canaries also cover consumer paths intended
for NativeAOT publishing. See the package documentation for platform support
before choosing NativeAOT for an application.

## Select a package

| Problem | Install command | Documentation |
| --- | --- | --- |
| Generate strongly typed localized text | `dotnet add package Runic.Translations --prerelease` | [Runic Translations](https://docs.runic-artifex.eu/products/runic-translations) |
| Build a portable typed CLI | `dotnet add package Runic.CommandLine --prerelease` | [Runic Command Line](https://docs.runic-artifex.eu/products/runic-command-line) |
| Package and serve application assets | `dotnet add package Runic.Assets --prerelease` | [Runic Assets](https://docs.runic-artifex.eu/products/runic-assets) |

For companion integrations, install the matching released package alongside the
core package: `Runic.Translations.Build`, `Runic.Translations.Generator`,
`Runic.CommandLine.Abstractions`, `Runic.CommandLine.Hosting`,
`Runic.CommandLine.Processes`, `Runic.Assets.AspNetCore`, or
`Runic.Assets.Desktop`. The canary project files show the supported
consumer configuration without pinning a version in this guide.

[Package catalog and preview status](https://docs.runic-artifex.eu/packages) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/integrations) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues) ·
[MIT License](https://github.com/Runic-Artifex/runic-toolkit-examples/blob/main/LICENSE)
