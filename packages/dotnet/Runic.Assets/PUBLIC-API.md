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
- `DevelopmentDirectoryAssetSource` (Linux, Windows and macOS)
- `IAssetSourceChangeNotifier`
- `AssetWatchOptions`
- `AssetSourceChangedEventArgs`

No API lets an environment variable replace an application's content. The
Runic Desktop Views host reads the directory that `dotnet runic dev` passes
(`RUNIC_APPLICATION_DEVELOPMENT_ASSETS`, an internal contract) only in a
`Development` environment and only when the application sets no surface
options of its own.

The API is transport-neutral and depends only on the .NET base class library.
