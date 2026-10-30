using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Kestrel;

// A Kestrel request as a PresentationExchange: every member reads or writes the HttpContext.
internal sealed class HttpContextExchange(HttpContext context)
    : PresentationExchange(context.Request.Method, context.Request.Path.Value ?? "/", context.Request.QueryString.Value ?? string.Empty)
{
    internal override IPAddress? RemoteIpAddress => context.Connection.RemoteIpAddress;

    internal override int RemotePort => context.Connection.RemotePort;

    internal override IPAddress? LocalIpAddress => context.Connection.LocalIpAddress;

    internal override int LocalPort => context.Connection.LocalPort;

    internal override CancellationToken RequestAborted
    {
        get => context.RequestAborted;
        set => context.RequestAborted = value;
    }

    internal override bool IsWebSocketRequest => context.WebSockets.IsWebSocketRequest;

    internal override string GetRequestHeader(string name) =>
        context.Request.Headers.TryGetValue(name, out var values) ? values.ToString() : string.Empty;

    internal override IEnumerable<KeyValuePair<string, string>> RequestHeaders =>
        context.Request.Headers.Select(static header => new KeyValuePair<string, string>(header.Key, header.Value.ToString()));

    internal override int StatusCode
    {
        get => context.Response.StatusCode;
        set => context.Response.StatusCode = value;
    }

    internal override string? ContentType
    {
        get => context.Response.ContentType;
        set => context.Response.ContentType = value;
    }

    internal override long? ContentLength
    {
        get => context.Response.ContentLength;
        set => context.Response.ContentLength = value;
    }

    internal override void SetResponseHeader(string name, string value) => context.Response.Headers[name] = value;

    internal override void AppendResponseHeader(string name, string value) => context.Response.Headers.Append(name, value);

    internal override IReadOnlyList<string> GetResponseHeader(string name) =>
        context.Response.Headers.TryGetValue(name, out var values) ? values.OfType<string>().ToArray() : [];

    internal override Stream ResponseBody => context.Response.Body;

    internal override Task SendFileAsync(string path, CancellationToken cancellationToken) =>
        context.Response.SendFileAsync(path, cancellationToken);

    internal override Task<WebSocket> AcceptWebSocketAsync(string? subProtocol) =>
        context.WebSockets.AcceptWebSocketAsync(subProtocol);
}
