using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Runic.Application.Views.CsWebUi;

// The other loopback address of a CS-WebUI blob endpoint's port. The endpoint's URLs name localhost, which a browser or
// Node resolves to ::1 and 127.0.0.1 and tries in turn, while HttpListener binds localhost to one of them only (the
// first address .NET resolves it to). Without a listener of the window on the other address, another process could
// listen there and receive the page's blob loads, its uploads and their tokens. The relay listens on that address and
// forwards each connection, byte for byte, to the endpoint's HttpListener, which checks the Host, the origin and the
// token as for a direct connection.
//
// HttpListener sees the relay's own loopback socket as the peer of a relayed connection, so its check that a peer is
// local cannot see the client. The relay therefore resets a client whose address is not loopback itself: a loopback
// address can still receive packets of another machine where the system routes them there (route_localnet on Linux).
// It also records which client each of its connections to the listener carries (ClientOf), for the endpoint's checks.
internal sealed class CsWebUiLoopbackRelay : IDisposable
{
    private const int BufferSize = 16 * 1024;
    private readonly Socket _socket;
    private readonly IPEndPoint _target;
    private readonly Func<Socket, EndPoint?> _peerOf;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, byte> _open = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<IPEndPoint, IPEndPoint> _clients = new();

    private CsWebUiLoopbackRelay(Socket socket, IPEndPoint target, Func<Socket, EndPoint?> peerOf)
    {
        _socket = socket;
        _target = target;
        _peerOf = peerOf;
        _ = Task.Run(AcceptAsync);
    }

    internal IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    // Listens on address:port and relays to target; null when this machine has no such address (IPv6 disabled), where no
    // other process can listen either. Throws SocketException (AddressAlreadyInUse) when another socket holds it.
    // peerOf reads a client's address, its RemoteEndPoint unless a test fakes another one.
    internal static CsWebUiLoopbackRelay? Start(IPAddress address, int port, IPEndPoint target, Func<Socket, EndPoint?>? peerOf = null)
    {
        Socket socket;
        try { socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp); }
        catch (SocketException error) when (IsMissing(error)) { return null; }
        try
        {
            // .NET sets SO_REUSEADDR on every TCP bind outside Windows, but never SO_REUSEPORT. A listening socket without
            // SO_REUSEPORT keeps every other socket off the address: Linux refuses another listener on a listening port
            // even with SO_REUSEADDR, and BSD and macOS share an exact address only between sockets that all set
            // SO_REUSEPORT.
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen();
        }
        catch (SocketException error) when (IsMissing(error))
        {
            socket.Dispose();
            return null;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        return new CsWebUiLoopbackRelay(socket, target, peerOf ?? (static client => client.RemoteEndPoint));
    }

    // Whether a client may be relayed: its address is loopback (127.0.0.0/8, ::1, or 127.0.0.0/8 mapped to IPv6).
    internal static bool IsLoopbackPeer(EndPoint? peer) => peer is IPEndPoint { Address: var address }
        && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    // The client that the relay's connection from upstream (as the listener sees its peer) carries, while it is open;
    // null for a connection the relay did not make.
    internal IPEndPoint? ClientOf(IPEndPoint? upstream) =>
        upstream is not null && _clients.TryGetValue(upstream, out var client) ? client : null;

    // The machine has no such loopback address or address family.
    internal static bool IsMissing(SocketException error) => error.SocketErrorCode is SocketError.AddressNotAvailable
        or SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.ProtocolFamilyNotSupported;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _socket.AcceptAsync(_stop.Token).ConfigureAwait(false); }
            catch (SocketException error) when (!_stop.IsCancellationRequested
                && error.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            _ = Task.Run(() => RelayAsync(client));
        }
    }

    // One connection: the client's bytes to the HttpListener and back. A side that closes its sending half closes the
    // other's; a side that fails resets both, so a client that goes away midway fails the request as it would directly.
    private async Task RelayAsync(Socket client)
    {
        // Another machine's client is reset before anything reaches the listener.
        EndPoint? peer;
        try { peer = _peerOf(client); }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { peer = null; }
        if (!IsLoopbackPeer(peer))
        {
            Reset(client);
            return;
        }
        var upstream = new Socket(_target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _open[client] = 0;
        _open[upstream] = 0;
        IPEndPoint? local = null;
        try
        {
            if (_stop.IsCancellationRequested) return;
            client.NoDelay = true;
            upstream.NoDelay = true;
            await upstream.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);
            // Recorded before any byte of the client reaches the listener.
            local = (IPEndPoint)upstream.LocalEndPoint!;
            _clients[local] = (IPEndPoint)peer!;
            await Task.WhenAll(PumpAsync(client, upstream), PumpAsync(upstream, client)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Reset(client);
            Reset(upstream);
        }
        finally
        {
            if (local is not null) _clients.TryRemove(local, out _);
            _open.TryRemove(client, out _);
            _open.TryRemove(upstream, out _);
            client.Dispose();
            upstream.Dispose();
        }
    }

    private async Task PumpAsync(Socket from, Socket to)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, BufferSize), SocketFlags.None, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    to.Shutdown(SocketShutdown.Send);
                    return;
                }
                var sent = 0;
                while (sent < read)
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The other direction is waiting on a socket of this one: end both.
            Reset(from);
            Reset(to);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Closes a socket with a reset instead of an orderly close.
    private static void Reset(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        socket.Dispose();
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _socket.Dispose();
        foreach (var socket in _open.Keys) Reset(socket);
        _stop.Dispose();
    }
}
