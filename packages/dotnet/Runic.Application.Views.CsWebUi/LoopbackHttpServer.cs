using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Runic.Application.Views.CsWebUi;

// The CS-WebUI blob endpoint's listener outside Windows: a minimal HTTP/1.1 server on one loopback socket, in place of
// the managed HttpListener. That listener can also serve https:// prefixes, which keeps SslStream, X.509, OCSP and an
// HttpClient in every NativeAOT application (about 3 MB on Linux and macOS), although the endpoint only ever serves
// http:// on loopback. Windows keeps http.sys (CsWebUiHttpSysListener).
//
// It serves exactly what the endpoint needs: one request at a time per connection, read by LoopbackHttpParser's strict
// rules (no request smuggling, the listener's own Host only), with a Content-Length or chunked request body, and a
// response with a Content-Length, chunked, or without a body (HEAD, 204, 304). It has no TLS, no HTTP/2 and no upgrade.
// Its bounds:
//
// - the head: 8 KiB of request line, 32 KiB in all, 100 fields, complete within 10 s of its first byte (a slowloris
//   head); otherwise 414, 431 or 408 and the connection closes. A connection idle for 30 s between requests closes;
// - a request body: each read within 60 s (the endpoint's uploads drop after 30 s idle), at most MaxBodyBytes;
// - each write of a response within 30 s;
// - 100 requests per connection; an unread request body up to 4 MiB is discarded after the response, a longer one
//   closes the connection, after a short lingering read so that the client receives the response before any reset;
// - 128 connections. A new connection must send its first byte within 5 s. At the limit, the oldest connection that
//   has no request in its handler (idle, reading a head, draining or lingering) is reset to make room, so another
//   local user who holds every slot with idle or slow sockets cannot starve the window's requests (DEV-SEC-68a): a
//   browser sends a whole head at once, and a request stays in its handler only as long as the endpoint serves it,
//   which takes a blob URL or an upload token;
// - a client whose address is not loopback is reset at accept, before it takes a slot (DEV-SEC-66);
// - a protocol error in a body, a failed read or write, and a handler that fails reset the connection.
//
// An accept that fails (EMFILE, ENFILE, ENOBUFS and the like) does not end the server: it accepts again after 100 ms,
// growing to 1 s while the failures last, and reports the first failure of each run (DEV-SEC-70). Dispose stops
// accepting, resets every open connection and cancels each request's RequestAborted.
internal sealed class LoopbackHttpServer : ICsWebUiBlobListener
{
    private readonly Socket _socket;
    private readonly string _authority;
    private readonly LoopbackHttpLimits _limits;
    private readonly Action<SocketException>? _acceptFailed;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Socket, Connection> _open = new(ReferenceEqualityComparer.Instance);
    // The connections that count against MaxConnections: open, and not evicted.
    private int _live;
    private Func<ICsWebUiBlobRequest, Task>? _handle;

    private LoopbackHttpServer(Socket socket, string authority, LoopbackHttpLimits limits, Action<SocketException>? acceptFailed)
    {
        _socket = socket;
        _authority = authority;
        _limits = limits;
        _acceptFailed = acceptFailed;
    }

    internal IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    // The connections that count against the limit now; for tests.
    internal int OpenConnections => Volatile.Read(ref _live);

    // The accept and a client's address; tests replace them before Serve, to fail accepts or fake a remote client.
    internal Func<Socket, CancellationToken, ValueTask<Socket>> AcceptClient { get; set; } = static (socket, token) => socket.AcceptAsync(token);
    internal Func<Socket, EndPoint?> PeerOf { get; set; } = static client => client.RemoteEndPoint;

