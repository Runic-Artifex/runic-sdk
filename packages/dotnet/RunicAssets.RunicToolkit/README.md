# RunicAssets.RunicToolkit

Give a Runic Toolkit host a validated frontend asset provider backed by a
Runic Assets manifest. The integration translates the manifest and its entry
point into Runic Toolkit's hosting contract while retaining the original source
for verified reads.

## Install

```bash
dotnet add package RunicAssets.RunicToolkit --prerelease
```

The package targets **.NET 10** and is currently in preview. It brings
`RunicAssets` and the exact compatible
`RunicToolkit.Hosting.Abstractions` contract transitively. Choose this package
only when your Runic Toolkit host consumes `IFrontendAssetProvider`; use a
different Runic Assets adapter for direct ASP.NET Core or CS-WebUI delivery.

## Create a Toolkit frontend provider

Load an embedded Vite archive (or any `IAssetSource`) and hand the boundary to
the part of your Toolkit host that accepts an `IFrontendAssetProvider`:

```csharp
using System;
using System.Reflection;
using RunicAssets;
using RunicAssets.RunicToolkit;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());

var frontendAssets = new RunicToolkitAssetBoundary(
    assets,
    new Uri("app://my-application/"));

await frontendAssets.ValidateAsync();
```

The base URI must be absolute and cannot contain a query or fragment. The
boundary derives an escaped entry-point URI and exposes the Toolkit manifest;
it preserves the manifest's exact files, media types, lengths, SHA-256 digests,
and entry-point designation.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/RunicAssets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)
