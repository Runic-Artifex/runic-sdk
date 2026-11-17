using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Runic.Application.Views.CsWebUi;

// The other loopback address of a CS-WebUI blob endpoint's port outside Windows. The endpoint's URLs name localhost,
// which a browser or Node resolves to ::1 and 127.0.0.1 and tries in turn, while the endpoint's loopback server binds
// one of them only (the first address .NET resolves localhost to, as HttpListener did). Without a listener of the
// window on the other address, another process could listen there and receive the page's blob loads, its uploads and
// their tokens. The relay listens on that address and forwards each connection, byte for byte, to the endpoint's
// listener, which checks the Host, the origin and the token as for a direct connection.
//
// The listener sees the relay's own loopback socket as the peer of a relayed connection, so its check that a peer is
// local cannot see the client. The relay therefore resets a client whose address is not loopback itself: a loopback
// address can still receive packets of another machine where the system routes them there (route_localnet on Linux).
// It also records which client each of its connections to the listener carries (ClientOf), for the endpoint's checks.
//
// Its connections are bounded as the listener's are (DEV-SEC-79): once one direction of a pair has ended in order, the
// other has HalfCloseTimeout (the listener's linger, 2 s) to end too before both are reset, so a client that never
// closes its socket cannot keep two descriptors of the process for good; and at MaxConnections (128) the pair that has
// gone longest without a byte either way is reset to make room, so an active download or upload keeps its pair.
internal sealed class CsWebUiLoopbackRelay : IDisposable
{
    private const int BufferSize = 16 * 1024;
    private readonly Socket _socket;
    private readonly IPEndPoint _target;
    private readonly Func<Socket, EndPoint?> _peerOf;
    private readonly Func<Socket, CancellationToken, ValueTask<Socket>> _accept;
    private readonly Action<SocketException>? _acceptFailed;
    private readonly int _maxConnections;
    private readonly TimeSpan _halfCloseTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Pair, byte> _pairs = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<IPEndPoint, IPEndPoint> _clients = new();
    // The pairs that count against MaxConnections: open, and not evicted.
    private int _live;

    private CsWebUiLoopbackRelay(Socket socket, IPEndPoint target, Func<Socket, EndPoint?> peerOf,
        Func<Socket, CancellationToken, ValueTask<Socket>> accept, Action<SocketException>? acceptFailed, int maxConnections,
        TimeSpan halfCloseTimeout)
    {
        _socket = socket;
        _target = target;
        _peerOf = peerOf;
        _accept = accept;
        _acceptFailed = acceptFailed;
        _maxConnections = maxConnections;
        _halfCloseTimeout = halfCloseTimeout;
        _ = Task.Run(AcceptAsync);
    }

    internal IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    // The relayed connections open now; for tests.
    internal int OpenConnections => Volatile.Read(ref _live);

    // Listens on address:port and relays to target; null when this machine has no such address (IPv6 disabled), where no
    // other process can listen either. Throws SocketException (AddressAlreadyInUse) when another socket holds it.
    // peerOf reads a client's address, its RemoteEndPoint unless a test fakes another one; accept is the socket's own
    // unless a test fails it. acceptFailed hears the first failed accept of each run of failures. The limits default to
    // the listener's (LoopbackHttpLimits: MaxConnections and LingerTimeout).
    internal static CsWebUiLoopbackRelay? Start(IPAddress address, int port, IPEndPoint target, Func<Socket, EndPoint?>? peerOf = null,
        Action<SocketException>? acceptFailed = null, Func<Socket, CancellationToken, ValueTask<Socket>>? accept = null,
        int maxConnections = 128, TimeSpan? halfCloseTimeout = null)
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
        return new CsWebUiLoopbackRelay(socket, target, peerOf ?? (static client => client.RemoteEndPoint),
            accept ?? (static (listener, token) => listener.AcceptAsync(token)), acceptFailed, maxConnections,
            halfCloseTimeout ?? TimeSpan.FromSeconds(2));
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

