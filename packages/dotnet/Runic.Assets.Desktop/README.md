# Runic.Assets.Desktop

Optional request-scoped delivery of authoritative Runic Assets snapshots through
Runic Desktop. The adapter preserves manifest media types, strong entity tags,
cache policy, byte ranges, streaming, and request cancellation while Runic
Desktop owns only the HTTP response lifetime.

```bash
dotnet add package Runic.Assets.Desktop --prerelease
```

Attach an `IAssetSource`, such as an embedded archive, to a Desktop surface:

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

Responses carry the manifest's entity tag and cache policy, `Accept-Ranges` and
`X-Content-Type-Options: nosniff`. A `text/*` media type without a charset is
sent with `; charset=utf-8`, as `Runic.Assets.AspNetCore` sends it, so one
archive gets the same `Content-Type` on both hosts.

An unexpected exception while the adapter prepares a response, after the path
resolved and before the body starts, becomes an empty `500`; the adapter does
not log it. A malformed `Range` header is ignored, and an unsatisfiable one
answers `416`. The asset itself is opened
only when Runic Desktop reads the body, through the response's stream factory.
A failure there, such as a source that cannot open the asset or content that no
longer matches the manifest entry's length and entity tag
(`InvalidDataException`), is thrown from that factory and handled by Runic
Desktop like any other failing content stream.

For a Runic Views application, which ships its frontend in `www/` instead, see
[Runic.Assets or the Views `www/` folder](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Assets/README.md#runicassets-or-the-views-www-folder).