    // Listens on a loopback address. Requests must name host and the port listened on as their Host. Throws SocketException
    // (AddressAlreadyInUse) when another socket holds the address, as the relay does: .NET sets SO_REUSEADDR on every TCP
    // bind outside Windows but never SO_REUSEPORT, so no other socket can listen on the exact address afterwards.
    // acceptFailed hears the first failed accept of each run of failures.
    internal static LoopbackHttpServer Listen(IPEndPoint endPoint, string host, LoopbackHttpLimits? limits = null,
        Action<SocketException>? acceptFailed = null)
    {
        if (!IPAddress.IsLoopback(endPoint.Address))
            throw new ArgumentException($"{endPoint.Address} is not a loopback address.", nameof(endPoint));
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(endPoint);
            socket.Listen();
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        var authority = string.Create(CultureInfo.InvariantCulture, $"{host}:{((IPEndPoint)socket.LocalEndPoint!).Port}");
        return new LoopbackHttpServer(socket, authority, limits ?? new LoopbackHttpLimits(), acceptFailed);
    }

    public void Serve(Func<ICsWebUiBlobRequest, Task> handle)
    {
        if (Interlocked.CompareExchange(ref _handle, handle, null) is not null)
            throw new InvalidOperationException("The server already serves.");
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await AcceptClient(_socket, _stop.Token).ConfigureAwait(false);
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
                // A transient failure, such as a process out of descriptors: the socket stays bound, so clients would
                // wait in its backlog for good. Accept again after a pause.
                if (failures++ == 0) _acceptFailed?.Invoke(error);
                try { await Task.Delay(AcceptBackoff(failures), _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            Admit(client);
        }
    }

    // 100 ms after the first failed accept, doubling to 1 s.
    internal static TimeSpan AcceptBackoff(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(1000, 100 << Math.Min(failures - 1, 4)));

    private void Admit(Socket client)
    {
        EndPoint? peer;
        try { peer = PeerOf(client); }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { peer = null; }
        if (!CsWebUiLoopbackRelay.IsLoopbackPeer(peer) || Volatile.Read(ref _live) >= _limits.MaxConnections && !EvictOldest())
        {
            Reset(client);
            return;
        }
        var connection = new Connection(this, client, (IPEndPoint)peer!);
        Interlocked.Increment(ref _live);
        _open[client] = connection;
        // Dispose may have reset the open connections before this one was added.
        if (_stop.IsCancellationRequested)
        {
            connection.TryEvict();
            return;
        }
        _ = Task.Run(() => ServeConnectionAsync(connection));
    }

    // Resets the connection that has waited longest without a request in its handler; false when every connection has one.
    private bool EvictOldest()
    {
        Connection? oldest = null;
        foreach (var connection in _open.Values)
            if (connection.Evictable && (oldest is null || connection.Since < oldest.Since))
                oldest = connection;
        return oldest?.TryEvict() == true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Whatever fails a connection, including its handler, resets that connection only.")]
    private async Task ServeConnectionAsync(Connection connection)
    {
        var socket = connection.Socket;
        try
        {
            socket.NoDelay = true;
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            Reset(socket);
        }
        finally
        {
            connection.End();
            _open.TryRemove(socket, out _);
            connection.Dispose();
            socket.Dispose();
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
    }

    // The status line's reason phrases of the statuses the endpoint and the server send.
    private static string Reason(int status) => status switch
    {
        100 => "Continue",
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        304 => "Not Modified",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        410 => "Gone",
        413 => "Content Too Large",
        414 => "URI Too Long",
        416 => "Range Not Satisfiable",
        417 => "Expectation Failed",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        507 => "Insufficient Storage",
        _ => string.Empty,
    };

    // One client connection: its requests in turn.
    private sealed class Connection : IDisposable
    {
        // Waiting: no request in the handler (idle, reading a head, draining, lingering), so it may be evicted.
        private const int Waiting = 0, Handling = 1, Evicted = 2, Ended = 3;
        private readonly LoopbackHttpServer _server;
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly LoopbackHttpReader _reader;
        private readonly IPEndPoint _remote;
        private int _state;
        private long _since = Stopwatch.GetTimestamp();

        internal Connection(LoopbackHttpServer server, Socket socket, IPEndPoint remote)
        {
            _server = server;
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
            _reader = new LoopbackHttpReader(_stream, _server._limits.MaxHeadBytes + 1);
            _remote = remote;
        }

        internal LoopbackHttpServer Server => _server;
        internal Socket Socket => _socket;
        internal LoopbackHttpLimits Limits => _server._limits;
        internal LoopbackHttpReader Reader => _reader;
        internal IPEndPoint Remote => _remote;
        internal bool Evictable => Volatile.Read(ref _state) == Waiting;
        // When it last started waiting.
        internal long Since => Volatile.Read(ref _since);

        // Resets a waiting connection to make room; false when it has a request in its handler or has ended.
        internal bool TryEvict()
        {
            if (Interlocked.CompareExchange(ref _state, Evicted, Waiting) != Waiting) return false;
            Interlocked.Decrement(ref _server._live);
            Reset(_socket);
            return true;
        }

        // A request goes to the handler; false when the connection was evicted first.
        private bool TryBeginHandling() => Interlocked.CompareExchange(ref _state, Handling, Waiting) == Waiting;

        // The handler is done with the request (or only its body is left to discard): the connection may be evicted.
        internal void EndHandling()
        {
            Volatile.Write(ref _since, Stopwatch.GetTimestamp());
            Interlocked.CompareExchange(ref _state, Waiting, Handling);
        }

        internal void End()
        {
            if (Interlocked.Exchange(ref _state, Ended) != Evicted) Interlocked.Decrement(ref _server._live);
        }

        public void Dispose() => _stream.Dispose();

        internal async Task RunAsync()
        {
            for (var served = 1; ; served++)
            {
                var head = await ReadHeadAsync(first: served == 1).ConfigureAwait(false);
                if (head is null || !TryBeginHandling()) return;
                var last = !head.KeepAlive || served >= Limits.MaxRequestsPerConnection;
                var request = new Request(this, head, last);
                try
                {
                    await _server._handle!(request).ConfigureAwait(false);
                    await request.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    request.Dispose();
                    EndHandling();
                }
                if (request.Closing)
                {
                    await LingerAsync().ConfigureAwait(false);
                    return;
                }
            }
        }

        // The next request's head; null when the connection ends: the client closed it between requests, the head did
        // not arrive in time, or it was refused (and answered).
        // A new connection has FirstByteTimeout for its first byte, a kept-alive one IdleTimeout; the head then has
        // HeadTimeout. The head is scanned once, as it arrives (LoopbackHttpHeadScanner).
        private async Task<LoopbackHttpRequestHead?> ReadHeadAsync(bool first)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_server._stop.Token);
            var started = !_reader.Buffered.IsEmpty;
            deadline.CancelAfter(started ? Limits.HeadTimeout : first ? Limits.FirstByteTimeout : Limits.IdleTimeout);
            var scanner = new LoopbackHttpHeadScanner();
            while (true)
            {
                var buffered = _reader.Buffered;
                if (!started && !buffered.IsEmpty)
                {
                    started = true;
                    deadline.CancelAfter(Limits.HeadTimeout);
                }
                var length = scanner.Advance(buffered.Span);
                if (length < 0) return await RefuseAsync(400).ConfigureAwait(false);
                if (length > 0)
                {
                    var head = LoopbackHttpParser.Parse(buffered.Span[..length], _server._authority, Limits, out var status, out _);
                    _reader.Consume(length);
                    return head ?? await RefuseAsync(status).ConfigureAwait(false);
                }
                if (buffered.Length > Limits.MaxHeadBytes) return await RefuseAsync(431).ConfigureAwait(false);
                if (scanner.RequestLineLength > Limits.MaxRequestLineBytes) return await RefuseAsync(414).ConfigureAwait(false);
                int read;
                try { read = await _reader.FillAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!_server._stop.IsCancellationRequested)
                {
                    // An idle connection closes quietly; a head that did not arrive in time is answered.
                    return _reader.Buffered.IsEmpty ? null : await RefuseAsync(408).ConfigureAwait(false);
                }
                if (read == 0) return null;
            }
        }

        // Answers a request that the server refuses before any handler sees it, and ends the connection.
        private async Task<LoopbackHttpRequestHead?> RefuseAsync(int status)
        {
            var answer = string.Create(CultureInfo.InvariantCulture,
                $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Length: 0\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await WriteAsync(Encoding.ASCII.GetBytes(answer), CancellationToken.None).ConfigureAwait(false);
            await LingerAsync().ConfigureAwait(false);
            return null;
        }

        // Every write of the connection, bounded by WriteTimeout. Throws IOException when it fails or times out, and
        // OperationCanceledException when cancellationToken cancels it.
        internal async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _server._stop.Token);
            timeout.CancelAfter(Limits.WriteTimeout);
            try { await _stream.WriteAsync(data, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException error) { throw new IOException("The client did not read the response in time.", error); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                throw new IOException("The connection failed.", error);
            }
        }

        // Ends the connection after its last response: stops sending, and reads what the client still sends for a
        // short while, so that the close does not reset the connection before the client read the response.
        private async Task LingerAsync()
        {
            try { _socket.Shutdown(SocketShutdown.Send); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { return; }
            using var linger = CancellationTokenSource.CreateLinkedTokenSource(_server._stop.Token);
            linger.CancelAfter(Limits.LingerTimeout);
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (await _reader.ReadAsync(buffer, linger.Token).ConfigureAwait(false) > 0) { }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // One request and its response.
    private sealed class Request : ICsWebUiBlobRequest, IDisposable
    {
        private readonly Connection _connection;
        private readonly LoopbackHttpRequestHead _head;
        private readonly bool _last;
        private readonly CancellationTokenSource _aborted;
        private readonly List<KeyValuePair<string, string>> _headers = [];
        private readonly bool _isHead;
        private int _status = 200;
        private long? _length;
        private bool _headSent;
        private bool _chunked;
        private long _written;
        private bool _failed;
        private bool _continueSent;
        private Stream? _requestBody;
        private Stream? _responseBody;

        // The request body: bytes left of the Content-Length or of the current chunk, and what has been delivered.
        private long _remaining;
        private bool _inChunk;
        private bool _bodyDone;
        // A read of the body was cancelled or failed: its framing is lost, so the connection cannot carry another request.
        private bool _bodyBroken;
        private long _delivered;

        internal Request(Connection connection, LoopbackHttpRequestHead head, bool last)
        {
            _connection = connection;
            _head = head;
            _last = last;
            _aborted = CancellationTokenSource.CreateLinkedTokenSource(connection.Server._stop.Token);
            _isHead = string.Equals(head.Method, "HEAD", StringComparison.OrdinalIgnoreCase);
            _remaining = head.ContentLength ?? 0;
            _bodyDone = !head.HasBody;
        }

        // The connection ends after this response.
        internal bool Closing { get; private set; }

        public string Method => _head.Method;
        public string Path => _head.Path;
        public IPEndPoint? RemoteEndPoint => _connection.Remote;
        public string GetRequestHeader(string name) => _head.Header(name);
        public Stream RequestBody => _requestBody ??= new BodyStream(this, read: true);
        public CancellationToken RequestAborted => _aborted.Token;

        public int StatusCode
        {
            get => _status;
            set
            {
                if (_headSent) throw new InvalidOperationException("The response has started.");
                _status = value is >= 100 and <= 999 ? value : throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        public string? ContentType
        {
            set
            {
                if (value is null) Remove("Content-Type");
                else SetResponseHeader("Content-Type", value);
            }
        }

        public long? ContentLength
        {
            set
            {
                if (value is not { } length) return;
                if (_headSent) throw new InvalidOperationException("The response has started.");
                _length = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        public void SetResponseHeader(string name, string value)
        {
            Validate(name, value);
            Remove(name);
            _headers.Add(new(name, value));
        }

        public void AppendResponseHeader(string name, string value)
        {
            Validate(name, value);
            _headers.Add(new(name, value));
        }

        public Stream ResponseBody => _responseBody ??= new BodyStream(this, read: false);

        private void Remove(string name) =>
            _headers.RemoveAll(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase));

        // A response field: a token name that is not the server's own framing, and a value without line breaks or other
        // control characters (no response splitting).
        private void Validate(string name, string value)
        {
            if (_headSent) throw new InvalidOperationException("The response has started.");
            if (name.Any(static character => character > 0x7F) || !LoopbackHttpParser.IsToken(Encoding.ASCII.GetBytes(name)))
                throw new ArgumentException($"{name} is not a field name.", nameof(name));
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"The server sets {name}.", nameof(name));
            foreach (var character in value)
                if (character is not ('\t' or >= ' ' and <= (char)0xFF) || character == (char)0x7F)
                    throw new ArgumentException($"The value of {name} holds a character a field cannot carry.", nameof(value));
        }

        private bool HasNoBody => _isHead || _status is >= 100 and < 200 or 204 or 304;

        private void Fail()
        {
            _failed = true;
            _bodyBroken = true;
            try { _aborted.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // ---- The request body ----

        private async ValueTask<int> ReadBodyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_failed) throw new IOException("The request failed.");
            if (_bodyDone || buffer.IsEmpty) return 0;
            // The body may exceed the limit by one byte only, so the handler's own check of the limit answers.
            var limit = _connection.Limits.MaxBodyBytes();
            var allowed = limit == long.MaxValue ? long.MaxValue : limit + 1 - _delivered;
            if (allowed <= 0)
            {
                Fail();
                throw new IOException("The request body is larger than the server's limit.");
            }
            if (buffer.Length > allowed) buffer = buffer[..(int)allowed];
            if (_head.ExpectContinue && !_continueSent && !_headSent)
            {
                _continueSent = true;
                try { await _connection.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), _aborted.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                {
                    Fail();
                    throw new IOException("The connection failed.", error);
                }
            }
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            reading.CancelAfter(_connection.Limits.BodyReadTimeout);
            try
            {
                var read = await ReadFramedAsync(buffer, reading.Token).ConfigureAwait(false);
                _delivered += read;
                return read;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up, as the endpoint does on its idle limit: it answers, and the connection then closes.
                _bodyBroken = true;
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException or ObjectDisposedException
                or OperationCanceledException)
            {
                Fail();
                throw new IOException(error is OperationCanceledException ? "The request body did not arrive in time." : "The request body failed.", error);
            }
        }

        // The next bytes of the body by its framing; 0 at its end. InvalidDataException for a malformed chunked body,
        // IOException when the client closes before the end.
        private async ValueTask<int> ReadFramedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var reader = _connection.Reader;
            if (_head.Chunked && !_inChunk)
            {
                var line = await reader.ReadLineAsync(_connection.Limits.MaxChunkLineBytes, cancellationToken).ConfigureAwait(false);
                if (LoopbackHttpParser.ParseChunkSize(line.Span) is not { } size)
                    throw new InvalidDataException("A chunk size is malformed.");
                if (size == 0)
                {
                    await ReadTrailerAsync(cancellationToken).ConfigureAwait(false);
                    _bodyDone = true;
                    return 0;
                }
                _remaining = size;
                _inChunk = true;
            }
            var read = await reader.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The client closed the connection before the end of the request body.");
            _remaining -= read;
            if (_remaining == 0)
            {
                if (!_head.Chunked) _bodyDone = true;
                else
                {
                    // The CRLF after the chunk's data, read now so that the next read starts at a chunk-size line.
                    var end = await reader.ReadLineAsync(0, cancellationToken).ConfigureAwait(false);
                    if (!end.IsEmpty) throw new InvalidDataException("A chunk is longer than its size.");
                    _inChunk = false;
                }
            }
            return read;
        }

        // The trailer of a chunked body: fields within the head's limits, checked and discarded.
        private async Task ReadTrailerAsync(CancellationToken cancellationToken)
        {
            var limits = _connection.Limits;
            var total = 0;
            for (var count = 0; ; count++)
            {
                var line = await _connection.Reader.ReadLineAsync(limits.MaxHeadBytes - total, cancellationToken).ConfigureAwait(false);
                if (line.IsEmpty) return;
                total += line.Length + 2;
                if (count == limits.MaxHeaderCount || total > limits.MaxHeadBytes || !LoopbackHttpParser.TryParseField(line.Span, out _, out _, out _))
                    throw new InvalidDataException("The trailer is malformed or too large.");
            }
        }

        // Reads and discards the unread rest of the body, up to MaxDrainBytes, so the connection can carry the next
        // request; false when it cannot.
        private async Task<bool> TryDrainAsync()
        {
            if (_bodyDone) return true;
            if (_bodyBroken || _head.ExpectContinue && !_continueSent) return false;
            var limits = _connection.Limits;
            if (!_head.Chunked && _remaining > limits.MaxDrainBytes) return false;
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                using var draining = CancellationTokenSource.CreateLinkedTokenSource(_aborted.Token);
                draining.CancelAfter(limits.HeadTimeout);
                long drained = 0;
                while (!_bodyDone)
                {
                    drained += await ReadFramedAsync(buffer, draining.Token).ConfigureAwait(false);
                    if (drained > limits.MaxDrainBytes) return false;
                }
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or SocketException or ObjectDisposedException
                or OperationCanceledException)
            {
                _bodyBroken = true;
                return false;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ---- The response ----

        private async Task SendHeadAsync(bool complete)
        {
            if (!_bodyDone && !Closing && (_bodyBroken || _head.ExpectContinue && !_continueSent
                    || !_head.Chunked && _remaining > _connection.Limits.MaxDrainBytes))
                Closing = true;
            var head = new StringBuilder(256);
            head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {_status} {Reason(_status)}\r\n");
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (var (name, value) in _headers) head.Append(name).Append(": ").Append(value).Append("\r\n");
            if (_status is not (>= 100 and < 200 or 204 or 304))
            {
                if (_length is { } length) head.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n");
                else if (complete) head.Append("Content-Length: 0\r\n");
                else if (!_isHead)
                {
                    head.Append("Transfer-Encoding: chunked\r\n");
                    _chunked = true;
                }
            }
            if (Closing) head.Append("Connection: close\r\n");
            head.Append("\r\n");
            _headSent = true;
            await WriteResponseAsync(Encoding.Latin1.GetBytes(head.ToString()), CancellationToken.None).ConfigureAwait(false);
        }

        private async ValueTask WriteBodyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (_failed) throw new IOException("The request failed.");
            if (!_headSent)
            {
                Closing = _last;
                await SendHeadAsync(complete: false).ConfigureAwait(false);
            }
            if (data.IsEmpty || HasNoBody) return;
            if (_length is { } length && _written + data.Length > length)
                throw new InvalidOperationException("The response is longer than its Content-Length.");
            if (_chunked)
            {
                var size = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{data.Length:x}\r\n"));
                await WriteResponseAsync(size, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync(data, cancellationToken).ConfigureAwait(false);
                await WriteResponseAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            else await WriteResponseAsync(data, cancellationToken).ConfigureAwait(false);
            _written += data.Length;
        }

        private async Task WriteResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted.Token);
            try { await _connection.WriteAsync(data, writing.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                // A write that ended midway leaves the response cut: the connection cannot go on.
                Fail();
                if (error is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                throw new IOException("The response could not be sent.", error);
            }
        }

        // Ends the response once the handler has returned: sends a response that has not started, or ends one that has.
        // A request that failed, or a response shorter than its Content-Length, resets the connection (IOException).
        internal async Task CompleteAsync()
        {
            if (_failed) throw new IOException("The request failed.");
            if (!_headSent)
            {
                if (_length > 0 && !HasNoBody)
                {
                    Fail();
                    throw new IOException("The response ended before its Content-Length.");
                }
                if (!_bodyDone) _connection.EndHandling();
                Closing = _last || !await TryDrainAsync().ConfigureAwait(false);
                await SendHeadAsync(complete: true).ConfigureAwait(false);
                return;
            }
            if (_chunked) await WriteResponseAsync("0\r\n\r\n"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
            else if (_length is { } length && !HasNoBody && _written < length)
            {
                Fail();
                throw new IOException("The response ended before its Content-Length.");
            }
            // Discarding the rest of a body is no longer the handler's work: the connection may be evicted meanwhile.
            _connection.EndHandling();
            if (!Closing && !await TryDrainAsync().ConfigureAwait(false)) Closing = true;
        }

        public void Dispose() => _aborted.Dispose();

        // The request body (read) or the response body (write) as a stream.
        private sealed class BodyStream(Request request, bool read) : Stream
        {
            public override bool CanRead => read;
            public override bool CanSeek => false;
            public override bool CanWrite => !read;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                read ? request.ReadBodyAsync(buffer, cancellationToken) : throw new NotSupportedException();

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                read ? throw new NotSupportedException() : request.WriteBodyAsync(buffer, cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}

// The buffered reader of a LoopbackHttpServer connection: the head and the chunk framing read lines from its buffer;
// a body reads the buffered bytes first and then the stream.
internal sealed class LoopbackHttpReader(Stream stream, int capacity)
{
    private readonly byte[] _buffer = new byte[capacity];

    private int _start;
    private int _end;

    internal ReadOnlyMemory<byte> Buffered => _buffer.AsMemory(_start, _end - _start);

    internal void Consume(int count)
    {
        _start += count;
        if (_start == _end) _start = _end = 0;
    }

    // Reads more of the stream into the buffer: the number of bytes, 0 at its end. InvalidDataException when the buffer
    // is full.
    internal async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
        if (_end == _buffer.Length) throw new InvalidDataException("The line is too long.");
        var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;
        return read;
    }

    // Buffered bytes first, then a read of the stream: at most destination's length, 0 at the stream's end.
    internal async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_end > _start)
        {
            var count = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, count).CopyTo(destination);
            Consume(count);
            return count;
        }
        return await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    // A line that ends in CRLF, without it, of at most max bytes. InvalidDataException for a longer line or a bare CR or
    // LF; IOException when the stream ends first. Each byte is looked at once, however the line arrives.
    internal async ValueTask<ReadOnlyMemory<byte>> ReadLineAsync(int max, CancellationToken cancellationToken)
    {
        var searched = 0;
        while (true)
        {
            var buffered = _buffer.AsSpan(_start, _end - _start);
            var found = buffered[searched..].IndexOfAny((byte)'\r', (byte)'\n');
            if (found < 0) searched = buffered.Length;
            else
            {
                var at = searched + found;
                if (buffered[at] == (byte)'\n') throw new InvalidDataException("A line ends in a bare LF.");
                if (at > max) throw new InvalidDataException("A line is too long.");
                if (at + 1 < buffered.Length)
                {
                    if (buffered[at + 1] != (byte)'\n') throw new InvalidDataException("A line holds a bare CR.");
                    var line = buffered[..at].ToArray();
                    Consume(at + 2);
                    return line;
                }
                // A CR at the end: its LF is still to come.
                searched = at;
            }
            if (searched > max) throw new InvalidDataException("A line is too long.");
            if (await FillAsync(cancellationToken).ConfigureAwait(false) == 0)
                throw new IOException("The client closed the connection within a line.");
        }
    }
}
