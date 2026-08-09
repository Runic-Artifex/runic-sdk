![Runic Assets banner](.github/assets/brand/banner.png)

# Runic Assets

Runic Assets is a framework-neutral static-asset model for .NET applications.
It provides safe paths, immutable manifests, embedded and development sources,
and a portable ZIP archive. Its core is trimming- and NativeAOT-compatible and
does not depend on a UI or web framework.

This repository was extracted from Runic Toolkit with its product history
intact. It uses independent `RunicAssets*` package, assembly, namespace, and
archive identities without compatibility aliases for the retired Toolkit-owned
identity.

## Projects

| Project | Purpose |
| --- | --- |
| `RunicAssets` | Transport-neutral contracts, sources, validation, media types, portable archives, and incremental `dist` embedding |
| `RunicAssets.CsWebUi` | Assets-owned direct HTTP response adapter over CsWebUi's custom file handler |
| `RunicAssets.AspNetCore` | Exact ASP.NET Core endpoints with cache and entity-tag metadata |
| `integrations/RunicAssets.RunicToolkit` | Published Toolkit frontend-asset integration owned by Runic Assets |

The Toolkit adapter is part of the standalone solution and consumes Toolkit
contracts as exact packages. This preserves the dependency direction: adapters
depend on both products; neither core depends on an integration.

## Archives

`AssetArchive` writes a standard ZIP containing a canonical
`runic-assets.json` manifest and declared files below `assets/`. Paths and
metadata are validated on read, undeclared content is rejected, and no private
host-specific archive format is required.

## Embed a Vite build

The `RunicAssets` package can turn a complete Vite `dist` directory into a
canonical metadata-bearing archive and embed it during the application build:

```xml
<PropertyGroup>
  <RunicAssetsDist>..\Client.Web\dist</RunicAssetsDist>
</PropertyGroup>
```

Load the archive without extraction. It remains embedded in single-file and
NativeAOT applications:

```csharp
using System.Reflection;
using RunicAssets;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());
```

Packing is incremental and reruns when the project, target, packer, or a file
below `RunicAssetsDist` changes. HTML files use revalidation caching; built
non-HTML assets use immutable caching, matching the conventional Vite output
model. Every file receives deterministic media type, length, SHA-256, ETag,
and cache metadata in the archive manifest.

The entry point defaults to `index.html`. Configure it and exact exclusions
when needed:

```xml
<PropertyGroup>
  <RunicAssetsEntryPoint>app.html</RunicAssetsEntryPoint>
  <RunicAssetsDistExclude>runic-assets.zip;stats.html</RunicAssetsDistExclude>
</PropertyGroup>
```

`RunicAssetsEmbeddedArchive` embeds an externally produced canonical Runic
Assets archive instead of packing a directory. `RunicAssetsEmbeddedResourceName`
changes the default `RunicAssets.StaticFiles` resource name; pass the same name
to `AssetArchive.ReadEmbedded`. `AssetArchiveReadOptions` bounds compressed
size, file count, and total uncompressed size.

## Development

```bash
nix develop
./eng/verify.sh
```

Verification performs a warning-free Release build, contract and adapter tests,
an isolated package-consumer test, and NativeAOT publication and execution.

## Prerelease packages

Pull requests produce validated, non-published artifacts for `RunicAssets`,
`RunicAssets.CsWebUi`, and `RunicAssets.AspNetCore`. Publishing to GitHub
Packages is a separate manually guarded workflow action.

```bash
./eng/pack.sh 0.1.0-preview.local.1 /tmp/runic-assets-packages
```

## License

Runic Assets is licensed under the [MIT License](LICENSE).
