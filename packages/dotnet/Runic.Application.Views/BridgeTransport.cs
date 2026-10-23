namespace Runic.Application.Views;

// The Bridge needs only callable handlers, change delivery, and typed input.
// Window ownership, native dialogs, and close policy stay with a host adapter.
/// <summary>Positional arguments of one browser call, read in declaration order.</summary>
public interface IBridgeArguments
{
    /// <summary>Reads the next argument as a 64-bit integer.</summary>
    long GetInt64();
    /// <summary>Reads the next argument as a Boolean.</summary>
    bool GetBoolean();
    /// <summary>Reads the next argument as a string.</summary>
    string GetString();
    /// <summary>The host-verified browser client identity, if the host provides one.</summary>
    string? ClientKey => null;
    /// <summary>The host-verified transport connection identity, if the host provides one.</summary>
    string? ConnectionKey => null;
}

/// <summary>The host adapter surface used by generated bridges to bind routes and publish state.</summary>
public interface IBridgeTransport
{
    /// <summary>Binds a synchronous browser route.</summary>
    /// <returns>A lease that unbinds the route.</returns>
    IDisposable Bind(string name, Func<IBridgeArguments, string> handler);
    /// <summary>Binds an asynchronous browser route.</summary>
    /// <returns>A lease that unbinds the route.</returns>
    IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler);
    /// <summary>Delivers an encoded state snapshot to the browser listener for <paramref name="name"/>.</summary>
    /// <remarks>
    /// A transport that does not implement <see cref="IAsyncBridgeTransport"/> must send every
    /// call, in call order and without coalescing, because collection delta frames depend on
    /// every preceding frame. CS-WebUI does this: each call writes one WebSocket message to
    /// every connected client and returns once the socket write completes.
    /// </remarks>
    void Publish(string name, string stateJson);
}

/// <summary>A host that can acknowledge state delivery before the next dependent frame is sent.</summary>
/// <remarks>
/// Desktop implements this because its <see cref="IBridgeTransport.Publish"/> coalesces
/// snapshots per route, and awaiting script completion lets a slow WebView fall behind into
/// a bounded recovery snapshot. CS-WebUI does not implement it: WebUI's only acknowledged
/// script call answers from one client and blocks a native thread. Its
/// <see cref="IBridgeTransport.Publish"/> instead writes to every client of its window
/// synchronously and blocks until each socket write completes or times out. A slow client
/// therefore applies TCP-level back-pressure: the blocked write holds the bridge's delivery
/// queue, which falls back to a recovery snapshot once it is full, and delays the window's
/// other clients, but not other CS-WebUI windows. This is not application-level
/// acknowledgement; frames are written in order, not confirmed as applied.
/// </remarks>
public interface IAsyncBridgeTransport : IBridgeTransport
{
    /// <summary>Delivers one frame without coalescing it with other frames.</summary>
    /// <remarks>Generated bridges await this outside the model context. Slow delivery is bounded by the bridge's recovery-snapshot queue.</remarks>
    ValueTask PublishAsync(string name, string stateJson);
}

/// <summary>A host that receives state frames as UTF-8 JSON instead of strings.</summary>
/// <remarks>
/// <para>
/// The generated Bridge finds this interface by a type test and then delivers every state frame through
/// <see cref="PublishUtf8Async"/> instead of <see cref="IBridgeTransport.Publish"/> or
/// <see cref="IAsyncBridgeTransport.PublishAsync"/>. The bytes are the UTF-8 encoding of the string the other
/// methods would receive for the same frame, without a byte order mark.
/// </para>
/// <para>
/// As with <see cref="IAsyncBridgeTransport.PublishAsync"/>, the Bridge awaits each call before it sends the next
/// frame of the route, outside the model context, and the transport delivers each frame without coalescing it with
/// other frames. The memory is valid only until the returned task completes; copy it to keep it longer.
/// Desktop and <c>Runic.Application.Testing</c>'s in-memory transport implement it.
/// </para>
/// </remarks>
public interface IBridgeUtf8Transport : IBridgeTransport
{
    /// <summary>Delivers one encoded state frame, as UTF-8 JSON, to the browser listener for <paramref name="name"/>.</summary>
    /// <param name="name">The route.</param>
    /// <param name="stateUtf8">The frame's JSON, encoded as UTF-8.</param>
    /// <param name="cancellationToken">Stops waiting for delivery; the frame may still be delivered.</param>
    ValueTask PublishUtf8Async(string name, ReadOnlyMemory<byte> stateUtf8, CancellationToken cancellationToken = default);
}
