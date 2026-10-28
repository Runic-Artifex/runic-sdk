using System.Text;
using Runic.Application.Testing;
using Runic.Application.Views;

// The custom transports of the protocol-3 delivery tests. Until the producer publishes protocol-4 frames (P4), the
// generated bridges publish their protocol-3 states through the window dispatcher's legacy routes, and these tests
// receive them here, as they did through IBridgeTransport.Publish, and call the routes as the in-memory transport does.
internal abstract class LegacyTestTransport : IBridgeTransport, IDisposable
{
    private BridgeDispatcher? _dispatcher;

    public IDisposable Serve(IBridgeRequestHandler handler)
    {
        _dispatcher = (BridgeDispatcher)handler;
        _dispatcher.Legacy.SetPublisher(PublishUtf8Async);
        return new Lease(_dispatcher);
    }

    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    // Calls a synchronous protocol-3 route of a bridge on this transport.
    public string Call(string name, BridgeTestArguments? arguments = null) =>
        _dispatcher is { } dispatcher && dispatcher.Legacy.TryFind(name, out var sync, out _) && sync is not null
            ? sync(arguments ?? new())
            : throw new KeyNotFoundException($"Route '{name}' is not bound.");

    // Calls an asynchronous protocol-3 route of a bridge on this transport.
    public ValueTask<string> CallAsync(string name, BridgeTestArguments? arguments = null, CancellationToken cancellationToken = default) =>
        _dispatcher is { } dispatcher && dispatcher.Legacy.TryFind(name, out _, out var asynchronous) && asynchronous is not null
            ? asynchronous(arguments ?? new(), cancellationToken)
            : throw new KeyNotFoundException($"Route '{name}' is not bound.");

    // A protocol-3 state, as IBridgeTransport.Publish received it.
    protected virtual void Publish(string name, string stateJson) { }

    protected virtual ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken)
    {
        Publish(name, Encoding.UTF8.GetString(stateUtf8.Span));
        return ValueTask.CompletedTask;
    }

    public virtual void Dispose() => _dispatcher?.Legacy.SetPublisher(null);

    private sealed class Lease(BridgeDispatcher dispatcher) : IDisposable
    {
        public void Dispose() => dispatcher.Legacy.SetPublisher(null);
    }
}
