# RunicAssets

`RunicAssets` gives a .NET application one portable, validated description of
its static files. Embed a Vite build for NativeAOT-friendly deployment, use
explicit assembly resources for a small bundle, or use a refreshable directory
while developing—then attach the same `IAssetSource` to the host adapter you
choose.

## Install

```bash
dotnet add package RunicAssets --prerelease
```

The package targets **.NET 10**, has no UI or web-framework dependency, and is
currently in preview. Install `RunicAssets.AspNetCore`,
`RunicAssets.CsWebUi`, or `RunicAssets.RunicToolkit` only when you also need
that delivery integration.

## Embed a Vite build

After your Vite project's `npm run build` creates `dist`, add this to the .NET
application project:

```xml
<PropertyGroup>
  <RunicAssetsDist>../Client.Web/dist</RunicAssetsDist>
</PropertyGroup>
```

`dotnet build` incrementally creates a canonical archive and embeds it. Load
the default `RunicAssets.StaticFiles` resource at runtime:

```csharp
using System.Reflection;
using RunicAssets;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());
```

The default entry point is `index.html`. Set `RunicAssetsEntryPoint` for a
different entry file; set `RunicAssetsDistExclude` to omit files such as build
statistics. To use an existing canonical archive, set
`RunicAssetsEmbeddedArchive` instead of `RunicAssetsDist`. Set
`RunicAssetsEmbeddedResourceName` if you need a resource name other than
`RunicAssets.StaticFiles`, and supply that name to `ReadEmbedded`.

## Small explicit bundles

For a few hand-authored files, declare assembly resources in your application
project and map them explicitly:

```xml
<ItemGroup>
  <EmbeddedResource Include="Assets/index.html" />
  <EmbeddedResource Include="Assets/app.css" />
</ItemGroup>
```

```csharp
using RunicAssets;

var assets = new EmbeddedAssetSource(
    typeof(Program).Assembly,
    [
        new("index.html", "MyApp.Assets.index.html", IsEntryPoint: true),
        new("assets/app.css", "MyApp.Assets.app.css",
            CacheMode: AssetCacheMode.Immutable),
    ]);
```

Use `DevelopmentDirectoryAssetSource` only for the development inner loop; it
refreshes local files and marks them `no-store`. Use an embedded source or an
archive for a reproducible deployed bundle.

## Guarantees and limits

`AssetPath` rejects rooted, traversal, encoded, ambiguous, and
control-character paths. `AssetManifest` is immutable and ordinally ordered.
Each entry carries media type, length, SHA-256 digest, strong entity tag, and
cache policy. Archive reads validate declared content and can be bounded with
`AssetArchiveReadOptions` when input is not trusted.

This package deliberately does not expose HTTP endpoints or a CS-WebUI handler;
choose a host adapter for that responsibility.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/RunicAssets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)
