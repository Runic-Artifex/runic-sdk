# Runic.Assets

`Runic.Assets` gives a .NET application one portable, validated description of
its static files. Embed a Vite build for NativeAOT-friendly deployment, use
explicit assembly resources for a small bundle, or use a refreshable directory
while developing—then attach the same `IAssetSource` to the host adapter you
choose.

## Install

```bash
dotnet add package Runic.Assets --prerelease
```

The package targets **.NET 10**, has no UI or web-framework dependency, and is
currently in preview. Install `Runic.Assets.AspNetCore` or
`Runic.Assets.Desktop` only when you also need that delivery integration.

## Embed a Vite build

After your Vite project's `npm run build` creates `dist`, add this to the .NET
application project:

```xml
<PropertyGroup>
  <RunicAssetsDist>../Client.Web/dist</RunicAssetsDist>
</PropertyGroup>
```

`dotnet build` incrementally creates a canonical archive and embeds it through
the packer's trusted generated-output snapshot mode. It works on Linux,
Windows, and macOS, rejects observed symbolic links and reparse points, and
assumes its frontend output is not concurrently hostilely mutated. Load
the default `Runic.Assets.StaticFiles` resource at runtime:

```csharp
using System.Reflection;
using Runic.Assets;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());
```

The default entry point is `index.html`. Set `RunicAssetsEntryPoint` for a
different entry file; set `RunicAssetsDistExclude` to omit files such as build
statistics. To use an existing canonical archive, set
`RunicAssetsEmbeddedArchive` instead of `RunicAssetsDist`. Set
`RunicAssetsEmbeddedResourceName` if you need a resource name other than
`Runic.Assets.StaticFiles`, and supply that name to `ReadEmbedded`.

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
using Runic.Assets;

var assets = new EmbeddedAssetSource(
    typeof(Program).Assembly,
    [
        new("index.html", "MyApp.Assets.index.html", IsEntryPoint: true),
        new("assets/app.css", "MyApp.Assets.app.css",
            CacheMode: AssetCacheMode.Immutable),
    ]);
```

Use `DevelopmentDirectoryAssetSource` only for the Linux development inner loop;
it requires Linux no-follow directory handles so root and ancestor replacement
cannot redirect a published source. It refreshes local files and marks them
`no-store`. Call `StartWatching` once to
coalesce filesystem signals into source-owned refreshes, then dispose its
`IAssetWatch` lease (or cancel its token) to stop it. Subscribe to
`IAssetSourceChangeNotifier.Changed` when a host needs to react to a successful
refresh; the source is the single publisher and each event contains immutable
previous/current manifest snapshots. Notifications are serialized and coalesced
after refresh, subscriber failures are isolated, and disposal stops future
refreshes. The watcher uses one fixed 8 KiB native event buffer and retries from
the authoritative directory scan after overflow, with at most three retries per
change burst. `IAssetSnapshotSource` is the transport boundary: its descriptor
and stream are one owned snapshot, so response entity tags and range metadata
describe exactly the bytes delivered. Reads materialize a verified temporary-file snapshot, so content that
changes after publication is rejected rather than delivered under the old digest.
Use an embedded source or an archive for a
reproducible deployed bundle.

## Guarantees and limits

`AssetPath` rejects rooted, traversal, encoded, ambiguous, and
control-character paths. `AssetManifest` is immutable and ordinally ordered.
Each entry carries media type, length, SHA-256 digest, strong entity tag,
Subresource Integrity token, and cache policy. CSP is host policy rather than
archive content: the archive cannot safely invent an application's script or
connect origins. ASP.NET Core delivery supports RFC conditional requests and
single byte ranges directly from this metadata. Archive reads validate declared content and can be bounded with
`AssetArchiveReadOptions` when input is not trusted.

`AssetArchive.Inspect` produces a deterministic report from the authoritative
manifest. `GetCompatibilityReport` and `MigrateAsync` make the schema policy
explicit: `runic.assets.archive/1` is retained, so compatible archives need no
migration and `MigrateAsync` validates then copies their bytes unchanged.

For application integration, consume `IAssetManifestProvider.Manifest` directly
for asset identity, SHA-256, entity tags, and cache metadata. Do not recreate a
parallel manifest in a hosting adapter.

This package deliberately does not expose HTTP endpoints or a Desktop handler;
choose a host adapter for that responsibility.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/Runic.Assets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)
