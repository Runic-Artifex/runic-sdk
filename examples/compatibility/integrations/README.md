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
dotnet run --project integrations/RunicTranslations.Canary --configuration Release
dotnet run --project integrations/RunicCommandLine.Canary --configuration Release
dotnet run --project integrations/RunicFlow.Canary --configuration Release
dotnet run --project integrations/RunicAssets.Canary --configuration Release
```

| Canary | What it proves | Expected result |
| --- | --- | --- |
| `RunicTranslations.Canary` | Catalog compilation, source generation, and typed lookup | `Hello, Runic Artifex!` |
| `RunicCommandLine.Canary` | Protocol, catalog, hosting, and process packages | A managed/NativeAOT success message |
| `RunicFlow.Canary` | Headless process decisions and Application Bridge operation identity | A managed/NativeAOT success message |
| `RunicAssets.Canary` | Archive round-trip and CS-WebUI, ASP.NET Core, and Runic Toolkit adapters | An asset round-trip success message |

The Command Line, Flow, and Assets canaries also cover consumer paths intended
for NativeAOT publishing. See the package documentation for platform support
before choosing NativeAOT for an application.

## Select a package

| Problem | Install command | Documentation |
| --- | --- | --- |
| Generate strongly typed localized text | `dotnet add package RunicTranslations --prerelease` | [Runic Translations](https://docs.runic-artifex.eu/products/runic-translations) |
| Build a portable typed CLI | `dotnet add package RunicCommandLine --prerelease` | [Runic Command Line](https://docs.runic-artifex.eu/products/runic-command-line) |
| Coordinate typed, UI-independent work | `dotnet add package RunicFlow --prerelease` | [Runic Flow](https://docs.runic-artifex.eu/products/runic-flow) |
| Package and serve application assets | `dotnet add package RunicAssets --prerelease` | [Runic Assets](https://docs.runic-artifex.eu/products/runic-assets) |

For companion integrations, install the matching released package alongside the
core package: `RunicTranslations.Build`, `RunicTranslations.Generator`,
`RunicCommandLine.Abstractions`, `RunicCommandLine.Hosting`,
`RunicCommandLine.Processes`, `RunicFlow.ApplicationBridge`,
`RunicAssets.CsWebUi`, `RunicAssets.AspNetCore`, or
`RunicAssets.RunicToolkit`. The canary project files show the supported
consumer configuration without pinning a version in this guide.

[Package catalog and preview status](https://docs.runic-artifex.eu/packages) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/integrations) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues) ·
[MIT License](https://github.com/Runic-Artifex/runic-toolkit-examples/blob/main/LICENSE)
