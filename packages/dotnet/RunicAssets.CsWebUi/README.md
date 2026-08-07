# RunicAssets.CsWebUi

This integration is owned by Runic Assets and maps an `IAssetSource` directly
to CsWebUi's policy-free managed custom file handler. CsWebUi remains
independent and does not depend on Runic Assets.

```csharp
using RunicAssets.CsWebUi;

window.SetRunicAssets(assets);
window.Show(assets.Manifest.EntryPoint.RelativePath);
```

For a Vite application, the `RunicAssets` package supplies the complete build
and runtime path:

```xml
<PropertyGroup>
  <RunicAssetsDist>..\Client.Web\dist</RunicAssetsDist>
</PropertyGroup>
```

```csharp
using System.Reflection;
using RunicAssets;
using RunicAssets.CsWebUi;

var assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
window.SetRunicAssets(assets);
window.Show(assets.Manifest.EntryPoint.RelativePath);
```

The incremental build target creates the canonical Runic Assets archive,
including media type, digest, cache policy, ETag, entry-point, and length
metadata. The archive stays embedded in single-file and NativeAOT applications
and is read without extraction.

The adapter resolves the source's current manifest for every request, performs
exact ordinal path lookup, and reads content directly from `IAssetSource`. `/`
maps to the current manifest entry point. Responses preserve `MediaType`,
`CacheControl`, `EntityTag`, and content length, and always include
`X-Content-Type-Options: nosniff`.

Unknown and invalid paths produce complete 404 responses; source and read
failures produce complete 500 responses. Neither can fall through to WebUI's
local filesystem. Stable responses are not cached because no measurement yet
justifies retaining a second full response copy. `NoStore` development assets
therefore observe `DevelopmentDirectoryAssetSource.Refresh()` without replacing
the window handler.

Single-page application fallback is explicit and applies only to extensionless
unknown routes:

```csharp
window.SetRunicAssets(
    assets,
    new RunicAssetsCsWebUiOptions
    {
        EnableSinglePageApplicationFallback = true,
    });
```

WebUI requires one contiguous buffer containing the complete HTTP header and
body. This is not streaming. Its callback cannot inspect request headers, so
the adapter cannot implement conditional 304 responses, ranges, or
request-dependent authentication. HTTP file handling is serialized behind a
process-wide native mutex and WebUI's async-response mode is process-wide;
sources should keep reads local and prompt.

Installing a custom file handler disables WebUI's authentication-cookie check
process-wide. This adapter assumes a private, loopback-only desktop deployment.
Do not expose the window with `SetPublic(true)` without an upstream
authentication layer.

CsWebUi owns callback retention, native buffer ownership, exception containment,
replacement, and disposal. The window retains the handler—and therefore the
asset source—until another handler replaces it or the window is disposed.
