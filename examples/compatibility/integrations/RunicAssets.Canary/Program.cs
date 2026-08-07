using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using RunicAssets;
using RunicAssets.AspNetCore;
using RunicAssets.CsWebUi;
using RunicAssets.RunicToolkit;

var source = new EmbeddedAssetSource(
    Assembly.GetExecutingAssembly(),
    [
        new("index.html", "RunicAssets.Canary.index.html", IsEntryPoint: true),
        new(
            "assets/app.css",
            "RunicAssets.Canary.assets.app.css",
            CacheMode: AssetCacheMode.Immutable),
    ]);
await source.ValidateAsync();

using var archive = new MemoryStream();
await AssetArchive.WriteAsync(source, archive);
archive.Position = 0;
AssetArchiveSource restored = AssetArchive.Read(archive);
await restored.ValidateAsync();

var toolkitAssets = new RunicToolkitAssetBoundary(
    restored,
    new Uri("app://runic-assets-canary/application"));
await toolkitAssets.ValidateAsync();
if (toolkitAssets.Manifest.ManifestVersion != "runic-toolkit.frontend-assets/1"
    || toolkitAssets.EntryPoint.AbsoluteUri !=
        "app://runic-assets-canary/application/index.html")
{
    throw new InvalidOperationException("Runic Toolkit did not receive the translated asset contract.");
}

global::CsWebUi.WebUiFileHandlerResult webUiResponse = restored.ToWebUiFileHandler()("/");
if (!webUiResponse.IsHandled
    || !Encoding.UTF8.GetString(webUiResponse.Response.Span)
        .Contains("Runic Assets canary", StringComparison.Ordinal))
{
    throw new InvalidOperationException("CsWebUi did not resolve the archived entry point.");
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
