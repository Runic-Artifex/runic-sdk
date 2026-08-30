# Runic.Assets.AspNetCore

Serve a Runic Assets manifest through exact ASP.NET Core GET endpoints without
recreating its media types, cache policy, content lengths, or entity tags.

## Install

```bash
dotnet add package Runic.Assets.AspNetCore --prerelease
```

The package targets **.NET 10**, uses the shared `Microsoft.AspNetCore.App`
framework, and brings `Runic.Assets` with it. It is currently a preview package.
Choose this adapter for ASP.NET Core; choose the core package alone when you
only need an asset source or archive.

## Map an embedded Vite build

After configuring `RunicAssetsDist` in the web application's project and
building the Vite `dist` directory, map the source in `Program.cs`:

```csharp
using System.Reflection;
using Runic.Assets;
using Runic.Assets.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());

app.MapRunicAssetSource(assets);
app.Run();
```

This maps the exact manifest paths, so the Vite entry point above is available
at `/index.html`. Add an optional prefix when assets should live below a route:

```csharp
app.MapRunicAssetSource(assets, "ui");
```

## HTTP behavior

Responses preserve the manifest-owned content type, length, cache-control
value, and strong `ETag`, and include `X-Content-Type-Options: nosniff`.
Matching `If-None-Match` values receive `304 Not Modified`. Unknown or invalid
paths return `404`; the adapter does not infer an SPA fallback or serve files
outside the manifest.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/Runic.Assets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)
