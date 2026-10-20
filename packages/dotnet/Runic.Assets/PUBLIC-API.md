# Public API

Namespace: `Runic.Assets`

- `AssetPath`
- `AssetMediaTypes`
- `AssetCacheMode`
- `AssetDescriptor`
- `AssetManifest`
- `AssetRoutingOptions`
- `IAssetSource`
- `AssetReadSnapshot`
- `AssetArchive`
- `AssetArchiveReadOptions`
- `AssetArchiveSource`
- `AssetArchiveInspection`
- `EmbeddedAssetRegistration`
- `EmbeddedAssetSource`

Experimental (`RUNICASSETS001`, see docs/experimental.md), Linux only:

- `DevelopmentDirectoryAssetSource`
- `IAssetSourceChangeNotifier`
- `IAssetWatch`
- `AssetWatchOptions`
- `AssetSourceChangedEventArgs`

Replacing the entry document with a development bootstrap is not public API:
an application calling it in production would let an environment variable
replace its `index.html`. It is internal to development tooling.

The API is transport-neutral and depends only on the .NET base class library.
