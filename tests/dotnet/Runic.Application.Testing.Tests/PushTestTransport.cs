using System.Text;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// A custom transport of the delivery tests (Views protocol 4). It keeps the window's request handler, so a test sends
// requests through it as a page would, each connection after its hello, and hands each broadcast push to
// OnBroadcastAsync, which a test overrides to take pushes slowly, fail or record them.
internal abstract class PushTestTransport : IBridgeTransport, IDisposable
{
    internal const string ConnectionKey = "connection-1";
    private readonly HashSet<string> _greeted = new(StringComparer.Ordinal);
    private IBridgeRequestHandler? _handler;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _handler = handler;
        return new Lease(this, handler);
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        OnBroadcastAsync(message, cancellationToken);

    // A broadcast push; the memory is valid only until the returned task completes.
    protected virtual ValueTask OnBroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        OnBroadcast(Encoding.UTF8.GetString(message.Span));
        return ValueTask.CompletedTask;
    }

    protected virtual void OnBroadcast(string json) { }

    // Sends one request on a connection, after the connection's hello, and returns the reply.
    public string Request(string json, string connectionKey = ConnectionKey) =>
        RequestAsync(json, connectionKey).AsTask().GetAwaiter().GetResult();

    public async ValueTask<string> RequestAsync(string json, string connectionKey = ConnectionKey, CancellationToken cancellationToken = default)
    {
        await HelloAsync(connectionKey).ConfigureAwait(false);
        return await SendAsync(json, connectionKey, cancellationToken).ConfigureAwait(false);
    }

    // Sends the hello of a connection once. Until a connection completed its hello, the window broadcasts nothing.
    public void Hello(string connectionKey = ConnectionKey) => HelloAsync(connectionKey).AsTask().GetAwaiter().GetResult();

    private async ValueTask HelloAsync(string connectionKey)
    {
        lock (_greeted)
            if (!_greeted.Add(connectionKey)) return;
        var reply = await SendAsync(BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "hello");
            writer.WriteNumber("protocol", BridgeProtocol.Version);
            writer.WriteString("session", $"{connectionKey}-session");
            writer.WriteStartObject("client");
            writer.WriteString("name", "Runic.Application.Testing.Tests");
            writer.WriteString("version", "1.0.0");
            writer.WriteEndObject();
        }), connectionKey, CancellationToken.None).ConfigureAwait(false);
        if (!reply.StartsWith("{\"ok\":true", StringComparison.Ordinal))
            throw new InvalidOperationException($"The hello of {connectionKey} was refused: {reply}");
    }

    private async ValueTask<string> SendAsync(string json, string connectionKey, CancellationToken cancellationToken)
    {
        var handler = _handler ?? throw new InvalidOperationException("No window serves this transport.");
        var reply = await handler.HandleAsync(new BridgeRequest(Encoding.UTF8.GetBytes(json), "client-1", connectionKey),
            cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(reply.Span);
    }

    public virtual void Dispose() { }

    private sealed class Lease(PushTestTransport owner, IBridgeRequestHandler handler) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._handler, handler)) owner._handler = null;
        }
    }
}
