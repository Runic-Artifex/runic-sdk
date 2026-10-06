# Runic.Assets.AspNetCore

Serve a Runic Assets manifest through ASP.NET Core GET and HEAD endpoints without
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

This serves the Vite entry point at `/` and `/index.html`, every manifest path
at its exact URL, and the entry point for missing paths without a file extension
(such as `/settings/profile`), so client-side routes survive a reload. Endpoints
mapped by the application take precedence over this catch-all route. Add an
optional prefix when assets should live below a route:

```csharp
app.MapRunicAssetSource(assets, "ui");
```

The Desktop adapter in `Runic.Assets.Desktop` resolves paths with the same
`AssetManifest.TryResolveRequestPath` rules and defaults, so one archive routes
identically on both hosts. Pass `AssetRoutingOptions` to change them:

```csharp
// Only exact manifest paths; the root and unknown paths return 404.
app.MapRunicAssetSource(assets, routing: new AssetRoutingOptions
{
    ServeEntryPointAtRoot = false,
    EnableSinglePageApplicationFallback = false,
});
```

The adapter accepts an `IAssetSnapshotSource`, which opens each asset's
descriptor and stream together. `AssetArchiveSource`, `EmbeddedAssetSource` and
`DevelopmentDirectoryAssetSource` implement it.

## HTTP behavior

Responses preserve the manifest-owned content type, length, cache-control
value, and strong `ETag`, and include `X-Content-Type-Options: nosniff`. `text/*`
content types without a charset are sent as UTF-8. `HEAD` returns the same
headers without a body.
Matching `If-None-Match` values receive `304 Not Modified`. Unknown and invalid
paths that the routing options do not send to the entry point return `404`; the
adapter never serves files outside the manifest.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/fixtures/assets/Runic.Assets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-sdk/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE)
