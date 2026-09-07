# Public API

Namespace: `Runic.Assets`

- `AssetPath`
- `AssetMediaTypes`
- `AssetCacheMode`
- `AssetDescriptor`
- `AssetManifest`
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
- `AssetArchiveCompatibilityReport`
- `EmbeddedAssetRegistration`
- `EmbeddedAssetSource`
- `DevelopmentDirectoryAssetSource` (Linux only)
- `DevelopmentDocumentAssets.WithDevelopmentDocument(IAssetSnapshotSource)`
- `DevelopmentDocumentAssets.WithDevelopmentDocument(IAssetSnapshotSource, string)`

The development-document extension reads an explicit absolute HTML file (maximum
1 MiB) once, replaces only the entry document, and retains all other manifest
assets. The parameterless form uses `RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT`;
without that variable it returns the original source. It does not serve a disk
directory or proxy arbitrary URLs.

The API is transport-neutral and depends only on the .NET base class library.
