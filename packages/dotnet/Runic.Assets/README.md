# Runic.Assets

`Runic.Assets` gives a .NET application one portable, validated description of
its static files. Embed a Vite build for NativeAOT-friendly deployment, use
explicit assembly resources for a small bundle, or use a refreshable directory
while developing—then attach the same `IAssetSource` to the host
adapter you choose.

## Runic.Assets or the Views `www/` folder

Runic has two ways to ship a frontend, and they do not share settings:

- **Runic.Application.Views** applications build `RunicApplicationFrontendDirectory`
  and copy its `dist/` as loose files to `www/` next to the executable; the
  Views host serves that folder. This is the default for Runic Views apps, and
  `dotnet runic dev` works with it. Without a Vite or Angular development
  server, `dev` has the Runic Desktop host serve `dist/` itself through
  `DevelopmentDirectoryAssetSource`, before `www/`, and reload its windows when
  `dist/` changes.
- **Runic.Assets** packs `RunicAssetsDist` into one validated archive and embeds
  it in the assembly. Use it for ASP.NET Core and for Runic Desktop applications
  without Views, or wherever a single-file or NativeAOT deployment must carry
  its frontend inside the executable. `Runic.Assets.AspNetCore` and
  `Runic.Assets.Desktop` serve it.

A Views application on Runic Desktop embeds its frontend with one build
property, `<RunicApplicationFrontendDelivery>Embedded</RunicApplicationFrontendDelivery>`:
the Views build composes what it would copy to `www/`, including the host's
`runic-views.js`, packs it as `RunicAssetsDist`, and the host serves the archive
without application code; `dotnet runic dev` keeps serving the live frontend.
See [Ship the frontend inside the executable](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views/README.md#ship-the-frontend-inside-the-executable).
Do not set `RunicAssetsDist` in such a project. To serve an archive from
elsewhere, set `RunicDesktopHostOptions.Assets`.

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
statistics (by default `runic-assets.zip`; the value replaces that default, and
an empty `<RunicAssetsDistExclude></RunicAssetsDistExclude>` in the project file
packs every file). `.git` directories and files whose names start with `.env`
(such as `.env.local` or `.envrc`) are never packed unless you set
`RunicAssetsDefaultExcludes` to `false`; a packed source map (`*.map`) is build
warning `RAS1005`, which `<NoWarn>$(NoWarn);RAS1005</NoWarn>` silences. To use
an existing canonical archive, set
`RunicAssetsEmbeddedArchive` instead of `RunicAssetsDist`. Set
`RunicAssetsEmbeddedResourceName` if you need a resource name other than
`Runic.Assets.StaticFiles`, and supply that name to `ReadEmbedded`.
To produce an archive in a separate step, run `dotnet runic assets pack`, the
packer's command in the `dotnet-runic` tool; see the
[packer README](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/Runic.Assets.Packer/README.md).

Directory archives mark a file `Immutable` (`public, max-age=31536000,
immutable`) only when its name carries a content hash, such as Vite's
`index-Cf3tzbYH.js`, SvelteKit's `start.D2kX0q3e.js` or webpack's
`main.<hex>.js`. Every other file, including `.html`/`.htm` documents and
unhashed `public/` files such as `favicon.ico`, `robots.txt` or `sw.js`, uses
`Revalidate` with its strong entity tag.

## Archives from code

`AssetArchive.WriteTrustedDirectoryAsync` packs a directory that your own build
produced, on Linux, Windows and macOS, with the same snapshot rules as the
build: it rejects the symbolic links, and on Windows the junctions and other
reparse points, that it sees, and it assumes that nobody changes the directory
while it reads it. `AssetArchiveDirectoryOptions` selects the entry point
(`index.html` by default), exact `ExcludedPaths`, and `ExcludeDefaultPatterns`,
which leaves `.git` directories and `.env*` files out (`true` by default). On
Linux, `AssetArchive.WriteDirectoryAsync(directory, destination, options,
cancellationToken)` reads through pinned no-follow handles instead, for a
directory that a less trusted process can write. The older
`WriteDirectoryAsync(directory, destination, entryPoint, excludedPaths)` overload
packs every file it is not told to exclude.

```csharp
await using FileStream output = File.Create("artifacts/app.runic-assets");
await AssetArchive.WriteTrustedDirectoryAsync(
    "Client.Web/dist",
    output,
    new AssetArchiveDirectoryOptions { ExcludedPaths = ["stats.html"] },
    cancellationToken);
```

`AssetArchive.ReadAsync(stream, options, cancellationToken)` loads an archive
from a stream without blocking, for example from an HTTP response or a file
opened for asynchronous reads; the stream need not be seekable. It applies the
same `AssetArchiveReadOptions` limits and validation as `AssetArchive.Read`.

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

Use `DevelopmentDirectoryAssetSource` for a development inner loop on Linux,
Windows or macOS. `dotnet runic dev` uses it when no Vite or Angular
development server runs: the Runic Desktop window serves the frontend output
directory and reloads when it changes
([dotnet-runic](https://github.com/Runic-Artifex/runic-sdk/blob/main/tools/dotnet-runic/README.md)).
The source refreshes local files and marks them `no-store`. No path component
below the root may be a symbolic link or reparse point. On Linux and macOS the
source pins its root with one no-follow directory handle and opens every file
relative to it, so replacing the root or an ancestor cannot redirect a
published source. Windows checks each path component for a reparse point
before it opens a file, but does not pin the root: a directory moved into the
root's place is read from then on. That is enough for a development source that
serves your own build output. Entries whose names are not valid asset paths (for example
containing `:`, `?`, `#` or a backslash) are skipped and listed in
`SkippedEntries` instead of failing the scan.

```csharp
using var source = new DevelopmentDirectoryAssetSource(frontendDirectory, "index.html");
source.Changed += (_, change) => Reload(change.Current);
source.StartWatching(new AssetWatchOptions { DebounceDelay = TimeSpan.FromMilliseconds(100) });
// Dispose the source to stop watching.
```

Subscribe to `Changed` (`IAssetSourceChangeNotifier`) first, then call
`StartWatching` once: it coalesces filesystem signals into source-owned
refreshes and runs until the source is disposed, which is the only way to stop
it; a second call throws `InvalidOperationException`. `RefreshAsync` scans
the directory on demand and returns the current manifest; refreshes run one at
a time. The source is the single publisher and each event contains immutable
previous/current manifest snapshots. Notifications are serialized and coalesced
after refresh, subscriber failures are isolated, and disposal stops future
refreshes. The watcher uses one fixed 8 KiB native event buffer and retries from
the authoritative directory scan after overflow, or after a scan that fails
because a build left the directory half written, with at most three retries per
change burst by default (`AssetWatchOptions.MaxRetryAttempts`). Reads materialize a verified temporary-file snapshot, so content that
changes after publication is rejected rather than delivered under the old digest.
Use an embedded source or an archive for a
reproducible deployed bundle.

## Sources

Every source implements `IAssetSource`: its immutable `Manifest`,
`ValidateAsync`, and `OpenAsync(relativePath)`, which returns an
`AssetReadSnapshot`. The snapshot's `Descriptor` and `Content` stream are one
owned unit, so response entity tags and range metadata describe exactly the
bytes delivered; dispose the snapshot to close the stream. A custom source
implements these three members. `OpenAsync` throws `FileNotFoundException` for a
path the manifest does not declare.

## Guarantees and limits

`AssetPath` rejects rooted, traversal, encoded, ambiguous, and
control-character paths. `AssetPath.TryNormalize`, `AssetManifest.TryGetAsset`
and `AssetManifest.TryResolveRequestPath` return `false` for such paths instead
of throwing, so request handlers can pass untrusted paths to them directly.
`AssetManifest` is immutable and ordinally ordered.
Each entry carries media type, length, SHA-256 digest, strong entity tag,
Subresource Integrity token, and cache policy. A Content-Security-Policy is
host policy rather than archive content, because the archive cannot know an
application's script or connect origins. Set it with
`AssetRoutingOptions.ContentSecurityPolicy`, which the ASP.NET Core and Desktop
adapters send with HTML assets, or with `DesktopSurfaceOptions.ContentSecurityPolicy`
for a whole Runic Desktop surface. ASP.NET Core delivery supports RFC conditional requests and
single byte ranges directly from this metadata. Archive reads validate declared content and can be bounded with
`AssetArchiveReadOptions` when input is not trusted.

`AssetArchive.Inspect` returns an archive's schema version, its manifest and the
SHA-256 digest of the canonical manifest bytes. Readers accept only the current
`runic.assets.archive/1` schema and reject incompatible archive versions.

For application integration, consume `IAssetSource.Manifest` directly
for asset identity, SHA-256, entity tags, and cache metadata. Do not recreate a
parallel manifest in a hosting adapter.

This package deliberately does not expose HTTP endpoints or a Desktop handler;
choose a host adapter for that responsibility.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/fixtures/assets/Runic.Assets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-sdk/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE)
