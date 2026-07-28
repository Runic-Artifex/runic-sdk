# WebUIToolkit.Assets

`WebUIToolkit.Assets` is the framework-neutral static-asset boundary for
WebUIToolkit applications. It has no dependency on hosting, CsWebUi, HTMX,
MVVM, a CSS system, or a browser framework.

- `EmbeddedAssetSource` serves explicitly mapped assembly resources directly,
  which supports offline, single-file, trimmed, and Native-AOT applications.
- `DevelopmentDirectoryAssetSource` provides refreshable local files with
  no-store caching for development only.
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

The package deliberately does not define HTTP endpoints or a CsWebUi adapter.
Those layers translate `IAssetSource` into their own transport contracts.
