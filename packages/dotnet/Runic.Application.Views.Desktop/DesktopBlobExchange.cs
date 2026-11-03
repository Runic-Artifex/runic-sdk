using Runic.Desktop.Internal;

namespace Runic.Application.Views.Desktop;

// blobs/1 on a Desktop surface: a request that WebUiWindow admitted for the window's page, as a Bridge blob exchange.
// A token names a blob (GET or HEAD); null is an upload (POST).
internal sealed class DesktopBlobExchange(PresentationExchange exchange) : IBridgeBlobExchange
{
    internal static Task ServeAsync(BridgeBlobs blobs, PresentationExchange exchange, string? token)
    {
        var adapted = new DesktopBlobExchange(exchange);
        return token is null ? blobs.ReceiveUploadAsync(adapted) : blobs.ServeBlobAsync(token, adapted);
    }

    public string Method => exchange.Method;
    public string GetRequestHeader(string name) => exchange.GetRequestHeader(name);
    public Stream RequestBody => exchange.RequestBody;

    public int StatusCode
    {
        get => exchange.StatusCode;
        set => exchange.StatusCode = value;
    }

    public string? ContentType { set => exchange.ContentType = value; }
    public long? ContentLength { set => exchange.ContentLength = value; }
    public void SetResponseHeader(string name, string value) => exchange.SetResponseHeader(name, value);
    public Stream ResponseBody => exchange.ResponseBody;
    public CancellationToken RequestAborted => exchange.RequestAborted;
}
