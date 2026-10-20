# Public API

Namespace: `Runic.Assets`

- `AssetPath`
- `AssetMediaTypes`
- `AssetCacheMode`
- `AssetDescriptor`
- `AssetManifest`
- `AssetRoutingOptions`
- `IAssetManifestProvider`
- `IAssetSource`
- `IAssetSnapshotSource`
- `AssetReadSnapshot`
- `IAssetSourceChangeNotifier`
- `IAssetWatch`
- `AssetWatchOptions`
- `AssetSourceChangedEventArgs`
- `AssetArchive`
- `AssetArchiveReadOptions`
- `AssetArchiveSource`
- `AssetArchiveInspection`
- `EmbeddedAssetRegistration`
- `EmbeddedAssetSource`
- `DevelopmentDirectoryAssetSource` (Linux only)

Replacing the entry document with a development bootstrap is not public API:
an application calling it in production would let an environment variable
replace its `index.html`. It is internal to development tooling.

The API is transport-neutral and depends only on the .NET base class library.
