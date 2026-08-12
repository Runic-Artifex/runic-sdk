![Runic Assets banner](.github/assets/brand/banner.png)

# Runic Assets

Package a Vite build once, embed it in your .NET application, and serve the
same validated asset manifest in desktop, ASP.NET Core, or Runic Toolkit hosts.
Runic Assets keeps paths, content metadata, caching, and archives independent
of the UI and hosting framework, so the asset boundary stays portable from
development through NativeAOT deployment.

## Choose a package

All packages target **.NET 10** and are currently published as previews. Use
`--prerelease` until a stable release is available.

| Package | Choose it when | Install |
| --- | --- | --- |
| [RunicAssets](https://www.nuget.org/packages/RunicAssets) | You need the framework-neutral manifest, embedded sources, development sources, or Vite archive embedding. | `dotnet add package RunicAssets --prerelease` |
| [RunicAssets.AspNetCore](https://www.nuget.org/packages/RunicAssets.AspNetCore) | You want exact GET endpoints for a Runic Assets source in ASP.NET Core. | `dotnet add package RunicAssets.AspNetCore --prerelease` |
| [RunicAssets.CsWebUi](https://www.nuget.org/packages/RunicAssets.CsWebUi) | You ship a private CS-WebUI desktop application and want it to serve a Runic Assets source. | `dotnet add package RunicAssets.CsWebUi --prerelease` |
| [RunicAssets.RunicToolkit](https://www.nuget.org/packages/RunicAssets.RunicToolkit) | Your Runic Toolkit host accepts `IFrontendAssetProvider`. | `dotnet add package RunicAssets.RunicToolkit --prerelease` |

Each adapter brings in `RunicAssets` transitively. See the individual package
READMEs for a host-specific example.

## Vite `dist` to an embedded archive

Use this path when a Vite build should travel inside your .NET application—no
runtime static-files directory or per-file resource registrations required.

1. Install the core package in the .NET application project:

   ```bash
   dotnet add package RunicAssets --prerelease
   ```

2. Build the Vite application so its output directory exists:

   ```bash
   npm run build
   ```

3. Point the application project at Vite's generated `dist` directory:

   ```xml
   <PropertyGroup>
     <RunicAssetsDist>../Client.Web/dist</RunicAssetsDist>
   </PropertyGroup>
   ```

4. Build the .NET application, then load the embedded archive:

   ```bash
   dotnet build
   ```

   ```csharp
   using System.Reflection;
   using RunicAssets;

   AssetArchiveSource assets = AssetArchive.ReadEmbedded(
       Assembly.GetExecutingAssembly());
   ```

The build target packages the complete directory into a canonical archive and
embeds it as `RunicAssets.StaticFiles`. It is incremental and runs again when
the project, target, packer, or a file under `RunicAssetsDist` changes. The
archive remains embedded in single-file, trimmed, and NativeAOT applications;
reading it does not extract files to disk.

`index.html` is the default entry point. Configure an alternate entry point or
exclude generated files when necessary:

```xml
<PropertyGroup>
  <RunicAssetsEntryPoint>app.html</RunicAssetsEntryPoint>
  <RunicAssetsDistExclude>runic-assets.zip;stats.html</RunicAssetsDistExclude>
</PropertyGroup>
```

To embed an archive produced elsewhere, set `RunicAssetsEmbeddedArchive`
instead of `RunicAssetsDist`. Change the logical resource name with
`RunicAssetsEmbeddedResourceName` and pass the same name to
`AssetArchive.ReadEmbedded`.

## What travels with the assets

Every manifest entry has an exact safe path, media type, byte length, SHA-256
digest, strong entity tag, and cache policy. HTML uses revalidation caching;
Vite's built non-HTML assets use immutable caching. `AssetArchive` is a
standard ZIP with a validated `runic-assets.json` manifest; archive read limits
can be set with `AssetArchiveReadOptions` when consuming untrusted input.

For development, `DevelopmentDirectoryAssetSource` provides refreshable local
files with `no-store` caching. For small, hand-authored bundles,
`EmbeddedAssetSource` maps explicit assembly resources directly.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Package-consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/RunicAssets.PackageConsumer)
- [Report an issue or request support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)

Preview packages are built and validated before release. Follow the repository
for release status and use the issue tracker for questions and bug reports.
