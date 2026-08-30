using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Runic.Assets;
using Runic.Assets.AspNetCore;
using Runic.Assets.Desktop;

var source = AssetArchive.ReadEmbedded(Assembly.GetExecutingAssembly());

await source.ValidateAsync();
AssetDescriptor entry = source.Manifest.EntryPoint;
var aspNetResponse = new DefaultHttpContext();
aspNetResponse.Response.Body = new MemoryStream();
aspNetResponse.Request.Headers.Range = "bytes=0-0";
await RunicAssetEndpointExtensions.WriteAssetAsync(aspNetResponse, source, entry);
global::Runic.Desktop.ContentHandler desktopHandler = source.ToDesktopContentHandler();
return desktopHandler is not null
    && aspNetResponse.Response.StatusCode == StatusCodes.Status206PartialContent
    && aspNetResponse.Response.Headers.ContentRange == "bytes 0-0/" + entry.Length
    && aspNetResponse.Response.Body.Length == 1
    ? 0
    : 1;