    // A failed accept (EMFILE and the like) does not end the relay: it accepts again after LoopbackHttpServer's backoff
    // (DEV-SEC-70). Only Dispose ends it.
    private async Task AcceptAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _accept(_socket, _stop.Token).ConfigureAwait(false);
                failures = 0;
            }
            catch (Exception error) when (_stop.IsCancellationRequested || error is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException error)
            {
                if (failures++ == 0) LoopbackHttpServer.ReportAcceptFailure(_acceptFailed, error);
                try { await Task.Delay(LoopbackHttpServer.AcceptBackoff(failures), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            Admit(client);
        }
    }

    private void Admit(Socket client)
    {
        // Another machine's client is reset before anything reaches the listener, and before it takes a slot.
        EndPoint? peer;
        try { peer = _peerOf(client); }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { peer = null; }
        if (!IsLoopbackPeer(peer))
        {
            Reset(client);
            return;
        }
        if (Volatile.Read(ref _live) >= _maxConnections) EvictIdlest();
        Socket upstream;
        try { upstream = new Socket(_target.AddressFamily, SocketType.Stream, ProtocolType.Tcp); }
        catch (SocketException)
        {
            Reset(client);
            return;
        }
        var pair = new Pair(client, upstream);
        Interlocked.Increment(ref _live);
        _pairs[pair] = 0;
        // Dispose may have reset the open pairs before this one was added.
        if (_stop.IsCancellationRequested)
        {
            Close(pair);
            return;
        }
        _ = Task.Run(() => RelayAsync(pair, (IPEndPoint)peer!));
    }

    // Resets the pair that has gone longest without a byte either way.
    private void EvictIdlest()
    {
        Pair? idlest = null;
        foreach (var pair in _pairs.Keys)
            if (!pair.Retired && (idlest is null || pair.Active < idlest.Active)) idlest = pair;
        if (idlest is not null) Close(idlest);
    }

    // Resets both sockets of a pair; it no longer counts against the limit.
    private void Close(Pair pair)
    {
        if (pair.TryRetire()) Interlocked.Decrement(ref _live);
        Reset(pair.Client);
        Reset(pair.Upstream);
    }

    // One connection: the client's bytes to the listener and back. A side that closes its sending half closes the
    // other's, and the other side then has HalfCloseTimeout to close too; a side that fails resets both, so a client
    // that goes away midway fails the request as it would directly.
    private async Task RelayAsync(Pair pair, IPEndPoint peer)
    {
        var (client, upstream) = (pair.Client, pair.Upstream);
        IPEndPoint? local = null;
        try
        {
            client.NoDelay = true;
            upstream.NoDelay = true;
            await upstream.ConnectAsync(_target, _stop.Token).ConfigureAwait(false);
            // Recorded before any byte of the client reaches the listener.
            local = (IPEndPoint)upstream.LocalEndPoint!;
            _clients[local] = peer;
            var toUpstream = PumpAsync(pair, client, upstream);
            var toClient = PumpAsync(pair, upstream, client);
            var first = await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            var other = first == toUpstream ? toClient : toUpstream;
            // DEV-SEC-79: a client that never closes its half would keep both sockets open for good.
            using (var halfClosed = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                if (await Task.WhenAny(other, Task.Delay(_halfCloseTimeout, halfClosed.Token)).ConfigureAwait(false) != other)
                    Close(pair);
                await halfClosed.CancelAsync().ConfigureAwait(false);
            }
            await other.ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            Close(pair);
        }
        finally
        {
            if (local is not null) _clients.TryRemove(local, out _);
            if (pair.TryRetire()) Interlocked.Decrement(ref _live);
            _pairs.TryRemove(pair, out _);
            client.Dispose();
            upstream.Dispose();
        }
    }

    private async Task PumpAsync(Pair pair, Socket from, Socket to)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await from.ReceiveAsync(buffer.AsMemory(0, BufferSize), SocketFlags.None, _stop.Token).ConfigureAwait(false);
                pair.Touch();
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

    // A relayed client and the relay's connection to the listener for it.
    private sealed class Pair(Socket client, Socket upstream)
    {
        private int _retired;
        private long _active = Stopwatch.GetTimestamp();

        internal Socket Client { get; } = client;
        internal Socket Upstream { get; } = upstream;
        internal bool Retired => Volatile.Read(ref _retired) != 0;
        // When a byte last crossed it, either way.
        internal long Active => Volatile.Read(ref _active);

        internal void Touch() => Volatile.Write(ref _active, Stopwatch.GetTimestamp());

        // True once only: for the eviction or the end that stops counting the pair.
        internal bool TryRetire() => Interlocked.Exchange(ref _retired, 1) == 0;
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
        foreach (var pair in _pairs.Keys) Close(pair);
        _stop.Dispose();
    }
}
