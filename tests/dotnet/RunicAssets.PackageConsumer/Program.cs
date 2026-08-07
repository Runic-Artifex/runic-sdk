using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RunicAssets;
using RunicAssets.CsWebUi;
using RunicAssets.RunicToolkit;

var source = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());

await source.ValidateAsync();
global::CsWebUi.WebUiFileHandlerResult webUiResponse = source.ToWebUiFileHandler()("/");
string rawHttpResponse = System.Text.Encoding.UTF8.GetString(webUiResponse.Response.Span);
var boundary = new RunicToolkitAssetBoundary(source, new Uri("app://package-consumer/application"));
await boundary.ValidateAsync();
await using Stream content = await boundary.OpenReadAsync(
    boundary.Manifest.Assets.Single(static asset => asset.IsEntryPoint).RelativePath,
    default);
using var reader = new StreamReader(content);
return webUiResponse.IsHandled
    && rawHttpResponse.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal)
    && rawHttpResponse.Contains("ETag: \"sha256-", StringComparison.Ordinal)
    && rawHttpResponse.EndsWith("package consumer</title>\n", StringComparison.Ordinal)
    && boundary.Manifest.ManifestVersion == "runic-toolkit.frontend-assets/1"
    && boundary.EntryPoint.AbsoluteUri == "app://package-consumer/application/index.html"
    && (await reader.ReadToEndAsync()).Contains("package consumer", StringComparison.Ordinal)
    ? 0
    : 1;
