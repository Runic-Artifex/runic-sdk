# RunicAssets.CsWebUi

Ship a Vite build inside a CS-WebUI desktop application and let the window serve
the exact files and metadata from a Runic Assets source. The adapter keeps host
delivery separate from archive creation, so the same bundle can also be used by
other Runic Assets hosts.

## Install

```bash
dotnet add package RunicAssets.CsWebUi --prerelease
```

The package targets **.NET 10**, brings `RunicAssets` and `CsWebUi`
transitively, and is currently in preview. Choose it for a CS-WebUI window;
choose `RunicAssets.AspNetCore` for an ASP.NET Core host.

## Serve an embedded Vite build

Configure the application project with `RunicAssetsDist` after the Vite build
has produced its `dist` directory, then attach the embedded source to a window:

```csharp
using System.Reflection;
using CsWebUi;
using RunicAssets;
using RunicAssets.CsWebUi;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly());

using var window = new WebUiWindow();
window.SetRunicAssets(assets);
window.Show(assets.Manifest.EntryPoint.RelativePath);
```

`/` resolves to the manifest entry point. Enable client-side routing explicitly
when an SPA should handle extensionless unknown routes:

```csharp
window.SetRunicAssets(
    assets,
    new RunicAssetsCsWebUiOptions
    {
        EnableSinglePageApplicationFallback = true,
    });
```

## Delivery and safety notes

The handler uses exact ordinal manifest lookup and sends the source's media
type, cache-control value, entity tag, and content length with
`X-Content-Type-Options: nosniff`. Invalid and unknown paths produce a complete
`404`; source or read failures produce a complete `500` and cannot fall through
to WebUI's local filesystem.

CS-WebUI owns callback retention, native buffer ownership, exception containment,
replacement, and disposal. The window retains the handler—and therefore the
asset source—until another handler replaces it or the window is disposed.

CS-WebUI requires one contiguous in-memory HTTP response, so this adapter is not
streaming and cannot implement ranges or conditional `304` responses. Its HTTP
callback is serialized process-wide; keep sources local and prompt.

Installing a custom file handler disables CS-WebUI's authentication-cookie check
process-wide. This adapter is for private, loopback-only desktop deployments.
Do not call `SetPublic(true)` without an upstream authentication layer.

## Documentation and support

- [Runic Assets documentation](https://docs.runic-artifex.eu/products/runic-assets)
- [Vite archive consumer example](https://github.com/Runic-Artifex/runic-assets/tree/main/tests/RunicAssets.PackageConsumer)
- [Issues and support](https://github.com/Runic-Artifex/runic-assets/issues)
- [MIT License](https://github.com/Runic-Artifex/runic-assets/blob/main/LICENSE)
