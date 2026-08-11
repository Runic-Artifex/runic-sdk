# RunicAssets

`RunicAssets` is a framework-neutral static-asset boundary. It has no dependency on hosting, CS-WebUI, HTMX,
MVVM, a CSS system, or a browser framework.

- `EmbeddedAssetSource` serves explicitly mapped assembly resources directly,
  which supports offline, single-file, trimmed, and Native-AOT applications.
- `DevelopmentDirectoryAssetSource` provides refreshable local files with
  no-store caching for development only.
- `AssetArchive.ReadEmbedded` loads a build-generated canonical archive without
  extracting files to disk and supports single-file and NativeAOT applications.
- `AssetManifest` is immutable and ordinally ordered. Every entry carries its
  media type, byte length, SHA-256 digest, strong entity tag, and cache policy.
- `AssetPath` rejects rooted, traversal, encoded, ambiguous, and
  control-character paths.

Embedded resources must be declared in the application project and registered
explicitly:

```csharp
var assets = new EmbeddedAssetSource(
    typeof(Program).Assembly,
    [
        new("index.html", "MyApp.Assets.index.html", IsEntryPoint: true),
        new("assets/app.css", "MyApp.Assets.app.css",
            CacheMode: AssetCacheMode.Immutable),
    ]);
```

For a complete Vite build, automatic packing avoids per-file registrations:

```xml
<PropertyGroup>
  <RunicAssetsDist>..\Client.Web\dist</RunicAssetsDist>
</PropertyGroup>
```

```csharp
var assets = AssetArchive.ReadEmbedded(typeof(Program).Assembly);
```

The build target is incremental, uses `index.html` as the default entry point,
and accepts `RunicAssetsEntryPoint`, `RunicAssetsDistExclude`,
`RunicAssetsEmbeddedArchive`, and `RunicAssetsEmbeddedResourceName` overrides.
The generated archive is the portable `runic.assets.archive/1` format rather
than a host-specific virtual-filesystem image.

The package deliberately does not define HTTP endpoints or a CS-WebUI adapter.
Those layers translate `IAssetSource` into their own transport contracts.
