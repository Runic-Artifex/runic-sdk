# Runic.Assets.Desktop

Optional request-scoped delivery of authoritative Runic Assets snapshots through
Runic Desktop. The adapter preserves manifest media types, strong entity tags,
cache policy, byte ranges, streaming, and request cancellation while Runic
Desktop owns only the HTTP response lifetime.

```bash
dotnet add package Runic.Assets.Desktop --prerelease
```

Attach an `IAssetSnapshotSource`, such as an embedded archive, to a Desktop
surface:

```csharp
using System.Reflection;
using Runic.Assets;
using Runic.Assets.Desktop;
using Runic.Desktop;

AssetArchiveSource assets = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());
await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = new DesktopContent.Handler(assets.ToDesktopContentHandler()),
});
```

Paths resolve with the same `AssetManifest.TryResolveRequestPath` rules and
defaults as `Runic.Assets.AspNetCore`: the root serves the entry point, manifest
paths are exact, and missing paths without a file extension fall back to the
entry point. A path with a trailing slash, such as `/settings/`, is not served.
Pass `AssetRoutingOptions` to `ToDesktopContentHandler` to disable the root or
the fallback. Unknown and invalid paths otherwise return `404`. Runic Desktop
answers methods other than `GET` and `HEAD` with `405`, and it decodes `%2F` in
request paths before the handler resolves them.
