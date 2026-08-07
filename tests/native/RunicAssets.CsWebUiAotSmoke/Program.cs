using System;
using System.Reflection;
using System.Text;
using RunicAssets;
using RunicAssets.CsWebUi;

var source = AssetArchive.ReadEmbedded(
    Assembly.GetExecutingAssembly(),
    "RunicAssets.CsWebUiAotSmoke.Assets");
if (source.Manifest.Assets.Count != 2
    || !StringComparer.Ordinal.Equals(source.Manifest.EntryPoint.RelativePath, "app.html")
    || !source.Manifest.TryGetAsset("assets/app-A1B2C3D4.js", out AssetDescriptor? script)
    || script is null
    || script.MediaType != "text/javascript"
    || script.CacheMode != AssetCacheMode.Immutable
    || source.Manifest.TryGetAsset("excluded.txt", out _))
{
    return 1;
}

global::CsWebUi.WebUiFileHandlerResult result = source.ToWebUiFileHandler()("/");
string response = Encoding.UTF8.GetString(result.Response.Span);
return result.IsHandled
    && response.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal)
    && response.Contains("ETag: \"sha256-", StringComparison.Ordinal)
    && response.EndsWith("\r\n\r\n<!doctype html><title>NativeAOT adapter smoke</title>\n", StringComparison.Ordinal)
    ? 0
    : 1;
