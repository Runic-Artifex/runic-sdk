using System;
using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Runic.Assets;
using Runic.Assets.AspNetCore;
using Runic.Assets.Desktop;

var source = new EmbeddedAssetSource(
    Assembly.GetExecutingAssembly(),
    [
        new("index.html", "Runic.Assets.Canary.index.html", IsEntryPoint: true),
        new(
            "assets/app.css",
            "Runic.Assets.Canary.assets.app.css",
            CacheMode: AssetCacheMode.Immutable),
    ]);
await source.ValidateAsync();

using var archive = new MemoryStream();
await AssetArchive.WriteAsync(source, archive);
archive.Position = 0;
AssetArchiveSource restored = AssetArchive.Read(archive);
await restored.ValidateAsync();

global::Runic.Desktop.ContentHandler desktopHandler = restored.ToDesktopContentHandler();
if (desktopHandler is null)
{
    throw new InvalidOperationException("Runic Desktop did not accept the archived asset source.");
}

AssetDescriptor stylesheet = restored.Manifest.Assets[0];
var context = new DefaultHttpContext();
context.Response.Body = new MemoryStream();
await RunicAssetEndpointExtensions.WriteAssetAsync(context, restored, stylesheet);
if (context.Response.StatusCode != StatusCodes.Status200OK
    || context.Response.ContentType != "text/css"
    || context.Response.Headers.CacheControl != stylesheet.CacheControl
    || context.Response.Headers.ETag != stylesheet.EntityTag
    || context.Response.Body.Length != stylesheet.Length)
{
    throw new InvalidOperationException("ASP.NET Core did not preserve the asset response contract.");
}

Console.WriteLine(
    $"{AssetArchive.CurrentVersion}: package-only managed and NativeAOT canary passed.");
