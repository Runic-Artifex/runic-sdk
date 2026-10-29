namespace Runic.Application.Views;

/// <summary>
/// Carries the Views protocol between one window and the pages connected to it: the requests of each connection with
/// their replies, and pushes to every connection.
/// </summary>
/// <remarks>
/// <para>
/// The window serves its requests through <see cref="Serve"/> and sends its pushes through
/// <see cref="BroadcastAsync"/>. Every message is one JSON object, encoded as UTF-8 without a byte order mark
/// (<c>specs/application/README.md</c>, "Transport"). A transport carries messages without reading them.
/// </para>
/// <para>
/// A transport that can push to one connection also implements <see cref="IBridgeConnectionTransport"/>. Without it,
/// the window queues each connection's targeted pushes, and the host script drains them with <c>receive</c> requests.
/// </para>
/// <para>
/// The replies and pushes of one connection must arrive in the order the window sent them. Runic Desktop, WPF
/// (<c>DesktopBridgeTransport</c>), CS-WebUI (<c>CreateBridgeSession</c>) and the in-memory transport of
/// <c>Runic.Application.Testing</c> implement this interface.
/// </para>
/// </remarks>
public interface IBridgeTransport
{
    /// <summary>Delivers the requests of every connection of this transport to <paramref name="handler"/>.</summary>
    /// <param name="handler">The window's request handler.</param>
    /// <returns>A lease that stops delivering requests to <paramref name="handler"/>.</returns>
    /// <remarks>
    /// <para>
    /// A transport serves one handler at a time. It calls <see cref="IBridgeRequestHandler.HandleAsync"/> for the
    /// requests of one connection in the order they arrived, without waiting for a reply before it passes the next
    /// request: some requests, such as <c>receive</c> and awaited commands, stay open for a long time. The handler admits
    /// a request before its returned task first yields, so calling it in arrival order keeps the connection's admission
    /// order. A transport reports each connection that closes with <see cref="IBridgeRequestHandler.ConnectionClosed"/>.
    /// </para>
    /// <para>
    /// A request that arrives while no handler is served, or after the lease was disposed, is answered with the
    /// <c>released</c> reply of the Views protocol, or not at all when the page's connection is closing.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The transport already serves a handler.</exception>
    /// <exception cref="ObjectDisposedException">The transport was disposed.</exception>
    IDisposable Serve(IBridgeRequestHandler handler);

    /// <summary>Sends one push to every connection of the window.</summary>
    /// <param name="message">The push, one JSON object as UTF-8. It is valid until the returned task completes.</param>
    /// <param name="cancellationToken">Stops waiting for the send; the push may still be delivered.</param>
    /// <returns>A task that completes once the transport has handed the push to every connection.</returns>
    /// <remarks>
    /// The window awaits each call before it sends the next push, so a transport sends pushes in call order without
    /// coalescing them. A push sent after the transport was disposed is dropped.
    /// </remarks>
    ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}

/// <summary>A transport that can push to one connection outside a request.</summary>
/// <remarks>
/// The pushes and replies of one connection keep the order in which the window sent them, whether they were broadcast
/// or targeted.
/// </remarks>
public interface IBridgeConnectionTransport : IBridgeTransport
{
    /// <summary>Sends one push to the connection <paramref name="connectionKey"/>.</summary>
    /// <param name="connectionKey">The connection's key, as the transport supplied it in <see cref="BridgeRequest.ConnectionKey"/>.</param>
    /// <param name="message">The push, one JSON object as UTF-8. It is valid until the returned task completes.</param>
    /// <param name="cancellationToken">Stops waiting for the send; the push may still be delivered.</param>
    /// <returns><see langword="true"/> when the transport handed the push to the connection; <see langword="false"/> when the connection is not open.</returns>
    ValueTask<bool> SendAsync(string connectionKey, ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);
}

/// <summary>Handles the Views protocol requests of a window's connections; the window implements it.</summary>
/// <remarks>Transports call it; applications implement <see cref="IBridgeTransport"/> for a custom host, not this interface.</remarks>
public interface IBridgeRequestHandler
{
    /// <summary>Handles one request and returns its reply.</summary>
    /// <param name="request">The request with the identities the transport authenticated.</param>
    /// <param name="cancellationToken">Cancelled when the transport no longer needs the reply, such as when the connection closed.</param>
    /// <returns>The reply, one JSON object as UTF-8. The transport may keep it; the handler does not reuse it.</returns>
    /// <remarks>Every request receives a reply, including a malformed one. The task does not fault.</remarks>
    ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRequest request, CancellationToken cancellationToken);

    /// <summary>Reports that the connection <paramref name="connectionKey"/> closed.</summary>
    /// <param name="connectionKey">The connection's key.</param>
    /// <remarks>
    /// The window releases the connection's session, presentations, watches and edit numbers. A transport reports each
    /// closed connection once; reporting an unknown or closed connection again does nothing.
    /// </remarks>
    void ConnectionClosed(string connectionKey);
}

/// <summary>One Views protocol request and the identities its transport authenticated.</summary>
/// <remarks>
/// The client and connection keys come from the transport, never from the message: the window trusts them to scope
/// presentations, interactions and pushes. A reconnect is a new connection with a new connection key.
/// </remarks>
public sealed class BridgeRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="message">The request, one JSON object as UTF-8. It is valid until the handler's task completes.</param>
    /// <param name="clientKey">The identity of the browser or process that sent the request.</param>
    /// <param name="connectionKey">The identity of the connection that carried the request.</param>
    /// <exception cref="ArgumentException"><paramref name="clientKey"/> or <paramref name="connectionKey"/> is empty.</exception>
    public BridgeRequest(ReadOnlyMemory<byte> message, string clientKey, string connectionKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientKey);
        ArgumentException.ThrowIfNullOrEmpty(connectionKey);
        Message = message;
        ClientKey = clientKey;
        ConnectionKey = connectionKey;
    }

    /// <summary>The request, one JSON object as UTF-8.</summary>
    public ReadOnlyMemory<byte> Message { get; }

    /// <summary>The trusted identity of the browser or process that sent the request.</summary>
    public string ClientKey { get; }

    /// <summary>The trusted identity of the connection that carried the request.</summary>
    public string ConnectionKey { get; }
}
